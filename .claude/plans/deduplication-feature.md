# Content-Addressed Deduplication — Design & Implementation Plan (written retrospectively)

**Status: shipped, plus two production-incident fixes.** This documents the deduplication feature
as designed and built across nine commits (`0b1c7dd` → `d45c172`), 403/403 tests green. It's
written after the fact, in plan form, because the feature was built incrementally through
conversation rather than from an upfront written plan — this is that plan, reconstructed for the
record, and kept up to date as real-world use turned up first a genuine bug in what §6 originally
called an accepted limitation (§2.2's `c08fbf1` follow-up, §4 stage 8), then a second incident
where a *symptom* of an as-yet-unfound bug (a torn dedup index) was itself refusing to let the
archive open at all (§2.6's `d45c172` follow-up, §4 stage 9).

## 1. Goal

Add content-addressed deduplication to `ChunkedStream`: when two physical chunk records hold
identical plaintext, store the bytes once and let every logical reference point at the same
record. Off by default (`Options.Deduplication`); opt-in, additive, and never a source of truth —
if the index is ever lost or wrong, the stream still opens and reads correctly, just without the
space saving.

This was staged as the second half of a two-part effort. Part one, content-defined chunking
(Gear-hash rolling splitter, shipped separately, 336/336 green), was explicitly built first so
that identical byte runs chunk identically wherever they occur — a precondition for chunk-level
dedup to ever find a match in the first place.
Deduplication itself does not depend on CDC being enabled (`ChunkSizeVariance = 0`, i.e. plain
fixed-size chunking, works with dedup too); CDC just makes matches more likely across writes whose
surrounding context differs.

## 2. Key design decisions

### 2.1 Dedup key: `PhysicalRecordId`, not a new identifier

The codebase's extents describe logical spans and have no stable identity of their own (an
overwrite or defrag can freely replace, split, or merge them). `PhysicalRecordId` is the one
identifier in the system that is stable, monotonically increasing, and never reused
(`AllocatePhysicalRecordId`). Deduplication targets it directly: an index entry says "this hash
of plaintext lives at this `PhysicalRecordId`," and any extent can be pointed at that id.

### 2.2 Index structure: extendible hashing

Considered a B-tree, a fixed-bucket-count hash table, and extendible hashing. Settled on
**extendible hashing**: a directory of bucket pointers that grows by splitting one overflowing
bucket at a time (using one more bit of the key) plus occasional cheap directory doubling — unlike
`hash mod N`, which needs a full rehash of every entry when N changes. This gives O(1) lookup that
scales the same way the existing extent/physical-record index pages already do, without a fixed
bucket count decided once at creation time.

Built as a **pure, self-contained algorithm** (`ExtendibleHashTable`, `Deduplication.vb`) with zero
coupling to persistence or crypto, verified by its own test suite before it touched anything else.
This caught a real bit-ordering bug early: `TopBits` (which packs key bits into a directory index)
and `DoubleDirectory`'s "copy low half into high half" doubling scheme require key-bit-0 to be the
directory index's *lowest*-order bit — these were initially inconsistent, causing an infinite
split/double loop (`OutOfMemoryException`) that only showed up under the stress test. Fixed by
tracing the bit arithmetic by hand rather than guessing. A `MaxGlobalDepth = 24` circuit breaker
also replaced a naive "stop at 256 bits" threshold, which OOM'd instead of failing cleanly (the
directory *doubles in size* every step, so 256 levels is astronomically past any real memory
budget).

**Follow-up, `c08fbf1` — a production crash traced back to `Insert`'s own contract.** Reported
live: `EmbeddedFileSystemSample` with `Options.Deduplication = True` threw
`InvalidOperationException: Extendible hash table directory depth exceeded its safety limit - this
points at duplicate or non-random keys, not normal growth.` mid-upload, on a real 256MB archive.
`Insert`'s doc comment required the caller to have already ruled out an existing entry via
`TryGetValue` — reasonable in isolation, except a "miss" from the write path's own
`TryDeduplicateWriteAsync` is ambiguous: it means either no entry for this key ever existed, *or*
an entry exists but points at a physical record reclaimed since (§6 originally filed this as an
accepted, harmless limitation — "a stale entry can shadow a fresher live entry"). The write path
treated both misses identically and called `Insert` unconditionally, so the stale case planted a
**second** entry for an already-present key. Because the key is a content hash and "the same small
set of distinct contents gets reclaimed and rewritten at one offset many times" is ordinary upload
churn, the same key recurs indefinitely — and, exactly like the bit-ordering bug above, a bucket
holding several entries that all share one identical key can *never* be split apart no matter how
many more bits are examined, so it grew the directory forever and tripped `MaxGlobalDepth`.

