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
                Throw New InvalidOperationException($"Physical record {RecordId} refcount overflow.")
            End If

            Record.RefCount += 1
            _PhysicalRecords(RecordId) = Record

            MarkAllMetadataPagesDirty()

        End Sub

        Private Sub DecrementPhysicalRecordRefCount(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount <= 0 Then
                Throw New InvalidDataException($"Physical record {RecordId} has an invalid refcount.")
            End If

            Record.RefCount -= 1
            _PhysicalRecords(RecordId) = Record

            MarkAllMetadataPagesDirty()

            If Record.RefCount <> 0 Then Return

            If HasOpenCheckpoint Then
                _PendingReclaimedPhysicalRecords.Add(RecordId)
                Return
            End If

            ReclaimPhysicalRecord(RecordId)

        End Sub

        Private Sub ReclaimPhysicalRecord(RecordId As Long)

            If RecordId = SparsePhysicalRecordId Then Return

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.RefCount <> 0 Then
                Throw New InvalidOperationException($"Cannot reclaim physical record {RecordId} because it is still referenced.")
            End If

            AddFreeChunkSpace(Record.PhysicalOffset, Record.PhysicalLength)
            _PhysicalRecords.Remove(RecordId)

            MarkAllMetadataPagesDirty()

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

            If LogicalOffset = Extent.LogicalOffset OrElse LogicalOffset = GetExtentEnd(Extent) Then Return

            Dim LeftLength = CInt(LogicalOffset - Extent.LogicalOffset)
            Dim RightLength = Extent.LogicalLength - LeftLength

            Dim LeftExtent =
                New ExtentIndexEntry With {
                    .LogicalOffset = Extent.LogicalOffset,
                    .LogicalLength = LeftLength,
                    .PhysicalRecordId = Extent.PhysicalRecordId,
                    .PhysicalRecordOffset = Extent.PhysicalRecordOffset
                }

            Dim RightExtent =
                New ExtentIndexEntry With {
                    .LogicalOffset = LogicalOffset,
                    .LogicalLength = RightLength,
                    .PhysicalRecordId = Extent.PhysicalRecordId,
                    .PhysicalRecordOffset = Extent.PhysicalRecordOffset + LeftLength
                }

            _Extents(ExtentIndex) = LeftExtent
            _Extents.Insert(ExtentIndex + 1, RightExtent)

            '
            ' Splitting one extent into two creates one additional extent reference
            ' to the same physical record.
            '
            ' RefCount is the number of extents referencing the physical record, so
            ' a successful split must increment the physical record refcount.
            '
            IncrementPhysicalRecordRefCount(Extent.PhysicalRecordId)

            MarkAllMetadataPagesDirty()

        End Sub

        Private Function ShouldMaterialiseRemoveBoundaryFragments(LeftLength As Integer,
                                                                  RightLength As Integer) As Boolean

            If Options.BisectLimit <= 0 Then Return False
            If LeftLength <= 0 OrElse RightLength <= 0 Then Return False

            Dim CombinedLength = LeftLength + RightLength

            If CombinedLength > Options.ChunkSize Then Return False

            Return LeftLength < Options.BisectLimit OrElse RightLength < Options.BisectLimit

        End Function

        Private Function ShouldMaterialiseCloneAdjacentBoundaryFragments(LeftLength As Integer,
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

        Private Sub InsertSparseRange(LogicalOffset As Long,
                                      Length As Long)

            If Length <= 0 Then Return
            If Length > Integer.MaxValue Then Throw New InvalidOperationException("Sparse insert length is too large for a single operation.")

            Dim Remaining = Length
            Dim InsertOffset = LogicalOffset
            Dim Extents As New List(Of ExtentIndexEntry)()

            While Remaining > 0

                Dim SegmentLength = CInt(Math.Min(CLng(Options.ChunkSize), Remaining))

                Extents.Add(New ExtentIndexEntry With {
                    .LogicalLength = SegmentLength,
                    .PhysicalRecordId = SparsePhysicalRecordId,
                    .PhysicalRecordOffset = 0
                })

                InsertOffset += SegmentLength
                Remaining -= SegmentLength

            End While

            InsertExtentsCore(LogicalOffset, Extents)

        End Sub

        Private Sub InsertExtentsCore(LogicalOffset As Long,
                                      NewExtents As IList(Of ExtentIndexEntry))

            If LogicalOffset < 0 OrElse LogicalOffset > _Length Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If NewExtents Is Nothing Then Throw New ArgumentNullException(NameOf(NewExtents))
            If NewExtents.Count = 0 Then Return

            SplitExtentAt(LogicalOffset)

            Dim InsertIndex = FindExtentInsertIndex(LogicalOffset)
            Dim InsertLength As Long = NewExtents.Sum(Function(extent) CLng(extent.LogicalLength))

            For Index = InsertIndex To _Extents.Count - 1
                Dim Extent = _Extents(Index)
                Extent.LogicalOffset += InsertLength
                _Extents(Index) = Extent
            Next

            Dim CurrentLogicalOffset = LogicalOffset
            Dim Materialised As New List(Of ExtentIndexEntry)(NewExtents.Count)

            For Each extent In NewExtents

                If extent.LogicalLength <= 0 Then Continue For

                Dim NewExtent = extent

                NewExtent.LogicalOffset = CurrentLogicalOffset

                Materialised.Add(NewExtent)

                CurrentLogicalOffset += NewExtent.LogicalLength

            Next

            _Extents.InsertRange(InsertIndex, Materialised)
            _Length += InsertLength

            MarkAllMetadataPagesDirty()

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

                If ShouldMaterialiseRemoveBoundaryFragments(LeftExtent.LogicalLength, RightExtent.LogicalLength) Then

                    Dim CombinedLength = LeftExtent.LogicalLength + RightExtent.LogicalLength
                    Dim Combined(CombinedLength - 1) As Byte

                    ReadExtentBytes(LeftExtent, 0, Combined, 0, LeftExtent.LogicalLength)
                    ReadExtentBytes(RightExtent, 0, Combined, LeftExtent.LogicalLength, RightExtent.LogicalLength)

                    Dim Record = WritePhysicalRecord(Combined, CombinedLength)

                    BoundaryReplacement =
                        New ExtentIndexEntry With {
                            .LogicalOffset = LeftExtent.LogicalOffset,
                            .LogicalLength = CombinedLength,
                            .PhysicalRecordId = Record.RecordId,
                            .PhysicalRecordOffset = 0
                        }

                    MaterialiseBoundaries = True

                End If

            End If

            If MaterialiseBoundaries Then

                Dim RightExtent = _Extents(RightBoundaryIndex)
                Dim LeftExtent = _Extents(LeftBoundaryIndex)

                DecrementPhysicalRecordRefCount(LeftExtent.PhysicalRecordId)
                DecrementPhysicalRecordRefCount(RightExtent.PhysicalRecordId)

                Dim RemoveStart = LeftBoundaryIndex
                Dim RemoveCount = RightBoundaryIndex - LeftBoundaryIndex + 1

                For Index = StartIndex To EndIndex - 1
                    Dim RemovedExtent = _Extents(Index)
                    DecrementPhysicalRecordRefCount(RemovedExtent.PhysicalRecordId)
                Next

                _Extents.RemoveRange(RemoveStart, RemoveCount)
                _Extents.Insert(RemoveStart, BoundaryReplacement.Value)

                Dim ShiftStartIndex = RemoveStart + 1
                Dim ShiftAmount = ActualLength

                For Index = ShiftStartIndex To _Extents.Count - 1
                    Dim Extent = _Extents(Index)
                    Extent.LogicalOffset -= ShiftAmount
                    _Extents(Index) = Extent
                Next

                _Length -= ActualLength

                MarkAllMetadataPagesDirty()

                Return

            End If

            For Index = StartIndex To EndIndex - 1
                Dim RemovedExtent = _Extents(Index)
                DecrementPhysicalRecordRefCount(RemovedExtent.PhysicalRecordId)
            Next

            If EndIndex > StartIndex Then
                _Extents.RemoveRange(StartIndex, EndIndex - StartIndex)
            End If

            For Index = StartIndex To _Extents.Count - 1
                Dim Extent = _Extents(Index)
                Extent.LogicalOffset -= ActualLength
                _Extents(Index) = Extent
            Next

            _Length -= ActualLength

            MarkAllMetadataPagesDirty()

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
                    Throw New InvalidDataException($"No extent found for clone source offset {CurrentOffset}.")
                End If

                Dim Extent = _Extents(ExtentIndex)
                Dim OffsetInsideExtent = CInt(CurrentOffset - Extent.LogicalOffset)
                Dim SegmentLength = CInt(Math.Min(CLng(Extent.LogicalLength - OffsetInsideExtent), SourceEndOffset - CurrentOffset))

                Segments.Add(New ExtentIndexEntry With {
                    .LogicalLength = SegmentLength,
                    .PhysicalRecordId = Extent.PhysicalRecordId,
                    .PhysicalRecordOffset = Extent.PhysicalRecordOffset + OffsetInsideExtent
                })

                CurrentOffset += SegmentLength

            End While

            If Segments.Count = 2 AndAlso
               ShouldMaterialiseCloneAdjacentBoundaryFragments(Segments(0).LogicalLength, Segments(1).LogicalLength) Then

                Dim CombinedLength = Segments(0).LogicalLength + Segments(1).LogicalLength
                Dim Combined(CombinedLength - 1) As Byte

                ReadExtentBytes(Segments(0), 0, Combined, 0, Segments(0).LogicalLength)
                ReadExtentBytes(Segments(1), 0, Combined, Segments(0).LogicalLength, Segments(1).LogicalLength)

                Dim Record = WritePhysicalRecord(Combined, CombinedLength)

                Result.Add(New ExtentIndexEntry With {
                    .LogicalLength = CombinedLength,
                    .PhysicalRecordId = Record.RecordId,
                    .PhysicalRecordOffset = 0
                })

                Return Result

            End If

            For Each segment In Segments

                If segment.PhysicalRecordId = SparsePhysicalRecordId Then
                    Result.Add(segment)
                    Continue For
                End If

                If Options.BisectLimit > 0 AndAlso segment.LogicalLength < Options.BisectLimit Then

                    Dim Buffer(segment.LogicalLength - 1) As Byte

                    ReadExtentBytes(segment, 0, Buffer, 0, segment.LogicalLength)

                    Dim Record = WritePhysicalRecord(Buffer, segment.LogicalLength)

                    Result.Add(New ExtentIndexEntry With {
                        .LogicalLength = segment.LogicalLength,
                        .PhysicalRecordId = Record.RecordId,
                        .PhysicalRecordOffset = 0
                    })

                Else

                    IncrementPhysicalRecordRefCount(segment.PhysicalRecordId)
                    Result.Add(segment)

                End If

            Next

            Return Result

        End Function

        Private Sub ValidateExtentsAreSortedAndNonOverlapping()

            Dim ExpectedOffset As Long = 0

            For Each extent In _Extents

                If extent.LogicalOffset < 0 Then Throw New InvalidDataException("Extent has a negative logical offset.")
                If extent.LogicalLength <= 0 Then Throw New InvalidDataException("Extent has an invalid logical length.")
                If extent.LogicalOffset <> ExpectedOffset Then Throw New InvalidDataException($"Extent layout contains a gap or overlap at logical offset {ExpectedOffset}.")
                If extent.PhysicalRecordOffset < 0 Then Throw New InvalidDataException("Extent has a negative physical record offset.")

                If extent.PhysicalRecordId <> SparsePhysicalRecordId Then

                    Dim Record = GetPhysicalRecord(extent.PhysicalRecordId)

                    If extent.PhysicalRecordOffset + extent.LogicalLength > Record.PlainLength Then
                        Throw New InvalidDataException($"Extent references beyond physical record {extent.PhysicalRecordId}.")
                    End If

                End If

                ExpectedOffset = extent.LogicalOffset + CLng(extent.LogicalLength)

            Next

            If ExpectedOffset <> _Length Then
                Throw New InvalidDataException($"Extent logical length mismatch. Expected {_Length}, found {ExpectedOffset}.")
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