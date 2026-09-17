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

            ''' <summary>
            ''' A torn or MAC-invalid dedup index page must not prevent Open from succeeding -
            ''' the dedup index is explicitly not the source of truth (every hit is re-verified
            ''' by decrypting and comparing before it's ever trusted - see the write-path hook),
            ''' so a corrupt copy of it should be discarded and the stream should still open and
            ''' read correctly, exactly the state a stream that had never used deduplication
            ''' would be in.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OpeningToleratesACorruptDedupIndexPageAndDegradesGracefully()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(64, 1))

                        Cs.Debug_DedupInsert(MakeIndexKey(1), 1L)
                        Cs.Write(1000, GenerateRandomData(64, 2))

                        AssertTrue(Cs.Debug_DedupIndexCount() > 0, "Sanity check: the dedup index should have at least one entry before corrupting it.")

                        Cs.Debug_CorruptFirstDedupIndexPage()

                    End Using

                    Ms.Position = 0

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(0, Reopened.Debug_DedupIndexCount(), "A corrupt dedup index should be discarded on open, not crash it.")
                        AssertTrue(Reopened.AutoRepairs.Any(Function(r) r.Field = "DedupIndex"), "The discard should be recorded as an AutoRepair.")

                        Dim ReadBackA(63) As Byte
                        Dim ReadBackB(63) As Byte
                        Reopened.Read(0, ReadBackA)
                        Reopened.Read(1000, ReadBackB)

                        AssertBytesEqual(GenerateRandomData(64, 1), ReadBackA, "Data unrelated to the dedup index should still read back correctly.")
                        AssertBytesEqual(GenerateRandomData(64, 2), ReadBackB, "Data unrelated to the dedup index should still read back correctly.")

                        ' Deduplication should keep working afterward, rebuilding from scratch.
                        Reopened.Options.Deduplication = True
                        Reopened.Write(2000, GenerateRandomData(64, 3))
                        Reopened.Write(3000, GenerateRandomData(64, 3))

                        AssertEqual(1, Reopened.Debug_DedupIndexCount(), "Deduplication should still work, rebuilding its index from scratch after the old one was discarded.")
                        AssertEqual(Reopened.Debug_GetPhysicalRecordIdAt(2000), Reopened.Debug_GetPhysicalRecordIdAt(3000), "The two new identical writes should share a record.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' A follow-up production incident: discarding a torn/MAC-invalid dedup index on
            ''' Open reset the in-memory entries but kept the on-disk page *descriptors* Root
            ''' read back - untouched, unvalidated leftovers from the very same corrupted region.
            ''' The next publish's "free any old dedup page not reused this time" cleanup
            ''' (PersistPagedMetadataAsync) would then defer-free whatever offset/length those
            ''' stale descriptors happened to claim, even though nothing about them was ever
            ''' confirmed real - on a real, churned archive this collided with space the free-space
            ''' allocator already correctly considered free, corrupting its bookkeeping (a
            ''' KeyNotFoundException out of an unrelated later allocation) rather than freeing
            ''' anything. Fixed by discarding the descriptors in the same Catch block that
            ''' discards the entries - both are equally untrustworthy once the read has failed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DiscardedDedupIndexAlsoDiscardsItsStalePageDescriptors()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        ' Enough distinct dedup-registered content to force at least one real,
                        ' durably published dedup index page with a real on-disk descriptor.
                        For Index = 0 To 49
                            Cs.Write(CLng(Index) * 1000, GenerateRandomData(64, 100 + Index))
                        Next

                        Cs.Flush()

                        AssertTrue(Cs.Debug_GetDedupPageDescriptorCount() > 0, "Sanity check: at least one dedup index page should have been persisted.")

                        Cs.Debug_CorruptFirstDedupIndexPage()

                    End Using

                    Ms.Position = 0

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertTrue(Reopened.AutoRepairs.Any(Function(r) r.Field = "DedupIndex"), "The discard should be recorded as an AutoRepair.")
                        AssertEqual(0, Reopened.Debug_DedupIndexCount(), "A corrupt dedup index should be discarded on open.")
                        AssertEqual(0, Reopened.Debug_GetDedupPageDescriptorCount(), "The stale page descriptors must be discarded along with the entries - keeping them would let the next publish defer-free an unvalidated offset/length pair.")

                        ' Enough further churn (fresh writes, each publishing) to exercise several
                        ' more metadata publishes - each one runs the "free any old dedup page not
                        ' reused this time" cleanup this bug lived in. Must not throw.
                        Reopened.Options.Deduplication = True
                        For Index = 0 To 19
                            Reopened.Write(CLng(Index) * 1000, GenerateRandomData(64, 200 + Index))
                        Next

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' GetActiveMetadataRanges (Storage.vb) - the "what space is currently spoken for"
            ''' list every free-space computation (BuildFreeSpaceMapCore's scan-based rebuild,
            ''' IsRangeSafeForStorage's per-allocation safety check, and Defragment's own hole/
            ''' trim accounting) is built from - covered extent pages, physical-record pages,
            ''' their directory pages, hole-directory pages and the metadata root, but never
            ''' dedup index pages. A live dedup page's on-disk space was therefore invisible to
            ''' every one of those - eligible to be handed out as "free" to an ordinary write, or
            ''' to have the backing stream trimmed right through it during a metadata compaction -
            ''' silently tearing a dedup page that was never actually superseded. This is the most
            ''' likely genuine root cause of "Dedup index page MAC invalid" corruption reported in
            ''' production (see dedup-stale-key-crash memory), not just a downstream consequence
            ''' of it. Fixed by adding _DedupPageDescriptors to GetActiveMetadataRanges, the same
            ''' treatment every other metadata page type already had.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DedupIndexPagesAreNeverTreatedAsFreeSpace()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.Deduplication = True

                        For Index = 0 To 49
                            Cs.Write(CLng(Index) * 1000, GenerateRandomData(64, 300 + Index))
                        Next

                        Cs.Flush()

                        Dim DedupRanges = Cs.Debug_GetDedupPageDescriptorRanges()
                        AssertTrue(DedupRanges.Count > 0, "Sanity check: at least one dedup index page should have been persisted.")

                        ' The exact scan-based rebuild GetActiveMetadataRanges' omission affected -
                        ' reachable directly via the public API, not just indirectly through a
                        ' DeferPublish rollback or BestFit/FirstFit fallback.
                        Cs.BuildFreeSpaceMap()

                        Dim FreeSpans = Cs.Debug_GetFreeSpaces()

                        For Each DedupRange In DedupRanges
                            For Each FreeSpan In FreeSpans

                                Dim Overlaps =
                                    FreeSpan.Offset < DedupRange.Offset + DedupRange.Length AndAlso
                                    DedupRange.Offset < FreeSpan.Offset + FreeSpan.Length

                                AssertFalse(Overlaps,
                                    $"Free span [{FreeSpan.Offset}, {FreeSpan.Offset + FreeSpan.Length}) must not overlap live dedup index page [{DedupRange.Offset}, {DedupRange.Offset + DedupRange.Length}) - it would be handed out to an ordinary write, silently tearing the dedup page.")

                            Next
                        Next

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' Reported from a real, heavily-churned EmbeddedFileSystem archive: Defragment(Move)
            ''' kept reporting fragmentation as 0% and saved as 0, over and over, even though the
            ''' backing file was hundreds of megabytes with almost no live data left in it.
            '''
            ''' TrimAndCommitDefragMetadata (Defrag.vb) forces a fresh, compact rewrite of every
            ''' metadata page type by clearing its descriptor dictionary before publishing - an
            ''' empty dictionary means WriteXxxPagesAsync's "keep it where it already is if nothing
            ''' changed" optimisation (see WriteDedupEntryPagesAsync's own remarks) finds no old
            ''' descriptor to compare against, so it always writes a fresh page near the compacted
            ''' data. _DedupPageDescriptors was the one descriptor dictionary left out of that
            ''' clear. Its pages were then found unchanged against their surviving old descriptors
            ''' and left exactly where they already sat - which, after a run of writes that grew the
            ''' file and then dereferenced almost all of it, could easily be far out past where the
            ''' compacted stream should now end. GetActiveMetadataRanges correctly refuses to treat
            ''' a live dedup page as free space (see DedupIndexPagesAreNeverTreatedAsFreeSpace above),
            ''' so that single stranded page pinned the trim boundary at its old offset, and the
            ''' backing store could never shrink below it no matter how many times Defragment ran.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MoveDefragRelocatesScatteredDedupPagesAndTrimsTheTail()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .Deduplication = True,
                        .DedupIndexPageEntryCount = 8
                    }

                    Dim DedupCountBeforeDefrag As Integer
                    Dim LengthBeforeDefrag As Long

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        ' Distinct chunks, flushed periodically while the file is still growing, so
                        ' the dedup pages published early sit at low offsets and the ones published
                        ' later - once the file is already large - land far out, exactly like the
                        ' reported archive's history of writes.
                        For Index = 0 To 199

                            Cs.Write(CLng(Index) * 1024, GenerateRandomData(1024, 50000 + Index))

                            If Index Mod 20 = 19 Then Cs.Flush()

                        Next

                        Cs.Flush()

                        DedupCountBeforeDefrag = Cs.Debug_DedupIndexCount()

                        AssertTrue(
                            Cs.Debug_GetDedupPageDescriptorCount() > 1,
                            "Sanity check: writes should have produced more than one dedup index page.")

                        ' Dereference every live record without shrinking the backing store - the
                        ' same shape as an archive whose content was mostly deleted: almost nothing
                        ' left to compact around, but the dedup pages from its larger past remain.
                        Cs.SetLength(0)

                        LengthBeforeDefrag = Ms.Length

                        AssertTrue(
                            LengthBeforeDefrag > 100000,
                            $"Sanity check: test setup should have left a large backing store. Length={LengthBeforeDefrag}")

                        Dim FirstCallSaved = Cs.Defragment(ChunkedStream.DefragTypes.Move)

                        AssertTrue(
                            Ms.Length < LengthBeforeDefrag \ 4L,
                            $"Defragment(Move) did not trim the backing store even though almost nothing was left live. Before={LengthBeforeDefrag}, After={Ms.Length}, Saved={FirstCallSaved}")

                        Dim LengthAfterFirstCall = Ms.Length

                        Dim SecondCallSaved = Cs.Defragment(ChunkedStream.DefragTypes.Move)

                        AssertEqual(
                            0L,
                            SecondCallSaved,
                            $"A second back-to-back Defragment(Move) reclaimed {SecondCallSaved} more bytes - the first call did not converge.")

                        AssertEqual(
                            LengthAfterFirstCall,
                            Ms.Length,
                            "A second back-to-back Defragment(Move) changed the backing-store length.")

                        AssertEqual(
                            DedupCountBeforeDefrag,
                            Cs.Debug_DedupIndexCount(),
                            "Relocating the dedup pages during defrag must not lose any dedup entries.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Ms.Position = 0

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        Reopened.Validate().ThrowIfErrors()

                        AssertEqual(
                            DedupCountBeforeDefrag,
                            Reopened.Debug_DedupIndexCount(),
                            "Reopening after the relocation should still see every dedup entry.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
