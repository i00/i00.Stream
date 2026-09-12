' ================================================================================
' ChunkedStream Defragmentation
' ================================================================================
'
' Purpose
'   - Physical storage optimisation and physical-record layout maintenance.
'
' Supported Modes
'   - Move
'       Moves later physical records into suitable earlier holes where they fit.
'
'   - Sequence
'       Reorders live physical records into record-id order and packs them from
'       DataStartOffset using journalled physical-record moves.
'
'   - Rebuild
'       Rewrites the logical stream using the current options, including chunk size,
'       compression, encryption and sparse policy, then sequences the resulting
'       physical records.
'
' Design
'   - Every mode first reclaims physical records that no live extent references.
'   - Defragmentation operates directly on live physical records.
'   - Physical record moves are journalled for crash recovery.
'   - Rebuild uses rebuild recovery and publishes the rebuilt metadata before
'     running the final sequence phase.
'
' Notes
'   - Defragmentation is not permitted while a checkpoint is active.
'
' ================================================================================
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        ''' <summary>
        ''' Physical storage optimisation strategy used by Defragment.
        ''' </summary>
        Public Enum DefragTypes
            ''' <summary>
            ''' Fast compaction mode. Moves later physical records into earlier holes where they fit.
            ''' </summary>
            Move = 0

            ''' <summary>
            ''' Reorders live physical records into record-id order.
            ''' </summary>
            Sequence = 1

            ''' <summary>
            ''' Fully rewrites the logical stream using the current options.
            ''' </summary>
            Rebuild = 2
        End Enum

        Private Structure DefragLiveRecord
            Public RecordId As Long
            Public Offset As Long
            Public RecordLength As Integer
        End Structure

        ''' <summary>
        ''' How many chunks' worth of MaxChunkSize Rebuild reads at once when content-defined
        ''' chunking is active, so the CDC rolling hash gets a long continuous scan instead of
        ''' resetting at every chunk-sized boundary. See the remarks where it's used.
        ''' </summary>
        Private Const RebuildContinuousScanChunkMultiple As Integer = 64

        Private Structure DefragHole
            Public Offset As Long
            Public Length As Long
        End Structure

        Private Enum DefragmentSequenceProgressModes
            Normal
            RebuildFinalPhase
        End Enum

        ''' <summary>
        ''' Defragments the physical storage layout.
        ''' </summary>
        ''' <remarks>
        ''' Defragmentation cannot be performed while a checkpoint is active.
        ''' </remarks>
        Public Function Defragment(Optional Type As DefragTypes = DefragTypes.Move,
                                   Optional ProgressCallback As StreamProgressCallback = Nothing) As Long

            Using EnterStateLock()
                Return DefragmentCore(Type, ProgressCallback)
            End Using

        End Function

        ''' <summary>
        ''' Asynchronously defragments the physical storage layout.
        ''' </summary>
        ''' <remarks>
        ''' Defragmentation is a long-running batch operation and is executed on a worker
        ''' thread. When <paramref name="CancellationToken" /> is cancelled it is surfaced to
        ''' the internal operation through its progress cancellation token; the call then
        ''' completes with a return value of -1 (cancelled), matching the synchronous
        ''' contract, rather than throwing.
        ''' </remarks>
        Public Function DefragmentAsync(Optional Type As DefragTypes = DefragTypes.Move,
                                        Optional ProgressCallback As StreamProgressCallback = Nothing,
                                        Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Long)

            Dim EffectiveCallback = BridgeProgressCancellation(ProgressCallback, CancellationToken)

            '
            ' The token is deliberately not handed to Task.Run: cancellation is surfaced
            ' through EffectiveCallback so the operation unwinds cleanly and returns its
            ' cancelled result (-1) rather than the task faulting with a cancellation.
            '
            Return Task.Run(
                Function()
                    Using EnterStateLock()
                        Return DefragmentCore(Type, EffectiveCallback)
                    End Using
                End Function)

        End Function

        '
        ' Wraps a caller progress callback so a Threading.CancellationToken firing also sets
        ' the operation's own progress cancellation token. Returns the original callback
        ' unchanged when the token can never be cancelled.
        '
        Private Shared Function BridgeProgressCancellation(ProgressCallback As StreamProgressCallback,
                                                          CancellationToken As Threading.CancellationToken) As StreamProgressCallback

            If Not CancellationToken.CanBeCanceled Then Return ProgressCallback

            Return Sub(ProcessedUnits, TotalUnits, UnitType, Token)
                       If CancellationToken.IsCancellationRequested Then Token.Cancel = True
                       If ProgressCallback IsNot Nothing Then ProgressCallback(ProcessedUnits, TotalUnits, UnitType, Token)
                   End Sub

        End Function

        Private Function DefragmentCore(Optional Type As DefragTypes = DefragTypes.Move,
                                        Optional ProgressCallback As StreamProgressCallback = Nothing) As Long


            ThrowIfDisposed()
            ThrowIfFaulted()

            If HasActiveCheckpoint Then
                Throw New InvalidOperationException("Defragmentation cannot be performed while a checkpoint is active.")
            End If

            If _DeferPublishDepth > 0 Then
                Throw New InvalidOperationException("Defragmentation cannot be performed while metadata publishing is deferred.")
            End If

            ' A pending write-cache buffer isn't reflected in _Extents/_PhysicalRecords, so it must
            ' be materialised before anything below scans or relocates the live record set.
            If _PendingChunkPlain IsNot Nothing Then
                CommitPendingChunkAsync(RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()
            End If

            '
            ' Drop any physical record that no live extent points at before compacting.
            ' Move and Sequence only ever relocate referenced records, and the live
            ' data end (which drives both the fragmentation figure and the trailing-space
            ' trim) is derived from every entry in _PhysicalRecords, so orphaned records
            ' left behind by an earlier edit would otherwise keep the stream fragmented
            ' and its tail un-trimmed. Rebuild reconstructs the record set anyway; running
            ' the sweep here first keeps every mode consistent.
            '
            ReclaimUnreferencedPhysicalRecords()

            '
            ' The cached physical-data end is maintained incrementally and an earlier edit
            ' can leave it stale-high (it only ratchets down when the record sitting at the
            ' very end is the one that moves). Every defragment pass trims the backing
            ' stream to a value derived from this cache, so refresh it from the live records
            ' now - a stale value would make a later pass fail its "data end beyond the
            ' backing stream" guard against a stream a previous pass has already shortened.
            '
            RecalculatePhysicalDataEnd()

            InvalidateChunkCache()
            ClearFreeSpaceMap()

            Dim OriginalLength = BaseStream.Length
            Dim CancellationToken As New CancellationToken()

            Try

                Select Case Type
                    Case DefragTypes.Move
                        DefragmentMove(ProgressCallback, CancellationToken)

                    Case DefragTypes.Sequence
                        DefragmentSequence(DefragmentSequenceProgressModes.Normal,
                                           ProgressCallback,
                                           CancellationToken)

                    Case DefragTypes.Rebuild
                        DefragmentRebuild(ProgressCallback, CancellationToken)

                    Case Else
                        Throw New ArgumentOutOfRangeException(NameOf(Type))
                End Select

            Catch

                '
                ' A defragment that fails partway has already rotated on-disk headers
                ' through its intermediate metadata publishes and its in-memory indexes
                ' may be half-updated. Fault the stream so DisposeCore does not publish
                ' that state over the last good generation - the caller can dispose and
                ' reopen the file exactly as it stood before this call.
                '
                _Faulted = True
                Throw

            Finally

                '
                ' Skip the rebuild on the fault path: the map would be built from
                ' inconsistent state and could throw, masking the original failure.
                '
                If _Faulted = False Then BuildFreeSpaceMapCore()

            End Try

            If CancellationToken.Cancel Then
                Return -1
            End If

            Return Math.Max(0L, OriginalLength - BaseStream.Length)


        End Function

        Private Function GetDefragStagingOffset() As Long

            Return Math.Max(BaseStream.Length, GetDataEndFromIndex())

        End Function

        Private Sub StreamCopyRecord(SourceOffset As Long,
                                     DestinationOffset As Long,
                                     Length As Integer)

            If Length <= 0 Then Return

            Const CopyBufferSize As Integer = 1024 * 1024

            Dim Buffer(Math.Min(CopyBufferSize, Length) - 1) As Byte

            If DestinationOffset > SourceOffset AndAlso DestinationOffset < SourceOffset + Length Then

                Dim Remaining = Length
                Dim SourceEnd = SourceOffset + Length
                Dim DestinationEnd = DestinationOffset + Length

                While Remaining > 0

                    Dim BytesToCopy = Math.Min(Remaining, Buffer.Length)

                    SourceEnd -= BytesToCopy
                    DestinationEnd -= BytesToCopy

                    ReadAt(SourceEnd, Buffer, 0, BytesToCopy)

                    WriteAt(DestinationEnd, Buffer, 0, BytesToCopy)

                    Remaining -= BytesToCopy

                End While

                Return

            End If

            Dim ForwardRemaining = Length
            Dim ReadOffset = SourceOffset
            Dim WriteOffset = DestinationOffset

            While ForwardRemaining > 0

                Dim BytesToCopy = Math.Min(ForwardRemaining, Buffer.Length)

                ReadAt(ReadOffset, Buffer, 0, BytesToCopy)

                WriteAt(WriteOffset, Buffer, 0, BytesToCopy)

                ReadOffset += BytesToCopy
                WriteOffset += BytesToCopy
                ForwardRemaining -= BytesToCopy

            End While

        End Sub

        Private Shared Function RangesOverlap(Offset1 As Long,
                                              Length1 As Long,
                                              Offset2 As Long,
                                              Length2 As Long) As Boolean

            Return Offset1 < Offset2 + Length2 AndAlso Offset2 < Offset1 + Length1

        End Function

        Private Function GetRecordAtPhysicalOffset(Offset As Long,
                                                   Length As Long,
                                                   Optional ExceptRecordId As Long? = Nothing) As PhysicalRecordEntry?

            For Each Record In _PhysicalRecords.Values

                If Record.RefCount <= 0 Then Continue For

                If ExceptRecordId.HasValue AndAlso Record.RecordId = ExceptRecordId.Value Then Continue For

                If RangesOverlap(Record.PhysicalOffset,
                                 Record.PhysicalLength,
                                 Offset,
                                 Length) Then

                    Return Record

                End If

            Next

            Return Nothing

        End Function

        Private Sub RelocatePhysicalRecord(RecordId As Long,
                                           NewOffset As Long)

            Dim Record = GetPhysicalRecord(RecordId)

            If Record.PhysicalOffset = NewOffset Then Return

            Dim OldOffset = Record.PhysicalOffset
            Dim OldLength = Record.PhysicalLength

            WriteJournal(JournalStates.Copying,
                         Record.RecordId,
                         OldOffset,
                         OldLength,
                         NewOffset,
                         OldLength)

            StreamCopyRecord(OldOffset,
                             NewOffset,
                             OldLength)

            FlushDurable()

            If IsValidPhysicalRecordAt(Record.RecordId,
                                       NewOffset,
                                       OldLength) = False Then

                Throw New CryptographicException(
                    "Copied physical record failed validation.")

            End If

            WriteJournal(JournalStates.Copied,
                         Record.RecordId,
                         OldOffset,
                         OldLength,
                         NewOffset,
                         OldLength)

            Record.PhysicalOffset = NewOffset
            _PhysicalRecords(Record.RecordId) = Record

            UpdatePhysicalRecordLocationIndexes(Record.RecordId,
                                                OldOffset,
                                                OldLength)

            '
            ' Only the moved physical record entry changed.
            ' Extents did not change, and the physical-record count did not change.
            '
            MarkPhysicalRecordPageDirty(Record.RecordId)

            '
            ' Publish the moved record metadata so a later crash does not leave committed
            ' metadata pointing at a target range that may be reused by subsequent moves.
            ' The intermediate publish intentionally suppresses hole-directory persistence.
            '
            PersistDefragMoveMetadata()

            AddFreeSpaceExcludingRange(OldOffset,
                                       OldLength,
                                       NewOffset,
                                       OldLength)

            ClearJournal()

        End Sub

        Private Sub MarkPhysicalRecordPageDirty(RecordId As Long)

            Dim Ordinal As Integer

            If _PhysicalRecordOrdinals.TryGetValue(RecordId, Ordinal) = False Then
                Throw New InvalidDataException(
            $"Physical record {RecordId} was not found while marking metadata dirty.")
            End If

            MarkPhysicalRecordPageDirtyByOrdinal(Ordinal)

        End Sub

        Private Sub PersistDefragMoveMetadata()

            Dim MetadataOffset = Math.Max(BaseStream.Length, GetDataEndFromIndex())
            Dim OriginalHoleDirectoryMode = Options.HoleDirectoryMode

            _CompactMetadataWriteOffset = MetadataOffset
            _CompactMetadataWriteLimit = Nothing

            Try

                '
                ' Intermediate defrag move metadata must not persist the hole directory.
                '
                ' Persisting the hole directory after every moved physical record can write
                ' a very large number of metadata pages and causes the stream to grow
                ' rapidly during defrag. The final defrag checkpoint publish is responsible
                ' for writing the compacted metadata and, if configured, the hole directory.
                '
                Options.HoleDirectoryMode = ChunkedStreamOptions.HoleDirectoryModes.Never

                PersistIndexAndHeader(MetadataOffset, True)

            Finally

                Options.HoleDirectoryMode = OriginalHoleDirectoryMode
                _CompactMetadataWriteOffset = Nothing
                _CompactMetadataWriteLimit = Nothing

            End Try

        End Sub

        Private Sub AddFreeSpaceExcludingRange(SourceOffset As Long,
                                               SourceLength As Long,
                                               ExcludeOffset As Long,
                                               ExcludeLength As Long)

            If SourceLength <= 0 Then Return

            Dim SourceEnd = SourceOffset + SourceLength
            Dim ExcludeEnd = ExcludeOffset + ExcludeLength

            If RangesOverlap(SourceOffset, SourceLength, ExcludeOffset, ExcludeLength) = False Then
                AddFreeSpace(SourceOffset, SourceLength)
                Return
            End If

            If ExcludeOffset > SourceOffset Then
                AddFreeSpace(SourceOffset, ExcludeOffset - SourceOffset)
            End If

            If ExcludeEnd < SourceEnd Then
                AddFreeSpace(ExcludeEnd, SourceEnd - ExcludeEnd)
            End If

        End Sub

        Private Sub EvacuateTargetRegion(TargetOffset As Long,
                                         TargetLength As Long,
                                         Optional ExceptRecordId As Long? = Nothing)

            While True

                Dim BlockingRecord =
                    GetRecordAtPhysicalOffset(TargetOffset,
                                              TargetLength,
                                              ExceptRecordId)

                If BlockingRecord.HasValue = False Then Exit While

                Dim Record = BlockingRecord.Value
                Dim StagingOffset = GetDefragStagingOffset()

                RelocatePhysicalRecord(Record.RecordId, StagingOffset)

            End While

        End Sub

        Private Function GetLiveRecordsSortedByOffset() As List(Of DefragLiveRecord)

            Dim Result As New List(Of DefragLiveRecord)()

            For Each record In _PhysicalRecords.Values

                If record.RefCount <= 0 Then Continue For

                If record.PhysicalOffset < DataStartOffset Then
                    Throw New InvalidDataException($"Invalid record offset for record {record.RecordId}.")
                End If

                If record.PhysicalLength < MinChunkRecordSize Then
                    Throw New InvalidDataException($"Invalid record length for record {record.RecordId}.")
                End If

                Result.Add(New DefragLiveRecord With {
                    .RecordId = record.RecordId,
                    .Offset = record.PhysicalOffset,
                    .RecordLength = record.PhysicalLength
                })

            Next

            Result.Sort(Function(left, right) left.Offset.CompareTo(right.Offset))

            Return Result

        End Function

        Private Function GetDeadHoles(LiveRecords As List(Of DefragLiveRecord)) As List(Of DefragHole)

            If LiveRecords Is Nothing Then
                Throw New ArgumentNullException(NameOf(LiveRecords))
            End If

            Dim ReservedRanges As New List(Of Tuple(Of Long, Long))()

            For Each Record In LiveRecords

                If Record.RecordLength <= 0 Then Continue For

                ReservedRanges.Add(
                        Tuple.Create(
                            Record.Offset,
                            Record.Offset + CLng(Record.RecordLength)))

            Next

            ReservedRanges.AddRange(GetActiveMetadataRanges())

            ReservedRanges = ReservedRanges.Where(Function(Range) Range.Item2 > DataStartOffset AndAlso Range.Item2 > Range.Item1).
                                            Select(Function(Range) Tuple.Create(Math.Max(CLng(DataStartOffset), Range.Item1), Range.Item2)).
                                            OrderBy(Function(Range) Range.Item1).
                                            ThenBy(Function(Range) Range.Item2).
                                            ToList()

            Dim Holes As New List(Of DefragHole)()
            Dim Cursor = CLng(DataStartOffset)

            For Each Range In ReservedRanges

                Dim RangeStart = Range.Item1
                Dim RangeEnd = Range.Item2

                If RangeStart > Cursor Then
                    Holes.Add(New DefragHole With {
                                  .Offset = Cursor,
                                  .Length = RangeStart - Cursor
                              })
                End If

                Cursor = Math.Max(Cursor, RangeEnd)

            Next

            Return Holes

        End Function

        Private Function FindLatestLiveRecordThatFitsHole(LiveRecords As List(Of DefragLiveRecord),
                                                          Hole As DefragHole) As Integer

            For Index = LiveRecords.Count - 1 To 0 Step -1

                Dim Record = LiveRecords(Index)

                If Record.Offset <= Hole.Offset Then Exit For

                If Record.RecordLength <= Hole.Length Then
                    Return Index
                End If

            Next

            Return -1

        End Function

        Private Sub DefragmentMove(ProgressCallback As StreamProgressCallback,
                                   CancellationToken As CancellationToken)

            Const ProgressScale As Long = 1000000

            ReportProgress(ProgressCallback,
                           0,
                           ProgressScale,
                           ProcessUnitTypes.Arbitrary,
                           CancellationToken)

            Dim OriginalFragmentation As Double? = Nothing

            '
            ' A pass fills every hole it can see, then compacts the surviving metadata into
            ' one block after the data. That compaction frees the scattered locations the
            ' metadata pages came from - fresh holes the pass could not see - so keep
            ' running passes until one relocates nothing. Every productive pass strictly
            ' lowers the total live-record offset, so this terminates.
            '
            Do

                Dim MovedInPass = False

                Do

                    If CancellationToken.Cancel Then Return

                    Dim LiveRecords = GetLiveRecordsSortedByOffset()
                    Dim Holes = GetDeadHoles(LiveRecords)

                    If Holes.Count = 0 Then Exit Do

                    Dim MovedSomething = False

                    For Each hole In Holes

                        If CancellationToken.Cancel Then Return

                        Dim CandidateIndex = FindLatestLiveRecordThatFitsHole(LiveRecords, hole)

                        If CandidateIndex < 0 Then Continue For

                        Dim Candidate = LiveRecords(CandidateIndex)

                        RelocatePhysicalRecord(Candidate.RecordId, hole.Offset)

                        MovedSomething = True
                        MovedInPass = True

                        Dim CurrentFragmentation = GetFragmentation()

                        If OriginalFragmentation.HasValue = False Then
                            OriginalFragmentation = Math.Max(CurrentFragmentation, 0.000001R)
                        End If

                        Dim CompletedUnits = CLng(((OriginalFragmentation.Value - CurrentFragmentation) / OriginalFragmentation.Value) * ProgressScale)

                        If CompletedUnits < 0 Then CompletedUnits = 0
                        If CompletedUnits > ProgressScale Then CompletedUnits = ProgressScale

                        ReportProgress(ProgressCallback,
                                       CompletedUnits,
                                       ProgressScale,
                                       ProcessUnitTypes.Arbitrary,
                                       CancellationToken)

                        Exit For

                    Next

                    If MovedSomething = False Then Exit Do

                Loop

                If CancellationToken.Cancel Then Return

                CommitDefragCheckpoint()

                If MovedInPass = False Then Exit Do

            Loop

            ReportProgress(ProgressCallback,
                           ProgressScale,
                           ProgressScale,
                           ProcessUnitTypes.Arbitrary,
                           CancellationToken)

        End Sub

        Private Sub CommitDefragCheckpoint()

            TrimAndCommitDefragMetadata()

        End Sub

        Private Sub TrimAndCommitDefragMetadata()

            '
            ' Refresh the cached physical-data end from the live records before trusting it.
            ' A previous pass has already trimmed the backing stream, and an incrementally
            ' maintained cache can sit stale-high; recomputing here means the guard below
            ' only fires on genuine corruption - a live record that really does claim space
            ' past the end of the backing stream.
            '
            RecalculatePhysicalDataEnd()

            Dim DataEnd = GetDataEndFromIndex()

            If DataEnd < DataStartOffset Then
                Throw New InvalidDataException("Invalid defrag data end.")
            End If

            If DataEnd > BaseStream.Length Then
                Throw New InvalidDataException("Defrag data end is beyond the backing stream length.")
            End If

            Dim CompactDataEnd = Math.Max(CLng(DataStartOffset), DataEnd)
            Dim FirstActiveMetadataOffset = GetFirstActiveMetadataOffset()
            Dim CompactWriteLimit As Long? = Nothing

            If FirstActiveMetadataOffset > CompactDataEnd Then
                CompactWriteLimit = FirstActiveMetadataOffset
            End If

            InvalidateChunkCache()
            ClearFreeSpaceMap()

            _IndexOffset = CompactDataEnd

            BuildFreeSpaceMapCore()

            _ExtentPageDescriptors.Clear()
            _ExtentDirectoryPageDescriptors.Clear()
            _PhysicalRecordPageDescriptors.Clear()
            _PhysicalRecordDirectoryPageDescriptors.Clear()
            _HoleDirectoryPageDescriptors.Clear()

            _MetadataRootOffset = 0
            _MetadataRootLength = 0
            _MetadataRootMac = Nothing

            MarkAllMetadataPagesDirty()

            _CompactMetadataWriteOffset = CompactDataEnd
            _CompactMetadataWriteLimit = CompactWriteLimit

            Try
                PersistIndexAndHeader(CompactDataEnd, True)
            Finally
                _CompactMetadataWriteOffset = Nothing
                _CompactMetadataWriteLimit = Nothing
            End Try

            '
            ' Trim to the true high-water mark of everything that must survive: the live
            ' physical data plus every active metadata page and the metadata root. The
            ' compacted metadata is not guaranteed to sit contiguously above the data - when
            ' it does not fit in the gap below the superseded metadata (CompactWriteLimit)
            ' the overflow pages fall back to appended offsets, and the root can be recycled
            ' into a freed hole well below the end - so deriving the new length from the root
            ' alone can truncate live records or freshly written metadata pages.
            '
            Dim NewEndOffset = Math.Max(CLng(DataStartOffset), GetDataEndFromIndex())

            For Each Range In GetActiveMetadataRanges()
                NewEndOffset = Math.Max(NewEndOffset, Range.Item2)
            Next

            If NewEndOffset < BaseStream.Length Then
                BaseStream.SetLength(NewEndOffset)
            End If

        End Sub

        Private Function GetFirstActiveMetadataOffset() As Long

            Dim Result As Long = Long.MaxValue

            For Each descriptor In _ExtentPageDescriptors.Values
                If descriptor.Offset > 0 AndAlso descriptor.Length > 0 Then
                    Result = Math.Min(Result, descriptor.Offset)
                End If
            Next

            For Each descriptor In _ExtentDirectoryPageDescriptors.Values
                If descriptor.Offset > 0 AndAlso descriptor.Length > 0 Then
                    Result = Math.Min(Result, descriptor.Offset)
                End If
            Next

            For Each descriptor In _PhysicalRecordPageDescriptors.Values
                If descriptor.Offset > 0 AndAlso descriptor.Length > 0 Then
                    Result = Math.Min(Result, descriptor.Offset)
                End If
            Next

            For Each descriptor In _PhysicalRecordDirectoryPageDescriptors.Values
                If descriptor.Offset > 0 AndAlso descriptor.Length > 0 Then
                    Result = Math.Min(Result, descriptor.Offset)
                End If
            Next

            For Each descriptor In _HoleDirectoryPageDescriptors.Values
                If descriptor.Offset > 0 AndAlso descriptor.Length > 0 Then
                    Result = Math.Min(Result, descriptor.Offset)
                End If
            Next

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then
                Result = Math.Min(Result, _MetadataRootOffset)
            End If

            If Result = Long.MaxValue Then
                Return BaseStream.Length
            End If

            Return Result

        End Function

        Private Sub DefragmentSequence(ProgressMode As DefragmentSequenceProgressModes,
                                       ProgressCallback As StreamProgressCallback,
                                       CancellationToken As CancellationToken)

            Dim ProgressStart As Double
            Dim ProgressEnd As Double

            Select Case ProgressMode

                Case DefragmentSequenceProgressModes.RebuildFinalPhase
                    ProgressStart = 0.5R
                    ProgressEnd = 1.0R

                Case DefragmentSequenceProgressModes.Normal
                    ProgressStart = 0.0R
                    ProgressEnd = 1.0R

                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(ProgressMode))

            End Select

            PrepareMetadataForSequenceDefrag()

            Dim OrderedRecords =
                _PhysicalRecords.Values.
                                 Where(Function(record) record.RefCount > 0).
                                 OrderBy(Function(record) record.RecordId).
                                 ToList()

            Dim TotalBytes = OrderedRecords.Sum(Function(record) CLng(record.PhysicalLength))
            Dim ProcessedBytes As Long = 0
            Dim TargetOffset = CLng(DataStartOffset)

            For Each RecordSnapshot In OrderedRecords

                If CancellationToken.Cancel Then Return
                If _PhysicalRecords.ContainsKey(RecordSnapshot.RecordId) = False Then Continue For

                Dim CurrentRecord = GetPhysicalRecord(RecordSnapshot.RecordId)

                If CurrentRecord.PhysicalOffset = TargetOffset Then

                    ProcessedBytes += CurrentRecord.PhysicalLength

                    Dim NoMoveRatio =
                        If(TotalBytes = 0,
                           1.0R,
                           ProcessedBytes / CDbl(TotalBytes))

                    Dim NoMoveScaledRatio =
                        ProgressStart + ((ProgressEnd - ProgressStart) * NoMoveRatio)

                    ReportProgress(ProgressCallback,
                                   CLng(NoMoveScaledRatio * Math.Max(1L, TotalBytes)),
                                   Math.Max(1L, TotalBytes),
                                   ProcessUnitTypes.Bytes,
                                   CancellationToken)

                    TargetOffset += CurrentRecord.PhysicalLength

                    Continue For

                End If

                If RangesOverlap(CurrentRecord.PhysicalOffset,
                                 CurrentRecord.PhysicalLength,
                                 TargetOffset,
                                 CurrentRecord.PhysicalLength) Then

                    RelocatePhysicalRecord(CurrentRecord.RecordId, GetDefragStagingOffset())
                    CurrentRecord = GetPhysicalRecord(RecordSnapshot.RecordId)

                End If

                EvacuateTargetRegion(TargetOffset,
                                     CurrentRecord.PhysicalLength,
                                     CurrentRecord.RecordId)

                CurrentRecord = GetPhysicalRecord(RecordSnapshot.RecordId)

                If ProgressMode = DefragmentSequenceProgressModes.RebuildFinalPhase OrElse
                   CurrentRecord.PhysicalOffset <> TargetOffset Then

                    RelocatePhysicalRecord(CurrentRecord.RecordId, TargetOffset)

                End If

                CurrentRecord = GetPhysicalRecord(RecordSnapshot.RecordId)

                ProcessedBytes += CurrentRecord.PhysicalLength

                Dim Ratio =
                    If(TotalBytes = 0,
                       1.0R,
                       ProcessedBytes / CDbl(TotalBytes))

                Dim ScaledRatio =
                    ProgressStart + ((ProgressEnd - ProgressStart) * Ratio)

                ReportProgress(ProgressCallback,
                               CLng(ScaledRatio * Math.Max(1L, TotalBytes)),
                               Math.Max(1L, TotalBytes),
                               ProcessUnitTypes.Bytes,
                               CancellationToken)

                TargetOffset += CurrentRecord.PhysicalLength

            Next

            If CancellationToken.Cancel Then Return

            CommitDefragCheckpoint()

        End Sub

        Private Sub PrepareMetadataForSequenceDefrag()

            '
            ' Move active metadata out of the data-compaction path before Sequence starts.
            ' This one-time publish may rewrite all metadata pages.
            '
            ' Individual record moves after this point publish only the changed
            ' physical-record metadata page and suppress hole-directory persistence.
            '
            ClearFreeSpaceMap()
            MarkAllMetadataPagesDirty()
            PersistDefragMoveMetadata()
            ClearFreeSpaceMap()

        End Sub

        Private Sub RestoreFailedRebuildState(OriginalPhysicalLength As Long,
                                              OriginalIndexOffset As Long,
                                              OriginalHeaderFlags As HeaderFlags,
                                              OriginalNextPhysicalRecordId As Long,
                                              OriginalNextAnchorId As Long,
                                              OriginalChunkSize As Integer,
                                              OriginalChunkSizeVariance As Double,
                                              OriginalIndexPageEntryCount As Integer,
                                              OriginalIndexDirectoryEntryCount As Integer,
                                              OriginalExtents As List(Of ExtentIndexEntry),
                                              OriginalPhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry))

            If OriginalExtents Is Nothing Then
                Throw New ArgumentNullException(NameOf(OriginalExtents))
            End If

            If OriginalPhysicalRecords Is Nothing Then
                Throw New ArgumentNullException(NameOf(OriginalPhysicalRecords))
            End If

            _IndexOffset = OriginalIndexOffset
            _HeaderFlags = OriginalHeaderFlags

            _NextPhysicalRecordId =
                Math.Max(SparsePhysicalRecordId + 1,
                         OriginalNextPhysicalRecordId)

            _NextAnchorId =
                Math.Max(1L,
                         OriginalNextAnchorId)

            _ChunkSize = OriginalChunkSize
            _ChunkSizeVariance = OriginalChunkSizeVariance
            _IndexPageEntryCount = OriginalIndexPageEntryCount
            _IndexDirectoryEntryCount = OriginalIndexDirectoryEntryCount

            _Extents.Clear()
            _Extents.AddRange(OriginalExtents)

            _PhysicalRecords.Clear()

            For Each pair In OriginalPhysicalRecords
                _PhysicalRecords(pair.Key) = pair.Value
            Next

            RebuildPhysicalRecordOrdinals()
            RebuildAnchorIndex()

            ClearFreeSpaceMap()
            DiscardPendingPhysicalRecordReclaims()
            InvalidateChunkCache()

            MarkAllMetadataPagesDirty()

            If BaseStream.Length > OriginalPhysicalLength Then
                BaseStream.SetLength(OriginalPhysicalLength)
            End If

        End Sub

        Private Sub DefragmentRebuild(ProgressCallback As StreamProgressCallback,
                                      CancellationToken As CancellationToken)

            If Options.ChunkSize <= 0 Then
                Throw New InvalidOperationException(
                    "Chunk size must be greater than zero.")
            End If

            If Options.IndexPageEntryCount <= 0 Then
                Throw New InvalidOperationException(
                    "Index page entry count must be greater than zero.")
            End If

            If Options.IndexDirectoryEntryCount <= 0 Then
                Throw New InvalidOperationException(
                    "Index directory entry count must be greater than zero.")
            End If

            Dim OriginalPhysicalLength = BaseStream.Length
            Dim OriginalIndexOffset = _IndexOffset
            Dim OriginalHeaderFlags = _HeaderFlags
            Dim OriginalNextPhysicalRecordId = _NextPhysicalRecordId
            Dim OriginalNextAnchorId = _NextAnchorId
            Dim OriginalChunkSize = _ChunkSize
            Dim OriginalChunkSizeVariance = _ChunkSizeVariance
            Dim OriginalIndexPageEntryCount = _IndexPageEntryCount
            Dim OriginalIndexDirectoryEntryCount = _IndexDirectoryEntryCount

            Dim OriginalExtents =
                New List(Of ExtentIndexEntry)(_Extents)

            Dim OriginalPhysicalRecords =
                _PhysicalRecords.ToDictionary(
                    Function(pair) pair.Key,
                    Function(pair) pair.Value)

            '
            ' Rebuild recreates the complete extent layout. Capture anchored logical
            ' boundaries so the same immutable AnchorIds can be attached to the rebuilt
            ' extents beginning at those positions.
            '
            Dim AnchoredOffsets =
                _Extents.
                Where(Function(extent) extent.AnchorId > 0).
                Select(
                    Function(extent)
                        Return New AnchoredBoundary With {
                            .AnchorId = extent.AnchorId,
                            .RelativeOffset = extent.LogicalOffset
                        }
                    End Function).
                OrderBy(Function(item) item.RelativeOffset).
                ToList()

            Dim PublishedRebuild = False

            WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength)

            Try

                ClearFreeSpaceMap()

                Dim TargetChunkSize = Options.ChunkSize

                '
                ' A window of TargetChunkSize would reset the CDC rolling hash at every
                ' chunk-sized boundary, defeating content-defined splitting almost entirely -
                ' each window would only ever produce the one chunk it exactly fits. Read a
                ' much larger window instead so the hash scans continuously across many chunks
                ' at once, only resetting at (rare) window boundaries. Not needed - and not
                ' applied - when ChunkSizeVariance is 0, since fixed-size splitting already
                ' produces exactly one output chunk per TargetChunkSize window with no scan to
                ' interrupt.
                '
                Dim ReadWindowSize As Integer

                If Options.ChunkSizeVariance > 0 Then
                    Dim ScaledWindow = CLng(Options.MaxChunkSize) * RebuildContinuousScanChunkMultiple
                    ReadWindowSize = CInt(Math.Min(ScaledWindow, Integer.MaxValue))
                Else
                    ReadWindowSize = TargetChunkSize
                End If

                Dim NewExtents As New List(Of ExtentIndexEntry)()
                Dim NewPhysicalRecordIds As New HashSet(Of Long)()
                Dim LogicalOffset As Long = 0
                Dim ProcessedBytes As Long = 0
                Dim TotalBytes = Math.Max(1L, _Length)

                While LogicalOffset < _Length

                    If CancellationToken.Cancel Then

                        RestoreFailedRebuildState(
                            OriginalPhysicalLength,
                            OriginalIndexOffset,
                            OriginalHeaderFlags,
                            OriginalNextPhysicalRecordId,
                            OriginalNextAnchorId,
                            OriginalChunkSize,
                            OriginalChunkSizeVariance,
                            OriginalIndexPageEntryCount,
                            OriginalIndexDirectoryEntryCount,
                            OriginalExtents,
                            OriginalPhysicalRecords)

                        ClearRecoveryState()

                        Return

                    End If

                    Dim SegmentLength =
                        CInt(Math.Min(CLng(ReadWindowSize),
                                      _Length - LogicalOffset))

                    Dim Buffer(SegmentLength - 1) As Byte

                    Read(LogicalOffset,
                         Buffer,
                         0,
                         SegmentLength)

                    Dim SegmentExtents =
                        BuildExtentsFromBuffer(Buffer,
                                               0,
                                               SegmentLength)

                    For Each extent In SegmentExtents

                        Dim NewExtent = extent

                        NewExtent.LogicalOffset = LogicalOffset
                        NewExtent.AnchorId = 0

                        NewExtents.Add(NewExtent)

                        If NewExtent.PhysicalRecordId <> SparsePhysicalRecordId Then
                            NewPhysicalRecordIds.Add(NewExtent.PhysicalRecordId)
                        End If

                        LogicalOffset += NewExtent.LogicalLength

                    Next

                    ProcessedBytes += SegmentLength

                    Dim FirstPhaseUnits =
                        CLng((Math.Min(ProcessedBytes, TotalBytes) /
                              CDbl(TotalBytes)) *
                             (TotalBytes / 2.0R))

                    ReportProgress(
                        ProgressCallback,
                        FirstPhaseUnits,
                        TotalBytes,
                        ProcessUnitTypes.Bytes,
                        CancellationToken)

                End While

                '
                ' Recreate each anchored boundary in the rebuilt logical layout.
                '
                NewExtents =
                    ApplyAnchoredBoundariesToExtents(
                        NewExtents,
                        AnchoredOffsets,
                        _Length)

                RebaseExtentLogicalOffsets(NewExtents)

                Dim NewPhysicalRecords As New Dictionary(Of Long, PhysicalRecordEntry)()

                For Each RecordId In NewPhysicalRecordIds

                    Dim Record = GetPhysicalRecord(RecordId)

                    NewPhysicalRecords(RecordId) = Record

                Next

                _Extents.Clear()
                _Extents.AddRange(NewExtents)

                _PhysicalRecords.Clear()

                For Each pair In NewPhysicalRecords
                    _PhysicalRecords(pair.Key) = pair.Value
                Next

                _ChunkSize = TargetChunkSize
                _ChunkSizeVariance = Options.ChunkSizeVariance
                _IndexPageEntryCount = Options.IndexPageEntryCount
                _IndexDirectoryEntryCount = Options.IndexDirectoryEntryCount

                '
                ' Rebuild after applying the new metadata page size so physical-record
                ' page membership is calculated using the new page boundaries.
                '
                RebuildPhysicalRecordOrdinals()
                RebuildAnchorIndex()

                _ExtentPageDescriptors.Clear()
                _ExtentDirectoryPageDescriptors.Clear()
                _PhysicalRecordPageDescriptors.Clear()
                _PhysicalRecordDirectoryPageDescriptors.Clear()
                _HoleDirectoryPageDescriptors.Clear()

                InvalidateChunkCache()
                ClearFreeSpaceMap()
                DiscardPendingPhysicalRecordReclaims()

                MarkAllMetadataPagesDirty()

                ClearRecoveryAreaInMemory()

                PersistIndexAndHeader(GetDataEndFromIndex(),
                                      True)

                PublishedRebuild = True

                If CancellationToken.Cancel Then Return

                DefragmentSequence(
                    DefragmentSequenceProgressModes.RebuildFinalPhase,
                    ProgressCallback,
                    CancellationToken)

                If CancellationToken.Cancel Then Return

                ReportProgress(
                    ProgressCallback,
                    TotalBytes,
                    TotalBytes,
                    ProcessUnitTypes.Bytes,
                    CancellationToken)

            Catch

                If PublishedRebuild = False Then

                    RestoreFailedRebuildState(
                        OriginalPhysicalLength,
                        OriginalIndexOffset,
                        OriginalHeaderFlags,
                        OriginalNextPhysicalRecordId,
                        OriginalNextAnchorId,
                        OriginalChunkSize,
                        OriginalChunkSizeVariance,
                        OriginalIndexPageEntryCount,
                        OriginalIndexDirectoryEntryCount,
                        OriginalExtents,
                        OriginalPhysicalRecords)

                    ClearRecoveryState()

                End If

                Throw

            End Try

        End Sub

    End Class
End Namespace
