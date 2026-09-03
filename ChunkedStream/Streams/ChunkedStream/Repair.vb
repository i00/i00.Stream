Imports System.IO
Imports System.Linq

Namespace Streams
    Partial Class ChunkedStream

        '
        ' Applies the repairable subset of a ValidationReport. Entry point for
        ' ValidationReport.Repair. Every problem is re-verified against the live stream
        ' before it is acted on, so the report may be built, inspected (and, for the
        ' embedded file system, used to mark affected entries) and only then repaired.
        '
        Friend Function ExecuteRepair(Report As ValidationReport,
                                      Selector As Func(Of ValidationProblem, Boolean)) As RepairResult

            If Report Is Nothing Then Throw New ArgumentNullException(NameOf(Report))
            If Selector Is Nothing Then Throw New ArgumentNullException(NameOf(Selector))

            Using EnterStateLock()

                ThrowIfDisposed()
                ThrowIfFaulted()

                If HasActiveCheckpoint Then
                    Throw New InvalidOperationException("Repair cannot run while a checkpoint is active.")
                End If

                If _DeferPublishDepth > 0 Then
                    Throw New InvalidOperationException("Repair cannot run while metadata publishing is deferred.")
                End If

                Try
                    Return ExecuteRepairCore(Report, Selector)
                Catch
                    _Faulted = True
                    Throw
                End Try

            End Using

        End Function

        Private Function ExecuteRepairCore(Report As ValidationReport,
                                           Selector As Func(Of ValidationProblem, Boolean)) As RepairResult

            Dim Repaired As New List(Of ValidationProblem)()
            Dim Skipped As New List(Of RepairSkip)()

            '
            ' The extent chain is the foundation every other repair builds on. If it is
            ' structurally broken (a gap, overlap or invalid field) nothing else is safe to
            ' touch - report every problem as unrepaired and leave the stream alone.
            '
            If Report.Problems.Any(Function(problem) problem.Kind = ValidationProblemKind.ExtentChainStructure) Then
                For Each Problem In Report.Problems
                    Skipped.Add(New RepairSkip(Problem, "The extent chain is structurally damaged; automatic repair was not attempted."))
                Next
                Return New RepairResult(Repaired, Skipped, 0)
            End If

            Dim RecordsToZeroFill As New HashSet(Of Long)()
            Dim ReconcileRefCounts = False
            Dim RebuildAnchors = False
            Dim RecomputeLength = False

            For Each Problem In Report.Problems

                If Problem.CanRepair = False Then
                    Skipped.Add(New RepairSkip(Problem, "This kind of problem cannot be repaired automatically."))
                    Continue For
                End If

                If Selector(Problem) = False Then
                    Skipped.Add(New RepairSkip(Problem,
                                               If(Problem.RepairIsLossy,
                                                  "Repairing this discards data and it was not selected for repair.",
                                                  "This problem was not selected for repair.")))
                    Continue For
                End If

                Select Case Problem.Kind

                    Case ValidationProblemKind.MissingPhysicalRecord,
                         ValidationProblemKind.ExtentBeyondPhysicalRecord,
                         ValidationProblemKind.PhysicalRecordUnreadable

                        If Problem.PhysicalRecordId Is Nothing OrElse PhysicalRecordProblemStillPresent(Problem) = False Then
                            Skipped.Add(New RepairSkip(Problem, "The problem was no longer present when the repair ran."))
                            Continue For
                        End If

                        RecordsToZeroFill.Add(Problem.PhysicalRecordId.Value)
                        Repaired.Add(Problem)

                    Case ValidationProblemKind.RefCountMismatch
                        ReconcileRefCounts = True
                        Repaired.Add(Problem)

                    Case ValidationProblemKind.AnchorIndexMismatch
                        RebuildAnchors = True
                        Repaired.Add(Problem)

                    Case ValidationProblemKind.LogicalLengthMismatch
                        RecomputeLength = True
                        Repaired.Add(Problem)

                    Case Else
                        Skipped.Add(New RepairSkip(Problem, "This kind of problem cannot be repaired automatically."))

                End Select

            Next

            If Repaired.Count = 0 Then Return New RepairResult(Repaired, Skipped, 0)

            Dim BytesZeroed As Long = 0

            Using Deferred = DeferPublish()

                For Each RecordId In RecordsToZeroFill
                    BytesZeroed += ZeroFillExtentsReferencing(RecordId)
                Next

                If ReconcileRefCounts Then ReconcilePhysicalRecordRefCounts()
                If RecomputeLength Then _Length = ComputeExtentChainSpan()
                If RebuildAnchors Then RebuildAnchorIndex()

                RebuildPhysicalRecordOrdinals()
                RecalculatePhysicalDataEnd()
                InvalidateChunkCache()
                MarkAllMetadataPagesDirty()

                Deferred.Publish()

            End Using

            Return New RepairResult(Repaired, Skipped, BytesZeroed)

        End Function

        '
        ' Re-reads the one physical record a problem concerns and confirms the problem still
        ' reproduces: a record no extent references any more, or one that now authenticates,
        ' is treated as already resolved.
        '
        Private Function PhysicalRecordProblemStillPresent(Problem As ValidationProblem) As Boolean

            Dim RecordId = Problem.PhysicalRecordId.Value

            If _Extents.Any(Function(extent) extent.PhysicalRecordId = RecordId) = False Then Return False

            Dim Record As PhysicalRecordEntry
            If _PhysicalRecords.TryGetValue(RecordId, Record) = False Then Return True
            If Record.RefCount <= 0 Then Return False

            Dim Probe = New DiagnosticsSnapshot With {
                .Extents = _Extents,
                .PhysicalRecords = _PhysicalRecords,
                .StoredRecords = New Dictionary(Of Long, Byte())(),
                .ChunkMacKey = If(_ChunkMacKey Is Nothing, Nothing, DirectCast(_ChunkMacKey.Clone(), Byte()))
            }

            If Record.PhysicalOffset >= DataStartOffset AndAlso
               Record.PhysicalLength >= MinChunkRecordSize AndAlso
               Record.PhysicalOffset <= BaseStream.Length - Record.PhysicalLength Then

                Dim Buffer(Record.PhysicalLength - 1) As Byte
                ReadAt(Record.PhysicalOffset, Buffer, 0, Buffer.Length)
                Probe.StoredRecords.Add(RecordId, Buffer)

            End If

            Return InspectPhysicalRecordSnapshot(Probe, Record) IsNot Nothing

        End Function

        '
        ' Converts every extent that points at RecordId into a sparse (zero) extent of the
        ' same logical length, so the logical layout, stream length and anchors are all
        ' preserved and the range simply reads back as zeros. Returns the logical bytes
        ' zeroed. Tolerates a record id that is not in the record table.
        '
        Private Function ZeroFillExtentsReferencing(RecordId As Long) As Long

            Dim Zeroed As Long = 0

            For Index = 0 To _Extents.Count - 1

                Dim Extent = _Extents(Index)
                If Extent.PhysicalRecordId <> RecordId Then Continue For

                Zeroed += Extent.LogicalLength
                Extent.PhysicalRecordId = SparsePhysicalRecordId
                Extent.PhysicalRecordOffset = 0
                _Extents(Index) = Extent

            Next

            Dim Record As PhysicalRecordEntry
            If _PhysicalRecords.TryGetValue(RecordId, Record) Then
                Record.RefCount = 0
                _PhysicalRecords(RecordId) = Record
                If HasOpenCheckpoint Then
                    _PendingReclaimedPhysicalRecords.Add(RecordId)
                Else
                    ReclaimPhysicalRecord(RecordId)
                End If
            End If

            Return Zeroed

        End Function

        '
        ' Rewrites every physical-record reference count to agree with the extent table.
        '
        Private Sub ReconcilePhysicalRecordRefCounts()

            Dim ActualCounts As New Dictionary(Of Long, Integer)()

            For Each Extent In _Extents
                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then Continue For
                Dim Count As Integer = 0
                ActualCounts.TryGetValue(Extent.PhysicalRecordId, Count)
                ActualCounts(Extent.PhysicalRecordId) = Count + 1
            Next

            For Each RecordId In _PhysicalRecords.Keys.ToList()
                Dim Record = _PhysicalRecords(RecordId)
                Dim Actual As Integer = 0
                ActualCounts.TryGetValue(RecordId, Actual)
                If Record.RefCount <> Actual Then
                    Record.RefCount = Actual
                    _PhysicalRecords(RecordId) = Record
                End If
            Next

        End Sub

        Private Function ComputeExtentChainSpan() As Long

            If _Extents.Count = 0 Then Return 0

            Dim LastExtent = _Extents(_Extents.Count - 1)
            Return LastExtent.LogicalOffset + CLng(LastExtent.LogicalLength)

        End Function

    End Class
End Namespace
