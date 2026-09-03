# ChunkedStream — DONE

Open items are in [TODO.md](TODO.md). Item ids match the audit artifact.

---

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

**Still open:** parallel per-chunk *write* — see TODO.

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
