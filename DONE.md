# ChunkedStream — DONE

Open items are in [TODO.md](TODO.md). Item ids match the audit artifact.

---

## 2026-09-17

### SB-1 / SB-2 — sub-block compression evaluated as a whole chunk, compressed at most once — FIXED
`PrepareChunkRecord` (`Storage.vb`) used to run a full whole-chunk trial compression purely to
decide `StoredCompressionMethod` / `CompressionEvaluatedPercent`, then compress every
sub-block independently *again* from scratch to build the actually-stored bytes — the trial's
own compressed output (`Payload` / `PayloadLength`) was computed and then never read (SB-2,
dead code), and the accept/reject decision was based on that separate, thrown-away number
rather than the real total the sub-block pass would actually store (SB-1) — for one
constructed case (a 16 KB pseudo-random unit repeated four times across a 64 KB chunk at an
8 KB sub-block size) this wasn't just cosmetic: the whole-chunk trial found the macro
repetition and said "compress", while independent 8 KB sub-blocks each see less than one unit
and don't compress at all, so the chunk was stored as full-size "compressed" data — no space
saved, format overhead, no correctness break.

Fix: the per-sub-block compression loop now runs *once* and is the only place compression
happens — `AttemptFullCompression` (set from the existing cheap sampled pre-check, unchanged)
gates whether it runs at all. Every sub-block's compressed bytes are kept from that one pass;
after the loop, one aggregate (`TotalCompressedLength`, the real sum across every sub-block)
decides `StoredCompressionMethod` for the *whole record* — either every sub-block is stored
via its already-computed compressed bytes, or every one of them falls back to plaintext, never
a per-sub-block mix. `CompressionEvaluatedPercent` is now derived from that same real total, so
it always matches what's actually on disk. The dead `Payload` / `PayloadLength` / whole-chunk
`Compressed` variables are gone.

