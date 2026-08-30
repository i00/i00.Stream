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

            ' ================================================================================
            ' In-window hole reuse
            ' ================================================================================

            ''' <summary>
            ''' Verifies that when a DeferPublish scope overwrites data - freeing one physical
            ''' record and reusing a pre-existing hole for the replacement - abandoning the
            ''' scope restores the pre-scope data, returns the hole to the free map, and leaves
            ''' no live physical record overlapping a reused span. This is the path where the
            ''' free-space snapshot is load-bearing for correctness.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishHoleFillingWriteAbandonedDoesNotOverlapLiveRecords()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        ' Ten chunks, free the middle four, then mature the freed span into
                        ' the allocatable free-space map with a few header rotations.
                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 10, 8001))
                        Cs.Remove(Cs.Options.ChunkSize * 3L, Cs.Options.ChunkSize * 4L)

                        For MatureIndex = 1 To 4
                            Cs.Write(Cs.Length, GenerateRandomData(32, 8010 + MatureIndex))
                        Next

                        Dim Baseline = Cs.ToArray()

                        Using Scope = Cs.DeferPublish()

                            ' Overwrite an existing chunk (frees its record) and append more
                            ' data - both allocations may draw on the matured hole.
                            Cs.Write(Cs.Options.ChunkSize * 1L,
                                     GenerateRandomData(Cs.Options.ChunkSize, 8050))
                            Cs.Write(Cs.Length,
                                     GenerateRandomData(Cs.Options.ChunkSize * 4, 8060))

                            ' No Scope.Publish() - dispose rolls the batch back.

                        End Using

                        AssertBytesEqual(
                            Baseline,
                            Cs.ToArray(),
                            "Abandoning the DeferPublish scope did not restore the pre-scope data.")

                        ' Validate walks every live record and fails on any overlap, so this
                        ' catches a reused span that was not returned to the free map.
                        Cs.Validate()

                        ' The hole is usable again: a normal write reuses it and stays intact.
                        Dim PhysicalBefore = Ms.Length
                        Dim NewData = GenerateRandomData(Cs.Options.ChunkSize * 4, 8090)
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

            ' ================================================================================
            ' Bulk zeroing batches under one DeferPublish scope (C2-a)
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a non-sparse Clear folds its per-chunk writes into a single
            ''' metadata publish rather than publishing once per chunk.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NonSparseClearFoldsPerChunkWritesIntoOneMetadataPublish()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Const Chunks As Integer = 20
                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * Chunks, 5301))

                        Dim SequenceBefore = Cs.GetStructure().HeaderSequence

                        Cs.Clear(0, CLng(Cs.Options.ChunkSize) * Chunks)

                        Dim Published = Cs.GetStructure().HeaderSequence - SequenceBefore

                        AssertTrue(Published > 0, "A non-sparse Clear published no metadata.")
                        AssertTrue(
                            Published <= 2,
                            $"A non-sparse Clear of {Chunks} chunks published {Published} times; it should batch into one.")

                        AssertTrue(Cs.ToArray().All(Function(b) b = 0), "Non-sparse Clear did not zero the range.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that losing the process while a non-sparse Clear is publishing
            ''' reopens the stream to a consistent known state - either fully cleared or
            ''' not cleared at all, never a partially cleared range. Before C2-a each chunk
            ''' published on its own, so an interruption could leave the first part of the
            ''' range cleared and the rest not.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NonSparseClearInterruptedWhilePublishingReopensToAKnownState()

                Using Ms As New FailingMemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 12, 5401)

                    Dim Cs = ChunkedStream.Open(Ms, Options)
                    Cs.Write(0, Baseline)
                    Cs.Flush()

                    ' From here every backing write fails - the machine "loses power"
                    ' during the clear's single batched metadata publish.
                    Ms.FailFromWriteNumber = Ms.WriteCount + 1

                    Try
                        Cs.Clear(0, CLng(ChunkedStream.DefaultChunkSize) * 12)
                    Catch
                    End Try

                    ' Abandon the stream without disposing - a crash writes nothing more.
                    Cs = Nothing
                    Ms.FailFromWriteNumber = 0

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(ChunkedStream.RecoveryStates.None, Reopened.RecoveryStateAtOpen,
                                    "A non-sparse Clear writes no recovery state.")

                        Dim Content = Reopened.ToArray()

                        AssertTrue(
                            Content.All(Function(b) b = 0) OrElse Content.SequenceEqual(Baseline),
                            "An interrupted non-sparse Clear left a partially cleared range.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a non-sparse Clear performed inside a checkpoint is undone by a
            ''' rollback and kept by a commit - the batching scope defers to the checkpoint.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NonSparseClearInsideACheckpointRollsBackAndCommitsAtomically()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Baseline = GenerateRandomData(Cs.Options.ChunkSize * 6, 5501)
                        Cs.Write(0, Baseline)

                        Using Checkpoint = Cs.CreateCheckpoint()
                            Cs.Clear(0, CLng(Cs.Options.ChunkSize) * 6)
                            AssertTrue(Cs.ToArray().All(Function(b) b = 0),
                                       "Clear inside a checkpoint did not take effect in memory.")
                            Checkpoint.Rollback()
                        End Using

                        AssertBytesEqual(Baseline, Cs.ToArray(),
                                         "A checkpoint rollback did not undo a non-sparse Clear.")
                        Cs.Validate()

                        Using Checkpoint = Cs.CreateCheckpoint()
                            Cs.Clear(0, CLng(Cs.Options.ChunkSize) * 6)
                            Checkpoint.Commit()
                        End Using

                        AssertTrue(Cs.ToArray().All(Function(b) b = 0),
                                   "A committed non-sparse Clear inside a checkpoint was lost.")
                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a non-sparse InsertNullBytes performed inside a caller's
            ''' DeferPublish scope is rolled back when that outer scope is abandoned.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NonSparseInsertNullBytesInsideOuterDeferPublishScopeRollsBackWhenAbandoned()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Baseline = GenerateRandomData(Cs.Options.ChunkSize * 3, 5601)
                        Cs.Write(0, Baseline)

                        Using Committed = Cs.DeferPublish()
                            Committed.Publish()
                        End Using

                        Using Outer = Cs.DeferPublish()
                            Cs.InsertNullBytes(Cs.Options.ChunkSize, CLng(Cs.Options.ChunkSize) * 5)
                            AssertEqual(Baseline.Length + CLng(Cs.Options.ChunkSize) * 5, Cs.Length,
                                        "InsertNullBytes did not extend the logical length in memory.")
                            ' Outer abandoned - no Publish.
                        End Using

                        AssertBytesEqual(
                            Baseline,
                            Cs.ToArray(),
                            "An abandoned outer DeferPublish scope did not roll back a nested non-sparse InsertNullBytes.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' An in-memory stream that throws on a chosen Write call, to exercise the
            ''' backing-store failure paths.
            ''' </summary>
            Private NotInheritable Class FailingMemoryStream
                Inherits MemoryStream

                ''' <summary>1-based index of the Write call that should throw; 0 disables the failure.</summary>
                Public Property FailOnWriteNumber As Integer

                ''' <summary>1-based index from which every Write call should throw; 0 disables it.</summary>
                Public Property FailFromWriteNumber As Integer

                Public Property WriteCount As Integer

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)

                    WriteCount += 1

                    If FailOnWriteNumber > 0 AndAlso WriteCount = FailOnWriteNumber Then
                        Throw New IOException("Simulated backing-store write failure.")
                    End If

                    If FailFromWriteNumber > 0 AndAlso WriteCount >= FailFromWriteNumber Then
                        Throw New IOException("Simulated backing-store write failure.")
                    End If

                    MyBase.Write(Buffer, Offset, Count)

                End Sub

            End Class

        End Class

    End Class

End Namespace
