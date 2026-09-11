Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies the on-disk deduplication index: entries survive reopen (plain and
        ''' encrypted), many entries spanning multiple index pages round-trip correctly, an
        ''' index that was never used persists nothing, and repeated publishes of an
        ''' append-only index don't corrupt earlier entries.
        ''' </summary>
        Public NotInheritable Class DedupIndexPersistence

            Private Sub New()
            End Sub

            Private Shared Function MakeIndexKey(Seed As Integer) As Byte()

                Dim Key(31) As Byte
                Dim Rng As New Random(Seed)
                Rng.NextBytes(Key)
                Return Key

            End Function

            <UnitTester.SimpleTest()>
            Public Shared Sub EntriesSurviveReopen()

                Using Ms As New MemoryStream()

                    Dim KeyA = MakeIndexKey(1)
                    Dim KeyB = MakeIndexKey(2)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Debug_DedupInsert(KeyA, 111L)
                        Cs.Debug_DedupInsert(KeyB, 222L)
                        Cs.Write(0, {0})
                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        Dim ValueA As Long, ValueB As Long

                        AssertTrue(Reopened.Debug_DedupTryGetValue(KeyA, ValueA), "The first entry should survive reopen.")
                        AssertEqual(111L, ValueA, "The first entry's value should be unchanged after reopen.")

                        AssertTrue(Reopened.Debug_DedupTryGetValue(KeyB, ValueB), "The second entry should survive reopen.")
                        AssertEqual(222L, ValueB, "The second entry's value should be unchanged after reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub EntriesSurviveReopenWhileEncrypted()

                Using Ms As New MemoryStream()

                    Dim Key = MakeIndexKey(3)
                    Dim EncryptionInfo As New ChunkedStream.EncryptionInfo(MakeKey(7101))

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Options.EncryptionInfo = EncryptionInfo
                        Cs.Debug_DedupInsert(Key, 333L)
                        Cs.Write(0, {0})
                    End Using

                    Ms.Position = 0

                    Dim ReopenOptions As New ChunkedStream.ChunkedStreamOptions With {.EncryptionInfo = EncryptionInfo}

                    Using Reopened = ChunkedStream.Open(Ms, ReopenOptions)

                        Dim Value As Long

                        AssertTrue(Reopened.Debug_DedupTryGetValue(Key, Value), "The entry should survive reopen of an encrypted stream.")
                        AssertEqual(333L, Value, "The entry's value should be unchanged after reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ManyEntriesSpanningMultiplePagesSurviveReopen()

                Const EntryCount As Integer = 50

                Using Ms As New MemoryStream()

                    Dim Keys As Byte()() = New Byte(EntryCount - 1)() {}

                    Using Cs = ChunkedStream.Open(Ms)

                        ' A small page-entry count forces the flat entry list across several
                        ' dedup index pages instead of fitting in just one.
                        Cs.Options.DedupIndexPageEntryCount = 4

                        For Index = 0 To EntryCount - 1
                            Keys(Index) = MakeIndexKey(Index + 40000)
                            Cs.Debug_DedupInsert(Keys(Index), CLng(Index) * 10L)
                        Next

                        Cs.Write(0, {0})

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(EntryCount, Reopened.Debug_DedupIndexCount(), "Every inserted entry should be counted after reopen.")

                        For Index = 0 To EntryCount - 1

                            Dim Value As Long

                            AssertTrue(Reopened.Debug_DedupTryGetValue(Keys(Index), Value), $"Entry {Index} should be found after reopen.")
                            AssertEqual(CLng(Index) * 10L, Value, $"Entry {Index} returned the wrong value after reopen.")

                        Next

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub StreamWithNoDedupActivityPersistsNoEntries()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, {1, 2, 3})
                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(0, Reopened.Debug_DedupIndexCount(), "A stream that never used deduplication should have an empty index after reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub EntriesInsertedAcrossSeparatePublishesAllSurvive()

                Using Ms As New MemoryStream()

                    Dim KeyA = MakeIndexKey(5)
                    Dim KeyB = MakeIndexKey(6)
                    Dim KeyC = MakeIndexKey(7)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Debug_DedupInsert(KeyA, 1L)
                        Cs.Write(0, {0})

                        ' The dedup index is append-only between rebuilds, so this publish should
                        ' leave KeyA's page untouched and only add KeyB's.
                        Cs.Debug_DedupInsert(KeyB, 2L)
                        Cs.Write(0, {0})

                        Cs.Debug_DedupInsert(KeyC, 3L)
                        Cs.Write(0, {0})

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        Dim ValueA As Long, ValueB As Long, ValueC As Long

                        AssertTrue(Reopened.Debug_DedupTryGetValue(KeyA, ValueA), "The entry from the first publish should survive.")
                        AssertEqual(1L, ValueA, "KeyA has the wrong value.")

                        AssertTrue(Reopened.Debug_DedupTryGetValue(KeyB, ValueB), "The entry from the second publish should survive.")
                        AssertEqual(2L, ValueB, "KeyB has the wrong value.")

                        AssertTrue(Reopened.Debug_DedupTryGetValue(KeyC, ValueC), "The entry from the third publish should survive.")
                        AssertEqual(3L, ValueC, "KeyC has the wrong value.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
