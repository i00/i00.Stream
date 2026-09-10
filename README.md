# ChunkedStream

**Random-access, authenticated, optionally encrypted and optionally compressed chunk
storage layered over any caller-owned .NET `Stream`** — with stable logical anchors,
sparse regions, data-only checkpoints, atomic metadata publishing, crash recovery,
defragmentation, structure diagnostics, and an embedded hierarchical file system built
on top.

`ChunkedStream` inherits `System.IO.Stream`, so anything that can read or write a stream
can read or write one of these. Underneath, the logical byte sequence is stored as a set
of independently authenticated — and optionally encrypted and compressed — physical
records whose on-disk order is completely decoupled from logical order.

- **Namespace:** `i00.Streams`
- **Target framework:** .NET Framework 4.8
- **Language:** VB.NET (`Option Strict On`, `Option Infer On`)
- **Backing store:** any seekable `Stream` you own — a `FileStream`, a `MemoryStream`,
  or one of the bundled positioned streams. `ChunkedStream` never disposes it.

---

## Contents

- [Why](#why)
- [Solution layout](#solution-layout)
- [Requirements](#requirements)
- [Building and testing](#building-and-testing)
- [Quick start](#quick-start)
- [Core concepts](#core-concepts)
- [Options](#options)
- [Asynchronous API](#asynchronous-api)
- [Diagnostics, validation and repair](#diagnostics-validation-and-repair)
- [Crash safety and recovery](#crash-safety-and-recovery)
- [Backing streams](#backing-streams)
- [Embedded file system](#embedded-file-system)
- [Sample application](#sample-application)
- [Project status](#project-status)
- [License](#license)

---

## Why

A plain encrypted file is all-or-nothing: to change one byte in the middle you rewrite
and re-authenticate the whole thing, and a power cut half way through can leave you with
neither the old file nor the new one.

`ChunkedStream` solves that. The logical stream is divided into fixed-size chunks; each
chunk is stored as its own physical record with its own HMAC (and its own IV when
encrypted). Writing to the middle of the stream rewrites only the affected chunks. Every
structure on disk —
headers, metadata pages, chunk records — is authenticated, so bit-rot and accidental
modification are always detected. Two alternating header copies plus a recovery journal
mean an interrupted write always reopens to a consistent, previously committed state.

On top of the raw stream you get:

- **Anchors** — immutable identifiers for logical positions that survive insertion,
  deletion, defragmentation, compression and encryption changes, so you never have to
  track byte offsets yourself.
- **Checkpoints** — data-only savepoints with commit / rollback and automatic rollback
  on dispose.
- **`DeferPublish`** — batch many edits into a single atomic metadata publish.
- **Defragmentation** — compact or fully rebuild the physical layout in place.
- **`EmbeddedFileSystem`** — a complete directory tree, file streams and pending-file
  crash recovery stored inside one `ChunkedStream`.

---

## Solution layout

`i00.Stream.sln`

| Project | Path | What it is |
| --- | --- | --- |
| **ChunkedStream** | `ChunkedStream/` | The library. `ChunkedStream`, `EmbeddedFileSystem`, the positioned-stream implementations, and the LZ4 / Snappy codecs. Its own entry point (`Autoexec.Main`) is a small throughput benchmark. |
| **UnitTests** | `_Tests/UnitTests/` | 300+ tests covering stream semantics, mutation, allocation, encryption, compression, sparse storage, defragmentation, durability, crash recovery, concurrency and the embedded file system. |
| **UnitTester** | `_Tests/UnitTester/` | The lightweight test runner the `UnitTests` project uses (`<UnitTester.SimpleTest>`). |
| **EmbeddedFileSystemSample** | `_Samples/EmbeddedFileSystemSample/` | A WinForms browser for `.efs` archives — drag-and-drop import/extract, thumbnails, search, and one-click validate / repair / recover. |

`TODO.md` and `DONE.md` in the repo root track the engineering backlog and its history.

---

## Requirements

- Windows with the **.NET Framework 4.8** developer pack.
- **Visual Studio 2022** (or its build tools). The code uses modern VB language features,
  so the legacy .NET Framework `vbc` / MSBuild (`C:\Windows\Microsoft.NET\Framework64\...`)
  cannot compile it — build with the MSBuild that ships with Visual Studio 2022.

The library itself has no third-party dependencies. LZ4 and Snappy block compression are
implemented in the project.

---

## Building and testing

Open `i00.Stream.sln` in Visual Studio 2022 and build, or from a **Developer Command
Prompt for VS 2022**:

```
msbuild i00.Stream.sln -t:Build -p:Configuration=Debug
```

Run the test suite:

```
_Tests\UnitTests\bin\Debug\UnitTests.exe
```

It takes no arguments, prints per-test results and a `Passed` / `Failed` summary, and
exits `0` when everything is green.

---

## Quick start

```vb
Imports System.IO
Imports i00.Streams

' Open an existing archive, or create one if the backing stream is empty.
Using Backing As New FileStream("data.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite)
    Using Cs = ChunkedStream.Open(Backing)

        ' Standard Stream API: reads and writes at Position.
        Cs.Position = 0
        Cs.Write(New Byte() {1, 2, 3, 4}, 0, 4)

        ' Random-access overloads: an explicit logical offset, Position untouched.
        Cs.Write(1_000_000, PayloadBytes)
        Dim Buffer(4095) As Byte
        Dim Read = Cs.Read(1_000_000, Buffer)

        Dim Everything = Cs.ToArray()
    End Using
End Using
```

### Encryption

Encryption is opt-in and set through `Options`. A random per-file master key is generated
the first time encryption is enabled and wrapped in the header; changing the passphrase
only re-wraps that key, it does not rewrite chunk data.

```vb
Dim Options As New ChunkedStream.ChunkedStreamOptions With {
    .EncryptionInfo = New ChunkedStream.EncryptionInfo("correct horse battery staple"),
    .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4
}

Using Cs = ChunkedStream.Open(Backing, Options)
    Cs.Write(0, SecretBytes)
End Using

' Later, decide up front whether a passphrase is needed:
If ChunkedStream.IsEncrypted(Backing) Then
    ' prompt for the passphrase and pass it in Options.EncryptionInfo
End If
```

Passphrase keys are derived with PBKDF2-SHA256 (600,000 iterations by default). Chunk
payloads are encrypted with AES-CTR and authenticated with HMAC-SHA256 using keys derived
from the file master key.

To turn encryption off for an existing archive, set `Options.EncryptionInfo = Nothing`
and call `ApplyOptions(ApplyOptionTypes.Encryption)` to rewrite the existing chunks as
plaintext (which also removes the now-unused master key).

### Anchors

An anchor is a stable handle to a logical position. Its identity never changes and is
never reused, even as data is inserted or removed before it.

```vb
Dim Record = Cs.CreateAnchor(RecordBytes)   ' appends the data and anchors its first byte
Dim Id = Record.AnchorId

Cs.Insert(0, New Byte(1023) {})             ' shift everything right by 1 KB

Dim Same = Cs.GetAnchor(Id)
' Same.Offset is now 1024; Same.AnchorId is unchanged.
```

### Checkpoints

A checkpoint is a data-only savepoint. Writes inside it are visible to reads immediately;
`Commit` moves the baseline forward, `Rollback` restores it, and disposing the checkpoint
rolls back. Checkpoints nest but must be closed in LIFO order.

```vb
Using Cp = Cs.CreateCheckpoint()
    Cs.Write(0, Attempt)
    If Not LooksRight() Then Cp.Rollback() Else Cp.Commit()
End Using
```

### Batching edits into one publish

`DeferPublish` folds the per-operation metadata publishes of a burst of edits into a
single atomic publish when the scope closes (call `Publish()` to commit; leaving the
scope without it rolls the batch back).

```vb
Using Scope = Cs.DeferPublish()
    For Each Item In Items
        Cs.Write(Item.Offset, Item.Data)
    Next
    Scope.Publish()
End Using
```

---

## Core concepts

### Logical and physical model

The logical stream is a list of **extents**. Each extent points at a **physical record**
and an offset within it. Multiple extents can share a physical record; physical records
are reference counted and reclaimed when the count reaches zero. Rewriting data generally
creates new physical records and retires old ones. Physical layout is independent of
logical order, and records are not required to be the same size.

A **sparse** extent references no physical record at all — an all-zero logical range that
costs nothing to store.

### Anchors

Anchors identify the *start of an extent*, not a storage location. At most one anchor can
exist at a given logical offset. Anchor IDs are immutable and never reused. Removing the
anchored data destroys the anchor; removing the anchor explicitly does not touch the
data. An `Anchor` exposes `AnchorId`, `Offset`, `IsValid` and `Remove()`.

### Paged metadata and atomic publishing

Metadata is stored as variable-sized paged structures — extent pages, extent directory
pages, physical-record pages, physical-record directory pages, optional hole-directory
pages, and a **metadata root** that describes them all. Only modified pages are
rewritten, and a page may reuse a suitable hole. A newly written root becomes active only
once a header update points at it, so publication is atomic from a reader's point of
view. A self-consistency check runs before every publish and faults rather than write a
torn index.

### Dual headers

The file begins with two 512-byte header copies written alternately, each with its own
sequence number and HMAC. `Open` validates both and selects the valid one with the
highest sequence number. Data starts at offset 1024.

```
+---------------------------+
| Header A           512 B  |
+---------------------------+
| Header B           512 B  |
+---------------------------+
| Chunk Records             |
+---------------------------+
| Index Pages               |
+---------------------------+
| Index Directory Pages     |
+---------------------------+
| Hole Directory Pages      |
+---------------------------+
| Metadata Roots            |
+---------------------------+
```

### Integrity model

Every header, metadata structure and chunk record is authenticated. Unencrypted
structures are MAC'd with a public integrity key: this detects corruption and accidental
modification but is **not** tamper protection. Encrypted content is authenticated with
keys derived from the file master key. Letting an `EncryptionMethod = None` record verify
against the public key is deliberate — it is what allows an archive to be "decrypted"
instantly by rewriting its chunks as plaintext.

### Compression

Compression is evaluated per chunk. Each physical record stores the method actually used,
the method evaluated, and the achieved compressed-size percentage.
`CompressionRatioThreshold` controls whether an evaluated result is worth storing.
Available methods: `Deflate`, `GZip`, `Lz4`, `Snappy`. Compression happens before
encryption.

### Write-location policies

Newly written chunk records are placed according to
`Options.NewChunkWriteLocationPolicy` — append beyond live data, or prefer a reusable
hole (`BestFit`) and fall back to append. While a checkpoint or `DeferPublish` scope is
open, chunk records still land in freed holes below the checkpoint mark, which stays
crash-safe because a rollback reloads the durable generation that never referenced them.
Existing layout is never reorganised automatically — that is what defragmentation is for.

### Defragmentation

`Defragment(DefragTypes.Move)` — fast compaction: move later records into earlier holes.
`DefragTypes.Sequence` — reorder live records into record-id order.
`DefragTypes.Rebuild` — fully rewrite the logical stream using the current options
(this is also how a new `ChunkSize` is applied). All modes preserve anchored logical
boundaries, report progress and can be cancelled.

### Read cache

The most recently decrypted plaintext chunk can be cached
(`Options.UseChunkReadCache`, on by default). It is invalidated on any data, structure or
checkpoint change and is never required for correctness.

---

## Options

`ChunkedStream.ChunkedStreamOptions` controls how *new* physical records are written.
Existing records keep their representation until rewritten by `ApplyOptions` or a
rebuild. Opening an existing archive updates `ChunkSize` (and the encryption state) to
match what is stored.

| Option | Default | Purpose |
| --- | --- | --- |
| `EncryptionInfo` | `Nothing` | Encryption key / passphrase for new chunks. `Nothing` = plaintext. |
| `CompressionMethod` | `None` | `Deflate`, `GZip`, `Lz4` or `Snappy`. |
| `CompressionEvaluation` | `Always` | Whether/how to trial-compress each chunk. |
| `CompressionRatioThreshold` | `0.95` | Store compressed only if it saves at least this fraction. |
| `ChunkSize` | 128 KB | Logical chunk size for new records; applied to existing data via `ApplyOptions(ChunkSize)` or `Defragment(Rebuild)`. |
| `SubBlockSize` | = `ChunkSize` | Sub-divide a chunk record into independently IV'd and MAC'd sub-blocks. |
| `StoreSparseChunks` | `False` | Represent all-zero ranges as sparse extents. |
| `NewChunkWriteLocationPolicy` | `BestFit` | Append vs. reuse holes for new chunk records. |
| `HoleDirectoryMode` | `Auto` | Whether to persist the free-space directory for faster reopen. |
| `ExtentReclaimType` | `RefCount` | Reference counting vs. full scan for reclaiming unreferenced records. |
| `UseChunkReadCache` | `True` | Cache the most recently decrypted chunk. |
| `MaxCryptoParallelism` | CPU count | Parallel per-chunk crypto for large reads and writes. |
| `MaxPhysicalReadParallelism` / `MaxPhysicalWriteParallelism` | `4` | Parallel physical I/O against a capable backing stream. |
| `AutoRecoverOnFault` | `False` | Reload the in-memory image from disk after a mid-operation fault instead of leaving the stream unusable. |

`ApplyOptions(Types)` rewrites existing chunks to satisfy the current compression,
encryption, sparseness and chunk-size settings, with progress and cancellation. It
returns a summary of what changed.

---

## Asynchronous API

Every blocking operation has a `...Async` counterpart with a `CancellationToken`:
`OpenAsync`, `ReadAsync` / `WriteAsync` (both the `Stream` overrides and the
logical-offset overloads), `ToArrayAsync`, `FlushAsync`, `SetLengthAsync`,
`Insert`/`Clear`/`Replace`/`Remove`/`Clone` async forms, `CreateAnchorAsync`,
`CreateCheckpointAsync` (and `Commit`/`Rollback`/`Close` async on the checkpoint),
`DeferPublishAsync`, `DefragmentAsync`, `ApplyOptionsAsync`, `ValidateAsync`,
`GetStructureAsync` and `GetFragmentationAsync`.

The async paths are genuinely asynchronous, not sync-over-async, and large reads and
writes run per-chunk crypto in parallel. If the backing stream implements
`IPositionedStreamAsync`, its awaitable positioned I/O is used in preference to
`Stream.ReadAsync` / `WriteAsync`.

---

## Diagnostics, validation and repair

```vb
Dim Report = Cs.Validate()          ' never throws; returns a ValidationReport
If Report.HasErrors Then
    For Each Problem In Report.Problems
        Console.WriteLine($"{Problem.Severity}: {Problem.Kind}")
    Next
    Dim Result = Report.Repair(ChunkedStream.RepairScope.NonLossy)
End If
Report.ThrowIfErrors()              ' or throw on anything unrepaired
```

`Validate` streams the archive one record at a time (it does not load everything into
memory) and does not hold the stream locked for the whole scan. `RepairScope.NonLossy`
(the default) fixes structural problems that lose no data — reference-count drift, the
anchor index, a stale cached length; `RepairScope.IncludeDataLoss` additionally
zero-fills logical ranges backed by unreadable data. Each problem is re-verified against
the live stream before it is acted on, so a report can be built, inspected and only then
repaired.

`GetStructure()` returns a full picture of chunk regions, metadata regions, holes,
fragmentation, and compression / encryption statistics. `GetFragmentation()` returns a
single ratio.

---

## Crash safety and recovery

Recovery is automatic on `Open()`. It always restores the most recently committed state:
chunk-move recovery validates copied records before publication, metadata recovery
restores the last valid root, checkpoint recovery restores the checkpoint baseline, and
an interrupted rebuild is truncated and the previous state reopened.

- **`StartupRecovery`** — a property grouping everything `Open` had to do: a `NeedsScan`
  flag, an overall `State`, a ready-made `Summary`, the list of repairs applied, whether
  a salvaged index was written back, a count of extents whose data is gone, and the
  recovery-journal result. (`RecoveryStateAtOpen`, `AutoRecoveryState`,
  `AutoRecoveryException` and `AutoRepairs` remain as aliases.)
- **`Options.AutoRecoverOnFault`** and **`Recover()`** — reload the in-memory image from
  the backing store after a fault, so a faulted stream becomes usable again without a
  dispose-and-reopen. The faulting call still throws. Back the stream with an unbuffered
  `FileStream` (`bufferSize:=1`) when relying on this against a file.
- An archive whose physical-record pages were left inconsistent by an interrupted
  process still opens: every readable record is kept, unreadable ones are recorded, and
  the cleaned index is written straight back.

---

## Backing streams

`ChunkedStream` works over any seekable `Stream`. Three optional interfaces let a backing
stream do better than `Position`-based I/O:

| Interface | Effect |
| --- | --- |
| `IPositionedStream` | Exposes `ReadAt` / `WriteAt` at an explicit physical offset without touching `Position`, and declares via `PositionedIoCapabilities` whether reads and/or writes may run without `ChunkedStream`'s physical-I/O lock. |
| `IPositionedStreamAsync` | Adds `ReadAtAsync` / `WriteAtAsync`, preferred on the async paths. |
| `IDurableFlush` | Lets a non-`FileStream` wrapper perform a real `FlushFileBuffers`-style durable flush, auto-detected the same way positioned I/O is. |

Bundled implementations:

- **`PositionedMemoryStream`** — an in-memory backing store implementing the full
  positioned + async contract; used throughout the tests.
- **`PositionedFileStream`** — a single-handle file backing store with positioned I/O
  and durable flush.
- **`PooledPositionedFileStream`** — a multi-handle pool for concurrent positioned I/O
  against one file.

If a stream implements none of these, `ChunkedStream` serialises physical I/O through its
own semaphore and still avoids disturbing `Position`.

---

## Embedded file system

`i00.Streams.EmbeddedFileSystem` stores a hierarchical file system inside one
`ChunkedStream`, addressing every directory and file by a stable anchor ID.

```vb
Using Cs = ChunkedStream.Open(Backing, Options)
    Using Efs As New EmbeddedFileSystem(Cs)

        Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
        Dim FileId = Efs.CreateFile(Docs, "notes.txt")

        Using Writer = Efs.OpenFile(FileId)          ' a seekable .NET Stream
            Dim Bytes = Text.Encoding.UTF8.GetBytes("hello")
            Writer.Write(Bytes, 0, Bytes.Length)
        End Using

        For Each Entry In Efs.GetDirectoryEntries(Docs)
            Console.WriteLine($"{Entry.Name} ({Entry.LengthOfDataAtEntry} bytes)")
        Next
    End Using
End Using
```

- **Pending files** — while a write stream is open its entry is marked `PendingFile`; on
  clean close it is finalised to `File`. A crash leaves it pending.
- **`RecoverPendingFiles`** — finalises, removes or lists pending, corrupt or
  unreferenced records. A second phase finds every record unreachable from the root and
  removes it or re-homes it under `_Recovered`.
- **`Mark`** — takes a `ChunkedStream.ValidationReport` and flags the entries whose data
  a validation could not read, so the tree degrades gracefully instead of throwing.
- Multi-step operations (create, delete, rename) run inside a single `DeferPublish`
  scope, so a crash mid-operation is all-or-nothing.

Archives are conventionally given the `.efs` extension.

---

## Sample application

`_Samples/EmbeddedFileSystemSample` is a WinForms browser for `.efs` archives:

```
EmbeddedFileSystemSample.exe "path\to\archive.efs"
```

It prompts to create the file if it does not exist, prompts for a passphrase if the
archive is encrypted, and opens a two-pane browser with drag-and-drop import (copy /
move / extract, including extracting dropped `.zip` archives), file and folder icons and
thumbnails, list / detail / tile views, Back / Forward navigation, search, and Quick /
Extended scan tools that run the validate → mark → repair → recover pass. If the archive
had to be recovered while opening, the window explains what happened and offers an
extended scan.

`repair-test-efs.ps1` opens a *copy* of a damaged `.efs` and runs the full headless
validate / mark / repair / recover pass, writing `<name>.repaired.efs` alongside it.

---

## Project status

Actively developed. The public API carries XML documentation throughout; the file header
of `ChunkedStream/Streams/ChunkedStream/_ChunkedStream.vb` is the authoritative
description of every model summarised here. Open work and completed history are tracked in
[`TODO.md`](TODO.md) and [`DONE.md`](DONE.md).

## License

No license has been specified for this repository yet. Until one is added, all rights are
reserved by the author.
