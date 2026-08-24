Imports System.IO
Imports i00.Streams

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

            ''' <summary>
            ''' Verifies that cancelling ApplyOptions partway through leaves already-rewritten chunks
            ''' committed, reports WasCancelled, and leaves the stream valid and resumable.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCancelledMidRunReportsWasCancelledAndStaysValid()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .CompressionRatioThreshold = 1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Expected =
                            GenerateRandomData(
                                Options.ChunkSize * 2,
                                6401)

                        Cs.Write(0, Expected)

                        ' Both chunks start uncompressed; both need converting once this changes -
                        ' cancelling after the first record lets us prove only one was converted.
                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate

                        Dim ProgressCalls = 0

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression,
                                Sub(ProcessedUnits As Long,
                                    TotalUnits As Long,
                                    UnitType As ChunkedStream.ProcessUnitTypes,
                                    CancellationToken As ChunkedStream.CancellationToken)

                                    ProgressCalls += 1
                                    CancellationToken.Cancel = True

                                End Sub)

                        AssertTrue(
                            ProgressCalls > 0,
                            "Test setup failed to invoke the progress callback.")

                        AssertTrue(
                            Result.WasCancelled,
                            "Expected ApplyOptions to report WasCancelled.")

                        AssertEqual(
                            1,
                            Result.ExaminedChunks,
                            "Expected cancellation to stop after exactly one chunk was examined.")

                        AssertEqual(
                            1,
                            Result.RewrittenChunks,
                            "Expected exactly one chunk to have actually been rewritten before cancellation.")

                        ' Assumes physical record ids - and therefore RecordIds' processing order in
                        ' ApplyOptions - were assigned in write order, so Chunks(0)/Chunks(1) line up
                        ' with "examined first" / "examined second".
                        Dim StructureAfterCancel = Cs.GetStructure()

                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            StructureAfterCancel.Chunks(0).CompressionMethod,
                            "Expected the first chunk to actually be converted to Deflate before cancellation.")

                        AssertTrue(
                            StructureAfterCancel.Chunks(1).CompressionMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            "Expected the second chunk to remain unconverted, proving the run stopped rather than finishing silently.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Cancelling ApplyOptions should not have altered logical data.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        Dim FinalResult =
                            Reopened.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                        AssertFalse(
                            FinalResult.WasCancelled,
                            "Expected the follow-up ApplyOptions call to complete.")

                        AssertEqual(
                            1,
                            FinalResult.RewrittenChunks,
                            "Expected exactly the remaining chunk to be rewritten on the follow-up call.")

                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            Reopened.GetStructure().Chunks(1).CompressionMethod,
                            "Expected the second chunk to be converted to Deflate on the follow-up call.")

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

            ''' <summary>
            ''' Verifies HoleDirectoryModes.Auto behaves like Never below the configured threshold
            ''' (a hole freed before close is unknown after reopen, so FillHoles - which does not scan -
            ''' cannot reuse it) and like Always once the threshold is met.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HoleDirectoryAutoModeRespectsThresholdBytes()

                Dim MakeOptions =
                    Function(ThresholdBytes As Long) As ChunkedStream.ChunkedStreamOptions
                        Return New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 256,
                            .IndexPageEntryCount = 4,
                            .IndexDirectoryEntryCount = 4,
                            .HoleDirectoryMode = ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Auto,
                            .HoleDirectoryAutoThresholdBytes = ThresholdBytes,
                            .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles
                        }
                    End Function

                Dim FreedPhysicalOffset As Long
                Dim ReusedOffsetBelowThreshold As Long?

                Using Ms As New MemoryStream()

                    Dim Options = MakeOptions(Long.MaxValue) ' physical stream will never reach this

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 6501))
                        Cs.Write(Options.ChunkSize, GenerateRandomData(Options.ChunkSize, 6502))

                        FreedPhysicalOffset = Cs.GetStructure().Chunks(1).PhysicalOffset.Value

                        Cs.Remove(Options.ChunkSize, Options.ChunkSize)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        Reopened.Write(Reopened.Length, GenerateRandomData(Options.ChunkSize, 6503))

                        Dim ReopenedChunks = Reopened.GetStructure().Chunks
                        ReusedOffsetBelowThreshold = ReopenedChunks(ReopenedChunks.Count - 1).PhysicalOffset

                    End Using

                End Using

                AssertFalse(
                    ReusedOffsetBelowThreshold.HasValue AndAlso ReusedOffsetBelowThreshold.Value = FreedPhysicalOffset,
                    "Expected the below-threshold hole to be unknown after reopen, so the new chunk should not land at the freed offset.")

                Dim ReusedOffsetAboveThreshold As Long?

                Using Ms As New MemoryStream()

                    Dim Options = MakeOptions(0) ' any non-empty stream is "above" the threshold

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 6501))
                        Cs.Write(Options.ChunkSize, GenerateRandomData(Options.ChunkSize, 6502))

                        FreedPhysicalOffset = Cs.GetStructure().Chunks(1).PhysicalOffset.Value

                        Cs.Remove(Options.ChunkSize, Options.ChunkSize)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        Reopened.Write(Reopened.Length, GenerateRandomData(Options.ChunkSize, 6503))

                        Dim ReopenedChunks = Reopened.GetStructure().Chunks
                        ReusedOffsetAboveThreshold = ReopenedChunks(ReopenedChunks.Count - 1).PhysicalOffset

                        Reopened.Validate()

                    End Using

                End Using

                AssertEqual(
                    FreedPhysicalOffset,
                    ReusedOffsetAboveThreshold.Value,
                    "Expected an above-threshold hole to be persisted and reused after reopen, landing the new chunk at the freed offset.")

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