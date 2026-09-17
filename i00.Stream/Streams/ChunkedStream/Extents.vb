Imports System.IO

Namespace Streams
    Partial Class ChunkedStream

        Private Function GetExtentEnd(Extent As ExtentIndexEntry) As Long

            Return Extent.LogicalOffset + CLng(Extent.LogicalLength)

        End Function

        Private Function FindExtentIndex(LogicalOffset As Long) As Integer

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))

            Dim Low = 0
            Dim High = _Extents.Count - 1

            While Low <= High

                Dim Mid = Low + ((High - Low) \ 2)
                Dim Extent = _Extents(Mid)
                Dim ExtentEnd = GetExtentEnd(Extent)

                If LogicalOffset < Extent.LogicalOffset Then
                    High = Mid - 1
                ElseIf LogicalOffset >= ExtentEnd Then
                    Low = Mid + 1
                Else
                    Return Mid
                End If

            End While

            Return -1

        End Function

        Private Function FindExtentInsertIndex(LogicalOffset As Long) As Integer

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))

            Dim Low = 0
            Dim High = _Extents.Count

            While Low < High

                Dim Mid = Low + ((High - Low) \ 2)

                If _Extents(Mid).LogicalOffset < LogicalOffset Then
                    Low = Mid + 1
                Else
                    High = Mid
                End If

            End While

            Return Low

        End Function

        Private Function AllocatePhysicalRecordId() As Long

            Dim Result = _NextPhysicalRecordId

            If Result <= SparsePhysicalRecordId Then
                Result = SparsePhysicalRecordId + 1
            End If

            If Result = Long.MaxValue Then
                Throw New InvalidOperationException("No more physical record ids are available.")
            End If

            _NextPhysicalRecordId = Result + 1

            Return Result

        End Function

        Private Function GetPhysicalRecord(RecordId As Long) As PhysicalRecordEntry

            If RecordId = SparsePhysicalRecordId Then
                Throw New InvalidOperationException("Sparse extents do not have physical records.")
            End If

            Dim Record As PhysicalRecordEntry = Nothing

            If _PhysicalRecords.TryGetValue(RecordId, Record) = False Then
                Throw New InvalidDataException($"Physical record {RecordId} was not found.")
            End If

            Return Record

        End Function

        Private Sub IncrementPhysicalRecordRefCount(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount = Integer.MaxValue Then
                Throw New InvalidOperationException(
            $"Physical record {RecordId} refcount overflow.")
            End If

            MarkPhysicalRecordDirty(RecordId)

            Dim WasUnreferenced = Record.RefCount = 0

            Record.RefCount += 1
            _PhysicalRecords(RecordId) = Record

            If WasUnreferenced Then
                AddLivePhysicalRecordOffset(Record)
                ' A resurrected record re-enters the live set - grow the cached end to
                ' cover it, mirroring AddPhysicalRecordToIndexes.
                _PhysicalDataEnd = Math.Max(_PhysicalDataEnd,
                                            Record.PhysicalOffset + CLng(Record.PhysicalLength))
            End If

        End Sub

        '
        ' TotalExtentCount is always passed explicitly rather than read from _Extents.Count:
        ' every caller must mark the affected pages dirty BEFORE mutating _Extents (a Thread.Abort
        ' between an under-marked mutation and its dirty-mark would leave the affected on-disk
        ' page stale forever - the next publish only rewrites pages in _DirtyExtentPages). Passing
        ' the post-mutation count (or the larger of before/after when the count is changing) lets
        ' the mark be computed correctly ahead of a mutation that hasn't happened yet.
        '
        Private Sub MarkExtentPageRangeDirty(FirstExtentIndex As Integer,
                                             LastExtentIndex As Integer,
                                             TotalExtentCount As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If TotalExtentCount = 0 Then Return

            Dim FirstIndex = Math.Max(0, FirstExtentIndex)
            Dim LastIndex = Math.Min(TotalExtentCount - 1, LastExtentIndex)

            If LastIndex < FirstIndex Then Return

            Dim FirstPage = FirstIndex \ _IndexPageEntryCount
            Dim LastPage = LastIndex \ _IndexPageEntryCount

            For PageNumber = FirstPage To LastPage
                _DirtyExtentPages.Add(PageNumber)
            Next

        End Sub

        Private Sub MarkExtentPagesDirtyFromIndex(ExtentIndex As Integer, TotalExtentCount As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If TotalExtentCount = 0 Then Return

            MarkExtentPageRangeDirty(ExtentIndex, TotalExtentCount - 1, TotalExtentCount)

        End Sub

        Private Sub MarkExtentPagesDirtyForReplacement(StartIndex As Integer,
                                                       RemovedExtentCount As Integer,
                                                       InsertedExtentCount As Integer,
                                                       TotalExtentCount As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If TotalExtentCount = 0 Then Return

            Dim SafeStartIndex = Math.Max(0, Math.Min(StartIndex, TotalExtentCount - 1))

            If RemovedExtentCount = InsertedExtentCount Then

                Dim DirtyCount = Math.Max(1, InsertedExtentCount)
                MarkExtentPageRangeDirty(SafeStartIndex, SafeStartIndex + DirtyCount - 1, TotalExtentCount)
                Return

            End If

            '
            ' If the extent count changed, every later ordinal can shift to a different
            ' page. With the current dense ordinal metadata format, pages from the change
            ' point onward must be rewritten.
            '
            MarkExtentPagesDirtyFromIndex(SafeStartIndex, TotalExtentCount)

        End Sub

        Private Sub MarkPhysicalRecordDirty(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Ordinal As Integer
            If _PhysicalRecordOrdinals.TryGetValue(RecordId, Ordinal) = False Then
                Throw New InvalidDataException(
                    $"Physical record ordinal for record {RecordId} was not found.")
            End If
            MarkPhysicalRecordPageDirtyByOrdinal(Ordinal)
        End Sub


        Private Sub DecrementPhysicalRecordRefCount(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount <= 0 Then
                Throw New InvalidDataException(
                    $"Physical record {RecordId} has an invalid refcount.")
            End If

            MarkPhysicalRecordDirty(RecordId)

            If Record.RefCount = 1 Then
                RemoveLivePhysicalRecordOffset(Record)
            End If

            Record.RefCount -= 1
            _PhysicalRecords(RecordId) = Record

            If Record.RefCount <> 0 Then Return

            '
            ' The record has left the live set. If it was at (or beyond) the cached
            ' physical-data end that end may now be lower - flag it stale so
            ' GetDataEndFromIndex recomputes before anything (a metadata publish's root
            ' offset, a diagnostics snapshot) reads it. Doing the O(records) recompute here,
            ' per decrement, would undo the batched-reclaim speed-up.
            '
            If Record.PhysicalOffset + CLng(Record.PhysicalLength) >= _PhysicalDataEnd Then
                _PhysicalDataEndDirty = True
            End If

            '
            ' In Scan mode the decision to reclaim is not taken from this counter -
            ' ReclaimUnreferencedPhysicalRecordsByScan reclaims every record no extent
            ' points at once the surrounding edit has rebuilt the extent layout. An open
            ' checkpoint always falls back to this counter-driven pending set instead,
            ' because the scan sweep does not run until the checkpoint is gone.
            '
            If HasOpenCheckpoint = False AndAlso
               Options.ExtentReclaimType = ChunkedStreamOptions.ExtentReclaimTypes.Scan Then Return

            '
            ' Defer the reclaim. Every record freed by one edit is detached in a single
            ' batch - one ordinal-map rebuild for the whole edit - by
            ' SettleDeferredPhysicalRecordReclaims when the edit finishes, or held until
            ' the outermost checkpoint commit by ReclaimPendingPhysicalRecords so a
            ' rollback can still restore it.
            '
            _PendingReclaimedPhysicalRecords.Add(RecordId)

        End Sub

        ''' <summary>
        ''' Result of <see cref="TryGetExtendableLastChunkAsync"/>: whether the stream's current
        ''' last extent is eligible to grow in place, and - when it is - the record it references
        ''' plus (only under <see cref="ChunkedStreamOptions.ChunkSizeVariance"/>, where checking
        ''' eligibility already requires decrypting it) its plaintext, so callers never decrypt
        ''' the same record twice.
        ''' </summary>
        Private Structure ExtendableLastChunkResult
            Public Eligible As Boolean
            Public Record As PhysicalRecordEntry
            Public Plain As Byte()
        End Structure

        ''' <summary>
        ''' True when the stream's current last extent wholly (from its own offset 0) and
        ''' exclusively (RefCount = 1) references a non-sparse record that is still open to grow -
        ''' the one condition under which extending it in place, rather than starting a new chunk,
        ''' is safe. A shared or dedup-matched record has other extents relying on its exact
        ''' current content; a sub-range reference isn't "the whole chunk" to begin with. A sparse
        ''' extent is never eligible, deliberately - see BuildExtentsFromBufferAsync's
        ''' AllowGrowableTail remarks for why a lone zero byte still needs to stay safe to grow
        ''' into a larger real chunk without going through this path at all.
        ''' </summary>
        ''' <remarks>
        ''' Under fixed-size chunking (<see cref="ChunkedStreamOptions.ChunkSizeVariance"/> = 0)
        ''' "still open" just means shorter than <see cref="ChunkedStreamOptions.ChunkSize"/>, the
        ''' same check this used to be. Under content-defined chunking it means more: the record's
        ''' plaintext must not already end on a genuine Gear-hash boundary or
        ''' <see cref="ChunkedStreamOptions.MaxChunkSize"/> - otherwise appending to it would
        ''' produce a chunk a one-shot write of the combined bytes would never have produced (see
        ''' <see cref="IsChunkClosedByCdcBoundary"/>). Checking that requires decrypting the
        ''' record, so this returns the plaintext too when eligible, rather than making
        ''' <see cref="TryExtendLastChunkAsync"/>/<see cref="OpenPendingChunkAsync"/> decrypt it a
        ''' second time.
        ''' </remarks>
        Private Async Function TryGetExtendableLastChunkAsync(RunAsync As Boolean, CancellationToken As Threading.CancellationToken) As Task(Of ExtendableLastChunkResult)

            If _Extents.Count = 0 Then Return New ExtendableLastChunkResult()

            Dim LastExtent = _Extents(_Extents.Count - 1)

            If LastExtent.PhysicalRecordId = SparsePhysicalRecordId Then Return New ExtendableLastChunkResult()
            If LastExtent.PhysicalRecordOffset <> 0 Then Return New ExtendableLastChunkResult()

            Dim Candidate = GetPhysicalRecord(LastExtent.PhysicalRecordId)

            If Candidate.RefCount <> 1 Then Return New ExtendableLastChunkResult()
            If Candidate.PlainLength <> LastExtent.LogicalLength Then Return New ExtendableLastChunkResult()

            If Options.ChunkSizeVariance <= 0 Then

                If Candidate.PlainLength >= Options.ChunkSize Then Return New ExtendableLastChunkResult()

                Return New ExtendableLastChunkResult With {.Eligible = True, .Record = Candidate}

            End If

            Dim Plain As Byte()

            If RunAsync Then
                Plain = Await ReadPhysicalRecordPlainAsync(Candidate, CancellationToken).ConfigureAwait(False)
            Else
                Plain = ReadPhysicalRecordPlain(Candidate)
            End If

            If IsChunkClosedByCdcBoundary(Plain, Plain.Length) Then Return New ExtendableLastChunkResult()

            Return New ExtendableLastChunkResult With {.Eligible = True, .Record = Candidate, .Plain = Plain}

        End Function

        ''' <summary>
        ''' Replays the same Gear-hash accumulation <see cref="DetermineNextSegmentLength"/> would
        ''' perform over Plain's first Length bytes if they were the start of a chunk, returning
        ''' the hash state at that point. Bytes before <see cref="ChunkedStreamOptions.MinChunkSize"/>
        ''' never enter the hash, matching <see cref="DetermineNextSegmentLength"/> exactly - see
        ''' its own remarks.
        ''' </summary>
        Private Function ComputeChunkHashState(Plain As IReadOnlyList(Of Byte), Length As Integer) As ULong

            Dim MinChunkSize = Options.MinChunkSize
            Dim Hash As ULong = 0

            For WrittenLength = 1 To Length
                If WrittenLength >= MinChunkSize Then
                    Hash = GearHash.Roll(Hash, Plain(WrittenLength - 1))
                End If
            Next

            Return Hash

        End Function

        ''' <summary>
        ''' True when Plain's first Length bytes represent a chunk a content-defined scan would
        ''' treat as genuinely closed - by <see cref="ChunkedStreamOptions.MaxChunkSize"/> or a
        ''' Gear-hash threshold trip at exactly Length - rather than one that merely ran out of
        ''' data at the time it was written. Only a chunk that stopped for the latter reason is
        ''' safe to extend in place or keep buffering: appending to one that already closed for
        ''' real would produce a chunk a one-shot write of the combined bytes would never have
        ''' produced. Assumes Plain is self-consistent (produced by this same chunking logic), so
        ''' it never checks for a boundary strictly before Length - only content this class itself
        ''' wrote is ever a candidate for extension.
        ''' </summary>
        Private Function IsChunkClosedByCdcBoundary(Plain As IReadOnlyList(Of Byte), Length As Integer) As Boolean

            If Length >= Options.MaxChunkSize Then Return True
            If Length < Options.MinChunkSize Then Return False

            Return ComputeChunkHashState(Plain, Length) < Options.SplitHashThreshold

        End Function

        ''' <summary>
        ''' CDC counterpart to the plain byte-count cap (<see cref="ChunkedStreamOptions.ChunkSize"/>)
        ''' used when extending the current last chunk in place or filling the write-cache buffer:
        ''' given ExistingLength bytes already part of the chunk (ExistingPlain, assumed not
        ''' already closed - see <see cref="IsChunkClosedByCdcBoundary"/>) plus new bytes about to
        ''' be appended (Input/DataOffset/Count), returns how many of the new bytes to absorb
        ''' before the chunk should close - a Gear-hash content boundary or
        ''' <see cref="ChunkedStreamOptions.MaxChunkSize"/>, whichever comes first, or all of Count
        ''' if neither is reached. Runs the same scan <see cref="DetermineNextSegmentLength"/> runs
        ''' over a single buffer, just resuming mid-chunk instead of starting one from scratch, so
        ''' a stream built up through many small appends chunks identically to the same bytes
        ''' written in one call.
        ''' </summary>
        Private Function DetermineChunkExtensionLength(ExistingPlain As IReadOnlyList(Of Byte),
                                                       ExistingLength As Integer,
                                                       Input As Byte(),
                                                       DataOffset As Integer,
                                                       Count As Integer) As Integer

            If Options.ChunkSizeVariance <= 0 Then
                Return Math.Max(0, Math.Min(Options.ChunkSize - ExistingLength, Count))
            End If

            Dim MinChunkSize = Options.MinChunkSize
            Dim MaxChunkSize = Options.MaxChunkSize
            Dim Threshold = Options.SplitHashThreshold

            Dim RoomLeft = MaxChunkSize - ExistingLength
            If RoomLeft <= 0 Then Return 0

            Dim Cap = Math.Min(RoomLeft, Count)
            Dim Hash = ComputeChunkHashState(ExistingPlain, ExistingLength)
            Dim WrittenLength = ExistingLength
            Dim Absorbed = 0

            While Absorbed < Cap

                Dim DataByte = Input(DataOffset + Absorbed)
                Absorbed += 1
                WrittenLength += 1

                If WrittenLength >= MinChunkSize Then

                    Hash = GearHash.Roll(Hash, DataByte)

                    If Hash < Threshold OrElse WrittenLength >= MaxChunkSize Then Return Absorbed

                End If

            End While

            Return Absorbed

        End Function

        ''' <summary>
        ''' Redirects the stream's current last extent onto NewRecordId (already carrying one
        ''' reference credited to this call, per WritePhysicalRecordAsync's usual contract) and
        ''' reclaims OldRecordId - the commit step shared by growing a chunk in place immediately
        ''' (TryExtendLastChunkAsync) and committing one that was buffered
        ''' (CommitPendingChunkAsync).
        ''' </summary>
        Private Sub ReplaceLastExtentRecord(NewRecordId As Long, NewLogicalLength As Integer, OldRecordId As Long)

            Dim LastIndex = _Extents.Count - 1
            Dim LastExtent = _Extents(LastIndex)

            MarkExtentPageDirty(LastIndex)

            LastExtent.PhysicalRecordId = NewRecordId
            LastExtent.PhysicalRecordOffset = 0
            LastExtent.LogicalLength = NewLogicalLength
            _Extents(LastIndex) = LastExtent

            DecrementPhysicalRecordRefCount(OldRecordId)
            SettleDeferredPhysicalRecordReclaims()

        End Sub


        ''' <summary>
        ''' Tries to absorb a genuine end-of-stream append into the physical record the stream's
        ''' current last extent already references, instead of always creating a brand-new
        ''' (possibly tiny) chunk for every Write() call - see the write-coalescing plan notes.
        ''' Unconditional: this is not gated behind an option, since every Write() call still
        ''' fully commits before returning either way, exactly as before this existed. Not used
        ''' when Options.CurrentChunkWriteCaching defers this same decision instead - see
        ''' AppendThroughWriteCacheAsync.
        ''' </summary>
        ''' <returns>
        ''' The number of leading bytes of Input (starting at DataOffset) actually absorbed into
        ''' the extended chunk - 0 if nothing was eligible to extend (an empty stream, a sparse or
        ''' shared or already-closed last chunk, or one only partially referenced by its extent).
        ''' Never more than Count, and never more than the room remaining up to
        ''' Options.ChunkSize/MaxChunkSize or the next content-defined boundary.
        ''' </returns>
        Private Async Function TryExtendLastChunkAsync(Input As Byte(),
                                                       DataOffset As Integer,
                                                       Count As Integer,
                                                       RunAsync As Boolean,
                                                       CancellationToken As Threading.CancellationToken) As Task(Of Integer)

            If Count <= 0 Then Return 0

            Dim Eligibility = Await TryGetExtendableLastChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)
            If Eligibility.Eligible = False Then Return 0

            Dim Record = Eligibility.Record

            Dim OldPlain As Byte()

            If Eligibility.Plain IsNot Nothing Then
                OldPlain = Eligibility.Plain
            ElseIf RunAsync Then
                OldPlain = Await ReadPhysicalRecordPlainAsync(Record, CancellationToken).ConfigureAwait(False)
            Else
                OldPlain = ReadPhysicalRecordPlain(Record)
            End If

            Dim AbsorbCount = DetermineChunkExtensionLength(OldPlain, OldPlain.Length, Input, DataOffset, Count)
            If AbsorbCount <= 0 Then Return 0

            Dim Combined(OldPlain.Length + AbsorbCount - 1) As Byte
            Buffer.BlockCopy(OldPlain, 0, Combined, 0, OldPlain.Length)
            Buffer.BlockCopy(Input, DataOffset, Combined, OldPlain.Length, AbsorbCount)

            ' Goes through the ordinary write pipeline - including the deduplication hook, if
            ' the grown content happens to already match something else in the index.
            Dim NewRecord = Await WritePhysicalRecordAsync(Combined, Combined.Length, RunAsync, CancellationToken).ConfigureAwait(False)

            _Length += AbsorbCount

            ReplaceLastExtentRecord(NewRecord.RecordId, Combined.Length, Record.RecordId)

            Return AbsorbCount

        End Function

        ''' <summary>
        ''' Opens <see cref="_PendingChunkPlain"/>, seeding it from the stream's current last
        ''' chunk when that chunk is eligible to grow (see
        ''' <see cref="TryGetExtendableLastChunkAsync"/>) or starting it empty otherwise. Only
        ''' ever called when no buffer is already open.
        ''' </summary>
        Private Async Function OpenPendingChunkAsync(RunAsync As Boolean, CancellationToken As Threading.CancellationToken) As Task

            Dim Eligibility = Await TryGetExtendableLastChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)

            If Eligibility.Eligible Then

                Dim SeedPlain As Byte()

                If Eligibility.Plain IsNot Nothing Then
                    SeedPlain = Eligibility.Plain
                ElseIf RunAsync Then
                    SeedPlain = Await ReadPhysicalRecordPlainAsync(Eligibility.Record, CancellationToken).ConfigureAwait(False)
                Else
                    SeedPlain = ReadPhysicalRecordPlain(Eligibility.Record)
                End If

                _PendingChunkPlain = New List(Of Byte)(SeedPlain)
                _PendingChunkStart = _Extents(_Extents.Count - 1).LogicalOffset
                _PendingChunkSourceRecordId = Eligibility.Record.RecordId

            Else

                _PendingChunkPlain = New List(Of Byte)()
                _PendingChunkStart = _Length
                _PendingChunkSourceRecordId = SparsePhysicalRecordId

            End If

        End Function

        ''' <summary>
        ''' Commits the pending write-cache buffer first if a read of EffectiveCount bytes
        ''' starting at LogicalOffset would otherwise reach into it - the buffered tail isn't
        ''' backed by any extent until it's committed, so an unflushed read into that range would
        ''' fail to resolve. Callers are responsible for only invoking this while holding the
        ''' stream's exclusive state lock: unlike an ordinary positional read, this can mutate
        ''' shared extent/physical-record state, which the read lock alone does not protect
        ''' against a concurrent reader also observing.
        ''' </summary>
        Private Async Function FlushPendingChunkBeforeReadAsync(LogicalOffset As Long,
                                                                EffectiveCount As Integer,
                                                                RunAsync As Boolean,
                                                                CancellationToken As Threading.CancellationToken) As Task

            If _PendingChunkPlain Is Nothing Then Return
            If EffectiveCount <= 0 Then Return
            If LogicalOffset + CLng(EffectiveCount) <= _PendingChunkStart Then Return

            Await CommitPendingChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)

        End Function

        ''' <summary>
        ''' Commits <see cref="_PendingChunkPlain"/> - replacing the extent it was seeded from, if
        ''' any, or appending a brand new one - and closes the buffer. Writes a real physical
        ''' record unless the buffered content is still entirely zero (and
        ''' Options.StoreSparseChunks is False), in which case it becomes (or stays) a sparse
        ''' extent instead - exactly what a one-shot write of the same bytes would have produced,
        ''' rather than always materialising a real record just because this content happened to
        ''' pass through the write-cache buffer. A no-op if nothing is currently buffered, so every
        ''' commit trigger (a full chunk, an explicit flush, or anything needing complete extent/
        ''' record state) can call this unconditionally.
        ''' </summary>
        Private Async Function CommitPendingChunkAsync(RunAsync As Boolean, CancellationToken As Threading.CancellationToken) As Task

            If _PendingChunkPlain Is Nothing Then Return

            If _PendingChunkPlain.Count = 0 Then
                _PendingChunkPlain = Nothing
                _PendingChunkSourceRecordId = SparsePhysicalRecordId
                Return
            End If

            Dim Combined = _PendingChunkPlain.ToArray()

            If _PendingChunkSourceRecordId <> SparsePhysicalRecordId Then

                ' A real seed's plaintext already has at least one non-zero byte (or it would have
                ' been sparse itself), so growing it can never end up all zero - always a real
                ' record here.
                Dim NewRecord = Await WritePhysicalRecordAsync(Combined, Combined.Length, RunAsync, CancellationToken).ConfigureAwait(False)

                ReplaceLastExtentRecord(NewRecord.RecordId, Combined.Length, _PendingChunkSourceRecordId)

            Else

                ' Nothing to replace - splice a brand new extent onto the stream's tail directly
                ' (rather than through InsertExtentsCore, which would double-count _Length: every
                ' buffered byte already advanced it the moment it was appended to the buffer).
                ' Sparse rather than a wasted all-zero record when the buffer never picked up a
                ' non-zero byte - exactly what a one-shot write of the same bytes would produce.
                Dim NewRecordId = SparsePhysicalRecordId

                If Options.StoreSparseChunks OrElse IsAllZero(Combined, Combined.Length) = False Then
                    Dim NewRecord = Await WritePhysicalRecordAsync(Combined, Combined.Length, RunAsync, CancellationToken).ConfigureAwait(False)
                    NewRecordId = NewRecord.RecordId
                End If

                MarkExtentPageDirty(_Extents.Count)

                _Extents.Add(New ExtentIndexEntry With {
                    .LogicalOffset = _PendingChunkStart,
                    .LogicalLength = Combined.Length,
                    .PhysicalRecordId = NewRecordId,
                    .PhysicalRecordOffset = 0,
                    .AnchorId = 0
                })

                RebuildAnchorIndex()

            End If

            _PendingChunkPlain = Nothing
            _PendingChunkSourceRecordId = SparsePhysicalRecordId

        End Function

        ''' <summary>
        ''' Routes a genuine end-of-stream append through the deferred write cache: opens
        ''' <see cref="_PendingChunkPlain"/> if nothing is buffered yet, and appends as many bytes
        ''' as fit before the chunk closes - <see cref="ChunkedStreamOptions.ChunkSize"/>, or under
        ''' content-defined chunking whichever of a Gear-hash boundary or
        ''' <see cref="ChunkedStreamOptions.MaxChunkSize"/> comes first - committing it if it does.
        ''' _Length advances immediately as each byte is buffered, so the stream's reported length
        ''' is never behind what Write() already returned to the caller.
        ''' </summary>
        ''' <remarks>
        ''' Only ever fills/closes *one* chunk directly - deliberately not a loop. Count can span
        ''' many chunks' worth in a single call (a large buffered upload draining all at once, for
        ''' instance), and looping this same one-chunk-at-a-time logic across all of them used to
        ''' mean every single chunk paid its own serial <see cref="WritePhysicalRecordAsync"/>
        ''' round trip - never reaching <see cref="BuildExtentsInParallelAsync"/> no matter how
        ''' large the write was, unlike the unconditional (non-cached) extend-in-place path, whose
        ''' own remainder already always could. Once the first (possibly seeded) chunk is settled,
        ''' anything left over is hashed out via the same <see cref="BuildExtentsFromBufferAsync"/>
        ''' remainder path that path uses - which both content-defines its own boundaries across
        ''' as many chunks as Remaining needs and, above
        ''' <see cref="ChunkedStreamOptions.MinChunksForParallelCrypto"/> chunks, dispatches them
        ''' to <see cref="BuildExtentsInParallelAsync"/>. <c>AllowGrowableTail:=True</c> keeps that
        ''' call's own last segment open to a later append the same way this buffer's seed always
        ''' is, so a follow-up small Write() call still coalesces into it via
        ''' <see cref="OpenPendingChunkAsync"/> - nothing about the buffering guarantee this option
        ''' promises is lost, only the everything-through-one-record serialisation was.
        ''' </remarks>
        Private Async Function AppendThroughWriteCacheAsync(Input As Byte(),
                                                             DataOffset As Integer,
                                                             Count As Integer,
                                                             RunAsync As Boolean,
                                                             CancellationToken As Threading.CancellationToken) As Task

            If _PendingChunkPlain Is Nothing Then
                Await OpenPendingChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)
            End If

            Dim AbsorbCount = DetermineChunkExtensionLength(_PendingChunkPlain, _PendingChunkPlain.Count, Input, DataOffset, Count)

            If AbsorbCount > 0 Then

                Dim Segment(AbsorbCount - 1) As Byte
                Buffer.BlockCopy(Input, DataOffset, Segment, 0, AbsorbCount)
                _PendingChunkPlain.AddRange(Segment)

                _Length += AbsorbCount

            End If

            Dim ChunkIsClosed =
                If(Options.ChunkSizeVariance <= 0,
                   _PendingChunkPlain.Count >= Options.ChunkSize,
                   IsChunkClosedByCdcBoundary(_PendingChunkPlain, _PendingChunkPlain.Count))

            If ChunkIsClosed Then
                Await CommitPendingChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)
            End If

            Dim RemainingCount = Count - AbsorbCount
            If RemainingCount <= 0 Then Return

            '
            ' AbsorbCount was capped by room left in the chunk (or a content boundary within it),
            ' not by Count, whenever there's a remainder here - so the chunk above always just
            ' closed, and _PendingChunkPlain is Nothing again. Nothing left buffered to conflict
            ' with placing the remainder as ordinary, immediately-committed extents.
            '
            Dim RemainingExtents =
                Await BuildExtentsFromBufferAsync(Input, DataOffset + AbsorbCount, RemainingCount, RunAsync, CancellationToken, AllowGrowableTail:=True).ConfigureAwait(False)

            InsertExtentsCore(_Length, RemainingExtents)

        End Function

        ''' <summary>
        ''' Commits whatever is currently buffered by <see cref="ChunkedStreamOptions.CurrentChunkWriteCaching"/>
        ''' right now, under-full or not. A no-op if nothing is buffered or the option was never
        ''' used - safe to call unconditionally.
        ''' </summary>
        Public Sub FlushCurrentChunkWriteCache()

            Using EnterStateLock()
                FlushCurrentChunkWriteCacheCore()
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronous counterpart to <see cref="FlushCurrentChunkWriteCache"/>.
        ''' </summary>
        Public Function FlushCurrentChunkWriteCacheAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken, Function() FlushCurrentChunkWriteCacheCoreAsync(RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub FlushCurrentChunkWriteCacheCore()

            FlushCurrentChunkWriteCacheCoreAsync(RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function FlushCurrentChunkWriteCacheCoreAsync(RunAsync As Boolean, CancellationToken As Threading.CancellationToken) As Task

            ThrowIfDisposed()
            ThrowIfFaulted()

            If _PendingChunkPlain Is Nothing Then Return

            Await CommitPendingChunkAsync(RunAsync, CancellationToken).ConfigureAwait(False)

            If MetadataPublishSuspended = False Then
                Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
            End If

        End Function

        '
        ' Applies every physical-record reclaim deferred during an extent mutation. In Scan
        ' mode the unreferenced records are found by scanning the rebuilt extent table;
        ' otherwise the records DecrementPhysicalRecordRefCount marked pending are detached
        ' as one batch. Under an open checkpoint nothing is reclaimed here -
        ' ReclaimPendingPhysicalRecords does it at the outermost commit.
        '
        Private Sub SettleDeferredPhysicalRecordReclaims()

            If HasOpenCheckpoint Then Return

            If Options.ExtentReclaimType = ChunkedStreamOptions.ExtentReclaimTypes.Scan Then
                ReclaimUnreferencedPhysicalRecordsByScan()
            Else
                ApplyPendingPhysicalRecordReclaims()
            End If

        End Sub

        '
        ' Detaches every still-unreferenced record in _PendingReclaimedPhysicalRecords, then
        ' closes the ordinal gaps that leaves via CompactPhysicalRecordOrdinalsAfterRemoval -
        ' see its remarks for why that is an O(records removed) operation, not O(records),
        ' regardless of how large the table has grown.
        '
        Private Sub ApplyPendingPhysicalRecordReclaims()

            If _PendingReclaimedPhysicalRecords.Count = 0 Then Return

            Dim PendingRecordIds = _PendingReclaimedPhysicalRecords.ToArray()
            _PendingReclaimedPhysicalRecords.Clear()

            Dim OldCount = _PhysicalRecordOrdinals.Count
            Dim RemovedOrdinals As New List(Of Integer)()

            For Each recordId In PendingRecordIds

                Dim Record As PhysicalRecordEntry

                If _PhysicalRecords.TryGetValue(recordId, Record) = False Then Continue For
                If Record.RefCount <> 0 Then Continue For

                Dim RemovedOrdinal = DetachReclaimedPhysicalRecord(recordId)
                If RemovedOrdinal >= 0 Then RemovedOrdinals.Add(RemovedOrdinal)

            Next

            If RemovedOrdinals.Count = 0 Then Return

            CompactPhysicalRecordOrdinalsAfterRemoval(OldCount, RemovedOrdinals)

        End Sub

        '
        ' Removes one unreferenced physical record from the record table: drops its
        ' live-offset index entry, defers its backing span, and - unlike the stale-until-
        ' rebuilt approach this replaced - immediately removes it from the ordinal map and
        ' its physical-record page too, leaving a genuine gap at the ordinal it held. The
        ' caller closes that gap for the whole batch via CompactPhysicalRecordOrdinalsAfterRemoval.
        ' Returns the ordinal the record held, or -1 for the sparse id.
        '
        Private Function DetachReclaimedPhysicalRecord(RecordId As Long) As Integer

            If RecordId = SparsePhysicalRecordId Then Return -1

            Dim RemovedOrdinal As Integer

            If _PhysicalRecordOrdinals.TryGetValue(RecordId,
                                                   RemovedOrdinal) = False Then

                Throw New InvalidDataException(
                    $"Physical record ordinal for record {RecordId} was not found.")

            End If

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount <> 0 Then

                Throw New InvalidOperationException(
                    $"Cannot reclaim physical record {RecordId} because it is still referenced.")

            End If

            ' Mark the page dirty before any table below changes - see MovePhysicalRecordOrdinal's
            ' comment. A killed thread past this point can at worst leave the page dirty
            ' with nothing actually removed from it yet; never the reverse.
            Dim PageNumber = RemovedOrdinal \ _IndexPageEntryCount
            _DirtyPhysicalRecordPages.Add(PageNumber)

            RemoveLivePhysicalRecordOffset(Record)

            '
            ' Hold the freed span until the header slot that referenced this record has
            ' been rotated out, so a crash cannot fall back to a header whose storage we
            ' have already overwritten. See _DeferredFreeRanges.
            '
            DeferFreeSpace(Record.PhysicalOffset,
                              Record.PhysicalLength)

            _PhysicalRecords.Remove(RecordId)
            _PhysicalRecordOrdinals.Remove(RecordId)

            ' The id is now dead (ids are only ever handed out ascending), so its cached
            ' plaintext can never be served again - drop it and free the slot.
            EvictCachedRecord(RecordId)

            Dim PageRecordIds As SortedSet(Of Long) = Nothing

            If _PhysicalRecordIdsByPage.TryGetValue(PageNumber, PageRecordIds) Then
                PageRecordIds.Remove(RecordId)
            End If

            Return RemovedOrdinal

        End Function

        '
        ' Single-record reclaim for the callers that only ever free one record at a time.
        ' The batched paths use DetachReclaimedPhysicalRecord directly.
        '
        Private Sub ReclaimPhysicalRecord(RecordId As Long)

            Dim OldCount = _PhysicalRecordOrdinals.Count
            Dim RemovedOrdinal = DetachReclaimedPhysicalRecord(RecordId)

            If RemovedOrdinal < 0 Then Return

            CompactPhysicalRecordOrdinalsAfterRemoval(OldCount, New List(Of Integer) From {RemovedOrdinal})

        End Sub

        '
        ' Closes the ordinal gap(s) DetachReclaimedPhysicalRecord's removals just left, by
        ' moving whichever records currently sit at the ordinals that no longer fit (>= the
        ' new, shrunken count) down into the freed slots - the same "swap with the tail"
        ' trick a packed array uses to delete an element without shifting everything after
        ' it. This is safe because nothing on disk records ordinal order: ReadPhysicalRecordPages
        ' reads every page's entries straight into a Dictionary keyed by RecordId, so which
        ' record ends up at which ordinal has never mattered - only that ordinals
        ' 0..NewCount-1 stay densely occupied. Every page that gains, loses or changes an
        ' entry is marked dirty as it happens (DetachReclaimedPhysicalRecord above, and the
        ' moves below) - never every page after the lowest hole, which is what made the
        ' RebuildPhysicalRecordOrdinals-based version of this cost O(records) on every
        ' reclaim, however small.
        '
        ' The survivors to move are found by walking backwards from the old top ordinal, a
        ' page at a time, skipping ordinals that were themselves just removed - a range
        ' bounded by how many records this batch removed, not by the table's total size.
        '
        Private Sub CompactPhysicalRecordOrdinalsAfterRemoval(OldCount As Integer, RemovedOrdinals As List(Of Integer))

            Dim NewCount = _PhysicalRecordOrdinals.Count

            If NewCount + RemovedOrdinals.Count <> OldCount Then
                Throw New InvalidDataException("Physical-record ordinal bookkeeping is inconsistent after a reclaim.")
            End If

            Dim HoleQueue As New Queue(Of Integer)(RemovedOrdinals.Where(Function(ordinal) ordinal < NewCount))

            If HoleQueue.Count > 0 AndAlso _IndexPageEntryCount > 0 Then

                Dim FirstTailPage = NewCount \ _IndexPageEntryCount
                Dim LastTailPage = (OldCount - 1) \ _IndexPageEntryCount

                For PageNumber = LastTailPage To FirstTailPage Step -1

                    If HoleQueue.Count = 0 Then Exit For

                    Dim PageRecordIds As SortedSet(Of Long) = Nothing
                    If _PhysicalRecordIdsByPage.TryGetValue(PageNumber, PageRecordIds) = False Then Continue For

                    ' Snapshot first - MovePhysicalRecordOrdinal mutates this same set as
                    ' survivors move out of it.
                    For Each SurvivorId In PageRecordIds.ToArray()

                        If HoleQueue.Count = 0 Then Exit For

                        Dim SurvivorOrdinal = _PhysicalRecordOrdinals(SurvivorId)
                        If SurvivorOrdinal < NewCount Then Continue For ' Already inside the kept range.

                        MovePhysicalRecordOrdinal(SurvivorId, SurvivorOrdinal, HoleQueue.Dequeue())

                    Next

                Next

                If HoleQueue.Count > 0 Then
                    Throw New InvalidDataException("Could not find enough surviving physical records to close every ordinal gap.")
                End If

            End If

        End Sub

        '
        ' Reassigns RecordId from OldOrdinal to NewOrdinal, keeping _PhysicalRecordOrdinals
        ' and _PhysicalRecordIdsByPage in sync and marking both the old and new page dirty -
        ' the counterpart to AddPhysicalRecordToIndexes for a record changing ordinal rather
        ' than being freshly inserted.
        '
        Private Sub MovePhysicalRecordOrdinal(RecordId As Long, OldOrdinal As Integer, NewOrdinal As Integer)

            If OldOrdinal = NewOrdinal Then Return

            Dim OldPageNumber = OldOrdinal \ _IndexPageEntryCount
            Dim NewPageNumber = NewOrdinal \ _IndexPageEntryCount

            '
            ' Mark both pages dirty BEFORE touching any table below. A killed thread
            ' landing anywhere from here on can only leave a page dirty that turns out
            ' unchanged (a harmless extra rewrite) - never the reverse, where a page that
            ' truly changed is left clean and the next publish silently skips it,
            ' serialising a stale on-disk copy that disagrees with the (self-consistent!)
            ' in-memory tables. AssertPhysicalRecordIndexConsistent cannot catch that: it
            ' only checks _PhysicalRecordOrdinals/_PhysicalRecordIdsByPage agree with each
            ' other, never that _DirtyPhysicalRecordPages covers every page they imply
            ' changed.
            '
            _DirtyPhysicalRecordPages.Add(OldPageNumber)
            _DirtyPhysicalRecordPages.Add(NewPageNumber)

            _PhysicalRecordOrdinals(RecordId) = NewOrdinal

            If OldPageNumber <> NewPageNumber Then

                Dim OldPageRecordIds As SortedSet(Of Long) = Nothing
                If _PhysicalRecordIdsByPage.TryGetValue(OldPageNumber, OldPageRecordIds) Then
                    OldPageRecordIds.Remove(RecordId)
                End If

                Dim NewPageRecordIds As SortedSet(Of Long) = Nothing
                If _PhysicalRecordIdsByPage.TryGetValue(NewPageNumber, NewPageRecordIds) = False Then
                    NewPageRecordIds = New SortedSet(Of Long)()
                    _PhysicalRecordIdsByPage.Add(NewPageNumber, NewPageRecordIds)
                End If
                NewPageRecordIds.Add(RecordId)

            End If

        End Sub

        Private Sub AddPhysicalRecordToIndexes(Record As PhysicalRecordEntry,
                                               Ordinal As Integer)

            If Record.RecordId <= SparsePhysicalRecordId Then
                Throw New InvalidDataException("Invalid physical record id.")
            End If

            If Ordinal < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Ordinal))
            End If

            If _PhysicalRecordOrdinals.ContainsKey(Record.RecordId) Then
                Throw New InvalidDataException(
                    $"Physical record {Record.RecordId} already exists in the ordinal index.")
            End If

            _PhysicalRecordOrdinals.Add(Record.RecordId, Ordinal)

            Dim PageNumber = Ordinal \ _IndexPageEntryCount
            Dim PageRecordIds As SortedSet(Of Long) = Nothing

            If _PhysicalRecordIdsByPage.TryGetValue(PageNumber, PageRecordIds) = False Then
                PageRecordIds = New SortedSet(Of Long)()
                _PhysicalRecordIdsByPage.Add(PageNumber, PageRecordIds)
            End If

            If PageRecordIds.Add(Record.RecordId) = False Then
                Throw New InvalidDataException(
                    $"Physical record {Record.RecordId} already exists in physical-record page {PageNumber}.")
            End If

            If Record.RefCount > 0 Then

                AddLivePhysicalRecordOffset(Record)

                '
                ' _PhysicalDataEnd tracks the end of live physical data only. An
                ' unreferenced record's span is already treated as free space by
                ' BuildFreeSpaceMapCore, so counting it here would pin the cached end
                ' above the real data - and, once a defragment trim shortens the backing
                ' stream, above the stream itself.
                '
                _PhysicalDataEnd =
                    Math.Max(_PhysicalDataEnd,
                             Record.PhysicalOffset + CLng(Record.PhysicalLength))

            End If

        End Sub

        Private Sub AddLivePhysicalRecordOffset(Record As PhysicalRecordEntry)

            If Record.RefCount <= 0 Then Return

            Dim ExistingRecordId As Long

            If _LivePhysicalRecordIdsByOffset.TryGetValue(Record.PhysicalOffset,
                                                          ExistingRecordId) Then

                If ExistingRecordId = Record.RecordId Then Return

                Throw New InvalidDataException(
                    $"Physical offset {Record.PhysicalOffset} is already indexed by physical record {ExistingRecordId}.")

            End If

            _LivePhysicalRecordIdsByOffset.Add(Record.PhysicalOffset, Record.RecordId)

        End Sub

        Private Sub RemoveLivePhysicalRecordOffset(Record As PhysicalRecordEntry)

            Dim ExistingRecordId As Long

            If _LivePhysicalRecordIdsByOffset.TryGetValue(Record.PhysicalOffset,
                                                          ExistingRecordId) = False Then

                Return

            End If

            If ExistingRecordId <> Record.RecordId Then
                Throw New InvalidDataException(
                    $"Physical-offset index mismatch at offset {Record.PhysicalOffset}. " &
                    $"Expected record {Record.RecordId}, found record {ExistingRecordId}.")
            End If

            _LivePhysicalRecordIdsByOffset.Remove(Record.PhysicalOffset)

        End Sub

        Private Sub UpdatePhysicalRecordLocationIndexes(RecordId As Long,
                                                        OldPhysicalOffset As Long,
                                                        OldPhysicalLength As Integer)

            Dim Record = GetPhysicalRecord(RecordId)

            Dim ExistingRecordId As Long

            If _LivePhysicalRecordIdsByOffset.TryGetValue(OldPhysicalOffset,
                                                          ExistingRecordId) Then

                If ExistingRecordId <> RecordId Then
                    Throw New InvalidDataException(
                        $"Physical-offset index mismatch at offset {OldPhysicalOffset}.")
                End If

                _LivePhysicalRecordIdsByOffset.Remove(OldPhysicalOffset)

            End If

            If Record.RefCount > 0 Then
                AddLivePhysicalRecordOffset(Record)
            End If

            Dim OldEnd = OldPhysicalOffset + CLng(OldPhysicalLength)
            Dim NewEnd = Record.PhysicalOffset + CLng(Record.PhysicalLength)

            If OldEnd >= _PhysicalDataEnd AndAlso NewEnd < OldEnd Then
                RecalculatePhysicalDataEnd()
            Else
                _PhysicalDataEnd = Math.Max(_PhysicalDataEnd, NewEnd)
            End If

        End Sub

        Private Sub RecalculatePhysicalDataEnd()

            '
            ' Only live records count towards the physical-data end. Unreferenced records
            ' are pending reclamation and their spans are already handed back as free
            ' space, so including them would leave the cached end drifting high above the
            ' real data.
            '
            Dim DataEnd = CLng(DataStartOffset)

            For Each Record In _PhysicalRecords.Values

                If Record.RefCount <= 0 Then Continue For

                DataEnd =
                    Math.Max(DataEnd,
                             Record.PhysicalOffset + CLng(Record.PhysicalLength))

            Next

            _PhysicalDataEnd = DataEnd
            _PhysicalDataEndDirty = False

        End Sub

        Private Sub RebuildPhysicalRecordOrdinals()

            _PhysicalRecordOrdinals.Clear()
            _PhysicalRecordIdsByPage.Clear()
            _LivePhysicalRecordIdsByOffset.Clear()

            _PhysicalDataEnd = DataStartOffset
            _PhysicalDataEndDirty = False

            '
            ' Ordinal order is ascending record id. Sort the ids (a primitive in-place
            ' sort) rather than OrderBy over the record structs, which copied every entry
            ' into the sort buffer on each rebuild.
            '
            Dim OrderedRecordIds As New List(Of Long)(_PhysicalRecords.Keys)
            OrderedRecordIds.Sort()

            Dim Ordinal = 0

            For Each recordId In OrderedRecordIds

                Dim Record = _PhysicalRecords(recordId)

                _PhysicalRecordOrdinals(Record.RecordId) = Ordinal

                Dim PageNumber = Ordinal \ _IndexPageEntryCount
                Dim PageRecordIds As SortedSet(Of Long) = Nothing

                If _PhysicalRecordIdsByPage.TryGetValue(PageNumber, PageRecordIds) = False Then
                    PageRecordIds = New SortedSet(Of Long)()
                    _PhysicalRecordIdsByPage.Add(PageNumber, PageRecordIds)
                End If

                PageRecordIds.Add(Record.RecordId)

                If Record.RefCount > 0 Then

                    If _LivePhysicalRecordIdsByOffset.ContainsKey(Record.PhysicalOffset) Then
                        Throw New InvalidDataException(
                            $"Multiple live physical records begin at offset {Record.PhysicalOffset}.")
                    End If

                    _LivePhysicalRecordIdsByOffset.Add(Record.PhysicalOffset, Record.RecordId)

                    ' Only live records extend the physical-data end - see RecalculatePhysicalDataEnd.
                    _PhysicalDataEnd =
                        Math.Max(_PhysicalDataEnd,
                                 Record.PhysicalOffset + CLng(Record.PhysicalLength))

                End If

                Ordinal += 1

            Next

        End Sub

        '
        ' Structural self-check run at the top of every metadata publish. The physical-record
        ' page index (_PhysicalRecordIdsByPage) and the ordinal map (_PhysicalRecordOrdinals)
        ' must both agree with _PhysicalRecords, and every record id must appear on exactly
        ' one physical-record page - the one its ordinal selects.
        '
        ' A violation means an earlier in-memory mutation was left torn - e.g. a process
        ' interrupted between the two steps of a MovePhysicalRecordOrdinal, or a killed
        ' worker thread. Throwing here (the caller turns it into a fault) stops the publish
        ' before a single byte is written, so the last durably published generation stays
        ' intact and openable. Serialising a torn index instead is exactly what bricks a
        ' reopen with "Loaded physical-record count does not match metadata root".
        '
        ' Cost is O(records + extents) dictionary lookups, once per publish - cheap next to
        ' the page writes the publish is about to do.
        '
        Private Sub AssertPhysicalRecordIndexConsistent()

            If _PhysicalRecordOrdinals.Count <> _PhysicalRecords.Count Then
                Throw New InvalidDataException(
                    $"Physical-record ordinal map holds {_PhysicalRecordOrdinals.Count} entries but the record table holds {_PhysicalRecords.Count}.")
            End If

            Dim PagedRecordCount As Integer = 0

            For Each Pair In _PhysicalRecordIdsByPage

                For Each RecordId In Pair.Value

                    PagedRecordCount += 1

                    If _PhysicalRecords.ContainsKey(RecordId) = False Then
                        Throw New InvalidDataException(
                            $"Physical record {RecordId} is on physical-record page {Pair.Key} but is not in the record table.")
                    End If

                    Dim Ordinal As Integer

                    If _PhysicalRecordOrdinals.TryGetValue(RecordId, Ordinal) = False Then
                        Throw New InvalidDataException(
                            $"Physical record {RecordId} is on physical-record page {Pair.Key} but is not in the ordinal map.")
                    End If

                    If _IndexPageEntryCount > 0 AndAlso Ordinal \ _IndexPageEntryCount <> Pair.Key Then
                        Throw New InvalidDataException(
                            $"Physical record {RecordId} is on physical-record page {Pair.Key} but its ordinal {Ordinal} belongs to page {Ordinal \ _IndexPageEntryCount}.")
                    End If

                Next

            Next

            If PagedRecordCount <> _PhysicalRecords.Count Then
                Throw New InvalidDataException(
                    $"Physical-record pages hold {PagedRecordCount} entries but the record table holds {_PhysicalRecords.Count}.")
            End If

            '
            ' Every check above only verifies the physical-record table is internally
            ' self-consistent (ordinals, page membership). None of them catch the other
            ' direction: an extent whose PhysicalRecordId was removed from - or never made
            ' it into - the table it still points at. A record's RefCount reaching zero
            ' reclaims it from _PhysicalRecords; if that ever happens while a live extent
            ' still references it (an over-eager decrement, a partial rollback, dedup
            ' sharing miscounted), this is the only place left that would ever notice
            ' before the inconsistency reaches disk - so check it explicitly rather than
            ' relying on it being implied by the checks above, which it is not.
            '
            ' Scoped to extents on a page in _DirtyExtentPages (the pages this publish is
            ' about to rewrite), NOT every extent - a tolerant-open salvage can leave extents
            ' referencing records the corruption genuinely lost (see
            ' PersistSalvagedPhysicalRecordTable's own opt-out below), and that state is
            ' deliberately allowed to persist on disk, unrepaired, across every ordinary
            ' publish afterwards (a plain Write elsewhere, or even just Dispose) until an
            ' explicit Validate().Repair(RepairScope.IncludeDataLoss) - it is what surfaces as
            ' Validate()'s MissingPhysicalRecord, not a fault at publish time. Re-validating
            ' every extent on every publish would block all further use of such a file.
            ' Restricting to dirty pages still catches a genuinely NEW inconsistency any
            ' mutation introduces (mutating an extent always dirties its page first, per the
            ' mark-dirty-before-mutate rule elsewhere in this file), while never re-flagging
            ' old, already-known, not-yet-repaired damage this publish isn't touching.
            '
            ' _AllowDanglingExtentReferencesOnNextPersist additionally skips this entirely:
            ' PersistSalvagedPhysicalRecordTable marks every page dirty (MarkAllMetadataPagesDirty)
            ' to force a full rewrite near the salvaged table, which would otherwise catch its
            ' own known, not-yet-repaired dangling references on the very publish that is
            ' correctly persisting them.
            '
            If _AllowDanglingExtentReferencesOnNextPersist = False AndAlso
               _DirtyExtentPages.Count > 0 AndAlso _IndexPageEntryCount > 0 Then

                For ExtentIndex = 0 To _Extents.Count - 1

                    If _DirtyExtentPages.Contains(ExtentIndex \ _IndexPageEntryCount) = False Then Continue For

                    Dim Extent = _Extents(ExtentIndex)

                    If Extent.PhysicalRecordId = SparsePhysicalRecordId Then Continue For

                    If _PhysicalRecords.ContainsKey(Extent.PhysicalRecordId) = False Then
                        Throw New InvalidDataException(
                            $"Extent at logical offset {Extent.LogicalOffset} references physical record {Extent.PhysicalRecordId}, which is not in the record table.")
                    End If

                Next

            End If

        End Sub

        Private Sub ReclaimPendingPhysicalRecords()

            '
            ' Reclaims are deferred while an outer checkpoint could still roll the freeing
            ' edit back. This runs only when the outermost checkpoint is being finalised -
            ' committed (stack depth 1) or closed (stack depth 0) - so a lingering inner
            ' checkpoint is the only case that must still wait.
            '
            If _CheckpointStack.Count > 1 Then Return

            ApplyPendingPhysicalRecordReclaims()

        End Sub

        Private Sub DiscardPendingPhysicalRecordReclaims()

            _PendingReclaimedPhysicalRecords.Clear()

        End Sub

        '
        ' Rebuilds the deferred-reclaim set from the physical-record table after a
        ' checkpoint snapshot has been restored. A record an inner checkpoint left
        ' unreferenced (RefCount 0) before committing into its parent is still pending
        ' reclamation once the parent finalises; a record the restore brought back to a
        ' positive reference count must not stay pending.
        '
        Private Sub RebuildPendingPhysicalRecordReclaims()

            _PendingReclaimedPhysicalRecords.Clear()

            For Each pair In _PhysicalRecords
                If pair.Value.RefCount <= 0 Then
                    _PendingReclaimedPhysicalRecords.Add(pair.Key)
                End If
            Next

        End Sub

        '
        ' Scan-based reclamation used when Options.ExtentReclaimType is Scan.
        '
        ' Rather than relying on the maintained physical-record reference counts to know
        ' when a record has become unreferenced, this rebuilds the set of referenced
        ' record ids directly from the current extent table and reclaims every physical
        ' record that no surviving extent points at. It is invoked after an edit has
        ' finished rebuilding the extent layout.
        '
        Private Sub ReclaimUnreferencedPhysicalRecordsByScan()

            If Options.ExtentReclaimType <> ChunkedStreamOptions.ExtentReclaimTypes.Scan Then Return
            If HasOpenCheckpoint Then Return

            ReclaimUnreferencedPhysicalRecords()

        End Sub

        '
        ' Rebuilds the set of physical-record ids still pointed at by a live extent and
        ' reclaims every physical record that no surviving extent references, returning the
        ' number reclaimed. The extent table is treated as authoritative: a stored
        ' reference count that disagrees with the scan is forced to zero before the record
        ' is dropped. The caller is responsible for ensuring no checkpoint is open.
        '
        Private Function ReclaimUnreferencedPhysicalRecords() As Integer

            If _PhysicalRecords.Count = 0 Then Return 0

            Dim ReferencedRecordIds As New HashSet(Of Long)()

            For Each extent In _Extents
                If extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                    ReferencedRecordIds.Add(extent.PhysicalRecordId)
                End If
            Next

            Dim UnreferencedRecordIds =
                _PhysicalRecords.Keys.
                                 Where(Function(recordId) ReferencedRecordIds.Contains(recordId) = False).
                                 ToList()

            Dim OldCount = _PhysicalRecordOrdinals.Count
            Dim RemovedOrdinals As New List(Of Integer)()

            For Each recordId In UnreferencedRecordIds

                Dim Record = _PhysicalRecords(recordId)

                '
                ' The counter is normally already zero here. Force it to agree with the
                ' scan before the record is dropped so DetachReclaimedPhysicalRecord's
                ' still-referenced guard does not trip on a drifted count.
                '
                If Record.RefCount <> 0 Then
                    Record.RefCount = 0
                    _PhysicalRecords(recordId) = Record
                End If

                Dim RemovedOrdinal = DetachReclaimedPhysicalRecord(recordId)
                If RemovedOrdinal >= 0 Then RemovedOrdinals.Add(RemovedOrdinal)

            Next

            If RemovedOrdinals.Count > 0 Then
                CompactPhysicalRecordOrdinalsAfterRemoval(OldCount, RemovedOrdinals)
            End If

            Return UnreferencedRecordIds.Count

        End Function

        Private Sub SplitExtentAt(LogicalOffset As Long)

            If LogicalOffset <= 0 OrElse LogicalOffset >= _Length Then Return

            Dim ExtentIndex = FindExtentIndex(LogicalOffset)

            If ExtentIndex < 0 Then Return

            Dim Extent = _Extents(ExtentIndex)

            If LogicalOffset = Extent.LogicalOffset OrElse
               LogicalOffset = GetExtentEnd(Extent) Then

                Return

            End If

            Dim LeftLength = CInt(LogicalOffset - Extent.LogicalOffset)
            Dim RightLength = Extent.LogicalLength - LeftLength

            Dim LeftPhysicalRecordOffset As Integer
            Dim RightPhysicalRecordOffset As Integer

            If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                LeftPhysicalRecordOffset = 0
                RightPhysicalRecordOffset = 0
            Else
                LeftPhysicalRecordOffset = Extent.PhysicalRecordOffset
                RightPhysicalRecordOffset = Extent.PhysicalRecordOffset + LeftLength
            End If

            Dim LeftExtent =
                New ExtentIndexEntry With {
                    .LogicalOffset = Extent.LogicalOffset,
                    .LogicalLength = LeftLength,
                    .PhysicalRecordId = Extent.PhysicalRecordId,
                    .PhysicalRecordOffset = LeftPhysicalRecordOffset,
                    .AnchorId = Extent.AnchorId
                }

            Dim RightExtent =
                New ExtentIndexEntry With {
                    .LogicalOffset = LogicalOffset,
                    .LogicalLength = RightLength,
                    .PhysicalRecordId = Extent.PhysicalRecordId,
                    .PhysicalRecordOffset = RightPhysicalRecordOffset,
                    .AnchorId = 0
                }

            MarkExtentPagesDirtyFromIndex(ExtentIndex, _Extents.Count + 1)

            _Extents(ExtentIndex) = LeftExtent
            _Extents.Insert(ExtentIndex + 1, RightExtent)

            If Extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                IncrementPhysicalRecordRefCount(Extent.PhysicalRecordId)
            End If

            RebuildAnchorIndex()

        End Sub

        Private Function ShouldMaterialiseAdjacentBoundaryFragments(LeftLength As Integer,
                                                                    RightLength As Integer) As Boolean

            If Options.BisectLimit <= 0 Then Return False
            If LeftLength <= 0 OrElse RightLength <= 0 Then Return False

            Dim CombinedLength = LeftLength + RightLength

            If CombinedLength > Options.ChunkSize Then Return False

            Return LeftLength < Options.BisectLimit OrElse RightLength < Options.BisectLimit

        End Function

        Private Function BuildExtentsFromBuffer(Input As Byte(),
                                                InputOffset As Integer,
                                                Count As Integer) As List(Of ExtentIndexEntry)

            Return BuildExtentsFromBufferAsync(Input, InputOffset, Count, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Function

        ''' <param name="AllowGrowableTail">
        ''' True only when this call is filling in the remainder of a genuine end-of-stream append
        ''' that TryExtendLastChunkAsync (or AppendThroughWriteCacheAsync) already partially
        ''' absorbed - never for an overwrite, insert, or gap fill, where nothing is ever going to
        ''' extend the result further. When true, the very last segment produced skips the
        ''' sparse-chunk optimisation if it hasn't genuinely closed yet (see
        ''' <see cref="IsChunkClosedByCdcBoundary"/>) - i.e. it's short only because Count ran out,
        ''' not because a real chunk boundary was found. Otherwise a lone zero byte written in
        ''' isolation (the smallest possible coalescing step) would get permanently frozen as a
        ''' sparse extent the moment it's created, since a sparse extent is never itself eligible
        ''' to extend (see TryGetExtendableLastChunkAsync) - producing a chunk boundary one byte
        ''' earlier than a one-shot write of the same bytes would have, and shifting every later
        ''' content-defined boundary for the rest of the stream. Never applied to any segment but
        ''' the last - forwarded as-is when this call dispatches to
        ''' <see cref="BuildExtentsInParallelAsync"/>, which applies the identical last-segment
        ''' check in its own planning loop. Every other segment already closed on its own terms (a
        ''' content boundary or <see cref="ChunkedStreamOptions.ChunkSize"/>/<see cref="ChunkedStreamOptions.MaxChunkSize"/>),
        ''' exactly like a one-shot write's non-final chunks always do; only the true tail can ever
        ''' be ambiguous between "closed" and "merely out of data for now".
        ''' </param>
        Private Async Function BuildExtentsFromBufferAsync(Input As Byte(),
                                                          InputOffset As Integer,
                                                          Count As Integer,
                                                          RunAsync As Boolean,
                                                          CancellationToken As Threading.CancellationToken,
                                                          Optional AllowGrowableTail As Boolean = False) As Task(Of List(Of ExtentIndexEntry))

            If ShouldBuildChunksInParallel(Count) Then
                Return Await BuildExtentsInParallelAsync(Input, InputOffset, Count, RunAsync, CancellationToken, AllowGrowableTail).ConfigureAwait(False)
            End If

            Dim Result As New List(Of ExtentIndexEntry)()
            Dim Remaining = Count
            Dim CurrentInputOffset = InputOffset

            While Remaining > 0

                Dim SegmentLength = DetermineNextSegmentLength(Input, CurrentInputOffset, Remaining)
                Dim Segment(SegmentLength - 1) As Byte

                Buffer.BlockCopy(Input, CurrentInputOffset, Segment, 0, SegmentLength)

                Dim IsGrowableTail = AllowGrowableTail AndAlso
                                     SegmentLength = Remaining AndAlso
                                     IsChunkClosedByCdcBoundary(Segment, SegmentLength) = False

                If IsGrowableTail = False AndAlso Options.StoreSparseChunks = False AndAlso IsAllZero(Segment, SegmentLength) Then

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = SegmentLength,
                        .PhysicalRecordId = SparsePhysicalRecordId,
                        .PhysicalRecordOffset = 0
                    })

                Else

                    Dim Record = Await WritePhysicalRecordAsync(Segment, SegmentLength, RunAsync, CancellationToken).ConfigureAwait(False)

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = SegmentLength,
                        .PhysicalRecordId = Record.RecordId,
                        .PhysicalRecordOffset = 0
                    })

                End If

                CurrentInputOffset += SegmentLength
                Remaining -= SegmentLength

            End While

            Return Result

        End Function

        Private Function ShouldBuildChunksInParallel(ByteCount As Integer) As Boolean

            Return Options.MaxCryptoParallelism > 1 AndAlso
                   Options.ChunkSize > 0 AndAlso
                   CLng(ByteCount) >= CLng(Options.ChunkSize) * Options.MinChunksForParallelCrypto

        End Function

        ''' <summary>
        ''' Determines how many bytes, starting at InputOffset with Remaining available, the
        ''' next chunk should take.
        ''' </summary>
        ''' <remarks>
        ''' When <see cref="ChunkedStreamOptions.ChunkSizeVariance"/> is <c>0</c> this is always
        ''' <c>Math.Min(Options.ChunkSize, Remaining)</c> - today's fixed-size behaviour, with no
        ''' scanning cost. Otherwise it runs a Gear-hash scan bounded by
        ''' <see cref="ChunkedStreamOptions.MinChunkSize"/>/<see cref="ChunkedStreamOptions.MaxChunkSize"/>,
        ''' so the boundary is chosen by the data's own content rather than always landing at a
        ''' fixed offset. The hash only starts accumulating once <c>MinChunkSize</c> bytes have
        ''' been consumed - below that, no boundary can be chosen anyway, so there is nothing to
        ''' gain from hashing those bytes.
        ''' </remarks>
        Private Function DetermineNextSegmentLength(Input As Byte(), InputOffset As Integer, Remaining As Integer) As Integer

            If Options.ChunkSizeVariance <= 0 Then
                Return Math.Min(Options.ChunkSize, Remaining)
            End If

            Dim MinChunkSize = Options.MinChunkSize
            Dim MaxChunkSize = Options.MaxChunkSize
            Dim Threshold = Options.SplitHashThreshold

            Dim Cap = Math.Min(MaxChunkSize, Remaining)

            If Cap <= MinChunkSize Then

                '
                ' Not enough data left to even reach MinChunkSize - the chunk ends where the
                ' data (or, in principle, the cap) does. This is the expected shape of the
                ' last chunk of any write.
                '
                Return Cap

            End If

            Dim Hash As ULong = 0
            Dim WrittenLength = 0

            While WrittenLength < Cap

                Dim DataByte = Input(InputOffset + WrittenLength)
                WrittenLength += 1

                If WrittenLength >= MinChunkSize Then

                    Hash = GearHash.Roll(Hash, DataByte)

                    If Hash < Threshold OrElse WrittenLength >= MaxChunkSize Then
                        Return WrittenLength
                    End If

                End If

            End While

            Return WrittenLength

        End Function

        Private Structure ChunkBuildPlan
            Public Segment As Byte()
            Public IsSparse As Boolean
            Public RecordId As Long
            Public Ivs As Byte()()
            Public IsDeduped As Boolean
            Public DedupedRecord As PhysicalRecordEntry

            ''' <summary>
            ''' Set instead of <see cref="DedupedRecord"/> when this plan matches an earlier plan
            ''' in the same batch rather than an already-persisted record - the earlier plan's own
            ''' physical record doesn't exist yet at planning time, so it's resolved by index once
            ''' the batch has actually been placed (see the resolution pass in
            ''' BuildExtentsInParallelAsync).
            ''' </summary>
            Public DuplicateOfPlanIndex As Integer?
        End Structure

        '
        ' Splits the input into chunks with a single serial pass (DetermineNextSegmentLength -
        ' a CDC scan when Options.ChunkSizeVariance is non-zero, otherwise the same fixed-size
        ' cut ShouldBuildChunksInParallel's caller would get either way), then compresses /
        ' encrypts / authenticates the non-sparse ones on a worker pool
        ' (Options.MaxCryptoParallelism), then places them serially so the free-space
        ' allocation, physical-record table and ordinal map are only ever touched from one
        ' thread. Each worker takes its own ChunkCipher.
        '
        ' AllowGrowableTail mirrors BuildExtentsFromBufferAsync's own parameter of the same name
        ' (see its doc comment) - true only when the caller may still append to the very last
        ' segment produced later on. Applied only to the last plan in Plans, exactly as the serial
        ' path applies it only to its own last segment.
        '
        Private Async Function BuildExtentsInParallelAsync(Input As Byte(),
                                                          InputOffset As Integer,
                                                          Count As Integer,
                                                          RunAsync As Boolean,
                                                          CancellationToken As Threading.CancellationToken,
                                                          Optional AllowGrowableTail As Boolean = False) As Task(Of List(Of ExtentIndexEntry))

            _Debug_ParallelBuildInvocationCount += 1

            Dim EncryptionMethod =
                If(_CurrentWriteEncryptionEnabled,
                   ChunkEncryptionMethods.AesCtrFileMasterKey,
                   ChunkEncryptionMethods.None)

            Dim StoreSparse = Options.StoreSparseChunks = False

            Dim Plans As New List(Of ChunkBuildPlan)()
            Dim Remaining = Count
            Dim CurrentInputOffset = InputOffset

            '
            ' Maps a content hash (Base64-encoded - simplest way to get structural rather than
            ' reference equality out of a Dictionary key, and this only ever holds as many entries
            ' as one batch has chunks) to the plan index that first established it, so two
            ' identical chunks within the *same* write can dedupe against each other even though
            ' neither is in the persisted index yet (nothing in this batch is placed until every
            ' plan has been built). Only ever consulted for a plan that already missed the
            ' persisted index, so a genuinely cross-write duplicate always prefers reusing the
            ' existing on-disk record over a same-batch sibling.
            '
            Dim SeenHashes As New Dictionary(Of String, Integer)()

            While Remaining > 0

                Dim SegmentLength = DetermineNextSegmentLength(Input, CurrentInputOffset, Remaining)
                Dim Segment(SegmentLength - 1) As Byte
                Buffer.BlockCopy(Input, CurrentInputOffset, Segment, 0, SegmentLength)

                ' Same last-segment carve-out BuildExtentsFromBufferAsync's serial path applies -
                ' see AllowGrowableTail's doc comment there. A tail that hasn't genuinely closed
                ' must not freeze as a sparse extent, since a sparse extent can never itself extend.
                Dim IsGrowableTail = AllowGrowableTail AndAlso
                                     SegmentLength = Remaining AndAlso
                                     IsChunkClosedByCdcBoundary(Segment, SegmentLength) = False

                Dim Plan As New ChunkBuildPlan With {
                    .Segment = Segment,
                    .IsSparse = IsGrowableTail = False AndAlso StoreSparse AndAlso IsAllZero(Segment, SegmentLength)}

                If Plan.IsSparse = False Then

                    Dim Deduped As PhysicalRecordEntry? = Nothing
                    Dim Hash As Byte() = Nothing

                    If Options.Deduplication Then
                        Dim DedupResult = Await TryDeduplicateWriteAsync(Segment, SegmentLength, RunAsync, CancellationToken).ConfigureAwait(False)
                        Deduped = DedupResult.Match
                        Hash = DedupResult.Hash
                    End If

                    If Deduped.HasValue Then

                        Plan.IsDeduped = True
                        Plan.DedupedRecord = Deduped.Value

                        If Hash IsNot Nothing Then
                            SeenHashes(Convert.ToBase64String(Hash)) = Plans.Count
                        End If

                    Else

                        Dim LeaderIndex As Integer = -1
                        Dim HashKey = If(Hash IsNot Nothing, Convert.ToBase64String(Hash), Nothing)

                        If HashKey IsNot Nothing AndAlso SeenHashes.TryGetValue(HashKey, LeaderIndex) AndAlso
                           Plans(LeaderIndex).Segment.Length = SegmentLength AndAlso
                           PlainContentEquals(Segment, SegmentLength, Plans(LeaderIndex).Segment) Then

                            Plan.IsDeduped = True
                            Plan.DuplicateOfPlanIndex = LeaderIndex

                        Else

                            If HashKey IsNot Nothing Then SeenHashes(HashKey) = Plans.Count

                            Plan.RecordId = AllocatePhysicalRecordId()
                            Dim SubBlockCount = ComputeSubBlockCount(SegmentLength, Options.SubBlockSize)
                            Dim Ivs As Byte()() = New Byte(SubBlockCount - 1)() {}
                            For SubBlockIndex = 0 To SubBlockCount - 1
                                Dim SubIv(IvSize - 1) As Byte
                                _Rng.GetBytes(SubIv)
                                Ivs(SubBlockIndex) = SubIv
                            Next
                            Plan.Ivs = Ivs

                        End If

                    End If

                End If

                Plans.Add(Plan)

                CurrentInputOffset += SegmentLength
                Remaining -= SegmentLength

            End While

            Dim NonSparse As New List(Of Integer)()
            For Index = 0 To Plans.Count - 1
                If Plans(Index).IsSparse = False AndAlso Plans(Index).IsDeduped = False Then NonSparse.Add(Index)
            Next

            Dim PreparedByIndex(Plans.Count - 1) As PreparedChunkRecord

            Dim PrepareAll =
                Sub()
                    Dim LoopOptions As New System.Threading.Tasks.ParallelOptions With {
                        .MaxDegreeOfParallelism = Math.Max(1, Options.MaxCryptoParallelism)}

                    Try
                        System.Threading.Tasks.Parallel.ForEach(NonSparse, LoopOptions,
                            Function() CreateChunkCipher(),
                            Function(PlanIndex, LoopState, Cipher)
                                Dim P = Plans(PlanIndex)
                                PreparedByIndex(PlanIndex) =
                                    PrepareChunkRecord(P.Segment, P.Segment.Length, P.RecordId, P.Ivs,
                                                       Options.CompressionMethod, Options.CompressionRatioThreshold,
                                                       False, EncryptionMethod, False, Cipher)
                                Return Cipher
                            End Function,
                            Sub(Cipher)
                                If Cipher IsNot Nothing Then Cipher.Dispose()
                            End Sub)
                    Catch ex As AggregateException When ex.InnerExceptions.Count > 0
                        Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions(0)).Throw()
                    End Try
                End Sub

            If RunAsync Then
                Await System.Threading.Tasks.Task.Run(PrepareAll).ConfigureAwait(False)
            Else
                PrepareAll()
            End If

            '
            ' Placing every non-sparse record as one batch (rather than one PlaceChunkRecordAsync
            ' await per record) is what lets PlaceChunkRecordsAsync overlap their actual disk
            ' writes when the backing store supports it - see its own comment. NonSparse and the
            ' returned list share the same order, so zipping them back onto Plan index is exact.
            '
            Dim PlacedByPlanIndex As New Dictionary(Of Integer, PhysicalRecordEntry)(NonSparse.Count)

            If NonSparse.Count > 0 Then

                Dim PreparedBatch = NonSparse.Select(Function(PlanIndex) PreparedByIndex(PlanIndex)).ToList()
                Dim PlacedBatch = Await PlaceChunkRecordsAsync(PreparedBatch, RunAsync, CancellationToken).ConfigureAwait(False)

                For BatchIndex = 0 To NonSparse.Count - 1
                    PlacedByPlanIndex(NonSparse(BatchIndex)) = PlacedBatch(BatchIndex)
                Next

                If Options.Deduplication Then
                    For Each PlanIndex In NonSparse
                        RegisterWrittenRecordForDeduplication(Plans(PlanIndex).Segment, Plans(PlanIndex).Segment.Length, PlacedByPlanIndex(PlanIndex).RecordId)
                    Next
                End If

            End If

            '
            ' Resolve every intra-batch duplicate now that its leader's actual physical record
            ' exists - either freshly placed above, or (if the leader was itself a cross-write
            ' dedup hit) already known at planning time. A leader is never itself a duplicate, so
            ' this never needs more than one hop. Each duplicate needs its own increment: the
            ' leader's own single reference was already accounted for when it was placed or
            ' matched, and every duplicate is one more independent use of the same content.
            '
            For Index = 0 To Plans.Count - 1

                If Plans(Index).DuplicateOfPlanIndex.HasValue = False Then Continue For

                Dim LeaderIndex = Plans(Index).DuplicateOfPlanIndex.Value
                Dim LeaderPlan = Plans(LeaderIndex)

                Dim ResolvedRecordId =
                    If(LeaderPlan.IsDeduped, LeaderPlan.DedupedRecord.RecordId, PlacedByPlanIndex(LeaderIndex).RecordId)

                IncrementPhysicalRecordRefCount(ResolvedRecordId)

                Dim ResolvedPlan = Plans(Index)
                ResolvedPlan.DedupedRecord = GetPhysicalRecord(ResolvedRecordId)
                Plans(Index) = ResolvedPlan

            Next

            Dim Result As New List(Of ExtentIndexEntry)()

            For Index = 0 To Plans.Count - 1

                If Plans(Index).IsSparse Then

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = Plans(Index).Segment.Length,
                        .PhysicalRecordId = SparsePhysicalRecordId,
                        .PhysicalRecordOffset = 0})

                ElseIf Plans(Index).IsDeduped Then

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = Plans(Index).Segment.Length,
                        .PhysicalRecordId = Plans(Index).DedupedRecord.RecordId,
                        .PhysicalRecordOffset = 0})

                Else

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = Plans(Index).Segment.Length,
                        .PhysicalRecordId = PlacedByPlanIndex(Index).RecordId,
                        .PhysicalRecordOffset = 0})

                End If

            Next

            Return Result

        End Function

        Private Function BuildSparseExtents(Length As Long) As List(Of ExtentIndexEntry)
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Dim Result As New List(Of ExtentIndexEntry)()
            Dim Remaining = Length

            While Remaining > 0
                If Result.Count = Integer.MaxValue Then
                    Throw New InvalidOperationException("Sparse extent count exceeds the maximum supported list size.")
                End If

                Dim SegmentLength = CInt(Math.Min(CLng(Options.ChunkSize), Remaining))

                Result.Add(New ExtentIndexEntry With {
                    .LogicalLength = SegmentLength,
                    .PhysicalRecordId = SparsePhysicalRecordId,
                    .PhysicalRecordOffset = 0
                })

                Remaining -= SegmentLength
            End While

            Return Result
        End Function

        Private Sub InsertSparseRange(LogicalOffset As Long,
                                      Length As Long,
                                      Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            If Length <= 0 Then Return

            Dim Extents = BuildSparseExtents(Length)

            InsertExtentsCore(LogicalOffset,
                              Extents,
                              AnchorActionAtLogicalOffset)

        End Sub

        Private Sub InsertExtentsCore(LogicalOffset As Long,
                                      NewExtents As IList(Of ExtentIndexEntry),
                                      Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            If LogicalOffset < 0 OrElse LogicalOffset > _Length Then
                Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            End If

            If NewExtents Is Nothing Then Throw New ArgumentNullException(NameOf(NewExtents))
            If NewExtents.Count = 0 Then Return

            SplitExtentAt(LogicalOffset)

            Dim InsertIndex = FindExtentInsertIndex(LogicalOffset)
            Dim ExistingAnchorId As Long = 0

            If InsertIndex < _Extents.Count AndAlso
               _Extents(InsertIndex).LogicalOffset = LogicalOffset Then

                ExistingAnchorId = _Extents(InsertIndex).AnchorId

            End If

            Dim InsertLength As Long = 0
            Dim Materialised As New List(Of ExtentIndexEntry)(NewExtents.Count)

            For Each extent In NewExtents

                If extent.LogicalLength <= 0 Then Continue For

                ValidateExtentReference(extent)

                Dim NewExtent = extent
                NewExtent.LogicalOffset = 0

                Materialised.Add(NewExtent)
                InsertLength += NewExtent.LogicalLength

            Next

            If Materialised.Count = 0 OrElse InsertLength <= 0 Then Return

            If ExistingAnchorId > 0 AndAlso
               AnchorActionAtLogicalOffset = AnchorActionsAtLogicalOffset.Use Then

                Dim FirstInserted = Materialised(0)

                If FirstInserted.AnchorId > 0 AndAlso
                   FirstInserted.AnchorId <> ExistingAnchorId Then

                    Throw New InvalidOperationException(
                        "The inserted data already contains a different anchor at the target logical offset.")
                End If

                FirstInserted.AnchorId = ExistingAnchorId
                Materialised(0) = FirstInserted

                Dim ExistingExtent = _Extents(InsertIndex)
                ExistingExtent.AnchorId = 0
                _Extents(InsertIndex) = ExistingExtent

            End If

            Dim NewLayout As New List(Of ExtentIndexEntry)(_Extents.Count + Materialised.Count)

            For Index = 0 To InsertIndex - 1
                NewLayout.Add(_Extents(Index))
            Next

            NewLayout.AddRange(Materialised)

            For Index = InsertIndex To _Extents.Count - 1
                NewLayout.Add(_Extents(Index))
            Next

            RebaseExtentLogicalOffsets(NewLayout)

            MarkExtentPagesDirtyForReplacement(InsertIndex,
                                               0,
                                               Materialised.Count,
                                               Math.Max(_Extents.Count, NewLayout.Count))

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            _Length += InsertLength

            RebuildAnchorIndex()

        End Sub

        Private Sub RemoveRangeCore(LogicalOffset As Long,
                                    Length As Long,
                                    AllowRemoveBoundaryMaterialise As Boolean)

            RemoveRangeCoreAsync(LogicalOffset, Length, AllowRemoveBoundaryMaterialise, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function RemoveRangeCoreAsync(LogicalOffset As Long,
                                                   Length As Long,
                                                   AllowRemoveBoundaryMaterialise As Boolean,
                                                   RunAsync As Boolean,
                                                   CancellationToken As Threading.CancellationToken) As Task

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 OrElse LogicalOffset >= _Length Then Return

            Dim ActualLength = Math.Min(Length, _Length - LogicalOffset)
            Dim EndOffset = LogicalOffset + ActualLength

            SplitExtentAt(LogicalOffset)
            SplitExtentAt(EndOffset)

            Dim StartIndex = FindExtentInsertIndex(LogicalOffset)
            Dim EndIndex = FindExtentInsertIndex(EndOffset)

            Dim LeftBoundaryIndex = StartIndex - 1
            Dim RightBoundaryIndex = EndIndex

            Dim HasLeftBoundary =
                AllowRemoveBoundaryMaterialise AndAlso
                LeftBoundaryIndex >= 0 AndAlso
                LeftBoundaryIndex < _Extents.Count AndAlso
                GetExtentEnd(_Extents(LeftBoundaryIndex)) = LogicalOffset

            Dim HasRightBoundary =
                AllowRemoveBoundaryMaterialise AndAlso
                RightBoundaryIndex >= 0 AndAlso
                RightBoundaryIndex < _Extents.Count AndAlso
                _Extents(RightBoundaryIndex).LogicalOffset = EndOffset

            Dim MaterialiseBoundaries = False
            Dim BoundaryReplacement As ExtentIndexEntry? = Nothing

            If HasLeftBoundary AndAlso HasRightBoundary Then

                Dim LeftExtent = _Extents(LeftBoundaryIndex)
                Dim RightExtent = _Extents(RightBoundaryIndex)

                '
                ' The right boundary cannot be consumed into the left extent when it is
                ' anchored, because that would destroy its anchored start.
                '
                If RightExtent.AnchorId = 0 AndAlso
                   ShouldMaterialiseAdjacentBoundaryFragments(LeftExtent.LogicalLength,
                                                              RightExtent.LogicalLength) Then

                    Dim CombinedLength =
                        LeftExtent.LogicalLength +
                        RightExtent.LogicalLength

                    Dim Combined(CombinedLength - 1) As Byte

                    Await ReadExtentBytesEitherAsync(RunAsync, LeftExtent,
                                                     0,
                                                     Combined,
                                                     0,
                                                     LeftExtent.LogicalLength,
                                                     CancellationToken).ConfigureAwait(False)

                    Await ReadExtentBytesEitherAsync(RunAsync, RightExtent,
                                                     0,
                                                     Combined,
                                                     LeftExtent.LogicalLength,
                                                     RightExtent.LogicalLength,
                                                     CancellationToken).ConfigureAwait(False)

                    Dim Record = Await WritePhysicalRecordAsync(Combined,
                                                               CombinedLength,
                                                               RunAsync,
                                                               CancellationToken).ConfigureAwait(False)

                    BoundaryReplacement =
                        New ExtentIndexEntry With {
                            .LogicalOffset = 0,
                            .LogicalLength = CombinedLength,
                            .PhysicalRecordId = Record.RecordId,
                            .PhysicalRecordOffset = 0,
                            .AnchorId = LeftExtent.AnchorId
                        }

                    MaterialiseBoundaries = True

                End If

            End If

            Dim NewLayout As New List(Of ExtentIndexEntry)(_Extents.Count)
            Dim DirtyStartIndex As Integer
            Dim RemovedExtentCount As Integer
            Dim InsertedExtentCount As Integer

            If MaterialiseBoundaries Then

                DirtyStartIndex = LeftBoundaryIndex
                RemovedExtentCount = RightBoundaryIndex - LeftBoundaryIndex + 1
                InsertedExtentCount = 1

                For Index = 0 To LeftBoundaryIndex - 1
                    NewLayout.Add(_Extents(Index))
                Next

                NewLayout.Add(BoundaryReplacement.Value)

                For Index = RightBoundaryIndex + 1 To _Extents.Count - 1
                    NewLayout.Add(_Extents(Index))
                Next

                For Index = LeftBoundaryIndex To RightBoundaryIndex
                    DecrementPhysicalRecordRefCount(_Extents(Index).PhysicalRecordId)
                Next

            Else

                DirtyStartIndex = StartIndex
                RemovedExtentCount = EndIndex - StartIndex
                InsertedExtentCount = 0

                For Index = 0 To StartIndex - 1
                    NewLayout.Add(_Extents(Index))
                Next

                For Index = EndIndex To _Extents.Count - 1
                    NewLayout.Add(_Extents(Index))
                Next

                For Index = StartIndex To EndIndex - 1
                    DecrementPhysicalRecordRefCount(_Extents(Index).PhysicalRecordId)
                Next

            End If

            RebaseExtentLogicalOffsets(NewLayout)

            MarkExtentPagesDirtyForReplacement(DirtyStartIndex,
                                               RemovedExtentCount,
                                               InsertedExtentCount,
                                               Math.Max(_Extents.Count, NewLayout.Count))

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            _Length -= ActualLength

            RebuildAnchorIndex()

            SettleDeferredPhysicalRecordReclaims()

        End Function

        Private Sub ReplaceRangeCore(LogicalOffset As Long,
                                     Length As Long,
                                     NewExtents As IList(Of ExtentIndexEntry))

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If NewExtents Is Nothing Then Throw New ArgumentNullException(NameOf(NewExtents))
            If Length = 0 Then Return
            If LogicalOffset >= _Length Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))

            Dim ActualLength = Math.Min(Length, _Length - LogicalOffset)
            Dim EndOffset = LogicalOffset + ActualLength

            Dim AnchoredBoundaries =
                CaptureAnchoredBoundaries(LogicalOffset,
                                          ActualLength,
                                          True,
                                          False)

            Dim Materialised As New List(Of ExtentIndexEntry)(NewExtents.Count)
            Dim InsertLength As Long = 0

            For Each extent In NewExtents

                If extent.LogicalLength <= 0 Then Continue For

                ValidateExtentReference(extent)

                Dim NewExtent = extent
                NewExtent.LogicalOffset = 0
                NewExtent.AnchorId = 0

                Materialised.Add(NewExtent)
                InsertLength += NewExtent.LogicalLength

            Next

            If InsertLength <> ActualLength Then
                Throw New InvalidOperationException(
                    "Replacement extent length must match the replaced logical length.")
            End If

            Materialised =
                ApplyAnchoredBoundariesToExtents(Materialised,
                                                 AnchoredBoundaries,
                                                 ActualLength)

            SplitExtentAt(LogicalOffset)
            SplitExtentAt(EndOffset)

            Dim StartIndex = FindExtentInsertIndex(LogicalOffset)
            Dim EndIndex = FindExtentInsertIndex(EndOffset)
            Dim RemovedExtentCount = EndIndex - StartIndex

            If RemovedExtentCount <= 0 Then
                Throw New InvalidDataException(
                    "No extents were found for the replacement range.")
            End If

            Dim NewLayout As New List(Of ExtentIndexEntry)(
                _Extents.Count - RemovedExtentCount + Materialised.Count)

            For Index = 0 To StartIndex - 1
                NewLayout.Add(_Extents(Index))
            Next

            Dim CurrentLogicalOffset = LogicalOffset

            For Each extent In Materialised

                Dim NewExtent = extent
                NewExtent.LogicalOffset = CurrentLogicalOffset

                NewLayout.Add(NewExtent)

                CurrentLogicalOffset += NewExtent.LogicalLength

            Next

            For Index = EndIndex To _Extents.Count - 1
                NewLayout.Add(_Extents(Index))
            Next

            For Index = StartIndex To EndIndex - 1
                DecrementPhysicalRecordRefCount(_Extents(Index).PhysicalRecordId)
            Next

            MarkExtentPagesDirtyForReplacement(StartIndex,
                                               RemovedExtentCount,
                                               Materialised.Count,
                                               Math.Max(_Extents.Count, NewLayout.Count))

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            RebuildAnchorIndex()

            SettleDeferredPhysicalRecordReclaims()

        End Sub

        Private Shared Sub RebaseExtentLogicalOffsets(Extents As IList(Of ExtentIndexEntry))

            If Extents Is Nothing Then Throw New ArgumentNullException(NameOf(Extents))

            Dim LogicalOffset As Long = 0
            Dim AnchorIds As New HashSet(Of Long)()

            For Index = 0 To Extents.Count - 1

                Dim Extent = Extents(Index)

                If Extent.LogicalLength <= 0 Then
                    Throw New InvalidDataException(
                        "Extent has an invalid logical length.")
                End If

                If Extent.AnchorId < 0 Then
                    Throw New InvalidDataException(
                        "Extent has an invalid anchor id.")
                End If

                If Extent.AnchorId > 0 AndAlso
                   AnchorIds.Add(Extent.AnchorId) = False Then

                    Throw New InvalidDataException(
                        $"Duplicate anchor id {Extent.AnchorId}.")

                End If

                Extent.LogicalOffset = LogicalOffset
                Extents(Index) = Extent

                LogicalOffset += Extent.LogicalLength

            Next

        End Sub

        Private Function BuildCloneExtents(SourceLogicalOffset As Long,
                                           Length As Long) As List(Of ExtentIndexEntry)

            Return BuildCloneExtentsAsync(SourceLogicalOffset, Length, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Function

        Private Async Function BuildCloneExtentsAsync(SourceLogicalOffset As Long,
                                                     Length As Long,
                                                     RunAsync As Boolean,
                                                     CancellationToken As Threading.CancellationToken) As Task(Of List(Of ExtentIndexEntry))

            If SourceLogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(SourceLogicalOffset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Dim Result As New List(Of ExtentIndexEntry)()

            If Length = 0 Then Return Result

            Dim SourceEndOffset = SourceLogicalOffset + Length
            Dim CurrentOffset = SourceLogicalOffset
            Dim Segments As New List(Of ExtentIndexEntry)()

            While CurrentOffset < SourceEndOffset

                Dim ExtentIndex = FindExtentIndex(CurrentOffset)

                If ExtentIndex < 0 Then
                    Throw New InvalidDataException(
                        $"No extent found for clone source offset {CurrentOffset}.")
                End If

                Dim Extent = _Extents(ExtentIndex)
                Dim OffsetInsideExtent = CInt(CurrentOffset - Extent.LogicalOffset)

                Dim SegmentLength =
                    CInt(Math.Min(CLng(Extent.LogicalLength - OffsetInsideExtent),
                                  SourceEndOffset - CurrentOffset))

                Dim SegmentPhysicalRecordOffset As Integer

                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                    SegmentPhysicalRecordOffset = 0
                Else
                    SegmentPhysicalRecordOffset =
                        Extent.PhysicalRecordOffset + OffsetInsideExtent
                End If

                '
                ' Cloning copies bytes and physical-record references, not anchor identity.
                '
                Segments.Add(
                    New ExtentIndexEntry With {
                        .LogicalLength = SegmentLength,
                        .PhysicalRecordId = Extent.PhysicalRecordId,
                        .PhysicalRecordOffset = SegmentPhysicalRecordOffset,
                        .AnchorId = 0
                    })

                CurrentOffset += SegmentLength

            End While

            If Segments.Count = 2 AndAlso
               ShouldMaterialiseAdjacentBoundaryFragments(
                   Segments(0).LogicalLength,
                   Segments(1).LogicalLength) Then

                Dim CombinedLength =
                    Segments(0).LogicalLength +
                    Segments(1).LogicalLength

                Dim Combined(CombinedLength - 1) As Byte

                Await ReadExtentBytesEitherAsync(RunAsync, Segments(0),
                                                 0,
                                                 Combined,
                                                 0,
                                                 Segments(0).LogicalLength,
                                                 CancellationToken).ConfigureAwait(False)

                Await ReadExtentBytesEitherAsync(RunAsync, Segments(1),
                                                 0,
                                                 Combined,
                                                 Segments(0).LogicalLength,
                                                 Segments(1).LogicalLength,
                                                 CancellationToken).ConfigureAwait(False)

                Dim Record =
                    Await WritePhysicalRecordAsync(Combined,
                                                   CombinedLength,
                                                   RunAsync,
                                                   CancellationToken).ConfigureAwait(False)

                Result.Add(
                    New ExtentIndexEntry With {
                        .LogicalLength = CombinedLength,
                        .PhysicalRecordId = Record.RecordId,
                        .PhysicalRecordOffset = 0,
                        .AnchorId = 0
                    })

                Return Result

            End If

            For Each Segment In Segments

                If Segment.PhysicalRecordId = SparsePhysicalRecordId Then

                    Result.Add(
                        New ExtentIndexEntry With {
                            .LogicalLength = Segment.LogicalLength,
                            .PhysicalRecordId = SparsePhysicalRecordId,
                            .PhysicalRecordOffset = 0,
                            .AnchorId = 0
                        })

                    Continue For

                End If

                If Options.BisectLimit > 0 AndAlso
                   Segment.LogicalLength < Options.BisectLimit Then

                    Dim Buffer(Segment.LogicalLength - 1) As Byte

                    Await ReadExtentBytesEitherAsync(RunAsync, Segment,
                                                     0,
                                                     Buffer,
                                                     0,
                                                     Segment.LogicalLength,
                                                     CancellationToken).ConfigureAwait(False)

                    Dim Record =
                        Await WritePhysicalRecordAsync(Buffer,
                                                       Segment.LogicalLength,
                                                       RunAsync,
                                                       CancellationToken).ConfigureAwait(False)

                    Result.Add(
                        New ExtentIndexEntry With {
                            .LogicalLength = Segment.LogicalLength,
                            .PhysicalRecordId = Record.RecordId,
                            .PhysicalRecordOffset = 0,
                            .AnchorId = 0
                        })

                Else

                    IncrementPhysicalRecordRefCount(
                        Segment.PhysicalRecordId)

                    Dim ClonedExtent = Segment
                    ClonedExtent.AnchorId = 0

                    Result.Add(ClonedExtent)

                End If

            Next

            Return Result

        End Function

        Private Sub ValidateExtentsAreSortedAndNonOverlapping()

            Dim ExpectedOffset As Long = 0
            Dim AnchorIds As New HashSet(Of Long)()
            Dim AnchorOffsets As New HashSet(Of Long)()

            For Each extent In _Extents

                If extent.LogicalOffset < 0 Then
                    Throw New InvalidDataException(
                        "Extent has a negative logical offset.")
                End If

                If extent.LogicalOffset <> ExpectedOffset Then
                    Throw New InvalidDataException(
                        $"Extent layout contains a gap or overlap at logical offset {ExpectedOffset}.")
                End If

                If extent.AnchorId < 0 Then
                    Throw New InvalidDataException(
                        "Extent has a negative anchor id.")
                End If

                If extent.AnchorId > 0 Then

                    If AnchorIds.Add(extent.AnchorId) = False Then
                        Throw New InvalidDataException(
                            $"Duplicate anchor id {extent.AnchorId}.")
                    End If

                    If AnchorOffsets.Add(extent.LogicalOffset) = False Then
                        Throw New InvalidDataException(
                            $"Multiple anchors identify logical offset {extent.LogicalOffset}.")
                    End If

                End If

                ValidateExtentReference(extent)

                ExpectedOffset =
                    extent.LogicalOffset +
                    CLng(extent.LogicalLength)

            Next

            If ExpectedOffset <> _Length Then
                Throw New InvalidDataException(
                    $"Extent logical length mismatch. Expected {_Length}, found {ExpectedOffset}.")
            End If

            ValidateAnchors()

        End Sub

        Private Sub ValidateExtentReference(Extent As ExtentIndexEntry)

            If Extent.LogicalLength <= 0 Then
                Throw New InvalidDataException("Extent has an invalid logical length.")
            End If

            If Extent.PhysicalRecordOffset < 0 Then
                Throw New InvalidDataException("Extent has a negative physical record offset.")
            End If

            If Extent.PhysicalRecordId = SparsePhysicalRecordId Then

                If Extent.PhysicalRecordOffset <> 0 Then
                    Throw New InvalidDataException("Sparse extent has a non-zero physical record offset.")
                End If

                Return

            End If

            Dim Record = GetPhysicalRecord(Extent.PhysicalRecordId)

            If Extent.PhysicalRecordOffset + Extent.LogicalLength > Record.PlainLength Then
                Throw New InvalidDataException($"Extent references beyond physical record {Extent.PhysicalRecordId}.")
            End If

        End Sub

        Private Sub ValidatePhysicalRecordRefCounts()

            Dim ActualCounts As New Dictionary(Of Long, Integer)()

            For Each extent In _Extents

                If extent.PhysicalRecordId = SparsePhysicalRecordId Then Continue For

                Dim CurrentCount As Integer = 0

                ActualCounts.TryGetValue(extent.PhysicalRecordId, CurrentCount)
                ActualCounts(extent.PhysicalRecordId) = CurrentCount + 1

            Next

            For Each pair In _PhysicalRecords

                Dim ActualCount As Integer = 0

                ActualCounts.TryGetValue(pair.Key, ActualCount)

                If pair.Value.RefCount <> ActualCount Then
                    Throw New InvalidDataException($"Refcount mismatch for physical record {pair.Key}. Expected {ActualCount}, found {pair.Value.RefCount}.")
                End If

            Next

        End Sub

    End Class
End Namespace