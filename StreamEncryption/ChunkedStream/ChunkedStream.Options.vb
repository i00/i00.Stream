' ================================================================================
' ChunkedStream Options
' ================================================================================
'
' Purpose
'   - Runtime configuration and option-application support.
'
' Features
'   - Compression configuration.
'   - Compression ratio thresholds.
'   - Sparse chunk policy.
'   - Encryption configuration.
'   - Chunk read-cache configuration.
'   - Chunk size configuration.
'   - New chunk write-location configuration.
'
' Design
'   - Options affect newly written physical records.
'   - Existing physical records retain their original representation until rewritten.
'   - Most option changes do not immediately rewrite existing physical records.
'   - ApplyOptions may be used to rewrite existing extents / physical records using
'     the currently configured policies.
'
' Chunk Size
'   - Stored in the file header as the current preferred write size.
'   - Existing streams automatically load their stored chunk size.
'   - Changing ChunkSize does not immediately affect existing extents.
'   - ApplyOptions(ApplyOptionTypes.ChunkSize) rewrites the logical stream using the
'     current Options.ChunkSize.
'
' ================================================================================
Imports System.Linq

Namespace Streams
    Partial Class ChunkedStream

        ''' <summary>
        ''' Options controlling newly written chunks.
        ''' Existing chunk records retain their original compression, encryption and sparse representation until rewritten.
        ''' </summary>
        Public Class ChunkedStreamOptions

            Public Enum ExtentReclaimTypes
                RefCount = 0
                Scan = 1
            End Enum

            ''' <summary>
            ''' Controls how unreferenced physical records are identified for reuse.
            ''' </summary>
            Public Property ExtentReclaimType As ExtentReclaimTypes = ExtentReclaimTypes.RefCount

            Private _BisectLimit As Integer = 0

            ''' <summary>
            ''' Minimum preferred fragment size when bisecting an existing extent.
            ''' A value of 0 always allows bisection.
            ''' </summary>
            ''' <remarks>
            ''' This is a best-effort editing policy. It avoids unnecessary tiny fragments
            ''' where practical, but it does not prevent small extents from existing when
            ''' they are the natural result of the requested operation.
            ''' </remarks>
            Public Property BisectLimit As Integer
                Get
                    Return _BisectLimit
                End Get
                Set
                    If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(BisectLimit))
                    _BisectLimit = Value
                End Set
            End Property

            Private _ChunkSize As Integer = ChunkedStream.DefaultChunkSize

            ''' <summary>
            ''' Preferred logical segment size used when writing new physical records and during ApplyOptions chunk-size rewrites.
            ''' </summary>
            ''' <remarks>
            ''' Opening an existing stream updates this property to the chunk size stored in the stream.
            ''' Changing this property does not immediately affect existing extents.
            ''' To apply a new chunk size to an existing stream, set this property and call ApplyOptions(ApplyOptionTypes.ChunkSize).
            ''' </remarks>
            Public Property ChunkSize As Integer
                Get
                    Return _ChunkSize
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkSize))
                    If Value = _ChunkSize Then Return
                    _ChunkSize = Value
#If DEBUG Then
                    Debug.Print($"ChunkedStream chunk size changed to {Value:N0} bytes. Existing extents will not be affected until {NameOf(ApplyOptions)}({NameOf(ApplyOptionTypes.ChunkSize)}) or {NameOf(Defragment)}({NameOf(DefragTypes.Rebuild)}) is performed.")
