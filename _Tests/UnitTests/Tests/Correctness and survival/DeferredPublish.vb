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
            ''' publish metadata, and that a single publish happens when the scope closes.
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

                        End Using

                        AssertTrue(
                            Cs.GetStructure().HeaderSequence > SequenceBefore,
                            "Closing the DeferPublish scope did not publish the batched metadata.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that data written inside a DeferPublish scope survives closing the
            ''' scope, closing the stream and reopening it.
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
                        End Using
                        AssertBytesEqual(Expected, Cs.ToArray(), "Deferred-publish data was lost before reopen.")
                        Cs.Validate()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Deferred-publish data did not survive reopen.")
                        AssertEqual(ChunkedStream.RecoveryStates.None, Reopened.RecoveryStateAtOpen,
                                    "A cleanly closed DeferPublish scope should not trigger recovery.")
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Flush publishes the pending metadata without ending the
            ''' suspension.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishFlushPublishesWithoutEndingScope()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Using Scope = Cs.DeferPublish()

                            Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 301))
                            Dim BeforeFlush = Cs.GetStructure().HeaderSequence

                            Cs.Flush()

                            Dim AfterFlush = Cs.GetStructure().HeaderSequence
                            AssertTrue(AfterFlush > BeforeFlush, "Flush did not publish the pending metadata.")

                            Cs.Write(Cs.Options.ChunkSize, GenerateRandomData(Cs.Options.ChunkSize, 302))
                            AssertEqual(AfterFlush, Cs.GetStructure().HeaderSequence,
                                        "A write after Flush published even though the scope was still open.")

                        End Using

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that scopes are reference-counted: the publish happens only when the
            ''' last scope closes, regardless of the order they are disposed in.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishReferenceCountsAndDisposesInAnyOrder()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim SequenceBefore = Cs.GetStructure().HeaderSequence

                        Dim First = Cs.DeferPublish()
                        Dim Second = Cs.DeferPublish()

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize, 401))

                        First.Dispose()

                        AssertEqual(SequenceBefore, Cs.GetStructure().HeaderSequence,
                                    "Metadata published while an inner DeferPublish scope was still open.")

                        Second.Dispose()

                        AssertTrue(Cs.GetStructure().HeaderSequence > SequenceBefore,
                                   "Metadata was not published when the last DeferPublish scope closed.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Crash behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a crash before a DeferPublish scope closes reopens the stream
            ''' at the last published generation, and that the window's orphaned physical
            ''' records are reclaimable by Defragment.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishCrashReopensAtLastPublishedGeneration()

                Using Ms As New MemoryStream()

                    Dim Published = GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 501)

                    Dim Cs = ChunkedStream.Open(Ms)
                    Cs.Write(0, Published)

                    Dim AbandonedScope = Cs.DeferPublish()
                    Cs.Write(Published.Length, GenerateRandomData(ChunkedStream.DefaultChunkSize * 8, 502))

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
            ''' Verifies that disposing the stream with a DeferPublish scope still open
            ''' publishes the pending metadata rather than losing it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishLeakedScopeStillPublishesOnStreamDispose()

                Using Ms As New MemoryStream()

                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 601)

                    Using Cs = ChunkedStream.Open(Ms)
                        Dim LeakedScope = Cs.DeferPublish()
                        Cs.Write(0, Expected)
                        GC.KeepAlive(LeakedScope)
                        ' scope intentionally not disposed
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Expected, Reopened.ToArray(),
                                         "Stream dispose did not publish the metadata a leaked scope was holding back.")
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

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 4, 701))

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

                        End Using

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
