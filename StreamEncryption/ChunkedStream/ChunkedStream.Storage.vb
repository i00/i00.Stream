' ================================================================================
' ChunkedStream Storage
' ================================================================================
'
' Purpose
'   - Chunk record storage, loading and index maintenance.
'
' Design
'   - Logical chunks are represented by chunk records.
'   - Sparse chunks are represented using empty index entries.
'   - Chunk records are appended when rewritten.
'
' Read Cache
'   - The most recently loaded plaintext chunk may be cached.
'   - Cache usage is controlled by ChunkedStreamOptions.UseChunkReadCache.
'   - Cache contents are invalidated when data or structure changes.
'
' Notes
'   - This partial owns low-level chunk loading and writing operations.
'
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        Private NotInheritable Class FreeSpaceAllocator

            Private ReadOnly _SpacesByLength As New SortedDictionary(Of Long, Queue(Of Long))()

            Public Sub Add(Offset As Long,
                           Length As Long)

                If Offset < DataStartOffset Then Return
                If Length <= 0 Then Return

                Dim Offsets As Queue(Of Long) = Nothing

                If _SpacesByLength.TryGetValue(Length, Offsets) = False Then
                    Offsets = New Queue(Of Long)()
                    _SpacesByLength.Add(Length, Offsets)
                End If

                Offsets.Enqueue(Offset)

            End Sub

            Public Function TryAllocate(RequiredLength As Long,
                                        ByRef Offset As Long) As Boolean

                If RequiredLength <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(RequiredLength))

                Dim SelectedLength As Long = -1

                For Each pair In _SpacesByLength
                    If pair.Key >= RequiredLength Then
                        SelectedLength = pair.Key
                        Exit For
                    End If
                Next

                If SelectedLength < 0 Then
                    Offset = -1
                    Return False
                End If

                Dim Offsets = _SpacesByLength(SelectedLength)
                Offset = Offsets.Dequeue()

                If Offsets.Count = 0 Then
                    _SpacesByLength.Remove(SelectedLength)
                End If

                Dim RemainingLength = SelectedLength - RequiredLength

                If RemainingLength > 0 Then
                    Add(Offset + RequiredLength, RemainingLength)
                End If

                Return True

            End Function

            Public Sub Clear()

                _SpacesByLength.Clear()

            End Sub

        End Class

        Private ReadOnly _FreeChunkSpaces As New FreeSpaceAllocator()
        Private ReadOnly _FreeIndexPageSpaces As New FreeSpaceAllocator()
        Private ReadOnly _FreeIndexDirectoryPageSpaces As New FreeSpaceAllocator()

        Private Sub ClearFreeSpaceMaps()

            _FreeChunkSpaces.Clear()
            _FreeIndexPageSpaces.Clear()
            _FreeIndexDirectoryPageSpaces.Clear()

        End Sub

        Private Sub AddFreeChunkSpace(Offset As Long,
                                      Length As Long)

            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return

            _FreeChunkSpaces.Add(Offset, Length)

        End Sub

        Private Sub LoadChunk(ChunkIndex As Long, Plain As Byte())

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If Plain.Length < _ChunkSize Then Throw New ArgumentException("Chunk buffer is too small.", NameOf(Plain))

            If Options.UseChunkReadCache AndAlso _CachedChunkIndex = ChunkIndex Then
                System.Buffer.BlockCopy(_CachedChunkPlain, 0, Plain, 0, _ChunkSize)
                Return
            End If

            Array.Clear(Plain, 0, Plain.Length)

            If ChunkIndex >= _Index.Count Then

                If Options.UseChunkReadCache Then
                    Array.Clear(_CachedChunkPlain, 0, _CachedChunkPlain.Length)
                    _CachedChunkIndex = ChunkIndex
                End If

                Return

            End If

            Dim Entry = _Index(CInt(ChunkIndex))

            If Entry.Offset = 0 OrElse Entry.RecordLength = 0 Then

                If Options.UseChunkReadCache Then
                    Array.Clear(_CachedChunkPlain, 0, _CachedChunkPlain.Length)
                    _CachedChunkIndex = ChunkIndex
                End If

                Return

            End If

            If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk index entry for chunk {ChunkIndex}.")
            If Entry.Offset + Entry.RecordLength > _IndexOffset Then Throw New InvalidDataException($"Chunk {ChunkIndex} record extends beyond data area.")

            Dim Record(Entry.RecordLength - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Record, 0, Record.Length)

            DecryptChunkRecord(ChunkIndex, Record, Plain)

            If Options.UseChunkReadCache Then
                System.Buffer.BlockCopy(Plain, 0, _CachedChunkPlain, 0, _ChunkSize)
                _CachedChunkIndex = ChunkIndex
            End If

        End Sub

        Private Sub WriteChunkRecord(ChunkIndex As Long, Plain As Byte(), PlainLength As Integer)

            InvalidateChunkCache()

            Dim EncryptionMethod =
                If(_CurrentWriteEncryptionEnabled,
                   ChunkEncryptionMethods.AesCtrFileMasterKey,
                   ChunkEncryptionMethods.None)

            WriteChunkRecordWithPolicy(ChunkIndex,
                                       Plain,
                                       PlainLength,
                                       Options.StoreSparseChunks,
                                       Options.CompressionMethod,
                                       Options.CompressionRatioThreshold,
                                       False,
                                       EncryptionMethod)

        End Sub

        Private Sub WriteChunkRecordWithPolicy(ChunkIndex As Long,
                                               Plain As Byte(),
                                               PlainLength As Integer,
                                               StoreSparseChunks As Boolean,
                                               CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods,
                                               CompressionRatioThreshold As Double,
                                               ForceCompression As Boolean,
                                               EncryptionMethod As ChunkEncryptionMethods)

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If PlainLength < 0 OrElse PlainLength > _ChunkSize Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))
            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            EnsureIndexSize(CInt(ChunkIndex + 1))

            Dim PreviousEntry = _Index(CInt(ChunkIndex))
            Dim PlaintextAllZero = PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)

            If PlainLength = 0 OrElse (Not StoreSparseChunks AndAlso PlaintextAllZero) Then

                _Index(CInt(ChunkIndex)) = New ChunkIndexEntry()
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks

                If PreviousEntry.Offset > 0 AndAlso PreviousEntry.RecordLength > 0 Then
                    AddFreeChunkSpace(PreviousEntry.Offset, PreviousEntry.RecordLength)
                End If

                Return

            End If

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim StoredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedPercent As Byte = 100

            If CompressionMethodToUse <> ChunkedStreamOptions.CompressionMethods.None AndAlso PlainLength > 0 Then

                Dim Compressed = CompressPayload(CompressionMethodToUse, Plain, PlainLength)

                CompressionEvaluatedMethod = CompressionMethodToUse
                CompressionEvaluatedPercent = GetCompressionEvaluatedPercent(PlainLength, Compressed.Length)

                If ForceCompression OrElse CompressionEvaluatedPercent / 100.0R <= CompressionRatioThreshold Then
                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    StoredCompressionMethod = CompressionMethodToUse
                    MarkCompressionFlag(StoredCompressionMethod)
                End If

            End If

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

            Dim NewRecordOffset = GetNextChunkRecordWriteOffset(Record.Length)

            _Fs.Position = NewRecordOffset
            _Fs.Write(Record, 0, Record.Length)

            _Index(CInt(ChunkIndex)) =
                New ChunkIndexEntry With {
                    .Offset = NewRecordOffset,
                    .RecordLength = Record.Length
                }

            If PreviousEntry.Offset > 0 AndAlso PreviousEntry.RecordLength > 0 Then
                AddFreeChunkSpace(PreviousEntry.Offset, PreviousEntry.RecordLength)
            End If

            Dim NewRecordEndOffset = NewRecordOffset + Record.Length

            If NewRecordEndOffset > _IndexOffset Then
                _IndexOffset = NewRecordEndOffset
            End If

        End Sub

        Private Function GetNextChunkRecordWriteOffset(RecordLength As Integer) As Long

            If RecordLength < MinChunkRecordSize Then Throw New ArgumentOutOfRangeException(NameOf(RecordLength))

            If HasOpenCheckpoint Then

                ' While a checkpoint is active, never overwrite the currently committed
                ' index table or any existing committed record. Append tentative records
                ' beyond the physical end so rollback can restore the previous state.
                Return Math.Max(Math.Max(_Fs.Length, GetDataEndFromIndex()), _IndexOffset)

            End If

            Select Case Options.NewChunkWriteLocationPolicy

                Case ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles

                    Dim HoleOffset As Long

                    If _FreeChunkSpaces.TryAllocate(RecordLength, HoleOffset) Then
                        Return HoleOffset
                    End If

                Case ChunkedStreamOptions.NewWriteLocationPolicies.FillHolesFromStart

                    Dim HoleOffset As Long

                    If _FreeChunkSpaces.TryAllocate(RecordLength, HoleOffset) Then
                        Return HoleOffset
                    End If

                    BuildFreeChunkSpaceMap()

                    If _FreeChunkSpaces.TryAllocate(RecordLength, HoleOffset) Then
                        Return HoleOffset
                    End If

            End Select

            Return _IndexOffset

        End Function

        Private Sub BuildFreeChunkSpaceMap()

            _FreeChunkSpaces.Clear()

            Dim LiveRanges As New List(Of Tuple(Of Long, Long))()

            For Each entry In _Index

                If entry.Offset = 0 OrElse entry.RecordLength = 0 Then Continue For

                If entry.Offset < DataStartOffset Then Throw New InvalidDataException("Invalid chunk index entry offset.")
                If entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException("Invalid chunk index entry record length.")
                If entry.Offset + entry.RecordLength > _IndexOffset Then Throw New InvalidDataException("Chunk record extends beyond data area.")

                LiveRanges.Add(Tuple.Create(entry.Offset, entry.Offset + CLng(entry.RecordLength)))

            Next

            LiveRanges.Sort(Function(left, right) left.Item1.CompareTo(right.Item1))

            Dim Cursor = CLng(DataStartOffset)

            For Each liveRange In LiveRanges

                If liveRange.Item1 > Cursor Then
                    AddFreeChunkSpace(Cursor, liveRange.Item1 - Cursor)
                End If

                Cursor = Math.Max(Cursor, liveRange.Item2)

            Next

            If _IndexOffset > Cursor Then
                AddFreeChunkSpace(Cursor, _IndexOffset - Cursor)
            End If

        End Sub

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

        Private Function GetRequiredChunkCount(Length As Long) As Integer

            If Length <= 0 Then Return 0

            Dim Count = ((Length - 1) \ _ChunkSize) + 1

            If Count > Integer.MaxValue Then Throw New InvalidDataException("Chunked stream has too many chunks for this implementation.")

            Return CInt(Count)

        End Function

    End Class

End Namespace