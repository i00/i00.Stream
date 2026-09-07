# ChunkedStream — TODO

Completed items are in [DONE.md](DONE.md).

Item ids (`C*`, `D*`, `S*`) match the audit artifact.

---

## Correctness

### C3 — validate before allocating in the extent builders
`BuildExtentsFromBuffer` / `BuildCloneExtents` write and refcount physical records **before**
the guards that could reject the resulting extents (`ReplaceRangeCore`'s
`InsertLength <> ActualLength`, `RebaseExtentLogicalOffsets`' duplicate-anchor / length
checks). A firing guard would orphan the records. Unreachable in the write path today;
brittle ordering.
**Fix:** move the reachable checks ahead of any `WritePhysicalRecord` call (`InsertLength` is
knowable from the input without materialising records). C2's fault flag backstops the
"shouldn't happen" ones, and C6's defrag sweep now cleans any that slip through.

### C5 — Scan-mode reclaim skips the checkpoint boundary
`ReclaimUnreferencedPhysicalRecordsByScan` bails on `HasOpenCheckpoint` and is never re-run
on commit/close, so a record made unreferenced via a *drifted* count during a checkpoint
leaks until the next non-checkpoint edit triggers a scan. (Narrowed by the C6 fix — the
count-hits-0 case is now covered by the pending mechanism.)
**Fix:** call `ReclaimUnreferencedPhysicalRecordsByScan()` after
`ReclaimPendingPhysicalRecords()` in `CommitCheckpointCore` (outermost branch) and
`CloseCheckpointCore` (stack → 0).

### C7 — remaining EFS-growth levers (in-checkpoint hole reuse is done)
EFS no longer uses checkpoints (see DONE), which sidesteps the original create/delete churn.
If EFS backing-store growth still bites, the levers are:
- **a.** Cut the number of paged-metadata relocations — write an unchanged-size dirty page
  in place once it has aged past `HeaderCopyCount` header rotations; narrow the dirty-page
  set (adding a record currently dirties every later page via ordinal shift).
- **b.** Batch EFS sub-operations under one publish scope (see D3).
- **c.** Give EFS a `Compact()` / auto-`Defragment(Move)`.

### C8 — Tier 2 pending-file length recovery
A pending file abandoned by a crash reports its last flushed `SetEntryLength` value, not the
true byte count. Recover the real length from the anchor geometry
(`nextAnchor.Offset - fileAnchor.Offset - FileHeaderSize`) during `RecoverPendingFiles`.

---

## C2 follow-ups (fault flag shipped; these tighten it)

- **C2-c — dedup.** The `Try … Catch : _Faulted = True : Throw` block is copy-pasted into
  ~13 methods. A zero-alloc `FaultScope` value type used with `Using` (like `StateLockScope`,
  as a struct) removes the duplication.

---

## DeferPublish (C8) follow-ups

- **a — `Publish()` is sticky.** Once Published, closing the scope *flushes* trailing edits
  rather than rolling them back. Reasonable, but the XML doc reads as if any un-Published
  edit rolls back. Tighten the wording.
- **b — `Flush()` inside a scope commits a partial batch** (publishes + re-baselines) so the
  batch stops being all-or-nothing. Consider making `Flush()` a no-op inside a scope, or make
  the doc blunt.
- **c — `EndDeferPublish`'s `HasUnpublishedWork` gate** (dirty pages only) skips the rollback
  path — and so doesn't restore `_FreeSpaces` / `_NextAnchorId` — when no page is dirty.
  Correct only while "no dirty pages ⇒ nothing else changed" holds. Add a comment or a cheap
  Debug assertion.
- **e — snapshot cost.** `DeferPublish()` deep-copies `_Extents` + `_PhysicalRecords`, same
  as `CreateCheckpoint()`. Doc note: worth it for 3+ ops.

---

## Robustness / design

### D3 — EFS recursion still opens a scope per level in `RecoverPending`
`DeleteCore` now brackets the whole recursive delete in one reference-counted `DeferPublish`
scope (done). `RecoverPending` still recurses without a single outer scope — low risk, same
one-scope treatment applies.

### D4c — `Validate(Optional Deep As Boolean = False)`
Only when `Deep` does it unwrap each payload (decrypt / decompress) and check the round-trip.
Shallow `Validate` stays MAC-only. (The old "does this all the time" note is stale.) While
here, fold in the D6 assertions.

### D5 (residual) — Defragment(Move) per-move cost
The practical D5 problem — livelock, monotonic file growth, needing several calls — is fixed
(see DONE: commits 128f99a / 02646a1). Still unchanged:
`DefragmentMove` rebuilds `GetLiveRecordsSortedByOffset` + `GetDeadHoles` (deep table copies)
and calls `GetFragmentation()` per single relocation — O(n²) within a pass; and
`RelocatePhysicalRecord` does ~4–5 fsyncs + a `PersistDefragMoveMetadata` publish per moved
record. Plus the progress metric `(origFrag − curFrag) / origFrag` sits near 0 for most of a
real run, and the sample `Defrag()` callback calls `GetStructure()` (~160 ms) every 250 ms.
**Fix:** batch K mutually-disjoint moves under one journal + one publish; compute
`GetFragmentation()` every N moves. Lower priority now the real-file problem is solved.

