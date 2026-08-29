Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' The seekable <see cref="Stream" /> returned by <c>OpenFile</c>: reading, writing,
        ''' seeking, growing, truncating, and the PendingFile bracketing around an open
        ''' stream.
        ''' </summary>
        Public NotInheritable Class FileStreams

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FileStreamReadWriteSeekAndSetLength()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim FileId = Efs.CreateFile(Efs.RootAnchorId, "data.bin")

                            Dim Original = GenerateRandomData(20000, 4201)

                            Using Stream = Efs.OpenFile(FileId)

                                Stream.Write(Original, 0, Original.Length)
                                AssertEqual(CLng(Original.Length), Stream.Length, "Length after write is wrong.")

                                Stream.Seek(5000, SeekOrigin.Begin)
                                Dim Patch = GenerateRandomData(2000, 4202)
                                Stream.Write(Patch, 0, Patch.Length)
                                Overlay(Original, Patch, 5000)

                                Stream.Position = 0
                                Dim ReadBack(Original.Length - 1) As Byte
                                Dim Total = 0
                                While Total < ReadBack.Length
                                    Dim Got = Stream.Read(ReadBack, Total, ReadBack.Length - Total)
                                    If Got = 0 Then Exit While
                                    Total += Got
                                End While
                                AssertBytesEqual(Original, ReadBack, "Read-back after seek/write is wrong.")

                                ' Grow by SetLength - the tail is zero-filled.
                                Stream.SetLength(Original.Length + 4096)
                                Dim Grown = CombineArrays(Original, GenerateZeroedData(4096))
                                Stream.Position = 0
                                AssertBytesEqual(Grown, ReadAll(Stream), "SetLength growth did not zero-fill.")

                                ' Shrink by SetLength.
                                Stream.SetLength(3000)
                                Stream.Position = 0
                                AssertBytesEqual(Slice(Original, 0, 3000), ReadAll(Stream), "SetLength shrink kept the wrong bytes.")

                            End Using

                            AssertEqual(3000L, Efs.FindEntry(Efs.RootAnchorId, "data.bin").LengthOfDataAtEntry, "Persisted length is wrong after close.")
                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub OpeningAFileMarksItPendingUntilTheStreamIsDisposed()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim FileId = Efs.CreateFile(Efs.RootAnchorId, "f.txt", CreateAsPending:=False)
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Efs.RootAnchorId, "f.txt").EntryType, "Fresh non-pending file should be a File.")

                            Dim Stream = Efs.OpenFile(FileId)
                            Try
                                AssertEqual(
                                    EmbeddedFileSystem.EntryTypes.PendingFile,
                                    Efs.FindEntry(Efs.RootAnchorId, "f.txt").EntryType,
                                    "An open file should be marked PendingFile.")

                                AssertThrows(Of IOException)(
                                    Sub() Efs.OpenFile(FileId),
                                    "Opening an already-open file should throw.")

                                AssertThrows(Of IOException)(
                                    Sub() Efs.DeleteEntry(Efs.RootAnchorId, "f.txt"),
                                    "Deleting an open file should throw.")

                            Finally
                                Stream.Dispose()
                            End Try

                            AssertEqual(
                                EmbeddedFileSystem.EntryTypes.File,
                                Efs.FindEntry(Efs.RootAnchorId, "f.txt").EntryType,
                                "Closing the stream should finalise the file.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DisposingTheFileSystemWithAnOpenStreamThrows()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Efs As New EmbeddedFileSystem(Cs)
                        Dim FileId = Efs.CreateFile(Efs.RootAnchorId, "held.txt")
                        Dim Stream = Efs.OpenFile(FileId)

                        Try
                            AssertThrows(Of InvalidOperationException)(
                                Sub() Efs.Dispose(),
                                "Disposing the file system with an open file stream should throw.")
                        Finally
                            Stream.Dispose()
                            Efs.Dispose()
                        End Try

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ManyFilesInterleavedRoundTripAndSurviveReopen()

                Using Ms As New MemoryStream()

                    Dim Expected As New Dictionary(Of String, Byte())()

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "files")

                            For Index = 0 To 11
                                Dim Name = $"file{Index}.bin"
                                Dim Data = GenerateRandomData(500 + (Index * 900), 4300 + Index)
                                Expected(Name) = Data
                                Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, Name, CreateAsPending:=False), Data)
                            Next

                            ' Rewrite a couple in place with a different length.
                            Dim Rewrite = GenerateRandomData(50, 4399)
                            Expected("file3.bin") = Rewrite
                            Basics.WriteWholeFile(Efs, Efs.FindEntry(Dir, "file3.bin").ChildAnchorId, Rewrite)

                            Cs.Validate()

                        End Using
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.FindEntry(Efs.RootAnchorId, "files").ChildAnchorId

                            For Each Pair In Expected
                                Dim Entry = Efs.FindEntry(Dir, Pair.Key)
                                AssertEqual(CLng(Pair.Value.Length), Entry.LengthOfDataAtEntry, $"{Pair.Key}: wrong length after reopen.")
                                AssertBytesEqual(Pair.Value, Basics.ReadWholeFile(Efs, Entry.ChildAnchorId), $"{Pair.Key}: wrong content after reopen.")
                            Next

                        End Using
                        Cs.Validate()
                    End Using

                End Using

            End Sub

            Private Shared Function ReadAll(Stream As Stream) As Byte()

                Using Buffer As New MemoryStream()
                    Stream.CopyTo(Buffer)
                    Return Buffer.ToArray()
                End Using

            End Function

        End Class

    End Class

End Namespace
