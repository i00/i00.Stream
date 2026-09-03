Imports System.IO
Imports i00.Streams

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

                            Cs.Validate().ThrowIfErrors()

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

                            Cs.Validate().ThrowIfErrors()

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

                            Cs.Validate().ThrowIfErrors()

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

                            Cs.Validate().ThrowIfErrors()

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

                            Cs.Validate().ThrowIfErrors()

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

                            Cs.Validate().ThrowIfErrors()

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

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that cancelling a Rebuild immediately after the new metadata has been
            ''' published still leaves the stream fully committed and readable - the final Sequence
            ''' (compaction) phase is skipped, not rolled back. This exercises the cooperative
            ''' CancellationToken path only; see RebuildInterruptedAfterPublishRethrowsAndLeaves...
            ''' for the uncooperative (exception/crash) counterpart at the same boundary.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RebuildCancelledAfterPublishStillCommitsData()

                Using Ms As New MemoryStream()

                    ' A single full chunk guarantees the write-phase loop runs exactly once, so
                    ' cancelling unconditionally inside the callback only takes effect after the
                    ' loop has already exited and the rebuild has already published.
                    Dim Expected =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            6101)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Expected)

                        Dim Cancelled = False

                        Dim Result =
                            Cs.Defragment(
                                ChunkedStream.DefragTypes.Rebuild,
                                Sub(ProcessedUnits As Long,
                                    TotalUnits As Long,
                                    UnitType As ChunkedStream.ProcessUnitTypes,
                                    CancellationToken As ChunkedStream.CancellationToken)

                                    Cancelled = True
                                    CancellationToken.Cancel = True

                                End Sub)

                        AssertTrue(
                            Cancelled,
                            "Test setup failed to invoke the progress callback.")

                        AssertEqual(
                            -1L,
                            Result,
                            "Expected Defragment to report cancellation.")

                        AssertEqual(
                            ChunkedStream.RecoveryStates.None,
                            Cs.GetRecoveryState(),
                            "Publish should have cleared recovery state even though the final sequence phase was skipped.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Cancelling after publish should not have altered committed data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.None,
                            Reopened.RecoveryStateAtOpen,
                            "Reopening after a publish-then-cancel rebuild should not trigger recovery.")

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Reopened stream lost data after a publish-then-cancel rebuild.")

                        ' Compaction was skipped, not lost: an explicit Sequence still succeeds afterwards.
                        Reopened.Defragment(ChunkedStream.DefragTypes.Sequence)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Data changed after compacting a previously publish-then-cancelled rebuild.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Storage reclamation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that every defragmentation mode reclaims physical records that no
            ''' live extent references, compacts the survivors and trims the freed tail.
            ''' Regression test for Move and Sequence leaving a stream reported as almost
            ''' fully fragmented, and its backing store un-trimmed, because the orphaned
            ''' records kept the live-data end pinned near the end of the file.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentationReclaimsUnreferencedPhysicalRecords()

                Const ChunkSize As Integer = 1024
                Const ChunkCount As Integer = 24

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = ChunkSize
                        }

                        Dim Expected =
                            GenerateRandomData(ChunkSize * ChunkCount, 15000 + CInt(DefragType))

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Write(0, Expected)

                            '
                            ' Drift every backing record's reference count upward so the
                            ' following overwrite cannot reclaim it, rewrite every chunk,
                            ' then drop those now-unreferenced records to a zero reference
                            ' count. They still occupy storage the replacement records were
                            ' appended beyond, so the live-data end stays pinned near the end
                            ' of the backing store and their bytes count as wasted space.
                            '
                            Dim OriginalRecordIds =
                                Cs.GetStructure().Chunks.
                                   Where(Function(chunk) chunk.PhysicalRecordId.HasValue).
                                   Select(Function(chunk) chunk.PhysicalRecordId.Value).
                                   Distinct().
                                   ToList()

                            For Each RecordId In OriginalRecordIds
                                Cs.Debug_CorruptPhysicalRecordMetadataRefCount(RecordId, 4096)
                            Next

                            For ChunkIndex = 0 To ChunkCount - 1

                                Dim Replacement =
                                    GenerateRandomData(ChunkSize, 15500 + ChunkIndex + CInt(DefragType))

                                Cs.Write(ChunkIndex * ChunkSize, Replacement)
                                Overlay(Expected, Replacement, ChunkIndex * ChunkSize)

                            Next

                            For Each RecordId In OriginalRecordIds
                                Cs.Debug_CorruptPhysicalRecordMetadataRefCount(RecordId, 0)
                            Next

                            Dim BeforeFragmentation = Cs.GetFragmentation()

                            AssertTrue(
                                BeforeFragmentation > 0.25R,
                                $"Test setup did not produce a fragmented stream. DefragType={DefragType}, Fragmentation={BeforeFragmentation}")

                            Cs.Defragment(DefragType)

                            Dim AfterFragmentation = Cs.GetFragmentation()

                            AssertTrue(
                                AfterFragmentation < 0.05R,
                                $"Defragmentation did not reclaim the unreferenced records. DefragType={DefragType}, Before={BeforeFragmentation}, After={AfterFragmentation}")

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragmentation changed logical data. DefragType={DefragType}")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        AssertTrue(
                            Ms.Length < CLng(ChunkSize) * ChunkCount * 3L,
                            $"Defragmentation did not trim the freed tail. DefragType={DefragType}, PhysicalLength={Ms.Length}")

                        Using Reopened = ChunkedStream.Open(Ms, Options)

                            AssertEqual(
                                ChunkedStream.RecoveryStates.None,
                                Reopened.RecoveryStateAtOpen,
                                $"Reopening a defragmented stream should not trigger recovery. DefragType={DefragType}")

                            AssertBytesEqual(
                                Expected,
                                Reopened.ToArray(),
                                $"Reopened stream lost data after defragmentation. DefragType={DefragType}")

                            Reopened.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Convergence
            ' ================================================================================

            ''' <summary>
            ''' A single Defragment(Move) call must reach a fixed point: it fills every hole
            ''' its metadata compaction shakes loose within the one call, and a further call
            ''' then relocates nothing, reports nothing saved and leaves the backing store
            ''' byte-for-byte where it was. Regression test for the move loop running a single
            ''' pass - so the holes vacated by the end-of-run metadata compaction were only
            ''' picked up by the *next* Defragment call - and for the resulting partially
            ''' compacted stream trimming and regrowing by one root length on every call.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RepeatedDefragmentationReachesAFixedPoint()

                Using Ms As New MemoryStream()

                    ' Threshold of 1 forces the hole directory to be persisted even for a
                    ' small test stream, which is what drives the churn being guarded against.
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .HoleDirectoryAutoThresholdBytes = 1
                    }

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Expected = CreateFragmentedStream(Cs, 30000)
                        Cs.Defragment(ChunkedStream.DefragTypes.Move)
                    End Using

                    Dim Saved As New List(Of Long)
                    Dim Lengths As New List(Of Long)

                    For Cycle = 1 To 8

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Saved.Add(Cs.Defragment(ChunkedStream.DefragTypes.Move))

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragment cycle {Cycle} changed logical data.")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        Lengths.Add(Ms.Length)

                    Next

                    ' The first call above already compacted the stream, so every call in the
                    ' loop is a settled no-op: nothing saved, and the length never moves.
                    For Cycle = 1 To Saved.Count

                        AssertEqual(
                            0L,
                            Saved(Cycle - 1),
                            $"A settled Defragment still reported saved bytes: per cycle = {String.Join(", ", Saved)}.")

                        AssertEqual(
                            Lengths(0),
                            Lengths(Cycle - 1),
                            $"A settled Defragment moved the backing-store length: per cycle = {String.Join(", ", Lengths)}.")

                    Next

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.None,
                            Reopened.RecoveryStateAtOpen,
                            "Reopening a repeatedly defragmented stream should not trigger recovery.")

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Reopened stream lost data after repeated defragmentation.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' One Defragment(Move) call performs all the compaction it is going to do - a
            ''' second call back-to-back relocates nothing further and reports nothing saved.
            ''' Regression test for the move loop running a single pass, so the holes freed
            ''' when its metadata compaction relocated the surviving pages were only consumed
            ''' by the *next* Defragment call.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentMoveConvergesInOneCall()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .HoleDirectoryAutoThresholdBytes = 1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Expected = CreateFragmentedStream(Cs, 41000)

                        Cs.Defragment(ChunkedStream.DefragTypes.Move)
                        Dim LengthAfterFirstCall = Ms.Length

                        Dim SecondCallSaved = Cs.Defragment(ChunkedStream.DefragTypes.Move)

                        AssertEqual(
                            0L,
                            SecondCallSaved,
                            $"A second back-to-back Defragment(Move) reclaimed {SecondCallSaved} bytes - the first call did not converge.")

                        AssertEqual(
                            LengthAfterFirstCall,
                            Ms.Length,
                            "A second back-to-back Defragment(Move) changed the backing-store length.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Defragment(Move) changed logical data.")

                        Cs.Validate().ThrowIfErrors()

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

            ''' <summary>
            ''' Verifies that Defragment refuses to run once the stream has faulted, rather
            ''' than snapshotting the half-mutated state as the "original" (C2-b).
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentIsRejectedOnAFaultedStream()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Using Cs = CreateFaultedChunkedStream(7300 + CInt(DefragType))

                        AssertThrows(Of InvalidOperationException)(
                            Sub() Cs.Defragment(DefragType),
                            $"Defragment should be rejected on a faulted stream. DefragType={DefragType}")

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' A defragment that fails partway through must fault the stream, so disposing it
            ''' does not publish the half-relocated in-memory state over the last durably
            ''' written generation. Reopening the backing store must still succeed with the
            ''' logical data intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentFailurePartwayFaultsTheStreamAndStaysReopenable()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Dim Backing As New FailingMemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024
                    }

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Expected = CreateFragmentedStream(Cs, 17000 + CInt(DefragType))
                        Cs.Flush()

                        ' Every backing write fails once the defragment is under way.
                        Backing.FailFromWriteNumber = Backing.WriteCount + 5

                        AssertThrows(Of IOException)(
                            Sub() Cs.Defragment(DefragType),
                            $"Expected the injected backing failure to surface. DefragType={DefragType}")

                        Backing.FailFromWriteNumber = 0

                        AssertThrows(Of InvalidOperationException)(
                            Sub() Cs.Write(0, New Byte(15) {}),
                            $"A failed defragment should fault the stream. DefragType={DefragType}")

                    End Using

                    Backing.Position = 0

                    Using Reopened = ChunkedStream.Open(Backing, Options)

                        Reopened.Validate().ThrowIfErrors()

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            $"A failed defragment changed the logical data. DefragType={DefragType}")

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' With Options.AutoRecoverOnFault set, a defragment that fails partway through
            ''' reloads the stream from the backing store on the next call instead of leaving
            ''' it faulted: the failed pass is discarded, the logical data is intact and the
            ''' stream stays usable with no dispose-and-reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentFailureAutoRecoversWhenEnabled()

                For Each DefragType As ChunkedStream.DefragTypes In
                    [Enum].GetValues(GetType(ChunkedStream.DefragTypes))

                    Dim Backing As New FailingMemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .AutoRecoverOnFault = True
                    }

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Dim Expected = CreateFragmentedStream(Cs, 19000 + CInt(DefragType))
                        Cs.Flush()

                        Backing.FailFromWriteNumber = Backing.WriteCount + 5

                        AssertThrows(Of IOException)(
                            Sub() Cs.Defragment(DefragType),
                            $"Expected the injected backing failure to surface. DefragType={DefragType}")

                        Backing.FailFromWriteNumber = 0

                        ' The next call reloads the last durable generation - no fault, no reopen.
                        Dim Patch = GenerateRandomData(64, 99000 + CInt(DefragType))
                        Cs.Write(0, Patch)
                        Overlay(Expected, Patch, 0)

                        AssertTrue(
                            Cs.FaultRecoveryCount >= 1,
                            $"The stream should have auto-recovered from the failed defragment. DefragType={DefragType}")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            $"Auto-recovery after a failed defragment lost or changed logical data. DefragType={DefragType}")

                        Cs.Validate().ThrowIfErrors()

                        ' A follow-up defragment on the recovered stream still succeeds.
                        Cs.Defragment(DefragType)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            $"Defragment after auto-recovery changed logical data. DefragType={DefragType}")

                    End Using

                    Backing.Position = 0

                    Using Reopened = ChunkedStream.Open(Backing, Options)
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                Next

            End Sub

            ''' <summary>
            ''' A cached physical-data end that has drifted above the real end of the live
            ''' data (observed after heavy churn - the cache only ratchets down when the
            ''' record at the very end is the one that moves) must not make a Move or
            ''' Sequence pass fail its "data end beyond the backing stream" guard. Every
            ''' pass recomputes the end from the live records before trimming.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentIgnoresADriftedPhysicalDataEndCache()

                For Each DefragType In {ChunkedStream.DefragTypes.Move, ChunkedStream.DefragTypes.Sequence}

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 1024
                        }

                        Dim Expected As Byte()

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Expected = CreateFragmentedStream(Cs, 18000 + CInt(DefragType))

                            ' Compact to a converged layout, then push the cached end past
                            ' the backing stream - the exact state a later pass tripped on.
                            Cs.Defragment(DefragType)
                            Cs.Debug_CorruptCachedPhysicalDataEnd(Ms.Length + 65536)

                            Cs.Defragment(DefragType)

                            AssertTrue(
                                Cs.GetStructure().LiveDataEndOffset <= Ms.Length,
                                $"The live-data end should sit within the backing stream. DefragType={DefragType}")

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Defragment changed the logical data. DefragType={DefragType}")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        Ms.Position = 0

                        Using Reopened = ChunkedStream.Open(Ms, Options)

                            Reopened.Validate().ThrowIfErrors()

                            AssertBytesEqual(
                                Expected,
                                Reopened.ToArray(),
                                $"Reopened stream lost data. DefragType={DefragType}")

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Repro for "Defrag data end is beyond the backing stream length." reported from
            ''' the ChunkedStream sample (a large LZ4 + encrypted BestFit stream, reopened and
            ''' Defragment(Move)d).
            '''
            ''' When a Move pass's compacted metadata does not fit in the gap below the
            ''' superseded metadata, the overflow pages fall back to appended offsets and the
            ''' metadata root can be recycled into a freed hole well below the end of the file.
            ''' TrimAndCommitDefragMetadata used to derive the post-trim length from the root
            ''' alone, so it truncated the appended pages (and any live record above the root),
            ''' which the next pass then reported as a data end past the backing stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MoveDefragKeepsMetadataThatSpilledPastTheCompactionGap()

                Dim Key = MakeKey(90210)

                Dim MakeOptions =
                    Function() As ChunkedStream.ChunkedStreamOptions
                        Return New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 8192,
                            .CompressionRatioThreshold = 1,
                            .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                            .NewIndexPageWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                            .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(Key)
                        }
                    End Function

                Const TotalLength As Integer = 6 * 1024 * 1024
                Const ChunkSize As Integer = 8192
                Const WriteBlock As Integer = 64 * 1024

                ' Clustered / bimodal compressibility, like a real executable: alternating runs
                ' of near-incompressible and highly compressible chunks. This is what leaves the
                ' awkward mid-sized freed holes a Move pass then recycles the metadata root into.
                Dim Expected = GenerateClusteredCompressibleData(TotalLength, ChunkSize, 4242)

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms, MakeOptions())

                        Dim Offset = 0
                        While Offset < Expected.Length
                            Dim BlockLength = Math.Min(WriteBlock, Expected.Length - Offset)
                            Cs.Write(Offset, Slice(Expected, Offset, BlockLength))
                            Offset += BlockLength
                        End While

                    End Using

                    Ms.Position = 0

                    Using Cs = ChunkedStream.Open(Ms, MakeOptions())

                        AssertBytesEqual(Expected, Cs.ToArray(), "Reopened stream did not round-trip before defrag.")

                        Cs.Defragment(ChunkedStream.DefragTypes.Move)

                        AssertBytesEqual(Expected, Cs.ToArray(), "Defragment(Move) changed the logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Ms.Position = 0

                    Using Reopened = ChunkedStream.Open(Ms, MakeOptions())
                        Reopened.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Reopened stream lost data after defrag.")
                    End Using

                End Using

            End Sub

            Private Shared Function GenerateClusteredCompressibleData(Length As Integer,
                                                                     ChunkSize As Integer,
                                                                     Seed As Integer) As Byte()

                Dim Result(Length - 1) As Byte
                Dim Randomizer As New Random(Seed)
                Dim Position = 0
                Dim RunRemaining = 0
                Dim RunCompressible = False

                While Position < Length

                    Dim ThisLength = Math.Min(ChunkSize, Length - Position)

                    If RunRemaining <= 0 Then
                        RunCompressible = Not RunCompressible
                        RunRemaining = Randomizer.Next(5, 45)
                    End If

                    RunRemaining -= 1

                    Dim CompressibleRatio =
                        If(RunCompressible,
                           0.82R + Randomizer.NextDouble() * 0.15R,
                           Randomizer.NextDouble() * 0.12R)

                    Dim CompressibleLength = CInt(ThisLength * CompressibleRatio)
                    Dim Period = 3 + Randomizer.Next(0, 40)

                    For Index = 0 To CompressibleLength - 1
                        Result(Position + Index) = CByte((Index Mod Period) + 65)
                    Next

                    Dim Tail(ThisLength - CompressibleLength - 1) As Byte
                    Randomizer.NextBytes(Tail)
                    Buffer.BlockCopy(Tail, 0, Result, Position + CompressibleLength, Tail.Length)

                    Position += ThisLength

                End While

                Return Result

            End Function

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