#End If
                End Set
            End Property

            Private _IndexPageEntryCount As Integer = 256
            Private _IndexDirectoryEntryCount As Integer = 256

            ''' <summary>
            ''' Controls where newly written storage records are placed.
            ''' </summary>
            Public Enum NewWriteLocationPolicies
                ''' <summary>
                ''' Always writes new records at the current append position.
                ''' This avoids free-space lookup overhead, but may increase fragmentation
                ''' and physical stream growth until defragmentation or rebuild is performed.
                ''' </summary>
                Append = 0

                ''' <summary>
                ''' Reuses known free spaces when a suitable space is already available in
                ''' memory or has been loaded from a stored hole directory.
                ''' This does not scan the existing physical stream layout to discover
                ''' unknown holes.
                ''' </summary>
                FillHoles = 1

                ''' <summary>
                ''' Reuses known free spaces and, when no suitable known space exists,
                ''' may rebuild the free-space map by scanning the active stream layout
                ''' from the start.
                ''' This can reclaim holes that were not already known, but may add
                ''' extra write-time overhead when the scan is required.
                ''' </summary>
                FillHolesFromStart = 2
            End Enum

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written physical chunk records.
            ''' </summary>
            ''' <remarks>
            ''' This setting affects newly written chunk records only. Existing layout is not
            ''' reorganised by changing this value. Use Defragment to actively compact or reorder
            ''' existing records.
            '''
            ''' When a checkpoint is active, ChunkedStream always uses append behaviour to preserve
            ''' checkpoint rollback and crash-recovery semantics.
            ''' </remarks>
            Public Property NewChunkWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.FillHoles

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written index pages.
            ''' </summary>
            Public Property NewIndexPageWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.FillHoles

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written index-directory pages.
            ''' </summary>
            Public Property NewIndexDirectoryPageWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.FillHoles

            ''' <summary>
            ''' Number of extent or physical-record entries stored in each authenticated metadata page.
            ''' </summary>
            Public Property IndexPageEntryCount As Integer
                Get
                    Return _IndexPageEntryCount
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(IndexPageEntryCount))
                    _IndexPageEntryCount = Value
                End Set
            End Property

            ''' <summary>
            ''' Number of directory entries stored in each authenticated index-directory page.
            ''' </summary>
            Public Property IndexDirectoryEntryCount As Integer
                Get
                    Return _IndexDirectoryEntryCount
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(IndexDirectoryEntryCount))
                    _IndexDirectoryEntryCount = Value
                End Set
            End Property

            ''' <summary>
            ''' Controls whether reusable free-space information is persisted as metadata.
            ''' </summary>
            Public Enum HoleDirectoryModes
                ''' <summary>
                ''' Never persist reusable free-space information.
                ''' Known holes created during the current open stream session may still be reused in memory.
                ''' </summary>
                Never = 0

                ''' <summary>
                ''' Persist reusable free-space information only once the stream reaches
                ''' HoleDirectoryAutoThresholdBytes.
                ''' </summary>
                Auto = 1

                ''' <summary>
                ''' Always persist reusable free-space information when metadata is published.
                ''' </summary>
                Always = 2
            End Enum

            ''' <summary>
            ''' Gets or sets when reusable free-space information should be persisted.
            ''' </summary>
            ''' <remarks>
            ''' A stored hole directory is an acceleration structure only. Correctness must not
            ''' depend on it. If the hole directory is missing, disabled or invalid, reusable
            ''' free space can still be discovered by scanning the active stream layout when
            ''' the selected write-location policy allows it.
            ''' </remarks>
            Public Property HoleDirectoryMode As HoleDirectoryModes = HoleDirectoryModes.Auto

            Private _HoleDirectoryAutoThresholdBytes As Long = 1024L * 1024L

            ''' <summary>
            ''' Minimum physical stream size before hole directories are persisted when
            ''' HoleDirectoryMode is Auto.
            ''' </summary>
            Public Property HoleDirectoryAutoThresholdBytes As Long
                Get
                    Return _HoleDirectoryAutoThresholdBytes
                End Get
                Set
                    If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(HoleDirectoryAutoThresholdBytes))
                    _HoleDirectoryAutoThresholdBytes = Value
                End Set
            End Property

            ''' <summary>
            ''' Raised when EncryptionInfo changes.
            ''' </summary>
            Friend Event EncryptionInfoChanged(OldValue As EncryptionInfo, NewValue As EncryptionInfo)

            Private _EncryptionInfo As EncryptionInfo

            ''' <summary>
            ''' Compression algorithm applied to individual chunk records.
            ''' </summary>
            Public Enum CompressionMethods As Integer
                ''' <summary>
                ''' Store the chunk payload uncompressed.
                ''' </summary>
                None = 0

                ' ================================================================================
                ' Inbuilt:
                ' ================================================================================
                ''' <summary>
                ''' Store the chunk payload using Deflate compression.
                ''' </summary>
                Deflate = 1

                ''' <summary>
                ''' Store the chunk payload using GZip compression.
                ''' </summary>
                GZip = 2

                ' ================================================================================
                ' Custom:
                ' ================================================================================
                ''' <summary>
                ''' Store the chunk payload using LZ4 block compression.
                ''' </summary>
                Lz4 = 3

                ''' <summary>
                ''' Store the chunk payload using Snappy block compression.
                ''' </summary>
                Snappy = 4
            End Enum

            ''' <summary>
            ''' Compression method used for newly written chunks.
            ''' </summary>
            Public Property CompressionMethod As CompressionMethods = CompressionMethods.None

            ''' <summary>
            ''' Maximum compressed-size ratio allowed before a chunk is stored compressed.
            ''' </summary>
            ''' <remarks>
            ''' A value of 0.95 means the compressed payload must be no larger than 95% of the original plaintext size.
            ''' Lower values require better compression before storing the chunk compressed.
            ''' </remarks>
            Public Property CompressionRatioThreshold As Double = 0.95R

            ''' <summary>
            ''' If True, all-zero chunks are stored as physical authenticated chunk records.
            ''' If False, all-zero chunks are represented by sparse index entries.
            ''' </summary>
            Public Property StoreSparseChunks As Boolean = False

            ''' <summary>
            ''' Enables caching of the most recently read plaintext chunk.
            ''' </summary>
            ''' <remarks>
            ''' Disabling this avoids the extra cache copy on chunk reads, but repeated reads of the same chunk may require repeated stream reads, MAC validation, decompression and decryption.
            ''' </remarks>
            Public Property UseChunkReadCache As Boolean = True

            ''' <summary>
            ''' Encryption information used for newly written chunks.
            ''' Setting this to Nothing disables encryption for newly written chunks.
            ''' Existing encrypted chunks remain readable if the file master key is available.
            ''' </summary>
            Public Property EncryptionInfo As EncryptionInfo
                Get
                    Return _EncryptionInfo
                End Get
                Set
                    If Object.ReferenceEquals(_EncryptionInfo, Value) Then Return
                    Dim OldValue = _EncryptionInfo
                    _EncryptionInfo = Value
                    RaiseEvent EncryptionInfoChanged(OldValue, Value)
                End Set
            End Property

        End Class

        ''' <summary>
        ''' Option categories that can be applied to existing chunks.
        ''' </summary>
        <Flags>
        Public Enum ApplyOptionTypes
            ''' <summary>
            ''' Do not apply any options.
            ''' </summary>
            None = 0

            ''' <summary>
            ''' Apply the current compression options to physical records that do not currently satisfy them.
            ''' </summary>
            Compression = 1 << 0

            ''' <summary>
            ''' Apply the current encryption state to physical records that do not currently satisfy it.
            ''' </summary>
            Encryption = 1 << 1

            ''' <summary>
            ''' Apply the current sparse-storage setting to extents that do not currently satisfy it.
            ''' </summary>
            Sparseness = 1 << 2

            ''' <summary>
            ''' Rewrite extents into physical records using the current Options.ChunkSize.
            ''' </summary>
            ChunkSize = 1 << 3

            All = Compression Or Encryption Or Sparseness Or ChunkSize
        End Enum

        ''' <summary>
        ''' Summary of an ApplyOptions operation.
        ''' </summary>
        Public NotInheritable Class ApplyOptionsResult

            ''' <summary>
            ''' Number of extents rewritten because their logical size did not match the requested chunk-size policy.
            ''' </summary>
            Public Property ChunkSizeChanges As Integer

            ''' <summary>
            ''' Number of chunks examined by the operation.
            ''' </summary>
            Public Property ExaminedChunks As Integer

            ''' <summary>
            ''' Number of chunks physically rewritten or converted between sparse and allocated forms.
            ''' </summary>
            Public Property RewrittenChunks As Integer

            ''' <summary>
            ''' Number of chunks rewritten because compression did not match the requested options.
            ''' </summary>
            Public Property CompressionChanges As Integer

            ''' <summary>
            ''' Number of chunks rewritten because encryption did not match the requested options.
            ''' </summary>
            Public Property EncryptionChanges As Integer

            ''' <summary>
            ''' Number of chunks rewritten or converted because sparse storage did not match the requested options.
            ''' </summary>
            Public Property SparsenessChanges As Integer

            ''' <summary>
            ''' Number of allocated chunks converted to sparse chunks.
            ''' </summary>
            Public Property NewlySparseChunks As Integer

            ''' <summary>
            ''' Number of sparse chunks converted to allocated physical chunk records.
            ''' </summary>
            Public Property NewlyAllocatedChunks As Integer

            ''' <summary>
            ''' Physical stream length before the operation.
            ''' </summary>
            Public Property PhysicalLengthBefore As Long

            ''' <summary>
            ''' Physical stream length after the operation.
            ''' </summary>
            Public Property PhysicalLengthAfter As Long

            ''' <summary>
            ''' True when the operation was cancelled through the progress callback.
            ''' </summary>
            Public Property WasCancelled As Boolean

            ''' <summary>
            ''' Physical byte delta after the operation.
            ''' Positive values mean the backing stream grew. Negative values mean it shrank.
            ''' </summary>
            Public ReadOnly Property PhysicalBytesChanged As Long
                Get
                    Return PhysicalLengthAfter - PhysicalLengthBefore
                End Get
            End Property

            ''' <summary>
            ''' Returns a concise diagnostic summary of the operation.
            ''' </summary>
            Public Overrides Function ToString() As String
                Return $"ApplyOptions [examined={ExaminedChunks}, rewritten={RewrittenChunks}, " &
                       $"compression={CompressionChanges}, encryption={EncryptionChanges}, sparse={SparsenessChanges}, " &
                       $"chunkSize={ChunkSizeChanges}, physicalDelta={PhysicalBytesChanged.FormatFileSizeFromBytes()}, cancelled={WasCancelled}]"
            End Function

        End Class

        ''' <summary>
        ''' Applies the current data options to existing extents and physical records that do not currently satisfy the selected option categories.
        ''' </summary>
        ''' <param name="Types">
        ''' Option categories to apply.
        ''' </param>
        ''' <param name="ProgressCallback">
        ''' Optional progress callback.
        ''' </param>
        ''' <param name="Durable">
        ''' If True, metadata publication is flushed durably when this call publishes metadata.
        ''' </param>
        ''' <returns>
        ''' A summary of the operation.
        ''' </returns>
        Public Function ApplyOptions(Optional Types As ApplyOptionTypes = ApplyOptionTypes.All,
                                     Optional ProgressCallback As StreamProgressCallback = Nothing,
                                     Optional Durable As Boolean = True) As ApplyOptionsResult

            SyncLock _SyncRoot

                ThrowIfDisposed()
                InvalidateChunkCache()

                Dim Result As New ApplyOptionsResult With {
                    .PhysicalLengthBefore = _Fs.Length,
                    .PhysicalLengthAfter = _Fs.Length
                }

                If Types = ApplyOptionTypes.None Then
                    Return Result
                End If

                Dim CancellationToken As New CancellationToken()

                If Types.HasFlag(ApplyOptionTypes.ChunkSize) AndAlso NeedsChunkSizeRewrite() Then

                    ApplyChunkSizeOptions(Result, ProgressCallback, CancellationToken)

                    If CancellationToken.Cancel Then
                        Result.WasCancelled = True
                        Result.PhysicalLengthAfter = _Fs.Length
                        Return Result
                    End If

                    Dim RemovedFileMasterKeyAfterChunkSizeRewrite = False

                    If Types.HasFlag(ApplyOptionTypes.Encryption) Then
                        RemovedFileMasterKeyAfterChunkSizeRewrite = RemoveUnusedFileMasterKeyIfPossible()
                    End If

                    If HasOpenCheckpoint = False Then
                        PersistIndexAndHeader(_IndexOffset, Durable)
                    End If

                    Result.PhysicalLengthAfter = _Fs.Length
                    Return Result

                End If

                If Types.HasFlag(ApplyOptionTypes.Sparseness) AndAlso Options.StoreSparseChunks Then

                    MaterialiseSparseExtents(Result, ProgressCallback, CancellationToken)

                    If CancellationToken.Cancel Then
                        Result.WasCancelled = True
                        Result.PhysicalLengthAfter = _Fs.Length
                        Return Result
                    End If

                End If

                Dim RecordIds = _PhysicalRecords.Values.
                                 Where(Function(record) record.RefCount > 0).
                                 OrderBy(Function(record) record.RecordId).
                                 Select(Function(record) record.RecordId).
                                 ToList()

                Dim TotalRecords = Math.Max(1, RecordIds.Count)
                Dim ProcessedRecords = 0

                For Each RecordId In RecordIds

                    If CancellationToken.Cancel Then
                        Result.WasCancelled = True
                        Exit For
                    End If

                    If _PhysicalRecords.ContainsKey(RecordId) = False Then
                        ProcessedRecords += 1
                        Continue For
                    End If

                    Result.ExaminedChunks += 1

                    If ApplyRecordOptions(RecordId, Types, Result) Then
                        Result.RewrittenChunks += 1
                    End If

                    ProcessedRecords += 1

                    ReportProgress(ProgressCallback,
                                   ProcessedRecords,
                                   TotalRecords,
                                   ProcessUnitTypes.Arbitrary,
                                   CancellationToken)

                Next

                Dim RemovedFileMasterKey = False

                If Result.WasCancelled = False AndAlso Types.HasFlag(ApplyOptionTypes.Encryption) Then
                    RemovedFileMasterKey = RemoveUnusedFileMasterKeyIfPossible()
                End If

                If HasOpenCheckpoint = False AndAlso (Result.RewrittenChunks > 0 OrElse RemovedFileMasterKey) Then
                    PersistIndexAndHeader(_IndexOffset, Durable)
                End If

                Result.PhysicalLengthAfter = _Fs.Length

                Return Result

            End SyncLock

        End Function

        Private Function NeedsChunkSizeRewrite() As Boolean

            If Options.ChunkSize <= 0 Then
                Throw New InvalidOperationException("Chunk size must be greater than zero.")
            End If

            If _ChunkSize <> Options.ChunkSize Then
                Return True
            End If

            For Each Extent In _Extents

                If Extent.LogicalLength > Options.ChunkSize Then
                    Return True
                End If

            Next

            Return False

        End Function

        Private Function ApplyRecordOptions(RecordId As Long,
                                            Types As ApplyOptionTypes,
                                            Result As ApplyOptionsResult) As Boolean

            Dim Record = GetPhysicalRecord(RecordId)
            Dim Header = ReadApplyOptionsPhysicalRecordHeader(Record)
            Dim Plain = ReadPhysicalRecordPlain(Record)
            Dim PlainIsAllZero = Plain.Length = 0 OrElse IsAllZero(Plain, Plain.Length)

            If Types.HasFlag(ApplyOptionTypes.Sparseness) AndAlso
               Options.StoreSparseChunks = False AndAlso
               PlainIsAllZero Then

                ReplacePhysicalRecordWithSparseExtents(RecordId)

                Result.SparsenessChanges += 1
                Result.NewlySparseChunks += 1

                Return True

            End If

            Dim NeedsRewrite = False

            If Types.HasFlag(ApplyOptionTypes.Compression) Then

                If NeedsCompressionRewrite(Header) Then
                    NeedsRewrite = True
                    Result.CompressionChanges += 1
                End If

            End If

            Dim DesiredEncryptionMethod =
                If(_CurrentWriteEncryptionEnabled,
                   ChunkEncryptionMethods.AesCtrFileMasterKey,
                   ChunkEncryptionMethods.None)

            If Types.HasFlag(ApplyOptionTypes.Encryption) Then

                If Header.EncryptionMethod <> DesiredEncryptionMethod Then
                    NeedsRewrite = True
                    Result.EncryptionChanges += 1
                End If

            Else

                DesiredEncryptionMethod = Header.EncryptionMethod

            End If

            If NeedsRewrite = False Then
                Return False
            End If

            Dim CompressionMethodToUse As ChunkedStreamOptions.CompressionMethods
            Dim CompressionRatioThreshold As Double
            Dim ForceCompression As Boolean

            If Types.HasFlag(ApplyOptionTypes.Compression) Then

                CompressionMethodToUse = Options.CompressionMethod
                CompressionRatioThreshold = Options.CompressionRatioThreshold
                ForceCompression = False

            Else

                CompressionMethodToUse = Header.CompressionMethod
                CompressionRatioThreshold = 1.0R
                ForceCompression = Header.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None

            End If

            ReplacePhysicalRecordWithNewRecord(RecordId,
                                               Plain,
                                               CompressionMethodToUse,
                                               CompressionRatioThreshold,
                                               ForceCompression,
                                               DesiredEncryptionMethod)

            Return True

        End Function


        Private Function NeedsCompressionRewrite(Header As ChunkHeaderSnapshot) As Boolean

            Dim DesiredCompressionMethod = Options.CompressionMethod

            If DesiredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None Then
                Return Header.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None OrElse
                       Header.CompressionEvaluatedMethod <> ChunkedStreamOptions.CompressionMethods.None
            End If

            If Header.CompressionEvaluatedMethod <> DesiredCompressionMethod Then
                Return True
            End If

            Dim CompressionRatioThreshold = Options.CompressionRatioThreshold

            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            Dim ShouldBeCompressed = (Header.CompressionEvaluatedPercent / 100.0R) <= CompressionRatioThreshold

            If ShouldBeCompressed Then
                Return Header.CompressionMethod <> DesiredCompressionMethod
            End If

            Return Header.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None

        End Function

        Private Sub ApplyChunkSizeOptions(Result As ApplyOptionsResult,
                                          ProgressCallback As StreamProgressCallback,
                                          CancellationToken As CancellationToken)

            If Options.ChunkSize <= 0 Then
                Throw New InvalidOperationException("Chunk size must be greater than zero.")
            End If

            Dim OriginalExtents = New List(Of ExtentIndexEntry)(_Extents)
            Dim OriginalPhysicalRecords = _PhysicalRecords.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
            Dim OriginalNextPhysicalRecordId = _NextPhysicalRecordId
            Dim OriginalIndexOffset = _IndexOffset
            Dim OriginalPhysicalLength = _Fs.Length
            Dim OriginalChunkSize = _ChunkSize
            Dim OriginalChunkPlain = _ChunkPlain
            Dim OriginalCachedChunkPlain = _CachedChunkPlain

            Try

                Dim NewExtents As New List(Of ExtentIndexEntry)()
                Dim NewRecordIds As New HashSet(Of Long)()
                Dim LogicalOffset As Long = 0
                Dim TotalBytes = Math.Max(1L, _Length)
                Dim ProcessedBytes As Long = 0

                While LogicalOffset < _Length

                    If CancellationToken.Cancel Then
                        RestoreApplyOptionsRewriteState(OriginalExtents,
                                                        OriginalPhysicalRecords,
                                                        OriginalNextPhysicalRecordId,
                                                        OriginalIndexOffset,
                                                        OriginalPhysicalLength,
                                                        OriginalChunkSize,
                                                        OriginalChunkPlain,
                                                        OriginalCachedChunkPlain)
                        Return
                    End If

                    Dim SegmentLength = CInt(Math.Min(CLng(Options.ChunkSize), _Length - LogicalOffset))
                    Dim Buffer(SegmentLength - 1) As Byte

                    Read(LogicalOffset, Buffer, 0, SegmentLength)

                    Dim SegmentExtents = BuildExtentsFromBuffer(Buffer, 0, SegmentLength)

                    For Each extent In SegmentExtents

                        Dim NewExtent = extent
                        NewExtent.LogicalOffset = LogicalOffset

                        NewExtents.Add(NewExtent)

                        If NewExtent.PhysicalRecordId <> SparsePhysicalRecordId Then
                            NewRecordIds.Add(NewExtent.PhysicalRecordId)
                        End If

                        LogicalOffset += NewExtent.LogicalLength

                    Next

                    Result.ExaminedChunks += 1
                    Result.ChunkSizeChanges += 1
                    Result.RewrittenChunks += 1

                    ProcessedBytes += SegmentLength

                    ReportProgress(ProgressCallback,
                                   Math.Min(ProcessedBytes, TotalBytes),
                                   TotalBytes,
                                   ProcessUnitTypes.Bytes,
                                   CancellationToken)

                End While

                Dim OldPhysicalRecords = _PhysicalRecords.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
                Dim NewPhysicalRecords As New Dictionary(Of Long, PhysicalRecordEntry)()

                For Each recordId In NewRecordIds
                    NewPhysicalRecords(recordId) = GetPhysicalRecord(recordId)
                Next

                _Extents.Clear()
                _Extents.AddRange(NewExtents)

                _PhysicalRecords.Clear()

                For Each pair In NewPhysicalRecords
                    _PhysicalRecords(pair.Key) = pair.Value
                Next

                _ChunkSize = Options.ChunkSize
                _ChunkPlain = New Byte(_ChunkSize - 1) {}
                _CachedChunkPlain = New Byte(_ChunkSize - 1) {}

                ReleaseOldPhysicalRecordSpaces(OldPhysicalRecords.Values, NewRecordIds)

                InvalidateChunkCache()
                MarkAllMetadataPagesDirty()

            Catch

                RestoreApplyOptionsRewriteState(OriginalExtents,
                                                OriginalPhysicalRecords,
                                                OriginalNextPhysicalRecordId,
                                                OriginalIndexOffset,
                                                OriginalPhysicalLength,
                                                OriginalChunkSize,
                                                OriginalChunkPlain,
                                                OriginalCachedChunkPlain)
                Throw

            End Try

        End Sub

        Private Sub RestoreApplyOptionsRewriteState(OriginalExtents As List(Of ExtentIndexEntry),
                                                    OriginalPhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry),
                                                    OriginalNextPhysicalRecordId As Long,
                                                    OriginalIndexOffset As Long,
                                                    OriginalPhysicalLength As Long,
                                                    OriginalChunkSize As Integer,
                                                    OriginalChunkPlain As Byte(),
                                                    OriginalCachedChunkPlain As Byte())

            _Extents.Clear()
            _Extents.AddRange(OriginalExtents)

            _PhysicalRecords.Clear()

            For Each pair In OriginalPhysicalRecords
                _PhysicalRecords(pair.Key) = pair.Value
            Next

            _NextPhysicalRecordId = OriginalNextPhysicalRecordId
            _IndexOffset = OriginalIndexOffset
            _ChunkSize = OriginalChunkSize
            _ChunkPlain = OriginalChunkPlain
            _CachedChunkPlain = OriginalCachedChunkPlain

            ClearFreeSpaceMaps()
            DiscardPendingPhysicalRecordReclaims()
            InvalidateChunkCache()
            MarkAllMetadataPagesDirty()

            If _Fs.Length > OriginalPhysicalLength Then
                _Fs.SetLength(OriginalPhysicalLength)
            End If

        End Sub

        Private Sub MaterialiseSparseExtents(Result As ApplyOptionsResult,
                                             ProgressCallback As StreamProgressCallback,
                                             CancellationToken As CancellationToken)

            Dim SparseIndexes = _Extents.
                                Select(Function(extent, index) New With {.Extent = extent, .Index = index}).
                                Where(Function(item) item.Extent.PhysicalRecordId = SparsePhysicalRecordId).
                                Select(Function(item) item.Index).
                                ToList()

            Dim Total = Math.Max(1, SparseIndexes.Count)
            Dim Processed = 0

            For Each extentIndex In SparseIndexes

                If CancellationToken.Cancel Then
                    Return
                End If

                Dim Extent = _Extents(extentIndex)

                If Extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                    Processed += 1
                    Continue For
                End If

                If Extent.LogicalLength <= 0 Then
                    Processed += 1
                    Continue For
                End If

                Dim Buffer(Extent.LogicalLength - 1) As Byte
                Dim Record = WritePhysicalRecord(Buffer, Buffer.Length)

                Extent.PhysicalRecordId = Record.RecordId
                Extent.PhysicalRecordOffset = 0

                _Extents(extentIndex) = Extent

                Result.ExaminedChunks += 1
                Result.SparsenessChanges += 1
                Result.NewlyAllocatedChunks += 1
                Result.RewrittenChunks += 1

                Processed += 1

                ReportProgress(ProgressCallback,
                               Processed,
                               Total,
                               ProcessUnitTypes.Arbitrary,
                               CancellationToken)

            Next

            MarkAllMetadataPagesDirty()

        End Sub

        Private Sub ReplacePhysicalRecordWithSparseExtents(RecordId As Long)

            Dim Record = GetPhysicalRecord(RecordId)

            For Index = 0 To _Extents.Count - 1

                Dim Extent = _Extents(Index)

                If Extent.PhysicalRecordId <> RecordId Then Continue For

                Extent.PhysicalRecordId = SparsePhysicalRecordId
                Extent.PhysicalRecordOffset = 0

                _Extents(Index) = Extent

            Next

            Record.RefCount = 0
            _PhysicalRecords(RecordId) = Record

            If HasOpenCheckpoint Then
                _PendingReclaimedPhysicalRecords.Add(RecordId)
            Else
                ReclaimPhysicalRecord(RecordId)
            End If

            MarkAllMetadataPagesDirty()

        End Sub

        Private Sub ReplacePhysicalRecordWithNewRecord(OldRecordId As Long,
                                                       Plain As Byte(),
                                                       CompressionMethod As ChunkedStreamOptions.CompressionMethods,
                                                       CompressionRatioThreshold As Double,
                                                       ForceCompression As Boolean,
                                                       EncryptionMethod As ChunkEncryptionMethods)

            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))

            Dim OldRecord = GetPhysicalRecord(OldRecordId)

            Dim NewRecord = WritePhysicalRecordWithPolicy(Plain,
                                                          Plain.Length,
                                                          CompressionMethod,
                                                          CompressionRatioThreshold,
                                                          ForceCompression,
                                                          EncryptionMethod)

            NewRecord.RefCount = OldRecord.RefCount
            _PhysicalRecords(NewRecord.RecordId) = NewRecord

            For Index = 0 To _Extents.Count - 1

                Dim Extent = _Extents(Index)

                If Extent.PhysicalRecordId <> OldRecordId Then Continue For

                Extent.PhysicalRecordId = NewRecord.RecordId
                _Extents(Index) = Extent

            Next

            OldRecord.RefCount = 0
            _PhysicalRecords(OldRecordId) = OldRecord

            If HasOpenCheckpoint Then
                _PendingReclaimedPhysicalRecords.Add(OldRecordId)
            Else
                ReclaimPhysicalRecord(OldRecordId)
            End If

            MarkAllMetadataPagesDirty()

        End Sub

        Private Sub ReleaseOldPhysicalRecordSpaces(OldRecords As IEnumerable(Of PhysicalRecordEntry),
                                                   NewRecordIds As HashSet(Of Long))

            If OldRecords Is Nothing Then Return

            For Each record In OldRecords

                If record.RecordId <= SparsePhysicalRecordId Then Continue For
                If NewRecordIds IsNot Nothing AndAlso NewRecordIds.Contains(record.RecordId) Then Continue For

                If HasOpenCheckpoint = False Then
                    AddFreeChunkSpace(record.PhysicalOffset, record.PhysicalLength)
                End If

            Next

        End Sub

        Private Function ReadApplyOptionsPhysicalRecordHeader(Record As PhysicalRecordEntry) As ChunkHeaderSnapshot

            If Record.RecordId <= SparsePhysicalRecordId Then
                Throw New System.IO.InvalidDataException("Invalid physical record id.")
            End If

            If Record.PhysicalOffset < DataStartOffset Then
                Throw New System.IO.InvalidDataException($"Invalid physical record offset for record {Record.RecordId}.")
            End If

            If Record.PhysicalLength < MinChunkRecordSize Then
                Throw New System.IO.InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            End If

            If Record.PhysicalOffset + Record.PhysicalLength > _Fs.Length Then
                Throw New System.IO.InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")
            End If

            Dim Header(ChunkRecordHeaderSize - 1) As Byte

            _Fs.Position = Record.PhysicalOffset
            ReadExactly(_Fs, Header, 0, Header.Length)

            Dim StoredRecordId = BitConverter.ToInt64(Header, 0)

            If StoredRecordId <> Record.RecordId Then
                Throw New System.IO.InvalidDataException($"Physical record id mismatch. Expected {Record.RecordId}, found {StoredRecordId}.")
            End If

            Dim PlainLength = BitConverter.ToInt32(Header, ChunkPlainLengthOffset)

            If PlainLength <> Record.PlainLength Then
                Throw New System.IO.InvalidDataException($"Physical record plain length mismatch for record {Record.RecordId}.")
            End If

            Dim PayloadLength = BitConverter.ToInt32(Header, ChunkPayloadLengthOffset)

            If PayloadLength < 0 Then
                Throw New System.IO.InvalidDataException($"Invalid payload length for record {Record.RecordId}.")
            End If

            If ChunkRecordDataOffset + PayloadLength + MacSize <> Record.PhysicalLength Then
                Throw New System.IO.InvalidDataException($"Invalid physical record length for record {Record.RecordId}.")
            End If

            Dim Flags = CType(BitConverter.ToInt32(Header, ChunkFlagsOffset), ChunkFlags)

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New System.IO.InvalidDataException($"Unsupported physical record flags for record {Record.RecordId}: {CInt(Flags)}.")
            End If

            Dim CompressionEvaluatedPercent = CInt(Header(ChunkCompressionEvaluatedPercentOffset))

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
               CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then

                Throw New System.IO.InvalidDataException($"Invalid compression evaluated percent for record {Record.RecordId}: {CompressionEvaluatedPercent}.")

            End If

            Return New ChunkHeaderSnapshot With {
                .CompressionMethod = CType(BitConverter.ToInt32(Header, ChunkCompressionMethodOffset), ChunkedStreamOptions.CompressionMethods),
                .CompressionEvaluatedMethod = CType(BitConverter.ToInt32(Header, ChunkCompressionEvaluatedMethodOffset), ChunkedStreamOptions.CompressionMethods),
                .CompressionEvaluatedPercent = CompressionEvaluatedPercent,
                .EncryptionMethod = CType(BitConverter.ToInt32(Header, ChunkEncryptionMethodOffset), ChunkEncryptionMethods),
                .PlainLength = PlainLength,
                .PayloadLength = PayloadLength,
                .ChunkFlags = Flags
            }

        End Function

    End Class
End Namespace