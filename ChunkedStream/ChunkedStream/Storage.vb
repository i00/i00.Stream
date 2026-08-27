Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Private NotInheritable Class FreeSpaceAllocator

            Private ReadOnly _SpacesByOffset As New SortedDictionary(Of Long, Long)()

            Public Sub Add(Offset As Long, Length As Long)

                If Offset < DataStartOffset Then Return
                If Length <= 0 Then Return
                If Offset > Long.MaxValue - Length Then Throw New ArgumentOutOfRangeException(NameOf(Length))

                Dim MergedStart = Offset
                Dim MergedEnd = Offset + Length
                Dim OffsetsToRemove As New List(Of Long)()

                For Each pair In _SpacesByOffset

                    Dim ExistingStart = pair.Key
                    Dim ExistingEnd = ExistingStart + pair.Value

                    If ExistingEnd < MergedStart Then Continue For
                    If ExistingStart > MergedEnd Then Exit For

                    MergedStart = Math.Min(MergedStart, ExistingStart)
                    MergedEnd = Math.Max(MergedEnd, ExistingEnd)
                    OffsetsToRemove.Add(ExistingStart)

                Next

                For Each existingOffset In OffsetsToRemove
                    _SpacesByOffset.Remove(existingOffset)
                Next

                _SpacesByOffset(MergedStart) = MergedEnd - MergedStart

            End Sub

            Public Function TryAllocate(RequiredLength As Long, FromStart As Boolean, ByRef Offset As Long) As Boolean

                If RequiredLength <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(RequiredLength))

                Dim SelectedOffset As Long = -1
                Dim SelectedLength As Long = Long.MaxValue

                For Each pair In _SpacesByOffset

                    If pair.Value < RequiredLength Then Continue For

                    If FromStart Then
                        SelectedOffset = pair.Key
                        SelectedLength = pair.Value
                        Exit For
                    End If

                    If pair.Value < SelectedLength Then
                        SelectedOffset = pair.Key
                        SelectedLength = pair.Value
                    End If

                Next

                If SelectedOffset < 0 Then
                    Offset = -1
                    Return False
                End If

                _SpacesByOffset.Remove(SelectedOffset)
                Offset = SelectedOffset

                Dim RemainingLength = SelectedLength - RequiredLength

                If RemainingLength > 0 Then
                    Add(SelectedOffset + RequiredLength, RemainingLength)
                End If

                Return True

            End Function

            Public Function Snapshot() As List(Of HoleDirectoryRecord)

                Return _SpacesByOffset.
                       Select(Function(pair) New HoleDirectoryRecord With {
                           .SpaceType = HoleSpaceTypes.FreeSpace,
                           .Offset = pair.Key,
                           .Length = pair.Value
                       }).
                       ToList()

            End Function

            Public Sub Clear()
                _SpacesByOffset.Clear()
            End Sub

        End Class

        Private ReadOnly _FreeSpaces As New FreeSpaceAllocator()

        Private Shared Function StorageRangesOverlap(Offset1 As Long,
                                                     Length1 As Long,
                                                     Offset2 As Long,
                                                     Length2 As Long) As Boolean

            Return Offset1 < Offset2 + Length2 AndAlso Offset2 < Offset1 + Length1

        End Function

        Private Function GetActiveMetadataRanges() As List(Of Tuple(Of Long, Long))

            Dim Result As New List(Of Tuple(Of Long, Long))()

            For Each Descriptor In _ExtentPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _ExtentDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _PhysicalRecordPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _PhysicalRecordDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            For Each Descriptor In _HoleDirectoryPageDescriptors.Values
                If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                    Result.Add(Tuple.Create(Descriptor.Offset, Descriptor.Offset + CLng(Descriptor.Length)))
                End If
            Next

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then
                Result.Add(Tuple.Create(_MetadataRootOffset, _MetadataRootOffset + CLng(_MetadataRootLength)))
            End If

            Return Result

        End Function

        Private Function RangeOverlapsLivePhysicalRecord(Offset As Long,
                                                         Length As Long) As Boolean

            If Length <= 0 Then Return False
            If _LivePhysicalRecordIdsByOffset.Count = 0 Then Return False
            If Offset > Long.MaxValue - Length Then Return True

            Dim EndOffset = Offset + Length
            Dim Low = 0
            Dim High = _LivePhysicalRecordIdsByOffset.Count

            '
            ' Find the first live physical record whose starting offset is greater
            ' than or equal to the requested range start.
            '
            While Low < High

                Dim Middle = Low + ((High - Low) \ 2)

                If _LivePhysicalRecordIdsByOffset.Keys(Middle) < Offset Then
                    Low = Middle + 1
                Else
                    High = Middle
                End If

            End While

            Dim CandidateIndex = Low

            '
            ' The preceding record may begin before the requested range and extend
            ' into it.
            '
            If CandidateIndex > 0 Then

                Dim PreviousRecordId =
                    _LivePhysicalRecordIdsByOffset.Values(CandidateIndex - 1)

                Dim PreviousRecord = GetPhysicalRecord(PreviousRecordId)

                If StorageRangesOverlap(Offset,
                                        Length,
                                        PreviousRecord.PhysicalOffset,
                                        PreviousRecord.PhysicalLength) Then

                    Return True

                End If

            End If

            '
            ' The first record beginning at or after Offset overlaps when its start
            ' occurs before EndOffset.
            '
            If CandidateIndex < _LivePhysicalRecordIdsByOffset.Count Then

                Dim CandidateOffset =
                    _LivePhysicalRecordIdsByOffset.Keys(CandidateIndex)

                If CandidateOffset < EndOffset Then
                    Return True
                End If

            End If

            Return False

        End Function

        Private Function RangeOverlapsActiveMetadata(Offset As Long,
                                                     Length As Long) As Boolean

            If Length <= 0 Then Return False

            For Each Range In GetActiveMetadataRanges()

                If StorageRangesOverlap(Offset,
                                        Length,
                                        Range.Item1,
                                        Range.Item2 - Range.Item1) Then

                    Return True

                End If

            Next

            Return False

        End Function

        Private Function IsRangeSafeForPhysicalRecord(Offset As Long,
                                                      Length As Long) As Boolean

            If Offset < DataStartOffset Then Return False
            If Length <= 0 Then Return False
            If RangeOverlapsLivePhysicalRecord(Offset, Length) Then Return False
            If RangeOverlapsActiveMetadata(Offset, Length) Then Return False

            Return True

        End Function

        Private Function IsRangeSafeForMetadata(Offset As Long,
                                                Length As Long) As Boolean

            If Offset < DataStartOffset Then Return False
            If Length <= 0 Then Return False
            If RangeOverlapsLivePhysicalRecord(Offset, Length) Then Return False

            Return True

        End Function

        Private Sub ClearFreeSpaceMap()
            _FreeSpaces.Clear()
        End Sub

        Private Sub AddFreeSpace(Offset As Long, Length As Long)
            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return
            _FreeSpaces.Add(Offset, Length)
        End Sub

        Private Function GetKnownHoleRecords() As List(Of HoleDirectoryRecord)
            Return _FreeSpaces.Snapshot()
        End Function

        Private Sub LoadKnownHoleRecords(Records As IEnumerable(Of HoleDirectoryRecord))

            If Records Is Nothing Then Return

            For Each Record In Records
                If Record.SpaceType = HoleSpaceTypes.None Then Continue For
                If IsRangeSafeForPhysicalRecord(Record.Offset, Record.Length) Then
                    _FreeSpaces.Add(Record.Offset, Record.Length)
                End If
            Next

        End Sub

        Private Function GetNextWriteOffset(Length As Integer,
                                            Policy As ChunkedStreamOptions.NewWriteLocationPolicies,
                                            IsMetadata As Boolean) As Long

            If Length <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If IsMetadata = False AndAlso Length < MinChunkRecordSize Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Dim Offset As Long

            If IsMetadata AndAlso TryGetCompactMetadataWriteOffset(Length, Offset) Then
                If IsRangeSafeForMetadata(Offset, Length) = False Then
                    Throw New InvalidOperationException($"Compact metadata allocation overlaps live physical data. Offset={Offset}, Length={Length}.")
                End If
                Return Offset
            End If

            If IsMetadata = False AndAlso HasOpenCheckpoint Then
                Return Math.Max(Math.Max(BaseStream.Length, GetDataEndFromIndex()), _IndexOffset)
            End If

            Select Case Policy

                Case ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                     ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan

                    If TryAllocateSafeSpace(Length, IsMetadata, False, Offset) Then
                        Return Offset
                    End If

                    If Policy = ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan Then
                        BuildFreeSpaceMap()
                        If TryAllocateSafeSpace(Length, IsMetadata, True, Offset) Then
                            Return Offset
                        End If
                    End If

            End Select

            Return Math.Max(Math.Max(BaseStream.Length, GetDataEndFromIndex()), _IndexOffset)

        End Function

        Private Function TryAllocateSafeSpace(Length As Integer,
                                              IsMetadata As Boolean,
                                              FromStart As Boolean,
                                              ByRef Offset As Long) As Boolean

            Dim CandidateOffset As Long

            While _FreeSpaces.TryAllocate(Length, FromStart, CandidateOffset)

                Dim IsSafe = If(IsMetadata,
                                IsRangeSafeForMetadata(CandidateOffset, Length),
                                IsRangeSafeForPhysicalRecord(CandidateOffset, Length))

                If IsSafe Then
                    Offset = CandidateOffset
                    Return True
                End If

            End While

            Offset = -1
            Return False

        End Function

        Private Sub BuildFreeSpaceMap()

            _FreeSpaces.Clear()

            Dim ReservedRanges As New List(Of Tuple(Of Long, Long))()

            For Each Record In _PhysicalRecords.Values
                If Record.RefCount <= 0 Then Continue For
                ReservedRanges.Add(Tuple.Create(Record.PhysicalOffset,
                                                Record.PhysicalOffset + CLng(Record.PhysicalLength)))
            Next

            ReservedRanges.AddRange(GetActiveMetadataRanges())
            ReservedRanges = ReservedRanges.
                             Where(Function(range) range.Item2 > DataStartOffset AndAlso range.Item2 > range.Item1).
                             OrderBy(Function(range) range.Item1).
                             ToList()

            Dim ScanEnd = Math.Max(Math.Max(BaseStream.Length, _IndexOffset), GetDataEndFromIndex())
            Dim Cursor = CLng(DataStartOffset)

            For Each Range In ReservedRanges

                Dim RangeStart = Math.Max(CLng(DataStartOffset), Range.Item1)
                Dim RangeEnd = Range.Item2

                If RangeEnd <= Cursor Then Continue For

                If RangeStart > Cursor Then
                    AddFreeSpace(Cursor, RangeStart - Cursor)
                End If

                Cursor = Math.Max(Cursor, RangeEnd)

            Next

            If ScanEnd > Cursor Then
                AddFreeSpace(Cursor, ScanEnd - Cursor)
            End If

        End Sub

        Private Function WritePhysicalRecord(Plain As Byte(),
                                             PlainLength As Integer) As PhysicalRecordEntry

            Dim EncryptionMethod =
                If(_CurrentWriteEncryptionEnabled,
                   ChunkEncryptionMethods.AesCtrFileMasterKey,
                   ChunkEncryptionMethods.None)

            Return WritePhysicalRecordWithPolicy(Plain,
                                                 PlainLength,
                                                 Options.CompressionMethod,
                                                 Options.CompressionRatioThreshold,
                                                 False,
                                                 EncryptionMethod)

        End Function

        Private Function WritePhysicalRecordWithPolicy(Plain As Byte(),
                                                       PlainLength As Integer,
                                                       CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods,
                                                       CompressionRatioThreshold As Double,
                                                       ForceCompression As Boolean,
                                                       EncryptionMethod As ChunkEncryptionMethods) As PhysicalRecordEntry

            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If PlainLength < 0 OrElse PlainLength > Plain.Length Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))

            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim StoredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None
            Dim CompressionEvaluatedPercent As Byte = 100

            If CompressionMethodToUse <> ChunkedStreamOptions.CompressionMethods.None AndAlso PlainLength > 0 Then

                Dim Compressed = CompressPayload(CompressionMethodToUse, Plain, PlainLength)

                CompressionEvaluatedMethod = CompressionMethodToUse
                CompressionEvaluatedPercent =
                    GetCompressionEvaluatedPercent(PlainLength, Compressed.Length)

                If ForceCompression OrElse
                   CompressionEvaluatedPercent / 100.0R <= CompressionRatioThreshold Then

                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    StoredCompressionMethod = CompressionMethodToUse

                    MarkCompressionFlag(StoredCompressionMethod)

                End If

            End If

            Dim PlaintextAllZero =
                PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)

            Dim Flags = ChunkFlags.None

            If PlaintextAllZero Then
                Flags = Flags Or ChunkFlags.PlaintextAllZero
            End If

            Dim RecordId = AllocatePhysicalRecordId()
            Dim RecordLength = ChunkRecordDataOffset + PayloadLength + MacSize
            Dim StoredRecord(RecordLength - 1) As Byte

            Buffer.BlockCopy(BitConverter.GetBytes(RecordId), 0, StoredRecord, 0, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(StoredCompressionMethod)), 0, StoredRecord, ChunkCompressionMethodOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(EncryptionMethod)), 0, StoredRecord, ChunkEncryptionMethodOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, StoredRecord, ChunkPlainLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, StoredRecord, ChunkPayloadLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(Flags)), 0, StoredRecord, ChunkFlagsOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(CompressionEvaluatedMethod)), 0, StoredRecord, ChunkCompressionEvaluatedMethodOffset, 4)

            StoredRecord(ChunkCompressionEvaluatedPercentOffset) =
                CompressionEvaluatedPercent

            _Rng.GetBytes(_Counter)

            Buffer.BlockCopy(_Counter,
                             0,
                             StoredRecord,
                             ChunkRecordIvOffset,
                             IvSize)

            Select Case EncryptionMethod

                Case ChunkEncryptionMethods.None

                    If PayloadLength > 0 Then
                        Buffer.BlockCopy(Payload,
                                         0,
                                         StoredRecord,
                                         ChunkRecordDataOffset,
                                         PayloadLength)
                    End If

                Case ChunkEncryptionMethods.AesCtrFileMasterKey

                    If _ChunkEncryptionKey Is Nothing Then
                        Throw New EncryptionMismatchException(
                            "Encryption is enabled but no file master key is available.")
                    End If

                    CryptPayload(Payload,
                                 0,
                                 PayloadLength,
                                 StoredRecord,
                                 ChunkRecordDataOffset,
                                 _ChunkEncryptionKey)

                Case Else

                    Throw New InvalidDataException(
                        $"Unsupported chunk encryption method: {CInt(EncryptionMethod)}.")

            End Select

            Dim RecordMacKey =
                If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                   _ChunkMacKey,
                   PublicIntegrityKey)

            Using Hmac As New HMACSHA256(RecordMacKey)

                Dim Mac =
                    Hmac.ComputeHash(StoredRecord,
                                     0,
                                     ChunkRecordDataOffset + PayloadLength)

                Buffer.BlockCopy(Mac,
                                 0,
                                 StoredRecord,
                                 ChunkRecordDataOffset + PayloadLength,
                                 MacSize)

            End Using

            Dim NewRecordOffset =
                GetNextWriteOffset(StoredRecord.Length, Options.NewChunkWriteLocationPolicy, False)

            WriteAt(NewRecordOffset, StoredRecord, 0, StoredRecord.Length)

            Dim Result =
                New PhysicalRecordEntry With {
                    .RecordId = RecordId,
                    .PhysicalOffset = NewRecordOffset,
                    .PhysicalLength = StoredRecord.Length,
                    .PlainLength = PlainLength,
                    .RefCount = 1
                }

            Dim Ordinal = _PhysicalRecords.Count

            _PhysicalRecords.Add(Result.RecordId, Result)

            AddPhysicalRecordToIndexes(Result, Ordinal)

            Dim NewRecordEndOffset =
                NewRecordOffset + CLng(StoredRecord.Length)

            If NewRecordEndOffset > _IndexOffset Then
                _IndexOffset = NewRecordEndOffset
            End If

            MarkPhysicalRecordPageDirtyByOrdinal(Ordinal)

            Return Result

        End Function

        Private Function ReadPhysicalRecordPlain(Record As PhysicalRecordEntry) As Byte()

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            If Record.PhysicalOffset + Record.PhysicalLength > BaseStream.Length Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")

            Dim StoredRecord(Record.PhysicalLength - 1) As Byte

            ReadAt(Record.PhysicalOffset, StoredRecord, 0, StoredRecord.Length)

            Dim Plain(Record.PlainLength - 1) As Byte

            DecryptPhysicalRecord(Record.RecordId, StoredRecord, Plain)

            Return Plain

        End Function

        Private Sub ReadExtentBytes(Extent As ExtentIndexEntry,
                                    OffsetInsideExtent As Integer,
                                    Output As Byte(),
                                    OutputOffset As Integer,
                                    Count As Integer)

            If Count <= 0 Then Return

            If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                Array.Clear(Output, OutputOffset, Count)
                Return
            End If

            Dim Record = GetPhysicalRecord(Extent.PhysicalRecordId)
            Dim Plain = ReadPhysicalRecordPlain(Record)
            Dim SourceOffset = Extent.PhysicalRecordOffset + OffsetInsideExtent

            If SourceOffset < 0 OrElse SourceOffset + Count > Plain.Length Then
                Throw New InvalidDataException($"Extent references beyond physical record {Extent.PhysicalRecordId}.")
            End If

            Buffer.BlockCopy(Plain, SourceOffset, Output, OutputOffset, Count)

        End Sub

        Private Function GetDataEndFromIndex() As Long

            Return _PhysicalDataEnd

        End Function

    End Class
End Namespace