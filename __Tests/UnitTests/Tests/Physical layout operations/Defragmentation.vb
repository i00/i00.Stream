Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests

    Partial Class PhysicalLayoutOperations

        Public NotInheritable Class Defragmentation

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Data preservation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that every defragmentation mode preserves logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationPreservesData()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Dim Expected As Byte()

                        Using Cs = ChunkedStream.Open(Ms)

                            Expected =
                                CreateFragmentedStream(
                                    Cs,
                                    1000 + CInt(DefragType))

                            Cs.Defragment(DefragType)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed logical data. DefragType={DefragType}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Fragmentation behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that defragmentation never increases fragmentation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationDoesNotIncreaseFragmentation()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Using Cs = ChunkedStream.Open(Ms)

                            CreateFragmentedStream(
                                Cs,
                                2000 + CInt(DefragType))

                            Dim Before =
                                Cs.GetFragmentation()

                            Cs.Defragment(DefragType)

                            Dim AfterDefrag =
                                Cs.GetFragmentation()

                            AssertTrue(
                                AfterDefrag <= Before,
                                $"Defragmentation increased fragmentation. DefragType={DefragType}, Before={Before}, After={AfterDefrag}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Shared physical-record behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that cloned/shared physical-record layouts survive every
            ''' defragmentation mode.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationPreservesSharedPhysicalRecords()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Using Cs = ChunkedStream.Open(Ms)

                            Dim Data =
                                GenerateRandomData(
                                    Cs.Options.ChunkSize * 4,
                                    3000 + CInt(DefragType))

                            Cs.Write(0, Data)

                            Cs.Clone(
                                Cs.Options.ChunkSize,
                                Cs.Options.ChunkSize * 2,
                                Cs.Options.ChunkSize)

                            Dim Expected =
                                Cs.ToArray()

                            Cs.Defragment(DefragType)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed cloned logical data. DefragType={DefragType}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Compression / Encryption / Sparse behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that compressed data survives every defragmentation mode.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationPreservesCompressedData()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            .CompressionRatioThreshold = 0.95R
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Dim Expected =
                                GeneratePartiallyCompressibleData(
                                    0.8R,
                                    Cs.Options.ChunkSize,
                                    8,
                                    4000 + CInt(DefragType))

                            Cs.Write(0, Expected)

                            Cs.Defragment(DefragType)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed compressed data. DefragType={DefragType}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that encrypted data survives every defragmentation mode.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationPreservesEncryptedData()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo =
                                New ChunkedStream.EncryptionInfo(
                                    MakeKey(5000 + CInt(DefragType)))
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Dim Expected =
                                GenerateRandomData(
                                    Cs.Options.ChunkSize * 8,
                                    5000 + CInt(DefragType))

                            Cs.Write(0, Expected)

                            Cs.Defragment(DefragType)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed encrypted data. DefragType={DefragType}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that sparse streams survive every defragmentation mode.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationPreservesSparseData()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Using Cs = ChunkedStream.Open(Ms)

                            Cs.SetLength(
                                Cs.Options.ChunkSize * 16)

                            Dim Expected =
                                Cs.ToArray()

                            Cs.Defragment(DefragType)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed sparse data. DefragType={DefragType}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Rebuild-specific behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that rebuild defragmentation applies a new chunk size while
            ''' preserving logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RebuildAppliesChunkSizeAndPreservesData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 16,
                                6001)

                        Cs.Write(0, Expected)

                        Dim NewChunkSize =
                            Cs.Options.ChunkSize \ 2

                        Cs.Options.ChunkSize =
                            NewChunkSize

                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Rebuild)

                        AssertEqual(
                            NewChunkSize,
                            Cs.ChunkSize,
                            "Rebuild did not apply the requested chunk size.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Rebuild changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Checkpoint behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that defragmentation is not allowed while a checkpoint is active.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationNotAllowedInsideCheckpoint()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Using Cs = ChunkedStream.Open(Ms)

                            Cs.Write(
                                0,
                                GenerateRandomData(
                                    Cs.Options.ChunkSize * 4,
                                    7000 + CInt(DefragType)))

                            Using Checkpoint = Cs.CreateCheckpoint()

                                AssertThrows(Of InvalidOperationException)(
                                    Sub()
                                        Cs.Defragment(DefragType)
                                    End Sub,
                                    $"Defragmentation should not be allowed inside a checkpoint. DefragType={DefragType}")

                            End Using

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Function CreateFragmentedStream(Cs As ChunkedStream,
                                                           Seed As Integer) As Byte()

                Dim ChunkCount = 16

                Dim Expected =
                    GenerateZeroedData(
                        ChunkCount * Cs.Options.ChunkSize)

                For ChunkIndex = 0 To ChunkCount - 1

                    Dim Data =
                        GenerateRandomData(
                            Cs.Options.ChunkSize,
                            Seed + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.Options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                For ChunkIndex = 0 To ChunkCount - 1 Step 2

                    Dim Data =
                        GeneratePatternData(
                            Cs.Options.ChunkSize,
                            Seed + 1000 + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.Options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                For ChunkIndex = 1 To ChunkCount - 1 Step 2

                    Dim Data =
                        GeneratePatternData(
                            Cs.Options.ChunkSize,
                            Seed + 2000 + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.Options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                Return Expected

            End Function

        End Class

    End Class

End Namespace