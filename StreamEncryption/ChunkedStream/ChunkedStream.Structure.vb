Imports System.Collections.ObjectModel
Imports System.IO

Namespace Streams

    ' ================================================================================
    ' ChunkedStream Structure Snapshot API
    ' ================================================================================
    '
    ' Compatibility
    '   - Designed for .NET Framework 4.8+.
    '   - Uses only APIs available in .NET Framework 4.8.
    '   - Compatible with Option Strict On.
    '   - Does not change the on-disk format.
    '   - Does not alter ChunkedStream read/write/defrag behaviour.
    '
    ' Purpose
    '   - Provides a read-only, immutable snapshot of the current ChunkedStream layout.
    '   - Intended for diagnostics, custom visualisation, reporting, debugging and UI
    '     inspection.
    '   - Exposes typed metadata derived from existing headers, index entries and chunk
    '     record headers.
    '
    ' Design
    '   - The storage engine continues to use raw byte offsets and compact structures for
    '     performance.
    '   - This API is intentionally separate from the internal data path.
    '   - GetStructure() reads only chunk record headers, not full chunk payloads.
    '   - GetStructure() does not decrypt, decompress or validate full chunk data.
    '   - Returned objects are snapshots. They do not update if the stream changes later.
    '
    ' Region Model
    '   - HeaderA/HeaderB represent the two physical header copies.
    '   - Chunk represents live chunk records referenced by the current index.
    '   - Hole represents unreferenced physical space inside the data area.
    '   - Index represents the current chunk index table.
    '   - Unused represents bytes beyond the current index table, if any.
    '
    ' Notes
    '   - Sparse chunks have no physical record.
    '   - Sparse chunks expose CompressionMethod = None and EncryptionMethod = None.
    '   - Physical offsets, physical sizes and physical-order properties are nullable for
    '     sparse chunks because no physical record exists.
    '
    ' ================================================================================

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
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PayloadLength As Integer
        End Structure

        Private Structure ChunkHeaderSnapshot
            Public CompressionMethod As ChunkedStreamOptions.CompressionMethods
            Public EncryptionMethod As ChunkEncryptionMethods
            Public PlainLength As Integer
            Public PayloadLength As Integer
        End Structure

        ''' <summary>
        ''' Returns an immutable snapshot of the current physical and logical stream structure.
        ''' </summary>
        ''' <returns>A read-only ChunkedStreamStructure snapshot.</returns>
        Public Function GetStructure() As ChunkedStreamStructure

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Dim BuildInfos As New List(Of StructureChunkBuildInfo)(_Index.Count)

                Dim AllocatedChunkCount = 0
                Dim SparseChunkCount = 0
                Dim EncryptedChunkCount = 0
                Dim UnencryptedChunkCount = 0
                Dim CompressedChunkCount = 0

                Dim PhysicalChunkRecordBytes As Long = 0
                Dim PayloadBytes As Long = 0
                Dim PlainBytesRepresentedByAllocatedChunks As Long = 0
                Dim EncryptedLogicalBytes As Long = 0
                Dim CompressedLogicalBytes As Long = 0

                For ChunkIndex = 0 To _Index.Count - 1

                    Dim Entry = _Index(ChunkIndex)
                    Dim LogicalOffset = CLng(ChunkIndex) * ChunkSize
                    Dim PlainLength = GetLogicalPlainLengthForChunk(ChunkIndex)

                    If Entry.Offset <> 0 AndAlso Entry.RecordLength > 0 Then

                        Dim Header = ReadChunkHeaderSnapshot(ChunkIndex, Entry)

                        AllocatedChunkCount += 1
                        PhysicalChunkRecordBytes += Entry.RecordLength
                        PayloadBytes += Header.PayloadLength
                        PlainBytesRepresentedByAllocatedChunks += Header.PlainLength

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
                            .LogicalEndOffset = LogicalOffset + PlainLength,
                            .PlainLength = Header.PlainLength,
                            .IsAllocated = True,
                            .PhysicalOffset = Entry.Offset,
                            .PhysicalLength = Entry.RecordLength,
                            .CompressionMethod = Header.CompressionMethod,
                            .EncryptionMethod = Header.EncryptionMethod,
                            .PayloadLength = Header.PayloadLength
                        })

                    Else

                        SparseChunkCount += 1

                        BuildInfos.Add(New StructureChunkBuildInfo With {
                            .Index = ChunkIndex,
                            .LogicalOffset = LogicalOffset,
                            .LogicalEndOffset = LogicalOffset + PlainLength,
                            .PlainLength = PlainLength,
                            .IsAllocated = False,
                            .PhysicalOffset = 0,
                            .PhysicalLength = 0,
                            .CompressionMethod = ChunkedStreamOptions.CompressionMethods.None,
                            .EncryptionMethod = ChunkEncryptionMethods.None,
                            .PayloadLength = 0
                        })

                    End If

                Next

                Dim PreviousGapByChunkIndex As New Dictionary(Of Integer, Long)
                Dim NextGapByChunkIndex As New Dictionary(Of Integer, Long)
                Dim PhysicalOrderByChunkIndex As New Dictionary(Of Integer, Integer)
                Dim ContiguousWithPreviousByChunkIndex As New Dictionary(Of Integer, Boolean)
                Dim ContiguousWithNextByChunkIndex As New Dictionary(Of Integer, Boolean)
                Dim InLogicalOrderByChunkIndex As New Dictionary(Of Integer, Boolean)

                Dim AllocatedInPhysicalOrder =
                    BuildInfos.
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

                        PreviousGapByChunkIndex(Current.Index) = PreviousGap
                        ContiguousWithPreviousByChunkIndex(Current.Index) = PreviousGap = 0
                        InLogicalOrderByChunkIndex(Current.Index) = Previous.Index < Current.Index
                    End If

                    If PhysicalOrder = AllocatedInPhysicalOrder.Count - 1 Then
                        Dim CurrentEnd = Current.PhysicalOffset + Current.PhysicalLength
                        Dim NextGap = Math.Max(0L, _IndexOffset - CurrentEnd)

                        NextGapByChunkIndex(Current.Index) = NextGap
                        ContiguousWithNextByChunkIndex(Current.Index) = NextGap = 0
                    Else
                        Dim [Next] = AllocatedInPhysicalOrder(PhysicalOrder + 1)
                        Dim CurrentEnd = Current.PhysicalOffset + Current.PhysicalLength
                        Dim NextGap = Math.Max(0L, [Next].PhysicalOffset - CurrentEnd)

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
                        EncryptionMethod:=BuildInfo.EncryptionMethod,
                        PayloadLength:=BuildInfo.PayloadLength,
                        PreviousPhysicalGap:=PreviousPhysicalGap,
                        NextPhysicalGap:=NextPhysicalGap,
                        IsPhysicallyContiguousWithPrevious:=BuildInfo.IsAllocated AndAlso ContiguousWithPreviousByChunkIndex(BuildInfo.Index),
                        IsPhysicallyContiguousWithNext:=BuildInfo.IsAllocated AndAlso ContiguousWithNextByChunkIndex(BuildInfo.Index),
                        IsInLogicalOrder:=BuildInfo.IsAllocated AndAlso InLogicalOrderByChunkIndex(BuildInfo.Index)))

                Next

                Dim IndexBytes = CLng(_Index.Count) * IndexEntrySize
                Dim DataAreaBytes = Math.Max(0L, _IndexOffset - DataStartOffset)
                Dim FragmentedBytes = Math.Max(0L, DataAreaBytes - PhysicalChunkRecordBytes)
                Dim Fragmentation = If(DataAreaBytes = 0, 0.0R, FragmentedBytes / CDbl(DataAreaBytes))

                Dim Regions = BuildPhysicalRegions(
                    AllocatedInPhysicalOrder,
                    Chunks,
                    IndexBytes)

                Dim WrapMode = CType(BitConverter.ToInt32(_Header, MasterKeyWrapModeOffset), MasterKeyWrapModes)

                Dim EncryptedCoveragePercent =
                    If(_Length = 0,
                       0.0R,
                       Math.Min(100.0R, (EncryptedLogicalBytes / CDbl(_Length)) * 100.0R))

                Dim CompressedCoveragePercent =
                    If(_Length = 0,
                       0.0R,
                       Math.Min(100.0R, (CompressedLogicalBytes / CDbl(_Length)) * 100.0R))

                Return New ChunkedStreamStructure(
                    LogicalLength:=_Length,
                    PhysicalLength:=_Fs.Length,
                    ChunkSize:=ChunkSize,
                    ChunkCount:=_Index.Count,
                    AllocatedChunkCount:=AllocatedChunkCount,
                    SparseChunkCount:=SparseChunkCount,
                    EncryptedChunkCount:=EncryptedChunkCount,
                    UnencryptedChunkCount:=UnencryptedChunkCount,
                    CompressedChunkCount:=CompressedChunkCount,
                    PhysicalHeaderBytes:=DataStartOffset,
                    PhysicalDataAreaBytes:=DataAreaBytes,
                    PhysicalChunkRecordBytes:=PhysicalChunkRecordBytes,
                    PhysicalPayloadBytes:=PayloadBytes,
                    PhysicalIndexBytes:=IndexBytes,
                    FragmentedBytes:=FragmentedBytes,
                    Fragmentation:=Fragmentation,
                    PlainBytesRepresentedByAllocatedChunks:=PlainBytesRepresentedByAllocatedChunks,
                    EncryptedLogicalBytes:=EncryptedLogicalBytes,
                    CompressedLogicalBytes:=CompressedLogicalBytes,
                    EncryptedCoveragePercent:=EncryptedCoveragePercent,
                    CompressedCoveragePercent:=CompressedCoveragePercent,
                    HasFileMasterKey:=_FileMasterKey IsNot Nothing,
                    HasWrappedFileMasterKey:=WrapMode <> MasterKeyWrapModes.None,
                    IsFileMasterKeyPubliclyWrapped:=WrapMode = MasterKeyWrapModes.PublicWrap,
                    IsFileMasterKeyUserWrapped:=WrapMode = MasterKeyWrapModes.UserWrap,
                    IsEncryptionEnabledForNewWrites:=_CurrentWriteEncryptionEnabled,
                    CurrentCompressionMethod:=Options.CompressionMethod,
                    CurrentCompressionMinimumSavingsPercent:=Options.CompressionMinimumSavingsPercent,
                    CurrentStoreSparseChunks:=Options.StoreSparseChunks,
                    HeaderSequence:=_HeaderSequence,
                    ActiveHeaderCopy:=_ActiveHeaderCopy,
                    IndexOffset:=_IndexOffset,
                    IndexEndOffset:=_IndexOffset + IndexBytes,
                    DataStartOffset:=DataStartOffset,
                    DataAreaEndOffset:=_IndexOffset,
                    LiveDataEndOffset:=GetDataEndFromIndex(),
                    Chunks:=Chunks,
                    Regions:=Regions)

            End SyncLock

        End Function

        Private Function GetLogicalPlainLengthForChunk(ChunkIndex As Integer) As Integer

            Dim LogicalOffset = CLng(ChunkIndex) * ChunkSize
            Dim Remaining = _Length - LogicalOffset

            If Remaining <= 0 Then
                Return 0
            End If

            Return CInt(Math.Min(CLng(ChunkSize), Remaining))

        End Function

        Private Function ReadChunkHeaderSnapshot(ExpectedChunkIndex As Integer,
                                                 Entry As ChunkIndexEntry) As ChunkHeaderSnapshot

            If Entry.Offset < DataStartOffset Then
                Throw New InvalidDataException($"Invalid chunk offset for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.RecordLength < MinChunkRecordSize Then
                Throw New InvalidDataException($"Invalid chunk record length for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.Offset + Entry.RecordLength > _IndexOffset Then
                Throw New InvalidDataException($"Chunk {ExpectedChunkIndex} extends beyond the current data area.")
            End If

            Dim Header(ChunkRecordHeaderSize - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Header, 0, Header.Length)

            Dim StoredChunkIndex = BitConverter.ToInt64(Header, 0)

            If StoredChunkIndex <> ExpectedChunkIndex Then
                Throw New InvalidDataException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {StoredChunkIndex}.")
            End If

            Dim CompressionMethod =
                CType(BitConverter.ToInt32(Header, ChunkCompressionMethodOffset),
                      ChunkedStreamOptions.CompressionMethods)

            Dim EncryptionMethod =
                CType(BitConverter.ToInt32(Header, ChunkEncryptionMethodOffset),
                      ChunkEncryptionMethods)

            Dim PlainLength = BitConverter.ToInt32(Header, ChunkPlainLengthOffset)
            Dim PayloadLength = BitConverter.ToInt32(Header, ChunkPayloadLengthOffset)

            If PlainLength < 0 OrElse PlainLength > ChunkSize Then
                Throw New InvalidDataException($"Invalid plain length for chunk {ExpectedChunkIndex}.")
            End If

            If PayloadLength < 0 Then
                Throw New InvalidDataException($"Invalid payload length for chunk {ExpectedChunkIndex}.")
            End If

            If ChunkRecordDataOffset + PayloadLength + MacSize <> Entry.RecordLength Then
                Throw New InvalidDataException($"Invalid chunk record length for chunk {ExpectedChunkIndex}.")
            End If

            Return New ChunkHeaderSnapshot With {
                .CompressionMethod = CompressionMethod,
                .EncryptionMethod = EncryptionMethod,
                .PlainLength = PlainLength,
                .PayloadLength = PayloadLength
            }

        End Function

        Private Function BuildPhysicalRegions(AllocatedInPhysicalOrder As IList(Of StructureChunkBuildInfo),
                                              Chunks As IList(Of ChunkedStreamStructure.Chunk),
                                              IndexBytes As Long) As List(Of ChunkedStreamStructure.Region)

            Dim Regions As New List(Of ChunkedStreamStructure.Region)

            Dim ChunkLookup =
                Chunks.ToDictionary(
                    Function(chunk) chunk.Index)

            Regions.Add(
                New ChunkedStreamStructure.Region(
                    Offset:=0,
                    Length:=HeaderSize,
                    RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                    Chunk:=Nothing,
                    Description:="Header A"))

            Regions.Add(
                New ChunkedStreamStructure.Region(
                    Offset:=HeaderSize,
                    Length:=HeaderSize,
                    RegionType:=ChunkedStreamStructure.RegionTypes.Header,
                    Chunk:=Nothing,
                    Description:="Header B"))

            Dim Cursor = CLng(DataStartOffset)

            For Each Entry In AllocatedInPhysicalOrder

                If Entry.PhysicalOffset > Cursor Then

                    Regions.Add(
                        New ChunkedStreamStructure.Region(
                            Offset:=Cursor,
                            Length:=Entry.PhysicalOffset - Cursor,
                            RegionType:=ChunkedStreamStructure.RegionTypes.Hole,
                            Chunk:=Nothing,
                            Description:="Unreferenced data area"))

                End If

                Regions.Add(
                    New ChunkedStreamStructure.Region(
                        Offset:=Entry.PhysicalOffset,
                        Length:=Entry.PhysicalLength,
                        RegionType:=ChunkedStreamStructure.RegionTypes.Chunk,
                        Chunk:=ChunkLookup(Entry.Index),
                        Description:=$"Chunk {Entry.Index}"))

                Cursor =
                    Math.Max(
                        Cursor,
                        Entry.PhysicalOffset + CLng(Entry.PhysicalLength))

            Next

            If Cursor < _IndexOffset Then

                Regions.Add(
                    New ChunkedStreamStructure.Region(
                        Offset:=Cursor,
                        Length:=_IndexOffset - Cursor,
                        RegionType:=ChunkedStreamStructure.RegionTypes.Hole,
                        Chunk:=Nothing,
                        Description:="Unreferenced data area"))

            End If

            If IndexBytes > 0 Then

                Regions.Add(
                    New ChunkedStreamStructure.Region(
                        Offset:=_IndexOffset,
                        Length:=IndexBytes,
                        RegionType:=ChunkedStreamStructure.RegionTypes.Index,
                        Chunk:=Nothing,
                        Description:="Chunk Index Table"))

            End If

            Dim IndexEnd = _IndexOffset + IndexBytes

            If _Fs.Length > IndexEnd Then

                Regions.Add(
                    New ChunkedStreamStructure.Region(
                        Offset:=IndexEnd,
                        Length:=_Fs.Length - IndexEnd,
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
            ''' Unreferenced space inside the data area.
            ''' </summary>
            Hole = 2

            ''' <summary>
            ''' Current chunk index table.
            ''' </summary>
            Index = 3

            ''' <summary>
            ''' Bytes beyond the current index table.
            ''' </summary>
            Unused = 4

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
                       PhysicalHeaderBytes As Long,
                       PhysicalDataAreaBytes As Long,
                       PhysicalChunkRecordBytes As Long,
                       PhysicalPayloadBytes As Long,
                       PhysicalIndexBytes As Long,
                       FragmentedBytes As Long,
                       Fragmentation As Double,
                       PlainBytesRepresentedByAllocatedChunks As Long,
                       EncryptedLogicalBytes As Long,
                       CompressedLogicalBytes As Long,
                       EncryptedCoveragePercent As Double,
                       CompressedCoveragePercent As Double,
                       HasFileMasterKey As Boolean,
                       HasWrappedFileMasterKey As Boolean,
                       IsFileMasterKeyPubliclyWrapped As Boolean,
                       IsFileMasterKeyUserWrapped As Boolean,
                       IsEncryptionEnabledForNewWrites As Boolean,
                       CurrentCompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods,
                       CurrentCompressionMinimumSavingsPercent As Integer,
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
            Me.PhysicalHeaderBytes = PhysicalHeaderBytes
            Me.PhysicalDataAreaBytes = PhysicalDataAreaBytes
            Me.PhysicalChunkRecordBytes = PhysicalChunkRecordBytes
            Me.PhysicalPayloadBytes = PhysicalPayloadBytes
            Me.PhysicalIndexBytes = PhysicalIndexBytes
            Me.FragmentedBytes = FragmentedBytes
            Me.Fragmentation = Fragmentation
            Me.PlainBytesRepresentedByAllocatedChunks = PlainBytesRepresentedByAllocatedChunks
            Me.EncryptedLogicalBytes = EncryptedLogicalBytes
            Me.CompressedLogicalBytes = CompressedLogicalBytes
            Me.EncryptedCoveragePercent = EncryptedCoveragePercent
            Me.CompressedCoveragePercent = CompressedCoveragePercent
            Me.HasFileMasterKey = HasFileMasterKey
            Me.HasWrappedFileMasterKey = HasWrappedFileMasterKey
            Me.IsFileMasterKeyPubliclyWrapped = IsFileMasterKeyPubliclyWrapped
            Me.IsFileMasterKeyUserWrapped = IsFileMasterKeyUserWrapped
            Me.IsEncryptionEnabledForNewWrites = IsEncryptionEnabledForNewWrites
            Me.CurrentCompressionMethod = CurrentCompressionMethod
            Me.CurrentCompressionMinimumSavingsPercent = CurrentCompressionMinimumSavingsPercent
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

        Public ReadOnly Property LogicalLength As Long

        Public ReadOnly Property PhysicalLength As Long

        Public ReadOnly Property ChunkSize As Integer

        Public ReadOnly Property ChunkCount As Integer

        Public ReadOnly Property AllocatedChunkCount As Integer

        Public ReadOnly Property SparseChunkCount As Integer

        Public ReadOnly Property EncryptedChunkCount As Integer

        Public ReadOnly Property UnencryptedChunkCount As Integer

        Public ReadOnly Property CompressedChunkCount As Integer

        Public ReadOnly Property PhysicalHeaderBytes As Long

        Public ReadOnly Property PhysicalDataAreaBytes As Long

        Public ReadOnly Property PhysicalChunkRecordBytes As Long

        Public ReadOnly Property PhysicalPayloadBytes As Long

        Public ReadOnly Property PhysicalIndexBytes As Long

        Public ReadOnly Property FragmentedBytes As Long

        Public ReadOnly Property Fragmentation As Double

        Public ReadOnly Property PlainBytesRepresentedByAllocatedChunks As Long

        Public ReadOnly Property EncryptedLogicalBytes As Long

        Public ReadOnly Property CompressedLogicalBytes As Long

        Public ReadOnly Property EncryptedCoveragePercent As Double

        Public ReadOnly Property CompressedCoveragePercent As Double

        Public ReadOnly Property HasFileMasterKey As Boolean

        Public ReadOnly Property HasWrappedFileMasterKey As Boolean

        Public ReadOnly Property IsFileMasterKeyPubliclyWrapped As Boolean

        Public ReadOnly Property IsFileMasterKeyUserWrapped As Boolean

        Public ReadOnly Property IsEncryptionEnabledForNewWrites As Boolean

        Public ReadOnly Property CurrentCompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

        Public ReadOnly Property CurrentCompressionMinimumSavingsPercent As Integer

        Public ReadOnly Property CurrentStoreSparseChunks As Boolean

        Public ReadOnly Property HeaderSequence As Long

        Public ReadOnly Property ActiveHeaderCopy As Integer

        Public ReadOnly Property IndexOffset As Long

        Public ReadOnly Property IndexEndOffset As Long

        Public ReadOnly Property DataStartOffset As Long

        Public ReadOnly Property DataAreaEndOffset As Long

        Public ReadOnly Property LiveDataEndOffset As Long

        Private ReadOnly _Chunks As ReadOnlyCollection(Of Chunk)

        Public ReadOnly Property Chunks As IReadOnlyList(Of Chunk)
            Get
                Return _Chunks
            End Get
        End Property

        Private ReadOnly _Regions As ReadOnlyCollection(Of Region)

        Public ReadOnly Property Regions As IReadOnlyList(Of Region)
            Get
                Return _Regions
            End Get
        End Property

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
                           EncryptionMethod As ChunkedStream.ChunkEncryptionMethods,
                           PayloadLength As Integer,
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
                Me.EncryptionMethod = EncryptionMethod
                Me.PayloadLength = PayloadLength
                Me.PreviousPhysicalGap = PreviousPhysicalGap
                Me.NextPhysicalGap = NextPhysicalGap
                Me.IsPhysicallyContiguousWithPrevious = IsPhysicallyContiguousWithPrevious
                Me.IsPhysicallyContiguousWithNext = IsPhysicallyContiguousWithNext
                Me.IsInLogicalOrder = IsInLogicalOrder

            End Sub

            Public ReadOnly Property Index As Integer

            Public ReadOnly Property LogicalOffset As Long

            Public ReadOnly Property LogicalEndOffset As Long

            Public ReadOnly Property PlainLength As Integer

            Public ReadOnly Property IsSparse As Boolean

            Public ReadOnly Property IsAllocated As Boolean

            Public ReadOnly Property PhysicalOffset As Long?

            Public ReadOnly Property PhysicalLength As Integer?

            Public ReadOnly Property PhysicalEndOffset As Long?

            Public ReadOnly Property PhysicalOrder As Integer?

            Public ReadOnly Property CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods

            Public ReadOnly Property EncryptionMethod As ChunkedStream.ChunkEncryptionMethods

            Public ReadOnly Property PayloadLength As Integer

            Public ReadOnly Property PreviousPhysicalGap As Long?

            Public ReadOnly Property NextPhysicalGap As Long?

            Public ReadOnly Property IsPhysicallyContiguousWithPrevious As Boolean

            Public ReadOnly Property IsPhysicallyContiguousWithNext As Boolean

            Public ReadOnly Property IsInLogicalOrder As Boolean

            Public ReadOnly Property IsCompressed As Boolean
                Get
                    Return CompressionMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None
                End Get
            End Property

            Public ReadOnly Property IsEncrypted As Boolean
                Get
                    Return EncryptionMethod <> ChunkedStream.ChunkEncryptionMethods.None
                End Get
            End Property

            Public ReadOnly Property PhysicalOverheadBytes As Long
                Get
                    If Not PhysicalLength.HasValue Then
                        Return 0
                    End If

                    Return Math.Max(0L, CLng(PhysicalLength.Value) - PayloadLength)
                End Get
            End Property

            Public ReadOnly Property PayloadSpaceSavedBytes As Long
                Get
                    Return Math.Max(0L, CLng(PlainLength) - PayloadLength)
                End Get
            End Property

            Public ReadOnly Property PayloadSpaceSavedPercent As Double
                Get
                    If PlainLength <= 0 Then
                        Return 0
                    End If

                    Return Math.Max(0.0R, (PayloadSpaceSavedBytes / CDbl(PlainLength)) * 100.0R)
                End Get
            End Property

            Public ReadOnly Property PayloadRatio As Double
                Get
                    If PlainLength <= 0 Then
                        Return 0
                    End If

                    Return PayloadLength / CDbl(PlainLength)
                End Get
            End Property

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
            ''' Populated only when RegionType = Chunk.
            ''' Otherwise Nothing.
            ''' </summary>
            Public ReadOnly Property Chunk As Chunk

            ''' <summary>
            ''' Human-readable description.
            ''' </summary>
            Public ReadOnly Property Description As String

        End Class

    End Class

End Namespace