Reproduced first with a minimal, targeted repro (write X twice, reclaim it, rewrite X again,
watch the index gain a *second* entry for X's own hash) before writing any fix, then confirmed
against a copy of the actual reported archive. Fixed by making `Insert` an upsert: it now scans
its target bucket for an existing entry with the same key before appending, and updates the value
in place if found. Unconditionally safe (`TryGetValue` only ever returns the first entry it finds
for a key anyway, so two entries for one key were never useful even before this bug), and it
permanently closes the "stale entry shadows a fresher live entry" limitation §6 originally
accepted — see §4 stage 8 for the fix's own tests, and §6 for the corrected status of that
limitation.

### 2.3 Entry format: HMAC-SHA256(plaintext) → PhysicalRecordId, no stored MAC

Each entry is 32 bytes of HMAC-SHA256 over the chunk's *plaintext* (not ciphertext — dedup has to
work regardless of whether the stream is encrypted, and matching on ciphertext would never find a
match since IVs differ per write) plus the 8-byte target `PhysicalRecordId`.

Two things this format deliberately does **not** do:

- **No stored MAC per entry.** An earlier idea was to also store the target record's own MAC in
  the index, to double-check integrity. Rejected: a valid ciphertext MAC only proves the bytes are
  well-formed, not that they decrypt to the plaintext the index claims. That's "necessary but not
  sufficient" for a real safety guarantee, so it wasn't worth the extra 32 bytes per entry.
- **Real verification is decrypt-and-compare, always.** Every dedup decision — write-path or
  catch-up — treats an index hit as a *hint*, then decrypts the candidate record and compares its
  plaintext byte-for-byte against the new data before committing to the match. The index can never
  cause a wrong dedup on its own, even if it were tampered with or corrupted.

### 2.4 The hash must be keyed (HMAC, not plain SHA-256)

Plain `SHA256(plaintext)` in the index would let anyone with the archive file — no decryption
capability needed — run an offline dictionary attack: hash a known file, check if that hash
appears in the index, and thereby learn "yes, this archive contains file X" without ever
decrypting anything. Keying the hash with a per-archive secret closes this.

The naive fix, `SHA256(key ‖ plaintext)`, was considered and rejected: SHA-256's Merkle–Damgård
construction makes `H(key ‖ message)` vulnerable to a length-extension attack (an attacker who
knows `H(key ‖ M)` can compute `H(key ‖ M ‖ pad ‖ M')` for a chosen `M'`, without knowing `key`).
**HMAC-SHA256 exists specifically to avoid this** and was used instead.

### 2.5 A separate, dedicated dedup key — not a reuse of the file master key

Confirmed via code inspection that the file master key is *not* always present: it's absent on a
freshly created unencrypted archive, and is explicitly cleared by
`RemoveUnusedFileMasterKeyIfPossible` once encryption is removed and no chunk still needs it. A
dedup index built against a key that can disappear would silently go dark. So the dedup key
(`_DedupKey`) is its own, independently wrapped secret:

- **Lazily generated** the first time deduplication is genuinely used (first hash computed) — a
  stream that never touches dedup never has one on disk.
- **Wrapped the same way** the file master key is (`PublicWrap` when unencrypted, `UserWrap`
  against the current `EncryptionInfo` otherwise) — recoverable exactly as strongly as the
  archive's own data, no weaker or stronger. The wrap/unwrap logic itself was refactored into a
  shared `WrapKeyMaterial`/`TryUnwrapKeyMaterial` primitive so both keys go through one tested
  implementation instead of duplicating it.
