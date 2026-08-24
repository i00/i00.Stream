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