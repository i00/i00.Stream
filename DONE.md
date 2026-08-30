# ChunkedStream — DONE

Open items are in [TODO.md](TODO.md). Item ids match the audit artifact.

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