- **Stable across encryption toggling.** The key's own *value* never changes just because
  encryption is turned on or off — only its wrapping does, kept in sync via the existing
  `Options_EncryptionInfoChangedCore` handler. Only an explicit `DedupRebuild(Soft:=False)`
  produces a new value.
- **Non-fatal on unwrap failure**, unlike the file master key. A corrupted or unrecoverable wrapped
  dedup key just leaves deduplication unavailable until rebuilt — it never blocks opening an
  otherwise-healthy archive. This matches the index's own "not the source of truth" philosophy.

### 2.6 On-disk index format: flat and rebuildable, not a mirror of the in-memory structure

The first instinct was to persist the extendible hash table's own bucket/directory structure
directly — one page per bucket, promoted to a directory-of-descriptors when there are too many, by
direct analogy with how extent and physical-record pages already work. This was **rejected** once
worked through in detail: it would require persisting each bucket's own identity-prefix bits
(because a bucket can be empty yet still meaningfully addressed, after certain splits) plus a
separate slot→bucket directory array — real structural complexity for something that is explicitly
declared not to be the source of truth.

Instead, the index persists as a **flat, unordered set of entries**, using exactly the same shape
and philosophy as the existing **hole directory**:

- One new `DirectoryTypes.DedupEntryPages` page kind, added to the metadata root's existing
  page-descriptor list.
- Pages are rebuilt in full on every publish that touches the index, with the hole directory's own
  "leave an unchanged page exactly where it sits" MAC-comparison optimization — since entries only
  ever accumulate between rebuilds, most pages stay byte-identical across publishes and this keeps
  republishing cheap.
- On `Open`, the entire `ExtendibleHashTable` is reconstructed by re-inserting every entry read
  back. The in-memory bucket/directory shape is derived fresh every time and never persisted
  directly — fully consistent with "rebuildable from scratch, not a source of truth."
- The new descriptor list's count lives in 4 bytes of the metadata root header that were already
  reserved (offset 60) — no growth of the root's fixed layout was needed.

One direct consequence: only **one** new tunable was needed, `Options.DedupIndexPageEntryCount`
(default 256, mirroring `IndexPageEntryCount`), doing double duty as the in-memory table's bucket
capacity *and* the on-disk page size. A second option, for a directory-of-descriptors promotion
tier (by analogy with `IndexDirectoryEntryCount`), was reserved in the header
(`DedupIndexDirectoryEntryCountOffset`, still unused) but turned out unnecessary once the format
settled on the hole directory's simpler single-tier shape.

