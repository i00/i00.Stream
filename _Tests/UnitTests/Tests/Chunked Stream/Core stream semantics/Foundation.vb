Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        Public NotInheritable Class Foundation

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Stream creation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a newly created stream is empty.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NewStreamIsEmpty()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        AssertEqual(
                            0L,
                            Cs.Length,
                            "New stream should have zero length.")

                        AssertEqual(
                            0,
                            Cs.ToArray().Length,
                            "New stream should contain no data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that an empty stream survives reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EmptyStreamCanBeReopened()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            0L,
                            Reopened.Length,
                            "Empty stream length changed after reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Basic write/read behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that basic writes can be read back correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub BasicWriteReadRoundTrip()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1001)

                        Cs.Write(0, Expected)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Basic write/read round-trip failed.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that overwriting existing data updates the logical contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OverwriteUpdatesData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GeneratePatternData(
                                Cs.options.ChunkSize * 2,
                                1101)

                        Cs.Write(0, Expected)

                        Dim Patch =
                            GenerateRandomData(
                                500,
                                1102)

                        Cs.Write(300, Patch)

                        Overlay(
                            Expected,
                            Patch,
                            300)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Overwrite did not update logical data correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Length behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that length reflects the highest written byte.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub LengthTracksWrittenData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                12345,
                                1201)

                        Cs.Write(0, Data)

                        AssertEqual(
                            CLng(Data.Length),
                            Cs.Length,
                            "Length did not track written data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing beyond the current length extends the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub WritingPastEndExtendsLength()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            Cs.options.ChunkSize * 4L,
                            GenerateRandomData(
                                1000,
                                1301))

                        AssertEqual(
                            (Cs.options.ChunkSize * 4L) + 1000L,
                            Cs.Length,
                            "Writing beyond end did not extend length correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Read behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that reading unwritten data returns zeroes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub UnwrittenDataReadsAsZeroes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(
                            Cs.options.ChunkSize * 4)

                        AssertBytesEqual(
                            GenerateZeroedData(
                                Cs.options.ChunkSize * 4),
                            Cs.ToArray(),
                            "Unwritten logical space did not read as zeroes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that partial reads return the correct subset of data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub PartialReadReturnsCorrectData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                1401)

                        Cs.Write(0, Data)

                        Dim Expected =
                            Slice(
                                Data,
                                500,
                                1000)

                        Dim Actual =
                            GenerateZeroedData(
                                Expected.Length)

                        Dim BytesRead =
                            Cs.Read(
                                500,
                                Actual)

                        AssertEqual(
                            Expected.Length,
                            BytesRead,
                            "Unexpected byte count from partial read.")

                        AssertBytesEqual(
                            Expected,
                            Actual,
                            "Partial read returned incorrect data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Reopen behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that ordinary stream data survives reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenPreservesData()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 8,
                            1501)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Expected)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Reopen did not preserve logical data.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Structure sanity
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a populated stream exposes allocated chunks in structure
            ''' diagnostics.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StructureReportsAllocatedChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1601))

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Count > 0,
                            "Structure did not report any chunks.")

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                            "Structure did not report allocated chunks.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Validation sanity
            ' ================================================================================

            ''' <summary>
            ''' Verifies that validation succeeds on representative healthy streams.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidationSucceedsOnHealthyStreams()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 8,
                                1701))

                        Cs.Write(
                            Cs.options.ChunkSize,
                            GeneratePatternData(
                                500,
                                1702))

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a healthy stream on a read-only backing store opens for reading,
            ''' reports itself as not writable, serves reads / diagnostics / validation, and
            ''' rejects a write attempt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReadOnlyBackingStreamOpensForReadingOnly()

                Dim Bytes As Byte()
                Dim Expected As Byte()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Expected = GenerateRandomData(Cs.Options.ChunkSize * 5, 1801)
                        Cs.Write(0, Expected)
                        Cs.Validate()
                    End Using

                    Bytes = Ms.ToArray()

                End Using

                Using ReadOnlyStream As New MemoryStream(Bytes, False)

                    Using Cs = ChunkedStream.Open(ReadOnlyStream)

                        AssertFalse(Cs.CanWrite, "A stream on a read-only backing store must report CanWrite = False.")
                        AssertTrue(Cs.CanRead, "The stream should still be readable.")

                        AssertEqual(
                            ChunkedStream.RecoveryStates.None,
                            Cs.RecoveryStateAtOpen,
                            "A healthy stream should not be pending recovery.")

                        AssertBytesEqual(Expected, Cs.ToArray(), "Read-only stream returned the wrong data.")

                        Dim Snapshot = Cs.GetStructure()
                        AssertTrue(Snapshot.AllocatedChunkCount > 0, "Expected allocated chunks in the diagnostic snapshot.")

                        Cs.Validate()

                        AssertThrows(Of NotSupportedException)(
                            Sub() Cs.Write(0, New Byte(15) {}),
                            "Writing to a read-only backing store should throw.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace