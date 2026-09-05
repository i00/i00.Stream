Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Private NotInheritable Class FreeSpaceAllocator

            '
            ' Free spans used to live in one SortedDictionary(Of Long, Long) keyed by offset,
            ' with every lookup (BestFit's "smallest sufficient hole", and Add's "what touches
            ' this span") scanning every entry. Both are hot paths - Add runs on every physical
            ' record reclaim, TryAllocate on every BestFit write - and both degrade linearly
            ' with the number of free spans, which grows with archive churn, not archive size.
            '
            ' An earlier version of this fix used SortedSet(Of Long).GetViewBetween for both
            ' queries - which turned out to be a regression, not a win: .NET's TreeSubSet (the
            ' view GetViewBetween returns) computes Count - and Min/Max, which check Count - by
            ' walking the view, not via the O(log n) navigation a real SortedSet gives. So a
            ' view is O(view size) per call, same complexity class as the original scan with
            ' extra overhead on top. Measured: it made the benchmark below ~2x *slower*.
            '
            ' What actually ships is classic boundary-tag coalescing (the K&R malloc/free
            ' technique) plus a plain-SortedSet ascending scan bounded by *distinct lengths*
            ' rather than free-span count:
            '   _OffsetIndex     - every free offset, sorted; only used for the linear-scan
            '                      fallback paths (FirstFit; MinOffset/MaxWaste-restricted
            '                      BestFit) and CloneSpaces/Snapshot enumeration order.
            '   _LengthByOffset  - O(1) length lookup for a known start offset. Doubles as the
            '                      "does a free span start exactly here" check Add's right-side
            '                      merge needs.
            '   _StartByEnd      - end offset -> start offset, so Add's left-side merge is an
            '                      O(1) "does a free span end exactly at my start" lookup
            '                      instead of a predecessor search.
            '   _OffsetsByLength - offsets sharing a length, so BestFit's original offset-order
            '                      tie-break (smallest offset among equal-length holes) still
            '                      holds.
            '   _DistinctLengths - just the lengths present. Chunk sizes cluster around a
            '                      handful of values, so an ascending scan for "smallest length
            '                      >= Required" costs O(distinct lengths), not O(free spans) -
            '                      order-of-magnitude fewer in practice, and immune to the
            '                      TreeSubSet trap above because it enumerates the real set.
            ' CloneSpaces/RestoreSpaces/Snapshot keep their original shape so checkpoint state
            ' capture/restore and hole-directory persistence need no changes.
            '
            Private ReadOnly _OffsetIndex As New SortedSet(Of Long)()
            Private ReadOnly _LengthByOffset As New Dictionary(Of Long, Long)()
            Private ReadOnly _StartByEnd As New Dictionary(Of Long, Long)()
            Private ReadOnly _OffsetsByLength As New Dictionary(Of Long, SortedSet(Of Long))()
            Private ReadOnly _DistinctLengths As New SortedSet(Of Long)()

            Private Sub Insert(Offset As Long, Length As Long)

                _OffsetIndex.Add(Offset)
                _LengthByOffset(Offset) = Length
                _StartByEnd(Offset + Length) = Offset

                Dim Bucket As SortedSet(Of Long) = Nothing
                If _OffsetsByLength.TryGetValue(Length, Bucket) = False Then
                    Bucket = New SortedSet(Of Long)()
                    _OffsetsByLength(Length) = Bucket
                    _DistinctLengths.Add(Length)
                End If
                Bucket.Add(Offset)

            End Sub

            Private Sub RemoveSpan(Offset As Long)

                Dim Length = _LengthByOffset(Offset)
                _OffsetIndex.Remove(Offset)
                _LengthByOffset.Remove(Offset)
                _StartByEnd.Remove(Offset + Length)

                Dim Bucket = _OffsetsByLength(Length)
                Bucket.Remove(Offset)
                If Bucket.Count = 0 Then
                    _OffsetsByLength.Remove(Length)
                    _DistinctLengths.Remove(Length)
                End If

            End Sub

            Public Function CloneSpaces() As SortedDictionary(Of Long, Long)

                Dim Result As New SortedDictionary(Of Long, Long)()
                For Each Offset In _OffsetIndex
                    Result(Offset) = _LengthByOffset(Offset)
                Next
                Return Result

            End Function

            Public Sub RestoreSpaces(Spaces As SortedDictionary(Of Long, Long))

                _OffsetIndex.Clear()
                _LengthByOffset.Clear()
                _StartByEnd.Clear()
                _OffsetsByLength.Clear()
                _DistinctLengths.Clear()

                If Spaces Is Nothing Then Return

                For Each pair In Spaces
                    Insert(pair.Key, pair.Value)
                Next

            End Sub

            Public Sub Add(Offset As Long, Length As Long)

                If Offset < DataStartOffset Then Return
                If Length <= 0 Then Return
                If Offset > Long.MaxValue - Length Then Throw New ArgumentOutOfRangeException(NameOf(Length))

                Dim MergedStart = Offset
                Dim MergedEnd = Offset + Length

                '
                ' Boundary-tag coalescing: a free span can only ever touch the new one at an
                ' EXACT address (spans never overlap by construction), so "is there a left
                ' neighbour" is "does a free span end exactly at MergedStart" (_StartByEnd) and
                ' "is there a right neighbour" is "does a free span start exactly at MergedEnd"
                ' (_LengthByOffset) - both O(1) dictionary lookups, no tree/scan involved. The
                ' While loops chase a run of several touching spans (e.g. three tiny adjacent
                ' holes becoming one) exactly as the original scan did.
                '
                Dim LeftOffset As Long
                While _StartByEnd.TryGetValue(MergedStart, LeftOffset)
                    MergedStart = LeftOffset
                    RemoveSpan(LeftOffset)
                End While

                Dim RightLength As Long
                While _LengthByOffset.TryGetValue(MergedEnd, RightLength)
                    RemoveSpan(MergedEnd)
                    MergedEnd += RightLength
                End While

                Insert(MergedStart, MergedEnd - MergedStart)

            End Sub

            Public Function TryAllocate(RequiredLength As Long, FromStart As Boolean, ByRef Offset As Long) As Boolean

                Return TryAllocate(RequiredLength, FromStart, 0, Offset)

            End Function

            '
            ' MinOffset restricts the search to holes that begin at or after it, so an
            ' in-checkpoint metadata write can be confined to the scratch region above the
            ' checkpoint mark.
            '
            Public Function TryAllocate(RequiredLength As Long, FromStart As Boolean, MinOffset As Long, ByRef Offset As Long) As Boolean

                Return TryAllocate(RequiredLength, FromStart, MinOffset, Long.MaxValue, Offset)

            End Function

            '
            ' MaxWaste caps how much larger than RequiredLength a reused hole may be, so a
            ' small payload (the metadata root) never splits a much larger data hole into a
            ' sliver too small to hold a record.
            '
            Public Function TryAllocate(RequiredLength As Long, FromStart As Boolean, MinOffset As Long, MaxWaste As Long, ByRef Offset As Long) As Boolean

                If RequiredLength <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(RequiredLength))
                If MaxWaste < 0 Then Throw New ArgumentOutOfRangeException(NameOf(MaxWaste))

                Dim SelectedOffset As Long = -1
                Dim SelectedLength As Long = Long.MaxValue

                '
                ' BestFit with no offset/waste restriction - every chunk and index-page write
                ' outside an in-checkpoint metadata scratch region - is the bulk hot path.
                ' Chunk sizes cluster around a handful of values in practice, so scanning the
                ' *distinct lengths* ascending (a plain SortedSet, not a GetViewBetween view -
                ' see the class comment on why a view is the wrong tool here) for the first one
                ' >= RequiredLength costs O(distinct lengths), not O(free spans). FirstFit
                ' (offset order matters, not length) and the narrow-scope restricted callers
                ' (in-checkpoint metadata, the snug metadata-root hole) keep the original
                ' linear scan, unchanged.
                '
                If FromStart = False AndAlso MinOffset = 0 AndAlso MaxWaste = Long.MaxValue Then

                    For Each CandidateLength In _DistinctLengths
                        If CandidateLength >= RequiredLength Then
                            SelectedLength = CandidateLength
                            Exit For
                        End If
                    Next

                    If SelectedLength = Long.MaxValue Then
                        Offset = -1
                        Return False
                    End If

                    SelectedOffset = _OffsetsByLength(SelectedLength).Min

                Else

                    For Each ExistingOffset In _OffsetIndex

                        If ExistingOffset < MinOffset Then Continue For
                        Dim ExistingLength = _LengthByOffset(ExistingOffset)
                        If ExistingLength < RequiredLength Then Continue For
                        If ExistingLength - RequiredLength > MaxWaste Then Continue For

                        If FromStart Then
                            SelectedOffset = ExistingOffset
                            SelectedLength = ExistingLength
                            Exit For
                        End If

                        If ExistingLength < SelectedLength Then
                            SelectedOffset = ExistingOffset
                            SelectedLength = ExistingLength
                        End If

                    Next

                    If SelectedOffset < 0 Then
                        Offset = -1
                        Return False
                    End If

                End If

                RemoveSpan(SelectedOffset)
                Offset = SelectedOffset

                Dim RemainingLength = SelectedLength - RequiredLength

                If RemainingLength > 0 Then
                    Add(SelectedOffset + RequiredLength, RemainingLength)
                End If

                Return True

            End Function

            Public Function Snapshot() As List(Of HoleDirectoryRecord)

                Return _OffsetIndex.
                       Select(Function(offset) New HoleDirectoryRecord With {
                           .SpaceType = HoleSpaceTypes.FreeSpace,
                           .Offset = offset,
                           .Length = _LengthByOffset(offset)
                       }).
                       ToList()

            End Function

            Public Sub Clear()
                _OffsetIndex.Clear()
                _LengthByOffset.Clear()
                _StartByEnd.Clear()
                _OffsetsByLength.Clear()
                _DistinctLengths.Clear()
            End Sub

        End Class

        Private ReadOnly _FreeSpaces As New FreeSpaceAllocator()

        Friend Structure DeferredFreeRange
            Public Offset As Long
            Public Length As Long
            Public FreedAtHeaderSequence As Long
        End Structure

        '
        ' Storage freed by superseding a metadata page, metadata root or chunk record is
        ' parked here instead of being made immediately allocatable.
        '
        ' Crash recovery selects the newest valid header copy, and Open falls back to an
        ' older copy when the newest generation's metadata cannot be loaded. So the
        ' generations described by the header copies currently on disk - up to
        ' HeaderCopyCount of them - must all stay byte-for-byte intact. A generation's
        ' header slot is not overwritten until HeaderCopyCount further publishes have
        ' happened, so a freed span is only safe to reuse once the header sequence has
        ' advanced by at least HeaderCopyCount since it was freed. ReleaseDeferredFreeSpace,
        ' called after every header rotation, moves the now-safe spans into _FreeSpaces.
        '
        ' Space written and freed within the same rolling window is still reused promptly
        ' (after HeaderCopyCount publishes), which keeps churn inside a long uncommitted
        ' write compact.
        '
        Private ReadOnly _DeferredFreeRanges As New List(Of DeferredFreeRange)()

        Private Shared Function StorageRangesOverlap(Offset1 As Long,
                                                     Length1 As Long,
                                                     Offset2 As Long,
                                                     Length2 As Long) As Boolean

            Return Offset1 < Offset2 + Length2 AndAlso Offset2 < Offset1 + Length1

        End Function

        Private Function GetActiveMetadataRanges() As List(Of Tuple(Of Long, Long))

            Dim Result As New List(Of Tuple(Of Long, Long))()

            For Each Descriptor In _ExtentPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _ExtentDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _PhysicalRecordPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _PhysicalRecordDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _HoleDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then
                Result.Add(Tuple.Create(_MetadataRootOffset, _MetadataRootOffset + CLng(_MetadataRootLength)))
            End If

            Return Result

        End Function

        Private Function RangeOverlapsLivePhysicalRecord(Offset As Long,
                                                         Length As Long) As Boolean

            If Length <= 0 Then Return False
            If _LivePhysicalRecordIdsByOffset.Count = 0 Then Return False
            If Offset > Long.MaxValue - Length Then Return True

            Dim EndOffset = Offset + Length
            Dim Low = 0
            Dim High = _LivePhysicalRecordIdsByOffset.Count

            '
            ' Find the first live physical record whose starting offset is greater
            ' than or equal to the requested range start.
            '
            While Low < High

                Dim Middle = Low + ((High - Low) \ 2)

                If _LivePhysicalRecordIdsByOffset.Keys(Middle) < Offset Then
                    Low = Middle + 1
                Else
                    High = Middle
                End If

            End While

            Dim CandidateIndex = Low

            '
            ' The preceding record may begin before the requested range and extend
            ' into it.
            '
            If CandidateIndex > 0 Then

                Dim PreviousRecordId =
                    _LivePhysicalRecordIdsByOffset.Values(CandidateIndex - 1)

                Dim PreviousRecord = GetPhysicalRecord(PreviousRecordId)

                If StorageRangesOverlap(Offset,
                                        Length,
                                        PreviousRecord.PhysicalOffset,
                                        PreviousRecord.PhysicalLength) Then

                    Return True

                End If

            End If

            '
            ' The first record beginning at or after Offset overlaps when its start
            ' occurs before EndOffset.
            '
            If CandidateIndex < _LivePhysicalRecordIdsByOffset.Count Then

                Dim CandidateOffset =
                    _LivePhysicalRecordIdsByOffset.Keys(CandidateIndex)

                If CandidateOffset < EndOffset Then
                    Return True
                End If

            End If

            Return False

        End Function

        Private Function RangeOverlapsActiveMetadata(Offset As Long,
                                                     Length As Long) As Boolean

            If Length <= 0 Then Return False

            For Each Range In GetActiveMetadataRanges()

                If StorageRangesOverlap(Offset,
                                        Length,
                                        Range.Item1,
                                        Range.Item2 - Range.Item1) Then

                    Return True

                End If

            Next

            Return False

        End Function

        Private Function IsRangeSafeForPhysicalRecord(Offset As Long,
                                                      Length As Long) As Boolean

            If Offset < DataStartOffset Then Return False
            If Length <= 0 Then Return False
            If RangeOverlapsLivePhysicalRecord(Offset, Length) Then Return False
            If RangeOverlapsActiveMetadata(Offset, Length) Then Return False

            Return True

        End Function

        Private Function IsRangeSafeForMetadata(Offset As Long,
                                                Length As Long) As Boolean

            If Offset < DataStartOffset Then Return False
            If Length <= 0 Then Return False
            If RangeOverlapsLivePhysicalRecord(Offset, Length) Then Return False

            Return True

        End Function

        Private Sub ClearFreeSpaceMap()
            _FreeSpaces.Clear()
            _DeferredFreeRanges.Clear()
        End Sub

        Private Sub AddFreeSpace(Offset As Long, Length As Long)
            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return
            _FreeSpaces.Add(Offset, Length)
        End Sub

        '
        ' Frees space that has just been superseded. It is parked in _DeferredFreeRanges,
        ' tagged with the current header sequence, and only becomes allocatable once
        ' ReleaseDeferredFreeSpace sees the sequence advance by HeaderCopyCount - by then
        ' the header slot that referenced it has been overwritten. See _DeferredFreeRanges.
        '
        Private Sub DeferFreeSpace(Offset As Long, Length As Long)
            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return
            _DeferredFreeRanges.Add(New DeferredFreeRange With {
                .Offset = Offset,
                .Length = Length,
                .FreedAtHeaderSequence = _HeaderSequence
            })
        End Sub

        '
        ' Moves every deferred span whose freeing header sequence is now at least
        ' HeaderCopyCount publishes old into the allocatable free-space map. Called after
        ' each header rotation (WriteHeaderCopies), once the sequence has already been
        ' incremented for that rotation.
        '
        Private Sub ReleaseDeferredFreeSpace()

            If _DeferredFreeRanges.Count = 0 Then Return

            Dim Kept As New List(Of DeferredFreeRange)(_DeferredFreeRanges.Count)

            For Each DeferredRange In _DeferredFreeRanges
                If _HeaderSequence - DeferredRange.FreedAtHeaderSequence >= HeaderCopyCount Then
                    AddFreeSpace(DeferredRange.Offset, DeferredRange.Length)
                Else
                    Kept.Add(DeferredRange)
                End If
            Next

            _DeferredFreeRanges.Clear()
            _DeferredFreeRanges.AddRange(Kept)

        End Sub

        Private Function GetKnownHoleRecords() As List(Of HoleDirectoryRecord)
            '
            ' Advertise the spans already in _FreeSpaces plus the ones the header rotation
            ' about to follow this call will release. That rotation makes those spans
            ' genuinely free before the header pointing at this directory becomes the
            ' newest generation, so the next Open can safely treat them as reusable.
            ' Spans still inside the crash-recovery window are withheld; a later scan or
            ' defragment recovers them.
            '
            Dim Records = _FreeSpaces.Snapshot()

            For Each DeferredRange In _DeferredFreeRanges
                If (_HeaderSequence + 1) - DeferredRange.FreedAtHeaderSequence >= HeaderCopyCount Then
                    Records.Add(New HoleDirectoryRecord With {
                        .SpaceType = HoleSpaceTypes.FreeSpace,
                        .Offset = DeferredRange.Offset,
                        .Length = DeferredRange.Length
                    })
                End If
            Next

            Return Records
        End Function

        Private Sub LoadKnownHoleRecords(Records As IEnumerable(Of HoleDirectoryRecord))

            If Records Is Nothing Then Return

            For Each Record In Records
                If Record.SpaceType = HoleSpaceTypes.None Then Continue For
                If IsRangeSafeForPhysicalRecord(Record.Offset, Record.Length) Then
                    _FreeSpaces.Add(Record.Offset, Record.Length)
                End If
            Next

        End Sub

        Private Function GetNextWriteOffset(Length As Integer,
                                            Policy As ChunkedStreamOptions.NewWriteLocationPolicies,
                                            IsMetadata As Boolean) As Long

            If Length <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If IsMetadata = False AndAlso Length < MinChunkRecordSize Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Dim Offset As Long

            If IsMetadata AndAlso TryGetCompactMetadataWriteOffset(Length, Offset) Then
                If IsRangeSafeForMetadata(Offset, Length) = False Then
                    Throw New InvalidOperationException($"Compact metadata allocation overlaps live physical data. Offset={Offset}, Length={Length}.")
                End If
                Return Offset
            End If

            Dim InCheckpoint = HasOpenCheckpoint

            Select Case Policy

                Case ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                     ChunkedStreamOptions.NewWriteLocationPolicies.BestFitScan,
                     ChunkedStreamOptions.NewWriteLocationPolicies.FirstFit,
                     ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan

                    Dim FromStart = Policy = ChunkedStreamOptions.NewWriteLocationPolicies.FirstFit OrElse
                                    Policy = ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan

                    If TryAllocateSafeSpace(Length, IsMetadata, FromStart, Offset) Then
                        Return Offset
                    End If

                    '
                    ' The scan variants rebuild the free-space map from the live layout when
                    ' the known holes cannot satisfy the request. That rebuild is unsafe
                    ' while a checkpoint is open - BuildFreeSpaceMapCore counts a record the
                    ' checkpoint just deleted (reference count zero, reclaim deferred to
                    ' commit) as free, and a rollback still needs its bytes - so a
                    ' checkpointed write considers only the holes already known.
                    '
                    If InCheckpoint = False AndAlso
                       (Policy = ChunkedStreamOptions.NewWriteLocationPolicies.BestFitScan OrElse
                        Policy = ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan) Then
                        BuildFreeSpaceMapCore()
                        If TryAllocateSafeSpace(Length, IsMetadata, True, Offset) Then
                            Return Offset
                        End If
                    End If

            End Select

            Return Math.Max(Math.Max(BaseStream.Length, GetDataEndFromIndex()), _IndexOffset)

        End Function

        Private Function TryAllocateSafeSpace(Length As Integer,
                                              IsMetadata As Boolean,
                                              FromStart As Boolean,
                                              ByRef Offset As Long) As Boolean

            Dim CandidateOffset As Long

            '
            ' While a checkpoint is open, new metadata is kept in the scratch region at or
            ' above the outermost checkpoint mark. A rollback or crash reloads the
            ' pre-checkpoint durable state and truncates there, so a metadata page written
            ' below the mark could overwrite one that state still needs; staying above the
            ' mark also leaves the larger pre-checkpoint holes intact for chunk records.
            ' Chunk records themselves may fill any safe hole - a rollback simply leaves
            ' their bytes as unreferenced free space, and a crash reloads a generation that
            ' never pointed into the hole.
            '
            Dim MinOffset As Long = 0

            If IsMetadata AndAlso HasOpenCheckpoint Then
                MinOffset = _CheckpointStack(0).State.PhysicalLength
            End If

            While _FreeSpaces.TryAllocate(Length, FromStart, MinOffset, CandidateOffset)

                Dim IsSafe = If(IsMetadata,
                                IsRangeSafeForMetadata(CandidateOffset, Length),
                                IsRangeSafeForPhysicalRecord(CandidateOffset, Length))

                If IsSafe Then
                    Offset = CandidateOffset
                    Return True
                End If

            End While

            Offset = -1
            Return False

        End Function

        '
        ' Places the metadata root, which is otherwise append-only, into a freed hole it
        ' very nearly fills. Recycling superseded root generations this way stops every
        ' hole-directory-bearing durable publish from growing the file by one root length,
        ' while the snug fit (waste capped at the root length) keeps the root from carving a
        ' chunk-sized data hole into an unusable sliver. Not used while a checkpoint is
        ' open - the root then follows the scratch-region rules like any other metadata.
        '
        Private Function TryAllocateSnugMetadataRootHole(Length As Integer, ByRef Offset As Long) As Boolean

            If HasOpenCheckpoint Then
                Offset = -1
                Return False
            End If

            Dim CandidateOffset As Long

            While _FreeSpaces.TryAllocate(Length, False, 0, CLng(Length), CandidateOffset)

                If IsRangeSafeForMetadata(CandidateOffset, Length) Then
                    Offset = CandidateOffset
                    Return True
                End If

            End While

            Offset = -1
            Return False

        End Function

        ''' <summary>
        ''' Discards the in-memory free-space map and rebuilds it from scratch by scanning
        ''' the current physical layout, so that later writes can immediately reuse every
        ''' hole that currently exists in the data area.
        ''' </summary>
        ''' <remarks>
        ''' The free-space map is the index that the <c>BestFit</c>, <c>BestFitScan</c>,
        ''' <c>FirstFit</c> and <c>FirstFitScan</c> write-location policies consult when
        ''' choosing where to place a new physical record or metadata page. Any span between
        ''' the data-start offset and the furthest of the backing-stream length, the index
        ''' offset and the live data end that is not covered by a live physical record or an
        ''' active metadata page is treated as free.
        '''
        ''' Calling this is normally unnecessary. The map is maintained incrementally as
        ''' records are written and freed, is loaded from the persisted hole directory when
        ''' the stream is opened, is rebuilt automatically after <see cref="Defragment"/> and
        ''' after a cancelled or failed <see cref="ApplyOptions"/> chunk-size rewrite, and is
        ''' rebuilt on demand by <c>BestFitScan</c> and <c>FirstFitScan</c> when a write
        ''' cannot be satisfied from the holes already known. It is worth calling explicitly
        ''' when using <c>BestFit</c> or <c>FirstFit</c> (which never rebuild the map on their
        ''' own) and you want hole reuse to pick up holes that have appeared since the map was
        ''' last built - for example after a batch of edits - without closing and reopening
        ''' the stream.
        '''
        ''' This only refreshes an in-memory index: it does not move data, change logical
        ''' content or write anything. The refreshed map reaches the persisted hole
        ''' directory on the next metadata write, subject to the configured hole-directory
        ''' mode.
        ''' </remarks>
        Public Sub BuildFreeSpaceMap()

            Using EnterStateLock()
                ThrowIfDisposed()
                BuildFreeSpaceMapCore()
            End Using

        End Sub

        Private Sub BuildFreeSpaceMapCore()

            _FreeSpaces.Clear()

            Dim ReservedRanges As New List(Of Tuple(Of Long, Long))()

            For Each Record In _PhysicalRecords.Values
                If Record.RefCount <= 0 Then Continue For
                ReservedRanges.Add(Tuple.Create(Record.PhysicalOffset,
                                                Record.PhysicalOffset + CLng(Record.PhysicalLength)))
            Next

            ReservedRanges.AddRange(GetActiveMetadataRanges())

            '
            ' Deferred spans are still inside the crash-recovery window, so treat them as
            ' reserved while rebuilding the map. They are folded back in by
            ' ReleaseDeferredFreeSpace as the header sequence advances. ClearFreeSpaceMap
            ' discards the deferred list outright for the authoritative rebuilds performed
            ' by defragmentation.
            '
            For Each DeferredRange In _DeferredFreeRanges
                ReservedRanges.Add(Tuple.Create(DeferredRange.Offset, DeferredRange.Offset + DeferredRange.Length))
            Next

            ReservedRanges = ReservedRanges.
                             Where(Function(range) range.Item2 > DataStartOffset AndAlso range.Item2 > range.Item1).
                             OrderBy(Function(range) range.Item1).
                             ToList()

            Dim ScanEnd = Math.Max(Math.Max(BaseStream.Length, _IndexOffset), GetDataEndFromIndex())
            Dim Cursor = CLng(DataStartOffset)

            For Each Range In ReservedRanges

                Dim RangeStart = Math.Max(CLng(DataStartOffset), Range.Item1)
                Dim RangeEnd = Range.Item2

                If RangeEnd <= Cursor Then Continue For

                If RangeStart > Cursor Then
                    AddFreeSpace(Cursor, RangeStart - Cursor)
                End If

                Cursor = Math.Max(Cursor, RangeEnd)

            Next

            If ScanEnd > Cursor Then
                AddFreeSpace(Cursor, ScanEnd - Cursor)
            End If

        End Sub

        Private Function WritePhysicalRecord(Plain As Byte(),
                                             PlainLength As Integer) As PhysicalRecordEntry

            Return WritePhysicalRecordAsync(Plain, PlainLength, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Function

        Private Function WritePhysicalRecordWithPolicy(Plain As Byte(),
                                                       PlainLength As Integer,
                                                       CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods,
                                                       CompressionRatioThreshold As Double,
                                                       ForceCompression As Boolean,
                                                       EncryptionMethod As ChunkEncryptionMethods,
                                                       Optional EvaluateFully As Boolean = False) As PhysicalRecordEntry

            Return WritePhysicalRecordWithPolicyAsync(Plain,
                                                      PlainLength,
                                                      CompressionMethodToUse,
                                                      CompressionRatioThreshold,
                                                      ForceCompression,
                                                      EncryptionMethod,
                                                      EvaluateFully,
                                                      RunAsync:=False,
                                                      CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Function

        Private Function WritePhysicalRecordAsync(Plain As Byte(),
                                                  PlainLength As Integer,
                                                  RunAsync As Boolean,
                                                  CancellationToken As Threading.CancellationToken) As Task(Of PhysicalRecordEntry)

            Dim EncryptionMethod =
                If(_CurrentWriteEncryptionEnabled,
                   ChunkEncryptionMethods.AesCtrFileMasterKey,
                   ChunkEncryptionMethods.None)

            Return WritePhysicalRecordWithPolicyAsync(Plain,
                                                      PlainLength,
                                                      Options.CompressionMethod,
                                                      Options.CompressionRatioThreshold,
                                                      False,
                                                      EncryptionMethod,
                                                      EvaluateFully:=False,
                                                      RunAsync:=RunAsync,
                                                      CancellationToken:=CancellationToken)

        End Function

        '
        ' EvaluateFully forces a full-plaintext compression evaluation even when
        ' Options.CompressionEvaluation is Sampled - used by ApplyOptions so a chunk it
        ' rewrites always ends up with an exact, non-estimated evaluation.
        '
        Private Async Function WritePhysicalRecordWithPolicyAsync(Plain As Byte(),
                                                                  PlainLength As Integer,
                                                                  CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods,
                                                                  CompressionRatioThreshold As Double,
                                                                  ForceCompression As Boolean,
                                                                  EncryptionMethod As ChunkEncryptionMethods,
                                                                  EvaluateFully As Boolean,
                                                                  RunAsync As Boolean,
                                                                  CancellationToken As Threading.CancellationToken) As Task(Of PhysicalRecordEntry)

            Dim Iv(IvSize - 1) As Byte
            _Rng.GetBytes(Iv)

            Dim Prepared =
                PrepareChunkRecord(Plain, PlainLength, AllocatePhysicalRecordId(), Iv,
                                   CompressionMethodToUse, CompressionRatioThreshold, ForceCompression,
                                   EncryptionMethod, EvaluateFully, _ChunkCipher)

            Return Await PlaceChunkRecordAsync(Prepared, RunAsync, CancellationToken).ConfigureAwait(False)

        End Function

        Private Structure PreparedChunkRecord
            Public RecordId As Long
            Public StoredRecord As Byte()
            Public PlainLength As Integer
            Public StoredCompressionMethod As ChunkedStreamOptions.CompressionMethods
        End Structure

        '
        ' Pure CPU: applies the compression policy, encrypts and authenticates one chunk into
        ' a complete stored record. Touches no shared stream state - given its own Cipher
        ' (Nothing only when the chunk is not encrypted) it is safe to run on a worker thread.
        ' The record id and IV are supplied because those are drawn serially by the caller,
        ' and MarkCompressionFlag (which mutates the header flags) is left to PlaceChunkRecord.
        '
        Private Function PrepareChunkRecord(Plain As Byte(),
                                            PlainLength As Integer,
                                            RecordId As Long,
                                            Iv As Byte(),
                                            CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods,
                                            CompressionRatioThreshold As Double,
                                            ForceCompression As Boolean,
                                            EncryptionMethod As ChunkEncryptionMethods,
                                            EvaluateFully As Boolean,
                                            Cipher As ChunkCipher) As PreparedChunkRecord

            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If PlainLength < 0 OrElse PlainLength > Plain.Length Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))

            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim StoredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedPercent As Byte = 100
            Dim Flags = ChunkFlags.None

            If CompressionMethodToUse <> ChunkedStreamOptions.CompressionMethods.None AndAlso PlainLength > 0 Then

                Dim SampledEvaluation =
                    EvaluateFully = False AndAlso
                    ForceCompression = False AndAlso
                    Options.CompressionEvaluation = ChunkedStreamOptions.CompressionEvaluationStates.Sampled AndAlso
                    PlainLength >= CompressionSampleMinimumChunkBytes

                Dim SkipFullCompression = False

                If SampledEvaluation Then

                    Dim SampleLength = Math.Min(PlainLength, CompressionSampleBytes)
                    Dim SampleCompressed = CompressPayload(CompressionMethodToUse, Plain, SampleLength)
                    Dim SamplePercent = GetCompressionEvaluatedPercent(SampleLength, SampleCompressed.Length)

                    If SamplePercent / 100.0R > CompressionRatioThreshold Then
                        '
                        ' The sample failed the threshold, so the full chunk almost certainly
                        ' would too. Skip the full compression, store the sample estimate and
                        ' flag the chunk so ApplyOptions re-evaluates it exactly.
                        '
                        SkipFullCompression = True
                        CompressionEvaluatedMethod = CompressionMethodToUse
                        CompressionEvaluatedPercent = SamplePercent
                        Flags = Flags Or ChunkFlags.CompressionEstimated
                    End If

                End If

                If SkipFullCompression = False Then

                    Dim Compressed = CompressPayload(CompressionMethodToUse, Plain, PlainLength)

                    CompressionEvaluatedMethod = CompressionMethodToUse
                    CompressionEvaluatedPercent =
                        GetCompressionEvaluatedPercent(PlainLength, Compressed.Length)

                    If ForceCompression OrElse
                       CompressionEvaluatedPercent / 100.0R <= CompressionRatioThreshold Then

                        Payload = Compressed
                        PayloadLength = Compressed.Length
                        StoredCompressionMethod = CompressionMethodToUse

                    End If

                End If

            End If

            Dim PlaintextAllZero =
                PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)

            If PlaintextAllZero Then
                Flags = Flags Or ChunkFlags.PlaintextAllZero
            End If

            Dim RecordLength = ChunkRecordDataOffset + PayloadLength + MacSize
            Dim StoredRecord(RecordLength - 1) As Byte

            Buffer.BlockCopy(BitConverter.GetBytes(RecordId), 0, StoredRecord, 0, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(StoredCompressionMethod)), 0, StoredRecord, ChunkCompressionMethodOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(EncryptionMethod)), 0, StoredRecord, ChunkEncryptionMethodOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, StoredRecord, ChunkPlainLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, StoredRecord, ChunkPayloadLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(Flags)), 0, StoredRecord, ChunkFlagsOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(CompressionEvaluatedMethod)), 0, StoredRecord, ChunkCompressionEvaluatedMethodOffset, 4)

            StoredRecord(ChunkCompressionEvaluatedPercentOffset) =
                CompressionEvaluatedPercent

            Buffer.BlockCopy(Iv, 0, StoredRecord, ChunkRecordIvOffset, IvSize)

            Select Case EncryptionMethod

                Case ChunkEncryptionMethods.None

                    If PayloadLength > 0 Then
                        Buffer.BlockCopy(Payload, 0, StoredRecord, ChunkRecordDataOffset, PayloadLength)
                    End If

                Case ChunkEncryptionMethods.AesCtrFileMasterKey

                    If Cipher Is Nothing OrElse _ChunkMacKey Is Nothing Then
                        Throw New EncryptionMismatchException(
                            "Encryption is enabled but no file master key is available.")
                    End If

                    Cipher.Crypt(Iv, 0, Payload, 0, PayloadLength, StoredRecord, ChunkRecordDataOffset)

                Case Else

                    Throw New InvalidDataException($"Unsupported chunk encryption method: {CInt(EncryptionMethod)}.")

            End Select

            Dim RecordMacKey =
                If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                   _ChunkMacKey,
                   PublicIntegrityKey)

            Using Hmac As New HMACSHA256(RecordMacKey)
                Dim Mac = Hmac.ComputeHash(StoredRecord, 0, ChunkRecordDataOffset + PayloadLength)
                Buffer.BlockCopy(Mac, 0, StoredRecord, ChunkRecordDataOffset + PayloadLength, MacSize)
            End Using

            Return New PreparedChunkRecord With {
                .RecordId = RecordId,
                .StoredRecord = StoredRecord,
                .PlainLength = PlainLength,
                .StoredCompressionMethod = StoredCompressionMethod
            }

        End Function

        '
        ' Serial: records the compression method in the header flags, allocates a backing
        ' span, writes the prepared record and updates the physical-record indexes.
        '
        Private Async Function PlaceChunkRecordAsync(Prepared As PreparedChunkRecord,
                                                     RunAsync As Boolean,
                                                     CancellationToken As Threading.CancellationToken) As Task(Of PhysicalRecordEntry)

            If Prepared.StoredCompressionMethod <> ChunkedStreamOptions.CompressionMethods.None Then
                MarkCompressionFlag(Prepared.StoredCompressionMethod)
            End If

            Dim NewRecordOffset =
                GetNextWriteOffset(Prepared.StoredRecord.Length, Options.NewChunkWriteLocationPolicy, False)

            Await WriteAtEitherAsync(RunAsync, NewRecordOffset, Prepared.StoredRecord, 0, Prepared.StoredRecord.Length, CancellationToken).ConfigureAwait(False)

            Dim Result =
                New PhysicalRecordEntry With {
                    .RecordId = Prepared.RecordId,
                    .PhysicalOffset = NewRecordOffset,
                    .PhysicalLength = Prepared.StoredRecord.Length,
                    .PlainLength = Prepared.PlainLength,
                    .RefCount = 1
                }

            Dim Ordinal = _PhysicalRecords.Count

            _PhysicalRecords.Add(Result.RecordId, Result)

            AddPhysicalRecordToIndexes(Result, Ordinal)

            Dim NewRecordEndOffset =
                NewRecordOffset + CLng(Prepared.StoredRecord.Length)

            If NewRecordEndOffset > _IndexOffset Then
                _IndexOffset = NewRecordEndOffset
            End If

            MarkPhysicalRecordPageDirtyByOrdinal(Ordinal)

            Return Result

        End Function

        Private Function ReadPhysicalRecordPlain(Record As PhysicalRecordEntry,
                                                 Optional Cipher As ChunkCipher = Nothing) As Byte()

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            If Record.PhysicalOffset + Record.PhysicalLength > BaseStream.Length Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")

            Dim StoredRecord(Record.PhysicalLength - 1) As Byte

            ReadAt(Record.PhysicalOffset, StoredRecord, 0, StoredRecord.Length)

            Dim Plain(Record.PlainLength - 1) As Byte

            DecryptPhysicalRecord(Record.RecordId, StoredRecord, Plain, Cipher)

            Return Plain

        End Function

        '
        ' Async twin of ReadPhysicalRecordPlain. Only the backing-store read differs; the
        ' guards and DecryptPhysicalRecord (MAC check, decrypt, decompress) are pure CPU
        ' and shared with the synchronous path.
        '
        Private Async Function ReadPhysicalRecordPlainAsync(Record As PhysicalRecordEntry,
                                                            CancellationToken As Threading.CancellationToken,
                                                            Optional Cipher As ChunkCipher = Nothing) As Task(Of Byte())

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            If Record.PhysicalOffset + Record.PhysicalLength > BaseStream.Length Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")

            Dim StoredRecord(Record.PhysicalLength - 1) As Byte

            Await ReadAtAsync(Record.PhysicalOffset, StoredRecord, 0, StoredRecord.Length, CancellationToken).ConfigureAwait(False)

            Dim Plain(Record.PlainLength - 1) As Byte

            DecryptPhysicalRecord(Record.RecordId, StoredRecord, Plain, Cipher)

            Return Plain

        End Function

        Private Sub ReadExtentBytes(Extent As ExtentIndexEntry,
                                    OffsetInsideExtent As Integer,
                                    Output As Byte(),
                                    OutputOffset As Integer,
                                    Count As Integer,
                                    Optional Cipher As ChunkCipher = Nothing)

            If Count <= 0 Then Return

            If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                Array.Clear(Output, OutputOffset, Count)
                Return
            End If

            Dim Record = GetPhysicalRecord(Extent.PhysicalRecordId)
            Dim Plain = ReadPhysicalRecordPlain(Record, Cipher)
            Dim SourceOffset = Extent.PhysicalRecordOffset + OffsetInsideExtent

            If SourceOffset < 0 OrElse SourceOffset + Count > Plain.Length Then
                Throw New InvalidDataException($"Extent references beyond physical record {Extent.PhysicalRecordId}.")
            End If

            Buffer.BlockCopy(Plain, SourceOffset, Output, OutputOffset, Count)

        End Sub

        '
        ' Async twin of ReadExtentBytes.
        '
        Private Async Function ReadExtentBytesAsync(Extent As ExtentIndexEntry,
                                                    OffsetInsideExtent As Integer,
                                                    Output As Byte(),
                                                    OutputOffset As Integer,
                                                    Count As Integer,
                                                    CancellationToken As Threading.CancellationToken,
                                                    Optional Cipher As ChunkCipher = Nothing) As Task

            If Count <= 0 Then Return

            If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                Array.Clear(Output, OutputOffset, Count)
                Return
            End If

            Dim Record = GetPhysicalRecord(Extent.PhysicalRecordId)
            Dim Plain = Await ReadPhysicalRecordPlainAsync(Record, CancellationToken, Cipher).ConfigureAwait(False)
            Dim SourceOffset = Extent.PhysicalRecordOffset + OffsetInsideExtent

            If SourceOffset < 0 OrElse SourceOffset + Count > Plain.Length Then
                Throw New InvalidDataException($"Extent references beyond physical record {Extent.PhysicalRecordId}.")
            End If

            Buffer.BlockCopy(Plain, SourceOffset, Output, OutputOffset, Count)

        End Function

        Private Function GetDataEndFromIndex() As Long

            If _PhysicalDataEndDirty Then RecalculatePhysicalDataEnd()

            Return _PhysicalDataEnd

        End Function

    End Class
End Namespace