**Follow-up, `d45c172` — a torn dedup index page used to refuse Open entirely.** Reported live: a
`KeyNotFoundException` during a `Deduplication` + `CurrentChunkWriteCaching` upload (root cause not
found - see §5's note on this) left the archive's dedup index pages torn on disk, and the *next*
`Open` attempt threw `CryptographicException: Dedup index page MAC invalid` and aborted before
anything else loaded - an otherwise perfectly healthy, fully-readable archive refused to open at
all over damage to one explicitly non-authoritative structure. This directly contradicted §2.5's
own already-established precedent (an unrecoverable dedup *key* is non-fatal), which just hadn't
been extended to the index *pages* themselves. Fixed the same way: `ReadPagedMetadata` now catches
`CryptographicException`/`InvalidDataException` around the dedup-page read and discards the whole
index (starts empty, same as a stream that never used deduplication) rather than propagating the
failure. Deliberately **unconditional**, not gated behind the `Tolerate` flag
`ReadPhysicalRecordPages` uses for its own, narrower class of inconsistency (duplicate entries from
a torn ordinal move) - a strict, first-attempt open must not fail over this one rebuildable
structure regardless of whether the caller is in some special recovery mode. Also resets
`_DedupCoveredUpToRecordId` to 0 when this fires, matching what `DedupRebuild(Soft:=False)` already
does when it discards the whole index for a different reason (key rotation) - otherwise a later
catch-up scan would wrongly believe everything up to the old mark was still indexed. Records an
`AutoRepair("DedupIndex", ...)` so the discard is visible, not silent.

### 2.7 Catch-up coverage: a high-water mark, not an entry-count comparison

The first idea for "does this stream need a dedup catch-up pass" was: compare the index's entry
count to the live physical-record count. **Rejected** on inspection — index entries are never
cleaned up when a record is reclaimed (that's accepted dead weight, cleaned up only by
`DedupRebuild`), so that comparison would desync forever after the very first *unrelated* reclaim
anywhere in the file, long before it had anything to do with dedup coverage.

Replaced with `_DedupCoveredUpToRecordId`: the highest `PhysicalRecordId` ever considered for
deduplication. Because `AllocatePhysicalRecordId` never reuses ids, "id ≤ this mark" is a stable,
O(1) answer to "has this record at least been looked at," immune to reclaims happening elsewhere.

### 2.8 Write-path coverage: two choke points, not one

Initial assumption was that a single function funnels every write. Checked and refined: there are
two low-level places that actually create or replace a physical record, and both needed the hook:

1. **`WritePhysicalRecordWithPolicyAsync`** — used by ordinary serial writes *and* by
   `ReplacePhysicalRecordWithNewRecord` (the function `ApplyOptions` uses for
   Compression/Encryption rewrites and chunk-size splits). One hook here covers both.
2. **`BuildExtentsInParallelAsync`'s own planning loop** — the large-write parallel path bypasses
   (1) entirely for batching/threading reasons, calling `PrepareChunkRecord`/`PlaceChunkRecordsAsync`
   directly. Needed its own, separate hook, applied inline in its existing serial planning pass.

Both call the same pair of shared helpers (`TryDeduplicateWriteAsync` /
`RegisterWrittenRecordForDeduplication`): hash the plaintext, look up the index, verify a hit by
decrypting and comparing, and either reuse the existing record (incrementing its reference count)
or write normally and index the result afterward.

**Follow-up, `dc1412d`: intra-batch duplicates within one parallel write.** The parallel path's
initial cut only caught matches against *already-persisted* content — two identical chunks inside
the same `BuildExtentsInParallelAsync` call couldn't dedupe against each other, because neither is
registered in the index until after the whole batch is placed (see §5, originally listed as a
known limitation). Closed by having the planning loop — which already runs single-threaded, before
any dispatch to the crypto worker pool — track a local `hash → plan index` map as it goes. A later
chunk whose hash matches an earlier one in the *same* batch is verified by comparing its plaintext
directly against that earlier chunk's still-in-memory bytes (no decrypt needed, since nothing has
been placed yet) and, if equal, marked a duplicate of that plan instead of being separately
allocated, prepared, or placed. The persisted index is still checked first for every chunk, so a
genuine cross-write match always takes priority over a same-batch sibling. Once the batch is
placed, a short resolution pass turns each duplicate's plan index into a real `PhysicalRecordId`
(the leader is always either a freshly placed record or itself a cross-write dedup hit — never
another duplicate, since a leader is by construction the first occurrence of its content) and
increments it once per duplicate, on top of the one reference the leader itself already accounts
for. `TryDeduplicateWriteAsync` changed its return shape to a tuple `(Match, Hash)` so this caller
gets the hash it needs without hashing the same plaintext twice — VB doesn't allow `ByRef`
parameters on `Async` functions, so an output parameter wasn't available.

### 2.9 `DedupRebuild(Optional Soft As Boolean = True)`

The maintenance/key-rotation API, with two modes:

