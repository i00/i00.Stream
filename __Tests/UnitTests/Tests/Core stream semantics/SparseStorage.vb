Imports System.IO
Imports System.Linq
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        Public NotInheritable Class SparseStorage

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Sparse chunk creation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that writing zero data produces sparse chunks when sparse chunks
            ''' are not stored physically.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZeroChunksBecomeSparse()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 8)

                        Cs.Write(0, Data)

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) Not chunk.IsAllocated),
                            "Expected at least one sparse chunk.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Sparse chunk data did not read back correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sparse chunks can be stored physically when configured.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseChunksCanBeStoredPhysically()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = True
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 8)

                        Cs.Write(0, Data)

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.All(Function(chunk) chunk.IsAllocated),
                            "Sparse chunks should have been physically stored.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Physically stored sparse chunks did not read back correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Sparse / allocated transitions
            ' ================================================================================

            ''' <summary>
            ''' Verifies that writing non-zero data into a sparse chunk allocates a physical
            ''' record.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseChunkBecomesAllocatedWhenWritten()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateZeroedData(
                                Cs.Options.ChunkSize))

                        Dim Before =
                            Cs.GetStructure()

                        AssertFalse(
                            Before.Chunks.Single().IsAllocated,
                            "Chunk should initially be sparse.")

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                2001)

                        Cs.Write(0, Data)

                        Dim AfterWrite =
                            Cs.GetStructure()

                        AssertTrue(
                            AfterWrite.Chunks.Single().IsAllocated,
                            "Writing non-zero data should allocate a physical record.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Allocated chunk did not read back correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing zeroes over an allocated chunk can return it to sparse
            ''' storage.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AllocatedChunkCanBecomeSparse()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                2101))

                        Dim Before =
                            Cs.GetStructure()

                        AssertTrue(
                            Before.Chunks.Single().IsAllocated,
                            "Chunk should initially be allocated.")

                        Cs.Write(
                            0,
                            GenerateZeroedData(
                                Cs.Options.ChunkSize))

                        Dim AfterWrite =
                            Cs.GetStructure()

                        AssertFalse(
                            AfterWrite.Chunks.Single().IsAllocated,
                            "Chunk should have become sparse.")

                        AssertBytesEqual(
                            GenerateZeroedData(
                                Cs.Options.ChunkSize),
                            Cs.ToArray(),
                            "Sparse conversion changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Mixed sparse layouts
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a mixture of allocated and sparse chunks read back correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MixedSparseAndAllocatedLayoutRoundTrips()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkCount = 16

                        Dim Expected =
                            GenerateZeroedData(
                                ChunkCount * Cs.Options.ChunkSize)

                        For ChunkIndex = 0 To ChunkCount - 1

                            Dim Offset =
                                ChunkIndex * Cs.Options.ChunkSize

                            If ChunkIndex Mod 2 = 0 Then

                                Dim Data =
                                    GenerateRandomData(
                                        Cs.Options.ChunkSize,
                                        3000 + ChunkIndex)

                                Cs.Write(Offset, Data)
                                Overlay(Expected, Data, Offset)

                            Else

                                Dim Data =
                                    GenerateZeroedData(
                                        Cs.Options.ChunkSize)

                                Cs.Write(Offset, Data)

                            End If

                        Next

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Mixed sparse layout did not read back correctly.")

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                            "Expected allocated chunks.")

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) Not chunk.IsAllocated),
                            "Expected sparse chunks.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Length behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that extending length creates logical sparse space.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthExtensionCreatesSparseRegion()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.SetLength(
                            Cs.Options.ChunkSize * 32)

                        AssertEqual(
                            CLng(Cs.Options.ChunkSize * 32),
                            Cs.Length,
                            "Unexpected length after extension.")

                        AssertBytesEqual(
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 32),
                            Cs.ToArray(),
                            "Extended sparse region did not read back as zeroes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clear behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that Clear creates sparse chunks when sparse chunks are not stored physically.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearCreatesSparseRangesWhenStoreSparseChunksFalse()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                3001))

                        Dim Before =
                            Cs.GetStructure()

                        Cs.Clear(
                            Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize)

                        Dim After =
                            Cs.GetStructure()

                        AssertTrue(
                            Before.Chunks.All(Function(chunk) chunk.IsAllocated),
                            "Expected no sparse chunks before Clear.")

                        AssertTrue(
                            After.Chunks.Any(Function(chunk) chunk.IsAllocated = False),
                            "Expected sparse chunks after Clear.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clone behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that cloning sparse regions preserves logical contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneSparseRegionPreservesData()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.SetLength(
                            Cs.Options.ChunkSize * 4)

                        Dim Original =
                            Cs.ToArray()

                        Cs.Clone(
                            0,
                            Cs.Length,
                            Cs.Length)

                        Dim Expected =
                            CombineArrays(
                                Original,
                                Original)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Cloned sparse region did not read back correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Compression / encryption interaction
            ' ================================================================================

            ''' <summary>
            ''' Verifies that sparse storage works correctly alongside compression and
            ''' encryption.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseStorageWithCompressionAndEncryption()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(5001))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkCount = 8

                        Dim Expected =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * ChunkCount)

                        For ChunkIndex = 0 To ChunkCount - 1

                            Dim Offset =
                                ChunkIndex * Cs.Options.ChunkSize

                            If ChunkIndex Mod 2 = 0 Then

                                Dim Data =
                                    GenerateRandomData(
                                        Cs.Options.ChunkSize,
                                        5002 + ChunkIndex)

                                Cs.Write(Offset, Data)
                                Overlay(Expected, Data, Offset)

                            Else

                                Cs.Write(
                                    Offset,
                                    GenerateZeroedData(
                                        Cs.Options.ChunkSize))

                            End If

                        Next

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Sparse/encrypted/compressed layout corrupted logical data.")

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                            "Expected allocated chunks.")

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) Not chunk.IsAllocated),
                            "Expected sparse chunks.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Migration behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that ApplyOptions can convert existing zero-filled chunks into sparse
            ''' chunks without changing logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparsenessMigrationCreatesSparseChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 8)

                        Cs.Write(0, Data)

                        Cs.Options.StoreSparseChunks = False

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) Not chunk.IsAllocated),
                            "Expected sparse chunks after migration.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Sparseness migration changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace