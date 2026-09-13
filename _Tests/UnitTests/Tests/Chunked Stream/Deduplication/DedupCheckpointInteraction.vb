Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Checkpoint rollback (unlike DeferPublish rollback, which reloads the whole image via
        ''' Open and so adopts every dedup field wholesale - see dedup-stale-key-crash memory,
        ''' fourth incident) restores extents, physical records and free space from an in-memory
        ''' snapshot but never touches the dedup hash table at all. A dedup registration made
        ''' inside a checkpoint that later rolls back is therefore never individually undone - it
        ''' becomes a stale entry pointing at a physical record id that no longer exists (and,
        ''' since record ids only ever move forward, never will again). Verifies this stays safe
        ''' rather than becoming a second correctness bug: TryDeduplicateWriteAsync always
        ''' re-verifies a hit by decrypting and byte-comparing before trusting it, so a stale
        ''' post-rollback entry can only ever resolve as a miss, never a false match.
        ''' </summary>
        Public NotInheritable Class DedupCheckpointInteraction

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RollingBackAWrittenCheckpointLeavesDedupSafe()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Dim ContentA = GenerateRandomData(64, 401)
                        Cs.Write(0, ContentA)

                        Dim ContentB = GenerateRandomData(64, 402)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            ' New content, registered against a fresh physical record id that
                            ' only exists inside this checkpoint's window.
                            Cs.Write(1000, ContentB)

                            Dim BHash = Cs.Debug_ComputeDedupHash(ContentB)
                            Dim BRecordId As Long

                            AssertTrue(Cs.Debug_DedupTryGetValue(BHash, BRecordId), "Sanity check: B's dedup entry should exist while the checkpoint is open.")
                            AssertTrue(Cs.Debug_DedupIndexCount() >= 2, "Sanity check: both A and B should be dedup-registered while the checkpoint is open.")

                            Checkpoint.Rollback()

                        End Using

                        ' B's write never happened as far as the stream's logical content is
                        ' concerned - the checkpoint baseline (A only) is fully restored.
                        AssertEqual(CLng(ContentA.Length), Cs.Length, "Rollback should have discarded B's write entirely, including the sparse fill up to offset 1000.")

                        Dim ReadBackA(ContentA.Length - 1) As Byte
                        Cs.Read(0, ReadBackA)
                        AssertBytesEqual(ContentA, ReadBackA, "A, written before the checkpoint, must survive the rollback untouched.")

                        ' Write B for real, twice, after the rollback. If the stale pre-rollback
                        ' dedup entry were ever trusted without verification, this could return
                        ' wrong data (or throw) instead of quietly missing and writing fresh.
                        Cs.Write(2000, ContentB)
                        Cs.Write(3000, ContentB)

                        Dim ReadBackB1(ContentB.Length - 1) As Byte
                        Dim ReadBackB2(ContentB.Length - 1) As Byte
                        Cs.Read(2000, ReadBackB1)
                        Cs.Read(3000, ReadBackB2)

                        AssertBytesEqual(ContentB, ReadBackB1, "B, rewritten after the rollback, must read back correctly - not corrupted by the stale pre-rollback dedup entry.")
                        AssertBytesEqual(ContentB, ReadBackB2, "The second post-rollback write of B must also read back correctly.")

                        ' Ordinary dedup must keep working normally afterward: two genuinely
                        ' identical post-rollback writes should still share one physical record.
                        AssertEqual(Cs.Debug_GetPhysicalRecordIdAt(2000), Cs.Debug_GetPhysicalRecordIdAt(3000), "The two identical post-rollback writes of B should dedup-match each other.")

                        Cs.Validate().ThrowIfErrors()

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
