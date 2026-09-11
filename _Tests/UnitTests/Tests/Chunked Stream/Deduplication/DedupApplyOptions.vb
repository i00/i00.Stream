Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies ApplyOptions(Deduplication)'s catch-up scan: merging physical records that
        ''' predate Options.Deduplication being turned on, indexing records that have no existing
        ''' match, leaving distinct content alone, advancing the high-water mark so the scan is
        ''' idempotent, and persisting its results so a later reopen still sees them.
        ''' </summary>
        Public NotInheritable Class DedupApplyOptions

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CatchUpMergesExistingDuplicatesWrittenBeforeDeduplicationWasOn()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GenerateRandomData(256, 1)

                        ' Deduplication is off for both writes, so these get independent records -
                        ' exactly the situation the catch-up scan exists to fix retroactively.
                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: writes with dedup off should not share a record.")

                        Cs.Options.Deduplication = True

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)

                        AssertEqual(1, Result.DeduplicationChanges, "Exactly one record should have been merged into the other.")
                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "The two records should now be merged into one.")

                        Dim RecordIdA = Cs.Debug_GetPhysicalRecordIdAt(0)
                        Dim RecordIdB = Cs.Debug_GetPhysicalRecordIdAt(1000)

                        AssertEqual(RecordIdA, RecordIdB, "Both extents should point at the same physical record after the catch-up scan.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(RecordIdA), "The merged record should have a reference count of 2.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Cs.Read(0, ReadBackA)
                        Cs.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "The first range should still read back correctly after the merge.")
                        AssertBytesEqual(Data, ReadBackB, "The merged range should still read back correctly after the merge.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CatchUpLeavesDistinctContentAlone()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(256, 2))
                        Cs.Write(1000, GenerateRandomData(256, 3))

                        Cs.Options.Deduplication = True

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)

                        AssertEqual(0, Result.DeduplicationChanges, "Distinct content should never be merged.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "Distinct content should keep separate records.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CatchUpIsIdempotent()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GenerateRandomData(256, 4)

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        Cs.Options.Deduplication = True

                        Dim FirstResult = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)
                        AssertEqual(1, FirstResult.DeduplicationChanges, "The first scan should merge the duplicate.")

                        Dim SecondResult = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)
                        AssertEqual(0, SecondResult.DeduplicationChanges, "A second scan should find nothing new to merge.")

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "The record count should be unchanged by the second scan.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CatchUpIndexedRecordIsUsedByLaterLiveWrites()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GenerateRandomData(256, 5)

                        ' Written with dedup off, so it has no index entry of its own yet.
                        Cs.Write(0, Data)

                        Cs.Options.Deduplication = True

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)

                        AssertEqual(0, Result.DeduplicationChanges, "There is nothing to merge the sole record against.")
                        AssertEqual(1, Cs.Debug_DedupIndexCount(), "The catch-up scan should have indexed the sole record as canonical.")

                        ' Now that it's indexed, a live write of identical content should dedupe
                        ' against it directly, without ever needing another catch-up scan.
                        Cs.Write(1000, Data)

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "A live write matching the catch-up-indexed record should reuse it.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CatchUpMergeSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Data = GenerateRandomData(256, 6)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        Cs.Options.Deduplication = True
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Deduplication)

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "The merge performed by ApplyOptions should persist across reopen.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Reopened.Read(0, ReadBackA)
                        Reopened.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "The first range should read back correctly after reopen.")
                        AssertBytesEqual(Data, ReadBackB, "The merged range should read back correctly after reopen.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
