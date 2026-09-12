Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies the pure in-memory extendible-hash algorithm behind the on-disk
        ''' deduplication index: split/double correctness under both deliberately small,
        ''' human-checkable scenarios and large random stress, and that its own safety net
        ''' against non-random/duplicate keys actually fires.
        ''' </summary>
        Public NotInheritable Class ExtendibleHashTable

            Private Sub New()
            End Sub

            Private Shared Function MakeKey(Seed As Integer) As Byte()

                Dim Key(31) As Byte
                Dim Rng As New Random(Seed)
                Rng.NextBytes(Key)
                Return Key

            End Function

            <UnitTester.SimpleTest()>
            Public Shared Sub InsertThenLookupFindsExactMatch()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=4)

                For Index = 0 To 19
                    Table.Insert(MakeKey(Index), Index * 100L)
                Next

                For Index = 0 To 19

                    Dim Found As Long

                    AssertTrue(Table.TryGetValue(MakeKey(Index), Found), $"Key {Index} should be found.")
                    AssertEqual(Index * 100L, Found, $"Key {Index} returned the wrong value.")

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub LookupMissingKeyReturnsFalse()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=4)

                Table.Insert(MakeKey(1), 111L)
                Table.Insert(MakeKey(2), 222L)

                Dim Found As Long

                AssertFalse(Table.TryGetValue(MakeKey(999), Found), "A key that was never inserted should not be found.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub GlobalDepthGrowsAsEntriesAreAdded()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=2)

                AssertEqual(0, Table.GlobalDepth, "A fresh table should start at global depth 0.")

                For Index = 0 To 199
                    Table.Insert(MakeKey(Index), Index)
                Next

                AssertTrue(Table.GlobalDepth > 0, "Inserting enough entries to overflow the initial bucket should grow the directory.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub StressInsertAndLookupManyRandomKeys()

                Const EntryCount As Integer = 50000

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=32)
                Dim Keys As Byte()() = New Byte(EntryCount - 1)() {}

                For Index = 0 To EntryCount - 1
                    Keys(Index) = MakeKey(Index + 500000)
                    Table.Insert(Keys(Index), CLng(Index))
                Next

                AssertEqual(EntryCount, Table.Count, "Every inserted entry should be counted.")

                For Index = 0 To EntryCount - 1

                    Dim Found As Long

                    AssertTrue(Table.TryGetValue(Keys(Index), Found), $"Key at index {Index} should be found after {EntryCount} inserts.")
                    AssertEqual(CLng(Index), Found, $"Key at index {Index} returned the wrong value.")

                Next

                Dim MissingKey = MakeKey(999999999)
                Dim MissingValue As Long

                AssertFalse(Table.TryGetValue(MissingKey, MissingValue), "A key outside the inserted set should not be found.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AllEntriesAreEnumerable()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=3)
                Dim Expected As New Dictionary(Of String, Long)()

                For Index = 0 To 299

                    Dim Key = MakeKey(Index + 12345)
                    Table.Insert(Key, Index)
                    Expected(Convert.ToBase64String(Key)) = Index

                Next

                Dim SeenCount = 0

                For Each Item In Table.GetAllEntries()

                    Dim EncodedKey = Convert.ToBase64String(Item.Key)

                    AssertTrue(Expected.ContainsKey(EncodedKey), "Enumerated an entry that was never inserted.")
                    AssertEqual(Expected(EncodedKey), Item.Value, "Enumerated entry has the wrong value.")

                    SeenCount += 1

                Next

                AssertEqual(Expected.Count, SeenCount, "GetAllEntries should enumerate every inserted entry exactly once.")

            End Sub

            ''' <summary>
            ''' Insert is an upsert: re-inserting an already-present key must update its value in
            ''' place, never add a second entry for it - a bucket holding several entries that
            ''' all share one identical key could never be split apart no matter how many more
            ''' bits are examined, so treating a repeat insert as "add another entry" would grow
            ''' the directory without bound (see the reclaimed-and-rewritten-content scenario this
            ''' exact case represents for the deduplication index - ChunkedStream's own dedup
            ''' entries recur under the same key whenever identical content is reclaimed and
            ''' written again).
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertingTheSameKeyManyTimesUpdatesItsValueWithoutGrowing()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=2)
                Dim SameKey = MakeKey(42)

                For Index = 0 To 999
                    Table.Insert(SameKey, Index)
                Next

                AssertEqual(0, Table.GlobalDepth, "A single distinct key should never need the directory to grow.")
                AssertEqual(1, Table.Count, "Re-inserting the same key should never add a second entry.")

                Dim Found As Long
                AssertTrue(Table.TryGetValue(SameKey, Found), "The key should still be found.")
                AssertEqual(999L, Found, "The value should be the most recently inserted one.")

            End Sub

            ''' <summary>
            ''' The safety net is still needed for genuinely non-random *distinct* keys - ones
            ''' that share a long common prefix without being identical, so separating them would
            ''' need more bits of depth than MaxGlobalDepth allows. Unlike a repeated identical
            ''' key (see InsertingTheSameKeyManyTimesUpdatesItsValueWithoutGrowing), an upsert
            ''' cannot rescue this case - these really are different entries that need their own
            ''' slots, and the table has run out of bits to tell them apart safely.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertingManyKeysSharingALongCommonPrefixThrowsRatherThanHanging()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=2)

                Dim Threw = False

                Try

                    ' Every key shares the same first 3 bytes (24 bits) and differs only after
                    ' that - indistinguishable from each other at any depth up to
                    ' MaxGlobalDepth, even though no two are actually identical.
                    For Index = 0 To 999

                        Dim Key = MakeKey(Index)
                        Key(0) = 0
                        Key(1) = 0
                        Key(2) = 0

                        Table.Insert(Key, Index)

                    Next

                Catch ex As InvalidOperationException

                    Threw = True

                End Try

                AssertTrue(Threw, "Inserting many keys that share a long common prefix should throw once depth is exhausted, not hang.")

            End Sub

        End Class

    End Class

End Namespace
