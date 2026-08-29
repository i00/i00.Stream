Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class CorrectnessAndSurvival

        Public NotInheritable Class DeferredPublish

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Batching behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that operations performed while a DeferPublish scope is open do not
            ''' publish metadata, and that Publish folds them into a single publish.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishFoldsWritesIntoOneMetadataPublish()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 100))

                        Dim SequenceBefore = Cs.GetStructure().HeaderSequence

                        Using Scope = Cs.DeferPublish()

                            For ChunkIndex = 1 To 20
                                Cs.Write(CLng(ChunkIndex) * Cs.Options.ChunkSize,
                                         GenerateRandomData(Cs.Options.ChunkSize, 100 + ChunkIndex))
                            Next

                            AssertEqual(
                                SequenceBefore,
                                Cs.GetStructure().HeaderSequence,
                                "Metadata was published while a DeferPublish scope was open.")

                            Scope.Publish()

                            AssertTrue(
                                Cs.GetStructure().HeaderSequence > SequenceBefore,
                                "Publish did not publish the batched metadata.")

                        End Using

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that data written inside a published DeferPublish scope survives
            ''' closing the scope, closing the stream and reopening it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishedDataSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 12, 201)

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Scope = Cs.DeferPublish()
                            For Offset = 0 To Expected.Length - 1 Step 4096
                                Dim Count = Math.Min(4096, Expected.Length - Offset)
                                Cs.Write(Offset, Expected, Offset, Count)
                            Next
                            Scope.Publish()
                        End Using
                        AssertBytesEqual(Expected, Cs.ToArray(), "Deferred-publish data was lost before reopen.")
                        Cs.Validate()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Deferred-publish data did not survive reopen.")
                        AssertEqual(ChunkedStream.RecoveryStates.None, Reopened.RecoveryStateAtOpen,
                                    "A cleanly published DeferPublish scope should not trigger recovery.")
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Flush publishes the pending metadata as a durable point without
            ''' ending the suspension, and that work after the Flush is still published by a
            ''' later Publish.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishFlushIsADurablePointMidScope()

                Using Ms As New MemoryStream()

                    Dim First = GenerateRandomData(ChunkedStream.DefaultChunkSize, 301)
                    Dim Second = GenerateRandomData(ChunkedStream.DefaultChunkSize, 302)

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Scope = Cs.DeferPublish()

                            Cs.Write(0, First)
                            Dim BeforeFlush = Cs.GetStructure().HeaderSequence

                            Cs.Flush()

                            Dim AfterFlush = Cs.GetStructure().HeaderSequence
                            AssertTrue(AfterFlush > BeforeFlush, "Flush did not publish the pending metadata.")

                            Cs.Write(CLng(First.Length), Second)
                            AssertEqual(AfterFlush, Cs.GetStructure().HeaderSequence,
                                        "A write after Flush published even though the scope was still open.")

                            Scope.Publish()

                        End Using

                        Dim Combined(First.Length + Second.Length - 1) As Byte
                        Buffer.BlockCopy(First, 0, Combined, 0, First.Length)
                        Buffer.BlockCopy(Second, 0, Combined, First.Length, Second.Length)
                        AssertBytesEqual(Combined, Cs.ToArray(), "Work after a mid-scope Flush was lost.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that scopes are reference-counted: a nested Publish is ignored, and
            ''' the batch is published only when the outermost scope publishes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishNestedScopesPublishAtTheOutermost()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim SequenceBefore = Cs.GetStructure().HeaderSequence

                        Dim Outer = Cs.DeferPublish()
                        Dim Inner = Cs.DeferPublish()

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 401))

                        Inner.Publish()
                        AssertEqual(SequenceBefore, Cs.GetStructure().HeaderSequence,
                                    "A nested Publish published while the outer scope was still open.")

                        Inner.Dispose()
                        AssertEqual(SequenceBefore, Cs.GetStructure().HeaderSequence,
                                    "Disposing the inner scope published before the outer scope closed.")

                        Outer.Publish()
                        AssertTrue(Cs.GetStructure().HeaderSequence > SequenceBefore,
                                   "The outermost Publish did not publish the batched metadata.")

                        Outer.Dispose()

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Rollback behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that disposing a scope without Publish rolls the metadata back to
            ''' where the scope opened and leaves the stream usable - not faulted.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishWithoutPublishRollsBackAndLeavesStreamUsable()

                Using Ms As New MemoryStream()

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 501)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Baseline)

                        Using Scope = Cs.DeferPublish()
                            Cs.Write(CLng(Baseline.Length), GenerateRandomData(ChunkedStream.DefaultChunkSize * 6, 502))
                            ' no Publish - the scope is abandoned
                        End Using

                        AssertBytesEqual(Baseline, Cs.ToArray(),
                                         "An abandoned DeferPublish scope did not roll its writes back.")

                        Dim AfterRollback = GenerateRandomData(ChunkedStream.DefaultChunkSize, 503)
                        Cs.Write(0, AfterRollback)
                        AssertBytesEqual(AfterRollback, Cs.ToArray(0, AfterRollback.Length),
                                         "The stream was not usable after a DeferPublish rollback.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertEqual(ChunkedStream.RecoveryStates.None, Reopened.RecoveryStateAtOpen,
                                    "A DeferPublish rollback writes no recovery state.")
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that an exception raised inside a DeferPublish scope rolls the whole
            ''' batch back rather than publishing a partial result.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishRollsBackWhenAnOperationThrows()

                Using Ms As New MemoryStream()

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 601)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Baseline)

                        Try
                            Using Scope = Cs.DeferPublish()
                                Cs.Write(CLng(Baseline.Length), GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 602))
                                If Cs.Length > 0 Then Throw New InvalidOperationException("Simulated mid-batch failure.")
                                Scope.Publish()
                            End Using
                        Catch Ex As InvalidOperationException
                        End Try

                        AssertBytesEqual(Baseline, Cs.ToArray(),
                                         "A failed DeferPublish batch was not rolled back.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a nested batch published by an inner Publish is still rolled
            ''' back when the outermost scope is abandoned.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishOutermostAbandonRollsBackNestedPublishedWork()

                Using Ms As New MemoryStream()

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 701)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Baseline)

                        Using Outer = Cs.DeferPublish()

                            Using Inner = Cs.DeferPublish()
                                Cs.Write(CLng(Baseline.Length), GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 702))
                                Inner.Publish()
                            End Using

                            ' Outer is abandoned - no Publish.
                        End Using

                        AssertBytesEqual(Baseline, Cs.ToArray(),
                                         "An abandoned outermost scope did not roll back nested published work.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Crash behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a crash before a DeferPublish scope publishes reopens the
            ''' stream at the last published generation, and that the window's orphaned
            ''' physical records are reclaimable by Defragment.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishCrashReopensAtLastPublishedGeneration()

                Using Ms As New MemoryStream()

                    Dim Published = GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 801)

                    Dim Cs = ChunkedStream.Open(Ms)
                    Cs.Write(0, Published)

                    Dim AbandonedScope = Cs.DeferPublish()
                    Cs.Write(Published.Length, GenerateRandomData(ChunkedStream.DefaultChunkSize * 8, 802))

                    GC.KeepAlive(AbandonedScope)
                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(ChunkedStream.RecoveryStates.None, Reopened.RecoveryStateAtOpen,
                                    "A DeferPublish window writes no recovery state, so no recovery should run.")

                        AssertBytesEqual(Published, Reopened.ToArray(),
                                         "Reopen after an abandoned DeferPublish window did not return the last published data.")

                        Reopened.Validate()

                        Dim PhysicalBeforeDefrag = Ms.Length
                        Reopened.Defragment(ChunkedStream.DefragTypes.Move)

                        AssertBytesEqual(Published, Reopened.ToArray(), "Defragment changed the recovered data.")
                        AssertTrue(Ms.Length < PhysicalBeforeDefrag,
                                   "Defragment did not reclaim the orphaned records left by the abandoned window.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that disposing the stream with an unpublished DeferPublish scope
            ''' still open rolls the scope back rather than persisting a partial batch.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishLeakedScopeRollsBackOnStreamDispose()

                Using Ms As New MemoryStream()

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 901)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Baseline)
                        Using Committed = Cs.DeferPublish()
                            Committed.Publish()
                        End Using

                        Dim LeakedScope = Cs.DeferPublish()
                        Cs.Write(CLng(Baseline.Length), GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 902))
                        GC.KeepAlive(LeakedScope)
                        ' scope intentionally not disposed or published
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Baseline, Reopened.ToArray(),
                                         "Stream dispose persisted a leaked scope's unpublished batch.")
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Interaction with checkpoints
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a checkpoint cannot be created while a DeferPublish scope is
            ''' open - the two do not nest that way.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointCannotBeCreatedInsideDeferPublishScope()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 1101))

                        Using Scope = Cs.DeferPublish()

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Cs.CreateCheckpoint(),
                                "CreateCheckpoint should be rejected while a DeferPublish scope is open.")

                            Scope.Publish()

                        End Using

                        ' The two nest fine in the other order.
                        Using Checkpoint = Cs.CreateCheckpoint()
                            Using Scope = Cs.DeferPublish()
                                Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 1102))
                                Scope.Publish()
                            End Using
                            Checkpoint.Commit()
                        End Using

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a DeferPublish scope opened inside a checkpoint defers entirely
            ''' to the checkpoint: its work is rolled back with the checkpoint.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishInsideCheckpointRollsBackWithTheCheckpoint()

                Using Ms As New MemoryStream()

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 1201)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Baseline)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Using Scope = Cs.DeferPublish()
                                Cs.Write(CLng(Baseline.Length), GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 1202))
                                Scope.Publish()
                            End Using

                            Checkpoint.Rollback()

                        End Using

                        AssertBytesEqual(Baseline, Cs.ToArray(),
                                         "A checkpoint rollback did not undo work published inside a nested DeferPublish scope.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Interaction with bulk operations
            ' ================================================================================

            ''' <summary>
            ''' Verifies that Defragment and ApplyOptions refuse to run while a DeferPublish
            ''' scope is open.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishBlocksDefragmentAndApplyOptions()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 4, 1001))

                        Using Scope = Cs.DeferPublish()

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Cs.Defragment(ChunkedStream.DefragTypes.Move),
                                "Defragment should be rejected while a DeferPublish scope is open.")

                            AssertThrows(Of InvalidOperationException)(
                                Sub()
                                    Cs.Options.ChunkSize = Cs.Options.ChunkSize \ 2
                                    Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.ChunkSize)
                                End Sub,
                                "ApplyOptions should be rejected while a DeferPublish scope is open.")

                            Scope.Publish()

                        End Using

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
