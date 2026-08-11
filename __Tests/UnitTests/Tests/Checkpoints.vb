Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests

    Partial Class StreamChunked
        Public NotInheritable Class Checkpoints

            ''' <summary>
            ''' Verifies that disposing a checkpoint without Commit rolls back data changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointDisposeRollsBack()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original = GeneratePatternData(10000, 1)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, GeneratePatternData(10000, 2))

                        End Using

                        Dim Result = GenerateZeroedData(Original.Length)

                        Cs.Read(0, Result)

                        AssertBytesEqual(
                            Original,
                            Result,
                            "Checkpoint dispose should have rolled back changes.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that committed checkpoint changes are retained.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitPersistsChanges()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original = GeneratePatternData(10000, 1)
                        Dim Updated = GeneratePatternData(10000, 2)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, Updated)

                            Checkpoint.Commit()

                        End Using

                        Dim Result = GenerateZeroedData(Updated.Length)

                        Cs.Read(0, Result)

                        AssertBytesEqual(
                            Updated,
                            Result,
                            "Committed checkpoint changes were lost.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that explicit rollback restores the checkpoint state.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackRestoresState()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original = GeneratePatternData(10000, 1)

                        Cs.Write(0, Original)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, GeneratePatternData(10000, 2))

                            Checkpoint.Rollback()

                        End Using

                        Dim Result = GenerateZeroedData(Original.Length)

                        Cs.Read(0, Result)

                        AssertBytesEqual(
                            Original,
                            Result,
                            "Rollback did not restore original data.")

                    End Using

                End Using

            End Sub


            ''' <summary>
            ''' Verifies that reads see uncommitted checkpoint writes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointReadsSeeUncommittedData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Updated = GeneratePatternData(10000, 2)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, Updated)

                            Dim Result = GenerateZeroedData(Updated.Length)

                            Cs.Read(0, Result)

                            AssertBytesEqual(
                                Updated,
                                Result,
                                "Reads did not see uncommitted checkpoint data.")

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rolling back an inner checkpoint preserves outer checkpoint changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NestedInnerRollbackPreservesOuterChanges()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Outer = Cs.CreateCheckpoint()

                            Dim OuterData = GeneratePatternData(10000, 1)

                            Cs.Write(0, OuterData)

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, GeneratePatternData(10000, 2))

                            End Using

                            Dim Result = GenerateZeroedData(OuterData.Length)

                            Cs.Read(0, Result)

                            AssertBytesEqual(
                                OuterData,
                                Result,
                                "Inner rollback removed outer checkpoint changes.")

                            Outer.Commit()

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that committing an inner checkpoint promotes its changes to the parent checkpoint.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NestedInnerCommitPromotesToParent()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Updated = GeneratePatternData(10000, 3)

                        Using Outer = Cs.CreateCheckpoint()

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, Updated)

                                Inner.Commit()

                            End Using

                            Dim Result = GenerateZeroedData(Updated.Length)

                            Cs.Read(0, Result)

                            AssertBytesEqual(
                                Updated,
                                Result,
                                "Inner commit was not visible inside parent checkpoint.")

                            Outer.Commit()

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rolling back an outer checkpoint rolls back committed inner checkpoints.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OuterRollbackRollsBackCommittedInnerChanges()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original = GeneratePatternData(10000, 1)

                        Cs.Write(0, Original)

                        Using Outer = Cs.CreateCheckpoint()

                            Using Inner = Cs.CreateCheckpoint()

                                Cs.Write(0, GeneratePatternData(10000, 2))

                                Inner.Commit()

                            End Using

                        End Using

                        Dim Result = GenerateZeroedData(Original.Length)

                        Cs.Read(0, Result)

                        AssertBytesEqual(
                            Original,
                            Result,
                            "Outer rollback failed to roll back committed inner checkpoint changes.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that checkpoints must be committed in LIFO order.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointLifoEnforced()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Outer = Cs.CreateCheckpoint()
                            Using Inner = Cs.CreateCheckpoint()
                                AssertThrows(Of InvalidOperationException)(
                                    Sub()
                                        Outer.Commit()
                                    End Sub,
                                    "Checkpoint commit should require LIFO order.")
                            End Using
                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rollback truncates appended checkpoint data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackTruncatesFile()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim InitialPhysicalLength = Ms.Length

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, GenerateRepeatingPattern(500000, 8))

                            AssertTrue(
                                Ms.Length > InitialPhysicalLength,
                                "Checkpoint write should have increased physical size.")

                        End Using

                        AssertEqual(
                            InitialPhysicalLength,
                            Ms.Length,
                            "Rollback did not truncate the physical stream.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that committed checkpoint data survives reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCommitSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Data = GeneratePatternData(100000, 123)

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(0, Data)

                            Checkpoint.Commit()

                        End Using

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Result = GenerateZeroedData(Data.Length)

                        Cs.Read(0, Result)

                        AssertBytesEqual(
                Data,
                Result,
                "Committed checkpoint data did not survive reopen.")

                    End Using

                End Using

            End Sub


        End Class
    End Class

End Namespace
