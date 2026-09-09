Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        ''' <summary>Severity of a <see cref="ValidationProblem" />.</summary>
        Public Enum ValidationSeverity
            ''' <summary>A recoverable inconsistency in derived state. The stream is usable.</summary>
            Warning
            ''' <summary>A structural or data-integrity failure.</summary>
            [Error]
        End Enum

        ''' <summary>Classifies a <see cref="ValidationProblem" /> and determines how it is repaired.</summary>
        Public Enum ValidationProblemKind
            ''' <summary>A gap, overlap or invalid field in the extent chain. Not automatically repairable.</summary>
            ExtentChainStructure
            ''' <summary>An extent references a physical record that is not in the record table. Repaired by zero-filling the range.</summary>
            MissingPhysicalRecord
            ''' <summary>An extent references data beyond the end of its physical record. Repaired by zero-filling the range.</summary>
            ExtentBeyondPhysicalRecord
            ''' <summary>A physical record's header is malformed or its authentication tag is invalid. Repaired by zero-filling every range that references it.</summary>
            PhysicalRecordUnreadable
            ''' <summary>An encrypted physical record exists but no file master key is available to authenticate it. Not automatically repairable.</summary>
            PhysicalRecordKeyUnavailable
            ''' <summary>A physical-record reference count disagrees with the extent table. Repaired by recomputing it.</summary>
            RefCountMismatch
            ''' <summary>The anchor index does not match the anchored extents. Repaired by rebuilding it.</summary>
            AnchorIndexMismatch
            ''' <summary>The logical length disagrees with the extent chain. Repaired by recomputing it.</summary>
            LogicalLengthMismatch
            ''' <summary>An incrementally maintained cache (the physical-data end, the anchor-id allocator, the live physical-record offset index) disagrees with a fresh rebuild. Repaired by rebuilding the affected index.</summary>
            CacheInconsistency
        End Enum

        ''' <summary>A contiguous span of the logical stream.</summary>
        Public Structure LogicalRange
            Friend Sub New(Offset As Long, Length As Long)
                Me.Offset = Offset
                Me.Length = Length
            End Sub
            ''' <summary>Logical offset of the first affected byte.</summary>
            Public ReadOnly Property Offset As Long
            ''' <summary>Number of affected bytes.</summary>
            Public ReadOnly Property Length As Long
            Public Overrides Function ToString() As String
                Return $"[{Offset}, {Offset + Length})"
            End Function
        End Structure

        ''' <summary>One problem found by <see cref="ChunkedStream.Validate" />.</summary>
        Public NotInheritable Class ValidationProblem

            Friend Sub New(Severity As ValidationSeverity,
                           Kind As ValidationProblemKind,
                           Message As String,
                           PhysicalRecordId As Long?,
                           AffectedRanges As IReadOnlyList(Of LogicalRange),
                           CanRepair As Boolean,
                           RepairIsLossy As Boolean)

                Me.Severity = Severity
                Me.Kind = Kind
                Me.Message = Message
                Me.PhysicalRecordId = PhysicalRecordId
                Me.AffectedRanges = If(AffectedRanges, DirectCast(Array.Empty(Of LogicalRange)(), IReadOnlyList(Of LogicalRange)))
                Me.CanRepair = CanRepair
                Me.RepairIsLossy = RepairIsLossy
                Me.DataLossBytes = If(RepairIsLossy, Me.AffectedRanges.Sum(Function(range) range.Length), 0L)
            End Sub

            ''' <summary>How serious the problem is.</summary>
            Public ReadOnly Property Severity As ValidationSeverity
            ''' <summary>What kind of problem this is.</summary>
            Public ReadOnly Property Kind As ValidationProblemKind
            ''' <summary>A human-readable description.</summary>
            Public ReadOnly Property Message As String
            ''' <summary>The physical record the problem concerns, or Nothing when it is not record-specific.</summary>
            Public ReadOnly Property PhysicalRecordId As Long?
            ''' <summary>The logical byte ranges that would be lost if this problem is repaired.</summary>
            Public ReadOnly Property AffectedRanges As IReadOnlyList(Of LogicalRange)
            ''' <summary>Whether <see cref="ValidationReport.Repair" /> can address this problem.</summary>
            Public ReadOnly Property CanRepair As Boolean
            ''' <summary>Whether repairing this problem discards data (zero-fills a logical range).</summary>
            Public ReadOnly Property RepairIsLossy As Boolean
            ''' <summary>Bytes that would be replaced with zeros if this problem is repaired.</summary>
            Public ReadOnly Property DataLossBytes As Long

            Public Overrides Function ToString() As String
                Return $"{Severity} {Kind}: {Message}"
            End Function
        End Class

        ''' <summary>Controls how much <see cref="ValidationReport.Repair" /> is allowed to do.</summary>
        Public Enum RepairScope
            ''' <summary>Only apply repairs that do not discard any data. Lossy problems are left unrepaired.</summary>
            NonLossy
            ''' <summary>Also zero-fill logical ranges backed by unreadable data.</summary>
            IncludeDataLoss
        End Enum

        ''' <summary>Records what one <see cref="ValidationProblem" /> could not be repaired, and why.</summary>
        Public NotInheritable Class RepairSkip
            Friend Sub New(Problem As ValidationProblem, Reason As String)
                Me.Problem = Problem
                Me.Reason = Reason
            End Sub
            ''' <summary>The problem that was not repaired.</summary>
            Public ReadOnly Property Problem As ValidationProblem
            ''' <summary>Why it was not repaired.</summary>
            Public ReadOnly Property Reason As String
        End Class

        ''' <summary>The outcome of <see cref="ValidationReport.Repair" />.</summary>
        Public NotInheritable Class RepairResult
            Friend Sub New(Repaired As IReadOnlyList(Of ValidationProblem),
                           Skipped As IReadOnlyList(Of RepairSkip),
                           BytesZeroed As Long)
                Me.Repaired = Repaired
                Me.Skipped = Skipped
                Me.BytesZeroed = BytesZeroed
            End Sub
            ''' <summary>Problems that were repaired.</summary>
            Public ReadOnly Property Repaired As IReadOnlyList(Of ValidationProblem)
            ''' <summary>Problems that were not repaired, each with a reason.</summary>
            Public ReadOnly Property Skipped As IReadOnlyList(Of RepairSkip)
            ''' <summary>Total number of logical bytes replaced with zeros.</summary>
            Public ReadOnly Property BytesZeroed As Long
        End Class

        ''' <summary>Thrown by <see cref="ValidationReport.ThrowIfErrors" />.</summary>
        Public NotInheritable Class ValidationException
            Inherits Exception

            Friend Sub New(Report As ValidationReport)
                MyBase.New($"The chunked stream failed validation with {Report.Errors.Count} error(s): " &
                           String.Join("; ", Report.Errors.Select(Function(problem) problem.Message)))
                Me.Report = Report
            End Sub

            ''' <summary>The full validation report.</summary>
            Public ReadOnly Property Report As ValidationReport
        End Class

        ''' <summary>The result of validating a <see cref="ChunkedStream" />.</summary>
        Public NotInheritable Class ValidationReport

            Private ReadOnly _Owner As ChunkedStream

            Friend Sub New(Owner As ChunkedStream, Problems As IReadOnlyList(Of ValidationProblem))
                _Owner = Owner
                Me.Problems = Problems
                Errors = Problems.Where(Function(problem) problem.Severity = ValidationSeverity.[Error]).ToList()
                Warnings = Problems.Where(Function(problem) problem.Severity = ValidationSeverity.Warning).ToList()
            End Sub

            ''' <summary>Every problem found, most structural first.</summary>
            Public ReadOnly Property Problems As IReadOnlyList(Of ValidationProblem)
            ''' <summary>The <see cref="ValidationSeverity.[Error]" />-severity problems.</summary>
            Public ReadOnly Property Errors As IReadOnlyList(Of ValidationProblem)
            ''' <summary>The <see cref="ValidationSeverity.Warning" />-severity problems.</summary>
            Public ReadOnly Property Warnings As IReadOnlyList(Of ValidationProblem)

            ''' <summary>True when at least one <see cref="ValidationSeverity.[Error]" />-severity problem was found.</summary>
            Public ReadOnly Property HasErrors As Boolean
                Get
                    Return Errors.Count > 0
                End Get
            End Property

            ''' <summary>Throws <see cref="ValidationException" /> when <see cref="HasErrors" /> is true.</summary>
            Public Sub ThrowIfErrors()
                If HasErrors Then Throw New ValidationException(Me)
            End Sub

            ''' <summary>
            ''' Repairs the problems in this report that fall within <paramref name="RepairScope" />.
            ''' Each problem is re-verified against the current stream before it is acted on, so a
            ''' problem that has since been fixed (or the stream mutated away from) is skipped. The
            ''' repair is applied as one durable metadata publish.
            ''' </summary>
            ''' <param name="RepairScope">
            ''' <see cref="RepairScope.NonLossy" /> (the default) rebuilds indexes and recomputes
            ''' counts only; <see cref="RepairScope.IncludeDataLoss" /> also replaces logical ranges
            ''' backed by unreadable data with zeros.
            ''' </param>
            Public Function Repair(Optional RepairScope As RepairScope = RepairScope.NonLossy) As RepairResult
                Dim IncludeLossy = (RepairScope = ChunkedStream.RepairScope.IncludeDataLoss)
                Return _Owner.ExecuteRepair(Me, Function(problem) Not problem.RepairIsLossy OrElse IncludeLossy)
            End Function

            ''' <summary>
            ''' Repairs only the problems for which <paramref name="Selector" /> returns True. The
            ''' selector replaces the <see cref="RepairScope" /> gate, so
            ''' <c>Repair(Function(p) p.DataLossBytes = 0)</c> is the non-lossy set,
            ''' <c>Repair(Function(p) p.Kind = ValidationProblemKind.RefCountMismatch)</c> is one
            ''' kind, and so on. Re-verification and the single-publish behaviour are unchanged.
            ''' </summary>
            Public Function Repair(Selector As Func(Of ValidationProblem, Boolean)) As RepairResult
                If Selector Is Nothing Then Throw New ArgumentNullException(NameOf(Selector))
                Return _Owner.ExecuteRepair(Me, Selector)
            End Function

        End Class

        Private NotInheritable Class DiagnosticsSnapshot
            Public LogicalLength As Long
            Public DataEnd As Long
            Public AnchorIndexCount As Integer
            Public NextAnchorId As Long
            Public LivePhysicalRecordOffsets As Dictionary(Of Long, Long)
            Public Extents As List(Of ExtentIndexEntry)
            Public PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
            Public ChunkMacKey As Byte()
        End Class

        ''' <summary>
        ''' Calculates physical-record fragmentation as a ratio of the live data area.
        ''' </summary>
        ''' <param name="CancellationToken">Token used to cancel the calculation.</param>
        ''' <returns>
        ''' The proportion of the data area between <see cref="DataStartOffset" /> and the
        ''' live data end that is not occupied by live physical records, in the range 0 to 1.
        ''' </returns>
        Public Function GetFragmentation(Optional CancellationToken As Threading.CancellationToken = Nothing) As Double

            Dim Snapshot As DiagnosticsSnapshot

            Using EnterStateLock()
                Snapshot = CaptureDiagnosticsSnapshotCore()
            End Using

            CancellationToken.ThrowIfCancellationRequested()

            Return GetFragmentationCore(Snapshot)

        End Function

        ''' <summary>
        ''' Asynchronously calculates physical-record fragmentation as a ratio of the live
        ''' data area.
        ''' </summary>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        ''' <returns>A task producing the fragmentation ratio, in the range 0 to 1.</returns>
        Public Async Function GetFragmentationAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Double)
            Return Await Task.Run(
                Function()
                    Return GetFragmentation(CancellationToken)
                End Function, CancellationToken).ConfigureAwait(False)
        End Function

        Private Function GetFragmentationCore(Snapshot As DiagnosticsSnapshot) As Double

            If Snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(Snapshot))

            Dim UsedBytes As Long = 0

            For Each Record In Snapshot.PhysicalRecords.Values
                If Record.RefCount > 0 Then UsedBytes += Record.PhysicalLength
            Next

            Dim TotalStoredBytes = Math.Max(0L, Snapshot.DataEnd - DataStartOffset)
            Dim WastedBytes = Math.Max(0L, TotalStoredBytes - UsedBytes)

            If TotalStoredBytes = 0 Then Return 0
            Return WastedBytes / CDbl(TotalStoredBytes)

        End Function

        ''' <summary>
        ''' Validates the extent layout, physical-record reference counts, anchor index and
        ''' every live physical record, returning a report of every problem found rather
        ''' than throwing at the first one. Call <see cref="ValidationReport.ThrowIfErrors" />
        ''' for the old throw-on-corruption behaviour, or <see cref="ValidationReport.Repair" />
        ''' to fix what can be fixed.
        ''' </summary>
        ''' <param name="ProgressCallback">Optional callback invoked as physical records are validated.</param>
        ''' <param name="CancellationToken">Token used to cancel validation.</param>
        Public Function Validate(Optional ProgressCallback As StreamProgressCallback = Nothing, Optional CancellationToken As Threading.CancellationToken = Nothing) As ValidationReport

            Dim Snapshot As DiagnosticsSnapshot

            Using EnterStateLock()
                Snapshot = CaptureDiagnosticsSnapshotCore()
            End Using

            CancellationToken.ThrowIfCancellationRequested()

            Return New ValidationReport(Me, CollectValidationProblems(Snapshot, ProgressCallback, CancellationToken))

        End Function

        ''' <summary>
        ''' Asynchronously validates the stream structure and every live physical record.
        ''' </summary>
        ''' <param name="ProgressCallback">Optional callback invoked as physical records are validated.</param>
        ''' <param name="CancellationToken">Token used to cancel validation.</param>
        Public Async Function ValidateAsync(Optional ProgressCallback As StreamProgressCallback = Nothing, Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of ValidationReport)
            Return Await Task.Run(
                Function()
                    Return Validate(ProgressCallback, CancellationToken)
                End Function, CancellationToken).ConfigureAwait(False)
        End Function

        Private Function CaptureDiagnosticsSnapshotCore() As DiagnosticsSnapshot

            ThrowIfDisposed()

            Return New DiagnosticsSnapshot With {
                .LogicalLength = _Length,
                .DataEnd = GetDataEndFromIndex(),
                .AnchorIndexCount = _ExtentIndexesByAnchorId.Count,
                .NextAnchorId = _NextAnchorId,
                .LivePhysicalRecordOffsets = _LivePhysicalRecordIdsByOffset.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value),
                .Extents = New List(Of ExtentIndexEntry)(_Extents),
                .PhysicalRecords = New Dictionary(Of Long, PhysicalRecordEntry)(_PhysicalRecords),
                .ChunkMacKey = If(_ChunkMacKey Is Nothing, Nothing, DirectCast(_ChunkMacKey.Clone(), Byte()))
            }

        End Function

        '
        ' The affected logical ranges for a physical-record problem: every extent that
        ' points at the record, each contributing its logical offset and length. Repairing
        ' the record zero-fills exactly these ranges.
        '
        Private Shared Function RangesReferencing(Snapshot As DiagnosticsSnapshot, RecordId As Long) As IReadOnlyList(Of LogicalRange)

            Dim Ranges As New List(Of LogicalRange)()

            For Each Extent In Snapshot.Extents
                If Extent.PhysicalRecordId = RecordId Then
                    Ranges.Add(New LogicalRange(Extent.LogicalOffset, Extent.LogicalLength))
                End If
            Next

            Return Ranges

        End Function

        Private Function CollectValidationProblems(Snapshot As DiagnosticsSnapshot,
                                                   ProgressCallback As StreamProgressCallback,
                                                   ThreadingCancellationToken As Threading.CancellationToken) As List(Of ValidationProblem)

            If Snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(Snapshot))

            Dim Problems As New List(Of ValidationProblem)()

            CollectExtentProblems(Snapshot, Problems)
            CollectRefCountProblems(Snapshot, Problems)
            CollectAnchorProblems(Snapshot, Problems)
            CollectCacheProblems(Snapshot, Problems)
            CollectPhysicalRecordProblems(Snapshot, Problems, ProgressCallback, ThreadingCancellationToken)

            Return Problems

        End Function

        Private Shared Sub CollectExtentProblems(Snapshot As DiagnosticsSnapshot, Problems As List(Of ValidationProblem))

            Dim ExpectedOffset As Long = 0
            Dim AnchorIds As New HashSet(Of Long)()
            Dim AnchorOffsets As New HashSet(Of Long)()

            For Each Extent In Snapshot.Extents

                If Extent.LogicalOffset < 0 Then
                    Problems.Add(StructureProblem("An extent has a negative logical offset."))
                ElseIf Extent.LogicalOffset <> ExpectedOffset Then
                    Problems.Add(StructureProblem($"The extent chain has a gap or overlap at logical offset {ExpectedOffset}."))
                End If

                If Extent.LogicalLength <= 0 Then Problems.Add(StructureProblem("An extent has an invalid logical length."))
                If Extent.PhysicalRecordOffset < 0 Then Problems.Add(StructureProblem("An extent has a negative physical-record offset."))
                If Extent.AnchorId < 0 Then Problems.Add(StructureProblem("An extent has a negative anchor id."))

                If Extent.AnchorId > 0 Then
                    If AnchorIds.Add(Extent.AnchorId) = False Then Problems.Add(StructureProblem($"Duplicate anchor id {Extent.AnchorId}."))
                    If AnchorOffsets.Add(Extent.LogicalOffset) = False Then Problems.Add(StructureProblem($"Multiple anchors identify logical offset {Extent.LogicalOffset}."))
                End If

                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                    If Extent.PhysicalRecordOffset <> 0 Then Problems.Add(StructureProblem("A sparse extent has a non-zero physical-record offset."))
                Else
                    Dim Record As PhysicalRecordEntry
                    If Snapshot.PhysicalRecords.TryGetValue(Extent.PhysicalRecordId, Record) = False Then
                        Problems.Add(New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.MissingPhysicalRecord,
                                                           $"Extent at logical offset {Extent.LogicalOffset} references physical record {Extent.PhysicalRecordId}, which is not in the record table.",
                                                           Extent.PhysicalRecordId,
                                                           {New LogicalRange(Extent.LogicalOffset, Extent.LogicalLength)},
                                                           CanRepair:=True, RepairIsLossy:=True))
                    ElseIf Extent.PhysicalRecordOffset > Record.PlainLength - Extent.LogicalLength Then
                        Problems.Add(New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.ExtentBeyondPhysicalRecord,
                                                           $"Extent at logical offset {Extent.LogicalOffset} references data beyond the end of physical record {Extent.PhysicalRecordId}.",
                                                           Extent.PhysicalRecordId,
                                                           {New LogicalRange(Extent.LogicalOffset, Extent.LogicalLength)},
                                                           CanRepair:=True, RepairIsLossy:=True))
                    End If
                End If

                If Extent.LogicalLength > 0 AndAlso Extent.LogicalOffset <= Long.MaxValue - CLng(Extent.LogicalLength) Then
                    ExpectedOffset = Extent.LogicalOffset + CLng(Extent.LogicalLength)
                End If

            Next

            If ExpectedOffset <> Snapshot.LogicalLength Then
                Problems.Add(New ValidationProblem(ValidationSeverity.Warning, ValidationProblemKind.LogicalLengthMismatch,
                                                   $"The logical length is {Snapshot.LogicalLength} but the extent chain spans {ExpectedOffset}.",
                                                   Nothing, Nothing, CanRepair:=True, RepairIsLossy:=False))
            End If

        End Sub

        Private Shared Function StructureProblem(Message As String) As ValidationProblem
            Return New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.ExtentChainStructure,
                                         Message, Nothing, Nothing, CanRepair:=False, RepairIsLossy:=False)
        End Function

        Private Shared Sub CollectRefCountProblems(Snapshot As DiagnosticsSnapshot, Problems As List(Of ValidationProblem))

            Dim ActualCounts As New Dictionary(Of Long, Integer)()

            For Each Extent In Snapshot.Extents
                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then Continue For
                Dim Count As Integer = 0
                ActualCounts.TryGetValue(Extent.PhysicalRecordId, Count)
                ActualCounts(Extent.PhysicalRecordId) = Count + 1
            Next

            For Each Pair In Snapshot.PhysicalRecords
                Dim ActualCount As Integer = 0
                ActualCounts.TryGetValue(Pair.Key, ActualCount)
                If Pair.Value.RefCount <> ActualCount Then
                    ' A too-low count lets the record be reclaimed while still referenced,
                    ' so a mismatch is an integrity error even though the fix is non-lossy.
                    Problems.Add(New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.RefCountMismatch,
                                                       $"Physical record {Pair.Key} has a stored reference count of {Pair.Value.RefCount}; {ActualCount} extents reference it.",
                                                       Pair.Key, Nothing, CanRepair:=True, RepairIsLossy:=False))
                End If
            Next

        End Sub

        Private Shared Sub CollectAnchorProblems(Snapshot As DiagnosticsSnapshot, Problems As List(Of ValidationProblem))

            Dim SeenAnchorIds As New HashSet(Of Long)()

            For Each Extent In Snapshot.Extents
                If Extent.AnchorId <= 0 Then Continue For
                SeenAnchorIds.Add(Extent.AnchorId)
            Next

            If SeenAnchorIds.Count <> Snapshot.AnchorIndexCount Then
                Problems.Add(New ValidationProblem(ValidationSeverity.Warning, ValidationProblemKind.AnchorIndexMismatch,
                                                   $"The anchor index holds {Snapshot.AnchorIndexCount} entries; {SeenAnchorIds.Count} extents are anchored.",
                                                   Nothing, Nothing, CanRepair:=True, RepairIsLossy:=False))
            End If

        End Sub

        '
        ' Checks the incrementally maintained caches against a fresh rebuild: the
        ' physical-data end (its drift caused the Defragment(Move) corruption incident), the
        ' anchor-id allocator, and the live physical-record offset index.
        '
        Private Shared Sub CollectCacheProblems(Snapshot As DiagnosticsSnapshot, Problems As List(Of ValidationProblem))

            Dim TrueDataEnd As Long = DataStartOffset
            Dim HighestAnchorId As Long = 0
            Dim TrueLiveOffsets As New Dictionary(Of Long, Long)()
            Dim DuplicateLiveOffset = False

            For Each Record In Snapshot.PhysicalRecords.Values
                If Record.RefCount <= 0 Then Continue For
                TrueDataEnd = Math.Max(TrueDataEnd, Record.PhysicalOffset + CLng(Record.PhysicalLength))
                If TrueLiveOffsets.ContainsKey(Record.PhysicalOffset) Then
                    DuplicateLiveOffset = True
                Else
                    TrueLiveOffsets.Add(Record.PhysicalOffset, Record.RecordId)
                End If
            Next

            For Each Extent In Snapshot.Extents
                If Extent.AnchorId > HighestAnchorId Then HighestAnchorId = Extent.AnchorId
            Next

            If Snapshot.DataEnd <> TrueDataEnd Then
                Problems.Add(CacheProblem(
                    $"The cached physical-data end is {Snapshot.DataEnd}; the live physical records end at {TrueDataEnd}."))
            End If

            If Snapshot.NextAnchorId <= HighestAnchorId Then
                Problems.Add(CacheProblem(
                    $"The next anchor id is {Snapshot.NextAnchorId} but anchor id {HighestAnchorId} is already in use."))
            End If

            If DuplicateLiveOffset = False AndAlso LiveOffsetIndexDiffers(Snapshot.LivePhysicalRecordOffsets, TrueLiveOffsets) Then
                Problems.Add(CacheProblem(
                    $"The live physical-record offset index holds {Snapshot.LivePhysicalRecordOffsets.Count} entries; {TrueLiveOffsets.Count} live records exist."))
            End If

        End Sub

        Private Shared Function CacheProblem(Message As String) As ValidationProblem
            Return New ValidationProblem(ValidationSeverity.Warning, ValidationProblemKind.CacheInconsistency,
                                         Message, Nothing, Nothing, CanRepair:=True, RepairIsLossy:=False)
        End Function

        Private Shared Function LiveOffsetIndexDiffers(Cached As Dictionary(Of Long, Long),
                                                       Rebuilt As Dictionary(Of Long, Long)) As Boolean
            If Cached.Count <> Rebuilt.Count Then Return True
            For Each Pair In Rebuilt
                Dim CachedRecordId As Long
                If Cached.TryGetValue(Pair.Key, CachedRecordId) = False OrElse CachedRecordId <> Pair.Value Then Return True
            Next
            Return False
        End Function

        Private Sub CollectPhysicalRecordProblems(Snapshot As DiagnosticsSnapshot,
                                                  Problems As List(Of ValidationProblem),
                                                  ProgressCallback As StreamProgressCallback,
                                                  ThreadingCancellationToken As Threading.CancellationToken)

            Dim TotalRecords = Math.Max(1, Snapshot.PhysicalRecords.Count)
            Dim ProcessedRecords As Long = 0
            Dim CancellationToken = If(ProgressCallback Is Nothing, Nothing, New CancellationToken)

            For Each Pair In Snapshot.PhysicalRecords.OrderBy(Function(Item) Item.Key)

                If CancellationToken?.Cancel Then Return
                ThreadingCancellationToken.ThrowIfCancellationRequested()

                If Pair.Value.RefCount > 0 Then
                    Dim Problem = InspectPhysicalRecordSnapshot(Snapshot, Pair.Value, ReadPhysicalRecordForValidation(Pair.Value.RecordId))
                    If Problem IsNot Nothing Then Problems.Add(Problem)
                End If

                ProcessedRecords += 1
                ProgressCallback?.Invoke(ProcessedRecords, TotalRecords, ProcessUnitTypes.Chunks, CancellationToken)

            Next

        End Sub

        '
        ' Reads one live physical record's stored bytes for validation, re-entering the
        ' state lock only long enough to copy them. Validation buffers a single record at a
        ' time this way rather than the whole archive at once. Returns Nothing when the
        ' record is no longer live or no longer fits the backing stream, which
        ' InspectPhysicalRecordSnapshot reports as an unreadable record.
        '
        Private Function ReadPhysicalRecordForValidation(RecordId As Long) As Byte()

            Using EnterStateLock()

                ThrowIfDisposed()

                Dim Record As PhysicalRecordEntry
                If _PhysicalRecords.TryGetValue(RecordId, Record) = False Then Return Nothing
                If Record.RefCount <= 0 Then Return Nothing
                If Record.PhysicalOffset < DataStartOffset Then Return Nothing
                If Record.PhysicalLength < MinChunkRecordSize Then Return Nothing
                If Record.PhysicalOffset > BaseStream.Length - Record.PhysicalLength Then Return Nothing

                Dim Buffer(Record.PhysicalLength - 1) As Byte
                ReadAt(Record.PhysicalOffset, Buffer, 0, Buffer.Length)
                Return Buffer

            End Using

        End Function

        Private Function InspectPhysicalRecordSnapshot(Snapshot As DiagnosticsSnapshot,
                                                       Record As PhysicalRecordEntry,
                                                       Buffer As Byte()) As ValidationProblem

            Dim Unreadable = Function(message As String) _
                New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.PhysicalRecordUnreadable,
                                      message, Record.RecordId, RangesReferencing(Snapshot, Record.RecordId),
                                      CanRepair:=True, RepairIsLossy:=True)

            If Record.RecordId <= SparsePhysicalRecordId Then Return Unreadable($"Physical record {Record.RecordId} has an invalid id.")
            If Record.PhysicalOffset < DataStartOffset Then Return Unreadable($"Physical record {Record.RecordId} has an invalid offset.")
            If Record.PhysicalLength < MinChunkRecordSize Then Return Unreadable($"Physical record {Record.RecordId} has an invalid length.")

            If Buffer Is Nothing Then
                Return Unreadable($"Physical record {Record.RecordId} extends beyond the end of the backing stream.")
            End If
            If BitConverter.ToInt64(Buffer, 0) <> Record.RecordId Then Return Unreadable($"Physical record {Record.RecordId} has a mismatched id in its stored header.")

            Dim EncryptionMethod = CType(BitConverter.ToInt32(Buffer, ChunkEncryptionMethodOffset), ChunkEncryptionMethods)
            Dim PlainLength = BitConverter.ToInt32(Buffer, ChunkPlainLengthOffset)
            Dim PayloadLength = BitConverter.ToInt32(Buffer, ChunkPayloadLengthOffset)
            Dim Flags = CType(BitConverter.ToInt32(Buffer, ChunkFlagsOffset), ChunkFlags)
            Dim CompressionEvaluatedPercent = CInt(Buffer(ChunkCompressionEvaluatedPercentOffset))
            Dim SubBlockCount = BitConverter.ToInt32(Buffer, ChunkSubBlockCountOffset)

            If PlainLength < 0 Then Return Unreadable($"Physical record {Record.RecordId} has an invalid plain length.")
            If PayloadLength < 0 OrElse ChunkRecordHeaderSize + PayloadLength <> Buffer.Length Then Return Unreadable($"Physical record {Record.RecordId} has an inconsistent stored length.")
            If SubBlockCount <= 0 OrElse SubBlockCount > Math.Max(1, PlainLength) Then Return Unreadable($"Physical record {Record.RecordId} has an invalid sub-block count.")
            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then Return Unreadable($"Physical record {Record.RecordId} has unsupported chunk flags {CInt(Flags)}.")
            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then
                Return Unreadable($"Physical record {Record.RecordId} has an invalid compression-evaluated percent {CompressionEvaluatedPercent}.")
            End If

            Dim RecordMacKey = If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey, Snapshot.ChunkMacKey, PublicIntegrityKey)
            If RecordMacKey Is Nothing Then
                Return New ValidationProblem(ValidationSeverity.[Error], ValidationProblemKind.PhysicalRecordKeyUnavailable,
                                             $"Physical record {Record.RecordId} is encrypted but no file master key is available to authenticate it.",
                                             Record.RecordId, RangesReferencing(Snapshot, Record.RecordId),
                                             CanRepair:=False, RepairIsLossy:=False)
            End If

            Dim SubBlockLengthTableSize = SubBlockCount * 4
            Dim SubBlockMacCoveredPrefixSize = ChunkRecordHeaderSize + SubBlockLengthTableSize
            If SubBlockMacCoveredPrefixSize > Buffer.Length Then Return Unreadable($"Physical record {Record.RecordId} has a truncated sub-block length table.")

            Dim SubBlockOffset = SubBlockMacCoveredPrefixSize
            For SubBlockIndex = 0 To SubBlockCount - 1

                Dim SubStoredLength = BitConverter.ToInt32(Buffer, ChunkRecordHeaderSize + SubBlockIndex * 4)
                If SubStoredLength < 0 OrElse SubBlockOffset + IvSize + SubStoredLength + MacSize > Buffer.Length Then
                    Return Unreadable($"Physical record {Record.RecordId} has an invalid sub-block length.")
                End If

                Dim MacOffset = SubBlockOffset + IvSize + SubStoredLength

                Using Hmac As New HMACSHA256(RecordMacKey)
                    Hmac.TransformBlock(Buffer, 0, SubBlockMacCoveredPrefixSize, Nothing, 0)
                    Dim ExpectedMac = Hmac.ComputeHash(Buffer, SubBlockOffset, IvSize + SubStoredLength)
                    If FixedTimeEquals(ExpectedMac, 0, Buffer, MacOffset, MacSize) = False Then
                        Return Unreadable($"Physical record {Record.RecordId} failed authentication.")
                    End If
                End Using

                SubBlockOffset = MacOffset + MacSize

            Next

            If SubBlockOffset <> Buffer.Length Then Return Unreadable($"Physical record {Record.RecordId}'s sub-blocks do not account for the whole record.")

            Return Nothing

        End Function

    End Class
End Namespace