Deliberately unchanged, per explicit design: an individual sub-block whose own slice compresses
poorly is still stored inflated when the *chunk as a whole* clears the ratio threshold — the
decision is chunk-wide by design, not per sub-block, so there is no per-sub-block "keep
plaintext if smaller" fallback to add. The sampled-evaluation fast path (`Options.CompressionEvaluation
= Sampled`, a small leading-bytes probe to skip full compression when it looks hopeless) is
unrelated and untouched.

Tests (`Storage representation policies/Compression.vb`, +2, suite 416 → 418):
`CompressionDecisionReflectsRealPerSubBlockTotalsNotAWholeChunkEstimate` (the repeated-16 KB-unit
case above — fails against the prior code: it stored the chunk compressed when no sub-block
actually benefited) and `OneIncompressibleSubBlockDoesNotPreventTheRestOfTheChunkFromCompressing`
(one incompressible sub-block among seven highly compressible ones still stores the whole
record compressed — passes either way, documents the intentional chunk-wide tradeoff).
Confirmed the first test non-vacuous by reverting just the source fix (`git stash` on
`Storage.vb` only, keeping the new tests) and re-running: 417 passed / 1 failed, with the
failing assertion showing `CompressionMethod=Deflate` where `None` was expected; restored and
re-verified 418/418 clean.

---

## 2026-09-14

### A second, unrelated Thread.Abort corruption mechanism, plus a genuine self-deadlock in the state lock — both FIXED

Reported: the user thread-aborted a file upload (`EmbeddedFileSystemSample`, real
`Thread.Abort()` via `frmProgress`, same as the 2026-09-10 incident) and hit the identical
symptom on reopen — `InconsistentPhysicalRecordPagesException: Physical record 650 appears
on more than one physical-record page.` Since the 2026-09-10 fix (`AssertPhysicalRecordIndexConsistent`,
[[torn-physical-record-index]]) was already on `master`, this had to be a *different* gap,
not a recurrence.

**Bug 1 — dirty-page tracking could desync from the tables it describes.**
`MovePhysicalRecordOrdinal`, `DetachReclaimedPhysicalRecord`, `PlaceChunkRecordAsync` /
`PlaceChunkRecordsAsync`, and `RelocatePhysicalRecord` (defrag) each mutated
`_PhysicalRecordIdsByPage` / `_PhysicalRecordOrdinals` (or, for the defrag case, a record's
offset) *first*, then marked the affected page(s) dirty in `_DirtyPhysicalRecordPages` as a
separate, later statement. A `Thread.Abort()` landing in that gap left the in-memory tables
fully self-consistent — `AssertPhysicalRecordIndexConsistent` sees nothing wrong, since it
only checks the tables against each other, never against `_DirtyPhysicalRecordPages` — but
the page that actually changed was never queued for rewrite. The next publish silently kept
serving the *stale* on-disk copy of that page, which still listed the record under its old
page while the new page (or the header) had already moved on: exactly
"appears on more than one physical-record page." Fixed by reordering all four sites to mark
dirty *before* mutating - a kill anywhere in the (now much narrower) gap can only produce a
spurious, harmless extra rewrite, never a missing one.

**Bug 2 — a genuine self-deadlock in `EnterStateLock`/`EnterReadLock`, found while stress-testing
the fix above.** A new Thread.Abort fuzz test (`KillingAWorkerThreadDuringRecordChurnNeverTearsThePhysicalRecordIndex`,
below) hung a real worker thread forever in ~50% of runs even after Bug 1 was fixed. Root
cause: `EnterStateLock()` called `_StateLock.EnterWriteAsync().GetAwaiter().GetResult()`
(acquire) and `_StateLockDepth.Value += 1` (record that fact) as two separate statements. An
abort landing between them left the custom `AsyncReaderWriterLock` genuinely held by the
thread, but `_StateLockDepth` still reading 0 ("not held"). Any reentrant call on the same
thread during the same unwind — e.g. `Dispose()`/`EndDeferPublish`'s own
`Using EnterStateLock()` — then saw depth 0, believed it needed to acquire fresh, and blocked
forever on a non-reentrant lock this very thread already owned: a textbook self-deadlock, not
just a data race. `EnterReadLock` had the same shape (acquire, then a separate `Return` that
could be interrupted before the caller ever received the releasing scope, leaking the read
lock held forever - which, against this writer-preference lock, eventually starves every
future writer too). A first attempt at fixing this only wrapped the `Return` in a
`Try/Catch`, leaving the acquire-then-bookmark gap itself completely unprotected - confirmed
by the fuzz test still hanging afterwards. The real fix puts the acquisition *itself* inside
the `Try`, with two flags (`LockAcquired`, `DepthBumped`) recording exactly how far the call
got, so the `Catch` can undo precisely what happened - lock release, depth decrement, or both
- leaving nothing orphaned regardless of where in the sequence the abort lands. The async
lock path (`EnterStateLockAsync`/`RunUnderStateLockAsync`/`RunUnderReadLockAsync`) has the
same shape but could not get the identical fix in this pass - `AsyncLocal` writes made
inside an awaited call do not flow back to the caller, so acquisition and bookkeeping can't
be folded into one atomic-with-respect-to-interruption step there without a larger
restructure. Flagged in a code comment for follow-up; lower real-world risk since a
continuation resuming on a pooled thread is untouched by an abort targeting the original
thread, and the sync path (confirmed as the user's actual repro) is now fully covered.

**Test:** `KillingAWorkerThreadDuringRecordChurnNeverTearsThePhysicalRecordIndex`
(`Hardening.vb`) - kills a real background thread at a randomized spin-wait point during
dense record churn (repeated overwrite forcing reclaim + replacement every iteration, plus
periodic grow/shrink forcing ordinal compaction), 60 trials, and requires every reopen
afterwards to be a clean strict open (zero `AutoRepairs`). Before the deadlock fix: hung the
whole suite in ~50% of runs (diagnosed by temporarily raising the join timeout to 60s and
confirming the thread never returns even then - not slow, genuinely stuck; a
`StackTrace(Thread, Boolean)` capture attempt failed with "Thread in invalid state" while
aborting, which is itself consistent with a thread stuck mid-abort-delivery inside a
synchronization primitive). After both fixes: 15 consecutive full-suite runs clean,
415/415.

**User's real file recovered.** Verified end-to-end against a copy of the user's actual
423 MB `Test.efs` (never the original): the existing 2026-09-10 tolerant-open salvage
pipeline already handled it correctly without any further library change - opened
tolerantly (1 duplicate entry / 1 record lost), salvaged table persisted back,
`Repair(IncludeDataLoss)` zeroed the one unrecoverable extent, `RecoverPendingFiles`
finalized 3 pending files left over from the interrupted upload, re-`Validate()` clean. User
chose to run the same recovery themselves via the rebuilt sample app rather than have it
applied here.

---

## 2026-09-13

### Dedup index pages were invisible to free-space accounting entirely — likely the true root cause of "Dedup index page MAC invalid" — FIXED

Found by checking whether `DeferPublish`'s rollback path (reload-from-disk, see
`PerformImageReload`/`AdoptLoadedImage`) handles dedup state correctly, prompted by a direct
question about it. `AdoptLoadedImage` turned out to be fine - it adopts every dedup field,
including `_DedupHashTable` itself, wholesale from the freshly reloaded image, so a DeferPublish
rollback is safe for dedup. But following that trail into `AdoptLoadedImage`'s own
`BuildFreeSpaceMapCore()` call surfaced something much more serious: `GetActiveMetadataRanges`
(`Storage.vb`) - the single "what space is currently spoken for" list every free-space computation
is built from - lists extent pages, physical-record pages, their directory pages, hole-directory
pages and the metadata root, but never dedup index pages. A live dedup page's on-disk space was
therefore invisible to `IsRangeSafeForStorage`'s per-allocation safety check, the scan-based
free-space rebuild (`BuildFreeSpaceMapCore`, used by `DeferPublish` rollback, `Defragment`, a
cancelled `ApplyOptions` chunk-size rewrite, and `BestFit`/`FirstFit`'s own fallback scan), and
`Defragment`'s own hole/trim accounting - eligible to be silently handed out to an ordinary write,
or to have the backing stream trimmed right through it during metadata compaction, tearing a dedup
page that was never actually superseded.

This is a strong root-cause candidate for the "Dedup index page MAC invalid" symptom this entire
investigation thread started from, independent of the free-space-descriptor bug fixed below (that
one only fires after an index is *already* torn; this one plausibly tears it in the first place).
Fixed by adding `_DedupPageDescriptors` to `GetActiveMetadataRanges` - the exact same treatment
every other metadata page type already had. Added `Debug_GetDedupPageDescriptorRanges` and
`Debug_GetFreeSpaces`, and `DedupIndexPagesAreNeverTreatedAsFreeSpace` to
`DedupIndexPersistence.vb`: persists a real dedup index page, calls the public `BuildFreeSpaceMap()`
directly, and asserts no resulting free span overlaps a live dedup page's range. Confirmed
non-vacuous by reverting and watching it fail with a concrete overlapping span. Re-verified end to
end against the actual reported `Test.efs` alongside the fix below, no regression. 411/411 passing.

### Root cause of the KeyNotFoundException found and fixed: discarding a torn dedup index kept its stale page descriptors — FIXED

Follow-up to the entry below (the open-time tolerance fix) - this is the *original* crash's root
cause, found by reproducing directly against the user's actual reported `Test.efs` (533MB) rather
than blind fuzzing. Running the browser sample's "Extended Scan" (`Validate` → `Mark` →
`Repair(IncludeDataLoss)` → `RecoverPendingFiles`) against the real file threw `KeyNotFoundException:
The given key was not present in the dictionary.` from `FreeSpaceAllocator.RemoveSpan`/`TryAllocate`
during an unrelated later write - a free-space bookkeeping corruption, not a dedup bug at all,
despite always appearing alongside dedup index corruption.

Root cause: the open-time fix below correctly discards a torn/MAC-invalid dedup index's *entries*,
but `.DedupPageDescriptors` was still populated unconditionally from `Root.DedupPageDescriptors` -
the on-disk page-location list read from the same corrupted metadata region, never independently
validated, and just as untrustworthy as the entries it sits next to. The next publish's "free any
old dedup page not reused this time" cleanup (`PersistPagedMetadataAsync`) then defer-freed
whatever offset/length those stale descriptors happened to claim. On a real, churned archive that
offset collided with space the free-space allocator already correctly held as free, silently
corrupting its five parallel indexes (`FreeSpaceAllocator`'s `_OffsetIndex`/`_LengthByOffset`/
`_StartByEnd`/`_OffsetsByLength`/`_DistinctLengths`) - which only surfaced much later, confusingly,
as a `KeyNotFoundException` out of an unrelated allocation.

Fixed by discarding the page descriptors in the same `Catch` block that discards the entries -
costs leaking the old pages' physical space until a `Defragment(Rebuild)` reclaims it, the same
trade-off already accepted for the entries themselves. Diagnosed with two temporary
self-consistency checks in `FreeSpaceAllocator` (removed once the exact corrupted call site was
found); one - an O(1) check in `Insert` that throws immediately if a span would overwrite an
already-registered free span - was kept permanently, since it catches this whole class of
double-free-shaped bug at the point of corruption instead of leaving it to surface later as a
confusing `KeyNotFoundException` somewhere unrelated. Added `Debug_GetDedupPageDescriptorCount`
and `DiscardedDedupIndexAlsoDiscardsItsStalePageDescriptors` to `DedupIndexPersistence.vb`: corrupts
a real, durably-published dedup index page, reopens, confirms the page descriptor count (not just
the entry count) drops to 0, then performs further writes and `Validate()` to confirm nothing
throws. Confirmed non-vacuous by temporarily reverting the fix and watching the assertion fail.
Confirmed the fix itself by re-running the exact Extended Scan repro against the exact same real
file end to end with no exception. 410/410 passing.

### CurrentChunkWriteCaching silently disabled parallel crypto/placement for large writes — FIXED

Found while chasing the `KeyNotFoundException` investigation (still unresolved, see the
deduplication-feature plan): `AppendThroughWriteCacheAsync` looped through
`CommitPendingChunkAsync` one chunk at a time for the *entire* length of a `Write()` call, however
large. Unlike the unconditional (non-cached) extend-in-place path - whose own remainder always
went through the parallel-capable `BuildExtentsFromBufferAsync` - a large buffered upload with
`CurrentChunkWriteCaching = True` never reached `BuildExtentsInParallelAsync`, or its batched
`PlaceChunkRecordsAsync` disk-write overlap, at all - silently falling back to fully serial
per-chunk crypto and placement regardless of `Options.MaxCryptoParallelism`/
`MaxPhysicalWriteParallelism`. This meant the sample app's own deliberate `WriteBufferFlushThreshold`
tuning (raised specifically to reach the parallel path for large uploads) had become a no-op the
moment write-caching was turned on alongside it.

Fixed by only ever handling the *first*, possibly-seeded chunk through the buffer directly;
anything left over in the same call now goes through the same `BuildExtentsFromBufferAsync`
remainder call the non-cached path already used (with `AllowGrowableTail:=True`, so the new tail
stays open to a later small append exactly like the buffer's own seed always was) - which is what
actually decides whether to parallelise. `AppendThroughWriteCacheAsync`'s own loop is gone
entirely; it only ever needs to run once now, since the remainder call handles however many
further chunks `Count` spans internally.

Also replaced the hardcoded `ParallelChunkCryptoMinChunks` constant (always 8) with
`Options.MinChunksForParallelCrypto`, a real per-stream setting - same default, same behaviour
unless changed, but now tunable per the request that prompted this. Updated both the write-side
and read-side parallel checks and the sample's own reference to the removed constant.

Added `Debug_ParallelBuildInvocationCount` (increments each time `BuildExtentsInParallelAsync`
actually runs) since the existing dedup tests only ever inferred "the parallel path ran" indirectly
from record counts - which can't distinguish a serial one-chunk-at-a-time commit sequence from a
parallel batch when both happen to produce identical final data. `LargeCachedWritesStillReachTheParallelBuildPath`
and `MinChunksForParallelCryptoControlsTheThreshold` added to `Concurrency.vb`. 405/405 passing.

**Locking/threading review, prompted by the user asking whether recent work increased locking or
disabled threading elsewhere:** re-audited every change from this session (write-coalescing
stages 1-3, the sparse-remainder fix, both dedup fixes) specifically for this. Found only the one
regression above (now fixed) and one pre-existing, already-documented, deliberate trade-off worth
restating: `Options.CurrentChunkWriteCaching` makes `Read`/`ReadAsync`/both `ToArray` overloads
upgrade from the shared read lock to the exclusive state lock *whenever the option is on*,
regardless of whether a write is actually in flight - necessary because a read may need to commit
the pending buffer, which mutates shared state unsafely under a lock a concurrent reader might
also hold. A stream that never uses the option is completely unaffected; nothing else found
increases lock contention or disables threading.

### Deduplication index: a torn/MAC-invalid page used to refuse Open entirely — FIXED

Reported live, a follow-up to the previous day's stale-key crash: after `Options.Deduplication`
and `Options.CurrentChunkWriteCaching` were both turned on and an upload failed with
`KeyNotFoundException`, the archive could no longer even be **opened** afterward -
`ChunkedStream.Open` threw `CryptographicException: Dedup index page MAC invalid.` and aborted
before anything else loaded. Root cause of the original crash not pinned down at the time (deferred
at the user's own request - a large fuzz test matching the reported settings ran clean, so it
needed a real repro first rather than more guessing) - this entry is about the *open-time* failure
only. **Root cause since found and fixed - see the entry above, dated the same day.**

The dedup index is documented, repeatedly, as explicitly not the source of truth - a rebuildable
hint the write path always verifies by decrypting and comparing before trusting (see
[[deduplication-feature]]) - so a damaged copy of *it specifically* refusing to open an otherwise
healthy, fully-readable archive contradicted the feature's own design. `ReadPagedMetadata`
(`Metadata.vb`) now catches `CryptographicException`/`InvalidDataException` around the dedup-page
read and discards the whole index (an empty table, same as a stream that never used
deduplication) rather than letting Open fail - unconditionally, not gated behind the narrower
`Tolerate` flag `ReadPhysicalRecordPages` uses for its own, different class of inconsistency, since
a strict first-attempt open of a healthy archive must not fail over this one non-authoritative
structure. Records an `AutoRepair("DedupIndex", ...)` so the discard is visible rather than silent.
The stored `_DedupCoveredUpToRecordId` high-water mark is also reset to 0 when this fires - keeping
it would make a later catch-up scan believe everything up to that mark was already indexed, when
the index backing that claim no longer exists; matches what `DedupRebuild(Soft:=False)` already
does when it discards the whole table for a different reason (key rotation).
- Added `Debug_CorruptFirstDedupIndexPage` (flips a byte in the first persisted dedup page's own
  bytes, independent of `_Header`, unlike the existing dedup-key corruption helper) and
  `OpeningToleratesACorruptDedupIndexPageAndDegradesGracefully` to `DedupIndexPersistence.vb`:
  corrupts a real, durably-published dedup page, reopens, and confirms Open succeeds, unrelated
  data reads back correctly, the discard is recorded, and deduplication keeps working afterward by
  rebuilding its index from scratch. 403/403 passing.

---

## 2026-09-12

### Deduplication index: stale-key crash (production incident) — FIXED

Reported live: the `EmbeddedFileSystemSample` app, with `Options.Deduplication = True`, threw
`InvalidOperationException: Extendible hash table directory depth exceeded its safety limit -
this points at duplicate or non-random keys, not normal growth.` while uploading files, and the
backing `Test.efs` could no longer accept uploads afterward.

**Root cause, confirmed by a minimal repro:** `ExtendibleHashTable.Insert`'s own contract requires
the caller to have already done a `TryGetValue` miss check - but a "miss" from
`TryDeduplicateWriteAsync` can mean two different things: no entry for this key ever existed, or
an entry exists but points at a physical record that's since been reclaimed (a known, previously
"harmless" limitation - the dedup index is never cleaned up on reclaim). `RegisterWrittenRecordForDeduplication`
treated both cases identically and called `Insert` unconditionally on any miss. For the second
case, this planted a **second** entry for a key that was already present. Since the key is a
content hash and reclaim-then-rewrite of the same content is ordinary churn (any offset
repeatedly overwritten with a small, recurring set of distinct contents - exactly what a real
upload session does), the same key could accumulate many duplicate entries in one bucket over
time - and a bucket full of entries that all share one identical key can never be split apart no
matter how many more bits are examined, so it grows the directory forever and trips
`MaxGlobalDepth`'s own safety limit.
- **Fix:** `ExtendibleHashTable.Insert` is now an upsert - it scans its target bucket for an
  existing entry with the same key first and updates its value in place, never appending a
  second entry for one key. This is unconditionally safe (nothing ever legitimately wants two
  entries for the same key - `TryGetValue` only ever returns the first one it finds anyway) and,
  as a side effect, also closes the "stale entry can shadow a fresher live entry" known limitation
  entirely for anything inserted from now on.
- Repro and regression guard: `ReclaimingAndRewritingTheSameContentManyTimesDoesNotDuplicateItsDedupEntry`
  (`DedupWritePath.vb`) cycles a small set of distinct contents through the same offset thousands
  of times (each cycle reclaims and later re-establishes every pattern's own dedup entry) and
  asserts the index never grows past one entry per distinct pattern. `ExtendibleHashTable.vb`'s
  old `InsertingManyIdenticalKeysThrowsRatherThanHanging` test asserted the *previous, buggy*
  behavior (that repeated identical keys eventually throw) and had to be replaced: split into
  `InsertingTheSameKeyManyTimesUpdatesItsValueWithoutGrowing` (proves the upsert directly) and
  `InsertingManyKeysSharingALongCommonPrefixThrowsRatherThanHanging` (keeps the actual safety net
  under test, using genuinely distinct keys that share a 24-bit prefix instead of one repeated
  key, since that's the one case an upsert genuinely cannot rescue).
- **Recovery of the actual reported file:** confirmed the crash's own `MissingPhysicalRecord`
  fallout was already isolated and repairable using the *existing* validation/repair tooling with
  no new work needed - `ChunkedStream.Validate()` found the affected extents,
  `EmbeddedFileSystem.Mark(Report)` localized them to exactly one nested subdirectory out of
  three top-level entries, and `Report.Repair(RepairScope.IncludeDataLoss)` brought the stream
  back to zero validation errors. Verified end-to-end against a copy of the real reported file:
  opens cleanly, repairs cleanly, and a subsequent upload with `Deduplication = True` (the exact
  originally-failing operation) now succeeds. 401/401 total.

### Write coalescing / current-chunk write caching — DONE

Every `Write()` call used to decide its own chunk boundaries in total isolation - two sequential
appends, even fully contiguous ones, always got two independent physical records (traced through
`WriteCoreAsync`: the append branch calls `BuildExtentsFromBufferAsync` on only that call's own
bytes, with no visibility into the previous extent's record; the same was true of the replace
branch for a write into a just-`SetLength`'d range). Fixed in two stages, both landing after the
deduplication feature shipped (see its own DONE.md entry) since the write-path hooks there turned
out to matter for this too.

**Stage 1 (`d39cadb`) - extend in place, unconditional:**
- `TryGetExtendableLastChunk` (`Extents.vb`): true when the stream's last extent wholly
  (`PhysicalRecordOffset = 0`, `LogicalLength = Record.PlainLength`) and exclusively
  (`RefCount = 1`) references a non-sparse record still under `Options.ChunkSize`. A shared or
  dedup-matched record is left alone - other extents rely on its exact current content.
- `TryExtendLastChunkAsync`: on a genuine end-of-stream append, decrypts that record once, appends
  as many new bytes as fit below `ChunkSize`, and writes the combined plaintext through the
  ordinary `WritePhysicalRecordAsync` pipeline (so it can itself dedupe) instead of always
  allocating a fresh record. `ReplaceLastExtentRecord` redirects the one extent and reclaims the
  old record - not gated behind any option, since every `Write()` call still fully commits before
  returning either way, exactly as before this existed.
- Fixed-size only for now - targets `Options.ChunkSize`, not CDC-aware.

**Stage 2 (`18ffd59`) - `Options.CurrentChunkWriteCaching` (off by default):**
- Defers stage 1's same commit across several small appends instead of paying the decrypt/
  re-encrypt round trip on every one: `_PendingChunkPlain` (a `List(Of Byte)`) accumulates in
  memory, seeded once via the same `TryGetExtendableLastChunk` eligibility check, and only commits
  (`CommitPendingChunkAsync`) when a chunk actually fills, `FlushCurrentChunkWriteCache`/`Async`
  (new public API) is called explicitly, or something needs the committed state to be complete.
- Commit triggers wired in: a full chunk (looped for a large append spanning several), explicit
  flush, a non-contiguous write or one issued after the option was turned back off mid-stream,
  `SetLength`, `Flush` (also publishes, matching an ordinary `Write()`), and `Dispose` (a faulted
  stream's buffer is discarded like every other half-applied state - losing a buffer silently
  otherwise would mean bytes a `Write()` call already returned success for never existed
  anywhere).
- **Locking:** a pending buffer isn't backed by any extent, so a read into it must commit first -
  which mutates shared state, unsafe under only the shared read lock a concurrent reader might
  also hold. The positional `Read`/`ReadAsync` overloads upgrade to the exclusive state lock only
  when `Options.CurrentChunkWriteCaching` is actually in use; a stream that never touches the
  option keeps its existing, fully concurrent read path untouched.
- `_Length` always reflects buffered-but-uncommitted bytes immediately (never behind what
  `Write()` already returned) - the brand-new-chunk commit path splices its extent directly rather
  than through `InsertExtentsCore`, which would have double-counted it.
- Tests: `WriteCoalescing.vb` (4, stage 1) + `CurrentChunkWriteCaching.vb` (8, stage 2), all under
  `Logical mutations and allocation`. 381/381 passing.

**Flush-site audit (`9fd6c87`) - closing stage 2's own known gap:**
`Validate`/`GetFragmentation` (share `CaptureDiagnosticsSnapshotCore`), `GetStructure`
(`CaptureStructureSnapshotCore`), `ApplyOptions`, `DedupRebuild`, `Defragment`, `Replace`,
`CreateCheckpoint`, and `DeferPublish` all now materialise a pending buffer first - every one of
them reads or resolves offsets through `_Extents`/`_PhysicalRecords` directly, which a buffered
tail isn't part of until committed. Before this, combining `CurrentChunkWriteCaching` with any of
them risked a false corruption report or worse.
- `CreateCheckpoint` and `DeferPublish` needed the strongest treatment: their rollback mechanisms
  (a captured baseline; reload-from-disk, respectively) would otherwise have no record of a buffer
  that predates them, silently losing it on a later rollback even though it was never touched by
  whatever gets rolled back. `DeferPublish` additionally *publishes* the commit (the same as an
  explicit `FlushCurrentChunkWriteCache`), not just materialises it, since its rollback reloads
  from what's actually on the backing store, not an in-memory snapshot.
- `AdoptLoadedImage` (the `AutoRecoverOnFault` reload path) discards a pending buffer outright
  rather than leaving it stale against a wholesale-replaced extent table - a faulted stream's
  not-yet-persisted mutations are already expected to be lost, same as everywhere else that reload
  touches.
- 6 more tests in `CurrentChunkWriteCaching.vb`, including a checkpoint rollback and a
  `DeferPublish` publish both confirming the pre-scope buffered data survives correctly. 387/387
  passing.

**Second flush-site audit (`9915203`) - the first pass was not actually exhaustive:** prompted by
asking "is it safe everywhere - ToArray, reading, etc", a systematic sweep of every
`EnterReadLock`/`RunUnderReadLockAsync`/`EnterStateLock`/`RunUnderStateLockAsync` call site (not
just the ones already suspected) found real remaining gaps: `ToArray`/`ToArrayAsync` (both
overloads call `ReadCore`/`ReadCoreAsync` directly, bypassing the earlier fix entirely), `Clone`,
`CloneInsert`, `Insert`, `Clear`'s sparse branch, `InsertNullBytes`'s sparse branch, `CreateAnchor`
(both overloads), and `ValidationReport.Repair`. All fixed the same way as the first pass.
- Also tried and **reverted**: putting the flush inside `PersistIndexAndHeaderAsync` itself as a
  catch-all. This broke caching outright, since `WriteCoreAsync` publishes at the end of every
  call including ones that legitimately left bytes buffered. The "problem" it solved -
  `_Length` briefly ahead of `_Extents`' actual span in a published header - turns out to already
  be safe (`OpenFromHeaderCandidate` treats that mismatch as a repairable `AutoRepair`, not
  corruption - exactly buffering's documented trade-off). Reverted the same mistake in
  `Options_EncryptionInfoChangedCore`. 5 more tests including a regression guard. 392/392 total.

**Stage 3 (content-defined-chunking awareness) - closing the original EFS/dedup-alignment gap:**
Stages 1-2 always targeted a fixed `Options.ChunkSize` cap, ignoring `Options.ChunkSizeVariance`
entirely - so a stream built up through many small appends would chunk differently than the same
final bytes written in one shot whenever CDC was in use, defeating the whole point of write
coalescing for EFS/dedup alignment. Fixed by making both `TryExtendLastChunkAsync` and
`AppendThroughWriteCacheAsync` run the same Gear-hash scan `DetermineNextSegmentLength` runs for a
one-shot write, just resuming mid-chunk instead of starting one from scratch:
- `TryGetExtendableLastChunk` became `TryGetExtendableLastChunkAsync`: under fixed-size chunking
  the eligibility check is unchanged (cheap, metadata-only), but under CDC it now decrypts the
  candidate record (once - returned alongside the eligibility result so neither caller decrypts it
  a second time) and calls the new `IsChunkClosedByCdcBoundary` to tell a chunk that's merely
  short of `ChunkSize` apart from one that's *genuinely closed* - already ended on a real
  Gear-hash boundary or `Options.MaxChunkSize`. Only the latter distinction matters for CDC: a
  chunk closed by content should never be extended further, even if it's short of `ChunkSize`,
  because a one-shot write of the combined bytes would never have extended it either - it would
  have started a fresh chunk (Hash resets to 0) right where the old one ended. That fresh-chunk
  case is already handled for free: `TryExtendLastChunkAsync`'s caller in `WriteCoreAsync` already
  routes anything past `ExtendedCount` through the ordinary `BuildExtentsFromBufferAsync`/
  `DetermineNextSegmentLength` path, so no restructuring was needed there at all.
- `IsChunkClosedByCdcBoundary(Plain, Length)`: `Length >= MaxChunkSize` is always closed;
  `Length < MinChunkSize` is always open (the hash never even engages that early - see
  `DetermineNextSegmentLength`'s own remarks); otherwise replays `ComputeChunkHashState` and
  checks whether the threshold trips at exactly `Length`. Assumes the plaintext is self-consistent
  (produced by this same chunking logic) rather than re-checking for an earlier trip - only
  content this class itself wrote is ever a candidate for extension.
- `DetermineChunkExtensionLength(ExistingPlain, ExistingLength, Input, DataOffset, Count)`: the
  CDC counterpart to the plain `ChunkSize - ExistingLength` room calculation. Primes the rolling
  hash to the state a from-scratch scan would have reached by `ExistingLength` (via
  `ComputeChunkHashState`, replaying only bytes from `MinChunkSize` onward - matching
  `DetermineNextSegmentLength` exactly), then continues rolling into the new bytes, stopping at a
  threshold trip, `MaxChunkSize`, or `Count`, whichever comes first. Used by both
  `TryExtendLastChunkAsync` (replacing its old `ChunkSize - PlainLength` cap) and
  `AppendThroughWriteCacheAsync`'s per-iteration fill amount (replacing its old
  `ChunkSize - _PendingChunkPlain.Count` cap) - the latter also swaps its old
  `_PendingChunkPlain.Count >= ChunkSize` commit check for `IsChunkClosedByCdcBoundary` under CDC,
  so a boundary found mid-buffer commits immediately rather than only once the buffer happens to
  reach `ChunkSize`.
- All three helpers accept `IReadOnlyList(Of Byte)` rather than `Byte()`, so
  `AppendThroughWriteCacheAsync` can pass `_PendingChunkPlain` (a `List(Of Byte)`) directly with no
  copy, while `TryExtendLastChunkAsync` passes a plain decrypted array.
- At `ChunkSizeVariance = 0` every one of these takes the same cheap, hashing-free fixed-size path
  they always did - this is purely additive for CDC users, zero behavioural or performance change
  otherwise.
- Added `ContentDefinedWriteCoalescing.vb` (4 tests, `Logical mutations and allocation`): a
  byte-at-a-time incrementally-written stream and a bursty, buffered
  (`CurrentChunkWriteCaching = True`) one both produce **exactly** the same extent-length sequence
  as a one-shot write of the identical bytes under CDC (`Debug_GetExtentCount`/
  `Debug_GetExtentLength`, both new); non-tail chunks close on a real Gear-hash boundary short of
  `MaxChunkSize`, not just always at the cap; and appending past a chunk closed that way (proven by
  truncating a one-shot write exactly at its first chunk's own boundary, which is guaranteed
  closed - only a write's true final chunk can end merely because data ran out) starts a genuinely
  new record rather than growing the closed one. 396/396 total.

This closes the original EFS/dedup-alignment gap write coalescing was built to fix: streamed and
one-shot writes of the same bytes now chunk identically under content-defined chunking, the same
way they already did under fixed-size chunking.

**Follow-up fix - `CalculateSplitHashCheck`'s average-chunk-length math was off by `MinChunkSize`:**
caught by checking the formula against how the hash is actually consumed. The hash only starts
rolling once `MinChunkSize` bytes are already behind it (see `DetermineNextSegmentLength`'s own
remarks) - those bytes are never subject to a threshold check at all. The old formula targeted
`Threshold = 2^64 / ChunkSize`, which makes the *post-MinChunkSize* scan's own expected length
`ChunkSize` - on top of the `MinChunkSize` bytes that already elapsed unconditionally before the
scan could even start. Total expected chunk length was therefore `MinChunkSize + ChunkSize`, not
`ChunkSize` - at the default 25% variance this pushes the average close to (or past)
`MaxChunkSize`, so almost every chunk was cut by hitting the cap rather than by content, silently
defeating most of CDC's own benefit. Fixed to target `Threshold = 2^64 / (ChunkSize - MinChunkSize)`
instead - the scan only ever needs to explain the bytes *after* `MinChunkSize`, since those are
already accounted for unconditionally. `RecalculateChunkSizeBounds` also now guards
`AverageBytesToScan <= 0` (possible at a tiny non-zero `ChunkSizeVariance`, where `CInt`'s
rounding can round `MinChunkSize` up to equal `ChunkSize`) by disabling the hash the same as
`ChunkSizeVariance = 0` would - no room left for content to pick a boundary.

**That fix immediately surfaced a real, previously-masked bug in stage 1/2 themselves:** with
chunks now actually landing near `ChunkSize` instead of almost always at `MaxChunkSize`, real
content-defined cuts became common for the first time - and one of `ContentDefinedWriteCoalescing.vb`'s
own equivalence tests started failing. Root cause: a lone zero byte that happens to be the *first*
byte of what should become a larger real chunk (e.g. one Write() call absorbing exactly one byte
before the previous chunk closed) was getting sparse-flagged in isolation by the generic
`BuildExtentsFromBufferAsync` extent builder - and a sparse extent is never itself eligible to
extend (`TryGetExtendableLastChunkAsync` rejects it outright), so it froze there permanently, one
byte short of what a one-shot write of the same combined bytes would have produced. That single
misclassification shifted every later Gear-hash window by one byte, cascading into a
stream-wide chunk-boundary mismatch (not a data-correctness bug - a sparse zero byte still reads
back as zero - but a real alignment one, directly undercutting the EFS/dedup-alignment goal stage
3 was built for).
- **First attempt, tried and reverted: make sparse extents themselves eligible for extension.**
  Seemed like the natural fix (treat a small sparse run's "plaintext" as a synthetic all-zero
  array and run it through the same eligibility/extend machinery as a real record), and it did fix
  the equivalence test - but it broke several *existing* deduplication tests
  (`IdenticalSmallWritesShareOnePhysicalRecord`, `SharedRecordIsNotExtendedInPlace`, and others):
  writing identical content at two offsets separated by a gap smaller than `ChunkSize` (a common
  dedup-test pattern - `Cs.Write(0, Data); Cs.Write(1000, Data)`) now merged the *explicit,
  deliberate* gap-filler (from a jump-ahead `Write()`) into the same physical record as whatever
  got written after it, since that gap is *also* just a sparse extent under `ChunkSize`. A
  deliberate gap and an undersized coalescing remainder are indistinguishable from an extent's own
  fields alone, so this made *every* small gap growable too - reaching across a boundary
  deduplication (and the pre-existing `SharedRecordIsNotExtendedInPlace` test) explicitly depends
  on staying intact. Reverted in full (`ReplaceLastExtentWithSparse`, the eligibility branch, and
  the transient `_PendingChunkHasSeedExtent` bookkeeping it needed).
- **Actual fix, scoped to where the ambiguity truly lives:** `BuildExtentsFromBufferAsync` gained
  an `AllowGrowableTail` parameter, set only by the one call site that fills in the remainder of a
  genuine end-of-stream append `TryExtendLastChunkAsync` already partially absorbed
  (`WriteCoreAsync`'s append branch) - never for an overwrite, insert, or gap fill, where nothing
  is ever going to extend the result further. When true, the *last* segment produced skips the
  sparse check if it hasn't genuinely closed yet (`IsChunkClosedByCdcBoundary` says open - i.e. it
  stopped only because `Count` ran out, not a real boundary or `MaxChunkSize`) - it's written as a
  real record instead, staying eligible for ordinary extend-in-place on the next `Write()` call,
  exactly like any other real chunk. A deliberate gap-filler never reaches this code path at all
  (`InsertSparseRange` has its own, separate extent-creation logic), so deduplication's
  gap-independence guarantee is completely untouched.
- **Bonus, independent fix found along the way:** `CommitPendingChunkAsync` (the
  `CurrentChunkWriteCaching` buffer's own commit) never checked `Options.StoreSparseChunks` /
  `IsAllZero` at all - a fully-zero buffered chunk always became a real record. Added the same
  check `BuildExtentsFromBufferAsync` already had, for the one case it's actually reachable (a
  buffer that started completely fresh, with no seed extent - a seeded buffer's content already
  has a non-zero byte by construction, or its seed would have been sparse itself).
- 3 more tests: `ALoneZeroByteAtTheStartOfANewChunkStaysGrowable` and
  `CoalescingDoesNotReachAcrossAnExplicitGap` in `WriteCoalescing.vb`,
  `AnAllZeroBufferedChunkBecomesSparseOnCommit` in `CurrentChunkWriteCaching.vb`. 399/399 total.

---

## 2026-09-11

### Multi-chunk read cache — DONE

`Options.UseChunkReadCache` had been dead since the Aug 2026 extents rework (d254bd5): it
gated a single-slot most-recently-read cache keyed by chunk-table index; the rework rebuilt
the read path around extents + reference-counted physical records and dropped every cache
hit/fill site. `_ChunkPlain` / `_CachedChunkPlain` / `_CachedExtentIndex` survived only as
snapshot/realloc ceremony in `Defrag.vb` / `Options.vb`, and the option was referenced only
in a header comment. The old `Diagnostics.vb` "chunk cache" tests asserted read correctness
only, so they passed either way.

- **`Options.ChunkReadBlockCache As Integer` (default `DefaultChunkReadBlockCache` = 32)**
  replaces `UseChunkReadCache`. `0` disables. It is a runtime knob (not persisted). The XML
  doc spells out the `ChunkReadBlockCache * ChunkSize` ceiling (~4 MB at the defaults) and
  `ChunkSize`'s doc now points at it.
- **The cache** (`_ChunkedStream.vb`) is a `Dictionary(Of Long, LinkedListNode(Of ReadCacheEntry))`
  + intrusive MRU `LinkedList`, keyed by physical-record id, holding the decrypted whole-record
  plaintext. Every access takes `_ReadCacheSync` — concurrent shared-lock readers reach it.
  Entries are immutable once inserted and handed out by reference (all callers of
  `ReadPhysicalRecordPlain*` treat the result read-only, verified incl. the `ApplyRecordOptions`
  write-back path, which copies out per sub-block). A record's stored bytes never change under
  a fixed id and ids only ascend, so a surviving entry is always valid for its id.
- **Wiring** (`Storage.vb`): `ReadPhysicalRecordPlain` + async twin check/fill; `ReadPhysicalRecordPlainRange`
  + async twin check a cached whole record and slice from it (serving any sub-range with no I/O
  or MAC/crypto even for multi-sub-block records). `ReadRangeInParallel[Async]` (`_ChunkedStream.vb`)
  consults the cache single-threaded while building its work list — a cached record is neither
  re-read nor re-decrypted — and fills it once the worker pool finishes. Nothing touches the
  cache from inside the parallel region.
- **Invalidation is per-record.** `EvictCachedRecord(id)` is hooked into
  `DetachReclaimedPhysicalRecord` — the single point a record leaves `_PhysicalRecords` (an
  overwrite's superseded records, a truncate, the pending-reclaim batch, the unreferenced
  sweep). An entry is valid for exactly as long as its record is in that table (bytes are
  immutable under a fixed id; ids only ascend), so an ordinary edit keeps the untouched
  working set warm. The `InvalidateChunkCache()` full-clear calls were removed from the data
  entrypoints (`Write` / `SetLength` / `Insert` / `Remove` / `Clone` / `Clear` /
  `InsertNullBytes` / `CreateAnchor`) and kept only at the bulk resets where ids can be
  renumbered or reloaded to different content: image reload, checkpoint rollback, all of
  `Defragment`, `ApplyOptions` (plus one added at the end of `RunApplyOptions`), repair.
- **Tests** (`Diagnostics.vb`, via new `Debug_ChunkReadCache*` seams): repeat reads served as
  hits not fills; MRU eviction at `ChunkReadBlockCache = 2`; an overwrite evicts only its own
  record and leaves the others warm (re-read = hits); the parallel path fills and is served;
  multi-sub-block records (partial read doesn't fill, but a cached whole record serves a later
  partial read); `ChunkReadBlockCache = 0` retains nothing; negative value rejected.
  `ParallelChunkCryptoMatchesSerialAndSurfacesCorruption` sets `ChunkReadBlockCache = 0` — it
  tampers with bytes the stream has already read, which a cache would legitimately mask. 335/335.
- **`CacheRecordPlain` fast-paths a disabled cache** off the lock (`ChunkReadBlockCache <= 0`
  returns without `SyncLock` once the cache is drained), so `ChunkReadBlockCache = 0` is truly
  zero-overhead on the read path.

### Throughput benchmark — DONE

`zBenchmarks.ChunkedStreamThroughput` (`_Tests/UnitTests/Benchmarks/_Core.vb`) — one
`SimpleBenchmark` reporting read and write MB/s for memory-backed streams over fixed
`ThroughputWindowMs` (500 ms) windows, so the run takes the same wall-clock time on any host
(a slow host reports a smaller number, it does not run longer). Staging is also time-bounded
(`ThroughputStageBudgetMs`). Writes overwrite a 32 MB region with 4 MB (multi-chunk) writes;
reads re-read it with 4 MB reads. Rows: Plain / Encrypted (AES-256-CTR) / Encrypted+Deflate
(60% compressible) for each of write and read; the per-scenario read rows force
`ChunkReadBlockCache = 0` to measure raw MAC+decrypt+decompress, and a final row re-reads a
4 MB fully cache-resident region for the cache ceiling. Each row round-trips one buffer as a
correctness guard; a row under ~8 MB/s warns.

Also: removed the dangling `repair-test-efs.ps1` mention from the README (the user deleted
the script — the validate/mark/repair/recover pass it ran is the built-in `Validate()` /
`ValidationReport.Repair()` / `RecoverPendingFiles` API plus the sample's own Extended Scan).

---

## 2026-09-10

### Torn physical-record index could be persisted, bricking the reopen — DONE
`_Samples/EmbeddedFileSystemSample/bin/Debug/Test.efs` became unopenable
(`InvalidDataException "Loaded physical-record count does not match metadata root."` from
`ReadPhysicalRecordPages`) after cancelling file copies mid-operation. The sample's
`frmProgress` cancels with `thread.Abort()`; a `ThreadAbortException` between the two halves
of a `MovePhysicalRecordOrdinal` (or inside `CompactPhysicalRecordOrdinalsAfterRemoval`)
left `_PhysicalRecordIdsByPage` disagreeing with `_PhysicalRecordOrdinals` — a record filed
onto two pages, another onto none. Because the metadata-publish spine had no
`Catch: _Faulted=True` guard (every public mutator has one) and `EndDeferPublish`'s
dirty-pages-only gate could skip the rollback, that torn index was serialised onto both
header generations. On reopen the per-id `Dictionary` collapsed the duplicates and the count
check threw, with no clean generation to fall back to.

Fix (branch Test2):
- **Contain.** `AssertPhysicalRecordIndexConsistent()` (`Extents.vb`) runs at the top of
  every `PersistPagedMetadataAsync`, and its whole body is now wrapped in
  `Try … Catch: _Faulted=True: Throw`. A torn index (id on the wrong page or two pages, a
  page/ordinal/record count mismatch) throws *before any byte is written*, so the last
  durably published generation stays intact and openable. O(records) per publish.
- **Recover in place.** `EndDeferPublish` now rolls back by `PerformImageReload()` — reload
  the last durable generation straight from the backing store — instead of
  `RestoreCheckpointState`; an in-memory restore cannot be trusted after a killed thread.
  The `_DeferPublishState` / `CheckpointState` snapshot is gone from the DeferPublish path.
  After the reload it re-applies the forward-only `_NextAnchorId` / `_NextPhysicalRecordId`
  (`Math.Max` with the pre-rollback values, so an id handed out in the abandoned window is
  never reissued) and rebuilds `_FreeSpaces` with `BuildFreeSpaceMapCore()` (Open only
  seeds it from the persisted hole directory, which the window's non-durable publishes may
  not have written). This also closes DeferPublish follow-ups **c** and **e**.
- **Salvage an already-torn file.** `ReadPhysicalRecordPages` throws a new
  `InconsistentPhysicalRecordPagesException` (`Inherits IOException`) for a duplicate id or
  a short count. `OpenCore` tries every header copy strictly first; only if they *all* fail
  that exact way does it retry newest-first with tolerance — keep the first copy of each
  duplicate, record an `AutoRepair`, and run `DropUnrecoverablePhysicalRecords`
  (`ReadPagedMetadata`) to drop records past the backing stream or overlapping another
  live record's span. A tolerant open of a *writable* stream then eagerly writes the
  cleaned table straight back (`PersistSalvagedPhysicalRecordTable` — `MarkAllMetadataPages­
  Dirty` + a durable publish), so every later open is an ordinary strict open with no
  AutoRepairs instead of repeating the retry. Best-effort: a failed write-back leaves the
  correct in-memory image and the next open just retries. The extents that reference
  records the corruption genuinely lost stay as `MissingPhysicalRecord` in `Validate()`
  until an explicit `Repair(RepairScope.IncludeDataLoss)` / the sample's Extended Scan —
  auto-zeroing data on open would defeat the point of `RepairScope`.

Verified on a copy of the real Test.efs: opens with 2 AutoRepairs, `EFS.Mark` flags 3
entries, `Repair(IncludeDataLoss)` → 13 repaired / 6.9 KB zeroed, re-validate clean,
`RecoverPendingFiles` re-homes 3, reopen fully clean (one casualty: the `Dune 2` directory
→ `CorruptDirectory`). New tests
`Recovery.TornPhysicalRecordIndexIsRefusedByThePublishAndTheFileStaysOpenable` and
`Recovery.PhysicalRecordPagesWithACrossPageDuplicateOpenTolerantlyAndRepair`, plus two
`Debug_*` helpers in `Unit Test Helpers.vb`. 327/327.

The sample still uses `Thread.Abort`; the library is now robust to it regardless — worst
case is a faulted stream, and `Autoexec.vb` already sets `AutoRecoverOnFault = True` so the
next call reloads.

### `ChunkedStream.StartupRecovery` — grouped open-time diagnostics — DONE
The open-time recovery state was spread across four flat properties (`RecoveryStateAtOpen`,
`AutoRecoveryState`, `AutoRecoveryException`, `AutoRepairs`) with no single "was this file
damaged?" signal — so `AutoRecoveryState = NotRequired` on a file that clearly needed
salvage (the journal only covers *protected operations*; a `DeferPublish` window writes
none). New `Public ReadOnly Property StartupRecovery As StartupRecoveryReport` groups them:
`.JournalStateAtOpen` / `.JournalReplay` / `.RecoveryException` / `.Repairs` /
`.RepairsApplied` / `.RepairsPersisted` (was the salvage written back) /
`.UnrecoverableExtentCount` (extents that reference physical records the corruption
destroyed — counted in `OpenFromHeaderCandidate`), plus the rollups `.NeedsScan`,
`.IsClean`, `.State` (`StartupRecoveryOutcome`: `Clean` / `RepairsApplied` /
`UnrecoverableData` / `JournalRecovered` / `RecoveryFailed`) and a ready-made `.Summary`
string. `.NeedsScan` stays True after a structural salvage has been persisted clean if
`UnrecoverableExtentCount > 0`, so the "run a scan" prompt survives the eager write-back.
The four flat properties stay as thin forwarders (26 call sites).
`PersistSalvagedPhysicalRecordTable` sets `_StartupSalvagePersisted`; `AdoptLoadedImage`
carries all the startup fields across a fault reload.

Sample: `EmbeddedFileSystemBrowserForm_Load` checks `StartupRecovery.NeedsScan` and, if set,
shows `StartupRecovery.Summary` once the window is up and offers to run the Extended Scan
there and then — Info style for a clean repair, Exclamation when data was actually lost
(`UnrecoverableData` / `RecoveryFailed`).

### `Validate()` buffered every live physical record into memory at once — DONE
`Validate()` captured its diagnostics snapshot under the state lock and, in the same pass,
read the full bytes of *every* live physical record into `DiagnosticsSnapshot.StoredRecords`
(`CaptureDiagnosticsSnapshotCore(IncludeStoredRecords:=True)`). For a large archive that put
the entire payload in RAM at once — and held the write lock for the whole read — before any
per-record inspection or progress callback ran.

Fix (`Diagnostics.vb`): `CaptureDiagnosticsSnapshotCore` no longer reads record bytes (the
`IncludeStoredRecords` parameter and the `StoredRecords` dictionary are gone).
`CollectPhysicalRecordProblems` now reads one record at a time through a new
`ReadPhysicalRecordForValidation`, which re-enters the state lock only long enough to copy
that record's bytes, then releases it so the CPU-heavy MAC checks still run unlocked. Peak
extra memory during validation drops from the whole archive to a single record. The lock is
now taken and dropped per record instead of held across the entire snapshot read, so a
concurrent reader or writer no longer waits out the full scan. A record that is moved or
reclaimed between the snapshot and its read is reported as unreadable; `Repair` already
re-verifies every physical-record problem under the lock before acting, so a spurious report
from that race is skipped. `Repair.vb`'s `PhysicalRecordProblemStillPresent` passes the one
record's bytes straight to `InspectPhysicalRecordSnapshot`, which now takes the buffer as a
parameter.

New test `Validation.ValidateReadsPhysicalRecordsOneAtATimeNotAllUpFront`: a positioned
backing store that counts reads, driven by `Validate` with a progress callback. By the first
callback only the first record has been read (was: all of them), and reads keep arriving as
later callbacks fire. Suite 324 → 325.

---

## 2026-09-06

### Batched concurrent chunk writes raced NTFS's zero-fill and silently lost data — DONE
The intermittent data-integrity bug the DOP benchmark surfaced. `PlaceChunkRecordsAsync`'s
parallel branch (taken for an async multi-chunk write when the backing store declares
`LockFreeWrites` and `MaxPhysicalWriteParallelism > 1`) issued every physical write of the
batch concurrently. For an appending write - the common large-write case - most of those
writes land past the file's end at once. A write past the file system's valid-data-length
makes it zero the gap between that length and the write offset; two such extending writes in
flight together let one write's zero-fill land on top of the bytes another just wrote and
blank them. On read-back the record was all-zero (`InvalidDataException: Physical record id
mismatch. Expected N, found 0.`) or partially zeroed (`CryptographicException: Physical
record MAC invalid.`). Reproduced ~30-90% of runs writing 512 MB - 1 GB through
`PooledPositionedFileStream`, with and without encryption, at any DOP > 1; a pure
`PooledPositionedFileStream` stress test missed it because its FIFO throttle kept the writes
near offset order.

Fix (`Storage.vb`): the parallel branch now issues the record that reaches furthest into the
backing store on its own first, awaited alone. That single write advances the
valid-data-length across the whole batch region, so every remaining write is an in-place
overwrite within valid data - and those are safe to overlap. Cost is one record's worth of
serialisation per batch; the benchmark already showed the parallel physical-write path gives
no measurable throughput gain, so this is free in practice.

New test `Concurrency.WriteBatchNeverRunsTwoFileExtendingWritesConcurrentlyEvenWhenTheStoreAllowsIt`:
a backing store that models the valid-data-length hazard (an extending write bumps the length
but defers the destructive gap-zero past a short window, and it counts extending writes in
flight), driven by a 24-chunk `WriteAsync` with and without encryption. Fails deterministically
without the fix ("Two file-extending physical writes overlapped (peak 8)"). The existing
`ConcurrentAsyncWritesOverlapWhenTheBackingStoreAllowsIt` was updated for the lone priming
write (peak overlap is now `WriterCount - 1`). Suite 321 → 322.

---

## 2026-09-04

### `EmbeddedFileSystem.Mark` crashed when a problem range hit a directory's own content list — DONE
`MarkCorrupt` called `ReadEntries` on every directory with no guard. When a validation problem
range (or an EFS-level structural break) landed inside a directory's *own* content-list record,
`ReadEntries` threw (`CryptographicException` / `InvalidDataException` / `EndOfStreamException`)
mid-walk: `Mark` aborted, the `DeferPublish` rolled back every mark made so far, and the
`Validate → Mark → Repair` path then zero-filled the directory record with nothing recorded.

Fix (`EmbeddedFileSystem.vb`):
- New `EntryTypes.CorruptDirectory` (= 5) — a directory whose content list intersects, or is
  unreadable because of, a validation problem. Distinct from `CorruptData` (a file whose *data*
  is unreadable but whose metadata is intact). `CorruptEntryMark` gained `IsDirectory`.
- `MarkCorrupt` split into `MarkCorruptChildDirectory` / `MarkCorruptChildFile`. Before
  descending into a child directory it checks the child record's extent
  (`LengthOfDataAtEntry`, floored at the header size) against the problem ranges; on overlap it
  flags the entry `CorruptDirectory`, records a mark and does **not** descend. The descent
  itself is also wrapped, so an EFS-structural parse failure the chunk-stream validator never
  saw still degrades instead of aborting. An unreadable **root** content list is reported as a
  `"\"` mark (nothing to re-type). `Mark` still throws only for faults a repair cannot address
  (e.g. a missing file master key — `EncryptionMismatchException` is deliberately not caught).
