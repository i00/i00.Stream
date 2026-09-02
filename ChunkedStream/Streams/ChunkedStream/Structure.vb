' ================================================================================
' ChunkedStream Structure Snapshot API
' ================================================================================
'
' Purpose
'   - Provides an immutable diagnostic view of a ChunkedStream.
'   - Exposes the logical extent view and a master physical walk of the backing stream.
'
' Design
'   - Produces read-only snapshots of logical chunks, physical regions and metadata pages.
'   - Regions are ordered by physical offset and represent the authoritative file-layout view.
'   - Allocated logical chunks are associated with their physical-record regions.
'   - Sparse chunks remain available through Chunks because they have no physical region.
'   - Reads physical-record and metadata-page headers only.
'   - Does not read plaintext physical-record payloads.
'   - Does not decrypt or decompress physical-record contents.
'
' Features
'   - Extent and anchor metadata inspection.
'   - Complete physical-region inspection.
'   - Metadata-page location, capacity and utilisation inspection.
'   - Compression and encryption statistics.
'   - Fragmentation and hole analysis.
'
' Notes
'   - Intended for diagnostics, reporting, visualisation and debugging.
'   - Returned objects are immutable snapshots.
'   - Region physical names use PhysicalOffset, PhysicalLength and PhysicalEndOffset
'     consistently with chunk and metadata-page snapshots.
'
' ================================================================================
Imports System.Collections.ObjectModel
Imports System.IO

