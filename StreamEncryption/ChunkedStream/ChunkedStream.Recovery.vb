' ================================================================================
' ChunkedStream Recovery
' ================================================================================
'
' Purpose
'   - Crash recovery and recovery-state management.
'
' Protected Operations
'   - Checkpoint recovery.
'   - Chunk move recovery.
'   - Chunk-size rebuild recovery.
'
' Design
'   - Recovery state is stored in the header recovery area.
'   - Recovery executes automatically during Open().
'   - Recovery state is durable before protected operations begin.
'
' Notes
'   - Chunk-size rebuild recovery rolls back incomplete rebuilds.
'   - Chunk move recovery validates old and new records before recovery completes.
'
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        Public Enum RecoveryStates As Integer

            None = 0

            CopyingPhysicalRecord = 1
            PhysicalRecordCopied = 2

            CheckpointActive = 100

            ChunkSizeRebuildActive = 200

        End Enum

        ' Compatibility enum for the existing Defrag partial.
        ' Defrag can continue calling WriteJournal(JournalStates.Copying, ...)
        ' while the recovery implementation uses RecoveryStates internally.
        Private Enum JournalStates As Integer

            None = RecoveryStates.None
            Copying = RecoveryStates.CopyingPhysicalRecord
            Copied = RecoveryStates.PhysicalRecordCopied

        End Enum

        Private Sub RecoverState()

            Dim State = GetRecoveryState()

            Select Case State

                Case RecoveryStates.None

                    Return

                Case RecoveryStates.CheckpointActive

                    RecoverCheckpoint()

                Case RecoveryStates.ChunkSizeRebuildActive

                    RecoverChunkSizeRebuild()

                Case RecoveryStates.CopyingPhysicalRecord, RecoveryStates.PhysicalRecordCopied

                    RecoverPhysicalRecordMove(State)

                Case Else

                    Throw New InvalidDataException($"Unknown recovery state: {CInt(State)}.")

            End Select

        End Sub

        <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
        Friend Sub WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength As Long)

            Dim CurrentState = GetRecoveryState()

            If CurrentState <> RecoveryStates.None Then
                Throw New InvalidOperationException("Another recovery operation is active.")
            End If

            If OriginalPhysicalLength < DataStartOffset Then
                Throw New InvalidDataException("Invalid chunk-size rebuild recovery length.")
            End If

            Array.Clear(_Header, RecoveryAreaOffset, RecoveryAreaLength)

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(RecoveryStates.ChunkSizeRebuildActive)), 0, _Header, RecoveryStateOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(OriginalPhysicalLength), 0, _Header, JournalChunkIndexOffset, 8)

            WriteHeaderCopies(True)

        End Sub

        Private Sub RecoverChunkSizeRebuild()

            Dim OriginalPhysicalLength = BitConverter.ToInt64(_Header, JournalChunkIndexOffset)

            If OriginalPhysicalLength < DataStartOffset Then
                Throw New InvalidDataException("Invalid chunk-size rebuild recovery length.")
            End If

            If OriginalPhysicalLength > _Fs.Length Then
                Throw New InvalidDataException("Chunk-size rebuild recovery length is beyond end of stream.")
            End If

            If _Fs.Length > OriginalPhysicalLength Then
                _Fs.SetLength(OriginalPhysicalLength)
            End If

            ClearRecoveryState()

        End Sub

        Public Function GetRecoveryState() As RecoveryStates

            Return CType(BitConverter.ToInt32(_Header, RecoveryStateOffset), RecoveryStates)

        End Function

        Private Sub RecoverCheckpoint()

            Dim PhysicalLength =
                BitConverter.ToInt64(
                    _Header,
                    RecoveryCheckpointPhysicalLengthOffset)

            Dim IndexOffset =
                BitConverter.ToInt64(
                    _Header,
                    RecoveryCheckpointIndexOffsetOffset)

            Dim LogicalLength =
                BitConverter.ToInt64(
                    _Header,
                    RecoveryCheckpointLogicalLengthOffset)

            If PhysicalLength < DataStartOffset Then
                Throw New InvalidDataException("Invalid checkpoint recovery physical length.")
            End If

            If PhysicalLength > _Fs.Length Then
                Throw New InvalidDataException("Checkpoint recovery physical length is beyond end of stream.")
            End If

            If IndexOffset < DataStartOffset Then
                Throw New InvalidDataException("Invalid checkpoint recovery index offset.")
            End If

            If IndexOffset > PhysicalLength Then
                Throw New InvalidDataException("Checkpoint recovery index offset is beyond the checkpoint physical length.")
            End If

            If LogicalLength < 0 Then
                Throw New InvalidDataException("Invalid checkpoint recovery logical length.")
            End If

            _Length = LogicalLength
            _IndexOffset = IndexOffset

            If _Fs.Length > PhysicalLength Then
                _Fs.SetLength(PhysicalLength)
            End If

            ClearRecoveryState()

        End Sub

        Private Sub RecoverPhysicalRecordMove(State As RecoveryStates)

            Dim RecordId =
                BitConverter.ToInt64(
                    _Header,
                    JournalChunkIndexOffset)

            Dim OldOffset =
                BitConverter.ToInt64(
                    _Header,
                    JournalOldOffsetOffset)

            Dim OldLength =
                BitConverter.ToInt32(
                    _Header,
                    JournalOldLengthOffset)

            Dim NewOffset =
                BitConverter.ToInt64(
                    _Header,
                    JournalNewOffsetOffset)

            Dim NewLength =
                BitConverter.ToInt32(
                    _Header,
                    JournalNewLengthOffset)

            If RecordId <= SparsePhysicalRecordId Then
                Throw New InvalidDataException("Invalid recovery journal record id.")
            End If

            Dim Record As PhysicalRecordEntry = Nothing

            If _PhysicalRecords.TryGetValue(RecordId, Record) = False Then
                Throw New InvalidDataException($"Recovery journal refers to unknown physical record {RecordId}.")
            End If

            Select Case State

                Case RecoveryStates.CopyingPhysicalRecord

                    If IsValidPhysicalRecordAt(RecordId,
                                               OldOffset,
                                               OldLength) Then

                        Record.PhysicalOffset = OldOffset
                        Record.PhysicalLength = OldLength

                        _PhysicalRecords(RecordId) = Record

                        PersistIndexAndHeader(GetDataEndFromIndex())

                        ClearRecoveryState()

                        Return

                    End If

                    Throw New CryptographicException("Recovery failed. Original physical record is invalid.")

                Case RecoveryStates.PhysicalRecordCopied

                    If IsValidPhysicalRecordAt(RecordId,
                                               NewOffset,
                                               NewLength) Then

                        Record.PhysicalOffset = NewOffset
                        Record.PhysicalLength = NewLength

                        _PhysicalRecords(RecordId) = Record

                        PersistIndexAndHeader(GetDataEndFromIndex())

                        ClearRecoveryState()

                        Return

                    End If

                    If IsValidPhysicalRecordAt(RecordId,
                                               OldOffset,
                                               OldLength) Then

                        Record.PhysicalOffset = OldOffset
                        Record.PhysicalLength = OldLength

                        _PhysicalRecords(RecordId) = Record

                        PersistIndexAndHeader(GetDataEndFromIndex())

                        ClearRecoveryState()

                        Return

                    End If

                    Throw New CryptographicException("Recovery failed. Neither version of the physical record is valid.")

                Case Else

                    Throw New InvalidDataException($"Unsupported recovery state: {CInt(State)}.")

            End Select

        End Sub

        Private Sub WriteCheckpointRecoveryState()

            Dim CurrentState = GetRecoveryState()

            If CurrentState <> RecoveryStates.None AndAlso
               CurrentState <> RecoveryStates.CheckpointActive Then

                Throw New InvalidOperationException(
                    "Another recovery operation is active.")

            End If

            Array.Clear(_Header, RecoveryAreaOffset, RecoveryAreaLength)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(CInt(RecoveryStates.CheckpointActive)),
                0,
                _Header,
                RecoveryStateOffset,
                4)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(_Fs.Length),
                0,
                _Header,
                RecoveryCheckpointPhysicalLengthOffset,
                8)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(_IndexOffset),
                0,
                _Header,
                RecoveryCheckpointIndexOffsetOffset,
                8)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(_Length),
                0,
                _Header,
                RecoveryCheckpointLogicalLengthOffset,
                8)

            WriteHeaderCopies(True)

        End Sub

        Private Sub WriteJournal(State As JournalStates,
                                 RecordId As Long,
                                 OldOffset As Long,
                                 OldLength As Integer,
                                 NewOffset As Long,
                                 NewLength As Integer)

            WritePhysicalRecordMoveRecoveryState(
                CType(State, RecoveryStates),
                RecordId,
                OldOffset,
                OldLength,
                NewOffset,
                NewLength)

        End Sub

        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub WritePhysicalRecordMoveRecoveryState(State As RecoveryStates,
                                                        RecordId As Long,
                                                        OldOffset As Long,
                                                        OldLength As Integer,
                                                        NewOffset As Long,
                                                        NewLength As Integer)

            Select Case State

                Case RecoveryStates.CopyingPhysicalRecord,
                     RecoveryStates.PhysicalRecordCopied

                Case Else

                    Throw New ArgumentOutOfRangeException(
                        NameOf(State),
                        $"Unsupported physical-record recovery state: {CInt(State)}.")

            End Select

            Array.Clear(_Header,
                        RecoveryAreaOffset,
                        RecoveryAreaLength)

            Buffer.BlockCopy(
                BitConverter.GetBytes(CInt(State)),
                0,
                _Header,
                RecoveryStateOffset,
                4)

            Buffer.BlockCopy(
                BitConverter.GetBytes(RecordId),
                0,
                _Header,
                JournalChunkIndexOffset,
                8)

            Buffer.BlockCopy(
                BitConverter.GetBytes(OldOffset),
                0,
                _Header,
                JournalOldOffsetOffset,
                8)

            Buffer.BlockCopy(
                BitConverter.GetBytes(OldLength),
                0,
                _Header,
                JournalOldLengthOffset,
                4)

            Buffer.BlockCopy(
                BitConverter.GetBytes(NewOffset),
                0,
                _Header,
                JournalNewOffsetOffset,
                8)

            Buffer.BlockCopy(
                BitConverter.GetBytes(NewLength),
                0,
                _Header,
                JournalNewLengthOffset,
                4)

            WriteHeaderCopies(True)

        End Sub

        Private Sub ClearJournal()

            ClearRecoveryState()

        End Sub

        Private Sub ClearRecoveryState()

            Array.Clear(_Header, RecoveryAreaOffset, RecoveryAreaLength)
            WriteHeaderCopies(True)

        End Sub

        Private Function IsValidPhysicalRecordAt(RecordId As Long,
                                                 Offset As Long,
                                                 RecordLength As Integer) As Boolean

            If RecordId <= SparsePhysicalRecordId Then Return False

            If Offset < DataStartOffset Then Return False

            If RecordLength < MinChunkRecordSize Then Return False

            If Offset + RecordLength > _Fs.Length Then Return False

            Try

                Dim Record(RecordLength - 1) As Byte

                _Fs.Position = Offset

                ReadExactly(_Fs,
                            Record,
                            0,
                            Record.Length)

                Dim HeaderRecordId =
                    BitConverter.ToInt64(Record, 0)

                If HeaderRecordId <> RecordId Then
                    Return False
                End If

                Dim PhysicalRecord =
                    New PhysicalRecordEntry With {
                        .RecordId = RecordId,
                        .PhysicalOffset = Offset,
                        .PhysicalLength = RecordLength,
                        .PlainLength = BitConverter.ToInt32(Record, ChunkPlainLengthOffset)
                    }

                ReadPhysicalRecordPlain(PhysicalRecord)

                Return True

            Catch Ex As IOException

                Return False

            Catch Ex As InvalidDataException

                Return False

            Catch Ex As CryptographicException

                Return False

            Catch Ex As EncryptionMismatchException

                Return False

            End Try

        End Function

    End Class

End Namespace