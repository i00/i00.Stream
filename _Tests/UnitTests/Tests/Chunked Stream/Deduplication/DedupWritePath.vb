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

        End Class

    End Class

End Namespace