- `RecoverPendingFiles` / `CollectPendingCandidates` sweep `CorruptDirectory` too: `Remove`
  (via a new `DeleteCore` branch — drop the entry, reclaim the directory record's own span) and
  `List`; `Finalize` is a skip (nothing to promote). `PendingFileRecoveryCandidate` carries
  `ParentDirectoryAnchorId` so a corrupt directory is re-located through its parent's content
  list, not its own (possibly unreadable) header.
- Tests +2 (`EmbeddedFileSystemRecovery.vb`): `CorruptDirectoryIndexIsFlaggedNotThrownAndCanBeRemoved`,
  `RecoverPendingFilesRemovesACorruptDirectory`. Suite 293 → 295.

### `RecoverPendingFiles` recovers records the tree can no longer reach — DONE
Follow-on to the above: removing a `CorruptDirectory` left its whole subtree as records with valid
anchors but no entry referencing them - invisible to `ChunkedStream.Validate`, never reclaimed by
`Defragment`. `RecoverPendingFiles` now has a second phase.

- New `<Flags> RecoveryConditions` (`Pending`, `CorruptData`, `Orphaned`, `Unreachable`) on
  `PendingFileRecoveryCandidate` / `...Result` (plus `IsDirectory`, and `RecoveredPath` on the
  result). `EntryTypes` on disk is unchanged - `Orphaned` / `Unreachable` are computed, never
  persisted, and `Pending` is never inferred for an unreferenced record (it lived in the lost entry).
- Phase 1 (unchanged): the root walk yields the reachable-anchor set and the pending / corrupt /
  corrupt-dir entry candidates. Phase 2: `CollectOrphanCandidates` = `GetAnchors()` − reachable,
  each classified by a single-hop parent check (`Orphaned` = own parent link won't resolve to a
  readable directory; else `Unreachable`), ordered parent-before-child by a BFS over record-header
  `ParentAnchorId`s (no content-list reads for ordering). Record spans come from the gap to the
  next anchor (EFS records are logically contiguous), so an orphan file's length is exact.
- Each phase-2 candidate is re-classified at its turn (an earlier `Remove` in the same loop
  orphans its children; an earlier `Finalize` makes a whole subtree reachable and it drops out).
  `Remove` = drop the record's span. `Finalize` = re-home under `\_Recovered` (created on demand,
  `DirectoryN` / `FileN` names continuing past whatever's there) by adding one entry and rewriting
  the record's parent pointer - a readable directory's subtree follows with its real names; a bare
  file lands as `File` (or `CorruptData`). A directory whose own list is unreadable carries
  `CorruptData`, its children are classified as orphan roots (not descendants), and `Finalize` is
  a skip for it - `Remove` it and its children flatten into `\_Recovered` individually.
- Sample "Scan" selector: `CorruptData ⇒ Remove`, else `Finalize` (pending → promote, unreachable
  → re-home).
- Tests +3 (`EmbeddedFileSystemRecovery.vb`): `UnreferencedRecordsAreRecoveredToTheRecoveredFolder`,
  `RemovingACorruptDirectoryCascadesThroughItsWholeSubtree`,
  `AnUnreadableUnreferencedDirectoryFlattensItsChildrenIntoRecovered`. Suite 295 → 298.
- **Not done:** salvaging a `CorruptDirectory`'s child *names* before Repair zero-fills its list
  (would need `Mark` to stash `{childAnchorId → name}`); until then the parent-died level of an
  orphaned subtree gets a generated `DirectoryN` / `FileN` name.

### `Defragment(Move)` truncated live data when the compacted metadata overflowed the gap — DONE
The sample (`Test("C:\Windows\System32\mrt.exe", False)` — a ~229 MB purely sequential LZ4 +
encrypted BestFit write, reopened and `Defragment(Move)`d) threw
`InvalidDataException("Defrag data end is beyond the backing stream length.")` on the second
Move pass's commit.

Root cause was in `TrimAndCommitDefragMetadata` (`Defrag.vb`), not the D6 `_PhysicalDataEnd`
cache. A Move pass compacts its surviving metadata into the gap
`[CompactDataEnd, FirstActiveMetadataOffset)` (`_CompactMetadataWriteLimit`). When the
compacted metadata is bigger than that gap — many records, little for Move to actually
reclaim, so the gap is small — the overflow pages fall back to appended offsets (above the
old metadata) and the metadata root gets recycled into a snug freed hole
(`TryAllocateSnugMetadataRootHole`) that can sit *below* the live data end. The post-trim
length was then derived from `_MetadataRootOffset + _MetadataRootLength` alone, on the
assumption the root is the highest object in the file, so `BaseStream.SetLength` truncated
the appended pages **and every live physical record above the recycled root** (~1.76 MB of
live data for `mrt.exe`). The next pass's guard caught the now-real "data end past EOF" and
`DefragmentCore`'s `Catch : _Faulted = True` kept the last good generation.

Fix: `NewEndOffset` is now the max of `GetDataEndFromIndex()` and the end of every
`GetActiveMetadataRanges()` entry — the true high-water mark of everything that must survive —
so the trim can never cut into a live record or a freshly written metadata page. (The
messier-than-ideal spilled layout is a convergence concern, tracked separately; the stream is
valid and readable.) New test
`Defragmentation.MoveDefragKeepsMetadataThatSpilledPastTheCompactionGap` (6 MB / 8 KB chunks /
clustered compressibility — reproduces the exact throw without the fix). Suite 292 → 293.

## 2026-09-03

### Re-entrant chunk crypto + parallel bulk read — DONE
Deferred item 3 from the async plan.

**Re-entrancy.** All AES-CTR state — the AES-ECB transform and the keystream scratch buffers —
moved off `ChunkedStream` instance fields (`_Counter`, `_CtrCounterScratch`,
`_CtrKeyStreamScratch`, `_ChunkCipherTransform`, `_AesProvider` all removed) onto a new
`ChunkCipher` class. `ChunkCipher.Crypt(counter, counterOffset, in, inOff, count, out, outOff)`
takes the counter per call and holds no shared state, so any number can run at once. The
serial path keeps one cached `_ChunkCipher` (rebuilt only on a key change); parallel workers
take their own via `CreateChunkCipher`. `DecryptPhysicalRecord` gained an optional `Cipher`.
Keystream byte-for-byte unchanged (existing encrypted files still read; the encryption /
reopen / migration / defrag suite is the proof). The dead `_Counter`-advance after each
`CryptPayload` was dropped (it was only ever called once per record).

**Parallel bulk read.** A synchronous read spanning ≥ 8 chunks now authenticates, decrypts and
decompresses its distinct physical records on `Parallel.ForEach` (one `ChunkCipher` per
partition via `localInit`/`localFinally`), capped at `Options.MaxCryptoParallelism` (default
`Environment.ProcessorCount`; 1 = fully serial). Only the per-chunk CPU is threaded — the
backing-store reads and the copy into the caller's buffer stay serial, and the workers touch
only immutable state (`_ChunkMacKey`, `_ChunkEncryptionKey`) plus their own buffers. A
`Parallel.ForEach` `AggregateException` is unwrapped (`ExceptionDispatchInfo`) so a corrupt
chunk still surfaces its `CryptographicException`. The async read path runs the whole parallel
block on `Task.Run` so the awaiting thread is not blocked. Tests +1 (`Encryption.vb`);
suite 290 → 291.

Tests +1 (`Encryption.vb`); suite 290 → 291. Committed `c333c78`.

### Parallel per-chunk write — DONE
The write side of deferred item 3.

`WritePhysicalRecordWithPolicy` is split in two. `PrepareChunkRecord` is pure CPU — it
applies the compression policy, builds the record header, encrypts (given its own
`ChunkCipher`) and computes the HMAC into a finished stored-record byte array, touching no
shared stream state. It does **not** allocate the record id, draw the IV, call
`MarkCompressionFlag`, or touch `_Rng` — those stay with the caller. `PlaceChunkRecordAsync`
is the serial tail: `MarkCompressionFlag`, `GetNextWriteOffset`, the backing-store write, and
the `_PhysicalRecords` / index / `_IndexOffset` / dirty-page updates.

`BuildExtentsFromBufferAsync` routes a write of ≥ 8 chunks (`ShouldBuildChunksInParallel`,
same `Options.MaxCryptoParallelism` gate as the read side) to `BuildExtentsInParallelAsync`:
it splits the buffer, classifies all-zero sparse chunks, and draws every record id + IV
serially; runs `PrepareChunkRecord` for the non-sparse chunks on `Parallel.ForEach` (one
`ChunkCipher` per worker via `localInit`/`localFinally`, `AggregateException` unwrapped with
`ExceptionDispatchInfo`); then walks the plans in order, emitting a sparse extent or
`Await PlaceChunkRecordAsync` for each. `RunAsync` wraps the parallel prepare in `Task.Run`.
`ParallelReadMinChunks` → `ParallelChunkCryptoMinChunks` (now shared by read and write).

New test `Encryption.ParallelChunkCryptoMatchesSerialAndSurfacesCorruption`: a 40-chunk
deflate+encrypted stream with a mid-stream sparse chunk, written and read back at
`MaxCryptoParallelism = 8`, byte-compared against the `= 1` result, plus reopen, ranged
read, and a corrupt-MAC chunk still surfacing `CryptographicException`.

### Lock-free parallel reads — DONE
The last `TODO.md` "Reader concurrency" item; unblocked by the re-entrant `ChunkCipher`.

`_StateLock` was a `SemaphoreSlim(1, 1)`; it is now a purpose-built
`AsyncReaderWriterLock` (nested in `_ChunkedStream.vb`). `_State` is `-1` (writer) / `0`
(free) / `> 0` (reader count); waiting writers and the waiting-reader batch are queues of
`TaskCompletionSource(Of Boolean)` with `RunContinuationsAsynchronously`. It is
**writer-preference** — `EnterReadAsync` only fast-paths when no writer is queued — so a
steady stream of readers cannot starve a write, which keeps the crash-safety-critical write
path's latency unchanged. An uncontended acquire returns `Task.CompletedTask` (no
allocation).