### D6 — assert the cached `_PhysicalDataEnd` — DONE (see DONE 2026-09-03)
`_PhysicalDataEnd` counts `RefCount > 0` records only; defrag recomputes before trusting it;
a failed defrag / ApplyOptions faults; `Validate()` now reports a `CacheInconsistency` problem
(non-lossy repair) for `_PhysicalDataEnd`, `_NextAnchorId` and `_LivePhysicalRecordIdsByOffset`
drift; and the producer (`DecrementPhysicalRecordRefCount` not shrinking the end when a record
leaves the live set inside a checkpoint) is fixed with a `_PhysicalDataEndDirty` flag +
lazy recompute in `GetDataEndFromIndex`.

### Repair — second-phase items
Follow-up to the 2026-09-03 corruption/repair work:
- Deep salvage when the extent *pages* are lost on both header copies: MAC-scan the data area
  for physical records by id and hand back their raw contents. Logical layout + anchor ids are
  unrecoverable (physical records carry no logical position), so this is a last-resort
  "recover N MB of chunk data" path, not a rebuild. Only build it if a real incident loses
  both roots — the dual-header + append-only design is meant to prevent exactly that.

### S3 — hand-rolled key wrap hygiene
File master key is wrapped as `FMK XOR HMAC-SHA256(userKey, wrapSalt)` with the *same* value
used as the HMAC auth key. Fresh random salt per wrap keeps it a one-time pad, so not broken,
but AES-KW (RFC 3394), an AEAD, or two HKDF-separated sub-keys would be cleaner.
`EncryptionInfo(KeyMaterial)` also accepts any non-empty length. Revisit on a format-version
bump.

### Snappy `Decompress` — cap the self-described output size
`ReadPreamble` returns up to `Integer.MaxValue`; `Decompress` then allocates
`Output(ExpectedSize - 1)`. A forged multi-gigabyte varint preamble → a multi-GB allocation
(OOM). Sanity-cap `ExpectedSize` against `Input.Length * a plausible max ratio`.

---

## Naming / housekeeping

### Rename the confusable header-recovery constants (`_ChunkedStream.vb` ~lines 100–165)
- Suffix convention: `…Pos` = a byte position in the header, `…Size` = a byte count. Kills
  `XxxOffsetOffset` / `IndexOffsetOffset`.
- Split the one `Journal*` / `Recovery*` union (same 64 bytes, three meanings by
  `RecoveryState`) into three prefixed views: `Move*` (physical-record move), `Checkpoint*`
  (checkpoint recovery), `Rebuild*` (chunk-size rebuild).
- Drop the `JournalStates` compat enum; update the Defrag call sites to `RecoveryStates`.

### Legacy `_IndexOffset` family (old todo item 18)
`_IndexOffset` / `IndexOffsetOffset` / `IndexCountOffset` / `IndexMacOffset` /
`IndexPageEntryCountOffset` — the field now behaves as the **data-area / metadata boundary**,
not a flat-index location. Rename: `_IndexOffset` → `_MetadataBoundary` (or `_DataAreaEnd`),
`IndexCountOffset` → `ExtentCountPos`, `IndexMacOffset` → `MetadataRootMacPos`, etc.

### `DirectoryTypes` only has 4 types (`None` / `ExtentPages` / `PhysicalRecordPages` /
`Holes`) — decide whether that's complete or document why.

### Stray `'TODO: Check` on `Metadata.vb` `ReadMetadataRoot` — resolve or delete the marker.

### `Autoexec.vb` (the new `Sub Main` benchmark harness that replaced Form1)
- Unused `Imports System.Windows.Forms`; `Imports i00CodeLib` warns (`BC40056`).
- **D1 residual:** the project is still `OutputType=Exe` with WinForms / `System.Drawing`
  references (`System.Drawing` used only by the fragmentation-bitmap methods in
  `Structure.Extensions.vb`). Decide whether the library should ship as `Library` with the
  demo/benchmark split out.

### `README.md` — fill in once the API settles.

---

## From the rev 8 audit re-read (2026-09-06)

New observations from re-reading `Crypto.vb` / `Storage.vb` / the positioned-stream layer
against the current branch. None are correctness bugs.

### Sub-block MAC omits the sub-block index (S1/S2 family)
Each sub-block's HMAC covers the record header, the sub-block length table and that
sub-block's own IV + ciphertext — but **not its index**. Two sub-blocks of equal stored
length within one record can be transposed by someone with backing-store write access and
every MAC still verifies; the plaintext lands scrambled. Same call as S1/S2 (the MACs target
bit-rot, which never reorders blocks). Cheap hardening: fold a 4-byte `SubBlockIndex` into
each sub-block's `HMACSHA256.TransformBlock` before the sub-block bytes. **No test** for
transposition yet.

