Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text

Namespace Streams

    Partial Class ChunkedStream

        Private Enum JournalStates As Integer
            None = 0
            Copying = 1
            Copied = 2
        End Enum

        Private Sub RecoverJournal()

            Dim State = CType(BitConverter.ToInt32(_Header, JournalStateOffset), JournalStates)

            If State = JournalStates.None Then Return

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
                Case JournalStates.Copying
                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = OldOffset, .RecordLength = OldLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearJournal()
                        Return
                    End If

                    Throw New CryptographicException("Recovery failed. Old chunk record is invalid.")

                Case JournalStates.Copied
                    If IsValidChunkRecordAt(CInt(ChunkIndex), NewOffset, NewLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = NewOffset, .RecordLength = NewLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearJournal()
                        Return
                    End If

                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = OldOffset, .RecordLength = OldLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearJournal()
                        Return
                    End If

                    Throw New CryptographicException("Recovery failed. Neither old nor new chunk record is valid.")

                Case Else
                    Throw New InvalidDataException($"Unknown recovery journal state: {CInt(State)}.")
            End Select

        End Sub

        Private Sub WriteJournal(State As JournalStates,
                                 ChunkIndex As Long,
                                 OldOffset As Long,
                                 OldLength As Integer,
                                 NewOffset As Long,
                                 NewLength As Integer)

            Array.Clear(_Header, JournalAreaOffset, JournalAreaLength)

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(State)), 0, _Header, JournalStateOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkIndex), 0, _Header, JournalChunkIndexOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(OldOffset), 0, _Header, JournalOldOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(OldLength), 0, _Header, JournalOldLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(NewOffset), 0, _Header, JournalNewOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(NewLength), 0, _Header, JournalNewLengthOffset, 4)

            WriteHeaderCopies(True)

        End Sub

        Private Sub ClearJournal()

            Array.Clear(_Header, JournalAreaOffset, JournalAreaLength)
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
            Catch ex As IOException
                Return False
            Catch ex As InvalidDataException
                Return False
            Catch ex As CryptographicException
                Return False
            Catch ex As EncryptionMismatchException
                Return False
            End Try

        End Function

    End Class

End Namespace
