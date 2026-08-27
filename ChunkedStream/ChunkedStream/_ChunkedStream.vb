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

    Public Class ChunkedStream
        Inherits Stream

        Private _Position As Long
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

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return _Fs.CanRead
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return _Fs.CanWrite
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return _Fs.CanSeek
            End Get
        End Property

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

        Public Const DefaultChunkSize As Integer = 64 * 1024
        Public Const IvSize As Integer = 16
        Public Const MacSize As Integer = 32
        Public Const HeaderSize As Integer = 512
        Public Const HeaderCopyCount As Integer = 2
        Public Const DataStartOffset As Integer = HeaderSize * HeaderCopyCount

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

        Private ReadOnly _Fs As Stream

        <Flags>
        Protected Enum PhysicalIoLockStates
            None = 0
            WriteLock = 1 << 0
            ReadLock = 1 << 1
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
                Dim PositionedStream = TryCast(_Fs, IPositionedStream)

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

        Protected Overridable Sub ReadAtCore(PhysicalOffset As Long,
                                             Buffer As Byte(),
                                             BufferOffset As Integer,
                                             Count As Integer)

            Dim PositionedStream = TryCast(_Fs, IPositionedStream)

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

            _Fs.Position = PhysicalOffset
            ReadExactly(_Fs, Buffer, BufferOffset, Count)

        End Sub

        Protected Overridable Sub WriteAtCore(PhysicalOffset As Long,
                                              Buffer As Byte(),
                                              BufferOffset As Integer,
                                              Count As Integer)

            Dim PositionedStream = TryCast(_Fs, IPositionedStream)

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

            _Fs.Position = PhysicalOffset
            _Fs.Write(Buffer, BufferOffset, Count)

        End Sub

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
        Private ReadOnly _KeyStream As Byte()

        Private ReadOnly _AesProvider As Aes
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

        Private _IndexPageEntryCount As Integer
        Private _IndexDirectoryEntryCount As Integer
        Private _MetadataRootOffset As Long
        Private _MetadataRootLength As Integer
        Private _CompactMetadataWriteOffset As Long?
        Private _CompactMetadataWriteLimit As Long?

        Public ReadOnly _DirtyExtentPages As New HashSet(Of Integer)()
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
            PlaintextAllZero = 1

        End Enum

        Private Const SupportedChunkFlags As ChunkFlags = ChunkFlags.PlaintextAllZero

        'TODO: get rid of these.. the values would be odvious in place and not change?
        Private Const MinimumCompressionEvaluatedPercent As Integer = 0
        Private Const MaximumCompressionEvaluatedPercent As Integer = 100
        Private Const MinimumCompressionRatioThreshold As Double = 0.0R
        Private Const MaximumCompressionRatioThreshold As Double = 1.0R

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

        Private Sub New(Fs As Stream,
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

            _Fs = Fs
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
            _KeyStream = New Byte(15) {}

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
        ''' <param name="Fs">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(Fs As Stream) As ChunkedStream
            Return Open(Fs, Nothing)
        End Function

        ''' <summary>
        ''' Opens an existing ChunkedStream or creates a new one if the backing stream is empty.
        ''' </summary>
        ''' <param name="Fs">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="Options">Options controlling newly written chunks.</param>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(Fs As Stream,
                                    Optional Options As ChunkedStreamOptions = Nothing,
                                    Optional AllowOpeningWhenRecoveryFails As Boolean = False) As ChunkedStream

            If Fs Is Nothing Then Throw New ArgumentNullException(NameOf(Fs))

            If Fs.CanRead = False OrElse Fs.CanSeek = False Then
                Throw New NotSupportedException($"Provided {NameOf(Fs)} must support {NameOf(Fs.CanRead)} and {NameOf(Fs.CanSeek)}.")
            End If

            If Fs.Length < DataStartOffset Then
                If Fs.CanWrite = False Then
                    Throw New NotSupportedException($"Provided {NameOf(Fs)} must support {NameOf(Fs.CanWrite)}.")
                End If
                Return CreateNew(Fs, Options)
            End If

            Dim EffectiveOptions = If(Options, New ChunkedStreamOptions())
            Dim Candidate = ReadBestHeader(Fs)

            If Candidate.Header Is Nothing Then
                Throw New InvalidDataException("No valid chunked stream header was found.")
            End If

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

            If FileLength < 0 Then Throw New InvalidDataException("Invalid chunked stream length.")
            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid chunked stream index offset.")
            If IndexOffset > Fs.Length Then Throw New InvalidDataException("Chunked stream index offset is beyond end of stream.")
            If IndexCount < 0 OrElse IndexCount > Integer.MaxValue Then Throw New InvalidDataException("Invalid chunked stream index count.")
            If StoredIndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid index page entry count.")
            If StoredIndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid index directory entry count.")

            EffectiveOptions.IndexPageEntryCount = StoredIndexPageEntryCount
            EffectiveOptions.IndexDirectoryEntryCount = StoredIndexDirectoryEntryCount

            Dim RootMac(MacSize - 1) As Byte

            Buffer.BlockCopy(Header, IndexMacOffset, RootMac, 0, RootMac.Length)

            Dim Metadata =
                ReadPagedMetadata(Fs,
                                  MetadataRootOffset,
                                  MetadataRootLength,
                                  RootMac,
                                  EffectiveOptions.IndexPageEntryCount,
                                  EffectiveOptions.IndexDirectoryEntryCount)

            If Metadata.Extents.Count <> CInt(IndexCount) Then
                Throw New InvalidDataException("Loaded extent count does not match header index count.")
            End If

            Dim Result = New ChunkedStream(Fs,
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

            Result._RecoveryStateAtOpen = Result.GetRecoveryState()
            If Result._RecoveryStateAtOpen <> RecoveryStates.None Then
                If Fs.CanWrite Then
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
            End If

            Return Result

        End Function

        Dim _RecoveryStateAtOpen As RecoveryStates
        Public ReadOnly Property RecoveryStateAtOpen As RecoveryStates
            Get
                Return _RecoveryStateAtOpen
            End Get
        End Property

        Public Enum AutoRecoveryStates
            NotRequired
            Required
            Repaired
            Failed
        End Enum

        Private Property _AutoRecoveryState As AutoRecoveryStates
        Public ReadOnly Property AutoRecoveryState As AutoRecoveryStates
            Get
                Return _AutoRecoveryState
            End Get
        End Property

        Private Property _AutoRecoveryException As Exception
        Public ReadOnly Property AutoRecoveryException As Exception
            Get
                Return _AutoRecoveryException
            End Get
        End Property

        Private Shared Function CreateNew(Fs As Stream, Options As ChunkedStreamOptions) As ChunkedStream

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

            Fs.Position = 0
            Fs.Write(Header, 0, Header.Length)

            Fs.Position = HeaderSize
            Fs.Write(Header, 0, Header.Length)

            Fs.SetLength(DataStartOffset)

            FlushDurable(Fs)

            Dim Result = New ChunkedStream(Fs,
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

            If EffectiveOptions.EncryptionInfo IsNot Nothing Then
                Result.InitialiseEncryptionForNewStream(EffectiveOptions.EncryptionInfo)
            End If

            Return Result

        End Function

        Private Shared Function ReadBestHeader(Fs As Stream) As HeaderCandidate

            Dim Best As New HeaderCandidate()

            For HeaderCopyIndex = 0 To HeaderCopyCount - 1

                Dim Header(HeaderSize - 1) As Byte
                Dim HeaderOffset = HeaderCopyIndex * HeaderSize

                Fs.Position = HeaderOffset
                ReadExactly(Fs, Header, 0, Header.Length)

                If Not FixedTimeEquals(HeaderMagic, 0, Header, MagicOffset, MagicSize) Then Continue For
                If Not VerifyHeaderMac(Header, PublicIntegrityKey) Then Continue For

                Dim HeaderSequence = BitConverter.ToInt64(Header, HeaderSequenceOffset)

                If Best.Header Is Nothing OrElse HeaderSequence > Best.HeaderSequence Then
                    Best.Header = Header
                    Best.HeaderSequence = HeaderSequence
                    Best.HeaderCopyIndex = HeaderCopyIndex
                End If

            Next

            Return Best

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

        Private Overloads Function ReadCore(LogicalOffset As Long,
                                       Output As Byte(),
                                       Optional OutputOffset As Integer = 0,
                                       Optional Count As Integer? = Nothing) As Integer


            ThrowIfDisposed()

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

        Public Overrides Sub Write(Buffer As Byte(),
                                   Offset As Integer,
                                   Count As Integer)

            Using EnterStateLock()
                WriteCore(Buffer, Offset, Count)
            End Using

        End Sub

        Private Overloads Sub WriteCore(Buffer As Byte(),
                                        Offset As Integer,
                                        Count As Integer)

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

            WriteCore(_Position,
                  Buffer,
                  Offset,
                  Count)

            _Position += Count


        End Sub

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

        Private Overloads Function WriteCore(LogicalOffset As Long,
                                             Input As Byte(),
                                             Optional DataOffset As Integer = 0,
                                             Optional Count As Integer? = Nothing) As Integer

            ThrowIfDisposed()

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
                Dim ReplacementExtents = BuildExtentsFromBuffer(Input, DataOffset, ExistingCount)

                ReplaceRangeCore(LogicalOffset, ExistingLength, ReplacementExtents)

            End If

            If ExistingLength < EffectiveCount Then

                Dim AppendOffset = LogicalOffset + ExistingLength
                Dim AppendDataOffset = DataOffset + CInt(ExistingLength)
                Dim AppendCount = EffectiveCount - CInt(ExistingLength)
                Dim AppendExtents = BuildExtentsFromBuffer(Input, AppendDataOffset, AppendCount)

                InsertExtentsCore(AppendOffset, AppendExtents)

            End If

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If

            Return EffectiveCount


        End Function

        ''' <summary>
        ''' Changes the logical plaintext length of the stream.
        ''' </summary>
        Public Overrides Sub SetLength(Length As Long)

            Using EnterStateLock()
                SetLengthCore(Length)
            End Using

        End Sub

        Private Sub SetLengthCore(Length As Long)


            ThrowIfDisposed()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            InvalidateChunkCache()

            If Length = _Length Then Return

            If Length < _Length Then

                RemoveRangeCore(Length, _Length - Length, False)

            Else

                InsertSparseRange(_Length, Length - _Length)

            End If

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

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

        Private Sub ReplaceCore(LogicalOffset As Long,
                           Length As Long,
                           Data As Byte(),
                           DataOffset As Integer,
                           Count As Integer,
                           Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                               AnchorActionsAtLogicalOffset.Use)


            ThrowIfDisposed()

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
                RemoveRangeCore(LogicalOffset,
                                ActualLength,
                                False)
            End If

            If Count > 0 Then

                Dim NewExtents =
                    BuildExtentsFromBuffer(Data,
                                           DataOffset,
                                           Count)

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

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

        Public Sub CloneInsert(SourceLogicalOffset As Long,
                               CloneLength As Long,
                               TargetLogicalOffset As Long,
                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                   AnchorActionsAtLogicalOffset.TransformAway)

            Using EnterStateLock()
                CloneInsertCore(SourceLogicalOffset, CloneLength, TargetLogicalOffset, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        Private Sub CloneInsertCore(SourceLogicalOffset As Long,
                               CloneLength As Long,
                               TargetLogicalOffset As Long,
                               Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset =
                                   AnchorActionsAtLogicalOffset.TransformAway)


            ThrowIfDisposed()

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

            InvalidateChunkCache()

            Dim ActualLength =
                Math.Min(CloneLength,
                         _Length - SourceLogicalOffset)

            Dim CloneExtents =
                BuildCloneExtents(SourceLogicalOffset,
                                  ActualLength)

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

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

        Public Overloads Sub Remove(LogicalOffset As Long,
                          Length As Long)

            Using EnterStateLock()
                RemoveCore(LogicalOffset, Length)
            End Using

        End Sub

        Private Overloads Sub RemoveCore(LogicalOffset As Long,
                          Length As Long)


            ThrowIfDisposed()

            If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 OrElse LogicalOffset >= _Length Then Return

            InvalidateChunkCache()

            Dim ActualLength = Math.Min(Length, _Length - LogicalOffset)

            RemoveRangeCore(LogicalOffset, ActualLength, True)

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

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

        Public Overloads Sub Insert(LogicalOffset As Long,
                                    Data As Byte(),
                                    DataOffset As Integer,
                                    Count As Integer,
                                    Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)

            Using EnterStateLock()
                InsertCore(LogicalOffset, Data, DataOffset, Count, AnchorActionAtLogicalOffset)
            End Using

        End Sub

        Private Overloads Sub InsertCore(LogicalOffset As Long,
                                    Data As Byte(),
                                    DataOffset As Integer,
                                    Count As Integer,
                                    Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)


            ThrowIfDisposed()

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

            InvalidateChunkCache()

            Dim NewExtents = BuildExtentsFromBuffer(Data,
                                                    DataOffset,
                                                    Count)

            InsertExtentsCore(LogicalOffset,
                              NewExtents,
                              AnchorActionAtLogicalOffset)

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

        Public Sub Clone(SourceLogicalOffset As Long,
                         CloneLength As Long,
                         TargetLogicalOffset As Long)

            Using EnterStateLock()
                CloneCore(SourceLogicalOffset, CloneLength, TargetLogicalOffset)
            End Using

        End Sub

        Private Sub CloneCore(SourceLogicalOffset As Long,
                         CloneLength As Long,
                         TargetLogicalOffset As Long)


            ThrowIfDisposed()

            If SourceLogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(SourceLogicalOffset))
            If CloneLength < 0 Then Throw New ArgumentOutOfRangeException(NameOf(CloneLength))
            If TargetLogicalOffset < 0 OrElse TargetLogicalOffset > _Length Then Throw New ArgumentOutOfRangeException(NameOf(TargetLogicalOffset))
            If CloneLength = 0 Then Return
            If SourceLogicalOffset >= _Length Then Return

            InvalidateChunkCache()

            Dim ActualLength = Math.Min(CloneLength, _Length - SourceLogicalOffset)
            Dim CloneExtents = BuildCloneExtents(SourceLogicalOffset, ActualLength)

            InsertExtentsCore(TargetLogicalOffset, CloneExtents)

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

        ''' <summary>
        ''' Replaces a logical range with zero bytes using sparse extents directly.
        ''' </summary>
        Public Overloads Sub Clear(LogicalOffset As Long,
                                   Count As Long)

            Using EnterStateLock()
                ClearCore(LogicalOffset, Count)
            End Using

        End Sub

        Private Overloads Sub ClearCore(LogicalOffset As Long,
                                   Count As Long)


            ThrowIfDisposed()

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

            If Options.StoreSparseChunks = False Then

                Dim ZeroBuffer(Options.ChunkSize - 1) As Byte

                Dim Remaining = Count
                Dim CurrentOffset = LogicalOffset

                While Remaining > 0

                    Dim ThisWrite =
                        CInt(Math.Min(CLng(ZeroBuffer.Length),
                                      Remaining))

                    WriteCore(CurrentOffset,
                          ZeroBuffer,
                          0,
                          ThisWrite)

                    CurrentOffset += ThisWrite
                    Remaining -= ThisWrite

                End While

            Else

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

            End If

            If HasOpenCheckpoint = False Then
                PersistIndexAndHeader(_IndexOffset)
            End If


        End Sub

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

        Private Overloads Sub InsertNullBytesCore(LogicalOffset As Long,
                                             Count As Long,
                                             Optional AnchorActionAtLogicalOffset As AnchorActionsAtLogicalOffset = AnchorActionsAtLogicalOffset.TransformAway)


            ThrowIfDisposed()

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

            If Options.StoreSparseChunks = False Then

                Dim ZeroBuffer(Options.ChunkSize - 1) As Byte
                Dim Remaining = Count
                Dim InsertOffset = LogicalOffset
                Dim FirstInsert = True

                While Remaining > 0

                    Dim ThisInsert =
                        CInt(Math.Min(CLng(ZeroBuffer.Length),
                                      Remaining))

                    InsertCore(InsertOffset,
                           ZeroBuffer,
                           0,
                           ThisInsert,
                           If(FirstInsert,
                              AnchorActionAtLogicalOffset,
                              AnchorActionsAtLogicalOffset.TransformAway))

                    InsertOffset += ThisInsert
                    Remaining -= ThisInsert
                    FirstInsert = False

                End While

            Else

                InsertSparseRange(LogicalOffset,
                                  Count,
                                  AnchorActionAtLogicalOffset)

                If HasOpenCheckpoint = False Then
                    PersistIndexAndHeader(_IndexOffset)
                End If

            End If


        End Sub

        ''' <summary>
        ''' Flushes pending changes to the backing stream.
        ''' </summary>
        Public Overrides Sub Flush()

            Using EnterStateLock()
                FlushCore()
            End Using

        End Sub

        Private Sub FlushCore()


            ThrowIfDisposed()

            If _Fs.CanWrite Then
                _Fs.Flush()
            End If


        End Sub

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

                If Disposing AndAlso _Fs IsNot Nothing AndAlso _Fs.CanWrite Then
                    PersistIndexAndHeader(_IndexOffset, True)
                    _Fs.Flush()
                End If

            Finally

                _Disposed = True

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

        Private Sub PersistIndexAndHeader(IndexOffset As Long,
                                          Optional Durable As Boolean = False)

            PersistPagedMetadata(IndexOffset, Durable)

        End Sub

        Private Sub UpdateHeader(Optional Durable As Boolean = False)

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

                Dim Root(_MetadataRootLength - 1) As Byte

                _Fs.Position = _MetadataRootOffset
                ReadExactly(_Fs, Root, 0, Root.Length)

                Dim RootMac = ComputeMac(Root, Root.Length - MacSize, PublicIntegrityKey)

                Buffer.BlockCopy(RootMac, 0, _Header, IndexMacOffset, MacSize)

            Else

                Array.Clear(_Header, IndexMacOffset, MacSize)

            End If

            WriteHeaderCopies(Durable)

        End Sub

        Private Sub WriteHeaderCopies(Durable As Boolean)

            _HeaderSequence += 1
            System.Buffer.BlockCopy(BitConverter.GetBytes(_HeaderSequence), 0, _Header, HeaderSequenceOffset, 8)

            WriteHeaderMac(_Header, PublicIntegrityKey)

            _ActiveHeaderCopy = (_ActiveHeaderCopy + 1) Mod HeaderCopyCount

            Dim HeaderOffset = _ActiveHeaderCopy * HeaderSize

            _Fs.Position = HeaderOffset
            _Fs.Write(_Header, 0, _Header.Length)

            If Durable Then FlushDurable(_Fs)

        End Sub

        Private Sub ThrowIfDisposed()

            If _Disposed Then Throw New ObjectDisposedException(GetType(ChunkedStream).FullName)

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

        Private Shared Sub FlushDurable(Target As Stream)

            Dim TargetFileStream = TryCast(Target, FileStream)

            If TargetFileStream IsNot Nothing Then
                TargetFileStream.Flush(True)
                Return
            End If

            Target.Flush()

        End Sub

    End Class

End Namespace
