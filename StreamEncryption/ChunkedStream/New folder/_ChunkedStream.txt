' ================================================================================
' ChunkedStream
' ================================================================================
'
' Compatibility
'   - Designed for .NET Framework 4.8+.
'   - Uses only APIs available in .NET Framework 4.8.
'   - Inherits Stream and supports standard stream operations such as Read, Write,
'     Seek, Position, Length, SetLength and Flush.
'   - The underlying stream remains owned by the caller.
'     i.e. Disposing ChunkedStream does not dispose the underlying stream.
'
' Overview
'   - Random-access authenticated chunk storage.
'   - Encryption is optional.
'   - Compression is optional and per chunk.
'   - Sparse chunk support.
'   - Configurable chunk-record placement policies
'     (Append-based storage with optional hole reuse for new chunk records).
'   - Defragmentation and recovery support.
'   - Data-only checkpoints with automatic rollback to the current checkpoint
'     baseline when disposed.
'   - Configurable fixed logical chunk size per stream.
'   - Optional most-recently-read plaintext chunk cache.
'
' Stream Model
'   - The standard Stream API reads and writes at Position.
'   - The random-access Read(LogicalOffset, Output) and Write(LogicalOffset, Input)
'     overloads do not use or modify Position.
'   - The stream is readable, writable and seekable.
'
' Chunk Size Model
'   - New streams use Options.ChunkSize.
'   - Existing streams load the stored chunk size from the header and update
'     Options.ChunkSize to match.
'   - Changing Options.ChunkSize does not immediately affect existing chunks.
'   - Defragment(DefragTypes.Rebuild) rewrites all chunks using the current
'     Options.ChunkSize.
'   - Incomplete chunk-size rebuilds are rolled back on the next open.
'
' Write Location Model
'   - Newly written physical chunk records are placed according to
'     Options.NewChunkWriteLocationPolicy.
'       - Append writes new chunk records at the current end of the chunk-data area.
'       - FillHoles attempts to place new chunk records into existing unreferenced
'         holes before extending the chunk-data area.
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
'   - Every header, index table and chunk record is authenticated.
'   - If a header, index table or unencrypted chunk is authenticated, it uses a fixed
'     public integrity key. This detects accidental corruption and bitrot, but is not
'     tamper-proof.
'   - If a chunk is encrypted, its MAC uses a key derived from the file master key.
'
' Encryption Model
'   - A random file master key is generated when encryption is first enabled.
'   - Encrypted chunks use keys derived from this file master key.
'   - The file master key is wrapped in the header.
'   - Changing the user encryption key only rewraps the file master key.
'   - Existing encrypted chunks do not need to be rewritten when the user key changes.
'   - If EncryptionInfo is set to Nothing, the file master key is publicly wrapped
'     while encrypted chunks still exist.
'   - If encryption is disabled and ApplyOptions(ApplyOptionTypes.Encryption) rewrites
'     all chunks as unencrypted, the unused file master key will be removed.
'   - New chunks are encrypted only when Options.EncryptionInfo is not Nothing.
'
' Compression Model
'   - Compression is evaluated per chunk.
'   - Each physical chunk record stores the compression method actually used.
'   - Each physical chunk record also stores the compression method last evaluated
'     and the evaluated compressed-size percentage.
'   - Compression Evaluated Percent is the compressed payload size as a percentage of
'     the original plaintext size. Lower values indicate better compression.
'   - Options.CompressionRatioThreshold controls whether evaluated compression is
'     stored. For example, 0.95 means the compressed payload must be no larger than
'     95% of the original plaintext size.
'
' Sparse Chunk Model
'   - All-zero logical chunks may be represented as sparse index entries.
'   - When sparse chunks are physically stored, the PlaintextAllZero chunk flag records
'     that the plaintext represented by the chunk is entirely zero bytes.
'   - Sparse chunks are treated as plaintext-all-zero by structure diagnostics.
'
' Checkpoint Model
'   - CreateCheckpoint() creates a data-only checkpoint.
'   - Writes and length changes inside a checkpoint are visible immediately to reads.
'   - Commit() updates the checkpoint baseline to the current stream data state and
'     keeps the checkpoint active.
'   - Rollback() restores the current checkpoint baseline and keeps the checkpoint active.
'   - Dispose restores the current checkpoint baseline and closes the checkpoint.
'   - Checkpoints may be nested, but must be committed, rolled back or disposed in
'     LIFO order.
'   - Committing an inner checkpoint only updates that inner checkpoint's baseline.
'   - A committed inner checkpoint is still part of its parent checkpoint and will be
'     rolled back if the parent checkpoint is rolled back or disposed.
'   - Only the outermost checkpoint owns header recovery state.
'   - Checkpoints roll back stream data only. Options, encryption settings and key
'     wrapping changes are not rolled back.
'   - Defragmentation is not allowed while a checkpoint is active.
'
' Recovery Model
'   - Recovery state is stored in the header recovery area.
'   - Recovery is processed automatically during Open().
'   - Chunk move recovery validates copied records before publishing recovered state.
'   - Checkpoint recovery restores the checkpoint baseline.
'   - Chunk-size rebuild recovery truncates incomplete rebuild output and reopens the
'     previously committed stream state.
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
'   | Chunk Index Table         |
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
' Header Layout (512 bytes)
'
'   Offset  Size    Description
'   0       8       Magic ("ESTRM001")
'   8       8       Header Sequence Number
'   16      8       Flags
'   24      8       Logical Plaintext File Length
'   32      16      File Salt
'   48      4       Chunk Size
'   52      8       Index Offset
'   60      8       Index Entry Count
'   68      32      Index HMAC-SHA256
'
'   100     64      Recovery State Area
'
'   164     4       Master Key Wrap Mode
'   168     16      Master Key Wrap Salt
'   184     32      Wrapped File Master Key
'   216     32      Wrapped File Master Key MAC
'
'   248     232     Reserved For Future Use
'
'   480     32      Header HMAC-SHA256
'
' Header HMAC covers bytes:
'   0..479
'
' Chunk Index Entry Layout (16 bytes)
'
'   Offset  Size    Description
'   0       8       Chunk Record Offset
'   8       4       Chunk Record Length
'   12      4       Reserved
'
' Chunk Record Layout
'
'   Offset  Size    Description
'   0       8       Chunk Index
'   8       4       Compression Method
'   12      4       Encryption Method
'   16      4       Plain Length
'   20      4       Payload Length
'   24      4       Chunk Flags
'   28      4       Compression Evaluated Method
'   32      1       Compression Evaluated Percent
'   33      15      Reserved
'
'   48      16      IV / Counter Start
'   64      N       Payload
'   64 + N  32      Chunk HMAC-SHA256
'
' Chunk HMAC covers:
'   Record header + IV + payload
'
' Compression Methods
'   0 = None
'   1 = LZ4
'   2 = Deflate
'   3 = GZip
'
' Encryption Methods
'   0 = None
'   1 = AES-CTR using file master key
'
' Chunk Flags
'   0 = None
'   1 = PlaintextAllZero
'
' Master Key Wrap Modes
'   0 = None
'   1 = PublicWrap
'   2 = UserWrap
'
' Recovery States
'   0   = None
'   1   = CopyingChunk
'   2   = ChunkCopied
'   100 = CheckpointActive
'   200 = ChunkSizeRebuildActive
'
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