Shared (concurrent) callers: `ToArray()`, `ToArray(Offset, Length)`,
`Read(LogicalOffset, …)` and their `*Async` twins — each takes its own per-call
`ChunkCipher` (`CreateChunkCipher()`), so nothing crypto-related is shared. `Optional Cipher
As ChunkCipher = Nothing` is threaded through `ReadCore` / `ReadExtentBytes` /
`ReadPhysicalRecordPlain` and the async twins; `Nothing` still selects the shared
serial-path `_ChunkCipher` for the exclusive callers. Backing-store reads are already
serialized by `_PhysicalIoLock` (unless the backing store advertises `LockFreeReads`), and
the record table / extent list / anchor index are only mutated under the exclusive lock, so
concurrent readers touch only stable state.

Everything else stays exclusive: `Stream.Read`/`Write`, anchor-relative reads,
`GetStructure`, `Validate`, `GetFragmentation`, every mutation, and open / recovery / defrag
/ checkpoints — reads and a writer never overlap, so `GetDataEndFromIndex`'s lazy
`_PhysicalDataEnd` recompute and the `_Faulted` flag stay single-threaded. `EnterReadLock`
is a `NullScope` no-op when the flow already holds the write lock; the read path is not
otherwise reentrant and keeps no depth counter.

Trade-off: a `CancellationToken` that fires *while waiting for the lock* no longer aborts
the wait (the lock has no queued-waiter cancellation). The `*Async` helpers still
`ThrowIfCancellationRequested()` up front, so a pre-cancelled token behaves as before. Lock
waits are short (one logical writer), so this is acceptable.

