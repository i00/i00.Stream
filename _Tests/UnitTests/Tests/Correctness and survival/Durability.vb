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

            ''' <summary>
            ''' Verifies that cancelling a chunk-size rewrite rolls back cleanly, leaves the
            ''' stream valid and resumable, and rebuilds the free-space map so a later write
            ''' still reuses an existing physical-record hole rather than only appending.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkSizeRewriteCancelledMidRunStaysValidAndReusesHoles()

                Const OriginalChunkSize As Integer = 256
                Const RewrittenChunkSize As Integer = 128
                Const ChunkCount As Integer = 6

                Using Ms As New MemoryStream()

                    ' Small metadata pages keep the physical layout dominated by chunk
                    ' records so the freed-chunk hole is the significant free region.
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = OriginalChunkSize,
                        .IndexPageEntryCount = 16,
                        .IndexDirectoryEntryCount = 16
                    }

                    Dim ExpectedFinal As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Expected =
                            GenerateRandomData(
                                OriginalChunkSize * ChunkCount,
                                7301)

                        Cs.Write(0, Expected)

                        ' Overwrite one chunk in place. Copy-on-write appends a replacement
                        ' physical record and frees the original, leaving a hole the size of a
                        ' full chunk record part way through the data area.
                        Dim ReplacementChunk =
                            GenerateRandomData(
                                OriginalChunkSize,
                                7302)

                        Dim ReplacedOffset = OriginalChunkSize * 3

                        Cs.Write(ReplacedOffset, ReplacementChunk)
                        Overlay(Expected, ReplacementChunk, ReplacedOffset)

                        Dim FragmentationBefore = Cs.GetFragmentation()

                        AssertTrue(
                            FragmentationBefore > 0,
                            "Test setup expected a physical-record hole after overwriting a chunk.")

                        Cs.Options.ChunkSize = RewrittenChunkSize

                        Dim ProgressCalls = 0

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.ChunkSize,
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
                            "Expected the cancelled chunk-size rewrite to report WasCancelled.")

                        AssertEqual(
                            OriginalChunkSize,
                            Cs.GetStructure().Chunks(0).PayloadLength,
                            "Cancelled chunk-size rewrite should have rolled back to the original chunk size.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Cancelling the chunk-size rewrite must not alter logical data.")

                        Cs.Validate()

                        ' The rollback must also have rebuilt the free-space map. Abandon the
                        ' rewrite, then append one more full chunk: its physical record should
                        ' drop into the freed hole, below the previous live-data end. If the
                        ' map had only been cleared, BestFit allocation would append past the
                        ' live data instead.
                        Cs.Options.ChunkSize = OriginalChunkSize

                        Dim LiveDataEndBeforeAppend = Cs.GetStructure().LiveDataEndOffset

                        Dim AppendedChunk =
                            GenerateRandomData(
                                OriginalChunkSize,
                                7303)

                        Cs.Write(Cs.Length, AppendedChunk)

                        Dim AppendedChunkOffset = Cs.GetStructure().Chunks(ChunkCount).PhysicalOffset

                        AssertTrue(
                            AppendedChunkOffset.HasValue AndAlso AppendedChunkOffset.Value < LiveDataEndBeforeAppend,
                            $"Expected the appended chunk to reuse the freed hole below the prior live-data end ({LiveDataEndBeforeAppend}), proving the free-space map was rebuilt after cancellation rather than left empty. Appended chunk offset was {AppendedChunkOffset}.")

                        ExpectedFinal = CombineArrays(Expected, AppendedChunk)

                        ' The stream must remain resumable: a full, uncancelled rewrite should
                        ' complete and apply the requested chunk size.
                        Cs.Options.ChunkSize = RewrittenChunkSize

                        Dim FinalResult =
                            Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.ChunkSize)

                        AssertFalse(
                            FinalResult.WasCancelled,
                            "Expected the follow-up chunk-size rewrite to complete.")

                        AssertEqual(
                            RewrittenChunkSize,
                            Cs.GetStructure().Chunks(0).PayloadLength,
                            "Follow-up chunk-size rewrite did not apply the requested chunk size.")

                        AssertBytesEqual(
                            ExpectedFinal,
                            Cs.ToArray(),
                            "Follow-up chunk-size rewrite changed logical data.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            ExpectedFinal,
                            Reopened.ToArray(),
                            "Data did not survive reopen after a cancelled then completed chunk-size rewrite.")

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
            ''' Verifies HoleDirectoryModes.Auto behaves like Never below the configured
            ''' threshold and like Always once the threshold is met.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HoleDirectoryAutoModeRespectsThresholdBytes()

                Dim MakeOptions =
                    Function(ThresholdBytes As Long) As ChunkedStream.ChunkedStreamOptions
                        Return New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 256,
                            .IndexPageEntryCount = 4,
                            .IndexDirectoryEntryCount = 4,
                            .HoleDirectoryMode =
                                ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Auto,
                            .HoleDirectoryAutoThresholdBytes = ThresholdBytes,
                            .NewChunkWriteLocationPolicy =
                                ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                            .NewIndexPageWriteLocationPolicy =
                                ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append,
                            .NewIndexDirectoryPageWriteLocationPolicy =
                                ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append
                        }
                    End Function

                Dim PhysicalLengthBeforeBelowThresholdWrite As Long
                Dim ReusedOffsetBelowThreshold As Long?

                Using Ms As New MemoryStream()

                    Dim Options = MakeOptions(Long.MaxValue)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 6501))
                        Cs.Write(Options.ChunkSize,
                                 GenerateRandomData(Options.ChunkSize, 6502))

                        Cs.Remove(Options.ChunkSize, Options.ChunkSize)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        PhysicalLengthBeforeBelowThresholdWrite = Ms.Length

                        Reopened.Write(Reopened.Length,
                                       GenerateRandomData(Options.ChunkSize, 6503))

                        Dim ReopenedChunks = Reopened.GetStructure().Chunks

                        ReusedOffsetBelowThreshold =
                            ReopenedChunks(ReopenedChunks.Count - 1).PhysicalOffset

                        Reopened.Validate()

                    End Using

                End Using

                AssertTrue(
                    ReusedOffsetBelowThreshold.HasValue,
                    "Expected the below-threshold chunk to have a physical offset.")

                AssertTrue(
                    ReusedOffsetBelowThreshold.Value >=
                        PhysicalLengthBeforeBelowThresholdWrite,
                    $"Expected BestFit to append because no hole directory was persisted. " &
                    $"Physical length before write: " &
                    $"{PhysicalLengthBeforeBelowThresholdWrite:N0}; " &
                    $"new chunk offset: {ReusedOffsetBelowThreshold.Value:N0}.")

                Dim PhysicalLengthBeforeAboveThresholdWrite As Long
                Dim ReusedOffsetAboveThreshold As Long?

                Using Ms As New MemoryStream()

                    Dim Options = MakeOptions(0)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 6501))
                        Cs.Write(Options.ChunkSize,
                                 GenerateRandomData(Options.ChunkSize, 6502))

                        Cs.Remove(Options.ChunkSize, Options.ChunkSize)

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        PhysicalLengthBeforeAboveThresholdWrite = Ms.Length

                        Reopened.Write(Reopened.Length,
                                       GenerateRandomData(Options.ChunkSize, 6503))

                        Dim ReopenedChunks = Reopened.GetStructure().Chunks

                        ReusedOffsetAboveThreshold =
                            ReopenedChunks(ReopenedChunks.Count - 1).PhysicalOffset

                        Reopened.Validate()

                    End Using

                End Using

                AssertTrue(
                    ReusedOffsetAboveThreshold.HasValue,
                    "Expected the above-threshold chunk to have a physical offset.")

                AssertTrue(
                    ReusedOffsetAboveThreshold.Value <
                        PhysicalLengthBeforeAboveThresholdWrite,
                    $"Expected BestFit to reuse persisted free space below the previous " &
                    $"physical end. Physical length before write: " &
                    $"{PhysicalLengthBeforeAboveThresholdWrite:N0}; " &
                    $"new chunk offset: {ReusedOffsetAboveThreshold.Value:N0}.")

            End Sub

            ''' <summary>
            ''' Verifies that a metadata persist which leaves the root unchanged does not
            ''' rewrite it. A no-op checkpoint commit still runs a full persist; with no
            ''' extent, physical-record, count, or next-id change the rebuilt root is
            ''' byte-for-byte identical to the persisted one, so it must stay where it is
            ''' rather than being appended afresh and growing the backing store.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub UnchangedMetadataRootIsNotRewrittenOnPersist()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .HoleDirectoryMode = ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Never
                }

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                ChunkedStream.DefaultChunkSize * 4,
                                7401))

                        Dim RootOffsetBeforeCommit = Cs.GetStructure().MetadataRootOffset
                        Dim PhysicalLengthBeforeCommit = Ms.Length

                        Using Checkpoint = Cs.CreateCheckpoint()
                            Checkpoint.Commit()
                        End Using

                        AssertEqual(
                            RootOffsetBeforeCommit,
                            Cs.GetStructure().MetadataRootOffset,
                            "A no-op persist rewrote the unchanged metadata root to a new location.")

                        AssertEqual(
                            PhysicalLengthBeforeCommit,
                            Ms.Length,
                            "A no-op persist grew the backing store by rewriting the unchanged metadata root.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)
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