' ================================================================================
' ChunkedStream Structure Snapshot API
' ================================================================================
'
' Purpose
'   - Immutable diagnostic view of a ChunkedStream.
'
' Design
'   - Produces a read-only snapshot of logical and physical structure.
'   - Reads chunk record headers only.
'   - Does not read plaintext chunk payloads.
'   - Does not decrypt or decompress chunk contents.
'
' Features
'   - Chunk metadata inspection.
'   - Physical-region inspection.
'   - Compression and encryption statistics.
'   - Fragmentation analysis.
'
' Notes
'   - Intended for diagnostics, reporting, visualisation and debugging.
'   - Returned objects are immutable snapshots.
'
' ================================================================================

Imports System.Collections.ObjectModel
Imports System.IO

Namespace Streams

    Partial Class ChunkedStream

        Private Structure StructureChunkBuildInfo
            Public Index As Integer
            Public LogicalOffset As Long
            Public LogicalEndOffset As Long
            Public PlainLength As Integer
            Public IsAllocated As Boolean
            Public PhysicalOffset As Long
            Public PhysicalLength As Integer
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedPercent As Integer
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PayloadLength As Integer
            Public ChunkFlags As ChunkFlags
        End Structure

        Private Structure ChunkHeaderSnapshot
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedMethod As ChunkedStreamOptions.CompressionMethods
            Public CompressionEvaluatedPercent As Integer
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PlainLength As Integer
            Public PayloadLength As Integer
            Public ChunkFlags As ChunkFlags
        End Structure

        Private Structure MetadataRegionBuildInfo
            Public Offset As Long
            Public Length As Long
            Public RegionType As ChunkedStreamStructure.RegionTypes
            Public Description As String
        End Structure

        Public Function GetStructure() As ChunkedStreamStructure
            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim BuildInfos As New List(Of StructureChunkBuildInfo)(_Index.Count)
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

                For ChunkIndex = 0 To _Index.Count - 1
                    Dim Entry = _Index(ChunkIndex)
                    Dim LogicalOffset = CLng(ChunkIndex) * _ChunkSize
                    Dim LogicalPlainLength = GetLogicalPlainLengthForChunk(ChunkIndex)

                    If Entry.Offset <> 0 AndAlso Entry.RecordLength > 0 Then
                        Dim Header = ReadChunkHeaderSnapshot(ChunkIndex, Entry)

                        AllocatedChunkCount += 1
                        PhysicalChunkRecordBytes += Entry.RecordLength
                        PhysicalPayloadBytes += Header.PayloadLength
                        LogicalPayloadBytes += Header.PlainLength

                        If Header.EncryptionMethod <> ChunkEncryptionMethods.None Then
                            EncryptedChunkCount += 1
                            EncryptedLogicalBytes += Header.PlainLength
                        Else
                            UnencryptedChunkCount += 1
                        End If

                        If Header.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None Then
                            CompressedChunkCount += 1
                            CompressedLogicalBytes += Header.PlainLength
                        End If

                        BuildInfos.Add(New StructureChunkBuildInfo With {
                                .Index = ChunkIndex,
                                .LogicalOffset = LogicalOffset,
                                .LogicalEndOffset = LogicalOffset + LogicalPlainLength,
                                .PlainLength = Header.PlainLength,
                                .IsAllocated = True,
                                .PhysicalOffset = Entry.Offset,
                                .PhysicalLength = Entry.RecordLength,
                                .CompressionMethod = Header.CompressionMethod,
                                .CompressionEvaluatedMethod = Header.CompressionEvaluatedMethod,
                                .CompressionEvaluatedPercent = Header.CompressionEvaluatedPercent,
                                .EncryptionMethod = Header.EncryptionMethod,
                                .PayloadLength = Header.PayloadLength,
                                .ChunkFlags = Header.ChunkFlags
                            })
                    Else
                        SparseChunkCount += 1

                        BuildInfos.Add(New StructureChunkBuildInfo With {
                                .Index = ChunkIndex,
                                .LogicalOffset = LogicalOffset,
                                .LogicalEndOffset = LogicalOffset + LogicalPlainLength,
                                .PlainLength = LogicalPlainLength,
                                .IsAllocated = False,
                                .PhysicalOffset = 0,
                                .PhysicalLength = 0,
                                .CompressionMethod = ChunkedStreamOptions.CompressionMethods.None,
                                .CompressionEvaluatedMethod = ChunkedStreamOptions.CompressionMethods.None,
                                .CompressionEvaluatedPercent = 100,
                                .EncryptionMethod = ChunkEncryptionMethods.None,
                                .PayloadLength = 0,
                                .ChunkFlags = ChunkFlags.PlaintextAllZero
                            })
                    End If
                Next

                Dim PreviousGapByChunkIndex As New Dictionary(Of Integer, Long)
                Dim NextGapByChunkIndex As New Dictionary(Of Integer, Long)
                Dim PhysicalOrderByChunkIndex As New Dictionary(Of Integer, Integer)
                Dim ContiguousWithPreviousByChunkIndex As New Dictionary(Of Integer, Boolean)
                Dim ContiguousWithNextByChunkIndex As New Dictionary(Of Integer, Boolean)
                Dim InLogicalOrderByChunkIndex As New Dictionary(Of Integer, Boolean)

                Dim AllocatedInPhysicalOrder = BuildInfos.
                                                    Where(Function(chunk) chunk.IsAllocated).
                                                    OrderBy(Function(chunk) chunk.PhysicalOffset).
                                                    ToList()

                For PhysicalOrder = 0 To AllocatedInPhysicalOrder.Count - 1
                    Dim Current = AllocatedInPhysicalOrder(PhysicalOrder)

                    PhysicalOrderByChunkIndex(Current.Index) = PhysicalOrder

                    If PhysicalOrder = 0 Then
                        PreviousGapByChunkIndex(Current.Index) = Math.Max(0L, Current.PhysicalOffset - DataStartOffset)
                        ContiguousWithPreviousByChunkIndex(Current.Index) = Current.PhysicalOffset = DataStartOffset
                        InLogicalOrderByChunkIndex(Current.Index) = True
                    Else
                        Dim Previous = AllocatedInPhysicalOrder(PhysicalOrder - 1)
                        Dim PreviousEnd = Previous.PhysicalOffset + Previous.PhysicalLength
                        Dim PreviousGap = Math.Max(0L, Current.PhysicalOffset - PreviousEnd)
                        Dim IsInLogicalOrder = Previous.Index < Current.Index

                        PreviousGapByChunkIndex(Current.Index) = PreviousGap
                        ContiguousWithPreviousByChunkIndex(Current.Index) = PreviousGap = 0
                        InLogicalOrderByChunkIndex(Current.Index) = IsInLogicalOrder

                        If IsInLogicalOrder = False Then
                            OutOfOrderChunkCount += 1
                        End If
                    End If

                    If PhysicalOrder = AllocatedInPhysicalOrder.Count - 1 Then
                        Dim CurrentEnd = Current.PhysicalOffset + Current.PhysicalLength
                        Dim NextGap = Math.Max(0L, GetLivePhysicalEndOffset() - CurrentEnd)

                        NextGapByChunkIndex(Current.Index) = NextGap
                        ContiguousWithNextByChunkIndex(Current.Index) = NextGap = 0
                    Else
                        Dim NextEntry = AllocatedInPhysicalOrder(PhysicalOrder + 1)
                        Dim CurrentEnd = Current.PhysicalOffset + Current.PhysicalLength
                        Dim NextGap = Math.Max(0L, NextEntry.PhysicalOffset - CurrentEnd)

                        NextGapByChunkIndex(Current.Index) = NextGap
                        ContiguousWithNextByChunkIndex(Current.Index) = NextGap = 0
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

                    If BuildInfo.IsAllocated Then
                        PhysicalOffset = BuildInfo.PhysicalOffset
                        PhysicalLength = BuildInfo.PhysicalLength
                        PhysicalEndOffset = BuildInfo.PhysicalOffset + BuildInfo.PhysicalLength
                        PhysicalOrder = PhysicalOrderByChunkIndex(BuildInfo.Index)
                        PreviousPhysicalGap = PreviousGapByChunkIndex(BuildInfo.Index)
                        NextPhysicalGap = NextGapByChunkIndex(BuildInfo.Index)
                    End If

                    Chunks.Add(New ChunkedStreamStructure.Chunk(
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
                            IsPhysicallyContiguousWithPrevious:=BuildInfo.IsAllocated AndAlso ContiguousWithPreviousByChunkIndex(BuildInfo.Index),
                            IsPhysicallyContiguousWithNext:=BuildInfo.IsAllocated AndAlso ContiguousWithNextByChunkIndex(BuildInfo.Index),
                            IsInLogicalOrder:=BuildInfo.IsAllocated AndAlso InLogicalOrderByChunkIndex(BuildInfo.Index)))
                Next

                Dim MetadataRegions = GetMetadataRegionBuildInfos()
                Dim PhysicalIndexBytes = MetadataRegions.Sum(Function(region) region.Length)
                Dim PhysicalHeaderBytes = CLng(DataStartOffset)
                Dim PhysicalDataAreaBytes = Math.Max(0L, GetLivePhysicalEndOffset() - DataStartOffset)
                Dim FragmentedBytes = CalculateFragmentedBytes(AllocatedInPhysicalOrder, MetadataRegions)
                Dim PhysicalChunkOverheadBytes = Math.Max(0L, PhysicalChunkRecordBytes - PhysicalPayloadBytes)
                Dim TotalMetadataBytes = PhysicalHeaderBytes + PhysicalIndexBytes + PhysicalChunkOverheadBytes
                Dim Regions = BuildPhysicalRegions(AllocatedInPhysicalOrder, Chunks, MetadataRegions)

                Dim HoleRegions = Regions.
                                        Where(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Hole).
                                        ToList()

                Dim HoleCount = HoleRegions.Count
                Dim LargestHoleBytes = If(HoleCount = 0, 0L, HoleRegions.Max(Function(region) region.Length))
                Dim AverageHoleBytes = If(HoleCount = 0, 0L, CLng(HoleRegions.Average(Function(region) CDbl(region.Length))))
                Dim WrapMode = CType(BitConverter.ToInt32(_Header, MasterKeyWrapModeOffset), MasterKeyWrapModes)
                Dim LiveDataEndOffset = GetDataEndFromIndex()
                Dim LivePhysicalEndOffset = GetLivePhysicalEndOffset()

                Return New ChunkedStreamStructure(
                        LogicalLength:=_Length,
                        PhysicalLength:=_Fs.Length,
                        ChunkSize:=_ChunkSize,
                        ChunkCount:=_Index.Count,
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
                        PhysicalIndexBytes:=PhysicalIndexBytes,
                        TotalMetadataBytes:=TotalMetadataBytes,
                        FragmentedBytes:=FragmentedBytes,
                        LogicalPayloadBytes:=LogicalPayloadBytes,
                        EncryptedLogicalBytes:=EncryptedLogicalBytes,
                        CompressedLogicalBytes:=CompressedLogicalBytes,
                        HoleCount:=HoleCount,
                        LargestHoleBytes:=LargestHoleBytes,
                        AverageHoleBytes:=AverageHoleBytes,
                        HasFileMasterKey:=_FileMasterKey IsNot Nothing,
                        HasWrappedFileMasterKey:=WrapMode <> MasterKeyWrapModes.None,
                        IsFileMasterKeyPubliclyWrapped:=WrapMode = MasterKeyWrapModes.PublicWrap,
                        IsFileMasterKeyUserWrapped:=WrapMode = MasterKeyWrapModes.UserWrap,
                        IsEncryptionEnabledForNewWrites:=_CurrentWriteEncryptionEnabled,
                        CurrentCompressionMethod:=Options.CompressionMethod,
                        CurrentCompressionRatioThreshold:=Options.CompressionRatioThreshold,
                        CurrentStoreSparseChunks:=Options.StoreSparseChunks,
                        HeaderSequence:=_HeaderSequence,
                        ActiveHeaderCopy:=_ActiveHeaderCopy,
                        IndexOffset:=_MetadataRootOffset,
                        IndexEndOffset:=If(_MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0, _MetadataRootOffset + _MetadataRootLength, 0L),
                        DataStartOffset:=DataStartOffset,
                        DataAreaEndOffset:=LivePhysicalEndOffset,
                        LiveDataEndOffset:=LiveDataEndOffset,
                        Chunks:=Chunks,
                        Regions:=Regions)
            End SyncLock
        End Function

        Private Function GetLogicalPlainLengthForChunk(ChunkIndex As Integer) As Integer
            Dim LogicalOffset = CLng(ChunkIndex) * _ChunkSize
            Dim Remaining = _Length - LogicalOffset

            If Remaining <= 0 Then
                Return 0
            End If

            Return CInt(Math.Min(CLng(_ChunkSize), Remaining))
        End Function

        Private Function ReadChunkHeaderSnapshot(ExpectedChunkIndex As Integer, Entry As ChunkIndexEntry) As ChunkHeaderSnapshot
            If Entry.Offset < DataStartOffset Then
                Throw New InvalidDataException($"Invalid chunk offset for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.RecordLength < MinChunkRecordSize Then
                Throw New InvalidDataException($"Invalid chunk record length for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.Offset + Entry.RecordLength > _Fs.Length Then
                Throw New InvalidDataException($"Chunk {ExpectedChunkIndex} extends beyond the backing stream.")
            End If

            Dim Header(ChunkRecordHeaderSize - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Header, 0, Header.Length)

            If IsKnownMetadataMagic(Header) Then
                Throw New InvalidDataException($"Chunk index entry {ExpectedChunkIndex} points at metadata instead of a chunk record.")
            End If

            Dim StoredChunkIndex = BitConverter.ToInt64(Header, 0)

            If StoredChunkIndex <> ExpectedChunkIndex Then
                Throw New InvalidDataException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {StoredChunkIndex}.")
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

            If PlainLength < 0 OrElse PlainLength > _ChunkSize Then
                Throw New InvalidDataException($"Invalid plain length for chunk {ExpectedChunkIndex}.")
            End If

            If PayloadLength < 0 Then
                Throw New InvalidDataException($"Invalid payload length for chunk {ExpectedChunkIndex}.")
            End If

            If ChunkRecordDataOffset + PayloadLength + MacSize <> Entry.RecordLength Then
                Throw New InvalidDataException($"Invalid chunk record length for chunk {ExpectedChunkIndex}.")
            End If

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported chunk flags for chunk {ExpectedChunkIndex}: {CInt(Flags)}.")
            End If

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
                    CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then
                Throw New InvalidDataException($"Invalid compression evaluated percent for chunk {ExpectedChunkIndex}: {CompressionEvaluatedPercent}.")
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

        Private Function GetMetadataRegionBuildInfos() As List(Of MetadataRegionBuildInfo)
            Dim Result As New List(Of MetadataRegionBuildInfo)()

            For Each Descriptor In _IndexPageDescriptors.Values
                If Descriptor.Offset <= 0 OrElse Descriptor.Length <= 0 Then Continue For

                Result.Add(New MetadataRegionBuildInfo With {
                        .Offset = Descriptor.Offset,
                        .Length = Descriptor.Length,
                        .RegionType = ChunkedStreamStructure.RegionTypes.IndexPage,
                        .Description = $"Chunk Index Page {Descriptor.PageNumber}"
                    })
            Next

            For Each Descriptor In _ChunkIndexDirectoryPageDescriptors.Values
                If Descriptor.Offset <= 0 OrElse Descriptor.Length <= 0 Then Continue For

                Result.Add(New MetadataRegionBuildInfo With {
                        .Offset = Descriptor.Offset,
                        .Length = Descriptor.Length,
                        .RegionType = ChunkedStreamStructure.RegionTypes.DirectoryPage,
                        .Description = $"Chunk Index Directory Page {Descriptor.PageNumber}"
                    })
            Next

            For Each Descriptor In _HoleDirectoryPageDescriptors.Values
                If Descriptor.Offset <= 0 OrElse Descriptor.Length <= 0 Then Continue For

                Result.Add(New MetadataRegionBuildInfo With {
                        .Offset = Descriptor.Offset,
                        .Length = Descriptor.Length,
                        .RegionType = ChunkedStreamStructure.RegionTypes.DirectoryPage,
                        .Description = $"Hole Directory Page {Descriptor.PageNumber}"
                    })
            Next

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then
                Result.Add(New MetadataRegionBuildInfo With {
                        .Offset = _MetadataRootOffset,
                        .Length = _MetadataRootLength,
                        .RegionType = ChunkedStreamStructure.RegionTypes.MetadataRoot,
                        .Description = "Metadata Root"
                    })
            End If

            Result.Sort(Function(left, right) left.Offset.CompareTo(right.Offset))

            Return Result
        End Function

        Private Function GetLivePhysicalEndOffset() As Long
            Dim Result = GetDataEndFromIndex()

            For Each MetadataRegion In GetMetadataRegionBuildInfos()
                Result = Math.Max(Result, MetadataRegion.Offset + MetadataRegion.Length)
            Next

            Return Math.Max(Result, CLng(DataStartOffset))
        End Function

        Private Function CalculateFragmentedBytes(AllocatedInPhysicalOrder As IList(Of StructureChunkBuildInfo),
                                                        MetadataRegions As IList(Of MetadataRegionBuildInfo)) As Long

            Dim UsedBytes As Long = 0

            For Each Entry In AllocatedInPhysicalOrder
                UsedBytes += Entry.PhysicalLength
            Next

            For Each MetadataRegion In MetadataRegions
                UsedBytes += MetadataRegion.Length
            Next

            Dim PhysicalDataAreaBytes = Math.Max(0L, GetLivePhysicalEndOffset() - DataStartOffset)

            Return Math.Max(0L, PhysicalDataAreaBytes - UsedBytes)
        End Function

        Private Function BuildPhysicalRegions(AllocatedInPhysicalOrder As IList(Of StructureChunkBuildInfo),
                                                    Chunks As IList(Of ChunkedStreamStructure.Chunk),
                                                    MetadataRegions As IList(Of MetadataRegionBuildInfo)) As List(Of ChunkedStreamStructure.Region)

            Dim Regions As New List(Of ChunkedStreamStructure.Region)
            Dim ChunkLookup = Chunks.ToDictionary(Function(chunk) chunk.Index)

            Regions.Add(New ChunkedStreamStructure.Region(Offset:=0,
                                                                Length:=HeaderSize,
                                                                RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                                                                Chunk:=Nothing,
                                                                Description:="Header A"))

            Regions.Add(New ChunkedStreamStructure.Region(Offset:=HeaderSize,
                                                                Length:=HeaderSize,
                                                                RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                                                                Chunk:=Nothing,
                                                                Description:="Header B"))

            Dim PhysicalSegments As New List(Of ChunkedStreamStructure.Region)()

            For Each Entry In AllocatedInPhysicalOrder
                PhysicalSegments.Add(New ChunkedStreamStructure.Region(Offset:=Entry.PhysicalOffset,
                                                                            Length:=Entry.PhysicalLength,
                                                                            RegionType:=ChunkedStreamStructure.RegionTypes.Chunk,
                                                                            Chunk:=ChunkLookup(Entry.Index),
                                                                            Description:=$"Chunk {Entry.Index}"))
            Next

            For Each MetadataRegion In MetadataRegions
                PhysicalSegments.Add(New ChunkedStreamStructure.Region(Offset:=MetadataRegion.Offset,
                                                                            Length:=MetadataRegion.Length,
                                                                            RegionType:=MetadataRegion.RegionType,
                                                                            Chunk:=Nothing,
                                                                            Description:=MetadataRegion.Description))
            Next

            PhysicalSegments = PhysicalSegments.
                                    OrderBy(Function(region) region.Offset).
                                    ThenBy(Function(region) region.EndOffset).
                                    ToList()

            Dim Cursor = CLng(DataStartOffset)

            For Each Segment In PhysicalSegments
                If Segment.Offset > Cursor Then
                    Regions.Add(New ChunkedStreamStructure.Region(Offset:=Cursor,
                                                                        Length:=Segment.Offset - Cursor,
                                                                        RegionType:=ChunkedStreamStructure.RegionTypes.Hole,
                                                                        Chunk:=Nothing,
                                                                        Description:="Unreferenced data area"))
                ElseIf Segment.Offset < Cursor Then
                    Regions.Add(New ChunkedStreamStructure.Region(Offset:=Segment.Offset,
                                                                        Length:=Segment.Length,
                                                                        RegionType:=ChunkedStreamStructure.RegionTypes.Unknown,
                                                                        Chunk:=Nothing,
                                                                        Description:=$"Overlapping region: {Segment.Description}"))
                    Cursor = Math.Max(Cursor, Segment.EndOffset)
                    Continue For
                End If

                Regions.Add(Segment)
                Cursor = Math.Max(Cursor, Segment.EndOffset)
            Next

            If _Fs.Length > Cursor Then
                Regions.Add(New ChunkedStreamStructure.Region(Offset:=Cursor,
                                                                    Length:=_Fs.Length - Cursor,
                                                                    RegionType:=ChunkedStreamStructure.RegionTypes.Unused,
                                                                    Chunk:=Nothing,
                                                                    Description:="Unused trailing space"))
            End If

            Return Regions
        End Function

    End Class

    ''' <summary>
    ''' Immutable read-only snapshot of a ChunkedStream's logical and physical structure.
    ''' </summary>
    Public NotInheritable Class ChunkedStreamStructure

        ''' <summary>
        ''' Physical region type in the backing stream.
        ''' </summary>
        Public Enum RegionTypes
            ''' <summary>
            ''' Header A or Header B.
            ''' </summary>
            Header = 0

            ''' <summary>
            ''' Live chunk record referenced by the current index.
            ''' </summary>
            Chunk = 1

            ''' <summary>
            ''' Unreferenced space inside the data/metadata area.
            ''' </summary>
            Hole = 2

            ''' <summary>
            ''' Legacy flat chunk index table.
            ''' </summary>
            Index = 3

            ''' <summary>
            ''' Bytes beyond the current live structure.
            ''' </summary>
            Unused = 4

            ''' <summary>
            ''' Authenticated chunk-index metadata page.
            ''' </summary>
            IndexPage = 5

            ''' <summary>
            ''' Authenticated metadata directory page.
            ''' </summary>
            DirectoryPage = 6

            ''' <summary>
            ''' Authenticated metadata root record.
            ''' </summary>
            MetadataRoot = 7

            ''' <summary>
            ''' Region could not be classified cleanly.
            ''' </summary>
            Unknown = 255
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
                       PhysicalIndexBytes As Long,
                       TotalMetadataBytes As Long,
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
                       IndexOffset As Long,
                       IndexEndOffset As Long,
                       DataStartOffset As Long,
                       DataAreaEndOffset As Long,
                       LiveDataEndOffset As Long,
                       Chunks As IList(Of Chunk),
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
            Me.PhysicalIndexBytes = PhysicalIndexBytes
            Me.TotalMetadataBytes = TotalMetadataBytes
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
            Me.IndexOffset = IndexOffset
            Me.IndexEndOffset = IndexEndOffset
            Me.DataStartOffset = DataStartOffset
            Me.DataAreaEndOffset = DataAreaEndOffset
            Me.LiveDataEndOffset = LiveDataEndOffset

            _Chunks = New ReadOnlyCollection(Of Chunk)(Chunks)
            _Regions = New ReadOnlyCollection(Of Region)(Regions)

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
        ''' Logical chunk size used by the stream.
        ''' </summary>
        Public ReadOnly Property ChunkSize As Integer

        ''' <summary>
        ''' Number of logical index entries.
        ''' </summary>
        Public ReadOnly Property ChunkCount As Integer

        ''' <summary>
        ''' Number of chunks with physical records.
        ''' </summary>
        Public ReadOnly Property AllocatedChunkCount As Integer

        ''' <summary>
        ''' Number of sparse or unallocated chunks.
        ''' </summary>
        Public ReadOnly Property SparseChunkCount As Integer

        ''' <summary>
        ''' Number of allocated chunks whose payload is encrypted.
        ''' </summary>
        Public ReadOnly Property EncryptedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated chunks whose payload is not encrypted.
        ''' </summary>
        Public ReadOnly Property UnencryptedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated chunks whose payload is compressed.
        ''' </summary>
        Public ReadOnly Property CompressedChunkCount As Integer

        ''' <summary>
        ''' Number of allocated chunks that are not physically ordered after the previous allocated chunk.
        ''' </summary>
        Public ReadOnly Property OutOfOrderChunkCount As Integer

        ''' <summary>
        ''' Physical bytes occupied by both fixed header copies.
        ''' </summary>
        Public ReadOnly Property PhysicalHeaderBytes As Long

        ''' <summary>
        ''' Physical bytes in the data area before the index table.
        ''' </summary>
        Public ReadOnly Property PhysicalDataAreaBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by live chunk records.
        ''' </summary>
        Public ReadOnly Property PhysicalChunkRecordBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by live chunk payloads only.
        ''' </summary>
        Public ReadOnly Property PhysicalPayloadBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by chunk record overhead such as record headers, IVs and MACs.
        ''' </summary>
        Public ReadOnly Property PhysicalChunkOverheadBytes As Long

        ''' <summary>
        ''' Physical bytes occupied by the current chunk index table.
        ''' </summary>
        Public ReadOnly Property PhysicalIndexBytes As Long

        ''' <summary>
        ''' Total physical metadata bytes, including headers, index bytes and chunk record overhead.
        ''' </summary>
        Public ReadOnly Property TotalMetadataBytes As Long

        ''' <summary>
        ''' Physical bytes in the data area that are not referenced by the current index.
        ''' </summary>
        Public ReadOnly Property FragmentedBytes As Long

        ''' <summary>
        ''' Logical bytes represented by allocated chunk records.
        ''' </summary>
        Public ReadOnly Property LogicalPayloadBytes As Long

        ''' <summary>
        ''' Logical bytes represented by encrypted allocated chunks.
        ''' </summary>
        Public ReadOnly Property EncryptedLogicalBytes As Long

        ''' <summary>
        ''' Logical bytes represented by compressed allocated chunks.
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
        ''' True when newly written chunks are currently encrypted.
        ''' </summary>
        Public ReadOnly Property IsEncryptionEnabledForNewWrites As Boolean

        ''' <summary>
        ''' Compression method currently configured for newly written chunks.
        ''' </summary>
        Public ReadOnly Property CurrentCompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

        ''' <summary>
        ''' Maximum compressed-size ratio currently allowed before storing a newly written chunk compressed.
        ''' </summary>
        Public ReadOnly Property CurrentCompressionRatioThreshold As Double

        ''' <summary>
        ''' True when newly written all-zero chunks are stored physically instead of being sparse.
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
        ''' Physical offset of the current chunk index table.
        ''' </summary>
        Public ReadOnly Property IndexOffset As Long

        ''' <summary>
        ''' Physical end offset of the current chunk index table.
        ''' </summary>
        Public ReadOnly Property IndexEndOffset As Long

        ''' <summary>
        ''' Physical offset at which chunk records may begin.
        ''' </summary>
        Public ReadOnly Property DataStartOffset As Long

        ''' <summary>
        ''' Physical end offset of the data area.
        ''' </summary>
        Public ReadOnly Property DataAreaEndOffset As Long

        ''' <summary>
        ''' Physical end offset of the highest referenced live chunk record.
        ''' </summary>
        Public ReadOnly Property LiveDataEndOffset As Long

        ''' <summary>
        ''' Fragmented bytes as a ratio of the physical data area.
        ''' </summary>
        Public ReadOnly Property FragmentationRatio As Double
            Get
                If PhysicalDataAreaBytes <= 0 Then Return 0
                Return FragmentedBytes / CDbl(PhysicalDataAreaBytes)
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
        ''' Live chunk record bytes divided by logical payload bytes.
        ''' This includes per-chunk headers, IVs and MACs but excludes global headers, the index table and holes.
        ''' </summary>
        Public ReadOnly Property ChunkRecordCompressionRatio As Double
            Get
                If LogicalPayloadBytes <= 0 Then Return 0
                Return PhysicalChunkRecordBytes / CDbl(LogicalPayloadBytes)
            End Get
        End Property

        ''' <summary>
        ''' Physical stream length divided by logical stream length.
        ''' This includes headers, index bytes, chunk overhead and fragmentation.
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
        ''' Physical payload bytes as a ratio of live chunk record bytes.
        ''' </summary>
        Public ReadOnly Property PayloadDensityRatio As Double
            Get
                If PhysicalChunkRecordBytes <= 0 Then Return 0
                Return PhysicalPayloadBytes / CDbl(PhysicalChunkRecordBytes)
            End Get
        End Property

        ''' <summary>
        ''' Metadata bytes as a ratio of the physical backing stream length.
        ''' </summary>
        Public ReadOnly Property MetadataRatio As Double
            Get
                If PhysicalLength <= 0 Then Return 0
                Return TotalMetadataBytes / CDbl(PhysicalLength)
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
                           IsInLogicalOrder As Boolean)

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
                    Return (ChunkFlags And ChunkedStream.ChunkFlags.PlaintextAllZero) = ChunkedStream.ChunkFlags.PlaintextAllZero
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
                    If Not PhysicalLength.HasValue Then Return 0
                    Return Math.Max(0L, CLng(PhysicalLength.Value) - PayloadLength)
                End Get
            End Property

            ''' <summary>
            ''' Physical chunk overhead as a ratio of physical chunk record length.
            ''' </summary>
            Public ReadOnly Property PhysicalOverheadRatio As Double
                Get
                    If Not PhysicalLength.HasValue OrElse PhysicalLength.Value <= 0 Then Return 0
                    Return PhysicalOverheadBytes / CDbl(PhysicalLength.Value)
                End Get
            End Property

            ''' <summary>
            ''' Plain bytes saved in the payload.
            ''' </summary>
            Public ReadOnly Property PayloadSpaceSavedBytes As Long
                Get
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

                If IsSparse Then
                    Return $"Chunk {Index} [Sparse, {PlainLength.FormatFileSizeFromBytes()} logical, zero={IsPlaintextAllZero}]"
                End If

                Dim EncryptionText = If(IsEncrypted, ", encrypted", "")
                Dim CompressionText =
                    If(IsCompressed,
                       $", {PayloadSpaceSavedRatio:P2} saved",
                       "")

                Dim EvaluatedCompressionText =
                    If(CompressionEvaluatedMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None,
                       $", evaluated {CompressionEvaluatedMethod} {CompressionEvaluatedRatio:P2}",
                       "")
                Dim ZeroText = If(IsPlaintextAllZero, ", zero", "")

                Return $"Chunk {Index} [{PlainLength.FormatFileSizeFromBytes()} -> {PayloadLength.FormatFileSizeFromBytes()}{CompressionText}{EvaluatedCompressionText}{EncryptionText}{ZeroText}]"

            End Function

        End Class

        ''' <summary>
        ''' Immutable read-only snapshot of a physical region in the backing stream.
        ''' </summary>
        Public NotInheritable Class Region

            Friend Sub New(Offset As Long,
                           Length As Long,
                           RegionType As RegionTypes,
                           Chunk As Chunk,
                           Description As String)

                Me.Offset = Offset
                Me.Length = Length
                Me.EndOffset = Offset + Length
                Me.RegionType = RegionType
                Me.Chunk = Chunk
                Me.Description = Description

            End Sub

            ''' <summary>
            ''' Physical region start offset.
            ''' </summary>
            Public ReadOnly Property Offset As Long

            ''' <summary>
            ''' Physical region length.
            ''' </summary>
            Public ReadOnly Property Length As Long

            ''' <summary>
            ''' Physical region end offset.
            ''' </summary>
            Public ReadOnly Property EndOffset As Long

            ''' <summary>
            ''' Region classification.
            ''' </summary>
            Public ReadOnly Property RegionType As RegionTypes

            ''' <summary>
            ''' Populated only when RegionType = Chunk. Otherwise Nothing.
            ''' </summary>
            Public ReadOnly Property Chunk As Chunk

            ''' <summary>
            ''' Human-readable description.
            ''' </summary>
            Public ReadOnly Property Description As String

            ''' <summary>
            ''' Returns a concise diagnostic summary of the physical region.
            ''' </summary>
            Public Overrides Function ToString() As String

                If RegionType = RegionTypes.Chunk AndAlso Chunk IsNot Nothing Then
                    Return $"Chunk {Chunk.Index} region [{Length.FormatFileSizeFromBytes()}]"
                End If

                Return $"{RegionType} [{Length.FormatFileSizeFromBytes()}]"

            End Function

        End Class

    End Class

End Namespace