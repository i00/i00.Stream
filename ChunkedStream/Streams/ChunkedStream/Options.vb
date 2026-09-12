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
'   - ApplyOptions(ApplyOptionTypes.ChunkSize) is a best-effort pass: it rewrites
'     physical records that clearly exceed the current bound and leaves undersized
'     ones alone. Defragment(DefragTypes.Rebuild) is what fully re-derives and
'     consolidates the logical stream under the current ChunkSize/ChunkSizeVariance.
'
' Content-Defined Chunking
'   - ChunkSizeVariance lets newly written chunk boundaries be chosen by a Gear-hash
'     scan of the data instead of always landing at a fixed ChunkSize, so identical
'     byte runs tend to chunk identically wherever they occur. A value of 0 restores
'     exactly today's fixed-size behaviour; the default is non-zero.
'   - Deduplication is a separate, independent setting reserved for future use -
'     ChunkSizeVariance only decides where chunk boundaries fall, not whether
'     matching chunks get reused.
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

            ''' <summary>
            ''' Controls how unreferenced physical records are identified for reclamation.
            ''' </summary>
            Public Enum ExtentReclaimTypes

                ''' <summary>
                ''' Reclaim a physical record as soon as the reference count maintained while
                ''' extents are added and removed reaches zero.
                ''' </summary>
                RefCount = 0

                ''' <summary>
                ''' Determine whether a physical record is unreferenced by scanning the extent
                ''' table after each edit, rather than trusting the maintained reference count.
                ''' This is more resilient to reference-count drift but adds a scan of the
                ''' extent table and physical-record set to every edit that removes extents.
                ''' </summary>
                Scan = 1

            End Enum

            ''' <summary>
            ''' Controls how unreferenced physical records are identified for reclamation.
            ''' </summary>
            ''' <remarks>
            ''' Physical-record reference counts are maintained regardless of this setting, so
            ''' it may be changed at any time and takes effect from the next edit.
            ''' </remarks>
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
            ''' This is also the per-record size the read cache is measured against: the cache
            ''' can hold up to <c>ChunkSize * </c><see cref="ChunkReadBlockCache" /> bytes per
            ''' open stream, so raising the chunk size raises that ceiling in proportion - see
            ''' <see cref="ChunkReadBlockCache" />.
            ''' </remarks>
            Public Property ChunkSize As Integer
                Get
                    Return _ChunkSize
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkSize))
                    If Value = _ChunkSize Then Return
                    _ChunkSize = Value
                    RecalculateChunkSizeBounds()
#If DEBUG Then
                    Debug.Print($"ChunkedStream chunk size changed to {Value:N0} bytes. Existing extents will not be affected until {NameOf(ApplyOptions)}({NameOf(ApplyOptionTypes.ChunkSize)}) or {NameOf(Defragment)}({NameOf(DefragTypes.Rebuild)}) is performed.")
#End If
                End Set
            End Property

            Private _ChunkSizeVariance As Double = 0

            ''' <summary>
            ''' How far a content-defined chunk boundary may vary from <see cref="ChunkSize"/>, expressed
            ''' as a fraction of it. A chunk's logical length ranges from
            ''' <c>ChunkSize * (1 - ChunkSizeVariance)</c> to <c>ChunkSize * (1 + ChunkSizeVariance)</c>;
            ''' the exact boundary within that range is chosen by a content-defined (Gear hash) scan of the
            ''' data being written, so identical byte runs tend to produce identical chunk boundaries
            ''' regardless of what precedes them in the same write.
            ''' </summary>
            ''' <remarks>
            ''' A value of <c>0</c> disables content-defined splitting entirely: chunks are produced at a
            ''' fixed <see cref="ChunkSize"/>, exactly as before this property existed. Any value greater
            ''' than <c>0</c> enables content-defined splitting for newly written chunks regardless of
            ''' <see cref="Deduplication"/> - deduplication decides whether matching chunks get reused, this
            ''' property only decides how chunk boundaries are chosen, and the two are independent.
            '''
            ''' Performance: whenever this is non-zero, every byte written is run through a rolling hash to
            ''' find chunk boundaries, on top of the existing compression/encryption cost. It's a cheap
            ''' per-byte operation (shift, table lookup, add), but it runs serially ahead of the parallel
            ''' per-chunk work controlled by <see cref="MaxCryptoParallelism"/>, since a chunk's boundary -
            ''' and therefore what there is to encrypt/compress in parallel - isn't known until the scan
            ''' reaches it. Leave this at <c>0</c> to skip the scan and keep today's zero-overhead
            ''' fixed-size splitting.
            '''
            ''' Changing this property does not immediately affect existing extents, the same as
            ''' <see cref="ChunkSize"/>: the stream records the <see cref="ChunkSizeVariance"/> it was last
            ''' rewritten with, and <see cref="ChunkedStream.ApplyOptions"/>(<see cref="ApplyOptionTypes.ChunkSize"/>)
            ''' (or <see cref="ChunkedStream.Defragment"/>(<see cref="DefragTypes.Rebuild"/>)) is what applies
            ''' a changed value to already-written data.
            ''' </remarks>
            Public Property ChunkSizeVariance As Double
                Get
                    Return _ChunkSizeVariance
                End Get
                Set
                    If Value < 0 OrElse Value >= 1 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkSizeVariance))
                    If Value = _ChunkSizeVariance Then Return
                    _ChunkSizeVariance = Value
                    RecalculateChunkSizeBounds()
                End Set
            End Property

            ''' <summary>
            ''' Whether matching chunks get reused instead of written again - see the
            ''' deduplication design notes.
            ''' </summary>
            ''' <remarks>
            ''' Independent of <see cref="ChunkSizeVariance"/>, which only decides where chunk
            ''' boundaries fall - content-defined splitting happens (or doesn't) based on
            ''' <see cref="ChunkSizeVariance"/> alone, regardless of this setting.
            ''' </remarks>
            Public Property Deduplication As Boolean = False

            ''' <summary>
            ''' When True, a genuine end-of-stream append that would otherwise pay to
            ''' decrypt/re-encrypt the stream's last chunk on every single Write() call instead
            ''' accumulates in memory and only commits when a chunk boundary is reached or
            ''' something needs the committed state to be complete (an explicit
            ''' <see cref="ChunkedStream.FlushCurrentChunkWriteCache"/>, <c>Flush</c>,
            ''' <c>Dispose</c>, a non-contiguous write, or a read into the buffered range).
            ''' </summary>
            ''' <remarks>
            ''' Coalescing the resulting chunk boundaries with the last existing chunk happens
            ''' unconditionally regardless of this setting - see the write-coalescing plan notes.
            ''' This option only controls whether that coalescing commits eagerly on every write
            ''' (False, the default) or is deferred to reduce the number of decrypt/re-encrypt
            ''' round trips a run of small appends pays for (True). While bytes are buffered they
            ''' exist only in process memory, not in any physical record or extent - a crash or a
            ''' killed process before a commit trigger fires loses them even though the Write()
            ''' call that produced them already returned successfully. Off by default for exactly
            ''' this reason.
            ''' </remarks>
            Public Property CurrentChunkWriteCaching As Boolean = False

            Private _MinChunkSize As Integer
            Private _MaxChunkSize As Integer
            Private _SplitHashThreshold As ULong

            ''' <summary>
            ''' Smallest logical length a content-defined chunk boundary may produce - derived from
            ''' <see cref="ChunkSize"/> and <see cref="ChunkSizeVariance"/>, recalculated whenever either
            ''' changes. Not meaningful when <see cref="ChunkSizeVariance"/> is <c>0</c>.
            ''' </summary>
            Friend ReadOnly Property MinChunkSize As Integer
                Get
                    Return _MinChunkSize
                End Get
            End Property

            ''' <summary>
            ''' Largest logical length a content-defined chunk boundary may produce - derived from
            ''' <see cref="ChunkSize"/> and <see cref="ChunkSizeVariance"/>, recalculated whenever either
            ''' changes. Also the hard cap chunk splitting always enforces, even when
            ''' <see cref="ChunkSizeVariance"/> is <c>0</c> (in which case it equals <see cref="ChunkSize"/>).
            ''' </summary>
            Friend ReadOnly Property MaxChunkSize As Integer
                Get
                    Return _MaxChunkSize
                End Get
            End Property

            ''' <summary>
            ''' Rolling-hash value below which a content-defined scan splits a chunk - derived from
            ''' <see cref="ChunkSize"/>, recalculated whenever <see cref="ChunkSize"/> or
            ''' <see cref="ChunkSizeVariance"/> changes. Zero (never matches) when
            ''' <see cref="ChunkSizeVariance"/> is <c>0</c>.
            ''' </summary>
            Friend ReadOnly Property SplitHashThreshold As ULong
                Get
                    Return _SplitHashThreshold
                End Get
            End Property

            Public Sub New()
                RecalculateChunkSizeBounds()
            End Sub

            Private Sub RecalculateChunkSizeBounds()
                _MinChunkSize = CInt(_ChunkSize * (1.0R - _ChunkSizeVariance))
                _MaxChunkSize = CInt(_ChunkSize * (1.0R + _ChunkSizeVariance))

                ' The hash only starts accumulating once MinChunkSize bytes are already behind
                ' it (see DetermineNextSegmentLength's own remarks), so those bytes are never
                ' subject to a threshold check at all - only the bytes from MinChunkSize onward
                ' are. Targeting ChunkSize itself here biases the average total chunk length high
                ' by roughly MinChunkSize. AverageBytesToScan can round down to 0 at a tiny
                ' non-zero ChunkSizeVariance (CInt's rounding can make MinChunkSize = ChunkSize);
                ' treat that the same as ChunkSizeVariance = 0 - no room for content to pick a
                ' boundary, so don't hash at all.
                Dim AverageBytesToScan = _ChunkSize - _MinChunkSize

                _SplitHashThreshold =
                    If(_ChunkSizeVariance > 0 AndAlso AverageBytesToScan > 0,
                       CalculateSplitHashCheck(AverageBytesToScan),
                       0UL)
            End Sub

            ''' <summary>
            ''' Rolling-hash threshold that gives a memoryless Gear-hash scan an average of
            ''' <paramref name="AverageBytesToScan"/> hashed bytes before it splits: a split is
            ''' taken whenever the rolling hash falls below this value, which happens with
            ''' probability approximately <c>1 / AverageBytesToScan</c> at each hashed byte.
            ''' </summary>
            Private Shared Function CalculateSplitHashCheck(AverageBytesToScan As Integer) As ULong
                If AverageBytesToScan <= 0 Then
                    Throw New ArgumentOutOfRangeException(NameOf(AverageBytesToScan))
                End If
                Const Possibilities As ULong = ULong.MaxValue
                Return CULng(Possibilities \ CULng(AverageBytesToScan))
            End Function

            Private _SubBlockSize As Integer = ChunkedStream.DefaultSubBlockSize

            ''' <summary>
            ''' Largest number of plaintext bytes independently compressed, encrypted and
            ''' authenticated within one chunk record.
            ''' </summary>
            ''' <remarks>
            ''' Decouples the MAC/compression/decrypt granularity from <see cref="ChunkSize" />
            ''' (which sets the physical-record / metadata-table granularity instead). Reading
            ''' or verifying any part of a chunk costs roughly this many bytes of decrypt/MAC
            ''' work, not the whole chunk - so a large <see cref="ChunkSize" /> (chosen to keep
            ''' the physical-record table small for a very large archive) need not make random
            ''' access expensive: keep this at a few hundred KB and only <see cref="ChunkSize" />
            ''' needs to grow. Applies to new chunk records only; each record stores how many
            ''' sub-blocks it was split into, so existing records are unaffected by a later
            ''' change to this property.
            ''' </remarks>
            Public Property SubBlockSize As Integer
                Get
                    Return _SubBlockSize
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(SubBlockSize))
                    _SubBlockSize = Value
                End Set
            End Property

            Private _IndexPageEntryCount As Integer = 256
            Private _IndexDirectoryEntryCount As Integer = 256
            Private _DedupIndexPageEntryCount As Integer = 256

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
                BestFit = 1

                ''' <summary>
                ''' Reuses known free spaces and, when no suitable known space exists,
                ''' may rebuild the free-space map by scanning the active stream layout.
                ''' This can reclaim holes that were not already known, but may add
                ''' extra write-time overhead when the scan is required.
                ''' </summary>
                BestFitScan = 2

                ''' <summary>
                ''' Works like BestFit, but always uses the first free space that is large
                ''' enough (the one with the lowest offset) rather than the smallest fitting one.
                ''' </summary>
                FirstFit = 3

                ''' <summary>
                ''' Works like BestFitScan, but always uses the first free space that is large
                ''' enough (the one with the lowest offset) rather than the smallest fitting one.
                ''' </summary>
                FirstFitScan = 4
            End Enum

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written physical chunk records.
            ''' </summary>
            ''' <remarks>
            ''' This setting affects newly written chunk records only. Existing layout is not
            ''' reorganised by changing this value. Use Defragment to actively compact or reorder
            ''' existing records.
            '''
            ''' While a checkpoint is active a chunk record may still be written into a known free
            ''' hole: a rollback or crash reloads the pre-checkpoint durable state, which does not
            ''' reference that hole, so the write becomes harmless unreferenced bytes there. The
            ''' scan policies do not rebuild the free-space map while a checkpoint is open (that
            ''' would count a record the checkpoint deleted as free), and new metadata pages are
            ''' kept in the scratch region above the checkpoint mark. Set
            ''' <see cref="NewWriteLocationPolicies.Append"/> for pure append behaviour.
            ''' </remarks>
            Public Property NewChunkWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.BestFit

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written index pages.
            ''' </summary>
            Public Property NewIndexPageWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.BestFit

            ''' <summary>
            ''' Gets or sets the placement policy used for newly written index-directory pages.
            ''' </summary>
            Public Property NewIndexDirectoryPageWriteLocationPolicy As NewWriteLocationPolicies = NewWriteLocationPolicies.BestFit

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
            ''' Number of entries stored in each authenticated deduplication-index page, and the
            ''' bucket capacity of the in-memory extendible hash table backing it. Fixed for a given
            ''' index the first time deduplication is actually used (the first hash computed or
            ''' entry inserted) - changing this afterwards has no effect until the index is rebuilt
            ''' (see <see cref="Deduplication"/> and its planned rebuild API).
            ''' </summary>
            Public Property DedupIndexPageEntryCount As Integer
                Get
                    Return _DedupIndexPageEntryCount
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(DedupIndexPageEntryCount))
                    _DedupIndexPageEntryCount = Value
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
            ''' How the compression benefit of a newly written chunk is evaluated.
            ''' </summary>
            Public Enum CompressionEvaluationStates As Integer

                ''' <summary>
                ''' Compress the whole chunk plaintext and measure the exact result, even when
                ''' the compressed payload is then discarded because it does not beat
                ''' <see cref="CompressionRatioThreshold" />. Every chunk stores an exact
                ''' evaluation, which <see cref="ChunkedStream.ApplyOptions" /> can rely on
                ''' without decompressing anything.
                ''' </summary>
                Always = 0

                ''' <summary>
                ''' Compress only a leading sample of the chunk first. If the sample clearly
                ''' fails <see cref="CompressionRatioThreshold" /> the full compression is
                ''' skipped, the chunk is stored as plaintext, and its evaluation is recorded
                ''' as an estimate (<see cref="ChunkedStream.ChunkFlags.CompressionEstimated" />).
                ''' Otherwise the full chunk is compressed and evaluated exactly as with
                ''' <see cref="Always" />.
                '''
                ''' This avoids paying full compression cost for data that will not compress,
                ''' at the cost of a small extra sample compression for data that will, and a
                ''' slightly approximate stored evaluation for the skipped chunks.
                ''' <see cref="ChunkedStream.ApplyOptions" /> re-evaluates every estimated
                ''' chunk in full, so running it (with any settings) restores exact
                ''' evaluations throughout; running it while this is set to <see cref="Always" />
                ''' guarantees no estimated chunk remains.
                '''
                ''' The benefit depends on the codec: LZ4 and Snappy are fast enough that a
                ''' discarded full compression costs little, so the advantage of Sampled is
                ''' largest for the slower Deflate and GZip methods.
                ''' </summary>
                Sampled = 1

            End Enum

            ''' <summary>
            ''' How the compression benefit of a newly written chunk is evaluated. Has no
            ''' effect when <see cref="CompressionMethod" /> is
            ''' <see cref="CompressionMethods.None" />.
            ''' </summary>
            Public Property CompressionEvaluation As CompressionEvaluationStates = CompressionEvaluationStates.Always

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

            Private _ChunkReadBlockCache As Integer = ChunkedStream.DefaultChunkReadBlockCache

            ''' <summary>
            ''' How many recently read decrypted chunk records are kept in memory so a repeat
            ''' read of the same region skips the backing-store read, MAC check, decryption and
            ''' decompression. <c>0</c> disables the cache.
            ''' </summary>
            ''' <remarks>
            ''' The cache holds up to this many physical records, each up to
            ''' <see cref="ChunkSize" /> bytes of plaintext, so it can grow to roughly
            ''' <c>ChunkReadBlockCache * ChunkSize</c> bytes per open stream - about 4 MB at the
            ''' defaults. Raising <see cref="ChunkSize" /> scales this ceiling with it; revisit
            ''' this value whenever you change <see cref="ChunkSize" />, especially if many
            ''' streams are open at once. Both the serial and the large multi-chunk read paths
            ''' fill and are served from it. Entries are keyed by physical record: a write only
            ''' drops the entries for the records it supersedes, so an unrelated read-heavy
            ''' working set stays warm across writes; defragment, ApplyOptions, checkpoint
            ''' rollback and fault recovery drop the whole cache.
            ''' </remarks>
            Public Property ChunkReadBlockCache As Integer
                Get
                    Return _ChunkReadBlockCache
                End Get
                Set
                    If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkReadBlockCache))
                    _ChunkReadBlockCache = Value
                End Set
            End Property

            ''' <summary>
            ''' Maximum worker threads a single large synchronous read may use to authenticate,
            ''' decrypt and decompress chunks in parallel. Defaults to the processor count; set
            ''' to 1 to keep chunk cryptography fully serial. Only reads spanning several chunks
            ''' parallelise, and only the per-chunk CPU work is threaded - the backing-store
            ''' reads and the copy into the caller's buffer stay serial.
            ''' </summary>
            Public Property MaxCryptoParallelism As Integer = Environment.ProcessorCount

            Private _MinChunksForParallelCrypto As Integer = 8

            ''' <summary>
            ''' A read or write must cover at least this many chunks before its per-chunk crypto
            ''' (and, for a batched write, its backing-store placement) runs on a worker pool -
            ''' see <see cref="MaxCryptoParallelism"/> and <see cref="MaxPhysicalWriteParallelism"/>.
            ''' A caller batching reads or writes in units smaller than
            ''' <c>ChunkSize * MinChunksForParallelCrypto</c> never reaches the parallel path, no
            ''' matter how large those parallelism limits are. Defaults to 8; raise it if the
            ''' per-call overhead of spinning up a worker pool for a modest batch outweighs the
            ''' benefit, or lower it (down to 1) to parallelise more eagerly.
            ''' </summary>
            Public Property MinChunksForParallelCrypto As Integer
                Get
                    Return _MinChunksForParallelCrypto
                End Get
                Set
                    If Value <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(MinChunksForParallelCrypto))
                    _MinChunksForParallelCrypto = Value
                End Set
            End Property

            ''' <summary>
            ''' Maximum worker threads used to encrypt/decrypt/authenticate a single chunk's
            ''' own sub-blocks in parallel - on top of, not instead of,
            ''' <see cref="MaxCryptoParallelism"/>'s parallelism across different chunks. Only
            ''' matters when a chunk actually contains more than one sub-block
            ''' (<see cref="SubBlockSize"/> less than <see cref="ChunkSize"/>) - most useful
            ''' when a single very large chunk is being read or written and there are too few
            ''' chunks for MaxCryptoParallelism to have anything to spread across. Defaults to
            ''' 1 (serial): unlike MaxCryptoParallelism's per-chunk units of work, a sub-block
            ''' is much smaller (often ~128 KB), so parallel-dispatch overhead is
            ''' proportionally larger, and combining this with MaxCryptoParallelism can
            ''' oversubscribe the thread pool (each of MaxCryptoParallelism's chunk workers
            ''' may itself spin up this many more threads). Benchmark for your own
            ''' ChunkSize/SubBlockSize combination before raising it.
            ''' </summary>
            Public Property MaxSubBlockCryptoParallelism As Integer = 1

            ''' <summary>
            ''' Maximum concurrent physical writes an async multi-chunk write may have in
            ''' flight against the backing store at once. Only takes effect when the backing
            ''' store both implements <see cref="IPositionedStreamAsync"/> and declares
            ''' <see cref="PositionedIoCapabilities.LockFreeWrites"/> (a plain FileStream does
            ''' neither, so this is a no-op there); otherwise every write already had to go
            ''' through ChunkedStream's own physical-I/O lock one at a time regardless of this
            ''' setting. Set to 1 to keep chunk writes fully serial even when the backing store
            ''' could support more. Unlike <see cref="MaxCryptoParallelism"/> (CPU-bound, so
            ''' processor count is a sensible default), the right value here depends entirely
            ''' on the backing store's own queue depth - an SSD/NVMe device or a pooled-handle
            ''' wrapper typically wants several in flight, a single spinning disk wants close to
            ''' one. Defaults conservatively; raise it only for a backing store you know
            ''' benefits - a same-hardware A/B (DOP 4 vs 8, 512 MB-1 GB through
            ''' <see cref="PooledPositionedFileStream"/>) showed no measurable gain from
            ''' raising this default, so it stays at 4 rather than matching
            ''' <see cref="PooledPositionedFileStream.DefaultPoolSize"/> on an unproven guess.
            ''' </summary>
            Public Property MaxPhysicalWriteParallelism As Integer = 4

            ''' <summary>
            ''' Maximum concurrent physical reads an async multi-chunk read may have in flight
            ''' against the backing store at once, fetching the distinct physical records a
            ''' large read touches before the (already-parallel) per-chunk decrypt/decompress
            ''' runs on them. Same conditions and caveats as
            ''' <see cref="MaxPhysicalWriteParallelism"/>: needs the backing store to implement
            ''' <see cref="IPositionedStreamAsync"/> and declare
            ''' <see cref="PositionedIoCapabilities.LockFreeReads"/>, a plain FileStream does
            ''' neither, and the right value depends on the backing store's queue depth, not
            ''' processor count.
            ''' </summary>
            Public Property MaxPhysicalReadParallelism As Integer = 4

            ''' <summary>
            ''' When True, an operation that faults the stream part-way through - leaving its
            ''' in-memory extent, physical-record or anchor state half-applied - triggers an
            ''' automatic reload of the whole in-memory image from the backing stream (the
            ''' same crash recovery <see cref="ChunkedStream.Open" /> performs) instead of
            ''' leaving the stream unusable until it is disposed and reopened. The faulting
            ''' operation still throws; the stream is usable again from the next call. Default
            ''' is False, which keeps the faulted stream refusing all further calls until it
            ''' is disposed and reopened.
            ''' </summary>
            ''' <remarks>
            ''' Recovery reloads the last durably published generation, so any work since the
            ''' last durable metadata publish is discarded - the same outcome as a crash. If
            ''' the reload cannot produce a consistent image the stream stays faulted, the
            ''' original exception is raised and <see cref="ChunkedStream.FaultRecoveryException" />
            ''' holds the reload failure. Auto-recovery does not run while a checkpoint or a
            ''' <see cref="ChunkedStream.DeferPublish" /> scope is open - those already roll
            ''' back to their own baseline when they close.
            '''
            ''' Auto-recovery is only as sound as the backing stream. Back the ChunkedStream
            ''' with an unbuffered FileStream (constructed with a buffer size of 1) when
            ''' enabling this against a file: a write interrupted mid-call can otherwise
            ''' strand bytes in the FileStream write buffer that the reload cannot see, and
            ''' seeking flushes rather than discards that buffer. An unbuffered FileStream
            ''' does not slow ChunkedStream down - it writes whole records and metadata pages
            ''' and manages its own durable flushing, so the managed write buffer only adds a
            ''' copy it never gets to amortise.
            ''' </remarks>
            Public Property AutoRecoverOnFault As Boolean = False

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
            ''' Rewrite physical records that clearly exceed the current chunk-size policy.
            ''' </summary>
            ''' <remarks>
            ''' This is a best-effort policy, the same as <see cref="ChunkedStreamOptions.BisectLimit"/>: it
            ''' only rewrites a record once splitting it is clearly worthwhile - large enough over the
            ''' current bound that the pieces it produces are a sensible size - and it never rewrites a
            ''' record for being too small. Undersized chunks are expected and left alone: the last chunk of
            ''' any write, and any chunk next to an anchor, is often smaller than <c>ChunkSize</c> by
            ''' construction, and there's no way to enlarge one without pulling in data that either doesn't
            ''' exist yet or belongs to a boundary that must stay fixed. Consolidating undersized chunks, or
            ''' driving every chunk as close to the configured bounds as possible, is
            ''' <see cref="DefragTypes.Rebuild"/>'s job, not this one's.
            ''' </remarks>
            ChunkSize = 1 << 3

            ''' <summary>
            ''' Scans physical records that predate <see cref="ChunkedStreamOptions.Deduplication"/>
            ''' being turned on (or that were written before a previous catch-up scan reached them)
            ''' and indexes or deduplicates each one - see the deduplication index notes.
            ''' </summary>
            ''' <remarks>
            ''' Deliberately excluded from <see cref="All"/>: unlike the other categories, this
            ''' hashes and potentially decrypts every not-yet-covered record regardless of whether
            ''' <see cref="ChunkedStreamOptions.Deduplication"/> is even in use, so it must be
            ''' requested explicitly rather than paid for by every default <c>ApplyOptions()</c> call.
            ''' </remarks>
            Deduplication = 1 << 4

            All = Compression Or Encryption Or Sparseness Or ChunkSize
        End Enum

        ''' <summary>
        ''' Summary of an ApplyOptions operation.
        ''' </summary>
        Public NotInheritable Class ApplyOptionsResult

            ''' <summary>
            ''' Number of physical records split because they exceeded the current chunk-size
            ''' policy. Each one becomes two or more new records, all counted together in
            ''' <see cref="RewrittenChunks"/> as a single rewritten record, the same as every
            ''' other category here.
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
            ''' Number of chunks merged into an already-indexed duplicate found by the
            ''' deduplication catch-up scan (<see cref="ApplyOptionTypes.Deduplication"/>).
            ''' Does not count records that were simply indexed for the first time - those aren't
            ''' a "change" to the record itself.
            ''' </summary>
            Public Property DeduplicationChanges As Integer

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
                       $"chunkSize={ChunkSizeChanges}, dedup={DeduplicationChanges}, physicalDelta={PhysicalBytesChanged.FormatFileSizeFromBytes()}, cancelled={WasCancelled}]"
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

            Using EnterStateLock()
                Return ApplyOptionsCore(Types, ProgressCallback, Durable)
            End Using

        End Function

        ''' <summary>
        ''' Asynchronously rewrites existing chunks to conform to the current options.
        ''' </summary>
        ''' <remarks>
        ''' ApplyOptions is a long-running batch operation and is executed on a worker
        ''' thread. When <paramref name="CancellationToken" /> is cancelled it is surfaced to
        ''' the internal operation through its progress cancellation token; the call then
        ''' completes with <c>WasCancelled</c> set on the result rather than throwing.
        ''' </remarks>
        Public Function ApplyOptionsAsync(Optional Types As ApplyOptionTypes = ApplyOptionTypes.All,
                                          Optional ProgressCallback As StreamProgressCallback = Nothing,
                                          Optional Durable As Boolean = True,
                                          Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of ApplyOptionsResult)

            Dim EffectiveCallback = BridgeProgressCancellation(ProgressCallback, CancellationToken)

            '
            ' The token is surfaced through EffectiveCallback rather than handed to
            ' Task.Run, so cancellation completes with WasCancelled set on the result
            ' rather than faulting the task.
            '
            Return Task.Run(
                Function()
                    Using EnterStateLock()
                        Return ApplyOptionsCore(Types, EffectiveCallback, Durable)
                    End Using
                End Function)

        End Function

        Private Function ApplyOptionsCore(Optional Types As ApplyOptionTypes = ApplyOptionTypes.All,
                                     Optional ProgressCallback As StreamProgressCallback = Nothing,
                                     Optional Durable As Boolean = True) As ApplyOptionsResult


            ThrowIfDisposed()
            ThrowIfFaulted()

            If _DeferPublishDepth > 0 Then
                Throw New InvalidOperationException("ApplyOptions cannot be performed while metadata publishing is deferred.")
            End If

            ' A pending write-cache buffer isn't reflected in _Extents/_PhysicalRecords, so it must
            ' be materialised first - RunApplyOptions below iterates the live physical-record table
            ' directly, and the newly-committed chunk should get the same chance to be considered
            ' as everything else already on disk.
            If _PendingChunkPlain IsNot Nothing Then
                CommitPendingChunkAsync(RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()
            End If

            InvalidateChunkCache()

            Dim Result As New ApplyOptionsResult With {
                .PhysicalLengthBefore = BaseStream.Length,
                .PhysicalLengthAfter = BaseStream.Length
            }

            If Types = ApplyOptionTypes.None Then
                Return Result
            End If

            Try
                RunApplyOptions(Types, ProgressCallback, Durable, Result)

            Catch

                '
                ' A rewrite that fails partway has updated the in-memory extent and
                ' physical-record tables without publishing them - the single metadata
                ' publish only happens once the record loop completes. Fault the stream so
                ' DisposeCore does not persist that half-applied state over the last good
                ' generation; the caller can dispose and reopen the file unchanged.
                '
                _Faulted = True
                Throw

            End Try

            Return Result

        End Function

        ''' <summary>
        ''' Rebuilds the deduplication index from scratch, then runs the same catch-up scan
        ''' <see cref="ApplyOptionTypes.Deduplication"/> does to cover anything the rebuild
        ''' doesn't already account for.
        ''' </summary>
        ''' <param name="Soft">
        ''' True (the default) keeps the existing dedup key and every still-valid hash - entries
        ''' whose target record has since been reclaimed are dropped, everything else is copied
        ''' forward into a fresh index (picking up a changed
        ''' <see cref="ChunkedStreamOptions.DedupIndexPageEntryCount"/> along the way), cheaply,
        ''' with no re-hashing. False discards the key entirely (via <see cref="RegenerateDedupKey"/>)
        ''' and every existing hash with it - every live record's plaintext is re-hashed from
        ''' scratch under the new key, the only variant where key rotation makes sense, since it's
        ''' already paying to touch every record's plaintext.
        ''' </param>
        ''' <returns>A summary of the operation, in the same shape ApplyOptions returns.</returns>
        Public Function DedupRebuild(Optional Soft As Boolean = True) As ApplyOptionsResult

            Using EnterStateLock()
                Return DedupRebuildCore(Soft)
            End Using

        End Function

        Private Function DedupRebuildCore(Soft As Boolean) As ApplyOptionsResult

            ThrowIfDisposed()
            ThrowIfFaulted()

            If _DeferPublishDepth > 0 Then
                Throw New InvalidOperationException("DedupRebuild cannot be performed while metadata publishing is deferred.")
            End If

            ' See ApplyOptionsCore's remarks - the catch-up pass this runs afterward iterates the
            ' live physical-record table directly, which a pending write-cache buffer isn't part
            ' of yet.
            If _PendingChunkPlain IsNot Nothing Then
                CommitPendingChunkAsync(RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()
            End If

            Dim Result As New ApplyOptionsResult With {
                .PhysicalLengthBefore = BaseStream.Length,
                .PhysicalLengthAfter = BaseStream.Length
            }

            Try

                If Soft Then

                    '
                    ' HMAC values computed under the current key stay valid, so there's nothing
                    ' to re-hash - just drop entries whose target record no longer exists and
                    ' copy every other entry into a fresh table (which also picks up a changed
                    ' DedupIndexPageEntryCount). _DedupCoveredUpToRecordId is deliberately left
                    ' untouched: every record that was already covered and is still live keeps
                    ' its surviving entry, so re-examining it here would only insert a redundant
                    ' duplicate of the exact entry it already has. Anything never covered is
                    ' unaffected by the rebuild and still reaches the catch-up pass below.
                    '
                    Dim OldTable = _DedupHashTable
                    _DedupHashTable = Nothing

                    Dim NewTable = EnsureDedupHashTable()

                    If OldTable IsNot Nothing Then

                        For Each Entry In OldTable.GetAllEntries()

                            Dim Record As PhysicalRecordEntry = Nothing

                            If _PhysicalRecords.TryGetValue(Entry.Value, Record) AndAlso Record.RefCount > 0 Then
                                NewTable.Insert(Entry.Key, Entry.Value)
                            End If

                        Next

                    End If

                Else

                    '
                    ' Every existing hash was computed under the key being discarded, so the
                    ' whole table is worthless - Soft:=False is explicitly the "recompute
                    ' everything" variant. Resetting the high-water mark to 0 forces the
                    ' catch-up pass below to re-hash and re-index every live record from
                    ' scratch under the new key.
                    '
                    RegenerateDedupKey()
                    _DedupHashTable = Nothing
                    _DedupCoveredUpToRecordId = 0

                End If

                RunApplyOptions(ApplyOptionTypes.Deduplication, Nothing, Durable:=True, Result)

            Catch

                _Faulted = True
                Throw

            End Try

            Return Result

        End Function

        Private Sub RunApplyOptions(Types As ApplyOptionTypes,
                                    ProgressCallback As StreamProgressCallback,
                                    Durable As Boolean,
                                    Result As ApplyOptionsResult)

            Dim CancellationToken As New CancellationToken()

            '
            ' Chunk-size splitting is handled per-record inside ApplyRecordOptions, alongside
            ' Compression/Encryption, rather than as its own whole-stream rebuild pass - see
            ' TryComputeChunkSizeSplit. Declare the stream's chunk-size preference up front so
            ' it's recorded even if no individual record ends up needing a split.
            '
            If Types.HasFlag(ApplyOptionTypes.ChunkSize) Then
                _ChunkSize = Options.ChunkSize
                _ChunkSizeVariance = Options.ChunkSizeVariance
            End If

            If Types.HasFlag(ApplyOptionTypes.Sparseness) AndAlso Options.StoreSparseChunks Then

                MaterialiseSparseExtents(Result, ProgressCallback, CancellationToken)

                If CancellationToken.Cancel Then
                    Result.WasCancelled = True
                    Result.PhysicalLengthAfter = BaseStream.Length
                    Return
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

            '
            ' Detach every record the loop replaced in one batch (a single ordinal-map
            ' rebuild). Under an open checkpoint they stay pending for the outermost commit.
            '
            If HasOpenCheckpoint = False Then ApplyPendingPhysicalRecordReclaims()

            '
            ' Detaching the replaced records already evicted them one by one; drop the whole
            ' set as well so an open checkpoint (whose reclaims are still pending) and a
            ' cancelled or partial rewrite cannot leave a superseded record cached.
            '
            InvalidateChunkCache()

            Dim RemovedFileMasterKey = False

            If Result.WasCancelled = False AndAlso Types.HasFlag(ApplyOptionTypes.Encryption) Then
                RemovedFileMasterKey = RemoveUnusedFileMasterKeyIfPossible()
            End If

            '
            ' Close the high-water mark up to the highest id ever allocated, not just the highest
            ' one actually seen in the loop above - a reclaimed record that has since been dropped
            ' from _PhysicalRecords entirely would otherwise leave a permanent gap the mark can
            ' never advance past (there is nothing live left to process for it, but its id still
            ' needs to count as "considered"). Only safe when the scan actually ran to completion.
            '
            If Result.WasCancelled = False AndAlso Types.HasFlag(ApplyOptionTypes.Deduplication) Then
                _DedupCoveredUpToRecordId = Math.Max(_DedupCoveredUpToRecordId, _NextPhysicalRecordId - 1)
            End If

            If HasOpenCheckpoint = False AndAlso
               (Types.HasFlag(ApplyOptionTypes.ChunkSize) OrElse
                Types.HasFlag(ApplyOptionTypes.Deduplication) OrElse
                Result.RewrittenChunks > 0 OrElse
                RemovedFileMasterKey) Then
                PersistIndexAndHeader(_IndexOffset, Durable)
            End If

            Result.PhysicalLengthAfter = BaseStream.Length

        End Sub

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

            If Types.HasFlag(ApplyOptionTypes.ChunkSize) AndAlso Record.PlainLength > Options.MaxChunkSize Then

                Dim Split = TryComputeChunkSizeSplit(Plain)

                If Split IsNot Nothing Then

                    ApplyChunkSizeSplitToRecord(RecordId, Split)

                    Result.ChunkSizeChanges += 1

                    Return True

                End If

            End If

            '
            ' Records consumed by the Sparseness/ChunkSize branches above never reach here - they
            ' already returned. Records rewritten below by Compression/Encryption already get
            ' considered for deduplication through WritePhysicalRecordWithPolicyAsync's own hook
            ' (via ReplacePhysicalRecordWithNewRecord), so the only genuine gap this closes is a
            ' record that nothing else in this pass would ever touch.
            '
            If Types.HasFlag(ApplyOptionTypes.Deduplication) Then

                If ApplyDeduplicationCatchUp(RecordId, Plain, Result) Then
                    Return True
                End If

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


        ''' <summary>
        ''' Considers one not-yet-covered physical record for the deduplication index: merges it
        ''' into an already-indexed duplicate if a verified match exists, otherwise indexes it as
        ''' the canonical entry for its content. Always advances
        ''' <see cref="_DedupCoveredUpToRecordId"/> past RecordId - either outcome means this
        ''' record has now been considered, which is all the high-water mark promises.
        ''' </summary>
        ''' <returns>
        ''' True if RecordId was merged into an existing record (and so was "rewritten" the same
        ''' way a Compression/Encryption change is); False if it was newly indexed, or if it was
        ''' already covered by an earlier scan.
        ''' </returns>
        Private Function ApplyDeduplicationCatchUp(RecordId As Long,
                                                   Plain As Byte(),
                                                   Result As ApplyOptionsResult) As Boolean

            If RecordId <= _DedupCoveredUpToRecordId Then Return False

            Dim Hash = ComputeDedupHash(Plain, Plain.Length)
            Dim CandidateRecordId As Long

            If EnsureDedupHashTable().TryGetValue(Hash, CandidateRecordId) AndAlso CandidateRecordId <> RecordId Then

                Dim Candidate As PhysicalRecordEntry = Nothing

                If _PhysicalRecords.TryGetValue(CandidateRecordId, Candidate) AndAlso
                   Candidate.RefCount > 0 AndAlso
                   Candidate.PlainLength = Plain.Length Then

                    Dim CandidatePlain = ReadPhysicalRecordPlain(Candidate)

                    If PlainContentEquals(Plain, Plain.Length, CandidatePlain) Then

                        MergePhysicalRecordIntoExisting(RecordId, CandidateRecordId)

                        _DedupCoveredUpToRecordId = RecordId
                        Result.DeduplicationChanges += 1

                        Return True

                    End If

                End If

            End If

            ' No live, verified match - this record becomes the canonical entry for its content.
            EnsureDedupHashTable().Insert(Hash, RecordId)
            _DedupCoveredUpToRecordId = RecordId

            Return False

        End Function

        ''' <summary>
        ''' Redirects every extent referencing OldRecordId onto TargetRecordId (an existing,
        ''' already-live record - unlike ReplacePhysicalRecordWithNewRecord, nothing was just
        ''' written for this call, so every redirected extent needs its own increment) and
        ''' reclaims OldRecordId.
        ''' </summary>
        Private Sub MergePhysicalRecordIntoExisting(OldRecordId As Long, TargetRecordId As Long)

            Dim RedirectedExtentCount = 0

            For Index = 0 To _Extents.Count - 1

                Dim Extent = _Extents(Index)

                If Extent.PhysicalRecordId <> OldRecordId Then Continue For

                Extent.PhysicalRecordId = TargetRecordId
                _Extents(Index) = Extent
                RedirectedExtentCount += 1

            Next

            For AdditionalReference = 1 To RedirectedExtentCount
                IncrementPhysicalRecordRefCount(TargetRecordId)
            Next

            Dim OldRecord = GetPhysicalRecord(OldRecordId)
            OldRecord.RefCount = 0
            _PhysicalRecords(OldRecordId) = OldRecord

            _PendingReclaimedPhysicalRecords.Add(OldRecordId)

            MarkAllMetadataPagesDirty()

        End Sub

        Private Function NeedsCompressionRewrite(Header As ChunkHeaderSnapshot) As Boolean

            '
            ' A sampled evaluation is only an estimate. Always rewrite the chunk so it is
            ' re-evaluated on the full plaintext (ReplacePhysicalRecordWithNewRecord passes
            ' EvaluateFully), which also clears the estimated flag.
            '
            If Header.ChunkFlags.HasFlag(ChunkFlags.CompressionEstimated) Then
                Return True
            End If

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

        ''' <summary>
        ''' Computes, but does not commit, the chunk-size split for one over-bound record's
        ''' plaintext. Returns Nothing if splitting isn't worthwhile - see the remarks on
        ''' <see cref="ApplyOptionTypes.ChunkSize"/>.
        ''' </summary>
        ''' <remarks>
        ''' Only the final piece of a multi-piece split can ever be smaller than
        ''' <see cref="ChunkedStreamOptions.MinChunkSize"/> - every earlier cut is already
        ''' bounded within [MinChunkSize, MaxChunkSize] by <see cref="DetermineNextSegmentLength"/>
        ''' itself - so checking only the last piece against <see cref="ChunkedStreamOptions.BisectLimit"/>
        ''' is sufficient. At the default BisectLimit of 0 this never rejects a split.
        ''' </remarks>
        Private Function TryComputeChunkSizeSplit(Plain As Byte()) As List(Of ExtentIndexEntry)

            Dim Candidate = BuildExtentsFromBuffer(Plain, 0, Plain.Length)

            Dim RemainderLength = Candidate(Candidate.Count - 1).LogicalLength

            If RemainderLength < Options.BisectLimit Then Return Nothing

            Return Candidate

        End Function

        ''' <summary>
        ''' Commits a chunk-size split computed by <see cref="TryComputeChunkSizeSplit"/>,
        ''' replacing every current extent that references <paramref name="OldRecordId"/> with
        ''' one or more extents over the new, already-written records in
        ''' <paramref name="SplitPieces"/>.
        ''' </summary>
        ''' <remarks>
        ''' A record's live extents don't necessarily cover its whole plaintext contiguously -
        ''' an overwrite or removal that landed in the middle of it drops the extent for that
        ''' sub-range while leaving the record referenced (and alive) via the surrounding
        ''' extents that still point into it. So a new piece can end up covering only
        ''' logically-dead byte ranges with nothing to reference it; those are reclaimed here
        ''' rather than left as an orphaned, unreferenced physical record.
        ''' </remarks>
        Private Sub ApplyChunkSizeSplitToRecord(OldRecordId As Long, SplitPieces As List(Of ExtentIndexEntry))

            Dim Pieces As New List(Of (Start As Integer, Finish As Integer, RecordId As Long))
            Dim Running As Integer = 0

            For Each Piece In SplitPieces
                Pieces.Add((Running, Running + Piece.LogicalLength, Piece.PhysicalRecordId))
                Running += Piece.LogicalLength
            Next

            Dim ReferenceCounts As New Dictionary(Of Long, Integer)()
            Dim NewExtents As New List(Of ExtentIndexEntry)(_Extents.Count)

            For Each Extent In _Extents

                If Extent.PhysicalRecordId <> OldRecordId Then
                    NewExtents.Add(Extent)
                    Continue For
                End If

                Dim RangeStart = Extent.PhysicalRecordOffset
                Dim RangeEnd = Extent.PhysicalRecordOffset + Extent.LogicalLength
                Dim LogicalCursor = Extent.LogicalOffset
                Dim IsFirstReplacement = True

                For Each Piece In Pieces

                    Dim OverlapStart = Math.Max(Piece.Start, RangeStart)
                    Dim OverlapEnd = Math.Min(Piece.Finish, RangeEnd)

                    If OverlapStart >= OverlapEnd Then Continue For

                    NewExtents.Add(New ExtentIndexEntry With {
                        .LogicalOffset = LogicalCursor,
                        .LogicalLength = OverlapEnd - OverlapStart,
                        .PhysicalRecordId = Piece.RecordId,
                        .PhysicalRecordOffset = OverlapStart - Piece.Start,
                        .AnchorId = If(IsFirstReplacement, Extent.AnchorId, 0)
                    })

                    LogicalCursor += OverlapEnd - OverlapStart
                    IsFirstReplacement = False

                    If Piece.RecordId <> SparsePhysicalRecordId Then

                        If ReferenceCounts.ContainsKey(Piece.RecordId) Then
                            ReferenceCounts(Piece.RecordId) += 1
                        Else
                            ReferenceCounts(Piece.RecordId) = 1
                        End If

                    End If

                Next

            Next

            For Each Piece In Pieces

                If Piece.RecordId = SparsePhysicalRecordId Then Continue For

                If ReferenceCounts.ContainsKey(Piece.RecordId) = False Then

                    ' Nothing referenced this piece - it only ever covered a logically-dead
                    ' sub-range of the old record. Reclaim it rather than leak it.
                    Dim DeadRecord = GetPhysicalRecord(Piece.RecordId)
                    DeadRecord.RefCount = 0
                    _PhysicalRecords(Piece.RecordId) = DeadRecord
                    _PendingReclaimedPhysicalRecords.Add(Piece.RecordId)

                Else

                    ' Each new record already carries RefCount = 1 from its own creation.
                    For AdditionalReference = 2 To ReferenceCounts(Piece.RecordId)
                        IncrementPhysicalRecordRefCount(Piece.RecordId)
                    Next

                End If

            Next

            _Extents.Clear()
            _Extents.AddRange(NewExtents)

            Dim OldRecord = GetPhysicalRecord(OldRecordId)
            OldRecord.RefCount = 0
            _PhysicalRecords(OldRecordId) = OldRecord

            _PendingReclaimedPhysicalRecords.Add(OldRecordId)

            RebuildAnchorIndex()
            MarkAllMetadataPagesDirty()

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

            '
            ' Detached as one batch by ApplyPendingPhysicalRecordReclaims at the end of the
            ' record loop (RunApplyOptions), or - under a checkpoint - at the outermost
            ' commit, so migrating a large file rebuilds the ordinal map once, not once per
            ' chunk.
            '
            _PendingReclaimedPhysicalRecords.Add(RecordId)

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

            '
            ' WritePhysicalRecordWithPolicy already credits this call with one reference to
            ' NewRecord - whether it was just created (the common case) or is an existing record
            ' matched via deduplication, which may already carry other live references of its
            ' own. Either way, NewRecord.RefCount must never be overwritten outright: every extent
            ' below is redirected onto NewRecord.RecordId, and only the ones beyond the first need
            ' an additional increment to account for.
            '
            Dim NewRecord = WritePhysicalRecordWithPolicy(Plain,
                                                          Plain.Length,
                                                          CompressionMethod,
                                                          CompressionRatioThreshold,
                                                          ForceCompression,
                                                          EncryptionMethod,
                                                          EvaluateFully:=True)

            Dim RedirectedExtentCount = 0

            For Index = 0 To _Extents.Count - 1

                Dim Extent = _Extents(Index)

                If Extent.PhysicalRecordId <> OldRecordId Then Continue For

                Extent.PhysicalRecordId = NewRecord.RecordId
                _Extents(Index) = Extent
                RedirectedExtentCount += 1

            Next

            For AdditionalReference = 2 To RedirectedExtentCount
                IncrementPhysicalRecordRefCount(NewRecord.RecordId)
            Next

            OldRecord.RefCount = 0
            _PhysicalRecords(OldRecordId) = OldRecord

            ' Detached as one batch - see ReplacePhysicalRecordWithSparseExtents.
            _PendingReclaimedPhysicalRecords.Add(OldRecordId)

            MarkAllMetadataPagesDirty()

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

            If Record.PhysicalOffset + Record.PhysicalLength > BaseStream.Length Then
                Throw New System.IO.InvalidDataException($"Physical record {Record.RecordId} extends beyond the backing stream.")
            End If

            Dim Header(ChunkRecordHeaderSize - 1) As Byte

            ReadAt(Record.PhysicalOffset, Header, 0, Header.Length)

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

            If ChunkRecordHeaderSize + PayloadLength <> Record.PhysicalLength Then
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
