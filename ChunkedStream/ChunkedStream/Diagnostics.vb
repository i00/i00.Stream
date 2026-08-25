Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Private NotInheritable Class DiagnosticsSnapshot
            Public LogicalLength As Long
            Public DataEnd As Long
            Public AnchorIndexCount As Integer
            Public Extents As List(Of ExtentIndexEntry)
            Public PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
            Public StoredRecords As Dictionary(Of Long, Byte())
            Public ChunkMacKey As Byte()
        End Class

        Public Function GetFragmentation(Optional CancellationToken As Threading.CancellationToken = Nothing) As Double

            Dim Snapshot As DiagnosticsSnapshot

            Using EnterStateLock()
                Snapshot = CaptureDiagnosticsSnapshotCore(False)
            End Using

            CancellationToken.ThrowIfCancellationRequested()

            Return GetFragmentationCore(Snapshot)

        End Function

        Public Async Function GetFragmentationAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Double)
            Return Await Task.Run(
                   Function()
                       CancellationToken.ThrowIfCancellationRequested()
                       Return GetFragmentation(CancellationToken)
                   End Function, CancellationToken).ConfigureAwait(False)
        End Function

        Private Function GetFragmentationCore(Snapshot As DiagnosticsSnapshot) As Double

            If Snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(Snapshot))

            Dim UsedBytes As Long = 0

            For Each Record In Snapshot.PhysicalRecords.Values
                If Record.RefCount > 0 Then UsedBytes += Record.PhysicalLength
            Next

            Dim TotalStoredBytes = Math.Max(0L, Snapshot.DataEnd - DataStartOffset)
            Dim WastedBytes = Math.Max(0L, TotalStoredBytes - UsedBytes)

            If TotalStoredBytes = 0 Then Return 0
            Return WastedBytes / CDbl(TotalStoredBytes)

        End Function

        Public Sub Validate(Optional ProgressCallback As StreamProgressCallback = Nothing, Optional CancellationToken As Threading.CancellationToken = Nothing)

            Dim Snapshot As DiagnosticsSnapshot

            Using EnterStateLock()
                Snapshot = CaptureDiagnosticsSnapshotCore(True)
            End Using

            ValidateCore(Snapshot, ProgressCallback, CancellationToken)

        End Sub


        Public Async Function ValidateAsync(Optional ProgressCallback As StreamProgressCallback = Nothing, Optional CancellationToken As Threading.CancellationToken = Nothing) As Task
            Await Task.Run(
                Sub()
                    CancellationToken.ThrowIfCancellationRequested()
                    GetStructure(CancellationToken)
                End Sub, CancellationToken).ConfigureAwait(False)
        End Function

        Private Function CaptureDiagnosticsSnapshotCore(IncludeStoredRecords As Boolean) As DiagnosticsSnapshot

            ThrowIfDisposed()

            Dim Snapshot = New DiagnosticsSnapshot With {
                .LogicalLength = _Length,
                .DataEnd = GetDataEndFromIndex(),
                .AnchorIndexCount = _ExtentIndexesByAnchorId.Count,
                .Extents = New List(Of ExtentIndexEntry)(_Extents),
                .PhysicalRecords = New Dictionary(Of Long, PhysicalRecordEntry)(_PhysicalRecords),
                .StoredRecords = New Dictionary(Of Long, Byte())(),
                .ChunkMacKey = If(_ChunkMacKey Is Nothing, Nothing, DirectCast(_ChunkMacKey.Clone(), Byte()))
            }

            If IncludeStoredRecords Then
                For Each Pair In Snapshot.PhysicalRecords
                    Dim Record = Pair.Value
                    If Record.RefCount <= 0 Then Continue For
                    If Record.PhysicalOffset < DataStartOffset Then Continue For
                    If Record.PhysicalLength < MinChunkRecordSize Then Continue For
                    If Record.PhysicalOffset > _Fs.Length - Record.PhysicalLength Then Continue For
                    Dim Buffer(Record.PhysicalLength - 1) As Byte
                    ReadAt(Record.PhysicalOffset, Buffer, 0, Buffer.Length)
                    Snapshot.StoredRecords.Add(Record.RecordId, Buffer)
                Next
            End If

            Return Snapshot

        End Function

        Private Sub ValidateCore(Snapshot As DiagnosticsSnapshot,
                                 Optional ProgressCallback As StreamProgressCallback = Nothing,
                                 Optional CancellationToken As Threading.CancellationToken = Nothing)

            If Snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(Snapshot))

            ValidateExtentSnapshot(Snapshot)
            ValidatePhysicalRecordRefCountSnapshot(Snapshot)
            ValidateAnchorSnapshot(Snapshot)

            ValidateAllLivePhysicalRecordsSnapshot(Snapshot, ProgressCallback, CancellationToken)

        End Sub

        Private Shared Sub ValidateExtentSnapshot(Snapshot As DiagnosticsSnapshot)

            Dim ExpectedOffset As Long = 0
            Dim AnchorIds As New HashSet(Of Long)()
            Dim AnchorOffsets As New HashSet(Of Long)()

            For Each Extent In Snapshot.Extents
                If Extent.LogicalOffset < 0 Then Throw New InvalidDataException("Extent has a negative logical offset.")
                If Extent.LogicalOffset <> ExpectedOffset Then Throw New InvalidDataException($"Extent layout contains a gap or overlap at logical offset {ExpectedOffset}.")
                If Extent.LogicalLength <= 0 Then Throw New InvalidDataException("Extent has an invalid logical length.")
                If Extent.PhysicalRecordOffset < 0 Then Throw New InvalidDataException("Extent has a negative physical record offset.")
                If Extent.AnchorId < 0 Then Throw New InvalidDataException("Extent has a negative anchor id.")

                If Extent.AnchorId > 0 Then
                    If AnchorIds.Add(Extent.AnchorId) = False Then Throw New InvalidDataException($"Duplicate anchor id {Extent.AnchorId}.")
                    If AnchorOffsets.Add(Extent.LogicalOffset) = False Then Throw New InvalidDataException($"Multiple anchors identify logical offset {Extent.LogicalOffset}.")
                End If

                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                    If Extent.PhysicalRecordOffset <> 0 Then Throw New InvalidDataException("Sparse extent has a non-zero physical record offset.")
                Else
                    Dim Record As PhysicalRecordEntry
                    If Snapshot.PhysicalRecords.TryGetValue(Extent.PhysicalRecordId, Record) = False Then Throw New InvalidDataException($"Missing physical record {Extent.PhysicalRecordId}.")
                    If Extent.PhysicalRecordOffset > Record.PlainLength - Extent.LogicalLength Then Throw New InvalidDataException($"Extent references beyond physical record {Extent.PhysicalRecordId}.")
                End If

                If Extent.LogicalOffset > Long.MaxValue - CLng(Extent.LogicalLength) Then Throw New InvalidDataException("Extent logical end offset overflowed.")
                ExpectedOffset = Extent.LogicalOffset + CLng(Extent.LogicalLength)
            Next

            If ExpectedOffset <> Snapshot.LogicalLength Then Throw New InvalidDataException($"Extent logical length mismatch. Expected {Snapshot.LogicalLength}, found {ExpectedOffset}.")

        End Sub

        Private Shared Sub ValidatePhysicalRecordRefCountSnapshot(Snapshot As DiagnosticsSnapshot)

            Dim ActualCounts As New Dictionary(Of Long, Integer)()

            For Each Extent In Snapshot.Extents
                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then Continue For
                Dim Count As Integer = 0
                ActualCounts.TryGetValue(Extent.PhysicalRecordId, Count)
                ActualCounts(Extent.PhysicalRecordId) = Count + 1
            Next

            For Each Pair In Snapshot.PhysicalRecords
                Dim ActualCount As Integer = 0
                ActualCounts.TryGetValue(Pair.Key, ActualCount)
                If Pair.Value.RefCount <> ActualCount Then Throw New InvalidDataException($"Refcount mismatch for physical record {Pair.Key}. Expected {ActualCount}, found {Pair.Value.RefCount}.")
            Next

        End Sub

        Private Shared Sub ValidateAnchorSnapshot(Snapshot As DiagnosticsSnapshot)

            Dim SeenAnchorIds As New HashSet(Of Long)()
            Dim SeenOffsets As New HashSet(Of Long)()

            For Each Extent In Snapshot.Extents
                If Extent.AnchorId < 0 Then Throw New InvalidDataException("Extent has a negative anchor id.")
                If Extent.AnchorId = 0 Then Continue For
                If SeenAnchorIds.Add(Extent.AnchorId) = False Then Throw New InvalidDataException($"Duplicate anchor id {Extent.AnchorId}.")
                If SeenOffsets.Add(Extent.LogicalOffset) = False Then Throw New InvalidDataException($"Multiple anchors identify logical offset {Extent.LogicalOffset}.")
            Next

            If SeenAnchorIds.Count <> Snapshot.AnchorIndexCount Then Throw New InvalidDataException("Anchor index count does not match the anchored extent count.")

        End Sub

        Private Sub ValidateAllLivePhysicalRecordsSnapshot(Snapshot As DiagnosticsSnapshot,
                                                           ProgressCallback As StreamProgressCallback,
                                                           ThreadingCancellationToken As Threading.CancellationToken)
            Dim TotalRecords = Math.Max(1, Snapshot.PhysicalRecords.Count)
            Dim ProcessedRecords As Long = 0

            Dim CancellationToken = If(ProgressCallback Is Nothing, Nothing, New CancellationToken)
            For Each Pair In Snapshot.PhysicalRecords.OrderBy(Function(Item) Item.Key)
                If CancellationToken?.Cancel Then Return
                ThreadingCancellationToken.ThrowIfCancellationRequested()
                If Pair.Value.RefCount > 0 Then ValidatePhysicalRecordSnapshot(Snapshot, Pair.Value)
                ProcessedRecords += 1
                ProgressCallback?.Invoke(ProcessedRecords, TotalRecords, ProcessUnitTypes.Chunks, CancellationToken)
            Next

        End Sub

        Private Sub ValidatePhysicalRecordSnapshot(Snapshot As DiagnosticsSnapshot,
                                                   Record As PhysicalRecordEntry)

            If Record.RecordId <= SparsePhysicalRecordId Then Throw New InvalidDataException("Invalid physical record id.")
            If Record.PhysicalOffset < DataStartOffset Then Throw New InvalidDataException("Invalid physical record offset.")
            If Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException("Invalid physical record length.")

            Dim Buffer As Byte() = Nothing
            If Snapshot.StoredRecords.TryGetValue(Record.RecordId, Buffer) = False Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the captured backing stream.")
            If BitConverter.ToInt64(Buffer, 0) <> Record.RecordId Then Throw New InvalidDataException("Physical record id mismatch.")

            Dim EncryptionMethod = CType(BitConverter.ToInt32(Buffer, ChunkEncryptionMethodOffset), ChunkEncryptionMethods)
            Dim PayloadLength = BitConverter.ToInt32(Buffer, ChunkPayloadLengthOffset)
            Dim Flags = CType(BitConverter.ToInt32(Buffer, ChunkFlagsOffset), ChunkFlags)
            Dim CompressionEvaluatedPercent = CInt(Buffer(ChunkCompressionEvaluatedPercentOffset))

            If PayloadLength < 0 Then Throw New InvalidDataException("Invalid physical record payload length.")
            If ChunkRecordDataOffset + PayloadLength + MacSize <> Buffer.Length Then Throw New InvalidDataException("Invalid physical record length.")
            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then Throw New InvalidDataException($"Unsupported chunk flags for physical record {Record.RecordId}: {CInt(Flags)}.")
            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then Throw New InvalidDataException($"Invalid compression evaluated percent for physical record {Record.RecordId}: {CompressionEvaluatedPercent}.")

            Dim RecordMacKey = If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey, Snapshot.ChunkMacKey, PublicIntegrityKey)
            If RecordMacKey Is Nothing Then Throw New EncryptionMismatchException("Encrypted physical record exists but no file master key is available.")

            Using Hmac As New HMACSHA256(RecordMacKey)
                Dim ExpectedMac = Hmac.ComputeHash(Buffer, 0, ChunkRecordDataOffset + PayloadLength)
                If FixedTimeEquals(ExpectedMac, 0, Buffer, ChunkRecordDataOffset + PayloadLength, MacSize) = False Then Throw New CryptographicException($"Physical record MAC invalid for record {Record.RecordId}.")
            End Using

        End Sub

    End Class
End Namespace
