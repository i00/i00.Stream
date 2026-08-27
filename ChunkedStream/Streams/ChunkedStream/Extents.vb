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

            Dim WasUnreferenced = Record.RefCount = 0

            Record.RefCount += 1
            _PhysicalRecords(RecordId) = Record

            If WasUnreferenced Then
                AddLivePhysicalRecordOffset(Record)
            End If

            MarkPhysicalRecordDirty(RecordId)

        End Sub

        Private Sub MarkExtentPageRangeDirty(FirstExtentIndex As Integer,
                                             LastExtentIndex As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If _Extents.Count = 0 Then Return

            Dim FirstIndex = Math.Max(0, FirstExtentIndex)
            Dim LastIndex = Math.Min(_Extents.Count - 1, LastExtentIndex)

            If LastIndex < FirstIndex Then Return

            Dim FirstPage = FirstIndex \ _IndexPageEntryCount
            Dim LastPage = LastIndex \ _IndexPageEntryCount

            For PageNumber = FirstPage To LastPage
                _DirtyExtentPages.Add(PageNumber)
            Next

        End Sub

        Private Sub MarkExtentPagesDirtyFromIndex(ExtentIndex As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If _Extents.Count = 0 Then Return

            MarkExtentPageRangeDirty(ExtentIndex, _Extents.Count - 1)

        End Sub

        Private Sub MarkExtentPagesDirtyForReplacement(StartIndex As Integer,
                                                       RemovedExtentCount As Integer,
                                                       InsertedExtentCount As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If _Extents.Count = 0 Then Return

            Dim SafeStartIndex = Math.Max(0, Math.Min(StartIndex, _Extents.Count - 1))

            If RemovedExtentCount = InsertedExtentCount Then

                Dim DirtyCount = Math.Max(1, InsertedExtentCount)
                MarkExtentPageRangeDirty(SafeStartIndex, SafeStartIndex + DirtyCount - 1)
                Return

            End If

            '
            ' If the extent count changed, every later ordinal can shift to a different
            ' page. With the current dense ordinal metadata format, pages from the change
            ' point onward must be rewritten.
            '
            MarkExtentPagesDirtyFromIndex(SafeStartIndex)

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

        Private Sub MarkPhysicalRecordPagesDirtyFromOrdinal(Ordinal As Integer)

            If _IndexPageEntryCount <= 0 Then Return
            If Ordinal < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Ordinal))
            If _PhysicalRecords.Count = 0 Then Return

            Dim FirstPage = Math.Max(0, Ordinal \ _IndexPageEntryCount)
            Dim PageCount = GetIndexPageCount(_PhysicalRecords.Count, _IndexPageEntryCount)

            If PageCount <= 0 Then Return

            For PageNumber = FirstPage To PageCount - 1
                _DirtyPhysicalRecordPages.Add(PageNumber)
            Next

        End Sub


        Private Sub DecrementPhysicalRecordRefCount(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount <= 0 Then
                Throw New InvalidDataException(
                    $"Physical record {RecordId} has an invalid refcount.")
            End If

            If Record.RefCount = 1 Then
                RemoveLivePhysicalRecordOffset(Record)
            End If

            Record.RefCount -= 1
            _PhysicalRecords(RecordId) = Record

            MarkPhysicalRecordDirty(RecordId)

            If Record.RefCount <> 0 Then Return

            If HasOpenCheckpoint Then
                _PendingReclaimedPhysicalRecords.Add(RecordId)
                Return
            End If

            ReclaimPhysicalRecord(RecordId)

        End Sub

        Private Sub ReclaimPhysicalRecord(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

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

            RemoveLivePhysicalRecordOffset(Record)

            '
            ' Hold the freed span until the next durable publish. Reusing it before the
            ' superseding generation is durable would let a crash fall back to a header
            ' that still references this record's storage. See _DeferredFreeSpaces.
            '
            DeferFreeSpace(Record.PhysicalOffset,
                              Record.PhysicalLength)

            _PhysicalRecords.Remove(RecordId)

            '
            ' Removing a record shifts every later ordinal and also
            ' rebuilds the cached physical-data end value.
            '
            RebuildPhysicalRecordOrdinals()

            MarkPhysicalRecordPagesDirtyFromOrdinal(RemovedOrdinal)

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
            End If

            _PhysicalDataEnd =
                Math.Max(_PhysicalDataEnd,
                         Record.PhysicalOffset + CLng(Record.PhysicalLength))

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

            Dim DataEnd = CLng(DataStartOffset)

            For Each Record In _PhysicalRecords.Values
                DataEnd =
                    Math.Max(DataEnd,
                             Record.PhysicalOffset + CLng(Record.PhysicalLength))
            Next

            _PhysicalDataEnd = DataEnd

        End Sub

        Private Sub RebuildPhysicalRecordOrdinals()

            _PhysicalRecordOrdinals.Clear()
            _PhysicalRecordIdsByPage.Clear()
            _LivePhysicalRecordIdsByOffset.Clear()

            _PhysicalDataEnd = DataStartOffset

            Dim Ordinal = 0

            For Each Record In _PhysicalRecords.Values.OrderBy(Function(x) x.RecordId)

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

                End If

                _PhysicalDataEnd =
                    Math.Max(_PhysicalDataEnd,
                             Record.PhysicalOffset + CLng(Record.PhysicalLength))

                Ordinal += 1

            Next

        End Sub

        Private Sub ReclaimPendingPhysicalRecords()

            If HasOpenCheckpoint Then Return

            Dim Pending = _PendingReclaimedPhysicalRecords.ToArray()

            _PendingReclaimedPhysicalRecords.Clear()

            For Each recordId In Pending

                If _PhysicalRecords.ContainsKey(recordId) = False Then Continue For

                Dim Record = _PhysicalRecords(recordId)

                If Record.RefCount = 0 Then
                    ReclaimPhysicalRecord(recordId)
                End If

            Next

        End Sub

        Private Sub DiscardPendingPhysicalRecordReclaims()

            _PendingReclaimedPhysicalRecords.Clear()

        End Sub

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

            _Extents(ExtentIndex) = LeftExtent
            _Extents.Insert(ExtentIndex + 1, RightExtent)

            If Extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                IncrementPhysicalRecordRefCount(Extent.PhysicalRecordId)
            End If

            RebuildAnchorIndex()
            MarkExtentPagesDirtyFromIndex(ExtentIndex)

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

            Dim Result As New List(Of ExtentIndexEntry)()
            Dim Remaining = Count
            Dim CurrentInputOffset = InputOffset

            While Remaining > 0

                Dim SegmentLength = Math.Min(Options.ChunkSize, Remaining)
                Dim Segment(SegmentLength - 1) As Byte

                Buffer.BlockCopy(Input, CurrentInputOffset, Segment, 0, SegmentLength)

                If Options.StoreSparseChunks = False AndAlso IsAllZero(Segment, SegmentLength) Then

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = SegmentLength,
                        .PhysicalRecordId = SparsePhysicalRecordId,
                        .PhysicalRecordOffset = 0
                    })

                Else

                    Dim Record = WritePhysicalRecord(Segment, SegmentLength)

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

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            _Length += InsertLength

            RebuildAnchorIndex()

            MarkExtentPagesDirtyForReplacement(InsertIndex,
                                               0,
                                               Materialised.Count)

        End Sub

        Private Sub RemoveRangeCore(LogicalOffset As Long,
                                    Length As Long,
                                    AllowRemoveBoundaryMaterialise As Boolean)

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

                    ReadExtentBytes(LeftExtent,
                                    0,
                                    Combined,
                                    0,
                                    LeftExtent.LogicalLength)

                    ReadExtentBytes(RightExtent,
                                    0,
                                    Combined,
                                    LeftExtent.LogicalLength,
                                    RightExtent.LogicalLength)

                    Dim Record = WritePhysicalRecord(Combined,
                                                     CombinedLength)

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

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            _Length -= ActualLength

            RebuildAnchorIndex()

            MarkExtentPagesDirtyForReplacement(DirtyStartIndex,
                                               RemovedExtentCount,
                                               InsertedExtentCount)

        End Sub

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

            _Extents.Clear()
            _Extents.AddRange(NewLayout)

            RebuildAnchorIndex()

            MarkExtentPagesDirtyForReplacement(StartIndex,
                                               RemovedExtentCount,
                                               Materialised.Count)

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

                ReadExtentBytes(Segments(0),
                                0,
                                Combined,
                                0,
                                Segments(0).LogicalLength)

                ReadExtentBytes(Segments(1),
                                0,
                                Combined,
                                Segments(0).LogicalLength,
                                Segments(1).LogicalLength)

                Dim Record =
                    WritePhysicalRecord(Combined,
                                        CombinedLength)

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

                    ReadExtentBytes(Segment,
                                    0,
                                    Buffer,
                                    0,
                                    Segment.LogicalLength)

                    Dim Record =
                        WritePhysicalRecord(Buffer,
                                            Segment.LogicalLength)

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