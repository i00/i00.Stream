Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        ''' <summary>
        ''' Physical storage optimisation strategy used by Defragment.
        ''' </summary>
        Public Enum DefragTypes

            ''' <summary>
            ''' Fast compaction mode. Moves later chunk records into earlier holes where they fit.
            ''' </summary>
            Move = 0

            ''' <summary>
            ''' Reorders live chunk records into logical chunk order.
            ''' </summary>
            Sequence = 1

            ''' <summary>
            ''' Fully rewrites all live chunk records using the current compression and sparse settings.
            ''' </summary>
            Rebuild = 2

        End Enum

        Private Structure DefragLiveEntry
            Public ChunkIndex As Integer
            Public Offset As Long
            Public RecordLength As Integer
        End Structure

        Private Structure DefragHole
            Public Offset As Long
            Public Length As Long
        End Structure

        ''' <summary>
        ''' Defragments the physical storage layout.
        ''' </summary>
        Public Function Defragment(Optional Type As DefragTypes = DefragTypes.Move,
                                   Optional ProgressCallback As StreamProgressCallback = Nothing) As Long

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim OriginalLength = _Fs.Length
                Dim CancellationToken As New CancellationToken()

                Select Case Type
                    Case DefragTypes.Move
                        DefragmentMove(ProgressCallback, CancellationToken)
                    Case DefragTypes.Sequence
                        DefragmentSequence(DefragmentSequenceProgressModes.Normal, ProgressCallback, CancellationToken)
                    Case DefragTypes.Rebuild
                        DefragmentRebuild(ProgressCallback, CancellationToken)
                    Case Else
                        Throw New ArgumentOutOfRangeException(NameOf(Type))
                End Select

                If CancellationToken.Cancel Then Return -1

                Return Math.Max(0L, OriginalLength - _Fs.Length)
            End SyncLock

        End Function

        Private Sub MoveChunkRecordJournaled(ChunkIndex As Integer, TargetOffset As Long)

            Dim Entry = _Index(ChunkIndex)

            If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Return
            If Entry.Offset = TargetOffset Then Return

            WriteJournal(JournalStates.Copying,
                               ChunkIndex,
                               Entry.Offset,
                               Entry.RecordLength,
                               TargetOffset,
                               Entry.RecordLength)

            CopyChunkRecord(Entry.Offset, Entry.RecordLength, TargetOffset)
            FlushDurable(_Fs)

            If Not IsValidChunkRecordAt(ChunkIndex, TargetOffset, Entry.RecordLength) Then
                Throw New CryptographicException("Copied chunk record failed validation.")
            End If

            WriteJournal(JournalStates.Copied,
                               ChunkIndex,
                               Entry.Offset,
                               Entry.RecordLength,
                               TargetOffset,
                               Entry.RecordLength)

            _Index(ChunkIndex) = New ChunkIndexEntry With {
                .Offset = TargetOffset,
                .RecordLength = Entry.RecordLength
            }

            PersistIndexAndHeader(GetDataEndFromIndex(), True)
            ClearJournal()

        End Sub

        Private Sub EnsureTargetRangeIsFree(TargetOffset As Long,
                                            RecordLength As Integer,
                                            ExceptChunkIndex As Integer)

            While True
                Dim OverlapChunkIndex = FindOverlappingLiveChunk(TargetOffset, RecordLength, ExceptChunkIndex)

                If OverlapChunkIndex < 0 Then Return

                Dim AppendOffset = Math.Max(_Fs.Length, GetDataEndFromIndex())
                MoveChunkRecordJournaled(OverlapChunkIndex, AppendOffset)
            End While

        End Sub

        Private Function FindOverlappingLiveChunk(TargetOffset As Long,
                                                  RecordLength As Integer,
                                                  ExceptChunkIndex As Integer) As Integer

            Dim TargetEnd = TargetOffset + RecordLength

            For ChunkIndex = 0 To _Index.Count - 1
                If ChunkIndex = ExceptChunkIndex Then Continue For

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                Dim EntryEnd = Entry.Offset + Entry.RecordLength

                If TargetOffset < EntryEnd AndAlso TargetEnd > Entry.Offset Then Return ChunkIndex
            Next

            Return -1

        End Function

        Private Function GetLiveEntriesSortedByOffset() As List(Of DefragLiveEntry)

            Dim Result As New List(Of DefragLiveEntry)()

            For ChunkIndex = 0 To _Index.Count - 1
                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For
                If Entry.Offset < DataStartOffset Then Throw New InvalidDataException($"Invalid chunk offset for chunk {ChunkIndex}.")
                If Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk record length for chunk {ChunkIndex}.")

                Result.Add(New DefragLiveEntry With {
                    .ChunkIndex = ChunkIndex,
                    .Offset = Entry.Offset,
                    .RecordLength = Entry.RecordLength
                })
            Next

            Result.Sort(Function(left, right) left.Offset.CompareTo(right.Offset))

            Return Result

        End Function

        Private Function GetDeadHoles(LiveEntries As List(Of DefragLiveEntry)) As List(Of DefragHole)

            Dim Holes As New List(Of DefragHole)()
            Dim Cursor = CLng(DataStartOffset)

            For Each Entry In LiveEntries
                If Entry.Offset > Cursor Then
                    Holes.Add(New DefragHole With {
                        .Offset = Cursor,
                        .Length = Entry.Offset - Cursor
                    })
                End If

                Cursor = Math.Max(Cursor, Entry.Offset + CLng(Entry.RecordLength))
            Next

            Return Holes

        End Function

        Private Function FindLatestLiveEntryThatFitsHole(LiveEntries As List(Of DefragLiveEntry),
                                                         Hole As DefragHole) As Integer

            For Index = LiveEntries.Count - 1 To 0 Step -1
                Dim Entry = LiveEntries(Index)

                If Entry.Offset <= Hole.Offset Then Exit For
                If Entry.RecordLength <= Hole.Length Then Return Index
            Next

            Return -1

        End Function

        Private Sub DefragmentMove(ProgressCallback As StreamProgressCallback,
                                   CancellationToken As CancellationToken)

            Const ProgressScale As Long = 1000000
            Dim OriginalFragmentation As Double? = Nothing

            ReportProgress(ProgressCallback, 0, ProgressScale, ProcessUnitTypes.Arbitrary, CancellationToken)

            Do
                If CancellationToken.Cancel Then Return

                Dim LiveEntries = GetLiveEntriesSortedByOffset()
                Dim Holes = GetDeadHoles(LiveEntries)

                If Holes.Count = 0 Then Exit Do

                Dim MovedSomething = False

                For Each hole In Holes
                    If CancellationToken.Cancel Then Return

                    Dim CandidateIndex = FindLatestLiveEntryThatFitsHole(LiveEntries, hole)

                    If CandidateIndex < 0 Then Continue For

                    Dim Candidate = LiveEntries(CandidateIndex)

                    MoveChunkRecordJournaled(Candidate.ChunkIndex, hole.Offset)
                    MovedSomething = True

                    Dim CurrentFragmentation = GetFragmentation()

                    If Not OriginalFragmentation.HasValue Then
                        OriginalFragmentation = Math.Max(CurrentFragmentation, 0.000001R)
                    End If

                    Dim CompletedUnits = CLng(((OriginalFragmentation.Value - CurrentFragmentation) / OriginalFragmentation.Value) * ProgressScale)

                    If CompletedUnits < 0 Then CompletedUnits = 0
                    If CompletedUnits > ProgressScale Then CompletedUnits = ProgressScale

                    ReportProgress(ProgressCallback, CompletedUnits, ProgressScale, ProcessUnitTypes.Arbitrary, CancellationToken)
                    Exit For
                Next

                If Not MovedSomething Then Exit Do
            Loop

            CommitDefragCheckpoint(GetDataEndFromIndex())
            ReportProgress(ProgressCallback, ProgressScale, ProgressScale, ProcessUnitTypes.Arbitrary, CancellationToken)

        End Sub

        Private Sub CopyChunkRecord(SourceOffset As Long,
                                    RecordLength As Integer,
                                    TargetOffset As Long)

            If SourceOffset < DataStartOffset Then Throw New InvalidDataException("Invalid source record offset.")
            If TargetOffset < DataStartOffset Then Throw New InvalidDataException("Invalid target record offset.")
            If RecordLength < MinChunkRecordSize Then Throw New InvalidDataException("Invalid record length.")

            Dim Record(RecordLength - 1) As Byte

            _Fs.Position = SourceOffset
            ReadExactly(_Fs, Record, 0, Record.Length)

            _Fs.Position = TargetOffset
            _Fs.Write(Record, 0, Record.Length)

        End Sub

        Private Sub CommitDefragCheckpoint(DataEnd As Long)

            If DataEnd < DataStartOffset Then Throw New InvalidDataException("Invalid defrag data end.")

            PersistIndexAndHeader(DataEnd, True)

        End Sub

        Private Enum DefragmentSequenceProgressModes
            Normal
            RebuildFinalPhase
        End Enum

        Private Sub DefragmentSequence(ProgressMode As DefragmentSequenceProgressModes,
                                       ProgressCallback As StreamProgressCallback,
                                       CancellationToken As CancellationToken)

            Dim ProgressStart As Double
            Dim ProgressEnd As Double

            Select Case ProgressMode
                Case DefragmentSequenceProgressModes.RebuildFinalPhase

                    ProgressStart = 0.5
                    ProgressEnd = 1

                Case DefragmentSequenceProgressModes.Normal

                    ProgressStart = 0
                    ProgressEnd = 1
            End Select

            Dim TotalBytes = _Index.Where(Function(entry) entry.Offset > 0 AndAlso entry.RecordLength > 0).
                                    Sum(Function(entry) CLng(entry.RecordLength))
            Dim ProcessedBytes As Long = 0
            Dim TargetOffset = CLng(DataStartOffset)

            For ChunkIndex = 0 To _Index.Count - 1
                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                EnsureTargetRangeIsFree(TargetOffset, Entry.RecordLength, ChunkIndex)

                Entry = _Index(ChunkIndex)

                If ProgressMode = DefragmentSequenceProgressModes.RebuildFinalPhase OrElse Entry.Offset <> TargetOffset Then
                    MoveChunkRecordJournaled(ChunkIndex, TargetOffset)
                End If

                ProcessedBytes += Entry.RecordLength

                Dim Ratio = If(TotalBytes = 0,
                               1.0R,
                               ProcessedBytes / CDbl(TotalBytes))

                Dim ScaledRatio = ProgressStart + ((ProgressEnd - ProgressStart) * Ratio)

                ReportProgress(
                    ProgressCallback,
                    CLng(ScaledRatio * TotalBytes),
                    TotalBytes,
                    ProcessUnitTypes.Bytes,
                    CancellationToken)

                TargetOffset += Entry.RecordLength

                If CancellationToken.Cancel Then Return
            Next

            CommitDefragCheckpoint(GetDataEndFromIndex())

        End Sub

        Private Sub DefragmentRebuild(ProgressCallback As StreamProgressCallback,
                                      CancellationToken As CancellationToken)

            Dim TotalBytes = _Index.Where(Function(entry) entry.Offset > 0 AndAlso entry.RecordLength > 0).
                                    Sum(Function(entry) CLng(entry.RecordLength))
            Dim ProcessedBytes As Long = 0

            For ChunkIndex = 0 To _Index.Count - 1
                If CancellationToken.Cancel Then Return

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                LoadChunk(ChunkIndex, _ChunkPlain)

                Dim ChunkStart = CLng(ChunkIndex) * ChunkSize
                Dim PlainLength = CInt(Math.Min(CLng(ChunkSize), Math.Max(0L, _Length - ChunkStart)))

                WriteChunkRecord(ChunkIndex, _ChunkPlain, PlainLength)
                PersistIndexAndHeader(_IndexOffset)

                ProcessedBytes += Entry.RecordLength
                Dim ReportedProcessedBytes = ProcessedBytes \ 2 '< \ 2 here so that we do 0-50% ... then DefragmentSequence does 50% - 100% due to the DefragmentSequenceProgressModes.RebuildFinalPhase
                ReportProgress(ProgressCallback, ReportedProcessedBytes, TotalBytes, ProcessUnitTypes.Bytes, CancellationToken)
            Next

            If CancellationToken.Cancel Then Return

            DefragmentSequence(DefragmentSequenceProgressModes.RebuildFinalPhase, ProgressCallback, CancellationToken)

        End Sub

    End Class

End Namespace