### SB-1 — per-sub-block compression: no fallback, wrong evaluated percent
`PrepareChunkRecord` decides `StoredCompressionMethod` from a whole-chunk trial compression,
then compresses each sub-block independently. An incompressible sub-block inside an
otherwise-compressible chunk is stored **inflated** (the compressed result is used
unconditionally — no per-sub-block "keep plaintext if smaller"). And
`CompressionEvaluatedPercent` is recorded from the whole-chunk trial, not the sub-block total
actually stored, so `ApplyOptions(Compression)`'s re-evaluation works off a slightly wrong
number. (The independent-per-sub-block ratio hit itself is the intended random-read tradeoff.)

### SB-2 — dead vars in `PrepareChunkRecord`
`Payload` / `PayloadLength` and the whole-chunk `Compressed` trial are assigned but the stored
bytes + header payload length are built entirely from the per-sub-block pass
(`TotalPayloadLength`). The trial is still needed to *decide* `StoredCompressionMethod`; the
leftover assignments just read as if the whole-chunk result is used. Trim.

### rw-lock — read path has no re-entrancy guard
`AsyncReaderWriterLock` is writer-preference. `EnterReadLock` is a no-op only when the flow
already holds the *write* lock. Shared-lock readers never nest today, but if a future change
nests one shared-lock read inside another *while a writer is queued*, the inner
`EnterReadAsync` parks behind the writer while the outer read still holds a slot → reader
re-entrancy deadlock. Add a comment on `EnterReadLock` ("read path must not nest") or a Debug
depth-assert.

---

## API / features

- **Async support.** — *done (see DONE.md).* Follow-ups: make `OpenAsync` / `DefragmentAsync` /
  `ApplyOptionsAsync` and the checkpoint / DeferPublish `*Async` methods truly async rather
  than worker-thread offloads; convert `GetStructureAsync` / `ValidateAsync` /
  `GetFragmentationAsync` onto the real async read path.
- **Reader concurrency** — lock-free parallel reads — *done (see DONE.md).* `_StateLock` is a
  writer-preference `AsyncReaderWriterLock`; the large positional reads take it shared with a
  per-call `ChunkCipher`.
- **Write coalescing / current-chunk write caching** — `Options.CurrentChunkWriteCaching`.
- **Multi-chunk read cache** — `Options.ReadCacheChunkCount > 1` (currently fixed at 1).
- **Read-time coalescing of contiguous extents.**
- **Shrink the file header?**
- **More compression methods:** fast Brotli, Zstd, LZMA / 7-Zip.
- **More encryption methods:** AES-GCM, ChaCha20-Poly1305.

---

## Compression / encryption throughput

### AES-CTR is interop-bound — DONE (see DONE.md)
### Compression always runs even when the result is discarded — DONE (`Options.CompressionEvaluation`, see DONE.md)
### Re-entrant chunk crypto → parallelism — DONE (see DONE.md)
All AES-CTR state moved onto a per-`ChunkCipher` instance; large reads authenticate / decrypt /
decompress on a worker pool (`Options.MaxCryptoParallelism`). Parallel per-chunk *write* also
done: `WritePhysicalRecordWithPolicy` split into `PrepareChunkRecord` (pure CPU, parallelised
by `BuildExtentsInParallelAsync`) + serial `PlaceChunkRecordAsync`.

---

## Tests still to add

- Broaden `ModelBasedRandomOperationsMatchByteArrayModel`: encryption + compression variants,
  anchor operations, and a mid-run reopen (it currently reopens only at the very end).
  `Replace` / `Clear` / `InsertNullBytes` / a `DeferPublish` burst were added 2026-08-28.
- Gap **#8** (tamper-rejection in encrypted mode) was deliberately skipped — S1 / S2 are
  accepted by design, so the test would only document a non-guarantee.
  `ValidateFailsWhenChunkMacCorrupted` already covers the real guarantee.

---

## Accepted by design — not changing

See the audit artifact for the reasoning.

- **S1 / S2** — metadata / header authenticated only with the public integrity key even when
  encrypted; a record's MAC key is chosen from its own `EncryptionMethod` field; and the
  per-sub-block MAC omits the sub-block index (equal-length sub-blocks within a record are
  transposable — see rev-8 section above). The MACs target bit-rot and accidental change
  without needing a key; the `None` path is what lets a user rewrite a file to plaintext and
  read it instantly. (The `SubBlockIndex`-in-MAC hardening is cheap if the threat model widens.)
- **S4** — `EncryptionInfo.DefaultPBKDF2Iterations` is a mutable process-global with a public
  setter; the test harness drops it to 1. Default 600,000.
- **D1** — Form1 is gone (replaced by the `Autoexec.vb` benchmark `Sub Main`, `Console`
  MyType). Residual is housekeeping: still `OutputType=Exe` + WinForms / `System.Drawing`
  references. See housekeeping section.
- **S5** — reader concurrency was blocked by shared AES-CTR state; that's now the re-entrant
  `ChunkCipher` and lock-free parallel reads shipped. **No longer accepted — fixed.** See DONE.

(**D7** — in-checkpoint chunk writes reusing pre-checkpoint holes — is **fixed**, not
accepted: shipped in `eac5ccd` with C7 and never reverted. See DONE.)
