Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Public Function GetFragmentation() As Double

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Dim UsedBytes As Long = 0

                For Each record In _PhysicalRecords.Values
                    If record.RefCount > 0 Then
                        UsedBytes += record.PhysicalLength
                    End If
                Next

                Dim DataEnd = GetDataEndFromIndex()
                Dim TotalStoredBytes = Math.Max(0L, DataEnd - DataStartOffset)
                Dim WastedBytes = Math.Max(0L, TotalStoredBytes - UsedBytes)

                If TotalStoredBytes = 0 Then Return 0

                Return WastedBytes / CDbl(TotalStoredBytes)

            End SyncLock

        End Function

        Public Sub Validate(Optional ProgressCallback As StreamProgressCallback = Nothing)

            SyncLock _SyncRoot

                ThrowIfDisposed()

                ValidateExtentsAreSortedAndNonOverlapping()
                ValidatePhysicalRecordRefCounts()

                Dim CancellationToken As New CancellationToken()

                ValidateAllLivePhysicalRecords(ProgressCallback, CancellationToken)

            End SyncLock

        End Sub

        Private Sub ValidateAllLivePhysicalRecords(ProgressCallback As StreamProgressCallback,
                                                   CancellationToken As CancellationToken)

            Dim TotalRecords = Math.Max(1, _PhysicalRecords.Count)
            Dim ProcessedRecords As Long = 0

            For Each pair In _PhysicalRecords.OrderBy(Function(x) x.Key)

                If CancellationToken.Cancel Then Return

                Dim Record = pair.Value

                If Record.RefCount > 0 Then
                    ValidatePhysicalRecord(Record)
                End If

                ProcessedRecords += 1

                ReportProgress(ProgressCallback,
                               ProcessedRecords,
                               TotalRecords,
                               ProcessUnitTypes.Arbitrary,
                               CancellationToken)

            Next

        End Sub

        Private Sub ValidatePhysicalRecord(Record As PhysicalRecordEntry)

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException($"Invalid physical offset for record {Record.RecordId}.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical length for record {Record.RecordId}.")
            If Record.PhysicalOffset + Record.PhysicalLength > _IndexOffset Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond data area.")
            If Record.PlainLength < 0 Then Throw New InvalidDataException($"Invalid plain length for record {Record.RecordId}.")
            If Record.RefCount < 0 Then Throw New InvalidDataException($"Invalid refcount for record {Record.RecordId}.")

            Dim Buffer(Record.PhysicalLength - 1) As Byte

            _Fs.Position = Record.PhysicalOffset
            ReadExactly(_Fs, Buffer, 0, Buffer.Length)

            Dim StoredRecordId = BitConverter.ToInt64(Buffer, 0)

            If StoredRecordId <> Record.RecordId Then
                Throw New InvalidDataException($"Physical record id mismatch. Expected {Record.RecordId}, found {StoredRecordId}.")
            End If

            Dim EncryptionMethod = CType(BitConverter.ToInt32(Buffer, ChunkEncryptionMethodOffset), ChunkEncryptionMethods)
            Dim PayloadLength = BitConverter.ToInt32(Buffer, ChunkPayloadLengthOffset)
            Dim Flags = CType(BitConverter.ToInt32(Buffer, ChunkFlagsOffset), ChunkFlags)
            Dim CompressionEvaluatedPercent = CInt(Buffer(ChunkCompressionEvaluatedPercentOffset))

            If PayloadLength < 0 Then Throw New InvalidDataException("Invalid physical record payload length.")
            If ChunkRecordDataOffset + PayloadLength + MacSize <> Buffer.Length Then Throw New InvalidDataException("Invalid physical record length.")

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported chunk flags for physical record {Record.RecordId}: {CInt(Flags)}.")
            End If

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
               CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then

                Throw New InvalidDataException($"Invalid compression evaluated percent for physical record {Record.RecordId}: {CompressionEvaluatedPercent}.")

            End If

            Dim RecordMacKey =
                If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                   _ChunkMacKey,
                   PublicIntegrityKey)

            If RecordMacKey Is Nothing Then
                Throw New EncryptionMismatchException("Encrypted physical record exists but no file master key is available.")
            End If

            Using Hmac As New HMACSHA256(RecordMacKey)

                Dim ExpectedMac = Hmac.ComputeHash(Buffer, 0, ChunkRecordDataOffset + PayloadLength)

                If FixedTimeEquals(ExpectedMac, 0, Buffer, ChunkRecordDataOffset + PayloadLength, MacSize) = False Then
                    Throw New CryptographicException($"Physical record MAC invalid for record {Record.RecordId}.")
                End If

            End Using

        End Sub

    End Class
End Namespace