New test `Concurrency.ConcurrentPositionalReadsOverlapRatherThanSerialise`: a gated
`IPositionedStream` backing store (`LockFreeReads`) makes every reader block inside a chunk
read until all four have arrived — which can only complete if the reads truly overlap.
Verified to fail when the read path is forced exclusive. Suite 291 → 292.

### D6 — cache-coherence invariant + drift producer — DONE
The `_PhysicalDataEnd` half-fix from the defrag work is now complete.

**Detection.** `Validate()` reports a `ValidationProblemKind.CacheInconsistency` (Warning,
non-lossy repair) when any incrementally maintained cache disagrees with a fresh rebuild:
the physical-data end vs the live records, `_NextAnchorId` vs the highest anchor id in use,
and `_LivePhysicalRecordIdsByOffset` vs a rebuild. `Repair` fixes it via `RebuildAnchorIndex`
+ the `RebuildPhysicalRecordOrdinals` / `RecalculatePhysicalDataEnd` it already runs.
`DiagnosticsSnapshot` gained `NextAnchorId` and `LivePhysicalRecordOffsets`.

**Producer.** `DecrementPhysicalRecordRefCount` never lowered `_PhysicalDataEnd` when a
record left the live set — fine for non-checkpoint edits (the batched reclaim recomputes at
the end) and for checkpoint commit (it reclaims before the persist), but inside an open
checkpoint the cache stayed stale-high until commit, so a mid-checkpoint metadata read (or a
concurrent `GetStructure`) saw a value above the real data. Now: `DecrementPhysicalRecordRefCount`
sets a `_PhysicalDataEndDirty` flag when a record at (or beyond) the end dies, and
`GetDataEndFromIndex` does the O(records) `RecalculatePhysicalDataEnd` lazily before the value
is next read (doing it per-decrement would undo the batched-reclaim speed-up).
`IncrementPhysicalRecordRefCount` grows the end directly for a resurrected record.

