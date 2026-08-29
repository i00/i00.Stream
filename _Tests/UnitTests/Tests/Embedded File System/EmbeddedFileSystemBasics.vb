Imports System.IO
Imports System.Text
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' Directory and entry basics for <see cref="EmbeddedFileSystem" />: creating,
        ''' listing, finding, renaming-by-collision, deleting and the non-removable root.
        ''' </summary>
        Public NotInheritable Class Basics

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub NewFileSystemHasAnEmptyRoot()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            AssertTrue(Efs.RootAnchorId > 0, "The root should have a positive anchor id.")
                            AssertEqual(0, Efs.GetRootEntries().Count, "A new file system root should be empty.")
                            AssertEqual(0L, Efs.GetParentAnchorId(Efs.RootAnchorId), "The root has no parent.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DirectoriesAndFilesCanBeCreatedListedAndFound()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim Pics = Efs.CreateDirectory(Efs.RootAnchorId, "pics")
                            Dim Readme = Efs.CreateFile(Efs.RootAnchorId, "readme.txt", CreateAsPending:=False)

                            Dim RootNames = Efs.GetRootEntries().Select(Function(e) e.Name).OrderBy(Function(n) n).ToArray()
                            AssertEqual("docs|pics|readme.txt", String.Join("|", RootNames), "Root listing is wrong.")

                            Dim DocsEntry = Efs.FindEntry(Efs.RootAnchorId, "docs")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.Directory, DocsEntry.EntryType, "docs should be a directory.")
                            AssertEqual(Docs, DocsEntry.ChildAnchorId, "docs entry points at the wrong anchor.")

                            Dim ReadmeEntry = Efs.FindEntry(Efs.RootAnchorId, "readme.txt")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, ReadmeEntry.EntryType, "readme.txt should be a finalised file.")

                            AssertEqual(Efs.RootAnchorId, Efs.GetParentAnchorId(Docs), "docs parent should be the root.")
                            AssertEqual(Docs, Efs.GetParentAnchorId(Efs.CreateFile(Docs, "a.txt")), "nested file parent is wrong.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DuplicateNamesAreRejectedCaseInsensitively()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Efs.CreateDirectory(Efs.RootAnchorId, "Data")

                            AssertThrows(Of IOException)(
                                Sub() Efs.CreateDirectory(Efs.RootAnchorId, "data"),
                                "A case-different duplicate directory name should be rejected.")

                            AssertThrows(Of IOException)(
                                Sub() Efs.CreateFile(Efs.RootAnchorId, "DATA"),
                                "A file colliding with an existing directory name should be rejected.")

                            AssertThrows(Of ArgumentException)(
                                Sub() Efs.CreateFile(Efs.RootAnchorId, "   "),
                                "A blank name should be rejected.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeletingADirectoryRemovesItAndItsSubtree()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Project = Efs.CreateDirectory(Efs.RootAnchorId, "project")
                            Dim Src = Efs.CreateDirectory(Project, "src")
                            Dim FileA = Efs.CreateFile(Src, "a.txt", CreateAsPending:=False)
                            Dim FileB = Efs.CreateFile(Project, "b.txt", CreateAsPending:=False)
                            Efs.CreateFile(Efs.RootAnchorId, "keep.txt", CreateAsPending:=False)

                            WriteWholeFile(Efs, FileA, GenerateRandomData(5000, 3301))
                            WriteWholeFile(Efs, FileB, GenerateRandomData(3000, 3302))

                            Efs.DeleteEntry(Efs.RootAnchorId, "project")

                            Dim Remaining = Efs.GetRootEntries().Select(Function(e) e.Name).ToArray()
                            AssertEqual("keep.txt", String.Join("|", Remaining), "Recursive delete left the wrong entries.")

                            AssertThrows(Of Collections.Generic.KeyNotFoundException)(
                                Sub() Cs.GetAnchor(Src),
                                "The removed subdirectory anchor should be gone.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MissingEntriesAndBadAnchorsAreRejected()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim FileId = Efs.CreateFile(Docs, "a.txt", CreateAsPending:=False)

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.FindEntry(Efs.RootAnchorId, "nope"),
                                "Finding a missing entry should throw FileNotFoundException.")

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.DeleteEntry(Efs.RootAnchorId, "nope"),
                                "Deleting a missing entry should throw FileNotFoundException.")

                            AssertThrows(Of InvalidDataException)(
                                Sub() Efs.GetDirectoryEntries(FileId),
                                "Listing a file as a directory should throw.")

                            AssertThrows(Of ArgumentOutOfRangeException)(
                                Sub() Efs.GetParentAnchorId(0),
                                "A zero anchor id should be rejected.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FileSystemStructureSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Payload = GenerateRandomData(12345, 3401)

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)
                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Dim Sub1 = Efs.CreateDirectory(Dir, "b")
                            Dim FileId = Efs.CreateFile(Sub1, "deep.bin", CreateAsPending:=False)
                            WriteWholeFile(Efs, FileId, Payload)
                        End Using
                        Cs.Validate()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.FindEntry(Efs.RootAnchorId, "a").ChildAnchorId
                            Dim Sub1 = Efs.FindEntry(Dir, "b").ChildAnchorId
                            Dim FileEntry = Efs.FindEntry(Sub1, "deep.bin")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, FileEntry.EntryType, "Reopened file lost its finalised state.")
                            AssertEqual(CLng(Payload.Length), FileEntry.LengthOfDataAtEntry, "Reopened file length is wrong.")
                            AssertBytesEqual(Payload, ReadWholeFile(Efs, FileEntry.ChildAnchorId), "Reopened file content is wrong.")

                        End Using
                        Cs.Validate()
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Friend Shared Sub WriteWholeFile(Efs As EmbeddedFileSystem, FileAnchorId As Long, Data As Byte())

                Using Stream = Efs.OpenFile(FileAnchorId)
                    Stream.SetLength(Data.Length)
                    Stream.Position = 0
                    Stream.Write(Data, 0, Data.Length)
                End Using

            End Sub

            Friend Shared Function ReadWholeFile(Efs As EmbeddedFileSystem, FileAnchorId As Long) As Byte()

                Using Stream = Efs.OpenFile(FileAnchorId)
                    Using Buffer As New MemoryStream()
                        Stream.CopyTo(Buffer)
                        Return Buffer.ToArray()
                    End Using
                End Using

            End Function

        End Class

    End Class

End Namespace
