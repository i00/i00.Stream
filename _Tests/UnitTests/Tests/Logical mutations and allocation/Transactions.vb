Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class Transactions

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Basic checkpoint behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that disposing a checkpoint rolls back changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointDisposeRollsBackChanges()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                1001)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(
                                0,
                                GenerateRandomData(
                                    Cs.options.ChunkSize * 2,
                                    1002))

                        End Using

                        AssertBytesEqual(
                            Original,
                            Cs.ToArray(),
                            "Checkpoint dispose did not roll back changes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that committing a checkpoint preserves changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitPreservesChanges()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Updated =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                1101)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, Updated)

                            Checkpoint.Commit()

                        End Using

                        AssertBytesEqual(
                            Updated,
                            Cs.ToArray(),
                            "Checkpoint commit did not preserve changes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Rollback restores the current checkpoint baseline.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackRestoresBaseline()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                1201)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(
                                0,
                                GenerateRandomData(
                                    Cs.options.ChunkSize,
                                    1202))

                            Checkpoint.Rollback()

                            AssertBytesEqual(
                                Original,
                                Cs.ToArray(),
                                "Rollback did not restore checkpoint baseline.")

                        End Using

                        AssertBytesEqual(
                            Original,
                            Cs.ToArray(),
                            "Dispose after rollback changed baseline.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Commit baseline updates
            ' ================================================================================

            ''' <summary>
            ''' Verifies that Commit updates the checkpoint baseline.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CommitUpdatesCheckpointBaseline()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim FirstVersion =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                2001)

                        Dim SecondVersion =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                2002)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, FirstVersion)

                            Checkpoint.Commit()

                            Cs.Write(0, SecondVersion)

                            Checkpoint.Rollback()

                            AssertBytesEqual(
                                FirstVersion,
                                Cs.ToArray(),
                                "Rollback did not restore committed checkpoint baseline.")

                        End Using

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Nested checkpoints
            ' ================================================================================

            ''' <summary>
            ''' Verifies that nested checkpoint rollback restores the inner baseline.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NestedCheckpointRollbackRestoresInnerBaseline()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim OuterData =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3001)

                        Dim InnerData =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3002)

                        Using Outer = Cs.CreateCheckpoint()

                            Cs.Write(0, OuterData)

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, InnerData)

                                Inner.Rollback()

                            End Using

                            AssertBytesEqual(
                                OuterData,
                                Cs.ToArray(),
                                "Inner rollback did not restore inner baseline.")

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that inner checkpoint commit remains subject to outer rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OuterRollbackUndoesCommittedInnerCheckpoint()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3101)

                        Dim Modified =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3102)

                        Cs.Write(0, Original)

                        Using Outer = Cs.CreateCheckpoint()

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, Modified)

                                Inner.Commit()

                            End Using

                        End Using

                        AssertBytesEqual(
                            Original,
                            Cs.ToArray(),
                            "Outer rollback did not undo committed inner checkpoint.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that nested commits preserve the final state.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NestedCheckpointCommitsPreserveFinalState()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim FinalState =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3201)

                        Using Outer = Cs.CreateCheckpoint()

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, FinalState)

                                Inner.Commit()

                            End Using

                            Outer.Commit()

                        End Using

                        AssertBytesEqual(
                            FinalState,
                            Cs.ToArray(),
                            "Nested commits did not preserve final state.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Length operations
            ' ================================================================================

            ''' <summary>
            ''' Verifies that SetLength participates in transaction rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackRestoresLength()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(
                            Cs.options.ChunkSize * 4)

                        Dim OriginalLength =
                            Cs.Length

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.SetLength(
                                Cs.options.ChunkSize)

                        End Using

                        AssertEqual(
                            OriginalLength,
                            Cs.Length,
                            "Checkpoint rollback did not restore length.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that committed SetLength changes survive checkpoint disposal.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitPreservesLengthChange()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.SetLength(
                                Cs.options.ChunkSize * 8)

                            Checkpoint.Commit()

                        End Using

                        AssertEqual(
                            CLng(Cs.options.ChunkSize * 8),
                            Cs.Length,
                            "Committed length change was not preserved.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Storage reclamation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that committing a checkpoint that overwrote data physically reclaims
            ''' the records the overwrite left unreferenced, instead of letting them
            ''' accumulate in - and be persisted with - the physical-record table. Regression
            ''' test for the deferred-reclaim list never being processed on commit (the
            ''' committing checkpoint is still on the stack) and then being discarded on
            ''' teardown, which is how a stream can end up almost entirely occupied by
            ''' physical records that no extent references.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitReclaimsFreedPhysicalRecords()

                Const ChunkSize As Integer = 1024
                Const ChunkCount As Integer = 16

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(ChunkSize * ChunkCount, 1601))

                        Dim BaselineRecordCount = Cs.Debug_GetPhysicalRecordCount()

                        Dim Latest As Byte() = Nothing

                        For Round = 1 To 4

                            Latest = GenerateRandomData(ChunkSize * ChunkCount, 1601 + Round)

                            Using Checkpoint = Cs.CreateCheckpoint()
                                Cs.Write(0, Latest)
                                Checkpoint.Commit()
                            End Using

                            AssertBytesEqual(
                                Latest,
                                Cs.ToArray(),
                                $"Checkpoint commit lost data on round {Round}.")

                            AssertEqual(
                                0,
                                Cs.Debug_GetUnreferencedPhysicalRecordCount(),
                                $"Checkpoint commit on round {Round} left physical records that no extent references.")

                            AssertTrue(
                                Cs.Debug_GetPhysicalRecordCount() <= BaselineRecordCount * 2,
                                $"Physical-record table grew unbounded across checkpoint rounds (round {Round}: {Cs.Debug_GetPhysicalRecordCount()} entries).")

                        Next

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        Reopened.Validate()

                        AssertEqual(
                            0,
                            Reopened.Debug_GetUnreferencedPhysicalRecordCount(),
                            "Reopened stream carries physical records that no extent references.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that records freed inside nested checkpoints that each commit into
            ''' their parent are all reclaimed once the outermost checkpoint commits, and
            ''' not before (an outer rollback must still be able to bring them back). Mirrors
            ''' the recursive delete pattern used by <see cref="EmbeddedFileSystem"/>.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NestedCheckpointCommitsReclaimFreedPhysicalRecords()

                Const ChunkSize As Integer = 1024
                Const ChunkCount As Integer = 12

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateRandomData(ChunkSize * ChunkCount, 1701))

                        Using Outer = Cs.CreateCheckpoint()

                            For Level = 1 To 4

                                Using Inner = Cs.CreateCheckpoint()
                                    Cs.Write(0, GenerateRandomData(ChunkSize * ChunkCount, 1701 + Level))
                                    Inner.Commit()
                                End Using

                                ' While the outer checkpoint is still open the freed records
                                ' must be retained - the outer scope can still roll back.
                                AssertTrue(
                                    Cs.Debug_GetUnreferencedPhysicalRecordCount() > 0,
                                    $"Inner checkpoint on level {Level} reclaimed records the outer checkpoint might still need.")

                            Next

                            Outer.Commit()

                        End Using

                        AssertEqual(
                            0,
                            Cs.Debug_GetUnreferencedPhysicalRecordCount(),
                            "Outermost checkpoint commit did not reclaim records freed by the nested checkpoints.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rolling back the outermost checkpoint restores records that
            ''' nested committed checkpoints had freed, and does not leave them queued for
            ''' reclamation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OuterRollbackRestoresRecordsFreedByCommittedInnerCheckpoint()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Original = GenerateRandomData(Options.ChunkSize * 8, 1801)
                        Cs.Write(0, Original)

                        Dim RecordCountBefore = Cs.Debug_GetPhysicalRecordCount()

                        Using Outer = Cs.CreateCheckpoint()

                            Using Inner = Cs.CreateCheckpoint()
                                Cs.Write(0, GenerateRandomData(Options.ChunkSize * 8, 1802))
                                Inner.Commit()
                            End Using

                            Outer.Rollback()

                            AssertBytesEqual(
                                Original,
                                Cs.ToArray(),
                                "Outer rollback did not restore data the inner checkpoint overwrote.")

                            AssertEqual(
                                0,
                                Cs.Debug_GetUnreferencedPhysicalRecordCount(),
                                "Outer rollback left the restored records queued for reclamation.")

                        End Using

                        AssertEqual(
                            RecordCountBefore,
                            Cs.Debug_GetPhysicalRecordCount(),
                            "Checkpoint teardown after an outer rollback changed the physical-record count.")

                        AssertBytesEqual(
                            Original,
                            Cs.ToArray(),
                            "Data changed after the rolled-back checkpoint was disposed.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a chunk record written inside a checkpoint reuses a matured
            ''' free hole instead of always appending, so a large committed write into
            ''' previously freed space does not grow the backing store.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointChunkWriteReusesFreeHole()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        ' Twelve chunks, then free the middle six, leaving a large hole.
                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 12, 1901))
                        Cs.Remove(Cs.Options.ChunkSize * 3L, Cs.Options.ChunkSize * 6L)

                        ' Rotate the header enough times to mature the freed span into the
                        ' allocatable free-space map.
                        For MatureIndex = 1 To 4
                            Cs.Write(Cs.Length, GenerateRandomData(32, 1910 + MatureIndex))
                        Next

                        Dim Baseline = Cs.ToArray()
                        Dim PhysicalBefore = Ms.Length
                        Dim NewData = GenerateRandomData(Cs.Options.ChunkSize * 6, 1950)

                        Using Checkpoint = Cs.CreateCheckpoint()
                            Cs.Write(Cs.Length, NewData)
                            Checkpoint.Commit()
                        End Using

                        AssertBytesEqual(
                            Baseline.Concat(NewData).ToArray(),
                            Cs.ToArray(),
                            "In-checkpoint hole-filling write corrupted logical data.")

                        AssertTrue(
                            Ms.Length < PhysicalBefore + (NewData.Length \ 4),
                            $"In-checkpoint write did not reuse the free hole: backing store grew from {PhysicalBefore:N0} to {Ms.Length:N0} for a {NewData.Length:N0}-byte write.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rolling back a checkpoint whose chunk writes filled a free
            ''' hole restores the pre-checkpoint data and returns the hole to the free map.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointHoleFillingWriteRolledBackRestoresState()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 10, 2001))
                        Cs.Remove(Cs.Options.ChunkSize * 3L, Cs.Options.ChunkSize * 4L)

                        For MatureIndex = 1 To 4
                            Cs.Write(Cs.Length, GenerateRandomData(32, 2010 + MatureIndex))
                        Next

                        Dim Baseline = Cs.ToArray()
                        Dim NewData = GenerateRandomData(Cs.Options.ChunkSize * 4, 2050)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(Cs.Length, NewData)
                            Checkpoint.Rollback()

                            AssertBytesEqual(
                                Baseline,
                                Cs.ToArray(),
                                "Rollback after an in-checkpoint hole-filling write did not restore data.")

                        End Using

                        AssertBytesEqual(
                            Baseline,
                            Cs.ToArray(),
                            "Data changed after disposing the rolled-back checkpoint.")

                        ' The hole is free again: a normal write reuses it and stays intact.
                        Dim PhysicalBefore = Ms.Length
                        Cs.Write(Cs.Length, NewData)

                        AssertBytesEqual(
                            Baseline.Concat(NewData).ToArray(),
                            Cs.ToArray(),
                            "Write into the hole freed by the rollback corrupted data.")

                        AssertTrue(
                            Ms.Length < PhysicalBefore + (NewData.Length \ 4),
                            "Post-rollback write did not reuse the hole the rollback freed.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that abandoning (crashing) a checkpoint whose chunk writes filled a
            ''' free hole recovers to the pre-checkpoint state on the next open, leaving the
            ''' hole's overwritten bytes as harmless free space.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointHoleFillingWriteAbandonedRecoversToBaseline()

                Using Ms As New MemoryStream()

                    Dim Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 10, 2101))
                    Cs.Remove(Cs.Options.ChunkSize * 3L, Cs.Options.ChunkSize * 4L)

                    For MatureIndex = 1 To 4
                        Cs.Write(Cs.Length, GenerateRandomData(32, 2110 + MatureIndex))
                    Next

                    Dim Baseline = Cs.ToArray()

                    Dim AbandonedCheckpoint = Cs.CreateCheckpoint()
                    Cs.Write(Cs.Length, GenerateRandomData(Cs.Options.ChunkSize * 4, 2150))

                    GC.KeepAlive(AbandonedCheckpoint)
                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.CheckpointActive,
                            Reopened.RecoveryStateAtOpen,
                            "Expected checkpoint recovery state at open.")

                        AssertBytesEqual(
                            Baseline,
                            Reopened.ToArray(),
                            "Checkpoint recovery did not roll back the in-checkpoint hole-filling write.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clone / insert / remove transactional behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that clone operations participate in rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRollbackRestoresOriginalLayout()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                4001)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Clone(
                                0,
                                Data.Length,
                                Data.Length)

                        End Using

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Clone rollback did not restore original layout.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that insert operations participate in rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertRollbackRestoresOriginalLayout()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                2000,
                                4101)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Insert(
                                1000,
                                GeneratePatternData(
                                    250,
                                    4102))

                        End Using

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Insert rollback did not restore original layout.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that remove operations participate in rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveRollbackRestoresOriginalLayout()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                3000,
                                4201)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Remove(
                                1000,
                                500)

                        End Using

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Remove rollback did not restore original layout.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace