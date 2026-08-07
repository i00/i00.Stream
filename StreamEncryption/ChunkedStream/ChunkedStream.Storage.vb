Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        Private Sub LoadChunk(ChunkIndex As Long, Plain As Byte())

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If ChunkIndex >= _Index.Count Then Return

            Dim Entry = _Index(CInt(ChunkIndex))

            If Entry.Offset = 0 OrElse Entry.RecordLength = 0 Then Return
            If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk index entry for chunk {ChunkIndex}.")
            If Entry.Offset + Entry.RecordLength > _IndexOffset Then Throw New InvalidDataException($"Chunk {ChunkIndex} record extends beyond data area.")

            Dim Record(Entry.RecordLength - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Record, 0, Record.Length)

            DecryptChunkRecord(ChunkIndex, Record, Plain)

        End Sub

        Private Sub WriteChunkRecord(ChunkIndex As Long, Plain As Byte(), PlainLength As Integer)

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If PlainLength < 0 OrElse PlainLength > ChunkSize Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))

            Dim PlaintextAllZero = PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)

            If PlainLength = 0 OrElse (Not Options.StoreSparseChunks AndAlso PlaintextAllZero) Then

                EnsureIndexSize(CInt(ChunkIndex + 1))
                _Index(CInt(ChunkIndex)) = New ChunkIndexEntry()
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks

                Return

            End If

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim CompressionMethod = ChunkedStreamOptions.CompressionMethods.None

            If Options.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None Then

                Dim Compressed = CompressPayload(Options.CompressionMethod, Plain, PlainLength)

                If ShouldUseCompressed(PlainLength, Compressed.Length) Then
                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    CompressionMethod = Options.CompressionMethod
                    MarkCompressionFlag(CompressionMethod)
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
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(CompressionMethod)), 0, Record, ChunkCompressionMethodOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(EncryptionMethod)), 0, Record, ChunkEncryptionMethodOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, Record, ChunkPlainLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, Record, ChunkPayloadLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(Flags)), 0, Record, ChunkFlagsOffset, 4)

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

            Dim NewRecordOffset = GetNextChunkRecordWriteOffset()

            _Fs.Position = NewRecordOffset
            _Fs.Write(Record, 0, Record.Length)

            EnsureIndexSize(CInt(ChunkIndex + 1))

            _Index(CInt(ChunkIndex)) =
                New ChunkIndexEntry With {
                    .Offset = NewRecordOffset,
                    .RecordLength = Record.Length
                }

            _IndexOffset = NewRecordOffset + Record.Length

        End Sub

        Private Function GetNextChunkRecordWriteOffset() As Long

            If Not HasOpenCheckpoint Then
                Return _IndexOffset
            End If

            ' While a checkpoint is active, never overwrite the currently committed
            ' index table. Append tentative records beyond the physical end so a crash
            ' before Commit leaves the previous header/index pair valid and recoverable.
            Return Math.Max(Math.Max(_Fs.Length, GetDataEndFromIndex()), _IndexOffset)

        End Function

        Private Function GetDataEndFromIndex() As Long

            Dim DataEnd = CLng(DataStartOffset)

            For Each Entry In _Index

                If Entry.Offset > 0 AndAlso Entry.RecordLength > 0 Then
                    DataEnd = Math.Max(DataEnd, Entry.Offset + CLng(Entry.RecordLength))
                End If

            Next

            Return DataEnd

        End Function

        Private Sub EnsureIndexSize(RequiredCount As Integer)

            If RequiredCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(RequiredCount))

            While _Index.Count < RequiredCount
                _Index.Add(New ChunkIndexEntry())
            End While

        End Sub

        Private Shared Function GetRequiredChunkCount(Length As Long) As Integer

            If Length <= 0 Then Return 0

            Dim Count = ((Length - 1) \ ChunkSize) + 1

            If Count > Integer.MaxValue Then Throw New InvalidDataException("Chunked stream has too many chunks for this implementation.")

            Return CInt(Count)

        End Function

    End Class

End Namespace