New seams `Debug_GetCachedPhysicalDataEnd`, `Debug_PhysicalDataEndIsStale`,
`Debug_CorruptNextAnchorId`. Tests +3 (`Validation.vb`); the 5 remaining `Validation.vb`
corruption tests now assert the specific `ValidationProblem.Kind`. Suite 287 → 290.

### Corruption tolerance + `chkdsk`-style repair — DONE
Built out from the `Defragment(Move)` corruption incident. Five layers:

**1. Open no longer rejects a stale allocation hint.** The paged metadata (root + pages, each
MAC-verified) is the trust root; the header's index-offset, logical length and extent count
are derived from it. `Open` now reconciles those against the loaded metadata instead of
throwing (`IndexOffset > BaseStream.Length` was the exact check that locked out the corrupted
`Test.efs`), records each correction in the new `ChunkedStream.AutoRepairs`
(`{Field, StoredValue, CorrectedValue, Reason}`), and persists the fixed value on the next
durable write. Page geometry and the root MAC stay hard failures (candidate fallback handles
those).

**2. `Validate()` returns a report instead of throwing.**
`Validate(...) As ValidationReport` collects every problem (`ValidationProblem`:
`Severity`, `Kind`, `Message`, `PhysicalRecordId?`, `AffectedRanges`, `CanRepair`,
`RepairIsLossy`, `DataLossBytes`). `ValidationReport` exposes `Problems` / `Errors` /
`Warnings` / `IsValid` / `HasErrors`, plus `ThrowIfErrors()` (throws `ValidationException`,
carrying the report) for the old behaviour. `ValidateAsync` returns `Task(Of ValidationReport)`.
~280 test call sites migrated to `.Validate().ThrowIfErrors()`.

**3. `ValidationReport.Repair`.** Two overloads:
`Repair(Optional RepairScope = NonLossy)` and `Repair(Func(Of ValidationProblem, Boolean))`,
so `Repair(Function(p) p.DataLossBytes = 0)` = non-lossy, `Repair(Function(p) p.Kind = ...)`
= one kind. `NonLossy` reconciles reference counts, rebuilds the anchor index and recomputes
the logical length; the lossy path converts every extent backed by an unreadable/missing
physical record into a sparse (zero) extent of the same logical length - offsets, stream
length and anchors are preserved, the range just reads as zeros. Every problem is re-verified
against the live stream before it is acted on (so `Validate → Mark → Repair` is safe - the
intervening `Mark` writes touch only directory entries), the whole repair is one durable
publish, and a mid-repair failure faults the stream. A structurally broken extent chain aborts
the repair untouched. Returns `RepairResult` (`Repaired`, `Skipped` with reasons, `BytesZeroed`).

**4. EFS corruption marking.** New `EmbeddedFileSystem.EntryTypes.CorruptData`.
`EmbeddedFileSystem.Mark(report)` maps each problem's logical range back to the file that
contains it and flips the entry to `CorruptData` (returns `CorruptEntryMark`:
`Path`, `AnchorId`, `LostBytes`). Call it before `Repair`.

**5. `RecoverPendingFiles` returns detail + per-entry selector.** Now
`IReadOnlyList(Of PendingFileRecoveryResult)` (`Path`, `AnchorId`, `PreviousState`, `Action`,
`DataLength`, `BytesZeroed`) and sweeps both `PendingFile` and `CorruptData` entries.
`PendingFileRecoveryActions` gained `None` (skip, omit from result) and `List` (report only, no
change). `RecoverPendingFiles(Func(Of PendingFileRecoveryCandidate, PendingFileRecoveryActions))`
picks the action per entry, e.g.
`Function(c) If(c.State = EntryTypes.CorruptData, Remove, Finalize)`; it enumerates all
candidates first (one `GetStructure()` for the zeroed-byte counts) then re-locates each by
anchor id, since `Remove` shifts logical offsets. Sample `EmbeddedFileSystemBrowserForm` "Scan"
rewritten as Validate → Mark → confirm-if-lossy → Repair → RecoverPendingFiles (Remove corrupt,
Finalize pending) with a full summary.

**Where it can't help:** if the extent/physical-record *pages themselves* were truncated away
(the real `Test.efs` - a failed defrag had appended all 147 pages above the eventual end, only
the 8 KB root survived), there is nothing to reconcile; `ReadMetadataPageBytes` now reports
that plainly ("truncated below its metadata") instead of a bare end-of-stream read. Deep
data-area salvage is out of scope (see TODO).

New files: `Streams/ChunkedStream/Repair.vb`. New test seam
`Debug_CorruptPersistedHeaderIndexOffset`. Tests +6, suite 273 → 278.

### `OpenFile` commit control + public `FileStreamView` — DONE
`EmbeddedFileSystem.OpenFile` now returns the (newly `Public`) `FileStreamView` and takes
`Optional PendingOnClose As Boolean = False`. `FileStreamView.PendingOnClose` is settable:
while set, disposing the stream leaves the parent entry `PendingFile` (and drops the write
buffer instead of flushing it) rather than finalising it to `File`. An upload opens with
`PendingOnClose:=True` and clears it as the last statement on the success path, so an aborted
copy is left for `RecoverPendingFiles` / the overwrite-pending path with no `Catch` +
`DeleteEntry` dance and no silent-truncation window. `Dispose` now drains and closes under a
`Try/Finally` so a failing buffer drain can't leak the open-file id. New
`FileStreamView.ToArray()` returns the whole file (buffered appends flushed, position
independent, `Array.Empty` for empty, throws above `Integer.MaxValue`). Sample upload loop
switched over. Tests +3.

### `DecrementPhysicalRecordRefCount` — batch the reclaim — DONE
Freeing K physical records in one edit called `RebuildPhysicalRecordOrdinals()` (a full
O(n log n) rebuild of three indexes) K times — O(K·n log n), seconds for a large
`DeleteEntry` / file replace / `ApplyOptions` migration (the user saw 400 ms+ single calls).

- `ReclaimPhysicalRecord` split into `DetachReclaimedPhysicalRecord` (drop one record from
  the table + live-offset index, defer its span, return its ordinal) and a batch finaliser
  that rebuilds the ordinal map and marks pages **once**. New `ApplyPendingPhysicalRecordReclaims`.
- `DecrementPhysicalRecordRefCount` no longer reclaims inline in RefCount mode — it adds to
  `_PendingReclaimedPhysicalRecords` (the mechanism checkpoints already used). New
  `SettleDeferredPhysicalRecordReclaims` at the end of `RemoveRangeCore` / `ReplaceRangeCore`
  drains the whole batch with one rebuild (or, under a checkpoint, leaves it for
  `ReclaimPendingPhysicalRecords` at the outermost commit — now also batched).
- Scan-mode sweep (`ReclaimUnreferencedPhysicalRecords`) and `ApplyOptions`'
  `ReplacePhysicalRecordWith*` helpers routed through the same batch path
  (`RunApplyOptions` drains once after its record loop) — kills the `ApplyOptions` O(n²).
- `RebuildPhysicalRecordOrdinals` sorts record-id `Long`s in place instead of `OrderBy` over
  the record structs.

Semantics unchanged across all four modes (RefCount / Scan × checkpoint / none); span-freeing
now matches Scan mode's existing end-of-edit timing. Measured: one `Remove` freeing 4,930
records went from seconds-class to **5 ms**. Resolves the TODO "batch the removals" item.
Tests +1 (`LargeRangeRemovalBatchesPhysicalRecordReclaims`), suite 278 → 282.

## 2026-09-02

### Defragment(Move) corrupted a heavily-churned file — DONE
A real `Test.efs` (~250 MB, heavy EFS churn) threw
`InvalidDataException("Defrag data end is beyond the backing stream length.")` mid-defragment
and then would not reopen (`Open` rejected both headers: index offset beyond end of stream).

Root cause: the cached `_PhysicalDataEnd` had drifted ~415 KB above the real end of the live
data (D6 — it only ratchets down in `UpdatePhysicalRecordLocationIndexes` when the record at
the very end is the one that moves, and the full recompute counted `RefCount = 0` records).
`TrimAndCommitDefragMetadata` trusted that cache in a hard guard, so once one pass had trimmed
the backing stream a later pass threw against it. `DefragmentCore` had no `Catch` and never set
`_Faulted`, so `DisposeCore` then published the half-defragmented state — with `_IndexOffset`
past EOF — over the last good header generation.

