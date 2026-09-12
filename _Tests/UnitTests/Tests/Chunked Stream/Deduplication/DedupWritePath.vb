Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies that Options.Deduplication actually makes ordinary writes share physical
        ''' records when their plaintext matches - both the serial write path
        ''' (WritePhysicalRecordWithPolicyAsync) and the parallel chunk-build path
        ''' (BuildExtentsInParallelAsync), and that nothing dedupes when the option is off.
        ''' </summary>
        Public NotInheritable Class DedupWritePath

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub IdenticalSmallWritesShareOnePhysicalRecord()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Dim Data = GenerateRandomData(256, 1)

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "Two identical writes should share a single physical record.")

                        Dim RecordIdA = Cs.Debug_GetPhysicalRecordIdAt(0)
                        Dim RecordIdB = Cs.Debug_GetPhysicalRecordIdAt(1000)

                        AssertEqual(RecordIdA, RecordIdB, "Both extents should point at the same physical record.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(RecordIdA), "The shared record should have a reference count of 2.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Cs.Read(0, ReadBackA)
                        Cs.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "Reading the first range should return the original data.")
                        AssertBytesEqual(Data, ReadBackB, "Reading the deduplicated range should return the original data.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DifferentSmallWritesGetSeparateRecords()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Cs.Write(0, GenerateRandomData(256, 2))
                        Cs.Write(1000, GenerateRandomData(256, 3))

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "Different plaintext should not be deduplicated.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeduplicationDisabledDoesNotShareRecords()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        ' Options.Deduplication defaults to False - identical writes should get
                        ' independent records, exactly like before this feature existed.
                        Dim Data = GenerateRandomData(256, 4)

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "Identical writes should not be deduplicated while the option is off.")
                        AssertEqual(0, Cs.Debug_DedupIndexCount(), "No dedup index entries should be created while the option is off.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DedupedRecordsSurviveReopenAndReadCorrectly()

                Using Ms As New MemoryStream()

                    Dim Data = GenerateRandomData(256, 5)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "The shared record should still be shared after reopen.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Reopened.Read(0, ReadBackA)
                        Reopened.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "The first range should still read back correctly after reopen.")
                        AssertBytesEqual(Data, ReadBackB, "The deduplicated range should still read back correctly after reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub IdenticalLargeWritesShareRecordsViaParallelPath()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True
                        Cs.Options.ChunkSize = 4096
                        Cs.Options.ChunkSizeVariance = 0
                        Cs.Options.MaxCryptoParallelism = 4

                        ' 10 chunks' worth - comfortably above ParallelChunkCryptoMinChunks (8) so
                        ' this routes through BuildExtentsInParallelAsync, not the serial path.
                        Dim Data = GenerateRandomData(4096 * 10, 6)

                        Cs.Write(0, Data)

                        Dim RecordCountAfterFirstWrite = Cs.Debug_GetPhysicalRecordCount()

                        AssertEqual(10, RecordCountAfterFirstWrite, "The first write should create one record per chunk.")

                        Cs.Write(CLng(Data.Length) * 2, Data)

                        AssertEqual(RecordCountAfterFirstWrite, Cs.Debug_GetPhysicalRecordCount(),
                                   "Writing identical data again through the parallel path should reuse every existing record rather than creating new ones.")

                        Dim ReadBack(Data.Length - 1) As Byte
                        Cs.Read(CLng(Data.Length) * 2, ReadBack)

                        AssertBytesEqual(Data, ReadBack, "The deduplicated large write should still read back correctly.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub IdenticalChunksWithinTheSameParallelWriteDedupeAgainstEachOther()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True
                        Cs.Options.ChunkSize = 4096
                        Cs.Options.ChunkSizeVariance = 0
                        Cs.Options.MaxCryptoParallelism = 4

                        ' 10 distinct chunks, big enough to clear ParallelChunkCryptoMinChunks (8)
                        ' so this routes through BuildExtentsInParallelAsync - then chunk 5 is
                        ' overwritten with an exact copy of chunk 0's bytes, all within this one
                        ' Write() call, so neither chunk is in the persisted index yet when the
                        ' other is planned.
                        Const ChunkSize As Integer = 4096
                        Dim Data = GenerateRandomData(ChunkSize * 10, 7)
                        Buffer.BlockCopy(Data, 0, Data, ChunkSize * 5, ChunkSize)

                        Cs.Write(0, Data)

                        AssertEqual(9, Cs.Debug_GetPhysicalRecordCount(), "Ten chunks with one intra-batch duplicate should create nine distinct records.")

                        Dim RecordIdA = Cs.Debug_GetPhysicalRecordIdAt(0)
                        Dim RecordIdB = Cs.Debug_GetPhysicalRecordIdAt(CLng(ChunkSize) * 5)

                        AssertEqual(RecordIdA, RecordIdB, "Chunk 0 and chunk 5 should share the same physical record.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(RecordIdA), "The shared record should have a reference count of 2.")

                        Dim ReadBack(Data.Length - 1) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(Data, ReadBack, "The whole write, including the intra-batch duplicate, should read back correctly.")

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' A dedup index entry pointing at a since-reclaimed record ("stale" - the index is
            ''' never cleaned up on reclaim, a known, accepted limitation of its own) must not
            ''' become a *second* entry for the same key the next time that same content is
            ''' written and misses the index. The underlying hash table has no way to ever split
            ''' a bucket apart when several of its entries all share one identical key - real
            ''' content repeatedly reclaimed and rewritten (ordinary overwrite churn at one
            ''' offset, nothing exotic) used to grow the directory without bound and eventually
            ''' trip its own MaxGlobalDepth safety limit, exactly the crash this reproduces.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReclaimingAndRewritingTheSameContentManyTimesDoesNotDuplicateItsDedupEntry()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        ' A small, fixed set of distinct contents, cycled through many times at
                        ' the same offset - every time a pattern comes back around, whichever
                        ' pattern overwrote it in between has already reclaimed its record,
                        ' leaving a stale dedup entry that this write must repair rather than
                        ' duplicate.
                        Const PatternCount As Integer = 8
                        Const Cycles As Integer = 300

                        For Round = 0 To PatternCount * Cycles - 1

                            Dim PatternIndex = Round Mod PatternCount
                            Cs.Write(0, GenerateRandomData(16, PatternIndex))

                        Next

                        AssertEqual(PatternCount, Cs.Debug_DedupIndexCount(), "Each distinct pattern should have exactly one dedup entry, no matter how many times it was reclaimed and rewritten.")

                        Dim LastPatternIndex = (PatternCount * Cycles - 1) Mod PatternCount
                        Dim ReadBack(15) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(GenerateRandomData(16, LastPatternIndex), ReadBack, "The final pattern should read back correctly.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
