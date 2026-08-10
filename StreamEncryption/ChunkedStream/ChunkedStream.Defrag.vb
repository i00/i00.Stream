' ================================================================================
' ChunkedStream Defragmentation
' ================================================================================
'
' Purpose
'   - Physical storage optimisation and chunk-layout maintenance.
'
' Supported Modes
'   - Move
'       Moves live chunk records into suitable gaps.
'
'   - Sequence
'       Reorders live chunk records into logical order.
'
'   - Rebuild
'       Rewrites all chunks using the current options.
'
' Design
'   - Defragmentation operates directly on live chunk records.
'   - Chunk moves are journalled for crash recovery.
'   - Rebuild uses checkpoint-style recovery and publishes changes atomically.
'
' Notes
'   - Defragmentation is not permitted while a checkpoint is active.
'
' ================================================================================

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
        ''' <remarks>
        ''' Defragmentation cannot be performed while a checkpoint is active.
        ''' </remarks>
        Public Function Defragment(Optional Type As DefragTypes = DefragTypes.Move,
                                   Optional ProgressCallback As StreamProgressCallback = Nothing) As Long

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If HasActiveCheckpoint Then
                    Throw New InvalidOperationException(
                        "Defragmentation cannot be performed while a checkpoint is active.")
                End If

                InvalidateChunkCache()

                Dim OriginalLength = _Fs.Length

                Dim CancellationToken As New CancellationToken()

                Select Case Type

                    Case DefragTypes.Move
                        DefragmentMove(ProgressCallback, CancellationToken)

                    Case DefragTypes.Sequence
                        DefragmentSequence(
                            DefragmentSequenceProgressModes.Normal,
                            ProgressCallback,
                            CancellationToken)

                    Case DefragTypes.Rebuild
                        DefragmentRebuild(
                            ProgressCallback,
                            CancellationToken)

                    Case Else
                        Throw New ArgumentOutOfRangeException(NameOf(Type))

                End Select

                If CancellationToken.Cancel Then
                    Return -1
                End If

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

            If Options.ChunkSize <= 0 Then
                Throw New InvalidOperationException("Chunk size must be greater than zero.")
            End If

            Dim OriginalPhysicalLength = _Fs.Length
            Dim OriginalChunkSize = _ChunkSize
            Dim OriginalChunkPlain = _ChunkPlain

            WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength)

            Dim TargetChunkSize = Options.ChunkSize

            Dim TargetChunkCount =
                If(_Length <= 0,
                   0,
                   CInt(((_Length - 1) \ TargetChunkSize) + 1))

            Dim NewIndex As New List(Of ChunkIndexEntry)(TargetChunkCount)

            Dim TargetBuffer(TargetChunkSize - 1) As Byte

            Dim PhysicalOffset =
                Math.Max(_Fs.Length,
                         GetDataEndFromIndex())

            Dim ProcessedBytes As Long = 0
            Dim TotalBytes = Math.Max(1L, _Length)

            For TargetChunkIndex = 0 To TargetChunkCount - 1

                If CancellationToken.Cancel Then

                    _ChunkSize = OriginalChunkSize
                    _ChunkPlain = OriginalChunkPlain

                    Return

                End If

                Array.Clear(TargetBuffer, 0, TargetBuffer.Length)

                Dim LogicalOffset = CLng(TargetChunkIndex) * TargetChunkSize

                Dim PlainLength =
                    CInt(Math.Min(CLng(TargetChunkSize),
                                  _Length - LogicalOffset))

                If PlainLength > 0 Then

                    Dim ReadBuffer(PlainLength - 1) As Byte

                    Read(LogicalOffset, ReadBuffer)

                    Buffer.BlockCopy(ReadBuffer,
                                     0,
                                     TargetBuffer,
                                     0,
                                     PlainLength)

                End If

                Dim Entry =
                    WriteChunkRecordForRebuild(TargetChunkIndex,
                                               TargetBuffer,
                                               PlainLength,
                                               PhysicalOffset)

                NewIndex.Add(Entry)

                If Entry.Offset > 0 AndAlso Entry.RecordLength > 0 Then
                    PhysicalOffset = Entry.Offset + Entry.RecordLength
                End If

                ProcessedBytes += PlainLength

                ReportProgress(ProgressCallback,
                               Math.Min(ProcessedBytes, TotalBytes),
                               TotalBytes,
                               ProcessUnitTypes.Bytes,
                               CancellationToken)

            Next

            If CancellationToken.Cancel Then

                _ChunkSize = OriginalChunkSize
                _ChunkPlain = OriginalChunkPlain

                Return

            End If

            _Index.Clear()
            _Index.AddRange(NewIndex)

            _ChunkSize = TargetChunkSize

            _ChunkPlain = New Byte(_ChunkSize - 1) {}
            _CachedChunkPlain = New Byte(_ChunkSize - 1) {}

            InvalidateChunkCache()

            ClearRecoveryAreaInMemory()

            PersistIndexAndHeader(PhysicalOffset, True)

            ReportProgress(ProgressCallback,
                           TotalBytes,
                           TotalBytes,
                           ProcessUnitTypes.Bytes,
                           CancellationToken)

        End Sub

        Private Function WriteChunkRecordForRebuild(ChunkIndex As Long,
                                                    Plain As Byte(),
                                                    PlainLength As Integer,
                                                    PhysicalOffset As Long) As ChunkIndexEntry

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If PlainLength < 0 OrElse PlainLength > Options.ChunkSize Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))

            Dim CompressionRatioThreshold = Options.CompressionRatioThreshold

            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            Dim PlaintextAllZero = PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)

            If PlainLength = 0 OrElse (Not Options.StoreSparseChunks AndAlso PlaintextAllZero) Then
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks
                Return New ChunkIndexEntry()
            End If

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim StoredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedPercent As Byte = 100

            If Options.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None AndAlso PlainLength > 0 Then

                Dim Compressed = CompressPayload(Options.CompressionMethod, Plain, PlainLength)

                CompressionEvaluatedMethod = Options.CompressionMethod
                CompressionEvaluatedPercent = GetCompressionEvaluatedPercent(PlainLength, Compressed.Length)

                If CompressionEvaluatedPercent / 100.0R <= CompressionRatioThreshold Then
                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    StoredCompressionMethod = Options.CompressionMethod
                    MarkCompressionFlag(StoredCompressionMethod)
                End If

            End If

            Dim EncryptionMethod =
                        If(_CurrentWriteEncryptionEnabled,
                           ChunkEncryptionMethods.AesCtrFileMasterKey,
                           ChunkEncryptionMethods.None)

            Dim Flags = ChunkFlags.None

            If PlaintextAllZero Then
                Flags = Flags Or ChunkFlags.PlaintextAllZero
            End If

            Dim RecordLength = ChunkRecordDataOffset + PayloadLength + MacSize
            Dim Record(RecordLength - 1) As Byte

            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkIndex), 0, Record, 0, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(StoredCompressionMethod)), 0, Record, ChunkCompressionMethodOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(EncryptionMethod)), 0, Record, ChunkEncryptionMethodOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, Record, ChunkPlainLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, Record, ChunkPayloadLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(Flags)), 0, Record, ChunkFlagsOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(CompressionEvaluatedMethod)), 0, Record, ChunkCompressionEvaluatedMethodOffset, 4)
            Record(ChunkCompressionEvaluatedPercentOffset) = CompressionEvaluatedPercent

            _Rng.GetBytes(_Counter)
            System.Buffer.BlockCopy(_Counter, 0, Record, ChunkRecordIvOffset, IvSize)

            Select Case EncryptionMethod

                Case ChunkEncryptionMethods.None

                    If PayloadLength > 0 Then
                        System.Buffer.BlockCopy(Payload, 0, Record, ChunkRecordDataOffset, PayloadLength)
                    End If

                Case ChunkEncryptionMethods.AesCtrFileMasterKey

                    If _ChunkEncryptionKey Is Nothing Then
                        Throw New EncryptionMismatchException("Encryption is enabled but no file master key is available.")
                    End If

                    CryptPayload(Payload, 0, PayloadLength, Record, ChunkRecordDataOffset, _ChunkEncryptionKey)

                Case Else

                    Throw New InvalidDataException($"Unsupported chunk encryption method: {CInt(EncryptionMethod)}.")

            End Select

            Dim RecordMacKey =
                        If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                           _ChunkMacKey,
                           PublicIntegrityKey)

            Using Hmac As New HMACSHA256(RecordMacKey)
                Dim Mac = Hmac.ComputeHash(Record, 0, ChunkRecordDataOffset + PayloadLength)
                System.Buffer.BlockCopy(Mac, 0, Record, ChunkRecordDataOffset + PayloadLength, MacSize)
            End Using

            _Fs.Position = PhysicalOffset
            _Fs.Write(Record, 0, Record.Length)

            Return New ChunkIndexEntry With {
                        .Offset = PhysicalOffset,
                        .RecordLength = Record.Length
                    }

        End Function

    End Class

End Namespace