Fix:
- `RecalculatePhysicalDataEnd` / `RebuildPhysicalRecordOrdinals` / `AddPhysicalRecordToIndexes`
  now count only `RefCount > 0` records, so the cached end means "end of live data" (D6, first
  half).
- `DefragmentCore` recomputes `_PhysicalDataEnd` once up front (after the unreferenced-record
  sweep), and `TrimAndCommitDefragMetadata` recomputes it before its guard — the guard now
  only fires on a live record genuinely past the end of the stream. `CommitDefragCheckpoint`
  / `TrimAndCommitDefragMetadata` lost their now-redundant `DataEnd` parameter.
- `DefragmentCore` and `ApplyOptionsCore` wrap their body in `Catch : _Faulted = True : Throw`
  (matching the mutation cores). A defragment / ApplyOptions that fails partway now faults the
  stream, so `DisposeCore` keeps the last durable generation and the caller can reopen the
  file exactly as it was. `ApplyOptionsCore`'s body moved to `RunApplyOptions` for the wrap.
- `DefragmentCore`'s `Finally` skips `BuildFreeSpaceMapCore` on the fault path (it would build
  from inconsistent state and could mask the original failure).
Tests: `Physical layout operations/Defragmentation.vb` (+2), suite 271 → 273. New
`Debug_CorruptCachedPhysicalDataEnd` seam. Not covered: the organic churn-scale drift itself
(needs the EFS workload) — see TODO D6 for the `Validate()` invariant that would catch it.

### AES-CTR keystream is now batched — DONE
`CryptPayload` (`Crypto.vb`) used to reassign `Aes.Key`, call `CreateEncryptor()`, and issue
one 16-byte `TransformBlock` per block (~4096 interop calls per 64 KB chunk), XORing
byte-by-byte.
Now: the cached AES-ECB encryptor (`_ChunkCipherTransform`, rebuilt only in
`DeriveFileMasterKeys` / `RemoveUnusedFileMasterKeyIfPossible`) encrypts a whole buffer of
successive big-endian counter blocks in one `TransformBlock` into `_CtrKeyStreamScratch`,
then that is XORed into the output. Byte-for-byte identical keystream, so existing encrypted
records still decrypt (verified by the encryption round-trip / reopen / migration / defrag
tests). `_KeyStream` field removed; `_CtrCounterScratch` / `_CtrKeyStreamScratch` grow to the
largest payload and are reused. `CryptPayload`'s dead `Key` parameter dropped.
`IncrementCounter` gained a `(Buffer, Offset)` overload.

### `Options.CompressionEvaluation` (Always | Sampled) — DONE
Previously every chunk with a compression method set was fully compressed even when the
result lost to `CompressionRatioThreshold` and was discarded — full compression cost for
incompressible data, for nothing.
`Sampled` compresses only a leading sample (`CompressionSampleBytes` = 8 KB, chunks
≥ `CompressionSampleMinimumChunkBytes` = 24 KB only). If the sample fails the threshold the
full compression is skipped, the chunk is stored as plaintext, its evaluation is recorded as
the sample estimate, and `ChunkFlags.CompressionEstimated` (new, `1 << 1`) is set. Otherwise
the full chunk is compressed and evaluated exactly as `Always`.
`ApplyOptions(Compression)` treats an estimated chunk as always needing a rewrite, and its
rewrite path (`ReplacePhysicalRecordWithNewRecord` → `WritePhysicalRecordWithPolicy(...,
EvaluateFully:=True)`) forces a full evaluation, so after ApplyOptions no estimated chunk
remains and the "chunks can be re-applied from their stored evaluation" property is restored.
Default is `Always` (unchanged behaviour). Diagnostics expose
`ChunkedStreamStructure...IsCompressionEvaluationEstimated`.
Tests: `Storage representation policies/Compression.vb` (+2), suite 269 → 271.

### Async support — DONE
`ChunkedStream` gained a full asynchronous API on .NET Framework 4.8 / VB (no new deps).

**Truly async end to end** (awaited call yields the thread during backing-store I/O):
`ReadAsync` / `WriteAsync` (Stream overrides + positional `(LogicalOffset,…)` + anchor-relative),
`FlushAsync`, `ToArrayAsync` (×2), `SetLengthAsync`,
`ReplaceAsync` / `InsertAsync` / `RemoveAsync` / `ClearAsync` / `CloneAsync` / `CloneInsertAsync` /
`InsertNullBytesAsync` (+ anchor-relative forms), `CreateAnchorAsync` (×2).
New optional `IPositionedStreamAsync` backing-store contract (`ReadAtAsync` / `WriteAtAsync`);
`ChunkedStream` prefers it, else falls back to `Stream.ReadAsync` / `WriteAsync`.

**Design:**
- Read path — hand-written sync + async twins (`ReadCoreAsync` / `ReadExtentBytesAsync` /
  `ReadPhysicalRecordPlainAsync`); the existing synchronous read chain is untouched, so no
  allocation regression on bulk reads.
- Write / metadata-publish / mutation spine — one flag-driven body per method
  (`…Async(…, RunAsync As Boolean, CancellationToken)`), with a thin synchronous bridge
  (`Xxx(…)` → `XxxAsync(…, RunAsync:=False, Nothing).GetAwaiter().GetResult()`). Safe because
  with `RunAsync:=False` no await ever suspends (the three `*EitherAsync` dispatch helpers
  return already-completed tasks), so the body runs straight through and `GetResult()`
  rethrows the original exception unwrapped. Single source of truth for the crash-safety
  ordering.
