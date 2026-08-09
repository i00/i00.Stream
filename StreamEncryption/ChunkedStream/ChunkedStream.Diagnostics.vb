Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        ''' <summary>
        ''' Returns fragmentation as a value between 0 and 1.
        ''' </summary>
        Public Function GetFragmentation() As Double

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim UsedBytes As Long = 0

                For Each entry In _Index
                    If entry.Offset <> 0 AndAlso entry.RecordLength > 0 Then
                        UsedBytes += entry.RecordLength
                    End If
                Next

                Dim DataEnd = GetDataEndFromIndex()
                Dim TotalStoredChunkBytes = Math.Max(0L, DataEnd - DataStartOffset)
                Dim WastedBytes = Math.Max(0L, TotalStoredChunkBytes - UsedBytes)

                If TotalStoredChunkBytes = 0 Then Return 0

                Return WastedBytes / CDbl(TotalStoredChunkBytes)
            End SyncLock

        End Function

        ''' <summary>
        ''' Validates all live chunk records.
        ''' </summary>
        Public Sub Validate(Optional ProgressCallback As StreamProgressCallback = Nothing)

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim CancellationToken As New CancellationToken()

                ValidateAllLiveChunkRecords(ProgressCallback, CancellationToken)
            End SyncLock

        End Sub


        Private Sub ValidateAllLiveChunkRecords(ProgressCallback As StreamProgressCallback,
                                                CancellationToken As CancellationToken)

            Dim TotalChunks = _Index.Count
            Dim ProcessedChunks As Long = 0

            For ChunkIndex = 0 To _Index.Count - 1
                If CancellationToken.Cancel Then Return

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                ValidateChunkRecord(ChunkIndex)

                ProcessedChunks += 1
                ReportProgress(ProgressCallback, ProcessedChunks, Math.Max(1, TotalChunks), ProcessUnitTypes.Chunks, CancellationToken)
            Next

        End Sub

        Private Sub ValidateChunkRecord(ExpectedChunkIndex As Integer)

            If ExpectedChunkIndex < 0 OrElse ExpectedChunkIndex >= _Index.Count Then Throw New ArgumentOutOfRangeException(NameOf(ExpectedChunkIndex))

            Dim Entry = _Index(ExpectedChunkIndex)

            If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Return

            If Entry.Offset < DataStartOffset Then Throw New InvalidDataException($"Invalid chunk offset for chunk {ExpectedChunkIndex}.")
            If Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk length for chunk {ExpectedChunkIndex}.")
            If Entry.Offset + Entry.RecordLength > _IndexOffset Then Throw New InvalidDataException($"Chunk {ExpectedChunkIndex} extends beyond data area.")

            Dim Record(Entry.RecordLength - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Record, 0, Record.Length)

            Dim ChunkIndex = BitConverter.ToInt64(Record, 0)

            If ChunkIndex <> ExpectedChunkIndex Then
                Throw New InvalidDataException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {ChunkIndex}.")
            End If

            Dim EncryptionMethod = CType(BitConverter.ToInt32(Record, ChunkEncryptionMethodOffset), ChunkEncryptionMethods)
            Dim PayloadLength = BitConverter.ToInt32(Record, ChunkPayloadLengthOffset)
            Dim Flags = CType(BitConverter.ToInt32(Record, ChunkFlagsOffset), ChunkFlags)
            Dim CompressionEvaluatedPercent = CInt(Record(ChunkCompressionEvaluatedPercentOffset))

            If PayloadLength < 0 Then Throw New InvalidDataException("Invalid chunk payload length.")
            If ChunkRecordDataOffset + PayloadLength + MacSize <> Record.Length Then Throw New InvalidDataException("Invalid chunk record length.")

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported chunk flags for chunk {ExpectedChunkIndex}: {CInt(Flags)}.")
            End If

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
               CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then

                Throw New InvalidDataException($"Invalid compression evaluated percent for chunk {ExpectedChunkIndex}: {CompressionEvaluatedPercent}.")

            End If

            Dim RecordMacKey =
                If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                   _ChunkMacKey,
                   PublicIntegrityKey)

            If RecordMacKey Is Nothing Then
                Throw New EncryptionMismatchException("Encrypted chunk exists but no file master key is available.")
            End If

            Using Hmac As New HMACSHA256(RecordMacKey)
                Dim ExpectedMac = Hmac.ComputeHash(Record, 0, ChunkRecordDataOffset + PayloadLength)

                If Not FixedTimeEquals(ExpectedMac, 0, Record, ChunkRecordDataOffset + PayloadLength, MacSize) Then
                    Throw New CryptographicException($"Chunk MAC invalid for chunk {ExpectedChunkIndex}.")
                End If
            End Using

        End Sub

    End Class

End Namespace