Namespace Streams

    Partial Class ChunkedStream

        Private NotInheritable Class StructureSnapshot
            Public LogicalLength As Long
            Public PhysicalLength As Long
            Public ChunkSize As Integer
            Public Extents As List(Of ExtentIndexEntry)
            Public PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
            Public PhysicalRecordHeaders As Dictionary(Of Long, ChunkHeaderSnapshot)
            Public MetadataRegions As List(Of MetadataRegionBuildInfo)
            Public WrapMode As MasterKeyWrapModes
            Public HasFileMasterKey As Boolean
            Public IsEncryptionEnabledForNewWrites As Boolean
            Public CurrentCompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CurrentCompressionRatioThreshold As Double
            Public CurrentStoreSparseChunks As Boolean
            Public HeaderSequence As Long
            Public ActiveHeaderCopy As Integer
            Public MetadataRootOffset As Long
            Public MetadataRootLength As Integer
            Public LiveDataEndOffset As Long
            Public LivePhysicalEndOffset As Long
        End Class

        Private Structure StructureChunkBuildInfo
            Public Index As Integer
            Public LogicalOffset As Long
            Public LogicalEndOffset As Long
            Public PlainLength As Integer
            Public IsAllocated As Boolean
            Public PhysicalRecordId As Long
            Public PhysicalRecordOffset As Integer
            Public AnchorId As Long
            Public PhysicalOffset As Long
            Public PhysicalLength As Integer
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedPercent As Integer
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PayloadLength As Integer
            Public ChunkFlags As ChunkFlags
        End Structure

        Private Structure PhysicalRecordStructureBuildInfo
            Public RecordId As Long
            Public PhysicalOffset As Long
            Public PhysicalLength As Integer
            Public PlainLength As Integer
            Public PayloadLength As Integer
            Public RefCount As Integer
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedPercent As Integer
            Public EncryptionMethod As ChunkEncryptionMethods
            Public ChunkFlags As ChunkFlags
            Public Chunks As IList(Of ChunkedStreamStructure.Chunk)
        End Structure

        Friend Structure ChunkHeaderSnapshot
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedPercent As Integer
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PlainLength As Integer
            Public PayloadLength As Integer
            Public ChunkFlags As ChunkFlags
        End Structure

        Private Structure MetadataRegionBuildInfo
            Public PhysicalOffset As Long
            Public PhysicalLength As Long
            Public RegionType As ChunkedStreamStructure.RegionTypes
            Public Description As String
            Public MetadataPage As ChunkedStreamStructure.MetadataPage
        End Structure

        ''' <summary>
        ''' Creates an immutable snapshot of the logical and physical stream structure.
        ''' </summary>
        ''' <returns>
        ''' A snapshot containing logical chunks, the ordered physical-region walk,
        ''' metadata-page utilisation and aggregate storage statistics.
        ''' </returns>
        Public Function GetStructure(Optional CancellationToken As Threading.CancellationToken = Nothing) As ChunkedStreamStructure

            Dim Snapshot As StructureSnapshot
            Using EnterStateLock()
                Snapshot = CaptureStructureSnapshotCore()
            End Using

            CancellationToken.ThrowIfCancellationRequested()

            Return GetStructureCore(Snapshot)

        End Function

        ''' <summary>
        ''' Asynchronously creates an immutable snapshot of the logical and physical stream
        ''' structure.
        ''' </summary>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        ''' <returns>A task producing the structure snapshot.</returns>
        Public Async Function GetStructureAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of ChunkedStreamStructure)
            Return Await Task.Run(
                Function()
                    Return GetStructure(CancellationToken)
                End Function, CancellationToken).ConfigureAwait(False)
        End Function

        Private Function CaptureStructureSnapshotCore() As StructureSnapshot

            ThrowIfDisposed()

            Dim Result = New StructureSnapshot With {
                .LogicalLength = _Length,
                .PhysicalLength = BaseStream.Length,
                .ChunkSize = _ChunkSize,
                .Extents = New List(Of ExtentIndexEntry)(_Extents),
                .PhysicalRecords = New Dictionary(Of Long, PhysicalRecordEntry)(_PhysicalRecords),
                .PhysicalRecordHeaders = New Dictionary(Of Long, ChunkHeaderSnapshot)(),
                .MetadataRegions = GetMetadataRegionBuildInfos(),
                .WrapMode = CType(BitConverter.ToInt32(_Header, MasterKeyWrapModeOffset), MasterKeyWrapModes),
                .HasFileMasterKey = _FileMasterKey IsNot Nothing,
                .IsEncryptionEnabledForNewWrites = _CurrentWriteEncryptionEnabled,
                .CurrentCompressionMethod = Options.CompressionMethod,
                .CurrentCompressionRatioThreshold = Options.CompressionRatioThreshold,
                .CurrentStoreSparseChunks = Options.StoreSparseChunks,
                .HeaderSequence = _HeaderSequence,
                .ActiveHeaderCopy = _ActiveHeaderCopy,
                .MetadataRootOffset = _MetadataRootOffset,
                .MetadataRootLength = _MetadataRootLength,
                .LiveDataEndOffset = GetDataEndFromIndex(),
                .LivePhysicalEndOffset = GetLivePhysicalEndOffset()
            }

            For Each Record In Result.PhysicalRecords.Values
                If Record.RefCount > 0 Then
                    Result.PhysicalRecordHeaders(Record.RecordId) = ReadPhysicalRecordHeaderSnapshot(Record)
                End If
            Next

            Return Result

        End Function

        Private Function GetStructureCore(Snapshot As StructureSnapshot) As ChunkedStreamStructure

            If Snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(Snapshot))

            Dim PhysicalRecordBuildInfos As New List(Of PhysicalRecordStructureBuildInfo)

            For Each Record In Snapshot.PhysicalRecords.Values.OrderBy(Function(x) x.RecordId)

                If Record.RefCount <= 0 Then Continue For

                Dim Header = Snapshot.PhysicalRecordHeaders(Record.RecordId)

                PhysicalRecordBuildInfos.Add(
                    New PhysicalRecordStructureBuildInfo With {
                        .RecordId = Record.RecordId,
                        .PhysicalOffset = Record.PhysicalOffset,
                        .PhysicalLength = Record.PhysicalLength,
                        .PlainLength = Header.PlainLength,
                        .PayloadLength = Header.PayloadLength,
                        .RefCount = Record.RefCount,
                        .CompressionMethod = Header.CompressionMethod,
                        .CompressionEvaluatedMethod = Header.CompressionEvaluatedMethod,
                        .CompressionEvaluatedPercent = Header.CompressionEvaluatedPercent,
                        .EncryptionMethod = Header.EncryptionMethod,
                        .ChunkFlags = Header.ChunkFlags
                    })

            Next

            Dim BuildInfos As New List(Of StructureChunkBuildInfo)(Snapshot.Extents.Count)

            Dim AllocatedChunkCount = 0
            Dim SparseChunkCount = 0
            Dim EncryptedChunkCount = 0
            Dim UnencryptedChunkCount = 0
            Dim CompressedChunkCount = 0
            Dim OutOfOrderChunkCount = 0

            Dim PhysicalChunkRecordBytes As Long = 0
            Dim PhysicalPayloadBytes As Long = 0

            Dim LogicalPayloadBytes As Long = 0
            Dim EncryptedLogicalBytes As Long = 0
            Dim CompressedLogicalBytes As Long = 0

            PhysicalChunkRecordBytes =
                PhysicalRecordBuildInfos.Sum(Function(record) CLng(record.PhysicalLength))

            PhysicalPayloadBytes =
                PhysicalRecordBuildInfos.Sum(Function(record) CLng(record.PayloadLength))

            For ExtentIndex = 0 To Snapshot.Extents.Count - 1

                Dim Extent = Snapshot.Extents(ExtentIndex)

                LogicalPayloadBytes += Extent.LogicalLength

                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then

                    SparseChunkCount += 1

                    BuildInfos.Add(
                        New StructureChunkBuildInfo With {
                            .Index = ExtentIndex,
                            .LogicalOffset = Extent.LogicalOffset,
                            .LogicalEndOffset = Extent.LogicalOffset + Extent.LogicalLength,
                            .PlainLength = Extent.LogicalLength,
                            .IsAllocated = False,
                            .PhysicalRecordId = SparsePhysicalRecordId,
                            .PhysicalRecordOffset = 0,
                            .AnchorId = Extent.AnchorId,
                            .PhysicalOffset = 0,
                            .PhysicalLength = 0,
                            .CompressionMethod = ChunkedStreamOptions.CompressionMethods.None,
                            .CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None,
                            .CompressionEvaluatedPercent = 100,
                            .EncryptionMethod = ChunkEncryptionMethods.None,
                            .PayloadLength = 0,
                            .ChunkFlags = ChunkFlags.PlaintextAllZero
                        })

                    Continue For

                End If

                Dim Record = Snapshot.PhysicalRecords(Extent.PhysicalRecordId)
                Dim Header = Snapshot.PhysicalRecordHeaders(Extent.PhysicalRecordId)

                AllocatedChunkCount += 1

                If Header.EncryptionMethod <> ChunkEncryptionMethods.None Then

                    EncryptedChunkCount += 1
                    EncryptedLogicalBytes += Extent.LogicalLength

                Else

                    UnencryptedChunkCount += 1

                End If

                If Header.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None Then

                    CompressedChunkCount += 1
                    CompressedLogicalBytes += Extent.LogicalLength

                End If

                BuildInfos.Add(
                    New StructureChunkBuildInfo With {
                        .Index = ExtentIndex,
                        .LogicalOffset = Extent.LogicalOffset,
                        .LogicalEndOffset = Extent.LogicalOffset + Extent.LogicalLength,
                        .PlainLength = Extent.LogicalLength,
                        .IsAllocated = True,
                        .PhysicalRecordId = Extent.PhysicalRecordId,
                        .PhysicalRecordOffset = Extent.PhysicalRecordOffset,
                        .AnchorId = Extent.AnchorId,
                        .PhysicalOffset = Record.PhysicalOffset,
                        .PhysicalLength = Record.PhysicalLength,
                        .CompressionMethod = Header.CompressionMethod,
                        .CompressionEvaluatedMethod = Header.CompressionEvaluatedMethod,
                        .CompressionEvaluatedPercent = Header.CompressionEvaluatedPercent,
                        .EncryptionMethod = Header.EncryptionMethod,
                        .PayloadLength = Header.PayloadLength,
                        .ChunkFlags = Header.ChunkFlags
                    })

            Next

            Dim PreviousGapByRecordId As New Dictionary(Of Long, Long)
            Dim NextGapByRecordId As New Dictionary(Of Long, Long)
            Dim PhysicalOrderByRecordId As New Dictionary(Of Long, Integer)

            Dim ContiguousWithPreviousByRecordId As New Dictionary(Of Long, Boolean)
            Dim ContiguousWithNextByRecordId As New Dictionary(Of Long, Boolean)

            Dim InPhysicalRecordOrderByRecordId As New Dictionary(Of Long, Boolean)

            Dim AllocatedRecordsInPhysicalOrder =
                PhysicalRecordBuildInfos.
                OrderBy(Function(record) record.PhysicalOffset).
                ThenBy(Function(record) record.RecordId).
                ToList()

            For PhysicalOrder = 0 To AllocatedRecordsInPhysicalOrder.Count - 1

                Dim Current = AllocatedRecordsInPhysicalOrder(PhysicalOrder)

                PhysicalOrderByRecordId(Current.RecordId) = PhysicalOrder

                If PhysicalOrder = 0 Then

                    PreviousGapByRecordId(Current.RecordId) =
                        Math.Max(0L, Current.PhysicalOffset - DataStartOffset)

                    ContiguousWithPreviousByRecordId(Current.RecordId) =
                        Current.PhysicalOffset = DataStartOffset

                    InPhysicalRecordOrderByRecordId(Current.RecordId) = True

                Else

                    Dim Previous = AllocatedRecordsInPhysicalOrder(PhysicalOrder - 1)

                    Dim PreviousEnd =
                        Previous.PhysicalOffset +
                        Previous.PhysicalLength

                    Dim PreviousGap =
                        Math.Max(0L, Current.PhysicalOffset - PreviousEnd)

                    Dim IsInPhysicalRecordOrder =
                        Previous.RecordId < Current.RecordId

                    PreviousGapByRecordId(Current.RecordId) = PreviousGap

                    ContiguousWithPreviousByRecordId(Current.RecordId) =
                        PreviousGap = 0

                    InPhysicalRecordOrderByRecordId(Current.RecordId) =
                        IsInPhysicalRecordOrder

                    If IsInPhysicalRecordOrder = False Then
                        OutOfOrderChunkCount += 1
                    End If

                End If

                If PhysicalOrder = AllocatedRecordsInPhysicalOrder.Count - 1 Then

                    Dim CurrentEnd =
                        Current.PhysicalOffset +
                        Current.PhysicalLength

                    Dim NextGap =
                        Math.Max(0L, Snapshot.LivePhysicalEndOffset - CurrentEnd)

                    NextGapByRecordId(Current.RecordId) = NextGap

                    ContiguousWithNextByRecordId(Current.RecordId) =
                        NextGap = 0

                Else

                    Dim NextEntry =
                        AllocatedRecordsInPhysicalOrder(PhysicalOrder + 1)

                    Dim CurrentEnd =
                        Current.PhysicalOffset +
                        Current.PhysicalLength

                    Dim NextGap =
                        Math.Max(0L, NextEntry.PhysicalOffset - CurrentEnd)

                    NextGapByRecordId(Current.RecordId) = NextGap

                    ContiguousWithNextByRecordId(Current.RecordId) =
                        NextGap = 0

                End If

            Next

            Dim Chunks As New List(Of ChunkedStreamStructure.Chunk)(BuildInfos.Count)

            For Each BuildInfo In BuildInfos

                Dim PhysicalOffset As Long? = Nothing
                Dim PhysicalLength As Integer? = Nothing
                Dim PhysicalEndOffset As Long? = Nothing

                Dim PhysicalOrder As Integer? = Nothing

                Dim PreviousPhysicalGap As Long? = Nothing
                Dim NextPhysicalGap As Long? = Nothing

                Dim IsPhysicallyContiguousWithPrevious = False
                Dim IsPhysicallyContiguousWithNext = False
                Dim IsInLogicalOrder = False

                If BuildInfo.IsAllocated Then

                    PhysicalOffset = BuildInfo.PhysicalOffset
                    PhysicalLength = BuildInfo.PhysicalLength

                    PhysicalEndOffset =
                        BuildInfo.PhysicalOffset +
                        BuildInfo.PhysicalLength

                    PhysicalOrder =
                        PhysicalOrderByRecordId(BuildInfo.PhysicalRecordId)

                    PreviousPhysicalGap =
                        PreviousGapByRecordId(BuildInfo.PhysicalRecordId)

                    NextPhysicalGap =
                        NextGapByRecordId(BuildInfo.PhysicalRecordId)

                    IsPhysicallyContiguousWithPrevious =
                        ContiguousWithPreviousByRecordId(BuildInfo.PhysicalRecordId)

                    IsPhysicallyContiguousWithNext =
                        ContiguousWithNextByRecordId(BuildInfo.PhysicalRecordId)

                    IsInLogicalOrder =
                        InPhysicalRecordOrderByRecordId(BuildInfo.PhysicalRecordId)

                End If

                Chunks.Add(
                    New ChunkedStreamStructure.Chunk(
                        Index:=BuildInfo.Index,
                        LogicalOffset:=BuildInfo.LogicalOffset,
                        LogicalEndOffset:=BuildInfo.LogicalEndOffset,
                        PlainLength:=BuildInfo.PlainLength,
                        IsSparse:=Not BuildInfo.IsAllocated,
                        IsAllocated:=BuildInfo.IsAllocated,
                        PhysicalOffset:=PhysicalOffset,
                        PhysicalLength:=PhysicalLength,
                        PhysicalEndOffset:=PhysicalEndOffset,
                        PhysicalOrder:=PhysicalOrder,
                        CompressionMethod:=BuildInfo.CompressionMethod,
                        CompressionEvaluatedMethod:=BuildInfo.CompressionEvaluatedMethod,
                        CompressionEvaluatedPercent:=BuildInfo.CompressionEvaluatedPercent,
                        EncryptionMethod:=BuildInfo.EncryptionMethod,
                        PayloadLength:=BuildInfo.PayloadLength,
                        ChunkFlags:=BuildInfo.ChunkFlags,
                        PreviousPhysicalGap:=PreviousPhysicalGap,
                        NextPhysicalGap:=NextPhysicalGap,
                        IsPhysicallyContiguousWithPrevious:=BuildInfo.IsAllocated AndAlso IsPhysicallyContiguousWithPrevious,
                        IsPhysicallyContiguousWithNext:=BuildInfo.IsAllocated AndAlso IsPhysicallyContiguousWithNext,
                        IsInLogicalOrder:=BuildInfo.IsAllocated AndAlso IsInLogicalOrder,
                        PhysicalRecordId:=BuildInfo.PhysicalRecordId,
                        PhysicalRecordOffset:=BuildInfo.PhysicalRecordOffset,
                        AnchorId:=BuildInfo.AnchorId))

            Next

            Dim ChunksByPhysicalRecordId =
                Chunks.
                Where(Function(chunk) chunk.IsAllocated AndAlso chunk.PhysicalRecordId.HasValue).
                GroupBy(Function(chunk) chunk.PhysicalRecordId.Value).
                ToDictionary(Function(group) group.Key,
                                Function(group) DirectCast(group.OrderBy(Function(chunk) chunk.Index).ToList(), IList(Of ChunkedStreamStructure.Chunk)))

            For Index = 0 To PhysicalRecordBuildInfos.Count - 1
                Dim BuildInfo = PhysicalRecordBuildInfos(Index)
                Dim RecordChunks As IList(Of ChunkedStreamStructure.Chunk) = Nothing

                If ChunksByPhysicalRecordId.TryGetValue(BuildInfo.RecordId, RecordChunks) = False Then
                    RecordChunks = New List(Of ChunkedStreamStructure.Chunk)()
                End If

                BuildInfo.Chunks = RecordChunks
                PhysicalRecordBuildInfos(Index) = BuildInfo
            Next

            Dim MetadataRegions = Snapshot.MetadataRegions

            Dim PhysicalHeaderBytes = CLng(DataStartOffset)

            Dim PhysicalDataAreaBytes =
                Math.Max(0L,
                            Snapshot.LivePhysicalEndOffset - DataStartOffset)

            Dim FragmentedBytes =
                CalculateFragmentedBytes(
                    AllocatedRecordsInPhysicalOrder,
                    MetadataRegions,
                    Snapshot.LivePhysicalEndOffset)

            Dim PhysicalChunkOverheadBytes =
                Math.Max(0L,
                            PhysicalChunkRecordBytes -
                            PhysicalPayloadBytes)

            Dim MetadataRootBytes =
                MetadataRegions.
                Where(Function(region)
                          Return region.RegionType =
                                    ChunkedStreamStructure.RegionTypes.MetadataRoot
                      End Function).
                Sum(Function(region) region.PhysicalLength)

            Dim IndexPageBytes =
                MetadataRegions.
                Where(Function(region)
                          Return region.RegionType =
                                    ChunkedStreamStructure.RegionTypes.IndexPage
                      End Function).
                Sum(Function(region) region.PhysicalLength)

            Dim DirectoryPageBytes =
                MetadataRegions.
                Where(Function(region)
                          Return region.RegionType =
                                        ChunkedStreamStructure.RegionTypes.ChunkIndexDirectoryPage OrElse
                                    region.RegionType =
                                        ChunkedStreamStructure.RegionTypes.HoleDirectoryPage
                      End Function).
                Sum(Function(region) region.PhysicalLength)

            Dim HoleDirectoryBytes =
                MetadataRegions.
                Where(Function(region)
                          Return region.RegionType =
                                    ChunkedStreamStructure.RegionTypes.HoleDirectoryPage
                      End Function).
                Sum(Function(region) region.PhysicalLength)

            Dim PhysicalMetadataBytes =
                MetadataRootBytes +
                IndexPageBytes +
                DirectoryPageBytes

            Dim TotalStructuralOverheadBytes =
                PhysicalHeaderBytes +
                PhysicalMetadataBytes +
                PhysicalChunkOverheadBytes

            Dim Regions =
                BuildPhysicalRegions(
                    PhysicalRecordBuildInfos,
                    MetadataRegions,
                    Snapshot.PhysicalLength)

            Dim HoleRegions =
                Regions.
                Where(Function(region)
                          Return region.RegionType =
                                    ChunkedStreamStructure.RegionTypes.Hole
                      End Function).
                ToList()

            Dim HoleCount = HoleRegions.Count

            Dim LargestHoleBytes =
                If(HoleCount = 0,
                    0L,
                    HoleRegions.Max(Function(region) region.PhysicalLength))

            Dim AverageHoleBytes =
                If(HoleCount = 0,
                    0L,
                    CLng(HoleRegions.Average(Function(region) CDbl(region.PhysicalLength))))

            Dim WrapMode = Snapshot.WrapMode

            Dim LiveDataEndOffset = Snapshot.LiveDataEndOffset

            Dim LivePhysicalEndOffset =
                Snapshot.LivePhysicalEndOffset

            Return New ChunkedStreamStructure(
                LogicalLength:=Snapshot.LogicalLength,
                PhysicalLength:=Snapshot.PhysicalLength,
                ChunkSize:=Snapshot.ChunkSize,
                ChunkCount:=Snapshot.Extents.Count,
                AllocatedChunkCount:=AllocatedChunkCount,
                SparseChunkCount:=SparseChunkCount,
                EncryptedChunkCount:=EncryptedChunkCount,
                UnencryptedChunkCount:=UnencryptedChunkCount,
                CompressedChunkCount:=CompressedChunkCount,
                OutOfOrderChunkCount:=OutOfOrderChunkCount,
                PhysicalHeaderBytes:=PhysicalHeaderBytes,
                PhysicalDataAreaBytes:=PhysicalDataAreaBytes,
                PhysicalChunkRecordBytes:=PhysicalChunkRecordBytes,
                PhysicalPayloadBytes:=PhysicalPayloadBytes,
                PhysicalChunkOverheadBytes:=PhysicalChunkOverheadBytes,
                PhysicalMetadataBytes:=PhysicalMetadataBytes,
                MetadataRootBytes:=MetadataRootBytes,
                IndexPageBytes:=IndexPageBytes,
                DirectoryPageBytes:=DirectoryPageBytes,
                HoleDirectoryBytes:=HoleDirectoryBytes,
                TotalStructuralOverheadBytes:=TotalStructuralOverheadBytes,
                FragmentedBytes:=FragmentedBytes,
                LogicalPayloadBytes:=LogicalPayloadBytes,
                EncryptedLogicalBytes:=EncryptedLogicalBytes,
                CompressedLogicalBytes:=CompressedLogicalBytes,
                HoleCount:=HoleCount,
                LargestHoleBytes:=LargestHoleBytes,
                AverageHoleBytes:=AverageHoleBytes,
                HasFileMasterKey:=Snapshot.HasFileMasterKey,
                HasWrappedFileMasterKey:=WrapMode <> MasterKeyWrapModes.None,
                IsFileMasterKeyPubliclyWrapped:=WrapMode = MasterKeyWrapModes.PublicWrap,
                IsFileMasterKeyUserWrapped:=WrapMode = MasterKeyWrapModes.UserWrap,
                IsEncryptionEnabledForNewWrites:=Snapshot.IsEncryptionEnabledForNewWrites,
                CurrentCompressionMethod:=Snapshot.CurrentCompressionMethod,
                CurrentCompressionRatioThreshold:=Snapshot.CurrentCompressionRatioThreshold,
                CurrentStoreSparseChunks:=Snapshot.CurrentStoreSparseChunks,
                HeaderSequence:=Snapshot.HeaderSequence,
                ActiveHeaderCopy:=Snapshot.ActiveHeaderCopy,
                MetadataRootOffset:=Snapshot.MetadataRootOffset,
                MetadataRootEndOffset:=If(Snapshot.MetadataRootOffset > 0 AndAlso
                                            Snapshot.MetadataRootLength > 0,
                                            Snapshot.MetadataRootOffset + Snapshot.MetadataRootLength,
                                            0L),
                DataStartOffset:=DataStartOffset,
                DataAreaEndOffset:=LivePhysicalEndOffset,
                LiveDataEndOffset:=LiveDataEndOffset,
                SparseChunks:=Chunks.Where(Function(chunk) chunk.IsSparse).ToList(),
                Regions:=Regions)

        End Function

        Private Function ReadPhysicalRecordHeaderSnapshot(Record As PhysicalRecordEntry) As ChunkHeaderSnapshot

            If Record.RecordId <= SparsePhysicalRecordId Then
                Throw New InvalidDataException("Invalid physical record id.")
            End If

            If Record.PhysicalOffset < DataStartOffset Then
                Throw New InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            End If

            If Record.PhysicalLength < MinChunkRecordSize Then
                Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            End If

            If Record.PhysicalOffset + Record.PhysicalLength > BaseStream.Length Then
                Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")
            End If

            Dim Header(ChunkRecordHeaderSize - 1) As Byte

            ReadAt(Record.PhysicalOffset, Header, 0, Header.Length)

            If IsKnownMetadataMagic(Header) Then
                Throw New InvalidDataException($"Physical record {Record.RecordId} points at metadata instead of a physical record.")
            End If

            Dim StoredRecordId = BitConverter.ToInt64(Header, 0)

            If StoredRecordId <> Record.RecordId Then
                Throw New InvalidDataException($"Physical record id mismatch. Expected {Record.RecordId}, found {StoredRecordId}.")
            End If

            Dim CompressionMethod =
                CType(BitConverter.ToInt32(Header, ChunkCompressionMethodOffset),
                      ChunkedStreamOptions.CompressionMethods)

            Dim CompressionEvaluatedMethod =
                CType(BitConverter.ToInt32(Header, ChunkCompressionEvaluatedMethodOffset),
                      ChunkedStreamOptions.CompressionMethods)

            Dim CompressionEvaluatedPercent =
                CInt(Header(ChunkCompressionEvaluatedPercentOffset))

            Dim EncryptionMethod =
                CType(BitConverter.ToInt32(Header, ChunkEncryptionMethodOffset),
                      ChunkEncryptionMethods)

            Dim PlainLength = BitConverter.ToInt32(Header, ChunkPlainLengthOffset)
            Dim PayloadLength = BitConverter.ToInt32(Header, ChunkPayloadLengthOffset)

            Dim Flags =
                CType(BitConverter.ToInt32(Header, ChunkFlagsOffset),
                      ChunkFlags)

            If PlainLength < 0 Then
                Throw New InvalidDataException($"Invalid plain length for physical record {Record.RecordId}.")
            End If

            If PlainLength <> Record.PlainLength Then
                Throw New InvalidDataException($"Physical record plain length mismatch for record {Record.RecordId}.")
            End If

            If PayloadLength < 0 Then
                Throw New InvalidDataException($"Invalid payload length for physical record {Record.RecordId}.")
            End If

            If ChunkRecordDataOffset + PayloadLength + MacSize <> Record.PhysicalLength Then
                Throw New InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            End If

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported physical record flags for record {Record.RecordId}: {CInt(Flags)}.")
            End If

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
               CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then
                Throw New InvalidDataException($"Invalid compression evaluated percent for physical record {Record.RecordId}: {CompressionEvaluatedPercent}.")
            End If

            Return New ChunkHeaderSnapshot With {
                .CompressionMethod = CompressionMethod,
                .CompressionEvaluatedMethod = CompressionEvaluatedMethod,
                .CompressionEvaluatedPercent = CompressionEvaluatedPercent,
                .EncryptionMethod = EncryptionMethod,
                .PlainLength = PlainLength,
                .PayloadLength = PayloadLength,
                .ChunkFlags = Flags
            }

        End Function

        Private Function IsKnownMetadataMagic(Header As Byte()) As Boolean

            If Header Is Nothing OrElse Header.Length < 8 Then Return False

            If FixedTimeEquals(IndexPageMagic, 0, Header, 0, IndexPageMagic.Length) Then Return True
            If FixedTimeEquals(DirectoryPageMagic, 0, Header, 0, DirectoryPageMagic.Length) Then Return True
            If FixedTimeEquals(MetadataRootMagic, 0, Header, 0, MetadataRootMagic.Length) Then Return True

            Return False

        End Function

        ''' <summary>
        ''' Builds a physical-region description of all persisted metadata structures.
        ''' </summary>
        Private Function GetMetadataRegionBuildInfos() As List(Of MetadataRegionBuildInfo)

            Dim Result As New List(Of MetadataRegionBuildInfo)()

            For Each Descriptor In _ExtentPageDescriptors.Values
                AddMetadataRegion(Result,
                                  Descriptor,
                                  ChunkedStreamStructure.MetadataPageTypes.ExtentPage,
                                  ChunkedStreamStructure.RegionTypes.IndexPage,
                                  $"Extent Page {Descriptor.PageNumber}",
                                  ExtentEntrySize,
                                  _IndexPageEntryCount)
            Next

            For Each Descriptor In _PhysicalRecordPageDescriptors.Values
                AddMetadataRegion(Result,
                                  Descriptor,
                                  ChunkedStreamStructure.MetadataPageTypes.PhysicalRecordPage,
                                  ChunkedStreamStructure.RegionTypes.IndexPage,
                                  $"Physical Record Page {Descriptor.PageNumber}",
                                  PhysicalRecordEntrySize,
                                  _IndexPageEntryCount)
            Next

            For Each Descriptor In _ExtentDirectoryPageDescriptors.Values
                AddMetadataRegion(Result,
                                  Descriptor,
                                  ChunkedStreamStructure.MetadataPageTypes.ExtentDirectoryPage,
                                  ChunkedStreamStructure.RegionTypes.ChunkIndexDirectoryPage,
                                  $"Extent Directory Page {Descriptor.PageNumber}",
                                  Nothing,
                                  _IndexDirectoryEntryCount)
            Next

            For Each Descriptor In _PhysicalRecordDirectoryPageDescriptors.Values
                AddMetadataRegion(Result,
                                  Descriptor,
                                  ChunkedStreamStructure.MetadataPageTypes.PhysicalRecordDirectoryPage,
                                  ChunkedStreamStructure.RegionTypes.ChunkIndexDirectoryPage,
                                  $"Physical Record Directory Page {Descriptor.PageNumber}",
                                  Nothing,
                                  _IndexDirectoryEntryCount)
            Next

            For Each Descriptor In _HoleDirectoryPageDescriptors.Values
                AddMetadataRegion(Result,
                                  Descriptor,
                                  ChunkedStreamStructure.MetadataPageTypes.HoleDirectoryPage,
                                  ChunkedStreamStructure.RegionTypes.HoleDirectoryPage,
                                  $"Hole Directory Page {Descriptor.PageNumber}",
                                  Nothing,
                                  _IndexDirectoryEntryCount)
            Next

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then
                Dim MetadataPage =
                    New ChunkedStreamStructure.MetadataPage(
                        MetadataPageType:=ChunkedStreamStructure.MetadataPageTypes.MetadataRoot,
                        PageNumber:=Nothing,
                        PhysicalOffset:=_MetadataRootOffset,
                        PhysicalLength:=_MetadataRootLength,
                        EntriesUsed:=Nothing,
                        EntryCapacity:=Nothing,
                        EntrySize:=Nothing,
                        Description:="Metadata Root")

                Result.Add(
                    New MetadataRegionBuildInfo With {
                        .PhysicalOffset = _MetadataRootOffset,
                        .PhysicalLength = _MetadataRootLength,
                        .RegionType = ChunkedStreamStructure.RegionTypes.MetadataRoot,
                        .Description = "Metadata Root",
                        .MetadataPage = MetadataPage
                    })
            End If

            Return Result

        End Function

        Private Sub AddMetadataRegion(Result As IList(Of MetadataRegionBuildInfo),
                                      Descriptor As MetadataPageDescriptor,
                                      MetadataPageType As ChunkedStreamStructure.MetadataPageTypes,
                                      RegionType As ChunkedStreamStructure.RegionTypes,
                                      Description As String,
                                      EntrySize As Integer?,
                                      EntryCapacity As Integer)

            If Result Is Nothing Then Throw New ArgumentNullException(NameOf(Result))
            If Descriptor.Offset <= 0 OrElse Descriptor.Length <= 0 Then Return

            Dim EntriesUsed = ReadMetadataPageEntryCount(Descriptor, MetadataPageType)
            Dim MetadataPage =
                New ChunkedStreamStructure.MetadataPage(
                    MetadataPageType:=MetadataPageType,
                    PageNumber:=Descriptor.PageNumber,
                    PhysicalOffset:=Descriptor.Offset,
                    PhysicalLength:=Descriptor.Length,
                    EntriesUsed:=EntriesUsed,
                    EntryCapacity:=EntryCapacity,
                    EntrySize:=EntrySize,
                    Description:=Description)

            Result.Add(
                New MetadataRegionBuildInfo With {
                    .PhysicalOffset = Descriptor.Offset,
                    .PhysicalLength = Descriptor.Length,
                    .RegionType = RegionType,
                    .Description = Description,
                    .MetadataPage = MetadataPage
                })

        End Sub

        Private Function ReadMetadataPageEntryCount(Descriptor As MetadataPageDescriptor,
                                                     MetadataPageType As ChunkedStreamStructure.MetadataPageTypes) As Integer

            Dim EntryCountOffset As Integer

            Select Case MetadataPageType
                Case ChunkedStreamStructure.MetadataPageTypes.ExtentPage,
                     ChunkedStreamStructure.MetadataPageTypes.PhysicalRecordPage
                    EntryCountOffset = 20
                Case ChunkedStreamStructure.MetadataPageTypes.ExtentDirectoryPage,
                     ChunkedStreamStructure.MetadataPageTypes.PhysicalRecordDirectoryPage,
                     ChunkedStreamStructure.MetadataPageTypes.HoleDirectoryPage
                    EntryCountOffset = 16
                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(MetadataPageType))
            End Select

            If Descriptor.Length < EntryCountOffset + 4 Then
                Throw New InvalidDataException($"Metadata page {Descriptor.PageNumber} is too short to contain its entry count.")
            End If

            Dim Buffer(3) As Byte
            ReadAt(Descriptor.Offset + EntryCountOffset, Buffer, 0, Buffer.Length)

            Dim Result = BitConverter.ToInt32(Buffer, 0)

            If Result < 0 Then
                Throw New InvalidDataException($"Metadata page {Descriptor.PageNumber} has an invalid entry count.")
            End If

            Return Result

        End Function

        Private Function GetLivePhysicalEndOffset() As Long

            Dim Result = GetDataEndFromIndex()

            For Each MetadataRegion In GetMetadataRegionBuildInfos()
                Result = Math.Max(Result, MetadataRegion.PhysicalOffset + MetadataRegion.PhysicalLength)
            Next

            Return Math.Max(Result, CLng(DataStartOffset))

        End Function

        Private Shared Function CalculateFragmentedBytes(AllocatedRecords As IEnumerable(Of PhysicalRecordStructureBuildInfo),
                                                         MetadataRegions As IEnumerable(Of MetadataRegionBuildInfo),
                                                         LivePhysicalEndOffset As Long) As Long

            If AllocatedRecords Is Nothing Then Throw New ArgumentNullException(NameOf(AllocatedRecords))
            If MetadataRegions Is Nothing Then Throw New ArgumentNullException(NameOf(MetadataRegions))

            Dim UsedBytes = AllocatedRecords.Sum(Function(entry) CLng(entry.PhysicalLength)) +
                            MetadataRegions.Sum(Function(entry) entry.PhysicalLength)

            Dim PhysicalDataAreaBytes = Math.Max(0L, LivePhysicalEndOffset - DataStartOffset)
            Return Math.Max(0L, PhysicalDataAreaBytes - UsedBytes)

        End Function

        Private Shared Function BuildPhysicalRegions(AllocatedRecords As IEnumerable(Of PhysicalRecordStructureBuildInfo),
                                                     MetadataRegions As IEnumerable(Of MetadataRegionBuildInfo),
                                                     PhysicalLength As Long) As List(Of ChunkedStreamStructure.Region)

            If AllocatedRecords Is Nothing Then Throw New ArgumentNullException(NameOf(AllocatedRecords))
            If MetadataRegions Is Nothing Then Throw New ArgumentNullException(NameOf(MetadataRegions))

            Dim Regions As New List(Of ChunkedStreamStructure.Region) From {
                New ChunkedStreamStructure.Region(PhysicalOffset:=0,
                                                  PhysicalLength:=HeaderSize,
                                                  RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                                                  Chunks:=Nothing,
                                                  MetadataPage:=Nothing,
                                                  Description:="Header A"),
                New ChunkedStreamStructure.Region(PhysicalOffset:=HeaderSize,
                                                  PhysicalLength:=HeaderSize,
                                                  RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                                                  Chunks:=Nothing,
                                                  MetadataPage:=Nothing,
                                                  Description:="Header B")
            }

            Dim PhysicalSegments As New List(Of ChunkedStreamStructure.Region)()

            For Each Entry In AllocatedRecords
                PhysicalSegments.Add(
                    New ChunkedStreamStructure.Region(
                        PhysicalOffset:=Entry.PhysicalOffset,
                        PhysicalLength:=Entry.PhysicalLength,
                        RegionType:=ChunkedStreamStructure.RegionTypes.Chunk,
                        Chunks:=Entry.Chunks,
                        MetadataPage:=Nothing,
                        Description:=$"Physical Record {Entry.RecordId}"))
            Next

            For Each MetadataRegion In MetadataRegions
                PhysicalSegments.Add(
                    New ChunkedStreamStructure.Region(
                        PhysicalOffset:=MetadataRegion.PhysicalOffset,
                        PhysicalLength:=MetadataRegion.PhysicalLength,
                        RegionType:=MetadataRegion.RegionType,
                        Chunks:=Nothing,
                        MetadataPage:=MetadataRegion.MetadataPage,
                        Description:=MetadataRegion.Description))
            Next

            PhysicalSegments = PhysicalSegments.OrderBy(Function(region) region.PhysicalOffset).
                                                ThenBy(Function(region) region.PhysicalEndOffset).
                                                ToList()

            Dim Cursor = CLng(DataStartOffset)

            For Each Segment In PhysicalSegments
                If Segment.PhysicalOffset > Cursor Then
                    Regions.Add(
                        New ChunkedStreamStructure.Region(
                            PhysicalOffset:=Cursor,
                            PhysicalLength:=Segment.PhysicalOffset - Cursor,
                            RegionType:=ChunkedStreamStructure.RegionTypes.Hole,
                            Chunks:=Nothing,
                            MetadataPage:=Nothing,
                            Description:="Unreferenced data area"))
                ElseIf Segment.PhysicalOffset < Cursor Then
                    Regions.Add(
                        New ChunkedStreamStructure.Region(
                            PhysicalOffset:=Segment.PhysicalOffset,
                            PhysicalLength:=Segment.PhysicalLength,
                            RegionType:=ChunkedStreamStructure.RegionTypes.Unknown,
                            Chunks:=Nothing,
                            MetadataPage:=Nothing,
                            Description:=$"Overlapping region: {Segment.Description}"))

                    Cursor = Math.Max(Cursor, Segment.PhysicalEndOffset)
                    Continue For
                End If

                Regions.Add(Segment)
                Cursor = Math.Max(Cursor, Segment.PhysicalEndOffset)
            Next

            If PhysicalLength > Cursor Then
                Regions.Add(
                    New ChunkedStreamStructure.Region(
                        PhysicalOffset:=Cursor,
                        PhysicalLength:=PhysicalLength - Cursor,
                        RegionType:=ChunkedStreamStructure.RegionTypes.Unused,
                        Chunks:=Nothing,
                        MetadataPage:=Nothing,
                        Description:="Unused trailing space"))
            End If

            Return Regions.OrderBy(Function(region) region.PhysicalOffset).
                           ThenBy(Function(region) region.PhysicalEndOffset).
                           ToList()

        End Function
    End Class

    ''' <summary>
    ''' Immutable read-only snapshot of a ChunkedStream's logical and physical structure.
    ''' </summary>
    Public NotInheritable Class ChunkedStreamStructure

        ''' <summary>
        ''' Physical region classification within the backing stream.
        ''' </summary>
        Public Enum RegionTypes

            ''' <summary>
            ''' Header A or Header B.
            ''' </summary>
            Header = 0

            ''' <summary>
            ''' Live physical record referenced by the current extent table.
            ''' </summary>
            Chunk = 1

            ''' <summary>
            ''' Unreferenced space located within the active data or metadata area.
            ''' </summary>
            Hole = 2

            ''' <summary>
            ''' Reserved for legacy flat-index formats.
            ''' Current paged-metadata streams do not emit this region type.
            ''' </summary>
            Index = 3

            ''' <summary>
            ''' Bytes beyond the active structure.
            ''' </summary>
            Unused = 4

            ''' <summary>
            ''' Authenticated extent or physical-record metadata page.
            ''' </summary>
            IndexPage = 5

            ''' <summary>
            ''' Authenticated extent or physical-record directory page.
            ''' </summary>
            ChunkIndexDirectoryPage = 6

            ''' <summary>
            ''' Authenticated hole-directory page.
            ''' </summary>
            HoleDirectoryPage = 7

            ''' <summary>
            ''' Authenticated metadata root record.
            ''' </summary>
            MetadataRoot = 8

            ''' <summary>
            ''' Region could not be classified.
            ''' </summary>
            Unknown = 255

        End Enum

        ''' <summary>
        ''' Classification of an active persisted metadata structure.
        ''' </summary>
        Public Enum MetadataPageTypes
            ''' <summary>
            ''' Authenticated metadata root record.
            ''' </summary>
            MetadataRoot = 0
            ''' <summary>
            ''' Authenticated logical extent page.
            ''' </summary>
            ExtentPage = 1
            ''' <summary>
            ''' Authenticated physical-record page.
            ''' </summary>
            PhysicalRecordPage = 2
            ''' <summary>
            ''' Authenticated extent-page directory page.
            ''' </summary>
            ExtentDirectoryPage = 3
            ''' <summary>
            ''' Authenticated physical-record-page directory page.
            ''' </summary>
            PhysicalRecordDirectoryPage = 4
            ''' <summary>
            ''' Authenticated persisted hole-directory page.
            ''' </summary>
            HoleDirectoryPage = 5
        End Enum

        Friend Sub New(LogicalLength As Long,
                       PhysicalLength As Long,
                       ChunkSize As Integer,
                       ChunkCount As Integer,
                       AllocatedChunkCount As Integer,
                       SparseChunkCount As Integer,
                       EncryptedChunkCount As Integer,
                       UnencryptedChunkCount As Integer,
                       CompressedChunkCount As Integer,
                       OutOfOrderChunkCount As Integer,
                       PhysicalHeaderBytes As Long,
                       PhysicalDataAreaBytes As Long,
                       PhysicalChunkRecordBytes As Long,
                       PhysicalPayloadBytes As Long,
                       PhysicalChunkOverheadBytes As Long,
                       PhysicalMetadataBytes As Long,
                       MetadataRootBytes As Long,
                       IndexPageBytes As Long,
                       DirectoryPageBytes As Long,
                       HoleDirectoryBytes As Long,
                       TotalStructuralOverheadBytes As Long,
                       FragmentedBytes As Long,
                       LogicalPayloadBytes As Long,
                       EncryptedLogicalBytes As Long,
                       CompressedLogicalBytes As Long,
                       HoleCount As Integer,
                       LargestHoleBytes As Long,
                       AverageHoleBytes As Long,
                       HasFileMasterKey As Boolean,
                       HasWrappedFileMasterKey As Boolean,
                       IsFileMasterKeyPubliclyWrapped As Boolean,
                       IsFileMasterKeyUserWrapped As Boolean,
                       IsEncryptionEnabledForNewWrites As Boolean,
                       CurrentCompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods,
                       CurrentCompressionRatioThreshold As Double,
                       CurrentStoreSparseChunks As Boolean,
                       HeaderSequence As Long,
                       ActiveHeaderCopy As Integer,
                       MetadataRootOffset As Long,
                       MetadataRootEndOffset As Long,
                       DataStartOffset As Long,
                       DataAreaEndOffset As Long,
                       LiveDataEndOffset As Long,
                       SparseChunks As IList(Of Chunk),
                       Regions As IList(Of Region))

            Me.LogicalLength = LogicalLength
            Me.PhysicalLength = PhysicalLength
            Me.ChunkSize = ChunkSize
            Me.ChunkCount = ChunkCount
            Me.AllocatedChunkCount = AllocatedChunkCount
            Me.SparseChunkCount = SparseChunkCount
            Me.EncryptedChunkCount = EncryptedChunkCount
            Me.UnencryptedChunkCount = UnencryptedChunkCount
            Me.CompressedChunkCount = CompressedChunkCount
            Me.OutOfOrderChunkCount = OutOfOrderChunkCount

            Me.PhysicalHeaderBytes = PhysicalHeaderBytes
            Me.PhysicalDataAreaBytes = PhysicalDataAreaBytes

            Me.PhysicalChunkRecordBytes = PhysicalChunkRecordBytes
            Me.PhysicalPayloadBytes = PhysicalPayloadBytes
            Me.PhysicalChunkOverheadBytes = PhysicalChunkOverheadBytes

            Me.PhysicalMetadataBytes = PhysicalMetadataBytes
            Me.MetadataRootBytes = MetadataRootBytes
            Me.IndexPageBytes = IndexPageBytes
            Me.DirectoryPageBytes = DirectoryPageBytes
            Me.HoleDirectoryBytes = HoleDirectoryBytes

            Me.TotalStructuralOverheadBytes = TotalStructuralOverheadBytes

            Me.FragmentedBytes = FragmentedBytes

            Me.LogicalPayloadBytes = LogicalPayloadBytes
            Me.EncryptedLogicalBytes = EncryptedLogicalBytes
            Me.CompressedLogicalBytes = CompressedLogicalBytes

            Me.HoleCount = HoleCount
            Me.LargestHoleBytes = LargestHoleBytes
            Me.AverageHoleBytes = AverageHoleBytes

            Me.HasFileMasterKey = HasFileMasterKey
            Me.HasWrappedFileMasterKey = HasWrappedFileMasterKey

            Me.IsFileMasterKeyPubliclyWrapped = IsFileMasterKeyPubliclyWrapped
            Me.IsFileMasterKeyUserWrapped = IsFileMasterKeyUserWrapped

            Me.IsEncryptionEnabledForNewWrites = IsEncryptionEnabledForNewWrites

            Me.CurrentCompressionMethod = CurrentCompressionMethod
            Me.CurrentCompressionRatioThreshold = CurrentCompressionRatioThreshold
            Me.CurrentStoreSparseChunks = CurrentStoreSparseChunks

            Me.HeaderSequence = HeaderSequence
            Me.ActiveHeaderCopy = ActiveHeaderCopy

            Me.MetadataRootOffset = MetadataRootOffset
            Me.MetadataRootEndOffset = MetadataRootEndOffset

            Me.DataStartOffset = DataStartOffset
            Me.DataAreaEndOffset = DataAreaEndOffset
            Me.LiveDataEndOffset = LiveDataEndOffset

            If SparseChunks Is Nothing Then Throw New ArgumentNullException(NameOf(SparseChunks))
            If Regions Is Nothing Then Throw New ArgumentNullException(NameOf(Regions))

            Dim AllChunks =
                Regions.SelectMany(Function(region) region.Chunks).
                        Concat(SparseChunks).
                        GroupBy(Function(chunk) chunk.Index).
                        Select(Function(group) group.First()).
                        OrderBy(Function(chunk) chunk.Index).
                        ToList()

            _Chunks = New ReadOnlyCollection(Of Chunk)(AllChunks)
            _Regions = New ReadOnlyCollection(Of Region)(Regions.ToList())
            _MetadataPages = New ReadOnlyCollection(Of MetadataPage)(
                Regions.Where(Function(region) region.MetadataPage IsNot Nothing).
                        Select(Function(region) region.MetadataPage).
                        ToList())

        End Sub

        ''' <summary>
        ''' Logical plaintext length of the stream.
        ''' </summary>
        Public ReadOnly Property LogicalLength As Long

        ''' <summary>
        ''' Physical length of the backing stream.
        ''' </summary>
        Public ReadOnly Property PhysicalLength As Long

        ''' <summary>
        ''' Preferred logical segment size used by the stream for newly written physical records.
        ''' Existing extents and physical records are not required to match this size.
        ''' </summary>
        Public ReadOnly Property ChunkSize As Integer

        ''' <summary>
        ''' Number of logical extent entries.
        ''' </summary>
        Public ReadOnly Property ChunkCount As Integer

        ''' <summary>
        ''' Number of logical extents with physical records.
        ''' </summary>
        Public ReadOnly Property AllocatedChunkCount As Integer

        ''' <summary>
        ''' Number of sparse or unallocated logical extents.
        ''' </summary>
        Public ReadOnly Property SparseChunkCount As Integer

        ''' <summary>
        ''' Number of allocated logical extents whose referenced payload is encrypted.
        ''' </summary>
        Public ReadOnly Property EncryptedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated logical extents whose referenced payload is not encrypted.
        ''' </summary>
        Public ReadOnly Property UnencryptedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated logical extents whose referenced payload is compressed.
        ''' </summary>
        Public ReadOnly Property CompressedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated physical records that are not physically ordered after the previous allocated physical record.
        ''' </summary>
        Public ReadOnly Property OutOfOrderChunkCount As Integer

        ''' <summary>
        ''' Physical bytes occupied by both fixed header copies.
        ''' </summary>
        Public ReadOnly Property PhysicalHeaderBytes As Long

        ''' <summary>
        ''' Physical bytes in the data area before metadata.
        ''' </summary>
        Public ReadOnly Property PhysicalDataAreaBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by live physical records.
        ''' </summary>
        Public ReadOnly Property PhysicalChunkRecordBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by live physical record payloads only.
        ''' </summary>
        Public ReadOnly Property PhysicalPayloadBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by physical record overhead such as record headers, IVs and MACs.
        ''' </summary>
        Public ReadOnly Property PhysicalChunkOverheadBytes As Long

        ''' <summary>
        ''' Physical bytes in the data area that are not referenced by the current metadata.
        ''' </summary>
        Public ReadOnly Property FragmentedBytes As Long

        ''' <summary>
        ''' Logical bytes represented by allocated logical extents.
        ''' </summary>
        Public ReadOnly Property LogicalPayloadBytes As Long

        ''' <summary>
        ''' Logical bytes represented by encrypted allocated extents.
        ''' </summary>
        Public ReadOnly Property EncryptedLogicalBytes As Long

        ''' <summary>
        ''' Logical bytes represented by compressed allocated extents.
        ''' </summary>
        Public ReadOnly Property CompressedLogicalBytes As Long

        ''' <summary>
        ''' Number of hole regions in the data area.
        ''' </summary>
        Public ReadOnly Property HoleCount As Integer

        ''' <summary>
        ''' Size of the largest unreferenced data-area hole.
        ''' </summary>
        Public ReadOnly Property LargestHoleBytes As Long

        ''' <summary>
        ''' Average size of unreferenced data-area holes.
        ''' </summary>
        Public ReadOnly Property AverageHoleBytes As Long

        ''' <summary>
        ''' True when the in-memory stream instance has a file master key available.
        ''' </summary>
        Public ReadOnly Property HasFileMasterKey As Boolean

        ''' <summary>
        ''' True when the header contains a wrapped file master key.
        ''' </summary>
        Public ReadOnly Property HasWrappedFileMasterKey As Boolean

        ''' <summary>
        ''' True when the file master key is wrapped using the public integrity key.
        ''' </summary>
        Public ReadOnly Property IsFileMasterKeyPubliclyWrapped As Boolean

        ''' <summary>
        ''' True when the file master key is wrapped using user-supplied encryption information.
        ''' </summary>
        Public ReadOnly Property IsFileMasterKeyUserWrapped As Boolean

        ''' <summary>
        ''' True when newly written physical records are currently encrypted.
        ''' </summary>
        Public ReadOnly Property IsEncryptionEnabledForNewWrites As Boolean

        ''' <summary>
        ''' Compression method currently configured for newly written physical records.
        ''' </summary>
        Public ReadOnly Property CurrentCompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

        ''' <summary>
        ''' Maximum compressed-size ratio currently allowed before storing a newly written physical record compressed.
        ''' </summary>
        Public ReadOnly Property CurrentCompressionRatioThreshold As Double

        ''' <summary>
        ''' True when newly written all-zero logical ranges are stored physically instead of being sparse.
        ''' </summary>
        Public ReadOnly Property CurrentStoreSparseChunks As Boolean

        ''' <summary>
        ''' Sequence number of the selected active header.
        ''' </summary>
        Public ReadOnly Property HeaderSequence As Long

        ''' <summary>
        ''' Active header copy index.
        ''' </summary>
        Public ReadOnly Property ActiveHeaderCopy As Integer

        ''' <summary>
        ''' Physical offset at which physical records may begin.
        ''' </summary>
        Public ReadOnly Property DataStartOffset As Long

        ''' <summary>
        ''' Physical end offset of the data area.
        ''' </summary>
        Public ReadOnly Property DataAreaEndOffset As Long

        ''' <summary>
        ''' Physical end offset of the highest referenced live physical record.
        ''' </summary>
        Public ReadOnly Property LiveDataEndOffset As Long

        ''' <summary>
        ''' Total physical bytes occupied by persisted metadata structures.
        ''' This includes metadata roots, extent pages, physical-record pages and directory pages.
        ''' </summary>
        Public ReadOnly Property PhysicalMetadataBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by metadata root records.
        ''' </summary>
        Public ReadOnly Property MetadataRootBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by extent and physical-record metadata pages.
        ''' </summary>
        Public ReadOnly Property IndexPageBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by all metadata directory pages.
        ''' This includes extent directory pages, physical-record directory pages and hole-directory pages.
        ''' </summary>
        Public ReadOnly Property DirectoryPageBytes As Long

        ''' <summary>
        ''' Physical bytes occupied specifically by persisted hole-directory pages.
        ''' This value is included within <see cref="DirectoryPageBytes" />.
        ''' </summary>
        Public ReadOnly Property HoleDirectoryBytes As Long

        ''' <summary>
        ''' Total structural overhead bytes.
        ''' This includes fixed headers, persisted metadata structures and
        ''' per-physical-record overhead.
        ''' </summary>
        Public ReadOnly Property TotalStructuralOverheadBytes As Long

        ''' <summary>
        ''' Physical offset of the active metadata root.
        ''' </summary>
        Public ReadOnly Property MetadataRootOffset As Long

        ''' <summary>
        ''' Physical end offset of the active metadata root.
        ''' </summary>
        Public ReadOnly Property MetadataRootEndOffset As Long

        ''' <summary>
        ''' Physical-record fragmentation as a ratio of the live physical-record data area.
        ''' This uses the same definition as ChunkedStream.GetFragmentation().
        ''' </summary>
        ''' <remarks>
        ''' This value measures unused space between DataStartOffset and LiveDataEndOffset
        ''' using live physical-record bytes only. Metadata layout holes are still exposed
        ''' through FragmentedBytes, HoleCount, LargestHoleBytes and Regions.
        ''' </remarks>
        Public ReadOnly Property FragmentationRatio As Double
            Get

                Dim TotalStoredChunkBytes =
                    Math.Max(0L, LiveDataEndOffset - DataStartOffset)

                If TotalStoredChunkBytes <= 0 Then Return 0

                Dim WastedChunkBytes =
                    Math.Max(0L, TotalStoredChunkBytes - PhysicalChunkRecordBytes)

                Return WastedChunkBytes / CDbl(TotalStoredChunkBytes)

            End Get
        End Property

        ''' <summary>
        ''' Sparse chunks as a ratio of all logical chunks.
        ''' </summary>
        Public ReadOnly Property SparseChunkRatio As Double
            Get
                If ChunkCount <= 0 Then Return 0
                Return SparseChunkCount / CDbl(ChunkCount)
            End Get
        End Property

        ''' <summary>
        ''' Encrypted chunks as a ratio of allocated chunks.
        ''' </summary>
        Public ReadOnly Property EncryptedChunkRatio As Double
            Get
                If AllocatedChunkCount <= 0 Then Return 0
                Return EncryptedChunkCount / CDbl(AllocatedChunkCount)
            End Get
        End Property

        ''' <summary>
        ''' Compressed chunks as a ratio of allocated chunks.
        ''' </summary>
        Public ReadOnly Property CompressedChunkRatio As Double
            Get
                If AllocatedChunkCount <= 0 Then Return 0
                Return CompressedChunkCount / CDbl(AllocatedChunkCount)
            End Get
        End Property

        ''' <summary>
        ''' Out-of-order chunks as a ratio of allocated chunks.
        ''' </summary>
        Public ReadOnly Property OutOfOrderChunkRatio As Double
            Get
                If AllocatedChunkCount <= 0 Then Return 0
                Return OutOfOrderChunkCount / CDbl(AllocatedChunkCount)
            End Get
        End Property

        ''' <summary>
        ''' Encrypted logical bytes as a ratio of total logical length.
        ''' </summary>
        Public ReadOnly Property EncryptedCoverageRatio As Double
            Get
                If LogicalLength <= 0 Then Return 0
                Return Math.Min(1.0R, EncryptedLogicalBytes / CDbl(LogicalLength))
            End Get
        End Property

        ''' <summary>
        ''' Compressed logical bytes as a ratio of total logical length.
        ''' </summary>
        Public ReadOnly Property CompressedCoverageRatio As Double
            Get
                If LogicalLength <= 0 Then Return 0
                Return Math.Min(1.0R, CompressedLogicalBytes / CDbl(LogicalLength))
            End Get
        End Property

        ''' <summary>
        ''' Stored payload bytes divided by logical payload bytes. Lower values mean better payload compression.
        ''' </summary>
        Public ReadOnly Property PayloadCompressionRatio As Double
            Get
                If LogicalPayloadBytes <= 0 Then Return 0
                Return PhysicalPayloadBytes / CDbl(LogicalPayloadBytes)
            End Get
        End Property

        ''' <summary>
        ''' Logical payload bytes saved by payload compression or sparse representation.
        ''' </summary>
        Public ReadOnly Property PayloadSpaceSavedRatio As Double
            Get
                If LogicalPayloadBytes <= 0 Then Return 0
                Return Math.Max(0.0R, 1.0R - PayloadCompressionRatio)
            End Get
        End Property

        ''' <summary>
        ''' Live physical-record bytes divided by logical payload bytes.
        ''' This includes per-record headers, IVs and MACs but excludes global headers, metadata and holes.
        ''' </summary>
        Public ReadOnly Property ChunkRecordCompressionRatio As Double
            Get
                If LogicalPayloadBytes <= 0 Then Return 0
                Return PhysicalChunkRecordBytes / CDbl(LogicalPayloadBytes)
            End Get
        End Property

        ''' <summary>
        ''' Physical stream length divided by logical stream length.
        ''' This includes headers, metadata bytes, physical-record overhead and fragmentation.
        ''' </summary>
        Public ReadOnly Property PhysicalToLogicalRatio As Double
            Get
                If LogicalLength <= 0 Then Return 0
                Return PhysicalLength / CDbl(LogicalLength)
            End Get
        End Property

        ''' <summary>
        ''' Logical stream length divided by physical stream length.
        ''' Values greater than 1 indicate the stream stores more logical bytes than physical bytes used.
        ''' </summary>
        Public ReadOnly Property LogicalToPhysicalRatio As Double
            Get
                If PhysicalLength <= 0 Then Return 0
                Return LogicalLength / CDbl(PhysicalLength)
            End Get
        End Property

        ''' <summary>
        ''' Physical payload bytes as a ratio of live physical-record bytes.
        ''' </summary>
        Public ReadOnly Property PayloadDensityRatio As Double
            Get
                If PhysicalChunkRecordBytes <= 0 Then Return 0
                Return PhysicalPayloadBytes / CDbl(PhysicalChunkRecordBytes)
            End Get
        End Property

        ''' <summary>
        ''' Structural overhead bytes as a ratio of the physical backing stream length.
        ''' This includes fixed headers, persisted metadata structures and per-record
        ''' overhead.
        ''' </summary>
        Public ReadOnly Property StructuralOverheadRatio As Double
            Get

                If PhysicalLength <= 0 Then Return 0

                Return TotalStructuralOverheadBytes / CDbl(PhysicalLength)

            End Get
        End Property

        Private ReadOnly _Chunks As ReadOnlyCollection(Of Chunk)

        ''' <summary>
        ''' Logical chunk snapshots.
        ''' </summary>
        Public ReadOnly Property Chunks As IReadOnlyList(Of Chunk)
            Get
                Return _Chunks
            End Get
        End Property

        Private ReadOnly _Regions As ReadOnlyCollection(Of Region)

        ''' <summary>
        ''' Physical region snapshots.
        ''' </summary>
        Public ReadOnly Property Regions As IReadOnlyList(Of Region)
            Get
                Return _Regions
            End Get
        End Property

        Private ReadOnly _MetadataPages As ReadOnlyCollection(Of MetadataPage)

        ''' <summary>
        ''' Active persisted metadata structures in physical file order.
        ''' </summary>
        Public ReadOnly Property MetadataPages As IReadOnlyList(Of MetadataPage)
            Get
                Return _MetadataPages
            End Get
        End Property

        ''' <summary>
        ''' Returns a concise diagnostic summary of the stream structure.
        ''' </summary>
        Public Overrides Function ToString() As String

            Return $"ChunkedStream [{LogicalLength.FormatFileSizeFromBytes()} logical, " &
                   $"{PhysicalLength.FormatFileSizeFromBytes()} physical, " &
                   $"{ChunkCount} chunks, " &
                   $"{PayloadSpaceSavedRatio:P2} saved, " &
                   $"{FragmentationRatio:P2} fragmented, " &
                   $"{EncryptedCoverageRatio:P2} encrypted, " &
                   $"{LogicalToPhysicalRatio:0.##}x efficiency]"

        End Function

        ''' <summary>
        ''' Immutable read-only snapshot of a logical chunk and its physical record metadata.
        ''' </summary>
        Public NotInheritable Class Chunk

            Friend Sub New(Index As Integer,
                           LogicalOffset As Long,
                           LogicalEndOffset As Long,
                           PlainLength As Integer,
                           IsSparse As Boolean,
                           IsAllocated As Boolean,
                           PhysicalOffset As Long?,
                           PhysicalLength As Integer?,
                           PhysicalEndOffset As Long?,
                           PhysicalOrder As Integer?,
                           CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods,
                           CompressionEvaluatedMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods,
                           CompressionEvaluatedPercent As Integer,
                           EncryptionMethod As ChunkedStream.ChunkEncryptionMethods,
                           PayloadLength As Integer,
                           ChunkFlags As ChunkedStream.ChunkFlags,
                           PreviousPhysicalGap As Long?,
                           NextPhysicalGap As Long?,
                           IsPhysicallyContiguousWithPrevious As Boolean,
                           IsPhysicallyContiguousWithNext As Boolean,
                           IsInLogicalOrder As Boolean,
                           Optional PhysicalRecordId As Long? = Nothing,
                           Optional PhysicalRecordOffset As Integer? = Nothing,
                           Optional AnchorId As Long = 0)

                Me.Index = Index
                Me.LogicalOffset = LogicalOffset
                Me.LogicalEndOffset = LogicalEndOffset
                Me.PlainLength = PlainLength
                Me.IsSparse = IsSparse
                Me.IsAllocated = IsAllocated
                Me.PhysicalOffset = PhysicalOffset
                Me.PhysicalLength = PhysicalLength
                Me.PhysicalEndOffset = PhysicalEndOffset
                Me.PhysicalOrder = PhysicalOrder
                Me.CompressionMethod = CompressionMethod
                Me.CompressionEvaluatedMethod = CompressionEvaluatedMethod
                Me.CompressionEvaluatedPercent = CompressionEvaluatedPercent
                Me.EncryptionMethod = EncryptionMethod
                Me.PayloadLength = PayloadLength
                Me.ChunkFlags = ChunkFlags
                Me.PreviousPhysicalGap = PreviousPhysicalGap
                Me.NextPhysicalGap = NextPhysicalGap
                Me.IsPhysicallyContiguousWithPrevious = IsPhysicallyContiguousWithPrevious
                Me.IsPhysicallyContiguousWithNext = IsPhysicallyContiguousWithNext
                Me.IsInLogicalOrder = IsInLogicalOrder
                Me.PhysicalRecordId = PhysicalRecordId
                Me.PhysicalRecordOffset = PhysicalRecordOffset
                Me.AnchorId = AnchorId

            End Sub

            ''' <summary>
            ''' Logical chunk index.
            ''' </summary>
            Public ReadOnly Property Index As Integer

            ''' <summary>
            ''' Logical start offset.
            ''' </summary>
            Public ReadOnly Property LogicalOffset As Long

            ''' <summary>
            ''' Logical end offset.
            ''' </summary>
            Public ReadOnly Property LogicalEndOffset As Long

            ''' <summary>
            ''' Logical length represented by the chunk.
            ''' </summary>
            Public ReadOnly Property LogicalLength As Long
                Get
                    Return LogicalEndOffset - LogicalOffset
                End Get
            End Property

            ''' <summary>
            ''' Plain logical bytes represented by the chunk.
            ''' </summary>
            Public ReadOnly Property PlainLength As Integer

            ''' <summary>
            ''' True when the chunk has no physical record.
            ''' </summary>
            Public ReadOnly Property IsSparse As Boolean

            ''' <summary>
            ''' True when the chunk has a physical record.
            ''' </summary>
            Public ReadOnly Property IsAllocated As Boolean

            ''' <summary>
            ''' Physical record id, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalRecordId As Long?

            ''' <summary>
            ''' Offset within the physical record plaintext, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalRecordOffset As Integer?

            ''' <summary>
            ''' Immutable anchor identifier for the chunk's logical start.
            ''' Zero indicates the chunk is not anchored.
            ''' </summary>
            Public ReadOnly Property AnchorId As Long

            ''' <summary>
            ''' True when this chunk begins at an anchored logical position.
            ''' </summary>
            Public ReadOnly Property IsAnchored As Boolean
                Get
                    Return AnchorId > 0
                End Get
            End Property

            ''' <summary>
            ''' Physical chunk record start offset, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalOffset As Long?

            ''' <summary>
            ''' Physical chunk record length, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalLength As Integer?

            ''' <summary>
            ''' Physical chunk record end offset, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalEndOffset As Long?

            ''' <summary>
            ''' Position in physical chunk order, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PhysicalOrder As Integer?

            ''' <summary>
            ''' Compression method stored in the chunk record.
            ''' </summary>
            Public ReadOnly Property CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

            ''' <summary>
            ''' Compression method that was last evaluated for this chunk.
            ''' </summary>
            Public ReadOnly Property CompressionEvaluatedMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

            ''' <summary>
            ''' Evaluated compressed size as a percentage of the original plaintext size.
            ''' Stored as 0-100. Lower values indicate better compression.
            ''' </summary>
            Public ReadOnly Property CompressionEvaluatedPercent As Integer

            ''' <summary>
            ''' Evaluated compressed size as a ratio of the original plaintext size.
            ''' Lower values indicate better compression.
            ''' </summary>
            Public ReadOnly Property CompressionEvaluatedRatio As Double
                Get
                    If CompressionEvaluatedPercent <= 0 Then Return 0
                    If CompressionEvaluatedPercent >= 100 Then Return 1
                    Return CompressionEvaluatedPercent / 100.0R
                End Get
            End Property

            ''' <summary>
            ''' Encryption method stored in the chunk record.
            ''' </summary>
            Public ReadOnly Property EncryptionMethod As ChunkedStream.ChunkEncryptionMethods

            ''' <summary>
            ''' Payload length stored in the chunk record.
            ''' </summary>
            Public ReadOnly Property PayloadLength As Integer

            ''' <summary>
            ''' Flags stored in the physical chunk record, or inferred for sparse chunks.
            ''' </summary>
            Public ReadOnly Property ChunkFlags As ChunkedStream.ChunkFlags

            ''' <summary>
            ''' True when the chunk plaintext is known to be entirely zero bytes.
            ''' Sparse chunks always return True.
            ''' </summary>
            Public ReadOnly Property IsPlaintextAllZero As Boolean
                Get
                    If IsSparse Then Return True
                    Return ChunkFlags.HasFlag(ChunkedStream.ChunkFlags.PlaintextAllZero)
                End Get
            End Property

            ''' <summary>
            ''' True when <see cref="CompressionEvaluatedPercent" /> is an estimate from a
            ''' sample rather than a measurement of the whole plaintext (see
            ''' <see cref="ChunkedStream.ChunkedStreamOptions.CompressionEvaluationStates.Sampled" />).
            ''' </summary>
            Public ReadOnly Property IsCompressionEvaluationEstimated As Boolean
                Get
                    Return ChunkFlags.HasFlag(ChunkedStream.ChunkFlags.CompressionEstimated)
                End Get
            End Property

            ''' <summary>
            ''' Gap before this chunk in physical order, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property PreviousPhysicalGap As Long?

            ''' <summary>
            ''' Gap after this chunk in physical order, or Nothing for sparse chunks.
            ''' </summary>
            Public ReadOnly Property NextPhysicalGap As Long?

            ''' <summary>
            ''' True when this chunk physically follows the previous physical chunk without a gap.
            ''' </summary>
            Public ReadOnly Property IsPhysicallyContiguousWithPrevious As Boolean

            ''' <summary>
            ''' True when the next physical chunk follows this chunk without a gap.
            ''' </summary>
            Public ReadOnly Property IsPhysicallyContiguousWithNext As Boolean

            ''' <summary>
            ''' True when this chunk appears after a lower logical chunk in physical order.
            ''' </summary>
            Public ReadOnly Property IsInLogicalOrder As Boolean

            ''' <summary>
            ''' True when the chunk payload was stored compressed.
            ''' </summary>
            Public ReadOnly Property IsCompressed As Boolean
                Get
                    Return CompressionMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None
                End Get
            End Property

            ''' <summary>
            ''' True when the chunk payload was stored encrypted.
            ''' </summary>
            Public ReadOnly Property IsEncrypted As Boolean
                Get
                    Return EncryptionMethod <> ChunkedStream.ChunkEncryptionMethods.None
                End Get
            End Property

            ''' <summary>
            ''' Physical chunk record bytes that are not payload bytes.
            ''' </summary>
            Public ReadOnly Property PhysicalOverheadBytes As Long
                Get
                    If PhysicalLength.HasValue = False Then Return 0
                    Return Math.Max(0L, CLng(PhysicalLength.Value) - PayloadLength)
                End Get
            End Property

            ''' <summary>
            ''' Physical chunk overhead as a ratio of physical chunk record length.
            ''' </summary>
            Public ReadOnly Property PhysicalOverheadRatio As Double
                Get
                    If PhysicalLength.HasValue = False OrElse PhysicalLength.Value <= 0 Then Return 0
                    Return PhysicalOverheadBytes / CDbl(PhysicalLength.Value)
                End Get
            End Property

            ''' <summary>
            ''' Plain bytes saved in the payload.
            ''' </summary>
            Public ReadOnly Property PayloadSpaceSavedBytes As Long
                Get
                    If IsSparse Then Return PlainLength
                    Return Math.Max(0L, CLng(PlainLength) - PayloadLength)
                End Get
            End Property

            ''' <summary>
            ''' Stored payload size divided by plain size. Lower values mean better compression.
            ''' </summary>
            Public ReadOnly Property PayloadCompressionRatio As Double
                Get
                    If PlainLength <= 0 Then Return 0
                    Return PayloadLength / CDbl(PlainLength)
                End Get
            End Property

            ''' <summary>
            ''' Payload space saved as a ratio of plain size.
            ''' </summary>
            Public ReadOnly Property PayloadSpaceSavedRatio As Double
                Get
                    If PlainLength <= 0 Then Return 0
                    Return PayloadSpaceSavedBytes / CDbl(PlainLength)
                End Get
            End Property

            ''' <summary>
            ''' Returns a concise diagnostic summary of the chunk.
            ''' </summary>
            Public Overrides Function ToString() As String

                Dim AnchorText = If(IsAnchored, $", Anchor={AnchorId}", "")
                Dim ZeroText = If(IsPlaintextAllZero, ", zero", "")

                If IsSparse Then
                    Return $"Chunk {Index} [Sparse, {PlainLength.FormatFileSizeFromBytes()} logical{AnchorText}{ZeroText}]"
                End If

                Dim EncryptionText = If(IsEncrypted, ", encrypted", "")
                Dim CompressionText = If(IsCompressed, $", {PayloadSpaceSavedRatio:P2} saved", "")

                Dim EvaluatedCompressionText = If(CompressionEvaluatedMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None, $", evaluated {CompressionEvaluatedMethod} {CompressionEvaluatedRatio:P2}", "")

                Return $"Chunk {Index} [{PlainLength.FormatFileSizeFromBytes()} -> {PayloadLength.FormatFileSizeFromBytes()}{CompressionText}{EvaluatedCompressionText}{EncryptionText}{ZeroText}{AnchorText}]"

            End Function

        End Class

        ''' <summary>
        ''' Immutable read-only snapshot of an active persisted metadata structure.
        ''' </summary>
        Public NotInheritable Class MetadataPage

            Friend Sub New(MetadataPageType As MetadataPageTypes,
                           PageNumber As Integer?,
                           PhysicalOffset As Long,
                           PhysicalLength As Long,
                           EntriesUsed As Integer?,
                           EntryCapacity As Integer?,
                           EntrySize As Integer?,
                           Description As String)

                Me.MetadataPageType = MetadataPageType
                Me.PageNumber = PageNumber
                Me.PhysicalOffset = PhysicalOffset
                Me.PhysicalLength = PhysicalLength
                Me.PhysicalEndOffset = PhysicalOffset + PhysicalLength
                Me.EntriesUsed = EntriesUsed
                Me.EntryCapacity = EntryCapacity
                Me.EntrySize = EntrySize
                Me.Description = Description

            End Sub

            ''' <summary>
            ''' Metadata structure classification.
            ''' </summary>
            Public ReadOnly Property MetadataPageType As MetadataPageTypes

            ''' <summary>
            ''' Metadata page number, or Nothing for the metadata root.
            ''' </summary>
            Public ReadOnly Property PageNumber As Integer?

            ''' <summary>
            ''' Physical start offset of the metadata structure.
            ''' </summary>
            Public ReadOnly Property PhysicalOffset As Long

            ''' <summary>
            ''' Physical length of the metadata structure.
            ''' </summary>
            Public ReadOnly Property PhysicalLength As Long

            ''' <summary>
            ''' Physical end offset of the metadata structure.
            ''' </summary>
            Public ReadOnly Property PhysicalEndOffset As Long

            ''' <summary>
            ''' Number of populated entries, or Nothing when the structure is not entry based.
            ''' </summary>
            Public ReadOnly Property EntriesUsed As Integer?

            ''' <summary>
            ''' Maximum entry capacity, or Nothing when the structure is not entry based.
            ''' </summary>
            Public ReadOnly Property EntryCapacity As Integer?

            ''' <summary>
            ''' Serialized bytes reserved for each entry, or Nothing when the structure is not entry based.
            ''' </summary>
            Public ReadOnly Property EntrySize As Integer?

            ''' <summary>
            ''' Human-readable metadata structure description.
            ''' </summary>
            Public ReadOnly Property Description As String

            ''' <summary>
            ''' Number of unused entry slots.
            ''' </summary>
            Public ReadOnly Property UnusedEntries As Integer?
                Get
                    If EntriesUsed.HasValue = False OrElse EntryCapacity.HasValue = False Then Return Nothing
                    Return Math.Max(0, EntryCapacity.Value - EntriesUsed.Value)
                End Get
            End Property

            ''' <summary>
            ''' Serialized bytes occupied by populated entry slots.
            ''' </summary>
            Public ReadOnly Property UsedEntryBytes As Long?
                Get
                    If EntriesUsed.HasValue = False OrElse EntrySize.HasValue = False Then Return Nothing
                    Return CLng(EntriesUsed.Value) * EntrySize.Value
                End Get
            End Property

            ''' <summary>
            ''' Serialized bytes reserved by unused entry slots.
            ''' </summary>
            Public ReadOnly Property UnusedEntryBytes As Long?
                Get
                    If UnusedEntries.HasValue = False OrElse EntrySize.HasValue = False Then Return Nothing
                    Return CLng(UnusedEntries.Value) * EntrySize.Value
                End Get
            End Property

            ''' <summary>
            ''' Populated entry slots as a ratio of entry capacity.
            ''' </summary>
            Public ReadOnly Property EntryUsageRatio As Double?
                Get
                    If EntriesUsed.HasValue = False OrElse EntryCapacity.HasValue = False Then Return Nothing
                    If EntryCapacity.Value <= 0 Then Return 0.0R
                    Return Math.Min(1.0R, EntriesUsed.Value / CDbl(EntryCapacity.Value))
                End Get
            End Property

            ''' <summary>
            ''' Unused entry slots as a ratio of entry capacity.
            ''' </summary>
            Public ReadOnly Property EntryWasteRatio As Double?
                Get
                    If EntryUsageRatio.HasValue = False Then Return Nothing
                    Return Math.Max(0.0R, 1.0R - EntryUsageRatio.Value)
                End Get
            End Property

            ''' <summary>
            ''' Returns a concise diagnostic summary of the metadata structure.
            ''' </summary>
            Public Overrides Function ToString() As String
                If EntriesUsed.HasValue AndAlso EntryCapacity.HasValue Then
                    Return $"{Description} [{PhysicalLength.FormatFileSizeFromBytes()}, {EntriesUsed.Value}/{EntryCapacity.Value} entries]"
                End If

                Return $"{Description} [{PhysicalLength.FormatFileSizeFromBytes()}]"
            End Function

        End Class

        ''' <summary>
        ''' Immutable read-only snapshot of a physical region in the backing stream.
        ''' </summary>
        Public NotInheritable Class Region

            Friend Sub New(PhysicalOffset As Long,
                           PhysicalLength As Long,
                           RegionType As RegionTypes,
                           Chunks As IEnumerable(Of Chunk),
                           MetadataPage As MetadataPage,
                           Description As String)

                Me.PhysicalOffset = PhysicalOffset
                Me.PhysicalLength = PhysicalLength
                Me.PhysicalEndOffset = PhysicalOffset + PhysicalLength
                Me.RegionType = RegionType
                Me.MetadataPage = MetadataPage
                Me.Description = Description

                Dim ChunkList = If(Chunks, Enumerable.Empty(Of Chunk)()).OrderBy(Function(chunk) chunk.Index).ToList()
                _Chunks = New ReadOnlyCollection(Of Chunk)(ChunkList)

            End Sub

            ''' <summary>
            ''' Physical region start offset.
            ''' </summary>
            Public ReadOnly Property PhysicalOffset As Long

            ''' <summary>
            ''' Physical region length.
            ''' </summary>
            Public ReadOnly Property PhysicalLength As Long

            ''' <summary>
            ''' Physical region end offset.
            ''' </summary>
            Public ReadOnly Property PhysicalEndOffset As Long

            ''' <summary>
            ''' Region classification.
            ''' </summary>
            Public ReadOnly Property RegionType As RegionTypes

            Private ReadOnly _Chunks As ReadOnlyCollection(Of Chunk)

            ''' <summary>
            ''' Logical chunks referencing this physical chunk-record region.
            ''' Empty for non-chunk regions.
            ''' </summary>
            Public ReadOnly Property Chunks As IReadOnlyList(Of Chunk)
                Get
                    Return _Chunks
                End Get
            End Property

            ''' <summary>
            ''' First logical chunk referencing this physical chunk-record region.
            ''' Nothing for non-chunk regions or when no logical chunk is associated.
            ''' Use Chunks when shared physical records may have multiple logical references.
            ''' </summary>
            Public ReadOnly Property Chunk As Chunk
                Get
                    Return _Chunks.FirstOrDefault()
                End Get
            End Property

            ''' <summary>
            ''' Metadata-page snapshot for metadata regions. Nothing for other region types.
            ''' </summary>
            Public ReadOnly Property MetadataPage As MetadataPage

            ''' <summary>
            ''' Human-readable region description.
            ''' </summary>
            Public ReadOnly Property Description As String

            ''' <summary>
            ''' Returns a concise diagnostic summary of the physical region.
            ''' </summary>
            Public Overrides Function ToString() As String
                If RegionType = RegionTypes.Chunk AndAlso Chunk IsNot Nothing Then
                    Dim SharedText = If(_Chunks.Count > 1, $", {_Chunks.Count} logical chunks", "")
                    Return $"Chunk {Chunk.Index} region [{PhysicalLength.FormatFileSizeFromBytes()}{SharedText}]"
                End If

                If MetadataPage IsNot Nothing Then Return MetadataPage.ToString()
                Return $"{RegionType} [{PhysicalLength.FormatFileSizeFromBytes()}]"
            End Function

        End Class

    End Class

End Namespace