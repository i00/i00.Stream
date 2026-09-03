' ================================================================================
' ChunkedStream
' ================================================================================
'
' Compatibility
'   - Designed for .NET Framework 4.8+.
'   - Uses only APIs available in .NET Framework 4.8.
'   - Inherits Stream and supports standard stream operations such as Read, Write,
'     Seek, Position, Length, SetLength and Flush.
'   - Public stateful operations acquire a SemaphoreSlim-based state lock.
'   - Lock-free Core methods compose operations without reacquiring the lock.
'   - The underlying stream remains owned by the caller.
'     i.e. Disposing ChunkedStream does not dispose the underlying stream.
'
' Overview
'   - Random-access authenticated chunk storage.
'   - Encryption is optional.
'   - Compression is optional and evaluated per chunk.
'   - Sparse chunk support.
'   - Stable logical anchors for identifying logical data without requiring
'     external offset tracking.
'   - Dynamic paged metadata architecture.
'   - Metadata roots, index pages, directory pages and optional hole-directory pages.
'   - Configurable chunk-record placement policies.
'   - Optional hole reuse for chunk records and metadata pages.
'   - Defragmentation, recovery and ApplyOptions support.
'   - Data-only checkpoints with automatic rollback to the current checkpoint
'     baseline when disposed.
'   - Configurable fixed logical chunk size per stream.
'   - Optional most-recently-read plaintext chunk cache.
'   - Built-in structure and fragmentation diagnostics.
'
' Stream Model
'   - The standard Stream API reads and writes at Position.
'   - The random-access Read(LogicalOffset, Output) and Write(LogicalOffset, Input)
'     overloads do not use or modify Position.
'   - The stream is readable, writable and seekable.
'
' Logical / Physical Model
'   - The logical stream is represented by extents.
'   - Each extent references a physical record and an offset within that record.
'   - Multiple extents may reference the same physical record.
'   - Physical records are reference counted.
'   - Rewriting data typically creates new physical records and retires old records.
'   - Physical layout is independent of logical ordering.
'   - Sparse extents may reference no physical record.
'   - Extents may optionally contain immutable anchor identifiers.
'   - Anchors identify logical data starts rather than physical storage locations.
'   - Anchor identities remain stable across logical movement, compression changes,
'     encryption changes, copy-on-write operations, defragmentation and rebuilds.
'
' Anchor Model
'   - Anchors provide stable references to logical data.
'   - Anchor IDs are immutable and are never reused.
'   - An anchor identifies the beginning of an extent.
'   - At most one anchor may exist at any logical offset.
'   - Anchors may be created for existing logical data using:
'       - CreateAnchor(LogicalOffset)
'   - Anchors may be created while appending new data using:
'       - CreateAnchor(Data)
'   - CreateAnchor(Data) appends the data and anchors the first appended byte.
'   - Anchors may be resolved later using:
'       - GetAnchor(AnchorId)
'   - Anchor objects expose:
'       - AnchorId
'       - Offset
'       - IsValid
'       - Remove()
'   - Anchors belong to a specific ChunkedStream instance and cannot be used on
'     another ChunkedStream.
'   - Removing anchored data destroys the associated anchor.
'   - Explicit anchor removal does not remove the underlying data.
'
' Chunk Size Model
'   - New streams use Options.ChunkSize.
'   - Existing streams load the stored chunk size from the header and update
'     Options.ChunkSize to match.
'   - Changing Options.ChunkSize does Not immediately affect existing extents.
'     To apply ChunkSize changes use either:
'       - ApplyOptions(ApplyOptionTypes.ChunkSize)
'       - or Defragment(DefragTypes.Rebuild)
'   - Physical records are Not required To share a common size.
'   - Streams may legitimately contain physical records of many different sizes.
'   - Rebuild operations preserve anchored logical boundaries.
'
' Metadata Model
'   - Metadata is stored using variable-sized paged structures.
'   - Metadata pages may be physically relocated when rewritten.
'   - Metadata pages may optionally reuse suitable metadata holes.
'   - Metadata consists of:
'       - Extent Pages
'       - Extent Directory Pages
'       - Physical Record Pages
'       - Physical Record Directory Pages
'       - Hole Directory Pages
'       - Metadata Root
'   - Extent metadata includes AnchorId information.
'   - Only modified metadata pages are normally rewritten.
'   - Metadata publication is atomic from the perspective of readers.
'   - A newly written metadata root becomes active only after a header update.
'
' Metadata Root Model
'   - The metadata root contains descriptors for all active metadata pages.
'   - The metadata root stores:
'       - Extent Page descriptors
'       - Extent Directory Page descriptors
'       - Physical Record Page descriptors
'       - Physical Record Directory Page descriptors
'       - Hole Directory Page descriptors
'       - NextPhysicalRecordId
'       - NextAnchorId
'       - Metadata generation information
'   - The root is authenticated.
'   - Old roots become inactive after publication of a newer root.
'
' Hole Directory Model
'   - Hole records track reusable free space within the physical stream.
'   - Hole records are maintained in memory while the stream is open.
'   - Hole records may optionally be persisted depending on
'     Options.HoleDirectoryMode.
'   - Persisted hole records accelerate allocator reconstruction after reopen.
'   - Hole records may describe chunk-space holes and metadata-space holes.
'
' Write Location Model
'   - Newly written physical chunk records are placed according to
'     Options.NewChunkWriteLocationPolicy.
'
'       Append
'         - New chunk records are written beyond the current live data area.
'
'       FillHoles
'         - Existing reusable holes are preferred.
'         - If no suitable hole exists, allocation falls back to append.
'
'       FillHolesFromStart
'         - Existing reusable holes are preferred.
'         - Hole searching is biased toward lower physical offsets.
'         - If no suitable hole exists, allocation falls back to append.
'
'   - The selected policy affects newly written chunk records only.
'   - Existing chunk layout is not reorganised automatically.
'   - Defragmentation remains the preferred mechanism for compacting or
'     reordering existing chunk records.
'   - While a checkpoint is active, chunk records are always appended beyond the
'     currently committed data area regardless of the selected policy.
'     This preserves checkpoint rollback and crash-recovery behaviour.
'
' Read Cache Model
'   - The most recently loaded plaintext chunk may be cached.
'   - The cache is controlled by Options.UseChunkReadCache.
'   - Cache contents are invalidated when data, structure or checkpoint state changes.
'   - The cache is an optimisation only and is never required for correctness.
'
' Integrity Model
'   - Every header, metadata structure and chunk record is authenticated.
'   - If a structure is unencrypted, authentication uses a public integrity key.
'   - Public authentication detects corruption and accidental modification but is
'     not tamper-proof.
'   - If encryption is enabled, encrypted content uses keys derived from the
'     file master key.
'
' Encryption Model
'   - A random file master key is generated when encryption is first enabled.
'   - Encrypted chunks use keys derived from this file master key.
'   - The file master key is wrapped in the header.
'   - Changing the user encryption key only rewraps the file master key.
'   - Existing encrypted chunks do not need to be rewritten when the user key changes.
'   - If EncryptionInfo is set to Nothing, the file master key is publicly wrapped
'     while encrypted chunks still exist.
'   - If encryption is disabled and ApplyOptions(ApplyOptionTypes.Encryption)
'     rewrites all chunks as unencrypted, the unused file master key will be removed.
'   - New chunks are encrypted only when Options.EncryptionInfo is not Nothing.
'
' Compression Model
'   - Compression is evaluated per chunk.
'   - Each physical chunk record stores the compression method actually used.
'   - Each physical chunk record stores the compression method evaluated.
'   - Each physical chunk record stores the evaluated compressed-size percentage.
'   - Compression Evaluated Percent is the compressed payload size as a percentage
'     of the original plaintext size.
'   - Lower values indicate better compression.
'   - Options.CompressionRatioThreshold controls whether evaluated compression is stored.
'
' Sparse Chunk Model
'   - All-zero logical ranges may be represented by sparse extents.
'   - Sparse extents consume no physical payload storage.
'   - Sparse extents may be anchored.
'   - Physically stored all-zero records are marked using
'     the PlaintextAllZero flag.
'   - Sparse extents are treated as plaintext-all-zero by diagnostics.
'
' ApplyOptions Model
'   - ApplyOptions may rewrite existing chunks to conform to current settings.
'   - Compression, encryption and sparseness may be applied independently.
'   - Existing chunk records are rewritten only when required.
'   - Anchors survive ApplyOptions rewrites.
'   - ApplyOptions supports progress reporting and cancellation.
'
' Checkpoint Model
'   - CreateCheckpoint() creates a data-only checkpoint.
'   - Writes and length changes inside a checkpoint are visible immediately to reads.
'   - Commit() updates the checkpoint baseline to the current stream data state and
'     keeps the checkpoint active.
'   - Rollback() restores the current checkpoint baseline and keeps the checkpoint active.
'   - Dispose restores the current checkpoint baseline and closes the checkpoint.
'   - Checkpoints may be nested but must be committed, rolled back or disposed in
'     LIFO order.
'   - Committing an inner checkpoint only updates that inner checkpoint's baseline.
'   - A committed inner checkpoint is still part of its parent checkpoint and will
'     be rolled back if the parent checkpoint is rolled back or disposed.
'   - Only the outermost checkpoint owns header recovery state.
'   - Checkpoints roll back:
'       - Stream data
'       - Anchor creation/removal
'   - Options and encryption configuration are not rolled back.
'   - Defragmentation is not allowed while a checkpoint is active.
'
' Recovery Model
'   - Recovery state is stored in the header recovery area.
'   - Recovery is processed automatically during Open().
'   - Recovery always restores the most recently committed stream state.
'   - Chunk move recovery validates copied records before publication.
'   - Metadata publication recovery restores the last valid metadata root.
'   - Checkpoint recovery restores the checkpoint baseline.
'   - Chunk-size rebuild recovery truncates incomplete rebuild output and reopens
'     the previously committed stream state.
'   - Anchors recover as part of normal metadata recovery.
'
' File Layout
'
'   +---------------------------+
'   | Header A           512 B  |
'   +---------------------------+
'   | Header B           512 B  |
'   +---------------------------+
'   | Chunk Records             |
'   | Chunk Records             |
'   | Chunk Records             |
'   +---------------------------+
'   | Index Pages               |
'   +---------------------------+
'   | Index Directory Pages     |
'   +---------------------------+
'   | Hole Directory Pages      |
'   +---------------------------+
'   | Metadata Roots            |
'   +---------------------------+
'
'   DataStartOffset = 1024
'
' Header Strategy
'   - Header A and Header B are written alternately.
'   - Each header has its own sequence number and HMAC.
'   - Open() validates both headers and selects the valid header with the highest
'     sequence number.
'
' Structure Diagnostics
'   - Diagnostic APIs expose:
'       - Chunk regions
'       - Metadata regions
'       - Hole regions
'       - Fragmentation statistics
'       - Compression statistics
'       - Encryption statistics
'       - Physical layout information
'       - Anchor information
'   - Diagnostic information does not alter stream state.
'
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Namespace Streams

    ''' <summary>
    ''' A seekable <see cref="Stream" /> providing random-access, authenticated, optionally
    ''' encrypted and optionally compressed chunk storage over a caller-owned backing stream,
    ''' with sparse regions, stable logical anchors, data-only checkpoints, defragmentation,
    ''' crash recovery and structure diagnostics.
    ''' </summary>
    ''' <remarks>
    ''' See the file header for a full description of the stream, logical/physical, anchor,
    ''' metadata, checkpoint and recovery models.
    ''' </remarks>
    Public Class ChunkedStream
        Inherits Stream

        Private _Position As Long
        ''' <summary>
        ''' Gets or sets the current position within the logical stream used by the
        ''' standard <see cref="Stream" /> Read and Write methods. The random-access
        ''' Read and Write overloads do not use or modify this value.
        ''' </summary>
        Public Overrides Property Position As Long
            Get
                Using EnterStateLock()
                    Return GetPositionCore()
                End Using
            End Get
            Set
                Using EnterStateLock()
                    SetPositionCore(Value)
                End Using
            End Set
        End Property

        Private Function GetPositionCore() As Long
            ThrowIfDisposed()
            Return _Position
        End Function

        Private Sub SetPositionCore(Value As Long)
            ThrowIfDisposed()
            If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Value))
            _Position = Value
        End Sub

        ''' <summary>
        ''' Gets a value indicating whether the stream supports reading. Reflects the
        ''' capability of the backing stream.
        ''' </summary>
        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return BaseStream.CanRead
            End Get
        End Property

        ''' <summary>
        ''' Gets a value indicating whether the stream supports writing. Reflects the
        ''' capability of the backing stream.
        ''' </summary>
        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return BaseStream.CanWrite
            End Get
        End Property

        ''' <summary>
        ''' Gets a value indicating whether the stream supports seeking. Reflects the
        ''' capability of the backing stream.
        ''' </summary>
        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return BaseStream.CanSeek
            End Get
        End Property

        ''' <summary>
        ''' Sets the position within the logical stream used by the standard
        ''' <see cref="Stream" /> Read and Write methods.
        ''' </summary>
        ''' <param name="Offset">Byte offset relative to <paramref name="Origin" />.</param>
        ''' <param name="Origin">Reference point used to interpret <paramref name="Offset" />.</param>
        ''' <returns>The new position within the logical stream.</returns>
        Public Overrides Function Seek(Offset As Long,
                                       Origin As SeekOrigin) As Long

            Using EnterStateLock()
                Return SeekCore(Offset, Origin)
            End Using

        End Function

        Private Function SeekCore(Offset As Long, Origin As SeekOrigin) As Long

            ThrowIfDisposed()

            Select Case Origin
                Case SeekOrigin.Begin
                    SetPositionCore(Offset)
                Case SeekOrigin.Current
                    SetPositionCore(_Position + Offset)
                Case SeekOrigin.End
                    SetPositionCore(_Length + Offset)
                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(Origin))
            End Select

            Return _Position

        End Function

        ''' <summary>
        ''' Unit type used by long-running operation progress callbacks.
        ''' </summary>
        Public Enum ProcessUnitTypes

            ''' <summary>
            ''' Progress values are byte counts.
            ''' </summary>
            Bytes = 0

            ''' <summary>
            ''' Progress values are chunk counts.
            ''' </summary>
            Chunks = 1

            ''' <summary>
            ''' Progress values are operation-specific arbitrary units.
            ''' </summary>
            Arbitrary = 2

        End Enum

        ''' <summary>
        ''' Cancellation token used by progress callbacks.
        ''' </summary>
        Public NotInheritable Class CancellationToken

            ''' <summary>
            ''' Set to True from the callback to request cancellation.
            ''' </summary>
            Public Property Cancel As Boolean

        End Class

        ''' <summary>
        ''' Reports progress for long-running ChunkedStream operations.
        ''' </summary>
        ''' <param name="ProcessedUnits">The current progress value.</param>
        ''' <param name="TotalUnits">The maximum progress value.</param>
        ''' <param name="UnitType">What the units used for progress represent.</param>
        ''' <param name="CancellationToken">Token that may be set to request cancellation.</param>
        Public Delegate Sub StreamProgressCallback(ProcessedUnits As Long,
                                                   TotalUnits As Long,
                                                   UnitType As ProcessUnitTypes,
                                                   CancellationToken As CancellationToken)

        ''' <summary>
        ''' Default logical chunk size, in bytes, used when no chunk size is configured.
        ''' </summary>
        Public Const DefaultChunkSize As Integer = 64 * 1024

        ''' <summary>
        ''' Size, in bytes, of the initialisation vector stored in each physical chunk record.
        ''' </summary>
        Friend Const IvSize As Integer = 16

        ''' <summary>
        ''' Size, in bytes, of the HMAC-SHA256 authentication tag used throughout the format.
        ''' </summary>
        Friend Const MacSize As Integer = 32

        ''' <summary>
        ''' Size, in bytes, of a single stream header copy.
        ''' </summary>
        Friend Const HeaderSize As Integer = 512

        ''' <summary>
        ''' Number of alternating header copies written at the start of the backing stream.
        ''' </summary>
        Friend Const HeaderCopyCount As Integer = 2

        ''' <summary>
        ''' Physical offset at which chunk records and metadata may begin, immediately after
        ''' the header copies.
        ''' </summary>
        Friend Const DataStartOffset As Integer = HeaderSize * HeaderCopyCount

        Private Const MagicOffset As Integer = 0
        Private Const MagicSize As Integer = 8

        <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
        Friend Const HeaderSequenceOffset As Integer = 8
        Private Const FlagsOffset As Integer = 16
        Private Const LengthOffset As Integer = 24
        Private Const FileSaltOffset As Integer = 32
        Private Const FileSaltSize As Integer = 16
        Private Const ChunkSizeOffset As Integer = 48
        Private Const IndexOffsetOffset As Integer = 52
        Private Const IndexCountOffset As Integer = 60
        Private Const IndexMacOffset As Integer = 68

        Private Const JournalStateOffset As Integer = 100
        Private Const JournalChunkIndexOffset As Integer = 108
        Private Const JournalOldOffsetOffset As Integer = 116
        Private Const JournalOldLengthOffset As Integer = 124
        Private Const JournalNewOffsetOffset As Integer = 132
        Private Const JournalNewLengthOffset As Integer = 140

        Private Const JournalAreaOffset As Integer = 100
        Private Const JournalAreaLength As Integer = 64

        'TODO: hrm ... kind of duplicated names ... also maybe name some better... I mean 2x offsets etc: JournalOldOffsetOffset (above) and names with things like LengthOffset ... is it length or offset :P?
        Private Const RecoveryStateOffset As Integer = JournalStateOffset
        Private Const RecoveryAreaOffset As Integer = JournalAreaOffset
        Private Const RecoveryAreaLength As Integer = JournalAreaLength

        Private Const RecoveryCheckpointPhysicalLengthOffset As Integer = JournalChunkIndexOffset
        Private Const RecoveryCheckpointIndexOffsetOffset As Integer = JournalOldOffsetOffset
        Private Const RecoveryCheckpointLogicalLengthOffset As Integer = JournalOldLengthOffset

        Private Const MasterKeyWrapModeOffset As Integer = 164
        Private Const MasterKeyWrapSaltOffset As Integer = 168
        Private Const MasterKeyWrapSaltSize As Integer = 16
        Private Const WrappedFileMasterKeyOffset As Integer = 184
        Private Const WrappedFileMasterKeySize As Integer = 32
        Private Const WrappedFileMasterKeyMacOffset As Integer = 216
        Private Const WrappedFileMasterKeyMacSize As Integer = 32
        Private Const MasterKeyWrapAreaOffset As Integer = 164
        Private Const MasterKeyWrapAreaLength As Integer = 84

        Private Const HeaderMacOffset As Integer = 480
        Private Const HeaderMacCoveredSize As Integer = 480

        Private Const ExtentEntrySize As Integer = 32
        Private Const PhysicalRecordEntrySize As Integer = 32

        Private Const ChunkRecordHeaderSize As Integer = 48
        Private Const ChunkRecordIvOffset As Integer = ChunkRecordHeaderSize
        Private Const ChunkRecordDataOffset As Integer = ChunkRecordHeaderSize + IvSize
        Private Const MinChunkRecordSize As Integer = ChunkRecordHeaderSize + IvSize + MacSize

        Private Const ChunkCompressionMethodOffset As Integer = 8
        Private Const ChunkEncryptionMethodOffset As Integer = 12
        Private Const ChunkPlainLengthOffset As Integer = 16
        Private Const ChunkPayloadLengthOffset As Integer = 20
        <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
        Friend Const ChunkFlagsOffset As Integer = 24

        Private Const ChunkCompressionEvaluatedMethodOffset As Integer = 28
        <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
        Friend Const ChunkCompressionEvaluatedPercentOffset As Integer = 32

        Private Const ChunkReservedOffset As Integer = 33
        Private Const ChunkReservedSize As Integer = 15

        Private Const MetadataRootOffsetOffset As Integer = 248
        Private Const MetadataRootLengthOffset As Integer = 256
        Private Const IndexPageEntryCountOffset As Integer = 260
        Private Const IndexDirectoryEntryCountOffset As Integer = 264
        Private Const MetadataReservedOffset As Integer = 268
        Private Const MetadataReservedLength As Integer = 212

        Private Const MetadataRootHeaderSize As Integer = 72
        Private Const MetadataRootMagicSize As Integer = 8
        Private Const MetadataDescriptorSize As Integer = 48
        Private Const MetadataRootDescriptorSize As Integer = 52

        Private Const IndexPageHeaderSize As Integer = 32
        Private Const IndexPageMagicSize As Integer = 8

        Private Const DirectoryPageHeaderSize As Integer = 32
        Private Const DirectoryPageMagicSize As Integer = 8

        Private Const HoleDirectoryEntrySize As Integer = 24

        Private Shared ReadOnly MetadataRootMagic As Byte() = Encoding.ASCII.GetBytes("MROOT001")
        Private Shared ReadOnly IndexPageMagic As Byte() = Encoding.ASCII.GetBytes("IXPAGE01")
        Private Shared ReadOnly DirectoryPageMagic As Byte() = Encoding.ASCII.GetBytes("DIRPAGE1")

        Private Enum DirectoryTypes As Integer
            None = 0
            ExtentPages = 1
            PhysicalRecordPages = 2
            Holes = 3
        End Enum

        'TODO: IS THIS ENUM USEFUL ANYMORE ... REMOVE IT?
        Private Enum HoleSpaceTypes As Integer
            None = 0
            FreeSpace = 1
        End Enum

        Private Structure MetadataPageDescriptor
            Public PageNumber As Integer
            Public Offset As Long
            Public Length As Integer
            Public Mac As Byte()
        End Structure

        Private Structure HoleDirectoryRecord
            Public SpaceType As HoleSpaceTypes
            Public Offset As Long
            Public Length As Long
        End Structure

        Private Shared ReadOnly HeaderMagic As Byte() = Encoding.ASCII.GetBytes("CSTRM001")
        Private Shared ReadOnly PublicIntegrityKey As Byte() = Encoding.UTF8.GetBytes("ChunkedStream Public Integrity Key")

        <Flags>
        Friend Enum HeaderFlags As Long
            None = 0
            VariableChunkIndex = 1 << 0
            StoreSparseChunks = 1 << 1
            CompressionDeflate = 1 << 2
            CompressionGZip = 1 << 3
            CompressionLz4 = 1 << 4
            CompressionSnappy = 1 << 5
        End Enum

        Friend Const SparsePhysicalRecordId As Long = 0

        Friend Structure ExtentIndexEntry

            Public LogicalOffset As Long
            Public LogicalLength As Integer
            Public PhysicalRecordId As Long

            Private _PhysicalRecordOffset As Integer

            Public Property PhysicalRecordOffset As Integer
                Get
                    Return _PhysicalRecordOffset
                End Get
                Set
                    _PhysicalRecordOffset = Value
                End Set
            End Property

            '
            ' Zero means unanchored.
            ' A positive value identifies this extent's logical start.
            '
            Public AnchorId As Long

        End Structure

        Friend Structure PhysicalRecordEntry

            Public RecordId As Long

            Public PhysicalOffset As Long

            Public PhysicalLength As Integer

            Public PlainLength As Integer

            Public RefCount As Integer

        End Structure

        Private Structure HeaderCandidate
            Public Header As Byte()
            Public HeaderSequence As Long
            Public HeaderCopyIndex As Integer
        End Structure

        ''' <summary>
        ''' The backing storage stream. The caller owns its lifetime; disposing the
        ''' ChunkedStream does not dispose this stream.
        ''' </summary>
        Public ReadOnly BaseStream As Stream

        Private _FlushDurableAction As Action

        ''' <summary>
        ''' Identifies which categories of physical I/O operation acquire the shared
        ''' physical-I/O lock.
        ''' </summary>
        <Flags>
        Protected Enum PhysicalIoLockStates
            ''' <summary>
            ''' No physical-I/O locking is required.
            ''' </summary>
            None = 0

            ''' <summary>
            ''' Position-based writes acquire the physical-I/O lock.
            ''' </summary>
            WriteLock = 1 << 0

            ''' <summary>
            ''' Position-based reads acquire the physical-I/O lock.
            ''' </summary>
            ReadLock = 1 << 1

            ''' <summary>
            ''' Both position-based reads and writes acquire the physical-I/O lock.
            ''' </summary>
            FullLock = WriteLock Or ReadLock
        End Enum

        Private ReadOnly _PhysicalIoLock As New SemaphoreSlim(1, 1)

        ''' <summary>
        ''' Selects which physical I/O operations use the shared physical-I/O lock.
        ''' </summary>
        ''' <remarks>
        ''' Removing a lock flag is valid only when the corresponding Core operation
        ''' does not use or modify the backing stream Position and is safe for the
        ''' resulting concurrent access pattern.
        ''' Read and write operations that request locking share one physical-I/O
        ''' lock so position-based reads and writes cannot interfere with each other.
        ''' </remarks>
        Protected Overridable ReadOnly Property RequiredPhysicalIoLocks As PhysicalIoLockStates
            Get
                Dim PositionedStream = TryCast(BaseStream, IPositionedStream)

                If PositionedStream Is Nothing Then
                    Return PhysicalIoLockStates.FullLock
                End If

                Dim Result = PhysicalIoLockStates.None
                Dim Capabilities = PositionedStream.PositionedIoCapabilities

                If Capabilities.HasFlag(PositionedIoCapabilities.LockFreeReads) = False Then
                    Result = Result Or PhysicalIoLockStates.ReadLock
                End If

                If Capabilities.HasFlag(PositionedIoCapabilities.LockFreeWrites) = False Then
                    Result = Result Or PhysicalIoLockStates.WriteLock
                End If

                Return Result
            End Get
        End Property

        Private Sub ReadAt(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer)
            ValidatePhysicalIoArguments(PhysicalOffset, Buffer, BufferOffset, Count)
            If Count = 0 Then Return
            If RequiredPhysicalIoLocks.HasFlag(PhysicalIoLockStates.ReadLock) Then
                _PhysicalIoLock.Wait()
                Try
                    ReadAtCore(PhysicalOffset, Buffer, BufferOffset, Count)
                Finally
                    _PhysicalIoLock.Release()
                End Try
            Else
                ReadAtCore(PhysicalOffset, Buffer, BufferOffset, Count)
            End If
        End Sub

        Private Sub WriteAt(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer)
            ValidatePhysicalIoArguments(PhysicalOffset, Buffer, BufferOffset, Count)
            If Count = 0 Then Return
            If RequiredPhysicalIoLocks.HasFlag(PhysicalIoLockStates.WriteLock) Then
                _PhysicalIoLock.Wait()
                Try
                    WriteAtCore(PhysicalOffset, Buffer, BufferOffset, Count)
                Finally
                    _PhysicalIoLock.Release()
                End Try
            Else
                WriteAtCore(PhysicalOffset, Buffer, BufferOffset, Count)
            End If
        End Sub

        Private Async Function ReadAtAsync(PhysicalOffset As Long,
                                           Buffer As Byte(),
                                           BufferOffset As Integer,
                                           Count As Integer,
                                           CancellationToken As Threading.CancellationToken) As Task

            ValidatePhysicalIoArguments(PhysicalOffset, Buffer, BufferOffset, Count)
            If Count = 0 Then Return

            If RequiredPhysicalIoLocks.HasFlag(PhysicalIoLockStates.ReadLock) Then
                Await _PhysicalIoLock.WaitAsync(CancellationToken).ConfigureAwait(False)
                Try
                    Await ReadAtCoreAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
                Finally
                    _PhysicalIoLock.Release()
                End Try
            Else
                Await ReadAtCoreAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
            End If

        End Function

        Private Async Function WriteAtAsync(PhysicalOffset As Long,
                                            Buffer As Byte(),
                                            BufferOffset As Integer,
                                            Count As Integer,
                                            CancellationToken As Threading.CancellationToken) As Task

            ValidatePhysicalIoArguments(PhysicalOffset, Buffer, BufferOffset, Count)
            If Count = 0 Then Return

            If RequiredPhysicalIoLocks.HasFlag(PhysicalIoLockStates.WriteLock) Then
                Await _PhysicalIoLock.WaitAsync(CancellationToken).ConfigureAwait(False)
                Try
                    Await WriteAtCoreAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
                Finally
                    _PhysicalIoLock.Release()
                End Try
            Else
                Await WriteAtCoreAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
            End If

        End Function

        ''' <summary>
        ''' Reads exactly <paramref name="Count" /> bytes from the backing stream at the
        ''' specified physical offset.
        ''' </summary>
        ''' <remarks>
        ''' The default implementation uses <see cref="IPositionedStream" /> when the backing
        ''' stream supports it, otherwise it seeks and reads while holding the physical-I/O
        ''' lock. Override to provide a custom positioned-read strategy.
        ''' </remarks>
        ''' <param name="PhysicalOffset">Physical offset within the backing stream to read from.</param>
        ''' <param name="Buffer">Destination buffer.</param>
        ''' <param name="BufferOffset">Offset within <paramref name="Buffer" /> to store the first byte.</param>
        ''' <param name="Count">Number of bytes to read.</param>
        Protected Overridable Sub ReadAtCore(PhysicalOffset As Long,
                                             Buffer As Byte(),
                                             BufferOffset As Integer,
                                             Count As Integer)

            Dim PositionedStream = TryCast(BaseStream, IPositionedStream)

            If PositionedStream IsNot Nothing Then
                Dim TotalRead = 0

                While TotalRead < Count
                    Dim BytesRead =
                        PositionedStream.ReadAt(
                            PhysicalOffset + TotalRead,
                            Buffer,
                            BufferOffset + TotalRead,
                            Count - TotalRead)

                    If BytesRead <= 0 Then
                        Throw New EndOfStreamException("Unexpected end of positioned stream.")
                    End If

                    If BytesRead > Count - TotalRead Then
                        Throw New InvalidDataException("The positioned stream returned more bytes than requested.")
                    End If

                    TotalRead += BytesRead
                End While

                Return
            End If

            If RequiredPhysicalIoLocks <> PhysicalIoLockStates.FullLock Then
                Throw New InvalidOperationException(
                    $"The default position-based {NameOf(ReadAtCore)} implementation requires " &
                    $"{NameOf(PhysicalIoLockStates.FullLock)}. Override {NameOf(ReadAtCore)} " &
                    $"or provide a backing stream that implements {NameOf(IPositionedStream)}.")
            End If

            BaseStream.Position = PhysicalOffset
            ReadExactly(BaseStream, Buffer, BufferOffset, Count)

        End Sub

        ''' <summary>
        ''' Writes <paramref name="Count" /> bytes to the backing stream at the specified
        ''' physical offset.
        ''' </summary>
        ''' <remarks>
        ''' The default implementation uses <see cref="IPositionedStream" /> when the backing
        ''' stream supports it, otherwise it seeks and writes while holding the physical-I/O
        ''' lock. Override to provide a custom positioned-write strategy.
        ''' </remarks>
        ''' <param name="PhysicalOffset">Physical offset within the backing stream to write to.</param>
        ''' <param name="Buffer">Source buffer.</param>
        ''' <param name="BufferOffset">Offset within <paramref name="Buffer" /> of the first byte to write.</param>
        ''' <param name="Count">Number of bytes to write.</param>
        Protected Overridable Sub WriteAtCore(PhysicalOffset As Long,
                                              Buffer As Byte(),
                                              BufferOffset As Integer,
                                              Count As Integer)

            Dim PositionedStream = TryCast(BaseStream, IPositionedStream)

            If PositionedStream IsNot Nothing Then
                PositionedStream.WriteAt(
                    PhysicalOffset,
                    Buffer,
                    BufferOffset,
                    Count)

                Return
            End If

            If RequiredPhysicalIoLocks <> PhysicalIoLockStates.FullLock Then
                Throw New InvalidOperationException(
                    $"The default position-based {NameOf(WriteAtCore)} implementation requires " &
                    $"{NameOf(PhysicalIoLockStates.FullLock)}. Override {NameOf(WriteAtCore)} " &
                    $"or provide a backing stream that implements {NameOf(IPositionedStream)}.")
            End If

            BaseStream.Position = PhysicalOffset
            BaseStream.Write(Buffer, BufferOffset, Count)

        End Sub

        ''' <summary>
        ''' Asynchronously reads exactly <paramref name="Count" /> bytes from the backing
        ''' stream at the specified physical offset.
        ''' </summary>
        ''' <remarks>
        ''' The default implementation prefers <see cref="IPositionedStreamAsync" />, then
        ''' the synchronous <see cref="IPositionedStream" /> (offloaded so the calling
        ''' thread is not blocked), otherwise it seeks and awaits <see cref="Stream.ReadAsync" />
        ''' while holding the physical-I/O lock. Override to provide a custom strategy.
        ''' </remarks>
        Protected Overridable Async Function ReadAtCoreAsync(PhysicalOffset As Long,
                                                            Buffer As Byte(),
                                                            BufferOffset As Integer,
                                                            Count As Integer,
                                                            CancellationToken As Threading.CancellationToken) As Task

            Dim AsyncPositionedStream = TryCast(BaseStream, IPositionedStreamAsync)

            If AsyncPositionedStream IsNot Nothing Then

                Dim TotalRead = 0

                While TotalRead < Count

                    Dim BytesRead =
                        Await AsyncPositionedStream.ReadAtAsync(
                            PhysicalOffset + TotalRead,
                            Buffer,
                            BufferOffset + TotalRead,
                            Count - TotalRead,
                            CancellationToken).ConfigureAwait(False)

                    If BytesRead <= 0 Then
                        Throw New EndOfStreamException("Unexpected end of positioned stream.")
                    End If

                    If BytesRead > Count - TotalRead Then
                        Throw New InvalidDataException("The positioned stream returned more bytes than requested.")
                    End If

                    TotalRead += BytesRead

                End While

                Return

            End If

            Dim PositionedStream = TryCast(BaseStream, IPositionedStream)

            If PositionedStream IsNot Nothing Then
                '
                ' The backing stream is position-free but has no async positioned form.
                ' Honour its concurrency contract by using its synchronous ReadAt, offloaded
                ' so the async caller's thread is not held for the duration of the I/O.
                '
                Await Task.Run(
                    Sub() ReadAtCore(PhysicalOffset, Buffer, BufferOffset, Count),
                    CancellationToken).ConfigureAwait(False)

                Return

            End If

            If RequiredPhysicalIoLocks <> PhysicalIoLockStates.FullLock Then
                Throw New InvalidOperationException(
                    $"The default position-based {NameOf(ReadAtCoreAsync)} implementation requires " &
                    $"{NameOf(PhysicalIoLockStates.FullLock)}. Override {NameOf(ReadAtCoreAsync)} " &
                    $"or provide a backing stream that implements {NameOf(IPositionedStream)}.")
            End If

            BaseStream.Position = PhysicalOffset
            Await ReadExactlyAsync(BaseStream, Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)

        End Function

        ''' <summary>
        ''' Asynchronously writes <paramref name="Count" /> bytes to the backing stream at
        ''' the specified physical offset.
        ''' </summary>
        ''' <remarks>
        ''' The default implementation prefers <see cref="IPositionedStreamAsync" />, then
        ''' the synchronous <see cref="IPositionedStream" /> (offloaded), otherwise it seeks
        ''' and awaits <see cref="Stream.WriteAsync" /> while holding the physical-I/O lock.
        ''' Override to provide a custom strategy.
        ''' </remarks>
        Protected Overridable Async Function WriteAtCoreAsync(PhysicalOffset As Long,
                                                             Buffer As Byte(),
                                                             BufferOffset As Integer,
                                                             Count As Integer,
                                                             CancellationToken As Threading.CancellationToken) As Task

            Dim AsyncPositionedStream = TryCast(BaseStream, IPositionedStreamAsync)

            If AsyncPositionedStream IsNot Nothing Then
                Await AsyncPositionedStream.WriteAtAsync(
                    PhysicalOffset,
                    Buffer,
                    BufferOffset,
                    Count,
                    CancellationToken).ConfigureAwait(False)

                Return

            End If

            Dim PositionedStream = TryCast(BaseStream, IPositionedStream)

            If PositionedStream IsNot Nothing Then
                Await Task.Run(
                    Sub() WriteAtCore(PhysicalOffset, Buffer, BufferOffset, Count),
                    CancellationToken).ConfigureAwait(False)

                Return

            End If

            If RequiredPhysicalIoLocks <> PhysicalIoLockStates.FullLock Then
                Throw New InvalidOperationException(
                    $"The default position-based {NameOf(WriteAtCoreAsync)} implementation requires " &
                    $"{NameOf(PhysicalIoLockStates.FullLock)}. Override {NameOf(WriteAtCoreAsync)} " &
                    $"or provide a backing stream that implements {NameOf(IPositionedStream)}.")
            End If

            BaseStream.Position = PhysicalOffset
            Await BaseStream.WriteAsync(Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)

        End Function

        '
        ' Flag-driven dispatch helpers. A method on the shared write / metadata / open
        ' spine carries a RunAsync flag; at each backing-store touch point it calls one of
        ' these, which either awaits the real async primitive (RunAsync) or runs the
        ' synchronous one and hands back an already-completed Task. When RunAsync is False
        ' no await ever suspends, so the whole spine method completes synchronously and its
        ' synchronous entry point can safely take the result with GetAwaiter().GetResult().
        '
        Private Function ReadAtEitherAsync(RunAsync As Boolean,
                                           PhysicalOffset As Long,
                                           Buffer As Byte(),
                                           BufferOffset As Integer,
                                           Count As Integer,
                                           CancellationToken As Threading.CancellationToken) As Task

            If RunAsync Then
                Return ReadAtAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken)
            End If

            ReadAt(PhysicalOffset, Buffer, BufferOffset, Count)
            Return Task.CompletedTask

        End Function

        Private Function WriteAtEitherAsync(RunAsync As Boolean,
                                            PhysicalOffset As Long,
                                            Buffer As Byte(),
                                            BufferOffset As Integer,
                                            Count As Integer,
                                            CancellationToken As Threading.CancellationToken) As Task

            If RunAsync Then
                Return WriteAtAsync(PhysicalOffset, Buffer, BufferOffset, Count, CancellationToken)
            End If

            WriteAt(PhysicalOffset, Buffer, BufferOffset, Count)
            Return Task.CompletedTask

        End Function

        Private Function FlushDurableEitherAsync(RunAsync As Boolean) As Task

            If RunAsync Then Return FlushDurableAsync()

            FlushDurable()
            Return Task.CompletedTask

        End Function

        Private Function ReadExtentBytesEitherAsync(RunAsync As Boolean,
                                                    Extent As ExtentIndexEntry,
                                                    OffsetInsideExtent As Integer,
                                                    Output As Byte(),
                                                    OutputOffset As Integer,
                                                    Count As Integer,
                                                    CancellationToken As Threading.CancellationToken) As Task

            If RunAsync Then
                Return ReadExtentBytesAsync(Extent, OffsetInsideExtent, Output, OutputOffset, Count, CancellationToken)
            End If

            ReadExtentBytes(Extent, OffsetInsideExtent, Output, OutputOffset, Count)
            Return Task.CompletedTask

        End Function

        Private Shared Sub ValidatePhysicalIoArguments(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer)
            If PhysicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalOffset))
            If Buffer Is Nothing Then Throw New ArgumentNullException(NameOf(Buffer))
            If BufferOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(BufferOffset))
            If Count < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Count))
            If BufferOffset > Buffer.Length - Count Then Throw New ArgumentException("Buffer offset and count exceed the buffer length.")
        End Sub
        Private ReadOnly _StateLock As New SemaphoreSlim(1, 1)

        ' WARNING: AsyncLocal flows forward into asynchronous forks (e.g., Task.Run, Task.WhenAll) as a shallow copy.
        ' If concurrent tasks are spawned while this lock is held, both branches will independently inherit the same
        ' depth counter, risking concurrent execution without proper re-acquisition.
        ' DO NOT introduce concurrent fan-out (like Task.WhenAll) under this lock.
        Private ReadOnly _StateLockDepth As New AsyncLocal(Of Integer)

        Private Function EnterStateLock() As IDisposable
            If _StateLockDepth.Value = 0 Then _StateLock.Wait()
            _StateLockDepth.Value += 1
            Return New StateLockScope(Me)
        End Function

        '
        ' Acquires the state lock for an async operation unless the current async flow
        ' already holds it, returning True when this call acquired it and must release it.
        '
        ' The reentrancy depth is an AsyncLocal, and a write made inside an awaited async
        ' method does NOT flow back to its caller (the caller's ExecutionContext is
        ' restored on resume). So this helper only performs the wait; the caller sets
        ' _StateLockDepth.Value in the method that owns the Try/Finally, where it stays
        ' visible to everything that method calls and unwinds automatically when it
        ' returns. Callers release with _StateLock.Release() (synchronous, non-blocking).
        '
        Private Async Function EnterStateLockAsync(RunAsync As Boolean,
                                                  CancellationToken As Threading.CancellationToken) As Task(Of Boolean)

            If _StateLockDepth.Value > 0 Then Return False

            If RunAsync Then
                Await _StateLock.WaitAsync(CancellationToken).ConfigureAwait(False)
            Else
                _StateLock.Wait()
            End If

            Return True

        End Function

        '
        ' Runs an asynchronous operation body under the state lock. Acquires the lock unless
        ' the current async flow already holds it, sets the reentrancy depth here (in the
        ' method that owns the Try/Finally, so it stays visible to the awaited body and
        ' unwinds when this returns), and releases on the way out.
        '
        Private Async Function RunUnderStateLockAsync(CancellationToken As Threading.CancellationToken,
                                                     Body As Func(Of Task)) As Task

            CancellationToken.ThrowIfCancellationRequested()

            Dim LockOwner = Await EnterStateLockAsync(True, CancellationToken).ConfigureAwait(False)
            If LockOwner Then _StateLockDepth.Value = 1

            Try
                Await Body().ConfigureAwait(False)
            Finally
                If LockOwner Then
                    _StateLockDepth.Value = 0
                    _StateLock.Release()
                End If
            End Try

        End Function

        Private Async Function RunUnderStateLockAsync(Of TResult)(CancellationToken As Threading.CancellationToken,
                                                                 Body As Func(Of Task(Of TResult))) As Task(Of TResult)

            CancellationToken.ThrowIfCancellationRequested()

            Dim LockOwner = Await EnterStateLockAsync(True, CancellationToken).ConfigureAwait(False)
            If LockOwner Then _StateLockDepth.Value = 1

            Try
                Return Await Body().ConfigureAwait(False)
            Finally
                If LockOwner Then
                    _StateLockDepth.Value = 0
                    _StateLock.Release()
                End If
            End Try

        End Function

        Private NotInheritable Class StateLockScope
            Implements IDisposable
            Private _Owner As ChunkedStream
            Public Sub New(Owner As ChunkedStream)
                _Owner = Owner
            End Sub
            Public Sub Dispose() Implements IDisposable.Dispose
                Dim Owner = _Owner
                If Owner Is Nothing Then Return
                _Owner = Nothing
                Owner._StateLockDepth.Value -= 1
                If Owner._StateLockDepth.Value = 0 Then Owner._StateLock.Release()
            End Sub
        End Class

        Private _Options As ChunkedStreamOptions
        ''' <summary>
        ''' Gets or sets the options controlling newly written chunks and metadata pages.
        ''' </summary>
        ''' <remarks>
        ''' Assigning a new options instance re-evaluates the encryption configuration.
        ''' Existing chunk records are not rewritten as a side effect of changing options.
        ''' </remarks>
        Public Property Options As ChunkedStreamOptions
            Get
                Return _Options
            End Get
            Set
                If Value Is Nothing Then Throw New ArgumentNullException(NameOf(Options))
                Dim OldOptions = _Options
                If OldOptions IsNot Nothing Then
                    RemoveHandler OldOptions.EncryptionInfoChanged, AddressOf Options_EncryptionInfoChanged
                End If

                _Options = Value
                If OldOptions IsNot Nothing Then
                    Options_EncryptionInfoChanged(OldOptions.EncryptionInfo, _Options.EncryptionInfo)
                End If
                AddHandler _Options.EncryptionInfoChanged, AddressOf Options_EncryptionInfoChanged
            End Set
        End Property

        Private ReadOnly _Header As Byte()
        Private ReadOnly _Extents As List(Of ExtentIndexEntry)
        Private ReadOnly _PhysicalRecordOrdinals As New Dictionary(Of Long, Integer)
        Private ReadOnly _PhysicalRecordIdsByPage As New Dictionary(Of Integer, SortedSet(Of Long))
        Private ReadOnly _LivePhysicalRecordIdsByOffset As New SortedList(Of Long, Long)
        Private ReadOnly _PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
        Private _PhysicalDataEnd As Long = DataStartOffset
        Private _NextPhysicalRecordId As Long = 1
        Private ReadOnly _PendingReclaimedPhysicalRecords As New HashSet(Of Long)()

        Private _ChunkPlain As Byte()

        Private _CachedExtentIndex As Integer = -1
        Private _CachedChunkPlain As Byte()

        Private ReadOnly _Counter As Byte()

        '
        ' AES-CTR keystream scratch. A whole chunk's worth of successive counter blocks is
        ' built in _CtrCounterScratch and encrypted in one TransformBlock call into
        ' _CtrKeyStreamScratch, instead of one 16-byte TransformBlock per block. Both grow
        ' to the largest payload seen and are reused. Serialised by the state lock.
        '
        Private _CtrCounterScratch As Byte()
        Private _CtrKeyStreamScratch As Byte()

        Private ReadOnly _AesProvider As Aes

        '
        ' Cached AES-ECB encryptor keyed with _ChunkEncryptionKey. Rebuilt only when the
        ' chunk encryption key changes (DeriveFileMasterKeys), not per CryptPayload call.
        '
        Private _ChunkCipherTransform As ICryptoTransform

        Private ReadOnly _Rng As RandomNumberGenerator

        Private _FileMasterKey As Byte()
        Private _ChunkEncryptionKey As Byte()
        Private _ChunkMacKey As Byte()
        Private _CurrentWriteEncryptionEnabled As Boolean

        Private _HeaderFlags As HeaderFlags
        Private _HeaderSequence As Long
        Private _ActiveHeaderCopy As Integer
        Private _IndexOffset As Long
        Private _Length As Long
        Private _ChunkSize As Integer
        Private _Disposed As Boolean

        '
        ' Set when a core mutation throws after passing its argument guards, meaning its
        ' in-memory extent, physical-record and anchor state may be half-applied. Every
        ' mutating operation is refused from that point on and Dispose must not persist,
        ' so a caught-and-continued failure can never make partial state durable.
        ' Restoring a checkpoint baseline rebuilds a consistent state and clears it.
        '
        Private _Faulted As Boolean

        Private _IndexPageEntryCount As Integer
        Private _IndexDirectoryEntryCount As Integer
        Private _MetadataRootOffset As Long
        Private _MetadataRootLength As Integer

        ''' <summary>
        ''' MAC of the metadata root currently persisted at <see cref="_MetadataRootOffset"/>,
        ''' or Nothing when no root has been written. Lets a persist skip rewriting the root
        ''' when the rebuilt root is byte-for-byte identical to the one already on disk.
        ''' </summary>
        Private _MetadataRootMac As Byte()

        Private _CompactMetadataWriteOffset As Long?
        Private _CompactMetadataWriteLimit As Long?

        ''' <summary>
        ''' Internal implementation detail. Not part of the supported public API.
        ''' Tracks extent metadata pages modified in memory that must be rewritten on the
        ''' next metadata publication.
        ''' </summary>
        Public ReadOnly _DirtyExtentPages As New HashSet(Of Integer)()

        ''' <summary>
        ''' Internal implementation detail. Not part of the supported public API.
        ''' Tracks physical-record metadata pages modified in memory that must be rewritten
        ''' on the next metadata publication.
        ''' </summary>
        Public ReadOnly _DirtyPhysicalRecordPages As New HashSet(Of Integer)()

        Private ReadOnly _ExtentPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
        Private ReadOnly _ExtentDirectoryPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
        Private ReadOnly _PhysicalRecordPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
        Private ReadOnly _PhysicalRecordDirectoryPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
        Private ReadOnly _HoleDirectoryPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()

        ''' <summary>
        ''' Flags stored in an individual physical chunk record.
        ''' </summary>
        <Flags>
        Public Enum ChunkFlags As Integer

            ''' <summary>
            ''' No chunk flags are set.
            ''' </summary>
            None = 0

            ''' <summary>
            ''' The logical plaintext represented by the chunk is entirely zero bytes.
            ''' </summary>
            PlaintextAllZero = 1 << 0

            ''' <summary>
            ''' The stored compression evaluation is an estimate from a sample of the chunk
            ''' rather than a measurement of the whole plaintext (see
            ''' <see cref="ChunkedStreamOptions.CompressionEvaluationStates.Sampled" />).
            ''' <see cref="ApplyOptions" /> re-evaluates such chunks in full.
            ''' </summary>
            CompressionEstimated = 1 << 1

        End Enum

        Private Const SupportedChunkFlags As ChunkFlags = ChunkFlags.PlaintextAllZero Or ChunkFlags.CompressionEstimated

        'TODO: get rid of these.. the values would be odvious in place and not change?
        Private Const MinimumCompressionEvaluatedPercent As Integer = 0
        Private Const MaximumCompressionEvaluatedPercent As Integer = 100
        Private Const MinimumCompressionRatioThreshold As Double = 0.0R
        Private Const MaximumCompressionRatioThreshold As Double = 1.0R

        ''' <summary>
        ''' Sampled compression evaluation: bytes of leading plaintext compressed as a
        ''' representative sample.
        ''' </summary>
        Private Const CompressionSampleBytes As Integer = 8 * 1024

        ''' <summary>
        ''' Sampled compression evaluation is only used for chunks at least this large; a
        ''' smaller chunk is barely bigger than the sample, so it is always evaluated in full.
        ''' </summary>
        Private Const CompressionSampleMinimumChunkBytes As Integer = CompressionSampleBytes * 3

        ''' <summary>
        ''' Gets the logical plaintext length of the stream.
        ''' </summary>
        Public Overrides ReadOnly Property Length As Long
            Get
                Using EnterStateLock()
                    Return GetLengthCore()
                End Using
            End Get
        End Property

        Private Function GetLengthCore() As Long
            ThrowIfDisposed()
            Return _Length
        End Function

        ''' <summary>
        ''' Preferred logical segment size used when
        ''' creating new physical records.
        '''
        ''' Existing extents and physical records are not
        ''' required to match this size.
        ''' </summary>
        Public ReadOnly Property ChunkSize As Integer
            Get
                Using EnterStateLock()
                    Return GetChunkSizeCore()
                End Using
            End Get
        End Property

        Private Function GetChunkSizeCore() As Integer
            ThrowIfDisposed()
            Return _ChunkSize
        End Function

        Private Sub New(BaseStream As Stream,
                        Header As Byte(),
                        HeaderSequence As Long,
                        ActiveHeaderCopy As Integer,
                        Length As Long,
                        IndexOffset As Long,
                        Extents As List(Of ExtentIndexEntry),
                        PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry),
                        NextPhysicalRecordId As Long,
                        NextAnchorId As Long,
                        HeaderFlags As HeaderFlags,
                        Options As ChunkedStreamOptions,
                        MetadataRootOffset As Long,
                        MetadataRootLength As Integer,
                        IndexPageEntryCount As Integer,
                        IndexDirectoryEntryCount As Integer)

            Me.BaseStream = BaseStream
            _Header = Header
            _HeaderSequence = HeaderSequence
            _ActiveHeaderCopy = ActiveHeaderCopy
            _Length = Length
            _IndexOffset = IndexOffset
            _Extents = If(Extents, New List(Of ExtentIndexEntry)())
            _PhysicalRecords = If(PhysicalRecords, New Dictionary(Of Long, PhysicalRecordEntry)())
            _NextPhysicalRecordId = Math.Max(1L, NextPhysicalRecordId)
            _NextAnchorId = Math.Max(1L, NextAnchorId)
            _HeaderFlags = HeaderFlags
            _MetadataRootOffset = MetadataRootOffset
            _MetadataRootLength = MetadataRootLength

            Me.Options = If(Options, New ChunkedStreamOptions())

            If Me.Options.ChunkSize <= 0 Then
                Me.Options.ChunkSize = DefaultChunkSize
            End If

            If Me.Options.CompressionRatioThreshold < MinimumCompressionRatioThreshold Then
                Me.Options.CompressionRatioThreshold = MinimumCompressionRatioThreshold
            End If

            If Me.Options.CompressionRatioThreshold > MaximumCompressionRatioThreshold Then
                Me.Options.CompressionRatioThreshold = MaximumCompressionRatioThreshold
            End If

            If IndexPageEntryCount <= 0 Then
                IndexPageEntryCount = Me.Options.IndexPageEntryCount
            End If

            If IndexDirectoryEntryCount <= 0 Then
                IndexDirectoryEntryCount = Me.Options.IndexDirectoryEntryCount
            End If

            If IndexPageEntryCount <= 0 Then IndexPageEntryCount = 256
            If IndexDirectoryEntryCount <= 0 Then IndexDirectoryEntryCount = 256

            _IndexPageEntryCount = IndexPageEntryCount
            _IndexDirectoryEntryCount = IndexDirectoryEntryCount

            Me.Options.IndexPageEntryCount = _IndexPageEntryCount
            Me.Options.IndexDirectoryEntryCount = _IndexDirectoryEntryCount

            RebuildPhysicalRecordOrdinals()
            RebuildAnchorIndex()

            _ChunkSize = Me.Options.ChunkSize
            _ChunkPlain = New Byte(_ChunkSize - 1) {}
            _CachedChunkPlain = New Byte(_ChunkSize - 1) {}
            _Counter = New Byte(IvSize - 1) {}

            _AesProvider = Aes.Create()
            _AesProvider.Mode = CipherMode.ECB
            _AesProvider.Padding = PaddingMode.None

            _Rng = RandomNumberGenerator.Create()

        End Sub

        Private Sub InvalidateChunkCache()

            _CachedExtentIndex = -1

        End Sub

        ''' <summary>
        ''' Opens an existing ChunkedStream or creates a new one if the backing stream is empty.
        ''' </summary>
        ''' <param name="BaseStream">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(BaseStream As Stream) As ChunkedStream
            Return Open(BaseStream, Nothing)
        End Function

        ''' <summary>
        ''' Opens an existing ChunkedStream or creates a new one if the backing stream is empty.
        ''' </summary>
        ''' <param name="BaseStream">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="Options">Options controlling newly written chunks.</param>
        ''' <param name="AllowOpeningWhenRecoveryFails">
        ''' When True, the stream is opened for diagnostic access even if automatic recovery
        ''' fails or cannot run. Inspect <see cref="AutoRecoveryState" /> and
        ''' <see cref="AutoRecoveryException" /> after opening.
        ''' </param>
        ''' <param name="FlushDurableAction">
        ''' Optional durable-flush implementation for the backing stream. It is invoked
        ''' whenever ChunkedStream needs the backing stream's pending writes to reach stable
        ''' storage (recovery-journal barriers, checkpoint state, durable metadata publishes).
        ''' The implementation must not return until the write barrier is complete. When
        ''' Nothing, a FileStream backing store is flushed with Flush(True) and any other
        ''' stream falls back to Stream.Flush().
        ''' </param>
        ''' <typeparam name="T">
        ''' Concrete backing-stream type, so <paramref name="FlushDurableAction" /> receives
        ''' it without a cast.
        ''' </typeparam>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(Of T As Stream)(
                                    BaseStream As T,
                                    Optional Options As ChunkedStreamOptions = Nothing,
                                    Optional AllowOpeningWhenRecoveryFails As Boolean = False,
                                    Optional FlushDurableAction As Action(Of T) = Nothing) As ChunkedStream

            If BaseStream Is Nothing Then Throw New ArgumentNullException(NameOf(BaseStream))

            If BaseStream.CanRead = False OrElse BaseStream.CanSeek = False Then
                Throw New NotSupportedException($"Provided {NameOf(BaseStream)} must support {NameOf(BaseStream.CanRead)} and {NameOf(BaseStream.CanSeek)}.")
            End If

            Dim AdaptedFlushDurableAction As Action = Nothing
            If FlushDurableAction IsNot Nothing Then
                AdaptedFlushDurableAction = Sub() FlushDurableAction(BaseStream)
            End If

            If BaseStream.Length < DataStartOffset Then
                If BaseStream.CanWrite = False Then
                    Throw New NotSupportedException($"Provided {NameOf(BaseStream)} must support {NameOf(BaseStream.CanWrite)}.")
                End If
                Return CreateNew(BaseStream, Options, AdaptedFlushDurableAction)
            End If

            Dim EffectiveOptions = If(Options, New ChunkedStreamOptions())
            Dim Candidates = ReadHeaderCandidates(BaseStream)

            If Candidates.Count = 0 Then
                Throw New InvalidDataException("No valid chunked stream header was found.")
            End If

            Dim FirstFailure As Exception = Nothing
            Dim Result As ChunkedStream = Nothing

            For Each Candidate In Candidates

                Try
                    Result = OpenFromHeaderCandidate(BaseStream,
                                                     Candidate,
                                                     EffectiveOptions,
                                                     AdaptedFlushDurableAction)
                    Exit For

                Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse
                                           TypeOf ex Is CryptographicException OrElse
                                           TypeOf ex Is EndOfStreamException

                    '
                    ' This header copy is valid but its metadata could not be loaded - a
                    ' crash may have left a newer generation's appended root or pages
                    ' half-written. Try the next, older, fully written copy. The
                    ' append-only non-durable publish rule guarantees it was not
                    ' overwritten by the generation that failed here.
                    '
                    If FirstFailure Is Nothing Then FirstFailure = ex

                End Try

            Next

            If Result Is Nothing Then Throw FirstFailure

            RunOpenRecovery(Result, BaseStream, AllowOpeningWhenRecoveryFails)

            Return Result

        End Function

        ''' <summary>
        ''' Asynchronously opens an existing ChunkedStream or creates a new one if the
        ''' backing stream is empty.
        ''' </summary>
        ''' <param name="BaseStream">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="CancellationToken">Token observed before the open begins.</param>
        Public Shared Function OpenAsync(BaseStream As Stream,
                                         Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of ChunkedStream)

            Return OpenAsync(Of Stream)(BaseStream, Nothing, False, Nothing, CancellationToken)

        End Function

        ''' <summary>
        ''' Asynchronously opens an existing ChunkedStream or creates a new one if the
        ''' backing stream is empty.
        ''' </summary>
        ''' <remarks>
        ''' Open reads the header copies and paged metadata and then runs any pending crash
        ''' recovery - a one-time cost per stream. It is executed on a worker thread so the
        ''' awaiting caller is not blocked; the steady-state read and write path is truly
        ''' asynchronous end to end.
        ''' </remarks>
        ''' <param name="BaseStream">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="Options">Options controlling newly written chunks.</param>
        ''' <param name="AllowOpeningWhenRecoveryFails">
        ''' When True, the stream is opened for diagnostic access even if automatic recovery
        ''' fails or cannot run.
        ''' </param>
        ''' <param name="FlushDurableAction">Optional durable-flush implementation for the backing stream.</param>
        ''' <param name="CancellationToken">Token observed before the open begins.</param>
        ''' <typeparam name="T">Concrete backing-stream type.</typeparam>
        Public Shared Async Function OpenAsync(Of T As Stream)(
                                     BaseStream As T,
                                     Optional Options As ChunkedStreamOptions = Nothing,
                                     Optional AllowOpeningWhenRecoveryFails As Boolean = False,
                                     Optional FlushDurableAction As Action(Of T) = Nothing,
                                     Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of ChunkedStream)

            CancellationToken.ThrowIfCancellationRequested()

            Return Await Task.Run(
                Function() Open(BaseStream, Options, AllowOpeningWhenRecoveryFails, FlushDurableAction),
                CancellationToken).ConfigureAwait(False)

        End Function

        Private Shared Function OpenFromHeaderCandidate(BaseStream As Stream,
                                                       Candidate As HeaderCandidate,
                                                       EffectiveOptions As ChunkedStreamOptions,
                                                       AdaptedFlushDurableAction As Action) As ChunkedStream

            Dim Header = Candidate.Header
            Dim WrapMode = CType(BitConverter.ToInt32(Header, MasterKeyWrapModeOffset), MasterKeyWrapModes)

            Select Case WrapMode
                Case MasterKeyWrapModes.None, MasterKeyWrapModes.PublicWrap
                    If EffectiveOptions.EncryptionInfo IsNot Nothing Then
                        Throw New EncryptionMismatchException("Encryption information was supplied for a stream that does not require it. Open the stream without encryption information, then set Options.EncryptionInfo to enable encryption for future writes.")
                    End If

                Case MasterKeyWrapModes.UserWrap
                    If EffectiveOptions.EncryptionInfo Is Nothing Then
                        Throw New EncryptionMismatchException("Encryption information is required to open this stream.")
                    End If

                Case Else
                    Throw New InvalidDataException($"Unsupported master key wrap mode: {CInt(WrapMode)}.")
            End Select

            Dim FlagsValue = BitConverter.ToInt64(Header, FlagsOffset)
            Dim Flags = CType(FlagsValue, HeaderFlags)

            Dim SupportedFlags = [Enum].GetValues(GetType(HeaderFlags)).
                                        Cast(Of HeaderFlags)().
                                        Aggregate(ChunkedStream.HeaderFlags.None, Function(Current, Flag) Current Or Flag)

            If (Flags And Not SupportedFlags) <> HeaderFlags.None Then
                Throw New InvalidDataException($"Unsupported chunked stream flags: {CLng(Flags)}.")
            End If

            If Flags.HasFlag(HeaderFlags.VariableChunkIndex) = False Then
                Throw New InvalidDataException("Chunked stream does not contain a chunk index.")
            End If

            Dim StoredChunkSize = BitConverter.ToInt32(Header, ChunkSizeOffset)

            If StoredChunkSize <= 0 Then
                Throw New InvalidDataException($"Invalid chunked stream chunk size: {StoredChunkSize}.")
            End If

            EffectiveOptions.ChunkSize = StoredChunkSize

            Dim IndexOffset = BitConverter.ToInt64(Header, IndexOffsetOffset)
            Dim IndexCount = BitConverter.ToInt64(Header, IndexCountOffset)
            Dim FileLength = BitConverter.ToInt64(Header, LengthOffset)
            Dim MetadataRootOffset = BitConverter.ToInt64(Header, MetadataRootOffsetOffset)
            Dim MetadataRootLength = BitConverter.ToInt32(Header, MetadataRootLengthOffset)
            Dim StoredIndexPageEntryCount = BitConverter.ToInt32(Header, IndexPageEntryCountOffset)
            Dim StoredIndexDirectoryEntryCount = BitConverter.ToInt32(Header, IndexDirectoryEntryCountOffset)

            '
            ' Page geometry is authoritative - the header HMAC covers it and the metadata
            ' cannot be parsed without it, so a bad value here is unrecoverable from this
            ' header copy (the candidate fallback will try the other one).
            '
            If StoredIndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid index page entry count.")
            If StoredIndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid index directory entry count.")
            If IndexCount < 0 OrElse IndexCount > Integer.MaxValue Then Throw New InvalidDataException("Invalid chunked stream index count.")

            EffectiveOptions.IndexPageEntryCount = StoredIndexPageEntryCount
            EffectiveOptions.IndexDirectoryEntryCount = StoredIndexDirectoryEntryCount

            Dim RootMac(MacSize - 1) As Byte

            Buffer.BlockCopy(Header, IndexMacOffset, RootMac, 0, RootMac.Length)

            '
            ' The paged metadata (root + pages, each MAC-verified) is the trust root. The
            ' remaining header scalars - the index-offset allocation hint, the logical
            ' length and the extent count - are derived from it and only cached in the
            ' header, so reconcile rather than reject: a process that faulted mid-operation
            ' can leave them stale over otherwise-intact metadata.
            '
            Dim Metadata =
                ReadPagedMetadata(BaseStream,
                                  MetadataRootOffset,
                                  MetadataRootLength,
                                  RootMac,
                                  EffectiveOptions.IndexPageEntryCount,
                                  EffectiveOptions.IndexDirectoryEntryCount)

            Dim Repairs As New List(Of AutoRepair)()

            Dim ExtentSpan As Long = 0
            If Metadata.Extents.Count > 0 Then
                Dim LastExtent = Metadata.Extents(Metadata.Extents.Count - 1)
                ExtentSpan = LastExtent.LogicalOffset + CLng(LastExtent.LogicalLength)
            End If

            If IndexCount <> CLng(Metadata.Extents.Count) Then
                Repairs.Add(New AutoRepair("IndexCount", IndexCount, CLng(Metadata.Extents.Count),
                                           "did not match the loaded extent count"))
            End If

            If FileLength < 0 OrElse FileLength <> ExtentSpan Then
                Repairs.Add(New AutoRepair("Length", FileLength, ExtentSpan,
                                           "did not match the extent chain's logical span"))
                FileLength = ExtentSpan
            End If

            If IndexOffset < DataStartOffset OrElse IndexOffset > BaseStream.Length Then
                Dim Boundary = Math.Min(BaseStream.Length,
                                        Math.Max(CLng(DataStartOffset),
                                                 MaxMetadataBoundary(Metadata, MetadataRootOffset, MetadataRootLength)))
                Repairs.Add(New AutoRepair("IndexOffset", IndexOffset, Boundary,
                                           If(IndexOffset > BaseStream.Length,
                                              "was past the end of the backing stream",
                                              "was below the data start offset")))
                IndexOffset = Boundary
            End If

            Dim Result = New ChunkedStream(BaseStream,
                                           Header,
                                           Candidate.HeaderSequence,
                                           Candidate.HeaderCopyIndex,
                                           FileLength,
                                           IndexOffset,
                                           Metadata.Extents,
                                           Metadata.PhysicalRecords,
                                           Metadata.NextPhysicalRecordId,
                                           Metadata.NextAnchorId,
                                           Flags,
                                           EffectiveOptions,
                                           MetadataRootOffset,
                                           MetadataRootLength,
                                           EffectiveOptions.IndexPageEntryCount,
                                           EffectiveOptions.IndexDirectoryEntryCount)

            Result._FlushDurableAction = AdaptedFlushDurableAction

            If Repairs.Count > 0 Then Result._AutoRepairs = Repairs.AsReadOnly()

            If MetadataRootLength > 0 Then
                Result._MetadataRootMac = RootMac
            End If

            For Each pair In Metadata.ExtentPageDescriptors
                Result._ExtentPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In Metadata.ExtentDirectoryPageDescriptors
                Result._ExtentDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In Metadata.PhysicalRecordPageDescriptors
                Result._PhysicalRecordPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In Metadata.PhysicalRecordDirectoryPageDescriptors
                Result._PhysicalRecordDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In Metadata.HoleDirectoryPageDescriptors
                Result._HoleDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            If Metadata.HoleRecords IsNot Nothing Then
                Result.LoadKnownHoleRecords(Metadata.HoleRecords)
            End If

            If Not Result.TryUnwrapFileMasterKey(EffectiveOptions.EncryptionInfo) Then
                Throw New EncryptionMismatchException("The supplied encryption information could not unwrap the file master key.")
            End If

            Return Result

        End Function

        '
        ' Highest byte offset the loaded metadata actually reaches: the end of every live
        ' physical record, every metadata page and the metadata root. Used to recompute a
        ' lost index-offset hint.
        '
        Private Shared Function MaxMetadataBoundary(Metadata As MetadataReadResult,
                                                   MetadataRootOffset As Long,
                                                   MetadataRootLength As Integer) As Long

            Dim MaxEnd As Long = DataStartOffset

            If MetadataRootOffset > 0 AndAlso MetadataRootLength > 0 Then
                MaxEnd = Math.Max(MaxEnd, MetadataRootOffset + CLng(MetadataRootLength))
            End If

            For Each Descriptors In {Metadata.ExtentPageDescriptors,
                                     Metadata.ExtentDirectoryPageDescriptors,
                                     Metadata.PhysicalRecordPageDescriptors,
                                     Metadata.PhysicalRecordDirectoryPageDescriptors,
                                     Metadata.HoleDirectoryPageDescriptors}

                For Each Descriptor In Descriptors.Values
                    If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                        MaxEnd = Math.Max(MaxEnd, Descriptor.Offset + CLng(Descriptor.Length))
                    End If
                Next

            Next

            For Each Record In Metadata.PhysicalRecords.Values
                If Record.RefCount > 0 Then
                    MaxEnd = Math.Max(MaxEnd, Record.PhysicalOffset + CLng(Record.PhysicalLength))
                End If
            Next

            Return MaxEnd

        End Function

        '
        ' Runs automatic crash recovery on a freshly opened stream. This is deliberately
        ' outside the header-candidate fallback loop: a pending recovery journal marks an
        ' in-progress protected operation, so a recovery failure is a hard error and must
        ' not be masked by silently opening an older header copy.
        '
        Private Shared Sub RunOpenRecovery(Result As ChunkedStream,
                                           BaseStream As Stream,
                                           AllowOpeningWhenRecoveryFails As Boolean)

            Result._RecoveryStateAtOpen = Result.GetRecoveryState()
            If Result._RecoveryStateAtOpen = RecoveryStates.None Then Return

            If BaseStream.CanWrite Then
                Try
                    Result.RecoverState()
                    Result.RebuildPhysicalRecordOrdinals()
                    Result.RebuildAnchorIndex()
                    Result._AutoRecoveryState = AutoRecoveryStates.Repaired
                Catch ex As Exception When AllowOpeningWhenRecoveryFails
                    'we ignore errors to allow diagnostics if AllowOpeningRecoveryFails is set
                    Result._AutoRecoveryException = ex
                    Result._AutoRecoveryState = AutoRecoveryStates.Failed
                End Try
            Else
                If AllowOpeningWhenRecoveryFails Then

                Else
                    Throw New NotSupportedException(
                        $"The stream is pending recovery ({Result._RecoveryStateAtOpen}). " &
                        $"The backing stream must support write access to perform recovery, " &
                        $"or {NameOf(AllowOpeningWhenRecoveryFails)} must be set to True to allow diagnostic access.")
                End If
            End If

        End Sub

        Dim _RecoveryStateAtOpen As RecoveryStates
        ''' <summary>
        ''' Gets the recovery state that was recorded in the header when the stream was
        ''' opened, before any automatic recovery was performed.
        ''' </summary>
        Public ReadOnly Property RecoveryStateAtOpen As RecoveryStates
            Get
                Return _RecoveryStateAtOpen
            End Get
        End Property

        ''' <summary>
        ''' Outcome of the automatic recovery attempt performed while opening the stream.
        ''' </summary>
        Public Enum AutoRecoveryStates
            ''' <summary>
            ''' The stream was not pending recovery when it was opened.
            ''' </summary>
            NotRequired

            ''' <summary>
            ''' The stream is pending recovery.
            ''' </summary>
            Required

            ''' <summary>
            ''' The stream was pending recovery and recovery completed successfully.
            ''' </summary>
            Repaired

            ''' <summary>
            ''' The stream was pending recovery and recovery failed. The stream was opened
            ''' for diagnostic access only.
            ''' </summary>
            Failed
        End Enum

        Private Property _AutoRecoveryState As AutoRecoveryStates
        ''' <summary>
        ''' Gets the outcome of the automatic recovery attempt performed while the stream
        ''' was opened.
        ''' </summary>
        Public ReadOnly Property AutoRecoveryState As AutoRecoveryStates
            Get
                Return _AutoRecoveryState
            End Get
        End Property

        Private Property _AutoRecoveryException As Exception
        ''' <summary>
        ''' Gets the exception captured when automatic recovery failed and the stream was
        ''' opened for diagnostic access, or Nothing when recovery did not fail.
        ''' </summary>
        Public ReadOnly Property AutoRecoveryException As Exception
            Get
                Return _AutoRecoveryException
            End Get
        End Property

        ''' <summary>
        ''' Describes one non-authoritative header field that <see cref="Open" /> found
        ''' inconsistent and recomputed from the metadata while opening the stream.
        ''' </summary>
        Public NotInheritable Class AutoRepair
            Friend Sub New(Field As String, StoredValue As Long, CorrectedValue As Long, Reason As String)
                Me.Field = Field
                Me.StoredValue = StoredValue
                Me.CorrectedValue = CorrectedValue
                Me.Reason = Reason
            End Sub

            ''' <summary>Name of the header field that was corrected.</summary>
            Public ReadOnly Property Field As String
            ''' <summary>Value read from the header.</summary>
            Public ReadOnly Property StoredValue As Long
            ''' <summary>Value the field was set to, recomputed from the loaded metadata.</summary>
            Public ReadOnly Property CorrectedValue As Long
            ''' <summary>Why the stored value could not be trusted.</summary>
            Public ReadOnly Property Reason As String

            Public Overrides Function ToString() As String
                Return $"{Field}: stored {StoredValue}, corrected to {CorrectedValue} ({Reason})"
            End Function
        End Class

        Private _AutoRepairs As IReadOnlyList(Of AutoRepair) = Array.Empty(Of AutoRepair)()
        ''' <summary>
        ''' Non-authoritative header fields (the index-offset allocation hint, the logical
        ''' length, the extent count) that were found inconsistent and recomputed from the
        ''' metadata while opening. Empty on a clean open. The corrected values are held in
        ''' memory and written back by the next durable persist; the metadata itself was not
        ''' in question. A non-empty list means an earlier write left the header stale -
        ''' usually a process that faulted mid-operation - and is worth logging.
        ''' </summary>
        Public ReadOnly Property AutoRepairs As IReadOnlyList(Of AutoRepair)
            Get
                Return _AutoRepairs
            End Get
        End Property

        Private Shared Function CreateNew(BaseStream As Stream,
                                          Options As ChunkedStreamOptions,
                                          FlushDurableAction As Action) As ChunkedStream

            Dim Header(HeaderSize - 1) As Byte
            Dim EffectiveOptions = If(Options, New ChunkedStreamOptions())
            Dim Flags = HeaderFlags.VariableChunkIndex

            If EffectiveOptions.StoreSparseChunks = False Then
                Flags = Flags Or HeaderFlags.StoreSparseChunks
            End If

            Flags = Flags Or CompressionMethodsToHeaderFlags(EffectiveOptions.CompressionMethod)

            If EffectiveOptions.ChunkSize <= 0 Then EffectiveOptions.ChunkSize = DefaultChunkSize
            If EffectiveOptions.IndexPageEntryCount <= 0 Then EffectiveOptions.IndexPageEntryCount = 256
            If EffectiveOptions.IndexDirectoryEntryCount <= 0 Then EffectiveOptions.IndexDirectoryEntryCount = 256

            Buffer.BlockCopy(HeaderMagic, 0, Header, MagicOffset, HeaderMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(1L), 0, Header, HeaderSequenceOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(Flags)), 0, Header, FlagsOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, Header, LengthOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(EffectiveOptions.ChunkSize), 0, Header, ChunkSizeOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(DataStartOffset)), 0, Header, IndexOffsetOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, Header, IndexCountOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, Header, MetadataRootOffsetOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, Header, MetadataRootLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EffectiveOptions.IndexPageEntryCount), 0, Header, IndexPageEntryCountOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EffectiveOptions.IndexDirectoryEntryCount), 0, Header, IndexDirectoryEntryCountOffset, 4)

            RandomNumberGeneratorFill(Header, FileSaltOffset, FileSaltSize)

            Array.Clear(Header, IndexMacOffset, MacSize)

            WriteHeaderMac(Header, PublicIntegrityKey)

            BaseStream.Position = 0
            BaseStream.Write(Header, 0, Header.Length)

            BaseStream.Position = HeaderSize
            BaseStream.Write(Header, 0, Header.Length)

            BaseStream.SetLength(DataStartOffset)

            FlushDurable(BaseStream, FlushDurableAction)

            Dim Result = New ChunkedStream(BaseStream,
                                           Header,
                                           1L,
                                           0,
                                           0L,
                                           DataStartOffset,
                                           New List(Of ExtentIndexEntry)(),
                                           New Dictionary(Of Long, PhysicalRecordEntry)(),
                                           1L,
                                           1L,
                                           Flags,
                                           EffectiveOptions,
                                           0L,
                                           0,
                                           EffectiveOptions.IndexPageEntryCount,
                                           EffectiveOptions.IndexDirectoryEntryCount)

            Result._FlushDurableAction = FlushDurableAction

            If EffectiveOptions.EncryptionInfo IsNot Nothing Then
                Result.InitialiseEncryptionForNewStream(EffectiveOptions.EncryptionInfo)
            End If

            Return Result

        End Function

        '
        ' Returns every structurally valid header copy, most recent sequence first. Open
        ' tries them in order: normally the newest copy is used, but if its metadata
        ' cannot be loaded - for example a crash left a newer generation's appended root
        ' or pages half-written - Open falls back to the next copy, which the append-only
        ' non-durable publish rule guarantees is still intact.
        '
        Private Shared Function ReadHeaderCandidates(BaseStream As Stream) As List(Of HeaderCandidate)

            Dim Candidates As New List(Of HeaderCandidate)()

            For HeaderCopyIndex = 0 To HeaderCopyCount - 1

                Dim Header(HeaderSize - 1) As Byte
                Dim HeaderOffset = HeaderCopyIndex * HeaderSize

                BaseStream.Position = HeaderOffset
                ReadExactly(BaseStream, Header, 0, Header.Length)

                If Not FixedTimeEquals(HeaderMagic, 0, Header, MagicOffset, MagicSize) Then Continue For
                If Not VerifyHeaderMac(Header, PublicIntegrityKey) Then Continue For

                Candidates.Add(New HeaderCandidate With {
                    .Header = Header,
                    .HeaderSequence = BitConverter.ToInt64(Header, HeaderSequenceOffset),
                    .HeaderCopyIndex = HeaderCopyIndex
                })

            Next

            Candidates.Sort(Function(left, right) right.HeaderSequence.CompareTo(left.HeaderSequence))

            Return Candidates

        End Function

        Private Sub ClearRecoveryAreaInMemory()

            Array.Clear(_Header, RecoveryAreaOffset, RecoveryAreaLength)

        End Sub

        ''' <summary>
        ''' Returns the entire logical plaintext stream as a byte array.
        ''' </summary>
        ''' <returns>
        ''' A byte array containing every logical byte in the stream.
        ''' </returns>
        Public Function ToArray() As Byte()

            Using EnterStateLock()
                Return ToArrayCore()
            End Using

        End Function

        Private Function ToArrayCore() As Byte()


            ThrowIfDisposed()

            If _Length > Integer.MaxValue Then
                Throw New InvalidOperationException(
                    $"The logical length exceeds the maximum supported by an {NameOf(Array)}.")
            End If

            If _Length = 0 Then
                Return New Byte() {}
            End If

            Dim Result(CInt(_Length) - 1) As Byte

            ReadCore(0, Result)

            Return Result


        End Function

        ''' <summary>
        ''' Asynchronously returns the entire logical plaintext stream as a byte array.
        ''' </summary>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        Public Overloads Function ToArrayAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Byte())

            Return RunUnderStateLockAsync(CancellationToken, Function() ToArrayCoreAsync(CancellationToken))

        End Function

        Private Overloads Async Function ToArrayCoreAsync(CancellationToken As Threading.CancellationToken) As Task(Of Byte())

            ThrowIfDisposed()

            If _Length > Integer.MaxValue Then
                Throw New InvalidOperationException(
                    $"The logical length exceeds the maximum supported by an {NameOf(Array)}.")
            End If

            If _Length = 0 Then
                Return New Byte() {}
            End If

            Dim Result(CInt(_Length) - 1) As Byte

            Await ReadCoreAsync(0L, Result, 0, Nothing, CancellationToken).ConfigureAwait(False)

            Return Result

        End Function

        ''' <summary>
        ''' Returns a logical range from the stream as a byte array.
        ''' </summary>
        ''' <param name="Offset">
        ''' Logical start offset.
        ''' </param>
        ''' <param name="Length">
        ''' Number of bytes to return.
        ''' </param>
        Public Function ToArray(Offset As Long,
                                Length As Integer) As Byte()

            Using EnterStateLock()
                Return ToArrayCore(Offset, Length)
            End Using

        End Function

        Private Function ToArrayCore(Offset As Long,
                                Length As Integer) As Byte()


            ThrowIfDisposed()

            If Offset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Length < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            If Length = 0 Then
                Return New Byte() {}
            End If

            Dim Result(Length - 1) As Byte

            Dim BytesRead = ReadCore(Offset, Result)

            If BytesRead = Length Then
                Return Result
            End If

            Array.Resize(Result, BytesRead)

            Return Result


        End Function

        ''' <summary>
        ''' Asynchronously returns a logical range from the stream as a byte array.
        ''' </summary>
        ''' <param name="Offset">Logical start offset.</param>
        ''' <param name="Length">Number of bytes to return.</param>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        Public Overloads Function ToArrayAsync(Offset As Long,
                                               Length As Integer,
                                               Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Byte())

            Return RunUnderStateLockAsync(CancellationToken, Function() ToArrayCoreAsync(Offset, Length, CancellationToken))

        End Function

        Private Overloads Async Function ToArrayCoreAsync(Offset As Long,
                                                         Length As Integer,
                                                         CancellationToken As Threading.CancellationToken) As Task(Of Byte())

            ThrowIfDisposed()

            If Offset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Length < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            If Length = 0 Then
                Return New Byte() {}
            End If

            Dim Result(Length - 1) As Byte

            Dim BytesRead = Await ReadCoreAsync(Offset, Result, 0, Nothing, CancellationToken).ConfigureAwait(False)

            If BytesRead = Length Then
                Return Result
            End If

            Array.Resize(Result, BytesRead)

            Return Result

        End Function

        ''' <summary>
        ''' Reads a sequence of bytes from the logical stream at the current
        ''' <see cref="Position" /> and advances the position by the number of bytes read.
        ''' </summary>
        ''' <param name="Buffer">Destination buffer.</param>
        ''' <param name="Offset">Offset within <paramref name="Buffer" /> at which to begin storing data.</param>
        ''' <param name="Count">Maximum number of bytes to read.</param>
        ''' <returns>
        ''' The number of bytes read into <paramref name="Buffer" />. This may be less than
        ''' <paramref name="Count" />, and is zero once the end of the stream is reached.
        ''' </returns>
        Public Overrides Function Read(Buffer As Byte(),
                                       Offset As Integer,
                                       Count As Integer) As Integer

            Using EnterStateLock()
                Return ReadCore(Buffer, Offset, Count)
            End Using

        End Function

        Private Overloads Function ReadCore(Buffer As Byte(),
                                            Offset As Integer,
                                            Count As Integer) As Integer

            If Buffer Is Nothing Then
                Throw New ArgumentNullException(NameOf(Buffer))
            End If

            If Offset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Offset + Count > Buffer.Length Then
                Throw New ArgumentException("Offset and count exceed the buffer length.")
            End If


            ThrowIfDisposed()

            Dim BytesRead As Integer =
                ReadCore(_Position,
                     Buffer,
                     Offset,
                     Count)

            _Position += BytesRead

            Return BytesRead


        End Function

        ''' <summary>
        ''' Asynchronously reads a sequence of bytes from the logical stream at the current
        ''' <see cref="Position" /> and advances the position by the number of bytes read.
        ''' </summary>
        Public Overrides Function ReadAsync(Buffer As Byte(),
                                            Offset As Integer,
                                            Count As Integer,
                                            CancellationToken As Threading.CancellationToken) As Task(Of Integer)

            Return RunUnderStateLockAsync(CancellationToken, Function() ReadCoreAsync(Buffer, Offset, Count, CancellationToken))

        End Function

        Private Overloads Async Function ReadCoreAsync(Buffer As Byte(),
                                                      Offset As Integer,
                                                      Count As Integer,
                                                      CancellationToken As Threading.CancellationToken) As Task(Of Integer)

            If Buffer Is Nothing Then
                Throw New ArgumentNullException(NameOf(Buffer))
            End If

            If Offset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Offset + Count > Buffer.Length Then
                Throw New ArgumentException("Offset and count exceed the buffer length.")
            End If

            ThrowIfDisposed()

            Dim BytesRead As Integer =
                Await ReadCoreAsync(CLng(_Position),
                                    Buffer,
                                    Offset,
                                    Count,
                                    CancellationToken).ConfigureAwait(False)

            _Position += BytesRead

            Return BytesRead

        End Function

        ''' <summary>
        ''' Reads plaintext from the logical stream at the specified offset.
        ''' </summary>
        ''' <param name="LogicalOffset">
        ''' Logical stream offset to start reading from.
        ''' </param>
        ''' <param name="Output">
        ''' Destination buffer.
        ''' </param>
        ''' <param name="OutputOffset">
        ''' Offset within <paramref name="Output" /> where bytes should be written.
        ''' </param>
        ''' <param name="Count">
        ''' Maximum number of bytes to read. If Nothing, reads as many bytes as will fit from <paramref name="OutputOffset" /> to the end of <paramref name="Output" />.
        ''' </param>
        ''' <returns>
        ''' Number of bytes read into <paramref name="Output" />.
        ''' </returns>
        Public Overloads Function Read(LogicalOffset As Long,
                                       Output As Byte(),
                                       Optional OutputOffset As Integer = 0,
                                       Optional Count As Integer? = Nothing) As Integer

            Using EnterStateLock()
                Return ReadCore(LogicalOffset, Output, OutputOffset, Count)
            End Using

        End Function

        ''' <summary>
        ''' Asynchronously reads plaintext from the logical stream at the specified offset.
        ''' Does not use or modify <see cref="Position" />.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical stream offset to start reading from.</param>
        ''' <param name="Output">Destination buffer.</param>
        ''' <param name="OutputOffset">Offset within <paramref name="Output" /> where bytes should be written.</param>
        ''' <param name="Count">
        ''' Maximum number of bytes to read. If Nothing, reads as many bytes as will fit from
        ''' <paramref name="OutputOffset" /> to the end of <paramref name="Output" />.
        ''' </param>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        ''' <returns>Number of bytes read into <paramref name="Output" />.</returns>
        Public Overloads Function ReadAsync(LogicalOffset As Long,
                                            Output As Byte(),
                                            Optional OutputOffset As Integer = 0,
                                            Optional Count As Integer? = Nothing,
                                            Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Integer)

            Return RunUnderStateLockAsync(CancellationToken, Function() ReadCoreAsync(LogicalOffset, Output, OutputOffset, Count, CancellationToken))

        End Function

        Private Overloads Function ReadCore(LogicalOffset As Long,
                                       Output As Byte(),
                                       Optional OutputOffset As Integer = 0,
                                       Optional Count As Integer? = Nothing) As Integer


            ThrowIfDisposed()
            ThrowIfFaulted()

            If Output Is Nothing Then Throw New ArgumentNullException(NameOf(Output))
            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If OutputOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(OutputOffset))
            If OutputOffset > Output.Length Then Throw New ArgumentException("Output offset exceeds the output buffer length.", NameOf(OutputOffset))

            Dim EffectiveCount =
                If(Count.HasValue,
                   Count.Value,
                   Output.Length - OutputOffset)

            If EffectiveCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Count))
            If EffectiveCount > Output.Length - OutputOffset Then Throw New ArgumentException("Invalid offset/count.")

            If EffectiveCount = 0 OrElse LogicalOffset >= _Length Then
                Return 0
            End If

            Dim ToRead = CInt(Math.Min(CLng(EffectiveCount), _Length - LogicalOffset))
            Dim Remaining = ToRead
            Dim CurrentLogicalOffset = LogicalOffset
            Dim CurrentOutputOffset = OutputOffset

            While Remaining > 0

                Dim ExtentIndex = FindExtentIndex(CurrentLogicalOffset)

                If ExtentIndex < 0 Then
                    Throw New InvalidDataException($"No extent found for logical offset {CurrentLogicalOffset}.")
                End If

                Dim Extent = _Extents(ExtentIndex)
                Dim OffsetInsideExtent = CInt(CurrentLogicalOffset - Extent.LogicalOffset)
                Dim CopyLength = Math.Min(Remaining, Extent.LogicalLength - OffsetInsideExtent)

                ReadExtentBytes(Extent,
                                OffsetInsideExtent,
                                Output,
                                CurrentOutputOffset,
                                CopyLength)

                CurrentLogicalOffset += CopyLength
                CurrentOutputOffset += CopyLength
                Remaining -= CopyLength

            End While

            Return ToRead


        End Function

        '
        ' Async twin of the range-read ReadCore. Mirrors it exactly; only ReadExtentBytes
        ' becomes awaited. Assumes the state lock is held (like the synchronous Core).
        '
        Private Overloads Async Function ReadCoreAsync(LogicalOffset As Long,
                                                      Output As Byte(),
                                                      OutputOffset As Integer,
                                                      Count As Integer?,
                                                      CancellationToken As Threading.CancellationToken) As Task(Of Integer)

            ThrowIfDisposed()
            ThrowIfFaulted()

            If Output Is Nothing Then Throw New ArgumentNullException(NameOf(Output))
            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If OutputOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(OutputOffset))
            If OutputOffset > Output.Length Then Throw New ArgumentException("Output offset exceeds the output buffer length.", NameOf(OutputOffset))

            Dim EffectiveCount =
                If(Count.HasValue,
                   Count.Value,
                   Output.Length - OutputOffset)

            If EffectiveCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Count))
            If EffectiveCount > Output.Length - OutputOffset Then Throw New ArgumentException("Invalid offset/count.")

            If EffectiveCount = 0 OrElse LogicalOffset >= _Length Then
                Return 0
            End If

            Dim ToRead = CInt(Math.Min(CLng(EffectiveCount), _Length - LogicalOffset))
            Dim Remaining = ToRead
            Dim CurrentLogicalOffset = LogicalOffset
            Dim CurrentOutputOffset = OutputOffset

            While Remaining > 0

                Dim ExtentIndex = FindExtentIndex(CurrentLogicalOffset)

                If ExtentIndex < 0 Then
                    Throw New InvalidDataException($"No extent found for logical offset {CurrentLogicalOffset}.")
                End If

                Dim Extent = _Extents(ExtentIndex)
                Dim OffsetInsideExtent = CInt(CurrentLogicalOffset - Extent.LogicalOffset)
                Dim CopyLength = Math.Min(Remaining, Extent.LogicalLength - OffsetInsideExtent)

                Await ReadExtentBytesAsync(Extent,
                                           OffsetInsideExtent,
                                           Output,
                                           CurrentOutputOffset,
                                           CopyLength,
                                           CancellationToken).ConfigureAwait(False)

                CurrentLogicalOffset += CopyLength
                CurrentOutputOffset += CopyLength
                Remaining -= CopyLength

            End While

            Return ToRead

        End Function

        ''' <summary>
        ''' Writes a sequence of bytes to the logical stream at the current
        ''' <see cref="Position" /> and advances the position by the number of bytes written.
        ''' </summary>
        ''' <param name="Buffer">Source buffer.</param>
        ''' <param name="Offset">Offset within <paramref name="Buffer" /> of the first byte to write.</param>
        ''' <param name="Count">Number of bytes to write.</param>
        Public Overrides Sub Write(Buffer As Byte(),
                                   Offset As Integer,
                                   Count As Integer)

            Using EnterStateLock()
                WriteCore(Buffer, Offset, Count)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously writes a sequence of bytes to the logical stream at the current
        ''' <see cref="Position" /> and advances the position by the number of bytes written.
        ''' </summary>
        Public Overrides Function WriteAsync(Buffer As Byte(),
                                             Offset As Integer,
                                             Count As Integer,
                                             CancellationToken As Threading.CancellationToken) As Task

            Return RunUnderStateLockAsync(CancellationToken, Function() WriteCoreAsync(Buffer, Offset, Count, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Sub WriteCore(Buffer As Byte(),
                                        Offset As Integer,
                                        Count As Integer)

            WriteCoreAsync(Buffer, Offset, Count, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Overloads Async Function WriteCoreAsync(Buffer As Byte(),
                                                       Offset As Integer,
                                                       Count As Integer,
                                                       RunAsync As Boolean,
                                                       CancellationToken As Threading.CancellationToken) As Task

            If Buffer Is Nothing Then
                Throw New ArgumentNullException(NameOf(Buffer))
            End If

            If Offset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Offset + Count > Buffer.Length Then
                Throw New ArgumentException("Offset and count exceed the buffer length.")
            End If


            ThrowIfDisposed()

            Await WriteCoreAsync(CLng(_Position),
                                 Buffer,
                                 Offset,
                                 Count,
                                 RunAsync,
                                 CancellationToken).ConfigureAwait(False)

            _Position += Count


        End Function

        ''' <summary>
        ''' Writes plaintext data at the specified logical offset.
        ''' </summary>
        ''' <param name="LogicalOffset">
        ''' Logical stream offset to start writing to.
        ''' </param>
        ''' <param name="Input">
        ''' Source buffer.
        ''' </param>
        ''' <param name="DataOffset">
        ''' Offset within <paramref name="Input" /> where bytes should be read from.
        ''' </param>
        ''' <param name="Count">
        ''' Number of bytes to write. If Nothing, writes from <paramref name="DataOffset" /> to the end of <paramref name="Input" />.
        ''' </param>
        ''' <returns>
        ''' Number of bytes written.
        ''' </returns>
        Public Overloads Function Write(LogicalOffset As Long,
                                        Input As Byte(),
                                        Optional DataOffset As Integer = 0,
                                        Optional Count As Integer? = Nothing) As Integer

            Using EnterStateLock()
                Return WriteCore(LogicalOffset, Input, DataOffset, Count)
            End Using

        End Function

        ''' <summary>
        ''' Asynchronously writes plaintext data at the specified logical offset. Does not
        ''' use or modify <see cref="Position" />.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical stream offset to start writing to.</param>
        ''' <param name="Input">Source buffer.</param>
        ''' <param name="DataOffset">Offset within <paramref name="Input" /> where bytes should be read from.</param>
        ''' <param name="Count">Number of bytes to write, or Nothing to write to the end of <paramref name="Input" />.</param>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        ''' <returns>Number of bytes written.</returns>
        Public Overloads Function WriteAsync(LogicalOffset As Long,
                                             Input As Byte(),
                                             Optional DataOffset As Integer = 0,
                                             Optional Count As Integer? = Nothing,
                                             Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of Integer)

            Return RunUnderStateLockAsync(CancellationToken, Function() WriteCoreAsync(LogicalOffset, Input, DataOffset, Count, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Function WriteCore(LogicalOffset As Long,
                                             Input As Byte(),
                                             Optional DataOffset As Integer = 0,
                                             Optional Count As Integer? = Nothing) As Integer

            Return WriteCoreAsync(LogicalOffset, Input, DataOffset, Count, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Function

        Private Overloads Async Function WriteCoreAsync(LogicalOffset As Long,
                                                       Input As Byte(),
                                                       DataOffset As Integer,
                                                       Count As Integer?,
                                                       RunAsync As Boolean,
                                                       CancellationToken As Threading.CancellationToken) As Task(Of Integer)

            ThrowIfDisposed()
            ThrowIfFaulted()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If DataOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(DataOffset))
            If DataOffset > Input.Length Then Throw New ArgumentException("Data offset exceeds the input buffer length.", NameOf(DataOffset))

            Dim EffectiveCount =
                If(Count.HasValue,
                   Count.Value,
                   Input.Length - DataOffset)

            If EffectiveCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Count))
            If EffectiveCount > Input.Length - DataOffset Then Throw New ArgumentException("Invalid offset/count.")
            If EffectiveCount = 0 Then Return 0

            If LogicalOffset > Long.MaxValue - CLng(EffectiveCount) Then
                Throw New ArgumentOutOfRangeException(NameOf(Count), "The write would exceed the maximum supported logical offset.")
            End If

            Try

                InvalidateChunkCache()

                If LogicalOffset > _Length Then
                    InsertSparseRange(_Length, LogicalOffset - _Length)
                End If

                Dim ExistingLength =
                    If(LogicalOffset < _Length,
                       Math.Min(CLng(EffectiveCount), _Length - LogicalOffset),
                       0L)

                If ExistingLength > 0 Then

                    Dim ExistingCount = CInt(ExistingLength)
                    Dim ReplacementExtents = Await BuildExtentsFromBufferAsync(Input, DataOffset, ExistingCount, RunAsync, CancellationToken).ConfigureAwait(False)

                    ReplaceRangeCore(LogicalOffset, ExistingLength, ReplacementExtents)

                End If

                If ExistingLength < EffectiveCount Then

                    Dim AppendOffset = LogicalOffset + ExistingLength
                    Dim AppendDataOffset = DataOffset + CInt(ExistingLength)
                    Dim AppendCount = EffectiveCount - CInt(ExistingLength)
                    Dim AppendExtents = Await BuildExtentsFromBufferAsync(Input, AppendDataOffset, AppendCount, RunAsync, CancellationToken).ConfigureAwait(False)

                    InsertExtentsCore(AppendOffset, AppendExtents)

                End If

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

                Return EffectiveCount

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Changes the logical plaintext length of the stream.
        ''' </summary>
        Public Overrides Sub SetLength(Length As Long)

            Using EnterStateLock()
                SetLengthCore(Length)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously changes the logical plaintext length of the stream.
        ''' </summary>
        ''' <param name="Length">The new logical length.</param>
        ''' <param name="CancellationToken">Token used to cancel the operation.</param>
        Public Function SetLengthAsync(Length As Long,
                                       Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken, Function() SetLengthCoreAsync(Length, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub SetLengthCore(Length As Long)

            SetLengthCoreAsync(Length, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function SetLengthCoreAsync(Length As Long,
                                                 RunAsync As Boolean,
                                                 CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            InvalidateChunkCache()

            If Length = _Length Then Return

            Try

                If Length < _Length Then

                    Await RemoveRangeCoreAsync(Length, _Length - Length, False, RunAsync, CancellationToken).ConfigureAwait(False)

                Else

                    InsertSparseRange(_Length, Length - _Length)

                End If

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Replaces a logical range with the supplied data. The replacement length does not
        ''' need to match the length of the range being replaced.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset at which the replaced range begins.</param>
        ''' <param name="Length">Number of existing logical bytes to replace.</param>
        ''' <param name="Data">Replacement data.</param>
        ''' <param name="AnchorActionAtLogicalOffset">
        ''' Controls whether an anchor at <paramref name="LogicalOffset" /> transfers to the
        ''' replacement data.
        ''' </param>
        Public Sub Replace(LogicalOffset As Long,
                           Length As Long,
                           Data As Byte(),
                           Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                               AnchorActionsAtLogicalOffset.Use)

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            Replace(LogicalOffset,
                    Length,
                    Data,
                    0,
                    Data.Length,
                    AnchorActionAtLogicalOffset)

        End Sub

        ''' <summary>
        ''' Replaces a logical range with a region of the supplied buffer. The replacement
        ''' length does not need to match the length of the range being replaced.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset at which the replaced range begins.</param>
        ''' <param name="Length">Number of existing logical bytes to replace.</param>
        ''' <param name="Data">Buffer containing the replacement data.</param>
        ''' <param name="DataOffset">Offset within <paramref name="Data" /> of the first replacement byte.</param>
        ''' <param name="Count">Number of replacement bytes to read from <paramref name="Data" />.</param>
        ''' <param name="AnchorActionAtLogicalOffset">
        ''' Controls whether an anchor at <paramref name="LogicalOffset" /> transfers to the
        ''' replacement data.
        ''' </param>
        Public Sub Replace(LogicalOffset As Long,
                           Length As Long,
                           Data As Byte(),
                           DataOffset As Integer,
                           Count As Integer,
                           Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                               AnchorActionsAtLogicalOffset.Use)

            Using EnterStateLock()
                ReplaceCore(LogicalOffset, Length, Data, DataOffset, Count, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously replaces a logical range with the supplied data.
        ''' </summary>
        Public Overloads Function ReplaceAsync(LogicalOffset As Long,
                                               Length As Long,
                                               Data As Byte(),
                                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                                   AnchorActionsAtLogicalOffset.Use,
                                               Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            Return ReplaceAsync(LogicalOffset, Length, Data, 0, Data.Length, AnchorActionAtLogicalOffset, CancellationToken)

        End Function

        ''' <summary>
        ''' Asynchronously replaces a logical range with a region of the supplied buffer.
        ''' </summary>
        Public Overloads Function ReplaceAsync(LogicalOffset As Long,
                                               Length As Long,
                                               Data As Byte(),
                                               DataOffset As Integer,
                                               Count As Integer,
                                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                                   AnchorActionsAtLogicalOffset.Use,
                                               Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() ReplaceCoreAsync(LogicalOffset, Length, Data, DataOffset, Count, AnchorActionAtLogicalOffset, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub ReplaceCore(LogicalOffset As Long,
                           Length As Long,
                           Data As Byte(),
                           DataOffset As Integer,
                           Count As Integer,
                           Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                               AnchorActionsAtLogicalOffset.Use)

            ReplaceCoreAsync(LogicalOffset, Length, Data, DataOffset, Count, AnchorActionAtLogicalOffset,
                             RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function ReplaceCoreAsync(LogicalOffset As Long,
                                               Length As Long,
                                               Data As Byte(),
                                               DataOffset As Integer,
                                               Count As Integer,
                                               AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset,
                                               RunAsync As Boolean,
                                               CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 OrElse LogicalOffset > _Length Then
                Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            End If

            If Length < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            If Data Is Nothing Then
                Throw New ArgumentNullException(NameOf(Data))
            End If

            If DataOffset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(DataOffset))
            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If DataOffset > Data.Length OrElse Count > Data.Length - DataOffset Then
                Throw New ArgumentException("Invalid offset/count.")
            End If

            If LogicalOffset >= _Length AndAlso Length > 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            End If

            Try

                InvalidateChunkCache()

                Dim ActualLength =
                    If(LogicalOffset < _Length,
                       Math.Min(Length, _Length - LogicalOffset),
                       0L)

                '
                ' Capture the anchor at the original replacement start before removing
                ' the old range. Anchors strictly inside the removed range are destroyed
                ' by RemoveRangeCore. An anchor at the removal end survives.
                '
                Dim StartAnchorId =
                    FindAnchorIdAtLogicalOffset(LogicalOffset)

                If ActualLength > 0 Then
                    Await RemoveRangeCoreAsync(LogicalOffset,
                                               ActualLength,
                                               False,
                                               RunAsync,
                                               CancellationToken).ConfigureAwait(False)
                End If

                If Count > 0 Then

                    Dim NewExtents =
                        Await BuildExtentsFromBufferAsync(Data,
                                                          DataOffset,
                                                          Count,
                                                          RunAsync,
                                                          CancellationToken).ConfigureAwait(False)

                    If StartAnchorId > 0 AndAlso
                       AnchorActionAtLogicalOffset = AnchorActionsAtLogicalOffset.Use Then

                        Dim FirstExtent = NewExtents(0)

                        FirstExtent.AnchorId = StartAnchorId
                        NewExtents(0) = FirstExtent

                    End If

                    '
                    ' Always use TransformAway for this insertion.
                    '
                    ' The original start anchor, when retained, has already been assigned
                    ' explicitly to the first replacement extent above.
                    '
                    ' An anchor at the original removal end temporarily occupies
                    ' LogicalOffset after removal. TransformAway keeps that anchor attached
                    ' to the surviving data and pushes it after the replacement data.
                    '
                    InsertExtentsCore(
                        LogicalOffset,
                        NewExtents,
                        AnchorActionsAtLogicalOffset.TransformAway)

                End If

                RebuildAnchorIndex()

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Copies a logical range and inserts the copy at another logical offset. Bytes and
        ''' physical-record references are cloned; source anchor identities are not.
        ''' </summary>
        ''' <param name="SourceLogicalOffset">Logical offset of the data to clone.</param>
        ''' <param name="CloneLength">Number of logical bytes to clone.</param>
        ''' <param name="TargetLogicalOffset">Logical offset at which the cloned data is inserted.</param>
        ''' <param name="AnchorActionAtLogicalOffset">
        ''' Controls how an existing anchor at <paramref name="TargetLogicalOffset" /> is handled.
        ''' </param>
        Public Sub CloneInsert(SourceLogicalOffset As Long,
                               CloneLength As Long,
                               TargetLogicalOffset As Long,
                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                   AnchorActionsAtLogicalOffset.TransformAway)

            Using EnterStateLock()
                CloneInsertCore(SourceLogicalOffset, CloneLength, TargetLogicalOffset, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously copies a logical range and inserts the copy at another logical
        ''' offset. Bytes and physical-record references are cloned; source anchor identities
        ''' are not.
        ''' </summary>
        Public Function CloneInsertAsync(SourceLogicalOffset As Long,
                                         CloneLength As Long,
                                         TargetLogicalOffset As Long,
                                         Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                             AnchorActionsAtLogicalOffset.TransformAway,
                                         Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() CloneInsertCoreAsync(SourceLogicalOffset, CloneLength, TargetLogicalOffset, AnchorActionAtLogicalOffset, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub CloneInsertCore(SourceLogicalOffset As Long,
                               CloneLength As Long,
                               TargetLogicalOffset As Long,
                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                   AnchorActionsAtLogicalOffset.TransformAway)

            CloneInsertCoreAsync(SourceLogicalOffset, CloneLength, TargetLogicalOffset, AnchorActionAtLogicalOffset,
                                 RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function CloneInsertCoreAsync(SourceLogicalOffset As Long,
                                                   CloneLength As Long,
                                                   TargetLogicalOffset As Long,
                                                   AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset,
                                                   RunAsync As Boolean,
                                                   CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If SourceLogicalOffset < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(SourceLogicalOffset))
            End If

            If CloneLength < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(CloneLength))
            End If

            If TargetLogicalOffset < 0 OrElse TargetLogicalOffset > _Length Then
                Throw New ArgumentOutOfRangeException(NameOf(TargetLogicalOffset))
            End If

            If CloneLength = 0 OrElse SourceLogicalOffset >= _Length Then Return

            Try

                InvalidateChunkCache()

                Dim ActualLength =
                    Math.Min(CloneLength,
                             _Length - SourceLogicalOffset)

                Dim CloneExtents =
                    Await BuildCloneExtentsAsync(SourceLogicalOffset,
                                                 ActualLength,
                                                 RunAsync,
                                                 CancellationToken).ConfigureAwait(False)

                '
                ' Source anchor identities are never cloned.
                '
                For Index = 0 To CloneExtents.Count - 1

                    Dim Extent = CloneExtents(Index)
                    Extent.AnchorId = 0

                    CloneExtents(Index) = Extent

                Next

                InsertExtentsCore(TargetLogicalOffset,
                                  CloneExtents,
                                  AnchorActionAtLogicalOffset)

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Removes a logical range from the stream, shortening the logical length. Anchors
        ''' whose logical starts fall inside the removed range are destroyed.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset at which removal begins.</param>
        ''' <param name="Length">Number of logical bytes to remove.</param>
        Public Overloads Sub Remove(LogicalOffset As Long,
                          Length As Long)

            Using EnterStateLock()
                RemoveCore(LogicalOffset, Length)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously removes a logical range from the stream, shortening the logical
        ''' length. Anchors whose logical starts fall inside the removed range are destroyed.
        ''' </summary>
        Public Overloads Function RemoveAsync(LogicalOffset As Long,
                                              Length As Long,
                                              Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() RemoveCoreAsync(LogicalOffset, Length, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Sub RemoveCore(LogicalOffset As Long,
                          Length As Long)

            RemoveCoreAsync(LogicalOffset, Length, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Overloads Async Function RemoveCoreAsync(LogicalOffset As Long,
                                                        Length As Long,
                                                        RunAsync As Boolean,
                                                        CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 OrElse LogicalOffset >= _Length Then Return

            Try

                InvalidateChunkCache()

                Dim ActualLength = Math.Min(Length, _Length - LogicalOffset)

                Await RemoveRangeCoreAsync(LogicalOffset, ActualLength, True, RunAsync, CancellationToken).ConfigureAwait(False)

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Inserts data at the specified logical offset, shifting subsequent data forward.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset at which the data is inserted.</param>
        ''' <param name="Data">Data to insert.</param>
        ''' <param name="AnchorActionAtLogicalOffset">
        ''' Controls whether an anchor at <paramref name="LogicalOffset" /> stays with the
        ''' existing data or moves to the inserted data.
        ''' </param>
        Public Overloads Sub Insert(LogicalOffset As Long,
                                    Data As Byte(),
                                    Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            Insert(LogicalOffset,
                   Data,
                   0,
                   Data.Length,
                   AnchorActionAtLogicalOffset)

        End Sub

        ''' <summary>
        ''' Inserts a region of the supplied buffer at the specified logical offset, shifting
        ''' subsequent data forward.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset at which the data is inserted.</param>
        ''' <param name="Data">Buffer containing the data to insert.</param>
        ''' <param name="DataOffset">Offset within <paramref name="Data" /> of the first byte to insert.</param>
        ''' <param name="Count">Number of bytes to insert from <paramref name="Data" />.</param>
        ''' <param name="AnchorActionAtLogicalOffset">
        ''' Controls whether an anchor at <paramref name="LogicalOffset" /> stays with the
        ''' existing data or moves to the inserted data.
        ''' </param>
        Public Overloads Sub Insert(LogicalOffset As Long,
                                    Data As Byte(),
                                    DataOffset As Integer,
                                    Count As Integer,
                                    Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            Using EnterStateLock()
                InsertCore(LogicalOffset, Data, DataOffset, Count, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously inserts data at the specified logical offset, shifting subsequent
        ''' data forward.
        ''' </summary>
        Public Overloads Function InsertAsync(LogicalOffset As Long,
                                              Data As Byte(),
                                              Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway,
                                              Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            Return InsertAsync(LogicalOffset, Data, 0, Data.Length, AnchorActionAtLogicalOffset, CancellationToken)

        End Function

        ''' <summary>
        ''' Asynchronously inserts a region of the supplied buffer at the specified logical
        ''' offset, shifting subsequent data forward.
        ''' </summary>
        Public Overloads Function InsertAsync(LogicalOffset As Long,
                                              Data As Byte(),
                                              DataOffset As Integer,
                                              Count As Integer,
                                              Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway,
                                              Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() InsertCoreAsync(LogicalOffset, Data, DataOffset, Count, AnchorActionAtLogicalOffset, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Sub InsertCore(LogicalOffset As Long,
                                    Data As Byte(),
                                    DataOffset As Integer,
                                    Count As Integer,
                                    Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            InsertCoreAsync(LogicalOffset, Data, DataOffset, Count, AnchorActionAtLogicalOffset,
                            RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Overloads Async Function InsertCoreAsync(LogicalOffset As Long,
                                                        Data As Byte(),
                                                        DataOffset As Integer,
                                                        Count As Integer,
                                                        AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset,
                                                        RunAsync As Boolean,
                                                        CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 OrElse LogicalOffset > _Length Then
                Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            End If

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))
            If DataOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(DataOffset))
            If Count < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Count))

            If Count > Data.Length - DataOffset Then
                Throw New ArgumentException("Invalid offset/count.")
            End If

            If Count = 0 Then Return

            Try

                InvalidateChunkCache()

                Dim NewExtents = Await BuildExtentsFromBufferAsync(Data,
                                                                  DataOffset,
                                                                  Count,
                                                                  RunAsync,
                                                                  CancellationToken).ConfigureAwait(False)

                InsertExtentsCore(LogicalOffset,
                                  NewExtents,
                                  AnchorActionAtLogicalOffset)

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Copies a logical range and inserts the copy at another logical offset using the
        ''' default anchor handling. Bytes and physical-record references are cloned; anchor
        ''' identities are not.
        ''' </summary>
        ''' <param name="SourceLogicalOffset">Logical offset of the data to clone.</param>
        ''' <param name="CloneLength">Number of logical bytes to clone.</param>
        ''' <param name="TargetLogicalOffset">Logical offset at which the cloned data is inserted.</param>
        Public Sub Clone(SourceLogicalOffset As Long,
                         CloneLength As Long,
                         TargetLogicalOffset As Long)

            Using EnterStateLock()
                CloneCore(SourceLogicalOffset, CloneLength, TargetLogicalOffset)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously copies a logical range and inserts the copy at another logical
        ''' offset using the default anchor handling.
        ''' </summary>
        Public Function CloneAsync(SourceLogicalOffset As Long,
                                   CloneLength As Long,
                                   TargetLogicalOffset As Long,
                                   Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() CloneCoreAsync(SourceLogicalOffset, CloneLength, TargetLogicalOffset, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub CloneCore(SourceLogicalOffset As Long,
                         CloneLength As Long,
                         TargetLogicalOffset As Long)

            CloneCoreAsync(SourceLogicalOffset, CloneLength, TargetLogicalOffset,
                           RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function CloneCoreAsync(SourceLogicalOffset As Long,
                                             CloneLength As Long,
                                             TargetLogicalOffset As Long,
                                             RunAsync As Boolean,
                                             CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If SourceLogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(SourceLogicalOffset))
            If CloneLength < 0 Then Throw New ArgumentOutOfRangeException(NameOf(CloneLength))
            If TargetLogicalOffset < 0 OrElse TargetLogicalOffset > _Length Then Throw New ArgumentOutOfRangeException(NameOf(TargetLogicalOffset))
            If CloneLength = 0 Then Return
            If SourceLogicalOffset >= _Length Then Return

            Try

                InvalidateChunkCache()

                Dim ActualLength = Math.Min(CloneLength, _Length - SourceLogicalOffset)
                Dim CloneExtents = Await BuildCloneExtentsAsync(SourceLogicalOffset, ActualLength, RunAsync, CancellationToken).ConfigureAwait(False)

                InsertExtentsCore(TargetLogicalOffset, CloneExtents)

                If MetadataPublishSuspended = False Then
                    Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Replaces a logical range with zero bytes using sparse extents directly.
        ''' </summary>
        Public Overloads Sub Clear(LogicalOffset As Long,
                                   Count As Long)

            Using EnterStateLock()
                ClearCore(LogicalOffset, Count)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously replaces a logical range with zero bytes.
        ''' </summary>
        Public Overloads Function ClearAsync(LogicalOffset As Long,
                                             Count As Long,
                                             Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() ClearCoreAsync(LogicalOffset, Count, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Sub ClearCore(LogicalOffset As Long,
                                   Count As Long)

            ClearCoreAsync(LogicalOffset, Count, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Overloads Async Function ClearCoreAsync(LogicalOffset As Long,
                                                       Count As Long,
                                                       RunAsync As Boolean,
                                                       CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 OrElse LogicalOffset > _Length Then
                Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Count = 0 Then Return

            If LogicalOffset > Long.MaxValue - Count Then
                Throw New ArgumentOutOfRangeException(
                    NameOf(Count),
                    "The clear range would exceed the maximum supported logical offset.")
            End If

            Dim ClearEndOffset = LogicalOffset + Count

            If ClearEndOffset > _Length Then
                Throw New ArgumentOutOfRangeException(
                    NameOf(Count),
                    "The clear range extends beyond the logical stream length.")
            End If

            InvalidateChunkCache()

            If Options.StoreSparseChunks Then

                Try

                    Dim ReplacementExtents As New List(Of ExtentIndexEntry)()

                    Dim Remaining = Count

                    While Remaining > 0

                        Dim SegmentLength =
                            CInt(Math.Min(CLng(Options.ChunkSize),
                                          Remaining))

                        ReplacementExtents.Add(
                            New ExtentIndexEntry With {
                                .LogicalLength = SegmentLength,
                                .PhysicalRecordId = SparsePhysicalRecordId,
                                .PhysicalRecordOffset = 0
                            })

                        Remaining -= SegmentLength

                    End While

                    ReplaceRangeCore(LogicalOffset,
                                     Count,
                                     ReplacementExtents)

                    If MetadataPublishSuspended = False Then
                        Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                    End If

                Catch

                    _Faulted = True
                    Throw

                End Try

                Return

            End If

            '
            ' The non-sparse clear is a loop of chunk-sized writes, each of which would
            ' otherwise publish on its own. Batching them under one DeferPublish scope
            ' collapses that to a single published generation - an interruption reopens
            ' all-or-nothing rather than a partly cleared range, and a failure during the
            ' loop rolls back. When a checkpoint or an outer scope already holds the
            ' per-operation publish there is nothing to add: WriteCore still faults the
            ' stream on failure and the enclosing boundary owns the outcome.
            '
            Dim Scope As DeferPublishScope = If(MetadataPublishSuspended, Nothing, DeferPublish())

            Try

                Dim ZeroBuffer(Options.ChunkSize - 1) As Byte

                Dim Remaining = Count
                Dim CurrentOffset = LogicalOffset

                While Remaining > 0

                    Dim ThisWrite =
                        CInt(Math.Min(CLng(ZeroBuffer.Length),
                                      Remaining))

                    Await WriteCoreAsync(CurrentOffset,
                                         ZeroBuffer,
                                         0,
                                         ThisWrite,
                                         RunAsync,
                                         CancellationToken).ConfigureAwait(False)

                    CurrentOffset += ThisWrite
                    Remaining -= ThisWrite

                End While

                If Scope IsNot Nothing Then Scope.Publish()

            Finally

                If Scope IsNot Nothing Then Scope.Dispose()

            End Try


        End Function

        ''' <summary>
        ''' Inserts zero bytes at the specified logical offset using sparse extents directly.
        ''' </summary>
        Public Overloads Sub InsertNullBytes(LogicalOffset As Long,
                                             Count As Long,
                                             Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            Using EnterStateLock()
                InsertNullBytesCore(LogicalOffset, Count, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously inserts zero bytes at the specified logical offset.
        ''' </summary>
        Public Overloads Function InsertNullBytesAsync(LogicalOffset As Long,
                                                       Count As Long,
                                                       Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway,
                                                       Optional CancellationToken As Threading.CancellationToken = Nothing) As Task

            Return RunUnderStateLockAsync(CancellationToken,
                                          Function() InsertNullBytesCoreAsync(LogicalOffset, Count, AnchorActionAtLogicalOffset, RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Overloads Sub InsertNullBytesCore(LogicalOffset As Long,
                                             Count As Long,
                                             Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            InsertNullBytesCoreAsync(LogicalOffset, Count, AnchorActionAtLogicalOffset,
                                     RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Overloads Async Function InsertNullBytesCoreAsync(LogicalOffset As Long,
                                                                 Count As Long,
                                                                 AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset,
                                                                 RunAsync As Boolean,
                                                                 CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 OrElse
               LogicalOffset > _Length Then

                Throw New ArgumentOutOfRangeException(
                    NameOf(LogicalOffset))

            End If

            If Count < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Count = 0 Then Return

            If _Length > Long.MaxValue - Count Then
                Throw New ArgumentOutOfRangeException(
                    NameOf(Count),
                    "The insert would exceed the maximum supported logical length.")
            End If

            InvalidateChunkCache()

            If Options.StoreSparseChunks Then

                Try

                    InsertSparseRange(LogicalOffset,
                                      Count,
                                      AnchorActionAtLogicalOffset)

                    If MetadataPublishSuspended = False Then
                        Await PersistIndexAndHeaderAsync(_IndexOffset, False, RunAsync, CancellationToken).ConfigureAwait(False)
                    End If

                Catch

                    _Faulted = True
                    Throw

                End Try

                Return

            End If

            '
            ' The non-sparse insert is a loop of chunk-sized inserts, each of which would
            ' otherwise publish on its own. Batching them under one DeferPublish scope
            ' collapses that to a single published generation - an interruption reopens
            ' all-or-nothing rather than a partial insert, and a failure during the loop
            ' rolls back. When a checkpoint or an outer scope already holds the
            ' per-operation publish there is nothing to add: InsertCore still faults the
            ' stream on failure and the enclosing boundary owns the outcome.
            '
            Dim Scope As DeferPublishScope = If(MetadataPublishSuspended, Nothing, DeferPublish())

            Try

                Dim ZeroBuffer(Options.ChunkSize - 1) As Byte
                Dim Remaining = Count
                Dim InsertOffset = LogicalOffset
                Dim FirstInsert = True

                While Remaining > 0

                    Dim ThisInsert =
                        CInt(Math.Min(CLng(ZeroBuffer.Length),
                                      Remaining))

                    Await InsertCoreAsync(InsertOffset,
                                          ZeroBuffer,
                                          0,
                                          ThisInsert,
                                          If(FirstInsert,
                                             AnchorActionAtLogicalOffset,
                                             AnchorActionsAtLogicalOffset.TransformAway),
                                          RunAsync,
                                          CancellationToken).ConfigureAwait(False)

                    InsertOffset += ThisInsert
                    Remaining -= ThisInsert
                    FirstInsert = False

                End While

                If Scope IsNot Nothing Then Scope.Publish()

            Finally

                If Scope IsNot Nothing Then Scope.Dispose()

            End Try


        End Function

        ''' <summary>
        ''' Flushes pending changes to the backing stream.
        ''' </summary>
        Public Overrides Sub Flush()

            Using EnterStateLock()
                FlushCore()
            End Using

        End Sub

        ''' <summary>
        ''' Asynchronously flushes pending changes to the backing stream.
        ''' </summary>
        Public Overrides Function FlushAsync(CancellationToken As Threading.CancellationToken) As Task

            Return RunUnderStateLockAsync(CancellationToken, Function() FlushCoreAsync(RunAsync:=True, CancellationToken:=CancellationToken))

        End Function

        Private Sub FlushCore()

            FlushCoreAsync(RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function FlushCoreAsync(RunAsync As Boolean,
                                             CancellationToken As Threading.CancellationToken) As Task


            ThrowIfDisposed()

            '
            ' Publish metadata a DeferPublish scope is holding back, so Flush is a real
            ' durability point even mid-batch. A checkpoint's pending metadata stays with
            ' the checkpoint - Flush must not partially commit it.
            '
            If _DeferPublishDepth > 0 AndAlso
               HasOpenCheckpoint = False AndAlso
               _Faulted = False AndAlso
               BaseStream.CanWrite Then

                Await PersistIndexAndHeaderAsync(_IndexOffset, True, RunAsync, CancellationToken).ConfigureAwait(False)

                '
                ' The batch is now durable, so it becomes the rollback baseline: a later
                ' abandon of the scope reverts to this Flush, not to where the scope
                ' opened. A Flush cannot be undone.
                '
                If _DeferPublishState IsNot Nothing Then _DeferPublishState.Capture(Me)

            End If

            If BaseStream.CanWrite Then
                If RunAsync Then
                    Await BaseStream.FlushAsync(CancellationToken).ConfigureAwait(False)
                Else
                    BaseStream.Flush()
                End If
            End If


        End Function

        ''' <summary>
        ''' Releases resources owned by the ChunkedStream. The underlying stream is not disposed.
        ''' </summary>
        Protected Overrides Sub Dispose(Disposing As Boolean)

            Using EnterStateLock()
                DisposeCore(Disposing)
            End Using

        End Sub

        Private Sub DisposeCore(Disposing As Boolean)


            If _Disposed Then Return

            Try
                RemoveHandler Options.EncryptionInfoChanged, AddressOf Options_EncryptionInfoChanged

                While _CheckpointStack.Count > 0
                    CloseCheckpointCore(_CheckpointStack(_CheckpointStack.Count - 1))
                End While

                '
                ' Close any DeferPublish scope still open. A scope that was published
                ' rolls its post-publish edits, if any, into the publish below; a scope
                ' that never published is rolled back to its snapshot.
                '
                While _DeferPublishDepth > 0
                    EndDeferPublish()
                End While

                '
                ' A faulted stream may hold half-applied in-memory state and must not
                ' publish it; the last durably persisted generation stays authoritative.
                ' Closing any open checkpoint above restores a consistent baseline and
                ' clears the fault, so a checkpointed stream still persists its rollback.
                '
                If Disposing AndAlso BaseStream IsNot Nothing AndAlso BaseStream.CanWrite AndAlso _Faulted = False Then
                    PersistIndexAndHeader(_IndexOffset, True)
                    BaseStream.Flush()
                End If

            Finally

                _Disposed = True

                If _ChunkCipherTransform IsNot Nothing Then _ChunkCipherTransform.Dispose()
                _AesProvider.Dispose()
                _Rng.Dispose()

            End Try


            MyBase.Dispose(Disposing)

        End Sub

        Private Sub ReportProgress(ProgressCallback As StreamProgressCallback,
                                   ProcessedUnits As Long,
                                   TotalUnits As Long,
                                   UnitType As ProcessUnitTypes,
                                   CancellationToken As CancellationToken)

            If ProgressCallback Is Nothing Then Return

            ProgressCallback(ProcessedUnits, TotalUnits, UnitType, CancellationToken)

        End Sub

        Private Shared Function IsAllZero(Buffer As Byte(), Count As Integer) As Boolean

            For Index = 0 To Count - 1
                If Buffer(Index) <> 0 Then Return False
            Next

            Return True

        End Function

        Private Function PersistIndexAndHeaderAsync(IndexOffset As Long,
                                                   Durable As Boolean,
                                                   RunAsync As Boolean,
                                                   CancellationToken As Threading.CancellationToken) As Task

            Return PersistPagedMetadataAsync(IndexOffset, Durable, RunAsync, CancellationToken)

        End Function

        '
        ' Synchronous bridge for the metadata-publish spine. The publish logic lives in a
        ' single flag-driven body; the synchronous callers (the sync public API, Dispose,
        ' and the paths not yet threaded for async) run it with RunAsync:=False, where no
        ' await ever suspends so GetResult() completes synchronously and rethrows the
        ' original exception unwrapped.
        '
        Private Sub PersistIndexAndHeader(IndexOffset As Long,
                                          Optional Durable As Boolean = False)

            PersistIndexAndHeaderAsync(IndexOffset, Durable, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Sub UpdateHeader(Optional Durable As Boolean = False)

            UpdateHeaderAsync(Durable, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function UpdateHeaderAsync(Durable As Boolean,
                                                RunAsync As Boolean,
                                                CancellationToken As Threading.CancellationToken) As Task

            _HeaderFlags = _HeaderFlags Or HeaderFlags.VariableChunkIndex

            If Options.StoreSparseChunks = False Then
                _HeaderFlags = _HeaderFlags Or HeaderFlags.StoreSparseChunks
            End If

            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_HeaderFlags)), 0, _Header, FlagsOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_Length), 0, _Header, LengthOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_ChunkSize), 0, _Header, ChunkSizeOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexOffset), 0, _Header, IndexOffsetOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_Extents.Count)), 0, _Header, IndexCountOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_MetadataRootOffset), 0, _Header, MetadataRootOffsetOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_MetadataRootLength), 0, _Header, MetadataRootLengthOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexPageEntryCount), 0, _Header, IndexPageEntryCountOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexDirectoryEntryCount), 0, _Header, IndexDirectoryEntryCountOffset, 4)

            If _MetadataRootOffset > 0 AndAlso _MetadataRootLength > 0 Then

                Dim RootMac = _MetadataRootMac

                If RootMac Is Nothing Then

                    '
                    ' _MetadataRootMac is kept in step with _MetadataRootOffset by every root
                    ' write and by Open, so this read-back is only a fallback for the not
                    ' expected case of the cache being empty while a root exists on disk.
                    '
                    Dim Root(_MetadataRootLength - 1) As Byte

                    BaseStream.Position = _MetadataRootOffset
                    If RunAsync Then
                        Await ReadExactlyAsync(BaseStream, Root, 0, Root.Length, CancellationToken).ConfigureAwait(False)
                    Else
                        ReadExactly(BaseStream, Root, 0, Root.Length)
                    End If

                    RootMac = ComputeMac(Root, Root.Length - MacSize, PublicIntegrityKey)

                End If

                Buffer.BlockCopy(RootMac, 0, _Header, IndexMacOffset, MacSize)

            Else

                Array.Clear(_Header, IndexMacOffset, MacSize)

            End If

            Await WriteHeaderCopiesAsync(Durable, RunAsync, CancellationToken).ConfigureAwait(False)

        End Function

        Private Sub WriteHeaderCopies(Durable As Boolean)

            WriteHeaderCopiesAsync(Durable, RunAsync:=False, CancellationToken:=Nothing).GetAwaiter().GetResult()

        End Sub

        Private Async Function WriteHeaderCopiesAsync(Durable As Boolean,
                                                     RunAsync As Boolean,
                                                     CancellationToken As Threading.CancellationToken) As Task

            _HeaderSequence += 1
            System.Buffer.BlockCopy(BitConverter.GetBytes(_HeaderSequence), 0, _Header, HeaderSequenceOffset, 8)

            WriteHeaderMac(_Header, PublicIntegrityKey)

            _ActiveHeaderCopy = (_ActiveHeaderCopy + 1) Mod HeaderCopyCount

            Dim HeaderOffset = _ActiveHeaderCopy * HeaderSize

            BaseStream.Position = HeaderOffset
            If RunAsync Then
                Await BaseStream.WriteAsync(_Header, 0, _Header.Length, CancellationToken).ConfigureAwait(False)
            Else
                BaseStream.Write(_Header, 0, _Header.Length)
            End If

            '
            ' This rotation has overwritten one header slot. Any deferred span whose
            ' freeing sequence is now HeaderCopyCount publishes old therefore had its
            ' referencing slot overwritten and is safe to reallocate.
            '
            ReleaseDeferredFreeSpace()

            If Durable Then Await FlushDurableEitherAsync(RunAsync).ConfigureAwait(False)

        End Function

        Private Sub ThrowIfDisposed()

            If _Disposed Then Throw New ObjectDisposedException(GetType(ChunkedStream).FullName)

        End Sub

        Private Sub ThrowIfFaulted()

            If _Faulted Then
                Throw New InvalidOperationException(
                    "The ChunkedStream faulted when an earlier operation threw partway through, so " &
                    "its in-memory state may be inconsistent and no further changes will be persisted. " &
                    "Dispose and reopen the stream, or roll back to a checkpoint created before the failure.")
            End If

        End Sub

        Private Shared Sub RandomNumberGeneratorFill(Buffer As Byte(), Offset As Integer, Length As Integer)

            Using Rng = RandomNumberGenerator.Create()

                Dim Temp(Length - 1) As Byte
                Rng.GetBytes(Temp)
                System.Buffer.BlockCopy(Temp, 0, Buffer, Offset, Length)

            End Using

        End Sub

        Private Shared Sub ReadExactly(Source As Stream,
                                       Buffer As Byte(),
                                       Offset As Integer,
                                       Count As Integer)

            Dim TotalRead = 0

            While TotalRead < Count

                Dim ReadBytes = Source.Read(Buffer, Offset + TotalRead, Count - TotalRead)

                If ReadBytes = 0 Then Throw New EndOfStreamException("Unexpected end of chunked stream.")

                TotalRead += ReadBytes

            End While

        End Sub

        Private Shared Async Function ReadExactlyAsync(Source As Stream,
                                                      Buffer As Byte(),
                                                      Offset As Integer,
                                                      Count As Integer,
                                                      CancellationToken As Threading.CancellationToken) As Task

            Dim TotalRead = 0

            While TotalRead < Count

                Dim ReadBytes =
                    Await Source.ReadAsync(Buffer, Offset + TotalRead, Count - TotalRead, CancellationToken).ConfigureAwait(False)

                If ReadBytes = 0 Then Throw New EndOfStreamException("Unexpected end of chunked stream.")

                TotalRead += ReadBytes

            End While

        End Function

        Private Shared Function FixedTimeEquals(Left As Byte(),
                                                LeftOffset As Integer,
                                                Right As Byte(),
                                                RightOffset As Integer,
                                                Count As Integer) As Boolean

            If Left Is Nothing OrElse Right Is Nothing Then Return False
            If LeftOffset < 0 OrElse RightOffset < 0 OrElse Count < 0 Then Return False
            If Left.Length - LeftOffset < Count OrElse Right.Length - RightOffset < Count Then Return False

            Dim Diff = 0

            For Index = 0 To Count - 1
                Diff = Diff Or (CInt(Left(LeftOffset + Index)) Xor CInt(Right(RightOffset + Index)))
            Next

            Return Diff = 0

        End Function

        Private Sub FlushDurable()

            FlushDurable(BaseStream, _FlushDurableAction)

        End Sub

        Private Shared Sub FlushDurable(Target As Stream,
                                        FlushDurableAction As Action)

            If FlushDurableAction IsNot Nothing Then
                FlushDurableAction()
                Return
            End If

            Dim TargetFileStream = TryCast(Target, FileStream)
            If TargetFileStream IsNot Nothing Then
                TargetFileStream.Flush(True)
                Return
            End If

            Target.Flush()

        End Sub

        Private Function FlushDurableAsync() As Task

            Return FlushDurableAsync(BaseStream, _FlushDurableAction)

        End Function

        Private Shared Async Function FlushDurableAsync(Target As Stream,
                                                       FlushDurableAction As Action) As Task

            '
            ' The durable-flush primitives (a caller-supplied Action, or FileStream.Flush(True))
            ' are synchronous write barriers with no async form. Offload them so the awaiting
            ' caller's thread is released for the duration of the fsync.
            '
            If FlushDurableAction IsNot Nothing Then
                Await Task.Run(FlushDurableAction).ConfigureAwait(False)
                Return
            End If

            Dim TargetFileStream = TryCast(Target, FileStream)
            If TargetFileStream IsNot Nothing Then
                Await Task.Run(Sub() TargetFileStream.Flush(True)).ConfigureAwait(False)
                Return
            End If

            Await Target.FlushAsync().ConfigureAwait(False)

        End Function

    End Class

End Namespace