- State lock — `SemaphoreSlim.WaitAsync`; the `AsyncLocal` reentrancy depth is written in the
  method that owns the `Try/Finally` (`RunUnderStateLockAsync`), never in an awaited helper
  (an awaited async method's `ExecutionContext` mutations do not flow back to its caller).

**Async by worker-thread offload** (documented; one-shot / long-running batch ops, matching
the pre-existing `ValidateAsync` etc.): `OpenAsync` (×2), `DefragmentAsync`, `ApplyOptionsAsync`
(both bridge a `CancellationToken` into the operation's progress cancellation token and return
the cancelled result rather than throwing), `CreateCheckpointAsync`,
`ChunkedStreamCheckpoint.CommitAsync` / `RollbackAsync` / `CloseAsync`,
`DeferPublishScope.PublishAsync` / `CloseAsync`, `DeferPublishAsync`. .NET Framework 4.8 has no
`IAsyncDisposable`, so the checkpoint / scope docs tell async callers to call `CloseAsync`
explicitly; `Dispose()` stays for sync callers and as a safety net.

Tests: `_Tests/UnitTests/Tests/Chunked Stream/Core stream semantics/Async.vb` (+8), suite
261 → 269. `PositionedStream.vb` test double implements `IPositionedStreamAsync`.

Follow-ups in TODO.md (make the offloaded methods truly async; crypto/compression throughput).

---

## 2026-08-30

### C2-a — non-sparse Clear / InsertNullBytes now batch into one publish — FIXED
`ClearCore` / `InsertNullBytesCore` (`_ChunkedStream.vb`), non-sparse path only, looped over
`WriteCore` / `InsertCore` and each iteration published (`PersistIndexAndHeader`) — an N-chunk
`Clear` was N durable publishes, and an interruption part way through left the front of the
range cleared and persisted while the rest was not.
Fix: the non-sparse loop now runs inside a single `DeferPublish` scope
(`If(MetadataPublishSuspended, Nothing, DeferPublish())` — no inner scope when a checkpoint or
an outer scope is already the boundary), `Scope.Publish()` after the loop, `Scope.Dispose()`
in a `Finally`. One published generation; an interruption reopens all-or-nothing. The methods
were split by branch (sparse path keeps its own `Try … Catch : _Faulted = True : Throw` and
returns early; the non-sparse path drops it — `WriteCore` / `InsertCore` already fault
themselves, and the scope's rollback clears `_Faulted` last). `InvalidateChunkCache()` hoisted
above the branch.
Tests (`Correctness and survival/DeferredPublish.vb`, +4, suite 246 → 250):
`NonSparseClearFoldsPerChunkWritesIntoOneMetadataPublish`,
`NonSparseClearInterruptedWhilePublishingReopensToAKnownState` (new `FailingMemoryStream`
double),
`NonSparseClearInsideACheckpointRollsBackAndCommitsAtomically`,
`NonSparseInsertNullBytesInsideOuterDeferPublishScopeRollsBackWhenAbandoned`.
Note: for all-zero data the loop writes no physical records (zeros → sparse extents), so the
only backing I/O is the batched publish; a persistent I/O failure *during* that publish is
completed by `EndDeferPublish` on scope close rather than rolled back (pre-existing
`HasUnpublishedWork` behaviour — TODO DeferPublish-c).

### C2-b — `ApplyOptions` / `Defragment` now check `_Faulted` — FIXED
`ApplyOptionsCore` (`Options.vb`) and `DefragmentCore` (`Defrag.vb`) gated on
`_DeferPublishDepth` / an open checkpoint but not the fault flag, so on an already-faulted
stream their own snapshot-and-restore captured the half-mutated state as the "original". Added
`ThrowIfFaulted()` at entry to both — a refusal, they don't need the C2 trap. Tests:
`ApplyOptionsIsRejectedOnAFaultedStream` (PolicyMigration.vb),
`DefragmentIsRejectedOnAFaultedStream` (Defragmentation.vb), backed by a shared
`Tests.Helpers.FailingMemoryStream` + `CreateFaultedChunkedStream` (the `FailingMemoryStream`
added for C2-a moved out of DeferredPublish.vb into Helpers.vb).

### D4a — `EmbeddedFileSystem.Dispose` doc corrected — FIXED
The XML summary said it "optionally disposes the backing ChunkedStream"; it never touches the
backing stream (caller-owned by design). Reworded to say so and to note it throws while any
file stream opened from the file system is still open.

### D4b — `ApplyOptions` no longer stops after the chunk-size rewrite — FIXED
`ApplyOptionsCore` returned immediately after `ApplyChunkSizeOptions`, skipping the
Sparseness / Compression / Encryption passes in that call. It now falls through: the
mid-operation `PersistIndexAndHeader` and the duplicate `RemoveUnusedFileMasterKeyIfPossible`
are gone, a `ChunkSizeRewritten` flag feeds the single tail publish, and the record loop runs
on the rebuilt records. No behavioural change in normal use — the chunk-size rewrite already
rebuilds every extent with the current policy — but the API is now honest and one call
genuinely processes every selected category. `ApplyOptions(ChunkSize)` chunk-size rewrite has
no recovery journal (only `Defragment(Rebuild)` does), so the shift from a mid-publish to one
atomic tail publish keeps it consistent with the rest of `ApplyOptions`. Test:
`ApplyOptionsAppliesRemainingCategoriesAfterAChunkSizeRewrite` (asserts
`ExaminedChunks > ChunkSizeChanges`, i.e. the record loop ran).
Suite 250 → 253.

---

## 2026-08-28 — review follow-through

### C1 — anchor ids were reused after a checkpoint / DeferPublish rollback — FIXED
`Checkpoints.vb` `RestoreCheckpointState` set `_NextAnchorId` / `_NextPhysicalRecordId` back
to the captured (lower) value, so an anchor id issued inside a rolled-back checkpoint could
be handed out again — breaking "never reused" and reviving a stale `Anchor` handle onto
unrelated data. Now clamped to `Math.Max(current, captured)` so the allocators only move
forward. Tests: `AnchorIdIsNotReusedAfterCheckpointRollback`,
`AnchorIdIsNotReusedAfterDeferPublishRollback` (Anchors.vb).

### C2 — mid-operation exception atomicity — FIXED (commit 3846f10)
Core edits mutated in-memory state and wrote physical records incrementally, persisting only
at the end; a caught-and-continued I/O throw left half-applied state that a later op — or
`Dispose` — would persist. Now: a `_Faulted` flag set in a `Catch` around each core mutation
body (after the arg guards); `ThrowIfFaulted()` gates every mutating op and the
`LogicalOffset` `ReadCore`; `DisposeCore` no longer persists when faulted;
`RestoreCheckpointState` clears the flag so a rollback / checkpoint close is a working
in-process recovery. `ApplyOptions` / `Defragment` / key-rewrap left ungated (own recovery
journal — but see TODO C2-b).

### C4 — LZ4 `ReadExtendedLength` overflow — FIXED
`Length += Value` had no cap → `OverflowException` (VB checked arithmetic) on an unterminated
run of 0xFF continuation bytes, instead of the `InvalidDataException` every other
malformed-block path throws. Now accumulates in a `Long` and throws `InvalidDataException`
above `Integer.MaxValue`. Tests: `CompressionCodecs.vb` (round-trips, truncation sweeps,
random garbage, and an explicit unterminated extended-length run).

### C6 — physical records freed inside a checkpoint were leaked — FIXED (commits 90cd524 / f11560f)
Found via `_Samples/.../bin/Debug/Test.efs` (370 MB, ~11 MB logical, 98% fragmentation,
un-defraggable; `"Test - Cant be defragged.efs"` kept as the repro). It held ~4,389
`PhysicalRecordEntry` rows with `RefCount = 0` that no extent referenced.
Root cause: a record freed during a checkpoint went to `_PendingReclaimedPhysicalRecords`;
`ReclaimPendingPhysicalRecords` bailed while any checkpoint was open, but commit does not pop
the checkpoint, so it never ran — then `RestoreCheckpointState` (rollback **and**
dispose-of-a-committed-checkpoint) discarded the list. Every
`CreateCheckpoint → edit → Commit → Dispose` leaked what the edit freed, and EFS wrapped
almost every mutation in a checkpoint.
Fix: guard is now `_CheckpointStack.Count > 1`; `CommitCheckpointCore` reclaims **before** the
durable publish and baseline capture; `RestoreCheckpointState` rebuilds the pending set from
restored records with `RefCount ≤ 0` instead of discarding. `Defragment` also sweeps
unreferenced records first (backstop + cleans pre-existing bad files). Tests:
`DefragmentationReclaimsUnreferencedPhysicalRecords`,
`CheckpointCommitReclaimsFreedPhysicalRecords`,
`NestedCheckpointCommitsReclaimFreedPhysicalRecords`,
`OuterRollbackRestoresRecordsFreedByCommittedInnerCheckpoint`.

### C7 — in-checkpoint chunk writes now reuse free holes — SHIPPED (partial)
1. `GetNextWriteOffset` no longer force-appends chunk records while a checkpoint is open —
   they run the normal `BestFit` / `FirstFit` hole search. (The `*Scan` rebuild stays
   disabled mid-checkpoint — it would count a checkpoint-deleted record as free.) Safe: a
   rollback / crash reloads the pre-checkpoint durable generation, which does not reference
   the hole, so the bytes are harmless free space; a commit publishes metadata that makes
   them live.
2. `FreeSpaceAllocator.TryAllocate` gains a `MinOffset`; in-checkpoint metadata is confined
   to holes ≥ the outermost checkpoint mark so small metadata pages don't fragment the big
   pre-checkpoint holes.
3. `Checkpoint.State` is captured **after** `WriteCheckpointRecoveryState` in
   create/commit/rollback so the deferred span that header rotation releases survives the
   close-time `RestoreCheckpointState`.
Verified: a 2 MB write into a freed 2 MB hole inside a checkpoint grows the file ~23 KB
(metadata) instead of 2 MB. Tests: `CheckpointChunkWriteReusesFreeHole`,
`CheckpointHoleFillingWriteRolledBackRestoresState`,
`CheckpointHoleFillingWriteAbandonedRecoversToBaseline` (Transactions.vb),
`CheckpointChunkAllocationPreservesDataUnderEveryPolicy` (Allocation.vb).
The EFS create/delete-cycle growth (metadata page churn, not chunk placement) is **not**
solved by this — see TODO C7. It is sidestepped by removing checkpoints from EFS (C8).

### C8 — `DeferPublish` — DONE
`ChunkedStream.DeferPublish() As DeferPublishScope` — reference-counted (not stacked); only
the outermost scope captures a `CheckpointState` snapshot and owns the outcome.
`Scope.Publish()` → `PersistIndexAndHeader` now + re-baseline the snapshot (nested `Publish()`
ignored). Disposing the outermost scope **without** `Publish()` → `RestoreCheckpointState`
(tables rolled back, window records truncated, `_Faulted` cleared, stream stays **usable**).
A clean `Publish()` with nothing dirtied since does no work — which is what keeps a burst of
small ops from bloating the file. Difference from a checkpoint: no per-op recovery-state
header; the published-close path is a no-op. `Flush()` mid-scope publishes durably and
re-baselines. `DisposeCore` rolls back a leaked unpublished scope. `Defragment` /
`ApplyOptions` / `CreateCheckpoint` all reject while a scope is open. A scope opened inside a
checkpoint is inert. `DeferredPublish.vb` + tests (`Correctness and survival/DeferredPublish.vb`).

**EFS side:** `CreateDirectory` / `CreateFile` / `DeleteCore` / `SetFileLength` /
`WriteFile(grow)` / `RecoverPendingFiles` each run in one `DeferPublish` scope; recursion
nests reference-counted scopes so a whole recursive delete is one snapshot and one publish.
`FileStreamView` holds no long-lived scope — each ~4 MB buffer drain self-publishes.
**No checkpoints left in EmbeddedFileSystem.** Benchmark (425 files / 40 dirs / 51.7 MB): file
55.7 MB / 1.7% frag after populate; delete-half then recreate ~27 MB grows the file only
~1 MB (deleted holes reused), frag 9%. Crash tests (kill mid-batch, no dispose): reopen
clean; each `CreateFile` is its own published unit; `Flush` is a real durable point;
`RecoverPendingFiles` finalizes to the flushed geometry. (Tier 2 pending-file length recovery
still open — TODO C8.)

### D3 — EFS recursive-delete checkpoint nesting — ADDRESSED
`DeleteCore` opened one full-snapshot checkpoint per directory level (O(depth) memory blow-up
plus deep recursion). It now uses a single reference-counted `DeferPublish` scope for the
whole recursive delete. (`RecoverPending` still recurses — TODO D3.)

### D5 — Defragment(Move) got stuck, grew the file, needed several calls — FIXED (commits 128f99a / 02646a1)
On a stream large enough to persist the hole directory (`HoleDirectoryMode = Auto`, ≥ 1 MB),
**every** durable publish rebuilt and relocated every hole-directory page → root descriptors
changed → the root re-appended at `Math.Max(BaseStream.Length, GetDataEndFromIndex())` (the
root allocator, alone among metadata pages, never consulted `_FreeSpaces`). The file grew
~19 KB per publish, monotonic, forever; and `Defragment(Move)` needed several external
re-runs to fully compact (each pass fills only the holes it can see; the post-pass metadata
compaction frees fresh ones), leaving a phantom "Saved: *n*" residual.
Fix, three parts: (1) `TryAllocateSnugMetadataRootHole` — the root now reuses a freed hole it
nearly fills (waste capped at the root length, so it can't shatter a data hole), backed by a
new `FreeSpaceAllocator.TryAllocate(…, MaxWaste, …)` overload; (2) `WriteHoleDirectoryPages`
leaves a byte-identical page where it sits (MAC compare, mirrors the root's `RootChanged`
guard) instead of chasing its own tail; (3) `DefragmentMove` is wrapped in an outer fixpoint
`Do…Loop` that re-runs passes until one relocates nothing — terminates because every
productive pass strictly lowers the total live-record offset.
Verified: **one** `Defragment(Move)` call on the real 292 MB `Test - defrag stuck.efs`
(7.8 s), then calls 2–6 report `saved = 0` and the length is a strict fixed point;
`Validate()` clean. Tests: `RepeatedDefragmentationReachesAFixedPoint` (strengthened to
assert `saved == 0` + strict fixed point + no growth), `DefragmentMoveConvergesInOneCall`
(new). The per-move O(n) table copies, per-move fsyncs, and the fragmentation-ratio progress
metric are unchanged — TODO D5 (residual).

### D7 — in-checkpoint chunk writes reuse free holes — FIXED (commit eac5ccd, with C7)
Filed as "accepted (append past EOF is fine)" with a note that a prototype had been reverted;
that note was stale. `eac5ccd` shipped the mechanism and was **not** reverted. In
`GetNextWriteOffset` → `TryAllocateSafeSpace`, `MinOffset` is raised to the outermost
checkpoint mark only when `IsMetadata`; a chunk record keeps `MinOffset = 0`, so inside an
open checkpoint it runs the normal `BestFit` / `FirstFit` search with no lower bound and will
land in a freed hole **below** the checkpoint mark — anywhere in the pre-checkpoint data
area — whenever one fits. Only in-checkpoint metadata stays in the scratch region above the
mark. Crash-safety: a rollback / crash reloads the pre-checkpoint durable generation, which
never referenced the hole, so the bytes are unreferenced free space; a commit publishes
metadata that makes them live. This promotes `_FreeSpaces` from "optimisation only" to
correctness-critical inside a checkpoint / `DeferPublish` window. Tests:
`CheckpointChunkWriteReusesFreeHole`, `CheckpointHoleFillingWriteRolledBackRestoresState`,
`CheckpointHoleFillingWriteAbandonedRecoversToBaseline`,
`CheckpointChunkAllocationPreservesDataUnderEveryPolicy`.

### Test dedup — done
Removed the 8 header-copy / physical-record-move-recovery tests `Hardening.vb` duplicated
verbatim from `Recovery.vb` (kept in `Recovery.vb` — the better home, with the superset incl.
the `GetFirstAllocatedChunk` helper and `…FailsWhenBothRecordsInvalid`). Moved
`MetadataPagingManyExtentPagesSurviveReopenAndDefrag` +
`MetadataPagingHoleDirectorySurvivesReopenAndReuse` to `Durability.vb`'s "Metadata durability"
section, replacing the weaker `MetadataPagingSurvivesReopen` / `HoleDirectorySurvivesReopen`
they subsume. `Hardening.vb` now holds only `ModelBasedRandomOperationsMatchByteArrayModel` +
its fuzz helpers. Dead helpers removed. Net −8 tests.

### Housekeeping — done
- Deleted `__Tests/` (double underscore) — stale `obj/` build output, not in the solution.
- Removed the ~200 lines of commented-out old implementation at the end of
  `Structure.Extensions.vb`.
- Removed the `#If DEBUG` guard around the friend test hooks (`Unit Test Helpers.vb`) — they
  compile in every configuration now.

### Coverage-gap tests — +37 tests (suite 207 → 244, whole run ~10 s)
Test folders reorganised under `_Tests/UnitTests/Tests/Chunked Stream/` and
`_Tests/UnitTests/Tests/Embedded File System/`; `UnitTests.vbproj` follows.
- **C1 / C4** regression tests (above).
- **Non-LIFO / disposed checkpoint:** `CheckpointOperationsOutOfLifoOrderThrow`,
  `DisposedCheckpointCannotBeCommittedOrRolledBack` (Transactions.vb).
- **EmbeddedFileSystem** — three new files (`Basics` / `FileStreams` / `Recovery`, class
  `Tests.EmbeddedFileSystemTests.*`): directory & file CRUD, case-insensitive name
  collisions, recursive delete of a 25-level tree, `FileStreamView` read / write / seek /
  `SetLength`, the PendingFile bracket around an open stream, `RecoverPendingFiles` in both
  modes, deep nesting, and a create/delete churn that asserts the backing store stays under
  512 KB.
- **`IPositionedStream`** — `PositionedStream.vb`: a `PositionedMemoryStream` test double with
  a configurable capability set, asserting `ReadAt` / `WriteAt` are actually used and that
  round-trip / reopen / compression / defrag all work through the positioned path.
- **Read-only backing stream:** `ReadOnlyBackingStreamOpensForReadingOnly` (Foundation.vb).
- **Concurrency:** `Concurrency.vb` — concurrent readers see consistent data; readers racing
  a writer never see a torn snapshot.
- **Real `FileStream` + tail truncation:** `CrashSafety.vb` — a temp-file round-trip through
  the durable `Flush(True)` path, plus a swept-tail-truncation harness ("known state or clean
  throw, never garbage").
- **Master-key removal:** `EncryptionRemovalClearsWrappedMasterKey` (PolicyMigration.vb) —
  asserts `GetStructure().HasWrappedFileMasterKey` is false and the old key is rejected on
  reopen.
- **DeferPublish hole-reuse rollback:** `DeferPublishHoleFillingWriteAbandonedDoesNotOverlapLiveRecords`
  (DeferredPublish.vb).
- **Fuzzer breadth:** the model fuzzer now also runs `Replace` / `Clear` / `InsertNullBytes`
  and a `FuzzDeferPublishBurst`.

### From `todo chunked stream.txt` — "test opening read-only underlying streams"
Covered: `ReadOnlyBackingStreamOpensForReadingOnly` (normal open),
`CrashRecoveryCheckpointActiveRequiresWritableStream` (recovery-pending, read-only rejected),
`CrashRecoveryFailureCanBeOpenedForDiagnostics` (`AllowOpeningWhenRecoveryFails` diagnostic
open).

---

## Earlier (context — see git history)

These landed before the review and are noted here only so the txt-file history isn't lost:
FirstFit write-location policies; the durable-flush `Open()` parameter; `PersistPagedMetadata`
skips a no-op root rewrite; `UpdateHeader` reuses `_MetadataRootMac` instead of re-reading;
deferred freed-space reuse held until a durable publish (crash-corruption fix);
`Options.ExtentReclaimTypes` made functional; defrag Move/Sequence unreferenced-record
reclamation.
