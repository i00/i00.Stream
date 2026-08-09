Namespace Streams

    Partial Class ChunkedStream

        ''' <summary>
        ''' Options controlling newly written chunks.
        ''' Existing chunk records retain their original compression, encryption and sparse representation until rewritten.
        ''' </summary>
        Public Class ChunkedStreamOptions

            Private _ChunkSize As Integer = ChunkedStream.DefaultChunkSize
            ''' <summary>
            ''' Logical chunk size used when creating new streams and during rebuild defragmentation.
            ''' </summary>
            ''' <remarks>
            ''' Opening an existing stream updates this property to the chunk size stored in the stream.
            ''' Changing this property does not immediately affect existing chunks.
            ''' To apply a new chunk size to an existing stream, set this property and then call Defragment(DefragTypes.Rebuild).
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
                    System.Diagnostics.Debug.Print($"ChunkedStream chunk size changed to {Value:N0} bytes. Existing chunks will not be affected until Defragment(Rebuild) is performed.")
#End If

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

                ''' <summary>
                ''' Store the chunk payload using LZ4 block compression.
                ''' </summary>
                Lz4 = 1

                ''' <summary>
                ''' Store the chunk payload using Deflate compression.
                ''' </summary>
                Deflate = 2

                ''' <summary>
                ''' Store the chunk payload using GZip compression.
                ''' </summary>
                GZip = 3

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
            ''' Apply the current compression options to chunks that do not currently satisfy them.
            ''' </summary>
            Compression = 1 << 0

            ''' <summary>
            ''' Apply the current encryption state to chunks that do not currently satisfy it.
            ''' </summary>
            Encryption = 1 << 1

            ''' <summary>
            ''' Apply the current sparse-storage setting to chunks that do not currently satisfy it.
            ''' </summary>
            Sparseness = 1 << 2

            All = Compression Or Encryption Or Sparseness

        End Enum

        ''' <summary>
        ''' Summary of an ApplyOptions operation.
        ''' </summary>
        Public NotInheritable Class ApplyOptionsResult

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
                       $"physicalDelta={PhysicalBytesChanged.FormatFileSizeFromBytes()}, cancelled={WasCancelled}]"

            End Function

        End Class

        ''' <summary>
        ''' Applies the current data options to existing chunks that do not currently satisfy the selected option categories.
        ''' </summary>
        ''' <param name="Types">
        ''' Option categories to apply.
        ''' </param>
        ''' <param name="ProgressCallback">
        ''' Optional progress callback.
        ''' </param>
        ''' <returns>
        ''' A summary of the operation.
        ''' </returns>
        ''' <remarks>
        ''' ApplyOptions rewrites only chunks that need to change.
        '''
        ''' Compression changes are applied only where the current payload does not satisfy the requested compression options.
        ''' Encryption changes are determined from chunk metadata.
        ''' Sparse changes use the chunk PlaintextAllZero flag and do not need to read plaintext data.
        '''
        ''' ApplyOptions may increase fragmentation because changed chunks are appended and old physical records become holes.
        '''
        ''' ApplyOptions is internally protected by a checkpoint. If it is called inside an existing checkpoint, the internal
        ''' checkpoint is nested and committed into the parent checkpoint.
        ''' </remarks>
        Public Function ApplyOptions(Optional Types As ApplyOptionTypes = ApplyOptionTypes.All,
                                     Optional ProgressCallback As StreamProgressCallback = Nothing,
                                     Optional Durable As Boolean = True) As ApplyOptionsResult

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Dim Result As New ApplyOptionsResult With {
                    .PhysicalLengthBefore = _Fs.Length,
                    .PhysicalLengthAfter = _Fs.Length
                }

                If Types = ApplyOptionTypes.None Then
                    Return Result
                End If

                Dim AppliesCompression = (Types And ApplyOptionTypes.Compression) = ApplyOptionTypes.Compression
                Dim AppliesEncryption = (Types And ApplyOptionTypes.Encryption) = ApplyOptionTypes.Encryption
                Dim AppliesSparseness = (Types And ApplyOptionTypes.Sparseness) = ApplyOptionTypes.Sparseness

                If Not AppliesCompression AndAlso Not AppliesEncryption AndAlso Not AppliesSparseness Then
                    Return Result
                End If

                Dim Struct = GetStructure()
                Dim CancellationToken As New CancellationToken()

                For Each chunk In Struct.Chunks

                    Result.ExaminedChunks += 1

                    Dim PlainLoaded = False
                    Dim NeedsCompressionChange = False
                    Dim NeedsEncryptionChange = False
                    Dim NeedsSparsenessChange = False
                    Dim NewlySparse = False
                    Dim NewlyAllocated = False

                    Dim StoreSparsePolicy = GetApplyOptionsStoreSparsePolicy(chunk, AppliesSparseness)

                    Dim WillBeSparse =
                        chunk.PlainLength = 0 OrElse
                        (Not StoreSparsePolicy AndAlso chunk.IsPlaintextAllZero)

                    Dim CompressionPolicy = GetApplyOptionsCompressionPolicy(chunk, AppliesCompression)

                    Dim ForceCompression =
                        Not AppliesCompression AndAlso
                        chunk.IsAllocated AndAlso
                        chunk.IsCompressed

                    Dim EncryptionPolicy = GetApplyOptionsEncryptionPolicy(chunk, AppliesEncryption)

                    If AppliesSparseness Then

                        If chunk.IsPlaintextAllZero AndAlso chunk.PlainLength > 0 Then

                            If Options.StoreSparseChunks AndAlso chunk.IsSparse Then
                                NeedsSparsenessChange = True
                                NewlyAllocated = True
                            ElseIf Not Options.StoreSparseChunks AndAlso chunk.IsAllocated Then
                                NeedsSparsenessChange = True
                                NewlySparse = True
                            End If

                        End If

                    End If

                    If AppliesEncryption AndAlso chunk.IsAllocated AndAlso Not WillBeSparse Then

                        Dim DesiredEncryption =
                            If(_CurrentWriteEncryptionEnabled,
                               ChunkEncryptionMethods.AesCtrFileMasterKey,
                               ChunkEncryptionMethods.None)

                        If chunk.EncryptionMethod <> DesiredEncryption Then
                            NeedsEncryptionChange = True
                        End If

                    End If

                    If AppliesCompression AndAlso chunk.IsAllocated AndAlso Not WillBeSparse Then
                        NeedsCompressionChange = NeedsCompressionRewrite(chunk, CompressionPolicy, PlainLoaded)
                    End If

                    Dim NeedsRewrite =
                        NeedsCompressionChange OrElse
                        NeedsEncryptionChange OrElse
                        NeedsSparsenessChange

                    If Not NeedsRewrite Then

                        ReportProgress(ProgressCallback,
                                       Result.ExaminedChunks,
                                       Struct.ChunkCount,
                                       ProcessUnitTypes.Chunks,
                                       CancellationToken)

                        If CancellationToken.Cancel Then
                            Result.WasCancelled = True
                            Exit For
                        End If

                        Continue For

                    End If

                    EnsurePlainLoadedForApplyOptions(chunk, PlainLoaded)

                    WriteChunkRecordWithPolicy(chunk.Index,
                                               _ChunkPlain,
                                               chunk.PlainLength,
                                               StoreSparsePolicy,
                                               CompressionPolicy,
                                               Options.CompressionRatioThreshold,
                                               ForceCompression,
                                               EncryptionPolicy)

                    Result.RewrittenChunks += 1

                    If NeedsCompressionChange Then Result.CompressionChanges += 1
                    If NeedsEncryptionChange Then Result.EncryptionChanges += 1

                    If NeedsSparsenessChange Then
                        Result.SparsenessChanges += 1

                        If NewlySparse Then Result.NewlySparseChunks += 1
                        If NewlyAllocated Then Result.NewlyAllocatedChunks += 1
                    End If

                    ReportProgress(ProgressCallback,
                                   Result.ExaminedChunks,
                                   Struct.ChunkCount,
                                   ProcessUnitTypes.Chunks,
                                   CancellationToken)

                    If CancellationToken.Cancel Then
                        Result.WasCancelled = True
                        Exit For
                    End If

                Next

                Dim RemovedFileMasterKey = False

                If Result.WasCancelled = False AndAlso AppliesEncryption Then
                    RemovedFileMasterKey = RemoveUnusedFileMasterKeyIfPossible()
                End If

                If HasOpenCheckpoint = False AndAlso (Result.RewrittenChunks > 0 OrElse RemovedFileMasterKey) Then
                    PersistIndexAndHeader(_IndexOffset, Durable)
                End If

                Result.PhysicalLengthAfter = _Fs.Length

                Return Result

            End SyncLock

        End Function

        Private Function RemoveUnusedFileMasterKeyIfPossible() As Boolean

            If _CurrentWriteEncryptionEnabled Then Return False
            If _FileMasterKey Is Nothing Then Return False

            ' If a checkpoint is active, a rollback may restore encrypted chunks.
            ' Defer key removal until the outermost checkpoint is closed.
            If HasOpenCheckpoint Then Return False

            Dim Struct = GetStructure()

            If Struct.EncryptedChunkCount <> 0 Then Return False

            _FileMasterKey = Nothing
            _ChunkEncryptionKey = Nothing
            _ChunkMacKey = Nothing

            Array.Clear(_Header, MasterKeyWrapAreaOffset, MasterKeyWrapAreaLength)

            Return True

        End Function

        Private Function GetApplyOptionsStoreSparsePolicy(chunk As ChunkedStreamStructure.Chunk,
                                                          AppliesSparseness As Boolean) As Boolean

            If AppliesSparseness Then
                Return Options.StoreSparseChunks
            End If

            ' Preserve existing sparse/allocated shape when sparseness is not being applied.
            Return chunk.IsAllocated

        End Function

        Private Function GetApplyOptionsCompressionPolicy(chunk As ChunkedStreamStructure.Chunk,
                                                          AppliesCompression As Boolean) As ChunkedStreamOptions.CompressionMethods

            If AppliesCompression Then
                Return Options.CompressionMethod
            End If

            If chunk.IsAllocated Then
                Return chunk.CompressionMethod
            End If

            ' Materialising a sparse chunk has no previous physical compression policy.
            ' Use the current option if the chunk must become allocated.
            Return Options.CompressionMethod

        End Function

        Private Function GetApplyOptionsEncryptionPolicy(chunk As ChunkedStreamStructure.Chunk,
                                                         AppliesEncryption As Boolean) As ChunkEncryptionMethods

            If AppliesEncryption Then

                Return If(_CurrentWriteEncryptionEnabled,
                          ChunkEncryptionMethods.AesCtrFileMasterKey,
                          ChunkEncryptionMethods.None)

            End If

            If chunk.IsAllocated Then
                Return chunk.EncryptionMethod
            End If

            ' Materialising a sparse chunk has no previous physical encryption policy.
            ' Use the current option if the chunk must become allocated.
            Return If(_CurrentWriteEncryptionEnabled,
                      ChunkEncryptionMethods.AesCtrFileMasterKey,
                      ChunkEncryptionMethods.None)

        End Function

        Private Function NeedsCompressionRewrite(chunk As ChunkedStreamStructure.Chunk,
                                                 DesiredCompressionMethod As ChunkedStreamOptions.CompressionMethods,
                                                 ByRef PlainLoaded As Boolean) As Boolean

            If DesiredCompressionMethod = ChunkedStreamOptions.CompressionMethods.None Then
                Return chunk.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None
            End If

            If chunk.CompressionEvaluatedMethod <> DesiredCompressionMethod Then
                Return True
            End If

            Dim CompressionRatioThreshold = Options.CompressionRatioThreshold

            If CompressionRatioThreshold < MinimumCompressionRatioThreshold Then CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If CompressionRatioThreshold > MaximumCompressionRatioThreshold Then CompressionRatioThreshold = MaximumCompressionRatioThreshold

            Dim ShouldBeCompressed =
                chunk.CompressionEvaluatedRatio <= CompressionRatioThreshold

            If ShouldBeCompressed Then
                Return chunk.CompressionMethod <> DesiredCompressionMethod
            End If

            Return chunk.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None

        End Function

        Private Sub EnsurePlainLoadedForApplyOptions(chunk As ChunkedStreamStructure.Chunk,
                                                     ByRef PlainLoaded As Boolean)

            If PlainLoaded Then Return

            Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)

            If chunk.IsSparse OrElse chunk.IsPlaintextAllZero Then
                PlainLoaded = True
                Return
            End If

            LoadChunk(chunk.Index, _ChunkPlain)

            PlainLoaded = True

        End Sub

    End Class

End Namespace