Namespace Streams

    Public NotInheritable Class ChunkedStream
        Inherits Stream

        Private _Position As Long
        Public Overrides Property Position As Long
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _Position
                End SyncLock
            End Get
            Set
                SyncLock _SyncRoot
                    ThrowIfDisposed()

                    If Value < 0 Then
                        Throw New ArgumentOutOfRangeException(NameOf(Value))
                    End If

                    _Position = Value

                End SyncLock
            End Set
        End Property

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides Function Seek(Offset As Long,
                                       Origin As SeekOrigin) As Long

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Select Case Origin

                    Case SeekOrigin.Begin

                        Position = Offset

                    Case SeekOrigin.Current

                        Position = _Position + Offset

                    Case SeekOrigin.[End]

                        Position = _Length + Offset

                    Case Else

                        Throw New ArgumentOutOfRangeException(NameOf(Origin))

                End Select

                Return _Position

            End SyncLock

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

        Private Const HeaderSequenceOffset As Integer = 8
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

        Private Const IndexEntrySize As Integer = 16

        Private Const ChunkRecordHeaderSize As Integer = 48
        Private Const ChunkRecordIvOffset As Integer = ChunkRecordHeaderSize
        Private Const ChunkRecordDataOffset As Integer = ChunkRecordHeaderSize + IvSize
        Private Const MinChunkRecordSize As Integer = ChunkRecordHeaderSize + IvSize + MacSize

        Private Const ChunkCompressionMethodOffset As Integer = 8
        Private Const ChunkEncryptionMethodOffset As Integer = 12
        Private Const ChunkPlainLengthOffset As Integer = 16
        Private Const ChunkPayloadLengthOffset As Integer = 20
        Private Const ChunkFlagsOffset As Integer = 24

        Private Const ChunkCompressionEvaluatedMethodOffset As Integer = 28
        Private Const ChunkCompressionEvaluatedPercentOffset As Integer = 32

        Private Const ChunkReservedOffset As Integer = 33
        Private Const ChunkReservedSize As Integer = 15

        Private Const MetadataRootOffsetOffset As Integer = 248
        Private Const MetadataRootLengthOffset As Integer = 256
        Private Const IndexPageEntryCountOffset As Integer = 260
        Private Const IndexDirectoryEntryCountOffset As Integer = 264
        Private Const MetadataReservedOffset As Integer = 268
        Private Const MetadataReservedLength As Integer = 212

        Private Const MetadataRootHeaderSize As Integer = 40
        Private Const MetadataRootMagicSize As Integer = 8
        Private Const MetadataDescriptorSize As Integer = 48

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
            ChunkIndexPages = 1
            Holes = 2
        End Enum

        Private Enum HoleSpaceTypes As Integer
            None = 0
            ChunkRecord = 1
            IndexPage = 2
            DirectoryPage = 3
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

        Private Shared ReadOnly HeaderMagic As Byte() = Encoding.ASCII.GetBytes("ESTRM001")
        Private Shared ReadOnly PublicIntegrityKey As Byte() = Encoding.UTF8.GetBytes("ChunkedStream Public Integrity Key v1")

        <Flags>
        Friend Enum HeaderFlags As Long
            None = 0
            VariableChunkIndex = 1
            CompressionLz4 = 2
            CompressionDeflate = 4
            CompressionGZip = 8
            SparseChunks = 16
        End Enum

        Friend Structure ChunkIndexEntry
            Public Offset As Long
            Public RecordLength As Integer
        End Structure

        Private Structure HeaderCandidate
            Public Header As Byte()
            Public HeaderSequence As Long
            Public HeaderCopyIndex As Integer
        End Structure

        Private ReadOnly _Fs As Stream
        Private ReadOnly _SyncRoot As New Object()

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
        Private ReadOnly _Index As List(Of ChunkIndexEntry)

        Private _ChunkPlain As Byte()

        Private _CachedChunkIndex As Long = -1
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

        Private ReadOnly _DirtyIndexPages As New HashSet(Of Integer)()
        Private ReadOnly _IndexPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
        Private ReadOnly _ChunkIndexDirectoryPageDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()
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

        'TODO: get rid of these?
        Private Const MinimumCompressionEvaluatedPercent As Integer = 0
        Private Const MaximumCompressionEvaluatedPercent As Integer = 100
        Private Const MinimumCompressionRatioThreshold As Double = 0.0R
        Private Const MaximumCompressionRatioThreshold As Double = 1.0R

        ''' <summary>
        ''' Gets the logical plaintext length of the stream.
        ''' </summary>
        Public Overrides ReadOnly Property Length As Long
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _Length
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Logical chunk size currently used by this stream instance.
        ''' </summary>
        Public ReadOnly Property ChunkSize As Integer
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _ChunkSize
                End SyncLock
            End Get
        End Property

        Private Sub New(Fs As Stream,
                        Header As Byte(),
                        HeaderSequence As Long,
                        ActiveHeaderCopy As Integer,
                        Length As Long,
                        IndexOffset As Long,
                        Index As List(Of ChunkIndexEntry),
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
            _Index = Index
            _HeaderFlags = HeaderFlags

            _MetadataRootOffset = MetadataRootOffset
            _MetadataRootLength = MetadataRootLength

            Me.Options = If(Options, New ChunkedStreamOptions())

            If Me.Options.ChunkSize <= 0 Then Me.Options.ChunkSize = DefaultChunkSize
            If Me.Options.CompressionRatioThreshold < MinimumCompressionRatioThreshold Then Me.Options.CompressionRatioThreshold = MinimumCompressionRatioThreshold
            If Me.Options.CompressionRatioThreshold > MaximumCompressionRatioThreshold Then Me.Options.CompressionRatioThreshold = MaximumCompressionRatioThreshold

            If IndexPageEntryCount <= 0 Then IndexPageEntryCount = Me.Options.IndexPageEntryCount
            If IndexDirectoryEntryCount <= 0 Then IndexDirectoryEntryCount = Me.Options.IndexDirectoryEntryCount
            If IndexPageEntryCount <= 0 Then IndexPageEntryCount = 256
            If IndexDirectoryEntryCount <= 0 Then IndexDirectoryEntryCount = 256

            _IndexPageEntryCount = IndexPageEntryCount
            _IndexDirectoryEntryCount = IndexDirectoryEntryCount

            Me.Options.IndexPageEntryCount = _IndexPageEntryCount
            Me.Options.IndexDirectoryEntryCount = _IndexDirectoryEntryCount

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

            _CachedChunkIndex = -1

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
                                    Optional Options As ChunkedStreamOptions = Nothing) As ChunkedStream

            If Fs Is Nothing Then Throw New ArgumentNullException(NameOf(Fs))

            If Fs.Length < DataStartOffset Then
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

            Dim SupportedFlags = HeaderFlags.VariableChunkIndex Or
                                 HeaderFlags.CompressionLz4 Or
                                 HeaderFlags.CompressionDeflate Or
                                 HeaderFlags.CompressionGZip Or
                                 HeaderFlags.SparseChunks

            If (FlagsValue And Not CLng(SupportedFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported chunked stream flags: {FlagsValue}.")
            End If

            If (Flags And HeaderFlags.VariableChunkIndex) = 0 Then
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

            Dim ChunkDirectoryDescriptors As Dictionary(Of Integer, MetadataPageDescriptor) = Nothing
            Dim HoleDirectoryDescriptors As Dictionary(Of Integer, MetadataPageDescriptor) = Nothing
            Dim IndexPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor) = Nothing
            Dim HoleRecords As List(Of HoleDirectoryRecord) = Nothing

            Dim EffectiveIndexPageEntryCount = StoredIndexPageEntryCount
            Dim EffectiveIndexDirectoryEntryCount = StoredIndexDirectoryEntryCount

            Dim Index =
                ReadPagedIndexTable(Fs,
                                    MetadataRootOffset,
                                    MetadataRootLength,
                                    RootMac,
                                    EffectiveIndexPageEntryCount,
                                    EffectiveIndexDirectoryEntryCount,
                                    ChunkDirectoryDescriptors,
                                    HoleDirectoryDescriptors,
                                    IndexPageDescriptors,
                                    HoleRecords)

            If Index.Count <> CInt(IndexCount) Then
                Throw New InvalidDataException("Loaded index count does not match header index count.")
            End If

            Dim Result = New ChunkedStream(Fs,
                                           Header,
                                           Candidate.HeaderSequence,
                                           Candidate.HeaderCopyIndex,
                                           FileLength,
                                           IndexOffset,
                                           Index,
                                           Flags,
                                           EffectiveOptions,
                                           MetadataRootOffset,
                                           MetadataRootLength,
                                           EffectiveIndexPageEntryCount,
                                           EffectiveIndexDirectoryEntryCount)

            For Each pair In ChunkDirectoryDescriptors
                Result._ChunkIndexDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In HoleDirectoryDescriptors
                Result._HoleDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            For Each pair In IndexPageDescriptors
                Result._IndexPageDescriptors(pair.Key) = pair.Value
            Next

            If HoleRecords IsNot Nothing Then
                Result.LoadKnownHoleRecords(HoleRecords)
            End If

            If Not Result.TryUnwrapFileMasterKey(EffectiveOptions.EncryptionInfo) Then
                Throw New EncryptionMismatchException("The supplied encryption information could not unwrap the file master key.")
            End If

            Result.RecoverState()

            Return Result

        End Function

        Private Shared Function CreateNew(Fs As Stream, Options As ChunkedStreamOptions) As ChunkedStream
            Dim Header(HeaderSize - 1) As Byte
            Dim EffectiveOptions = If(Options, New ChunkedStreamOptions())

            Dim Flags = HeaderFlags.VariableChunkIndex

            If Not EffectiveOptions.StoreSparseChunks Then
                Flags = Flags Or HeaderFlags.SparseChunks
            End If

            Select Case EffectiveOptions.CompressionMethod
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    Flags = Flags Or HeaderFlags.CompressionLz4

                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    Flags = Flags Or HeaderFlags.CompressionDeflate

                Case ChunkedStreamOptions.CompressionMethods.GZip
                    Flags = Flags Or HeaderFlags.CompressionGZip
            End Select

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

            Dim Index As New List(Of ChunkIndexEntry)()

            Dim Result = New ChunkedStream(Fs,
                                           Header,
                                           1L,
                                           0,
                                           0L,
                                           DataStartOffset,
                                           Index,
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

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If _Length > Integer.MaxValue Then
                    Throw New InvalidOperationException(
                        "The logical length exceeds the maximum supported byte array size.")
                End If

                If _Length = 0 Then
                    Return New Byte() {}
                End If

                Dim Result(CInt(_Length) - 1) As Byte

                Read(0, Result)

                Return Result

            End SyncLock

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

            SyncLock _SyncRoot

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

                Dim BytesRead = Read(Offset, Result)

                If BytesRead = Length Then
                    Return Result
                End If

                Array.Resize(Result, BytesRead)

                Return Result

            End SyncLock

        End Function

        Public Overrides Function Read(Buffer As Byte(),
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

            If Buffer.Length - Offset < Count Then
                Throw New ArgumentException("Invalid offset/count.")
            End If

            Dim Temp(Count - 1) As Byte

            Dim BytesRead = Read(_Position, Temp)

            If BytesRead > 0 Then
                System.Buffer.BlockCopy(Temp, 0, Buffer, Offset, BytesRead)
            End If

            _Position += BytesRead

            Return BytesRead

        End Function

        ''' <summary>
        ''' Reads plaintext from the logical stream at the specified offset.
        ''' </summary>
        Public Overloads Function Read(LogicalOffset As Long, Output As Byte()) As Integer

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Output Is Nothing Then Throw New ArgumentNullException(NameOf(Output))
                If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
                If Output.Length = 0 OrElse LogicalOffset >= _Length Then Return 0

                Dim ToRead = CInt(Math.Min(CLng(Output.Length), _Length - LogicalOffset))
                Dim OutPos = 0
                Dim FirstChunk = LogicalOffset \ _ChunkSize
                Dim LastChunk = (LogicalOffset + ToRead - 1) \ _ChunkSize

                For ChunkIndex = FirstChunk To LastChunk

                    Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                    LoadChunk(ChunkIndex, _ChunkPlain)

                    Dim ChunkStart = ChunkIndex * CLng(_ChunkSize)
                    Dim SrcOffset = CInt(Math.Max(0L, LogicalOffset - ChunkStart))
                    Dim CopyLength = Math.Min(_ChunkSize - SrcOffset, ToRead - OutPos)

                    System.Buffer.BlockCopy(_ChunkPlain, SrcOffset, Output, OutPos, CopyLength)

                    OutPos += CopyLength

                Next

                Return OutPos

            End SyncLock

        End Function

        Public Overrides Sub Write(Buffer As Byte(),
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

            If Buffer.Length - Offset < Count Then
                Throw New ArgumentException("Invalid offset/count.")
            End If

            If Count = 0 Then
                Return
            End If

            Dim Temp(Count - 1) As Byte

            System.Buffer.BlockCopy(Buffer, Offset, Temp, 0, Count)

            Write(_Position, Temp)

            _Position += Count

        End Sub

        ''' <summary>
        ''' Writes plaintext data at the specified logical offset.
        ''' </summary>
        Public Overloads Function Write(LogicalOffset As Long, Input As Byte()) As Integer

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
                If LogicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalOffset))
                If Input.Length = 0 Then Return 0

                Dim EndOffset = LogicalOffset + CLng(Input.Length)
                Dim NewLogicalLength = Math.Max(_Length, EndOffset)
                Dim InPos = 0
                Dim FirstChunk = LogicalOffset \ _ChunkSize
                Dim LastChunk = (EndOffset - 1) \ _ChunkSize

                EnsureIndexSize(CInt(LastChunk + 1))

                For ChunkIndex = FirstChunk To LastChunk

                    Dim ChunkStart = ChunkIndex * CLng(_ChunkSize)
                    Dim DstOffset = CInt(Math.Max(0L, LogicalOffset - ChunkStart))
                    Dim CopyLength = Math.Min(_ChunkSize - DstOffset, Input.Length - InPos)
                    Dim LogicalPlainLength = CInt(Math.Min(CLng(_ChunkSize), Math.Max(0L, NewLogicalLength - ChunkStart)))
                    Dim IsFullLogicalChunkWrite = DstOffset = 0 AndAlso CopyLength = LogicalPlainLength

                    If IsFullLogicalChunkWrite Then
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                        System.Buffer.BlockCopy(Input, InPos, _ChunkPlain, 0, CopyLength)
                    Else
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                        LoadChunk(ChunkIndex, _ChunkPlain)
                        System.Buffer.BlockCopy(Input, InPos, _ChunkPlain, DstOffset, CopyLength)
                    End If

                    InPos += CopyLength

                    WriteChunkRecord(ChunkIndex, _ChunkPlain, LogicalPlainLength)

                Next

                _Length = NewLogicalLength

                If Not HasOpenCheckpoint Then
                    PersistIndexAndHeader(_IndexOffset)
                End If

                Return Input.Length

            End SyncLock

        End Function

        ''' <summary>
        ''' Changes the logical plaintext length of the stream.
        ''' </summary>
        Public Overrides Sub SetLength(Length As Long)
            SyncLock _SyncRoot
                ThrowIfDisposed()

                If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

                InvalidateChunkCache()
                ClearFreeSpaceMaps()

                _Length = Length

                Dim RequiredChunks = GetRequiredChunkCount(_Length)

                If _Index.Count > RequiredChunks Then
                    _Index.RemoveRange(RequiredChunks, _Index.Count - RequiredChunks)
                    MarkAllIndexPagesDirty()
                Else
                    EnsureIndexSize(RequiredChunks)
                End If

                If _Length > 0 AndAlso RequiredChunks > 0 Then
                    Dim LastChunkIndex = RequiredChunks - 1
                    Dim LastChunkStart = CLng(LastChunkIndex) * _ChunkSize
                    Dim LastChunkPlainLength = CInt(_Length - LastChunkStart)

                    If LastChunkPlainLength > 0 AndAlso LastChunkPlainLength < _ChunkSize Then
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)

                        LoadChunk(LastChunkIndex, _ChunkPlain)

                        Array.Clear(_ChunkPlain, LastChunkPlainLength, _ChunkSize - LastChunkPlainLength)

                        WriteChunkRecord(LastChunkIndex, _ChunkPlain, LastChunkPlainLength)
                    End If
                End If

                _IndexOffset = GetDataEndFromIndex()

                If Not HasOpenCheckpoint Then
                    PersistIndexAndHeader(_IndexOffset)
                End If
            End SyncLock
        End Sub
        ''' <summary>
        ''' Flushes pending changes to the backing stream.
        ''' </summary>
        Public Overrides Sub Flush()

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If _Fs.CanWrite Then
                    _Fs.Flush()
                End If

            End SyncLock

        End Sub

        ''' <summary>
        ''' Releases resources owned by the ChunkedStream. The underlying stream is not disposed.
        ''' </summary>
        Protected Overrides Sub Dispose(Disposing As Boolean)

            SyncLock _SyncRoot

                If _Disposed Then Return

                Try
                    RemoveHandler Options.EncryptionInfoChanged, AddressOf Options_EncryptionInfoChanged

                    While _CheckpointStack.Count > 0
                        CloseCheckpoint(_CheckpointStack(_CheckpointStack.Count - 1))
                    End While

                    If _Fs.CanWrite Then _Fs.Flush()

                Finally

                    _Disposed = True

                    _AesProvider.Dispose()
                    _Rng.Dispose()

                End Try

            End SyncLock

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

            If Not Options.StoreSparseChunks Then
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks
            End If

            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_HeaderFlags)), 0, _Header, FlagsOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_Length), 0, _Header, LengthOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_ChunkSize), 0, _Header, ChunkSizeOffset, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexOffset), 0, _Header, IndexOffsetOffset, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_Index.Count)), 0, _Header, IndexCountOffset, 8)
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