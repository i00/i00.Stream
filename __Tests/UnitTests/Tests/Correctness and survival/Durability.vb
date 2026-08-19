Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests

    Partial Class CorrectnessAndSurvival

        Public NotInheritable Class Durability

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Basic durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that plain data survives closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub PlainRoundTripSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 8,
                            1001)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Expected)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Plain stream data did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that compressed data survives closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressedRoundTripSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R
                    }

                    Dim Expected =
                        GeneratePartiallyCompressibleData(
                            0.8R,
                            ChunkedStream.DefaultChunkSize,
                            8,
                            1002)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Compressed stream data did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encrypted data survives closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptedRoundTripSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(
                            MakeKey(1003))
                    }

                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 8,
                            1003)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Encrypted stream data did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sparse logical regions survive closing and reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparseRoundTripSurvivesReopen()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(
                            Cs.Options.ChunkSize * 16)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            CLng(ChunkedStream.DefaultChunkSize * 16),
                            Reopened.Length,
                            "Sparse logical length did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Shared physical-record durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that clone-created shared physical records survive reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRoundTripSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                2001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize * 2,
                            Cs.Options.ChunkSize)

                        Expected = Cs.ToArray()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Clone data did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shared physical records survive repeated reopen cycles.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SharedPhysicalRecordsSurviveMultipleReopens()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                2002)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize * 2,
                            Cs.Options.ChunkSize)

                        Expected = Cs.ToArray()

                    End Using

                    For ReopenIndex = 1 To 5

                        Using Cs = ChunkedStream.Open(Ms)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Shared record durability failed after reopen {ReopenIndex}.")

                            Cs.Validate()

                        End Using

                    Next

                End Using

            End Sub

            ' ================================================================================
            ' ApplyOptions durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that compression migration survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GeneratePartiallyCompressibleData(
                            0.75R,
                            ChunkedStream.DefaultChunkSize,
                            8,
                            3001)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Expected)

                        Cs.Options.CompressionMethod =
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate

                        Cs.Options.CompressionRatioThreshold = 0.95R

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Compression migration did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encryption migration survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsEncryptionSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 8,
                            3002)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)

                        Cs.Options.EncryptionInfo =
                            New ChunkedStream.EncryptionInfo(
                                MakeKey(3002))

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Encryption)

                    End Using

                    Options.EncryptionInfo =
                        New ChunkedStream.EncryptionInfo(
                            MakeKey(3002))

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Encryption migration did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sparseness migration survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsSparsenessSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GenerateZeroedData(
                            ChunkedStream.DefaultChunkSize * 8)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Expected)

                        Cs.Options.StoreSparseChunks = True

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Sparseness migration did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Defragmentation durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that move defragmentation survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentMoveSurvivesReopen()

                DefragmentationRoundTrip(
                    ChunkedStream.DefragTypes.Move,
                    4001)

            End Sub

            ''' <summary>
            ''' Verifies that sequence defragmentation survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentSequenceSurvivesReopen()

                DefragmentationRoundTrip(
                    ChunkedStream.DefragTypes.Sequence,
                    4002)

            End Sub

            ''' <summary>
            ''' Verifies that rebuild defragmentation survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentRebuildSurvivesReopen()

                DefragmentationRoundTrip(
                    ChunkedStream.DefragTypes.Rebuild,
                    4003)

            End Sub

            ' ================================================================================
            ' Metadata durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that paged metadata survives reopening and validation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MetadataPagingSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4
                    }

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 127

                            Cs.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(
                                    Options.ChunkSize,
                                    5000 + ChunkIndex))

                        Next

                        Expected = Cs.ToArray()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Paged metadata stream did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that persisted hole-directory metadata survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HoleDirectorySurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4,
                        .HoleDirectoryMode = ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Always
                    }

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 63

                            Cs.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(
                                    Options.ChunkSize,
                                    6000 + ChunkIndex))

                        Next

                        Expected = Cs.ToArray()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Hole-directory metadata did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Checkpoint durability
            ' ================================================================================

            ''' <summary>
            ''' Verifies that committed checkpoint changes survive reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            7001)

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, Expected)

                            Checkpoint.Commit()

                        End Using

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Committed checkpoint data did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rolled-back checkpoint changes do not survive reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Original =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            7002)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(
                                0,
                                GenerateRandomData(
                                    Cs.Options.ChunkSize * 4,
                                    7003))

                        End Using

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Original,
                            Reopened.ToArray(),
                            "Checkpoint rollback did not survive reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Sub DefragmentationRoundTrip(DefragType As ChunkedStream.DefragTypes,
                                                        Seed As Integer)

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        For ChunkIndex = 0 To 15

                            Cs.Write(
                                ChunkIndex * Cs.Options.ChunkSize,
                                GenerateRandomData(
                                    Cs.Options.ChunkSize,
                                    Seed + ChunkIndex))

                        Next

                        For ChunkIndex = 0 To 15 Step 2

                            Cs.Write(
                                ChunkIndex * Cs.Options.ChunkSize,
                                GeneratePatternData(
                                    Cs.Options.ChunkSize,
                                    8000 + ChunkIndex))

                        Next

                        Expected = Cs.ToArray()

                        Cs.Defragment(DefragType)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            $"{DefragType} durability failed after reopen.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace