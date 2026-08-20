Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Private NotInheritable Class FreeSpaceAllocator

            Private ReadOnly _SpacesByLength As New SortedDictionary(Of Long, Queue(Of Long))()

            Public Sub Add(Offset As Long, Length As Long)

                If Offset < DataStartOffset Then Return
                If Length <= 0 Then Return

                Dim Offsets As Queue(Of Long) = Nothing

                If _SpacesByLength.TryGetValue(Length, Offsets) = False Then
                    Offsets = New Queue(Of Long)()
                    _SpacesByLength.Add(Length, Offsets)
                End If

                Offsets.Enqueue(Offset)

            End Sub

            Public Function TryAllocate(RequiredLength As Long, ByRef Offset As Long) As Boolean

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

            Public Function Snapshot(SpaceType As HoleSpaceTypes) As List(Of HoleDirectoryRecord)

                Dim Result As New List(Of HoleDirectoryRecord)()

                For Each pair In _SpacesByLength
                    For Each offset In pair.Value
                        Result.Add(New HoleDirectoryRecord With {
                            .SpaceType = SpaceType,
                            .Offset = offset,
                            .Length = pair.Key
                        })
                    Next
                Next

                Return Result

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
            If IsRangeSafeForPhysicalRecord(Offset, Length) = False Then Return

            _FreeChunkSpaces.Add(Offset, Length)

        End Sub

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

            For Each Record In _PhysicalRecords.Values

                If Record.RefCount <= 0 Then Continue For

                If StorageRangesOverlap(Offset,
                                        Length,
                                        Record.PhysicalOffset,
                                        Record.PhysicalLength) Then

                    Return True

                End If

            Next

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

        Private Sub AddFreeIndexPageSpace(Offset As Long,
                                          Length As Long)

            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return
            If IsRangeSafeForMetadata(Offset, Length) = False Then Return

            _FreeIndexPageSpaces.Add(Offset, Length)

        End Sub

        Private Sub AddFreeIndexDirectoryPageSpace(Offset As Long,
                                                   Length As Long)

            If Offset < DataStartOffset Then Return
            If Length <= 0 Then Return
            If IsRangeSafeForMetadata(Offset, Length) = False Then Return

            _FreeIndexDirectoryPageSpaces.Add(Offset, Length)

        End Sub

        Private Function GetKnownHoleRecords() As List(Of HoleDirectoryRecord)

            Dim Result As New List(Of HoleDirectoryRecord)()

            Result.AddRange(_FreeChunkSpaces.Snapshot(HoleSpaceTypes.ChunkRecord))
            Result.AddRange(_FreeIndexPageSpaces.Snapshot(HoleSpaceTypes.IndexPage))
            Result.AddRange(_FreeIndexDirectoryPageSpaces.Snapshot(HoleSpaceTypes.DirectoryPage))

            Return Result

        End Function

        Private Sub LoadKnownHoleRecords(Records As IEnumerable(Of HoleDirectoryRecord))

            If Records Is Nothing Then Return

            For Each Record In Records

                Select Case Record.SpaceType

                    Case HoleSpaceTypes.ChunkRecord

                        If IsRangeSafeForPhysicalRecord(Record.Offset, Record.Length) Then
                            _FreeChunkSpaces.Add(Record.Offset, Record.Length)
                        End If

                    Case HoleSpaceTypes.IndexPage

                        If IsRangeSafeForMetadata(Record.Offset, Record.Length) Then
                            _FreeIndexPageSpaces.Add(Record.Offset, Record.Length)
                        End If

                    Case HoleSpaceTypes.DirectoryPage

                        If IsRangeSafeForMetadata(Record.Offset, Record.Length) Then
                            _FreeIndexDirectoryPageSpaces.Add(Record.Offset, Record.Length)
                        End If

                End Select

            Next

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
            Try
                swWritePhysicalRecordWithPolicy.Start()

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

                    Dim Compressed = (Function()
                                          Try
                                              swCompressPayload.Start()
                                              Return CompressPayload(CompressionMethodToUse, Plain, PlainLength)
                                          Finally
                                              swCompressPayload.Stop()
                                          End Try
                                      End Function).Invoke()

                    CompressionEvaluatedMethod = CompressionMethodToUse
                    CompressionEvaluatedPercent = GetCompressionEvaluatedPercent(PlainLength, Compressed.Length)

                    If ForceCompression OrElse CompressionEvaluatedPercent / 100.0R <= CompressionRatioThreshold Then
                        Payload = Compressed
                        PayloadLength = Compressed.Length
                        StoredCompressionMethod = CompressionMethodToUse
                        MarkCompressionFlag(StoredCompressionMethod)
                    End If

                End If

                Dim PlaintextAllZero = PlainLength = 0 OrElse IsAllZero(Plain, PlainLength)
                Dim Flags = ChunkFlags.None

                If PlaintextAllZero Then
                    Flags = Flags Or ChunkFlags.PlaintextAllZero
                End If

                Dim RecordId = AllocatePhysicalRecordId()
                Dim RecordLength = ChunkRecordDataOffset + PayloadLength + MacSize
                Dim Record(RecordLength - 1) As Byte

                Buffer.BlockCopy(BitConverter.GetBytes(RecordId), 0, Record, 0, 8)
                Buffer.BlockCopy(BitConverter.GetBytes(CInt(StoredCompressionMethod)), 0, Record, ChunkCompressionMethodOffset, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(CInt(EncryptionMethod)), 0, Record, ChunkEncryptionMethodOffset, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, Record, ChunkPlainLengthOffset, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, Record, ChunkPayloadLengthOffset, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(CInt(Flags)), 0, Record, ChunkFlagsOffset, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(CInt(CompressionEvaluatedMethod)), 0, Record, ChunkCompressionEvaluatedMethodOffset, 4)

                Record(ChunkCompressionEvaluatedPercentOffset) = CompressionEvaluatedPercent

                _Rng.GetBytes(_Counter)
                Buffer.BlockCopy(_Counter, 0, Record, ChunkRecordIvOffset, IvSize)

                Select Case EncryptionMethod

                    Case ChunkEncryptionMethods.None

                        If PayloadLength > 0 Then
                            Buffer.BlockCopy(Payload, 0, Record, ChunkRecordDataOffset, PayloadLength)
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
                    Dim Mac = (Function()
                                   Try
                                       swComputeHash.Start()
                                       Return Hmac.ComputeHash(Record, 0, ChunkRecordDataOffset + PayloadLength)
                                   Finally
                                       swComputeHash.Stop()

                                   End Try
                               End Function).Invoke()
                    Buffer.BlockCopy(Mac, 0, Record, ChunkRecordDataOffset + PayloadLength, MacSize)
                End Using

                Dim NewRecordOffset = GetNextPhysicalRecordWriteOffset(Record.Length)

                _Fs.Position = NewRecordOffset
                _Fs.Write(Record, 0, Record.Length)

                Dim Result =
                    New PhysicalRecordEntry With {
                        .RecordId = RecordId,
                        .PhysicalOffset = NewRecordOffset,
                        .PhysicalLength = Record.Length,
                        .PlainLength = PlainLength,
                        .RefCount = 1
                    }

                _PhysicalRecords(Result.RecordId) = Result

                Dim NewRecordEndOffset = NewRecordOffset + Record.Length

                If NewRecordEndOffset > _IndexOffset Then
                    _IndexOffset = NewRecordEndOffset
                End If

                Try
                    swMarkPhysicalRecordDirty.Start()
                    MarkPhysicalRecordDirty(Result.RecordId)
                Finally
                    swMarkPhysicalRecordDirty.Stop()
                End Try

                Return Result
            Finally
                swWritePhysicalRecordWithPolicy.Stop()
            End Try



        End Function

        Private Function GetNextPhysicalRecordWriteOffset(RecordLength As Integer) As Long

            Try
                swGetNextPhysicalRecordWriteOffset.Start()

                If RecordLength < MinChunkRecordSize Then Throw New ArgumentOutOfRangeException(NameOf(RecordLength))

                If HasOpenCheckpoint Then
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

                Return Math.Max(Math.Max(_Fs.Length, GetDataEndFromIndex()), _IndexOffset)
            Finally
                swGetNextPhysicalRecordWriteOffset.Stop()
            End Try



        End Function

        Private Function ReadPhysicalRecordPlain(Record As PhysicalRecordEntry) As Byte()

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            If Record.PhysicalOffset + Record.PhysicalLength > _Fs.Length Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")

            Dim StoredRecord(Record.PhysicalLength - 1) As Byte

            _Fs.Position = Record.PhysicalOffset
            ReadExactly(_Fs, StoredRecord, 0, StoredRecord.Length)

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

            Dim DataEnd = CLng(DataStartOffset)

            For Each Record In _PhysicalRecords.Values
                DataEnd = Math.Max(DataEnd, Record.PhysicalOffset + CLng(Record.PhysicalLength))
            Next

            Return DataEnd

        End Function

        Private Sub BuildFreeChunkSpaceMap()

            _FreeChunkSpaces.Clear()

            Dim ReservedRanges As New List(Of Tuple(Of Long, Long))()

            For Each Record In _PhysicalRecords.Values

                If Record.RefCount <= 0 Then Continue For

                ReservedRanges.Add(
                    Tuple.Create(
                        Record.PhysicalOffset,
                        Record.PhysicalOffset + CLng(Record.PhysicalLength)))

            Next

            ReservedRanges.AddRange(GetActiveMetadataRanges())

            ReservedRanges =
                ReservedRanges.
                Where(Function(range) range.Item2 > DataStartOffset AndAlso range.Item2 > range.Item1).
                OrderBy(Function(range) range.Item1).
                ToList()

            Dim ScanEnd = Math.Max(_IndexOffset, GetDataEndFromIndex())

            For Each Range In ReservedRanges
                ScanEnd = Math.Max(ScanEnd, Range.Item2)
            Next

            Dim Cursor = CLng(DataStartOffset)

            For Each Range In ReservedRanges

                Dim RangeStart = Math.Max(CLng(DataStartOffset), Range.Item1)
                Dim RangeEnd = Range.Item2

                If RangeEnd <= RangeStart Then Continue For

                If RangeStart > Cursor Then
                    AddFreeChunkSpace(Cursor, RangeStart - Cursor)
                End If

                Cursor = Math.Max(Cursor, RangeEnd)

            Next

            If ScanEnd > Cursor Then
                AddFreeChunkSpace(Cursor, ScanEnd - Cursor)
            End If

        End Sub

    End Class
End Namespace