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

            <UnitTester.SimpleTest()>
            Public Shared Sub InsertingManyIdenticalKeysThrowsRatherThanHanging()

                Dim Table As New ChunkedStream.ExtendibleHashTable(BucketCapacity:=2)
                Dim SameKey = MakeKey(42)

                Dim Threw = False

                Try

                    ' No amount of splitting can separate identical keys - the table should
                    ' detect this and fail loudly rather than spin forever.
                    For Index = 0 To 999
                        Table.Insert(SameKey, Index)
                    Next

                Catch ex As InvalidOperationException

                    Threw = True

                End Try

                AssertTrue(Threw, "Inserting many identical keys should throw once the key's bits are exhausted, not hang.")

            End Sub

        End Class

    End Class

End Namespace