- **`Soft:=True`** (default): keep the existing key (its HMAC values are still valid), rebuild the
  index by copying forward only entries whose target record is still live, and drop the rest —
  the cleanup the catch-up scan never does on its own. The high-water mark is deliberately left
  untouched (every record it already covered still has a surviving, correct entry — re-examining
  it would just insert a redundant duplicate of what's already there). A changed
  `DedupIndexPageEntryCount` takes effect here, the same way a changed `IndexPageEntryCount` only
  takes effect at `Defragment(Rebuild)`.
- **`Soft:=False`**: rotate the key (`RegenerateDedupKey`) and discard the whole index — every
  existing hash was computed under the key being replaced, so none of it is reusable. The
  high-water mark resets to 0, forcing every live record to be re-hashed under the new key.

Either mode finishes by running the same catch-up pass `ApplyOptions(Deduplication)` uses, so
anything not already accounted for by the rebuild still gets indexed or deduplicated.

## 3. On-disk format summary

| Item | Location | Notes |
|---|---|---|
| Dedup key wrap area | Outer header, offset 276 (84 bytes) | Same mode/salt/wrapped-key/MAC shape as the file master key's wrap area |
| `DedupEntryCount` | Outer header, offset 360 (8 bytes) | Informational — refreshed from the live table's count on every header update |
| `DedupIndexPageEntryCount` | Outer header, offset 368 (4 bytes) | Fixed the first time the index is created; changes take effect only via `DedupRebuild` |
| `DedupIndexDirectoryEntryCountOffset` | Outer header, offset 372 (4 bytes) | Reserved, unused (see §2.6) |
| `_DedupCoveredUpToRecordId` | Outer header, offset 376 (8 bytes) | The high-water mark (§2.7) |
| Dedup index pages | Metadata root's page-descriptor list, `DirectoryTypes.DedupEntryPages` | Flat entry pages, hole-directory-shaped (§2.6) |
| Page descriptor count | Metadata root header, offset 60 (4 bytes) | Previously-reserved bytes, no root growth needed |

All of these offsets were re-verified against the *entire* live header layout before being
claimed, not assumed from memory or a partial grep — an earlier mistake in the CDC phase
(`ChunkSizeVarianceOffset` initially collided with `MetadataRootLengthOffset` because of a
truncated search) made this a standing rule for the rest of the work.

## 4. Implementation stages (as shipped)

1. **`0b1c7dd`** — `ExtendibleHashTable` core algorithm, tested in complete isolation (§2.2).
2. **`f1ccb9c`** — Dedup key lifecycle: lazy generation, wrap/unwrap, encryption-toggle stability,
   header fields for the key and the small addressing scalars (§2.5, §3).
3. **`23fe99e`** — On-disk index format: page build/write/read, metadata root wiring, index
   reconstruction on `Open` (§2.6).
4. **`4e32c9b`** — Write-path hook: dedup actually happens on live writes now, both choke points
   (§2.8). Surfaced and fixed a real reference-count bug in `ReplacePhysicalRecordWithNewRecord`
   (it used to blindly overwrite a record's ref count on the assumption the record was always
   freshly created and exclusively owned — no longer true once a "new" record can be an existing,
   already-shared one returned by a dedup hit).
5. **`8fc4571`** — `ApplyOptionTypes.Deduplication` catch-up scan (§2.7), for records written
   before `Options.Deduplication` was turned on.
6. **`8c4c6f4`** — `DedupRebuild` (§2.9), completing the feature as originally scoped.
7. **`dc1412d`** — Intra-batch parallel-write dedup (§2.8 follow-up), closing limitation #1 below.
8. **`c08fbf1`** — Production-incident fix: `ExtendibleHashTable.Insert` is now an upsert (§2.2
   follow-up), closing the "stale entry shadows a fresher live entry" limitation §6 originally
   accepted. The pre-existing `InsertingManyIdenticalKeysThrowsRatherThanHanging` test had
   asserted the *bug itself* as correct behaviour and had to be replaced, not patched: split into
   `InsertingTheSameKeyManyTimesUpdatesItsValueWithoutGrowing` (proves the upsert directly) and
   `InsertingManyKeysSharingALongCommonPrefixThrowsRatherThanHanging` (keeps the real
   `MaxGlobalDepth` safety net under test, using genuinely distinct keys that share a long prefix
   instead of one repeated key - the one case an upsert can't rescue). Added
   `ReclaimingAndRewritingTheSameContentManyTimesDoesNotDuplicateItsDedupEntry` to
   `DedupWritePath.vb`, reproducing the exact crash pattern as a permanent regression guard.
9. **`d45c172`** — Production-incident fix: a torn dedup index page no longer refuses `Open`
   entirely (§2.6 follow-up) - the index is discarded and the archive opens normally, degraded but
   working, exactly the state a stream that never used deduplication would be in. Added
   `Debug_CorruptFirstDedupIndexPage` and `OpeningToleratesACorruptDedupIndexPageAndDegradesGracefully`
   to `DedupIndexPersistence.vb`. The root cause of *why* the index was torn in the first place
   (see §5) was not found in this pass - deliberately deferred at the user's own request, so this
   stage only closes the open-time symptom, not the underlying trigger.

Testing throughout: each stage built, tested in isolation, and committed before the next began.
403 tests pass at completion, up from the pre-feature baseline; every stage's own test file is
still in the tree (`ExtendibleHashTable.vb`, `DedupKeyLifecycle.vb`, `DedupIndexPersistence.vb`,
`DedupWritePath.vb`, `DedupApplyOptions.vb`, `DedupRebuild.vb`, under
`_Tests/UnitTests/Tests/Chunked Stream/Deduplication/`).

## 5. Known, accepted limitations

Limitation 1 below was identified during the initial work and closed in a follow-up (`dc1412d`) —
kept here for the record, since the plan predates the fix. Limitation 2's root cause was later
closed too, by a separate feature (`write-coalescing`) built for its own reasons - kept here
because the specific EFS-level guarantee it implies has not been re-verified end-to-end. Limitation
3 is genuinely open - a real production crash whose trigger has not yet been found:

1. ~~**Intra-batch parallel-write dedup.**~~ **Fixed in `dc1412d`.** Two identical chunks written
   within the *same* `BuildExtentsInParallelAsync` call couldn't dedupe against each other —
   neither was in the index until after the whole batch was placed. (The serial write path never
   had this gap: it registers each segment immediately, before the next one is planned.) Fixed
   exactly as sketched below: the planning loop's existing single-threaded pass now tracks a local
   hash → plan-index map, verifies an intra-batch match by direct in-memory plaintext comparison
   (no decrypt needed), and resolves each match's real `PhysicalRecordId` in a short pass after
   the batch is placed. See §2.8 for the full description as built. Bounded cost (a dictionary
   sized to one batch's chunk count, an in-memory byte compare instead of a decrypt), independent
   of `ChunkSizeVariance`.

2. **EFS write-pattern dependency — root cause fixed by a separate feature, `write-coalescing`,
   not re-verified against actual EFS traffic.** This plan originally described the gap as: two
   EFS files with identical content only dedupe reliably if both were written via the same *shape*
   of `Write()` calls, because `WriteFile` appends via the ordinary `Write()`/`Insert()` path and
   content-defined chunking's rolling hash reset at the start of every `Write()` call, with no
   visibility into where the previous call left off. Candidate fix (a) below - carrying rolling-hash
   state across contiguous `Write()` calls - is exactly what the `write-coalescing` feature (a
   separate, later effort; see `TODO.md`/`DONE.md`, no `.claude/plans` document of its own) ended
   up building, in three stages: unconditional extend-in-place, opt-in
   `Options.CurrentChunkWriteCaching` buffering, and content-defined-chunking awareness (its own
   "stage 3") that specifically carries the Gear-hash accumulator across separate `Write()` calls
   the way (a) below describes. It also closed the fixed-size grid-alignment gap noted at the
   bottom of this limitation, as a side effect of the same mechanism. **Not specifically
   re-verified end-to-end against real EFS `WriteFile` traffic** (write-coalescing's own tests
   exercise `ChunkedStream.Write()` directly) - worth a targeted EFS-level dedup test if cross-file
   dedup reliability matters again. Candidate fix (b) (buffering whole-file writes at the EFS
   layer) was not pursued once (a) was addressed generally.

   *Original discussion, kept for the record:*

   - **(a) General fix — carry rolling-hash state across contiguous `Write()` calls.** Persist the
     Gear-hash accumulator and any buffered, not-yet-boundaried tail bytes, and continue them into
     the next call if it's a logically contiguous append. Makes streamed writes chunk identically
     to one-shot writes in general, not just for EFS. Real durability trade-off, though: a
     `Write()` call would no longer always fully commit everything it received before returning —
     some tail bytes could sit buffered in memory across calls, needing a clear flush trigger
     (explicit `Flush()`/`Dispose()`, or a broken-continuity write) so a crash between two
     "contiguous" writes can't silently lose them. Only relevant when `ChunkSizeVariance > 0` —
     with it at 0 there is no rolling hash to carry, so this fix would do nothing in that mode (see
     the note on fixed-size chunking below). Would need its own dedicated design pass (an explicit
     opt-in streaming mode, and clear rules for what partial state survives `Flush`/`Dispose`)
     before being built.
   - **(b) Narrow fix — buffer whole-file writes at the EFS layer.** Change EFS so writing a file's
     full content issues one `Write()` call instead of several smaller ones, sidestepping the
     cross-call problem entirely rather than solving it in `ChunkedStream`. Confined to the EFS
     layer, no new durability semantics, no change to the core write/CDC path. Recommended as the
     first move: smaller, lower-risk, and matches the specific case that prompted this
     ("create a file, then write to it").

   Separately noted: even at `ChunkSizeVariance = 0` (fixed-size chunking, no rolling hash at all),
   `DetermineNextSegmentLength` still doesn't know a write's logical offset — every `Write()` call
   starts its own boundary count from 0. Two logically contiguous fixed-size writes only produce
   grid-aligned chunks if the first call happened to end on an exact multiple of `ChunkSize`. A fix
   for that case wouldn't need a rolling-hash carry, just a plain integer (bytes remaining to
   complete the previous chunk), which would be far cheaper than (a) — but whether grid-aligning
   fixed-size chunks to an absolute stream position has side effects elsewhere (overwrite
   semantics, defrag, bisection) hasn't been investigated, so it's flagged here rather than
   assumed safe.

3. **OPEN, not yet reproduced — `Deduplication` + `CurrentChunkWriteCaching` together threw
   `KeyNotFoundException` on a real upload (`C:\Windows\System32\mrt.exe`, ~250MB, through
   `EmbeddedFileSystemSample`) and left the dedup index torn on disk (see `d45c172`, stage 9 above,
   which fixes only the resulting open-time refusal, not this).** Deliberately deferred at the
   user's own request rather than guessed at. Ruled out so far: the sample's exact options
   (Lz4 compression, both features on, `MaxCryptoParallelism` etc.) with dedup-friendly repeated
   content through the real `EmbeddedFileSystem`/`FileStreamView` API; a 3000-operation randomized
   fuzz test (sequential appends, mid-file overwrites, explicit flushes, single-call and bursty
   writes) - both ran clean. One real finding from the investigation: with
   `CurrentChunkWriteCaching = True`, a plain append no longer ever reaches the parallel crypto
   path (`BuildExtentsInParallelAsync`) regardless of size - `AppendThroughWriteCacheAsync` now
   absorbs the whole append serially, one chunk at a time. This means the sample's own
   `WriteBufferFlushThreshold` tuning (deliberately set above `ChunkSize * ParallelChunkCryptoMinChunks`
   to reach the parallel path) is now a no-op for appends, though still relevant for overwrites.
   The next root-cause attempt should focus on `AppendThroughWriteCacheAsync`'s loop,
   `CommitPendingChunkAsync`, and their interaction with dedup's reclaim/registration - not the
   parallel path, which write-caching bypasses entirely for appends. Needs the actual crashed file
   (not just a description) to make further progress efficiently.

## 6. Explicitly out of scope

- Any change to how EFS decides *when* to write (this plan only covers what dedup does once bytes
  reach `ChunkedStream`).
- Cleaning up stale index entries automatically on reclaim, in general, remains out of scope: a
  hash whose content is never written again keeps a dead entry sitting in the index forever
  (harmless disk/memory weight, not a correctness risk) until an explicit `DedupRebuild` drops it.
  This is still accepted, unchanged dead weight.
- ~~A stale index entry shadowing a fresher live entry with the same key is a known, accepted
  consequence of never cleaning up on reclaim.~~ **This was not actually harmless - see §2.2's
  `c08fbf1` follow-up.** A stale entry whose content *is* written again used to plant a second,
  duplicate entry for that key rather than being repaired, and repeating that cycle enough times
  crashed the process (`MaxGlobalDepth` exceeded) on real user data. `ExtendibleHashTable.Insert`
  is now an upsert, so this specific case - the same key recurring - self-heals automatically on
  the very next write of that content, no `DedupRebuild` needed. `DedupRebuild(Soft:=True)` is
  still the only way to clean up dead entries whose content never recurs (the bullet above).
