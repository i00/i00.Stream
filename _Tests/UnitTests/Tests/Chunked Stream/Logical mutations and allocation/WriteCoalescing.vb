Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        ''' <summary>
        ''' Verifies write coalescing: a genuine end-of-stream append absorbs into the physical
        ''' record the stream's last extent already references (instead of always creating a new,
        ''' possibly tiny, chunk), up to Options.ChunkSize, unconditionally - not gated behind an
        ''' option, since every Write() call still fully commits before returning either way.
        ''' </summary>
        Public NotInheritable Class WriteCoalescing

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub TwoSingleByteAppendsShareOnePhysicalRecord()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, New Byte() {1})
                        Cs.Write(1, New Byte() {2})

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "Two contiguous single-byte appends should share one physical record.")
                        AssertEqual(Cs.Debug_GetPhysicalRecordIdAt(0), Cs.Debug_GetPhysicalRecordIdAt(1), "Both bytes should be covered by the same extent's record.")

                        Dim ReadBack(1) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2}, ReadBack, "Both appended bytes should read back correctly.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ManySmallAppendsCoalesceIntoOneRecordThenStartAnother()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.ChunkSize = 8

                        Dim Expected As New List(Of Byte)()

                        For Value = 1 To 8
                            Cs.Write(CLng(Value - 1), New Byte() {CByte(Value)})
                            Expected.Add(CByte(Value))
                        Next

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "Eight single-byte appends filling exactly one ChunkSize should still be one record.")

                        Dim FirstRecordId = Cs.Debug_GetPhysicalRecordIdAt(0)

                        ' The chunk is now exactly full - the next append must start a new record
                        ' rather than growing this one past ChunkSize.
                        Cs.Write(8, New Byte() {9})
                        Expected.Add(9)

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "An append past a full chunk should create a second record, not overflow the first.")
                        AssertFalse(Cs.Debug_GetPhysicalRecordIdAt(8) = FirstRecordId, "The ninth byte should live in a different record than the first eight.")

                        Dim ReadBack(8) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(Expected.ToArray(), ReadBack, "All nine bytes should read back correctly across both records.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SharedRecordIsNotExtendedInPlace()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        Dim Data = GenerateRandomData(64, 1)

                        Cs.Write(0, Data)
                        Cs.Write(1000, Data)

                        Dim SharedRecordId = Cs.Debug_GetPhysicalRecordIdAt(0)

                        AssertEqual(SharedRecordId, Cs.Debug_GetPhysicalRecordIdAt(1000), "Sanity check: both writes should share one deduplicated record.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(SharedRecordId), "Sanity check: the shared record should have a reference count of 2.")

                        ' The stream's logical end now sits right after the shared record's own
                        ' extent - appending here must not mutate content two other extents don't
                        ' expect to change.
                        Cs.Write(1000 + CLng(Data.Length), New Byte() {99})

                        AssertEqual(SharedRecordId, Cs.Debug_GetPhysicalRecordIdAt(0), "The shared record itself must be untouched.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(SharedRecordId), "The shared record's reference count must be unaffected by the later append.")

                        Dim ReadBackA(Data.Length - 1) As Byte
                        Dim ReadBackB(Data.Length - 1) As Byte

                        Cs.Read(0, ReadBackA)
                        Cs.Read(1000, ReadBackB)

                        AssertBytesEqual(Data, ReadBackA, "The first shared range should still read back correctly.")
                        AssertBytesEqual(Data, ReadBackB, "The second shared range should still read back correctly.")

                        Dim AppendedByte(0) As Byte
                        Cs.Read(1000 + CLng(Data.Length), AppendedByte)

                        AssertEqual(99, CInt(AppendedByte(0)), "The newly appended byte should read back correctly from its own, separate record.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CoalescingStillHappensAfterReopen()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, New Byte() {1})
                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "Sanity check: one record after the first session.")

                        Reopened.Write(1, New Byte() {2})

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "Appending after reopen should still coalesce into the existing last record.")

                        Dim ReadBack(1) As Byte
                        Reopened.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2}, ReadBack, "Both bytes, written across two sessions, should read back correctly.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
