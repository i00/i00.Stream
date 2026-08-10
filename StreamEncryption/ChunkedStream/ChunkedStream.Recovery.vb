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

        Private Enum RecoveryStates As Integer

            None = 0

            CopyingChunk = 1
            ChunkCopied = 2

            CheckpointActive = 100

            ChunkSizeRebuildActive = 200

        End Enum

        ' Compatibility enum for the existing Defrag partial.
        ' Defrag can continue calling WriteJournal(JournalStates.Copying, ...)
        ' while the recovery implementation uses RecoveryStates internally.
        Private Enum JournalStates As Integer

            None = RecoveryStates.None
            Copying = RecoveryStates.CopyingChunk
            Copied = RecoveryStates.ChunkCopied

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

                Case RecoveryStates.CopyingChunk,
                     RecoveryStates.ChunkCopied

                    RecoverChunkMove(State)

                Case Else

                    Throw New InvalidDataException($"Unknown recovery state: {CInt(State)}.")

            End Select

        End Sub

        Private Sub WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength As Long)

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

        Private Function GetRecoveryState() As RecoveryStates

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

        Private Sub RecoverChunkMove(State As RecoveryStates)

            Dim ChunkIndex = BitConverter.ToInt64(_Header, JournalChunkIndexOffset)
            Dim OldOffset = BitConverter.ToInt64(_Header, JournalOldOffsetOffset)
            Dim OldLength = BitConverter.ToInt32(_Header, JournalOldLengthOffset)
            Dim NewOffset = BitConverter.ToInt64(_Header, JournalNewOffsetOffset)
            Dim NewLength = BitConverter.ToInt32(_Header, JournalNewLengthOffset)

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then
                Throw New InvalidDataException("Invalid recovery journal chunk index.")
            End If

            EnsureIndexSize(CInt(ChunkIndex + 1))

            Select Case State

                Case RecoveryStates.CopyingChunk

                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then

                        _Index(CInt(ChunkIndex)) =
                            New ChunkIndexEntry With {
                                .Offset = OldOffset,
                                .RecordLength = OldLength
                            }

                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearRecoveryState()

                        Return

                    End If

                    Throw New CryptographicException("Recovery failed. Old chunk record is invalid.")

                Case RecoveryStates.ChunkCopied

                    If IsValidChunkRecordAt(CInt(ChunkIndex), NewOffset, NewLength) Then

                        _Index(CInt(ChunkIndex)) =
                            New ChunkIndexEntry With {
                                .Offset = NewOffset,
                                .RecordLength = NewLength
                            }

                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearRecoveryState()

                        Return

                    End If

                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then

                        _Index(CInt(ChunkIndex)) =
                            New ChunkIndexEntry With {
                                .Offset = OldOffset,
                                .RecordLength = OldLength
                            }

                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearRecoveryState()

                        Return

                    End If

                    Throw New CryptographicException("Recovery failed. Neither old nor new chunk record is valid.")

                Case Else

                    Throw New InvalidDataException($"Unsupported chunk move recovery state: {CInt(State)}.")

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
                                 ChunkIndex As Long,
                                 OldOffset As Long,
                                 OldLength As Integer,
                                 NewOffset As Long,
                                 NewLength As Integer)

            WriteChunkMoveRecoveryState(
                CType(State, RecoveryStates),
                ChunkIndex,
                OldOffset,
                OldLength,
                NewOffset,
                NewLength)

        End Sub

        Private Sub WriteChunkMoveRecoveryState(State As RecoveryStates,
                                                ChunkIndex As Long,
                                                OldOffset As Long,
                                                OldLength As Integer,
                                                NewOffset As Long,
                                                NewLength As Integer)

            Select Case State

                Case RecoveryStates.CopyingChunk,
                     RecoveryStates.ChunkCopied

                    ' Valid chunk move recovery state.

                Case Else

                    Throw New ArgumentOutOfRangeException(NameOf(State), $"Unsupported chunk move recovery state: {CInt(State)}.")

            End Select

            Array.Clear(_Header, RecoveryAreaOffset, RecoveryAreaLength)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(CInt(State)),
                0,
                _Header,
                RecoveryStateOffset,
                4)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(ChunkIndex),
                0,
                _Header,
                JournalChunkIndexOffset,
                8)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(OldOffset),
                0,
                _Header,
                JournalOldOffsetOffset,
                8)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(OldLength),
                0,
                _Header,
                JournalOldLengthOffset,
                4)

            System.Buffer.BlockCopy(
                BitConverter.GetBytes(NewOffset),
                0,
                _Header,
                JournalNewOffsetOffset,
                8)

            System.Buffer.BlockCopy(
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

        Private Function IsValidChunkRecordAt(ChunkIndex As Integer,
                                              Offset As Long,
                                              RecordLength As Integer) As Boolean

            If Offset < DataStartOffset OrElse RecordLength < MinChunkRecordSize Then Return False
            If Offset + RecordLength > _Fs.Length Then Return False

            Try

                Dim Record(RecordLength - 1) As Byte

                _Fs.Position = Offset
                ReadExactly(_Fs, Record, 0, Record.Length)

                Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)

                DecryptChunkRecord(ChunkIndex, Record, _ChunkPlain)

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