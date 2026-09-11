Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies DedupRebuild: Soft:=True drops entries pointing at reclaimed records while
        ''' keeping the rest (and still catches up anything never covered), Soft:=False rotates
        ''' the dedup key and re-indexes every live record under it, and either variant's results
        ''' persist across reopen.
        ''' </summary>
        Public NotInheritable Class DedupRebuild

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SoftRebuildDropsDeadEntriesAndKeepsLiveOnes()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Dim DataA = GenerateRandomData(256, 1)
                        Dim DataB = GenerateRandomData(256, 2)
                        Dim DataC = GenerateRandomData(256, 3)

                        Cs.Write(0, DataA)
                        Cs.Write(1000, DataA)

                        Cs.Write(2000, DataB)

                        AssertEqual(2, Cs.Debug_DedupIndexCount(), "Two distinct content hashes should be indexed before the rebuild.")

                        ' Overwriting DataB's range with different content detaches its original
                        ' physical record, leaving DataB's index entry pointing at nothing live -
                        ' DataC's own write is itself live-indexed too, so there are 3 entries
                        ' (DataA, dead DataB, DataC) going into the rebuild.
                        Cs.Write(2000, DataC)

                        AssertEqual(3, Cs.Debug_DedupIndexCount(), "Sanity check: DataC's write should have added its own entry alongside the now-dead DataB one.")

                        Dim Result = Cs.DedupRebuild(Soft:=True)

                        AssertEqual(2, Cs.Debug_DedupIndexCount(), "The soft rebuild should drop only the entry for the reclaimed record, keeping DataA and DataC.")

                        Cs.Write(3000, DataA)

                        AssertEqual(Cs.Debug_GetPhysicalRecordIdAt(0), Cs.Debug_GetPhysicalRecordIdAt(3000),
                                   "A write matching the surviving entry should still dedupe against it after the rebuild.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub HardRebuildRegeneratesKeyAndReindexesEverything()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Dim Data = GenerateRandomData(256, 4)

                        Cs.Write(0, Data)

                        Dim KeyBefore = CType(Cs.Debug_GetDedupKey().Clone(), Byte())
                        Dim HashBefore = Cs.Debug_ComputeDedupHash(Data)

                        Cs.DedupRebuild(Soft:=False)

                        Dim KeyAfter = Cs.Debug_GetDedupKey()

                        AssertFalse(KeyBefore.SequenceEqual(KeyAfter), "A hard rebuild should generate a new dedup key.")

                        Dim HashAfter = Cs.Debug_ComputeDedupHash(Data)

                        AssertFalse(HashBefore.SequenceEqual(HashAfter), "The same plaintext should hash differently under the new key.")

                        ' The existing record should have been re-indexed under the new key - a
                        ' fresh write of identical content should dedupe against it.
                        Cs.Write(1000, Data)

                        AssertEqual(Cs.Debug_GetPhysicalRecordIdAt(0), Cs.Debug_GetPhysicalRecordIdAt(1000),
                                   "A live write should dedupe against the record the hard rebuild re-indexed under the new key.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SoftRebuildResultSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Data = GenerateRandomData(256, 5)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        Cs.DedupRebuild(Soft:=True)

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "The dedup state established by the rebuild should persist across reopen.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Reopened.Read(0, ReadBackA)
                        Reopened.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "The first range should read back correctly after reopen.")
                        AssertBytesEqual(Data, ReadBackB, "The deduplicated range should read back correctly after reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RebuildOnAStreamThatNeverUsedDeduplicationIsHarmless()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(256, 6))

                        Dim Result = Cs.DedupRebuild()

                        AssertEqual(1, Cs.Debug_DedupIndexCount(), "The rebuild should index the one existing live record even though deduplication was never turned on for live writes.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
