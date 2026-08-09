Imports System.IO
Imports System.Text
Imports StreamEncryption.Streams
Imports System.IO.Compression

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        Public NotInheritable Class Core

            ''' <summary>
            ''' Verifies that ChunkedStream behaves as a fully compatible .NET Stream
            ''' by successfully round-tripping a ZipArchive through an encrypted
            ''' ChunkedStream instance.
            '''
            ''' This test intentionally exercises the standard Stream API surface
            ''' rather than the ChunkedStream random-access methods. The ZipArchive
            ''' implementation performs arbitrary combinations of Read, Write,
            ''' Seek, Position and Length operations, making it an effective
            ''' integration test for Stream compatibility.
            '''
            ''' The test writes a ZIP file containing random data directly into an
            ''' encrypted ChunkedStream, then reopens the ZIP archive from the same
            ''' ChunkedStream and verifies that the extracted payload exactly matches
            ''' the original source data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StreamCompatibility_ZipArchiveRoundTrip()

                Dim SourceData =
                    Helpers.MakeRandomData(
                        1024 * 1024,
                        123)

                Using Ms As New MemoryStream()

                    Using Cs =
                        ChunkedStream.Open(
                            Ms,
                            New ChunkedStream.ChunkedStreamOptions With {
                                .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                                .EncryptionInfo = New ChunkedStream.EncryptionInfo("Password")
                            })

                        Using Archive =
                            New ZipArchive(
                                Cs,
                                ZipArchiveMode.Create,
                                True)

                            Dim Entry =
                                Archive.CreateEntry(
                                    "Test.bin",
                                    CompressionLevel.Optimal)

                            Using EntryStream = Entry.Open()

                                EntryStream.Write(
                                    SourceData,
                                    0,
                                    SourceData.Length)

                            End Using

                        End Using

                        Cs.Position = 0

                        Using Archive =
                            New ZipArchive(
                                Cs,
                                ZipArchiveMode.Read,
                                True)

                            Dim Entry =
                                Archive.GetEntry("Test.bin")

                            AssertTrue(Entry IsNot Nothing, "Zip entry not found.")

                            Using EntryStream = Entry.Open()

                                Using Result As New MemoryStream()

                                    EntryStream.CopyTo(Result)

                                    AssertBytesEqual(
                                        SourceData,
                                        Result.ToArray(),
                                        "Zip round-trip data mismatch.")

                                End Using

                            End Using

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that physically stored all-zero chunks are stored correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseChunksStreamStorageCheck()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = True
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = MakeBuffer(ChunkedStream.ChunkSize)

                        Cs.Write(0, Data)

                        Dim Struct = Cs.GetStructure()
                        Dim Chunk = Struct.Chunks.First()

                        AssertEqual(Chunk.PayloadLength, ChunkedStream.ChunkSize, $"Empty chunk with {NameOf(ChunkedStream.ChunkedStreamOptions.StoreSparseChunks)} set should be {ChunkedStream.ChunkSize} bytes.")
                        AssertTrue(Chunk.IsAllocated, "Expected stored zero chunk to be allocated.")
                        AssertTrue(Chunk.IsPlaintextAllZero, "Stored zero chunk did not expose PlaintextAllZero.")
                        AssertTrue((Chunk.ChunkFlags And ChunkedStream.ChunkFlags.PlaintextAllZero) = ChunkedStream.ChunkFlags.PlaintextAllZero,
                                   "PlaintextAllZero flag was not set.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sparse chunks are stored correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseChunksCheck()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(ChunkedStream.ChunkSize)

                        Dim Struct = Cs.GetStructure()
                        Dim Chunk = Struct.Chunks.First()

                        AssertEqual(Chunk.PayloadLength, 0, "Sparse chunk size should be 0.")
                        AssertTrue(Chunk.IsSparse, "Expected chunk to be sparse.")
                        AssertTrue(Chunk.IsPlaintextAllZero, "Sparse chunk did not expose PlaintextAllZero.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that plaintext data can be written and read back at several offsets and lengths.
            ''' </summary>
            <UnitTester.SimpleTest({0, 0}, True)>
            <UnitTester.SimpleTest({1, 0}, True)>
            <UnitTester.SimpleTest({1000, 0}, True)>
            <UnitTester.SimpleTest({65536, 0}, True)>
            <UnitTester.SimpleTest({131072, 0}, True)>
            <UnitTester.SimpleTest({5000, 65436}, True)>
            <UnitTester.SimpleTest({100000, 12345}, True)>
            Public Shared Function PlainRoundTrip(Length As Integer,
                                                      Offset As Integer) As Boolean

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakePattern(Length, 17)

                        Cs.Write(Offset, Data)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(Offset, Result)

                        If BytesRead <> Data.Length Then
                            Return False
                        End If

                        Return BytesEqual(Data, Result)

                    End Using

                End Using

            End Function

            ''' <summary>
            ''' Verifies that sparse chunks read back as zero-filled data.
            ''' </summary>
            <UnitTester.SimpleTest({0}, True)>
            <UnitTester.SimpleTest({1}, True)>
            <UnitTester.SimpleTest({5}, True)>
            Public Shared Function SparseReadReturnsZeroes(ChunkOffset As Integer) As Boolean

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(CLng(ChunkedStream.ChunkSize) * 10L)

                        Dim Output = MakeBuffer(4096)
                        Dim ReadOffset = CLng(ChunkedStream.ChunkSize) * CLng(ChunkOffset)

                        Cs.Read(ReadOffset, Output)

                        For Each value In Output
                            If value <> 0 Then
                                Return False
                            End If
                        Next

                        Return True

                    End Using

                End Using

            End Function

            ''' <summary>
            ''' Verifies that ToArray returns the complete logical stream contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ToArrayReturnsCompleteData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakePattern(200000, 99)

                        Cs.Write(0, Data)

                        Dim Result = Cs.ToArray()

                        AssertBytesEqual(
                        Data,
                        Result,
                        "ToArray did not return the expected data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shrinking the logical stream length preserves the expected prefix bytes.
            ''' </summary>
            <UnitTester.SimpleTest({1000, 100}, True)>
            <UnitTester.SimpleTest({100000, 1000}, True)>
            <UnitTester.SimpleTest({200000, 65536}, True)>
            Public Shared Function SetLengthShrinkPreservesPrefix(InitialLength As Integer,
                                                                      FinalLength As Integer) As Boolean

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakePattern(InitialLength, 41)

                        Cs.Write(0, Data)
                        Cs.SetLength(FinalLength)

                        If Cs.Length <> FinalLength Then
                            Return False
                        End If

                        Dim Result = MakeBuffer(FinalLength)
                        Dim BytesRead = Cs.Read(0, Result)

                        If BytesRead <> FinalLength Then
                            Return False
                        End If

                        For Index = 0 To FinalLength - 1
                            If Result(Index) <> Data(Index) Then
                                Return False
                            End If
                        Next

                        Return True

                    End Using

                End Using

            End Function

            ''' <summary>
            ''' Verifies that an empty backing stream creates a valid empty ChunkedStream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CreateEmptyStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        AssertEqual(0L, Cs.Length, "Empty stream length was incorrect.")

                        Dim Struct = Cs.GetStructure()

                        AssertNotNothing(Struct, "Structure snapshot was Nothing.")
                        AssertEqual(0L, Struct.LogicalLength, "Structure logical length was incorrect.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that uncompressed plaintext data remains intact after closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenPreservesPlainData()

                Using Ms As New MemoryStream()

                    Dim Data = Encoding.UTF8.GetBytes("ChunkedStream plain reopen test")

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(123, Data)
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(123, Result)

                        AssertEqual(Data.Length, BytesRead, "Unexpected byte count after reopen.")
                        AssertBytesEqual(Data, Result, "Plain data changed after reopen.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that expanding stream length creates zero-filled sparse areas.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthExpandReadsZeroes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakePattern(1000, 31)

                        Cs.Write(0, Data)
                        Cs.SetLength(CLng(ChunkedStream.ChunkSize) * 3L)

                        Dim Output = MakeBuffer(4096)
                        Cs.Read(ChunkedStream.ChunkSize * 2L, Output)

                        For Each value In Output
                            If value <> 0 Then
                                Throw New Exception("Expanded sparse area was not zero-filled.")
                            End If
                        Next

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a partial overwrite across a chunk boundary does not corrupt unaffected bytes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub PartialOverwritePreservesUnaffectedBytes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original = MakePattern(ChunkedStream.ChunkSize * 2, 37)
                        Dim Patch = MakePattern(1000, 43)
                        Dim PatchOffset = ChunkedStream.ChunkSize - 500

                        Cs.Write(0, Original)
                        Cs.Write(PatchOffset, Patch)

                        Dim Expected = DirectCast(Original.Clone(), Byte())
                        Buffer.BlockCopy(Patch, 0, Expected, PatchOffset, Patch.Length)

                        Dim Result = MakeBuffer(Expected.Length)
                        Cs.Read(0, Result)

                        AssertBytesEqual(Expected, Result, "Partial overwrite corrupted unaffected bytes.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that validation succeeds for a healthy stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForHealthyStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, MakePattern(200000, 47))
                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that validation fails after a chunk record is corrupted.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsAfterCorruption()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, MakePattern(100000, 53))

                        Dim Struct = Cs.GetStructure()
                        Dim FirstChunk = Struct.Chunks.First(Function(chunk) chunk.IsAllocated)

                        Dim CorruptOffset = FirstChunk.PhysicalOffset.Value + 50

                        Ms.Position = CorruptOffset

                        Dim OriginalByte = Ms.ReadByte()

                        If OriginalByte < 0 Then
                            Throw New Exception("Could not read byte to corrupt.")
                        End If

                        Ms.Position = CorruptOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        AssertThrows(Of System.Security.Cryptography.CryptographicException)(
                                Sub()
                                    Cs.Validate()
                                End Sub,
                                "Validation should fail after corrupting a chunk record.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that structure snapshots contain header, chunk and index regions after data is written.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StructureSnapshotContainsExpectedRegions()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, MakePattern(ChunkedStream.ChunkSize * 2, 59))

                        Dim Struct = Cs.GetStructure()

                        AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Header),
                                       "Structure did not contain header regions.")

                        AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Chunk),
                                       "Structure did not contain chunk regions.")

                        AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Index),
                                       "Structure did not contain an index region.")

                        AssertTrue(Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                                       "Structure did not contain allocated chunks.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that defragmentation preserves logical data.
            ''' </summary>

            <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Move})>
            <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Sequence})>
            <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Rebuild})>
            Private Shared Sub DefragmentPreservesData(Type As ChunkedStream.DefragTypes)

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected = CreateFragmentedContent(Cs)

                        Cs.Defragment(Type)

                        Dim Result = MakeBuffer(Expected.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        AssertEqual(Expected.Length, BytesRead, $"Unexpected byte count after {Type}.")
                        AssertBytesEqual(Expected, Result, $"{Type} corrupted data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Move defragmentation does not increase fragmentation and preserves data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentMoveDoesNotIncreaseFragmentation()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected = CreateFragmentedContent(Cs)
                        Dim Before = Cs.GetFragmentation()

                        Cs.Defragment(ChunkedStream.DefragTypes.Move)

                        Dim After = Cs.GetFragmentation()

                        If After > Before Then
                            Throw New Exception($"Move defrag increased fragmentation. Before={Before}, After={After}.")
                        End If

                        Dim Result = MakeBuffer(Expected.Length)
                        Cs.Read(0, Result)

                        AssertBytesEqual(Expected, Result, "Move defrag corrupted data.")

                    End Using

                End Using

            End Sub

        End Class
    End Class

End Namespace