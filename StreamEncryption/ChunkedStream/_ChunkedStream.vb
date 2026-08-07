' ================================================================================
' ChunkedStream
' ================================================================================
'
' Compatibility
'   - Designed for .NET Framework 4.8+.
'   - Uses only APIs available in .NET Framework 4.8.
'   - The underlying stream remains owned by the caller.
'     i.e. Disposing ChunkedStream does not dispose the underlying stream.
'
' Overview
'   - Random-access authenticated chunk storage.
'   - Encryption is optional.
'   - Compression is optional and per chunk.
'   - Sparse chunk support.
'   - Append-on-write chunk updates.
'   - Defragmentation and recovery support.
'   - Data-only checkpoints with automatic rollback if not committed.
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
'   - If EncryptionInfo is set to Nothing, the file master key is publicly wrapped.
'     Existing encrypted chunks remain encrypted on disk, but can be read without a secret.
'   - New chunks are encrypted only when Options.EncryptionInfo is not Nothing.
'
' Checkpoint Model
'   - CreateCheckpoint() creates a data-only checkpoint.
'   - Writes and length changes inside a checkpoint are visible immediately to reads.
'   - If Commit() is called, changes are retained.
'   - If Commit() is not called before disposal, the checkpoint rolls back.
'   - Checkpoints may be nested, but must be committed or rolled back in LIFO order.
'   - Committing an inner checkpoint only merges it into its parent checkpoint.
'   - Only the outermost checkpoint writes the committed index/header.
'   - Rolling back an outer checkpoint also rolls back committed inner checkpoints.
'   - Checkpoints roll back stream data only. Options, encryption settings and key
'     wrapping changes are not rolled back.
'   - Defragmentation is not allowed while a checkpoint is active.
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
'   28      4       Reserved
'
'   32      16      IV / Counter Start
'   48      N       Payload
'   48 + N  32      Chunk HMAC-SHA256
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
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

Namespace Streams

    Public NotInheritable Class ChunkedStream
        Implements IDisposable

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
        ''' Options controlling newly written chunks.
        ''' Existing chunk records retain their original compression, encryption and sparse representation until rewritten.
        ''' </summary>
        Public Class ChunkedStreamOptions

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
            ''' Minimum percentage saving required before a compressed chunk is stored compressed.
            ''' </summary>
            Public Property CompressionMinimumSavingsPercent As Integer = 5

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
        ''' A data-only checkpoint for ChunkedStream.
        ''' </summary>
        ''' <remarks>
        ''' Checkpoints may be nested, but must be committed or rolled back in LIFO order.
        ''' A committed nested checkpoint is still part of its parent checkpoint and will be rolled back if the parent rolls back.
        ''' Checkpoints protect stream data only. Options, encryption information and key wrapping changes are not rolled back.
        ''' </remarks>
        Public NotInheritable Class ChunkedStreamCheckpoint
            Implements IDisposable

            Private ReadOnly _Owner As ChunkedStream
            Private _IsCommitted As Boolean
            Private _IsRolledBack As Boolean
            Private _Disposed As Boolean

            Friend ReadOnly Property State As CheckpointState

            Friend Sub New(Owner As ChunkedStream, Depth As Integer)

                If Owner Is Nothing Then Throw New ArgumentNullException(NameOf(Owner))

                _Owner = Owner
                Me.Depth = Depth

                State = New CheckpointState With {
                    .LogicalLength = Owner._Length,
                    .PhysicalLength = Owner._Fs.Length,
                    .IndexOffset = Owner._IndexOffset,
                    .HeaderFlags = Owner._HeaderFlags,
                    .Index = New List(Of ChunkIndexEntry)(Owner._Index)
                }

            End Sub

            ''' <summary>
            ''' Gets the checkpoint nesting depth.
            ''' </summary>
            Public ReadOnly Property Depth As Integer

            ''' <summary>
            ''' True while the checkpoint has not been committed, rolled back or disposed.
            ''' </summary>
            Public ReadOnly Property IsActive As Boolean
                Get
                    Return Not _IsCommitted AndAlso Not _IsRolledBack AndAlso Not _Disposed
                End Get
            End Property

            ''' <summary>
            ''' True when this checkpoint has been committed.
            ''' </summary>
            Public ReadOnly Property IsCommitted As Boolean
                Get
                    Return _IsCommitted
                End Get
            End Property

            ''' <summary>
            ''' True when this checkpoint has been rolled back.
            ''' </summary>
            Public ReadOnly Property IsRolledBack As Boolean
                Get
                    Return _IsRolledBack
                End Get
            End Property

            ''' <summary>
            ''' Commits this checkpoint.
            ''' </summary>
            ''' <param name="Durable">
            ''' If True, the outermost checkpoint commit flushes to durable storage when the backing stream supports it.
            ''' </param>
            ''' <remarks>
            ''' For nested checkpoints, Commit promotes the checkpoint into its parent but does not persist the stream.
            ''' Only the outermost checkpoint commit writes the new index and header.
            ''' </remarks>
            Public Sub Commit(Optional Durable As Boolean = True)

                If Not IsActive Then
                    Throw New InvalidOperationException("Checkpoint is no longer active.")
                End If

                _Owner.CommitCheckpoint(Me, Durable)

            End Sub

            ''' <summary>
            ''' Rolls stream data back to the checkpoint state.
            ''' </summary>
            ''' <remarks>
            ''' If this checkpoint contains committed child checkpoints, those child changes are rolled back as part of this rollback.
            ''' Options and encryption settings are not rolled back.
            ''' </remarks>
            Public Sub Rollback()

                If Not IsActive Then
                    Throw New InvalidOperationException("Checkpoint is no longer active.")
                End If

                _Owner.RollbackCheckpoint(Me)

            End Sub

            Friend Sub MarkCommitted()

                _IsCommitted = True

            End Sub

            Friend Sub MarkRolledBack()

                _IsRolledBack = True

            End Sub

            ''' <summary>
            ''' Rolls back the checkpoint if it has not already been committed or rolled back.
            ''' </summary>
            Public Sub Dispose() Implements IDisposable.Dispose

                If _Disposed Then Return

                Try
                    If IsActive Then
                        Rollback()
                    End If
                Finally
                    _Disposed = True
                End Try

            End Sub

        End Class

        Friend NotInheritable Class CheckpointState

            Public Property LogicalLength As Long

            Public Property PhysicalLength As Long

            Public Property IndexOffset As Long

            Public Property HeaderFlags As HeaderFlags

            Public Property Index As List(Of ChunkIndexEntry)

        End Class

        Public Const ChunkSize As Integer = 64 * 1024
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

        Private Const ChunkRecordHeaderSize As Integer = 32
        Private Const ChunkRecordIvOffset As Integer = ChunkRecordHeaderSize
        Private Const ChunkRecordDataOffset As Integer = ChunkRecordHeaderSize + IvSize
        Private Const MinChunkRecordSize As Integer = ChunkRecordHeaderSize + IvSize + MacSize

        Private Const ChunkCompressionMethodOffset As Integer = 8
        Private Const ChunkEncryptionMethodOffset As Integer = 12
        Private Const ChunkPlainLengthOffset As Integer = 16
        Private Const ChunkPayloadLengthOffset As Integer = 20
        Private Const ChunkFlagsOffset As Integer = 24
        Private Const ChunkReservedOffset As Integer = 28

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
        Private ReadOnly _CheckpointStack As New List(Of ChunkedStreamCheckpoint)

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

        Private ReadOnly _ChunkPlain As Byte()
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
        Private _Disposed As Boolean

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

        ''' <summary>
        ''' Gets the logical plaintext length of the stream.
        ''' </summary>
        Public ReadOnly Property Length As Long
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _Length
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' True when one or more data-only checkpoints are active.
        ''' </summary>
        Public ReadOnly Property HasActiveCheckpoint As Boolean
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return HasOpenCheckpoint
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Number of currently active nested checkpoints.
        ''' </summary>
        Public ReadOnly Property CheckpointDepth As Integer
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _CheckpointStack.Count
                End SyncLock
            End Get
        End Property

        Private ReadOnly Property HasOpenCheckpoint As Boolean
            Get
                Return _CheckpointStack.Count > 0
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
                        Options As ChunkedStreamOptions)

            _Fs = Fs
            _Header = Header
            _HeaderSequence = HeaderSequence
            _ActiveHeaderCopy = ActiveHeaderCopy
            _Length = Length
            _IndexOffset = IndexOffset
            _Index = Index
            _HeaderFlags = HeaderFlags
            Me.Options = If(Options, New ChunkedStreamOptions())

            If Me.Options.CompressionMinimumSavingsPercent < 0 Then Me.Options.CompressionMinimumSavingsPercent = 0
            If Me.Options.CompressionMinimumSavingsPercent > 100 Then Me.Options.CompressionMinimumSavingsPercent = 100

            _ChunkPlain = New Byte(ChunkSize - 1) {}
            _Counter = New Byte(IvSize - 1) {}
            _KeyStream = New Byte(15) {}

            _AesProvider = Aes.Create()
            _AesProvider.Mode = CipherMode.ECB
            _AesProvider.Padding = PaddingMode.None

            _Rng = RandomNumberGenerator.Create()

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
            Dim SupportedFlags = HeaderFlags.VariableChunkIndex Or HeaderFlags.CompressionLz4 Or HeaderFlags.CompressionDeflate Or HeaderFlags.CompressionGZip Or HeaderFlags.SparseChunks

            If (FlagsValue And Not CLng(SupportedFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported chunked stream flags: {FlagsValue}.")
            End If

            If (Flags And HeaderFlags.VariableChunkIndex) = 0 Then
                Throw New InvalidDataException("Chunked stream does not contain a chunk index.")
            End If

            Dim StoredChunkSize = BitConverter.ToInt32(Header, ChunkSizeOffset)
            If StoredChunkSize <> ChunkSize Then Throw New InvalidDataException($"Unsupported chunked stream chunk size: {StoredChunkSize}.")

            Dim IndexOffset = BitConverter.ToInt64(Header, IndexOffsetOffset)
            Dim IndexCount = BitConverter.ToInt64(Header, IndexCountOffset)
            Dim FileLength = BitConverter.ToInt64(Header, LengthOffset)

            If FileLength < 0 Then Throw New InvalidDataException("Invalid chunked stream length.")
            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid chunked stream index offset.")
            If IndexOffset > Fs.Length Then Throw New InvalidDataException("Chunked stream index offset is beyond end of stream.")
            If IndexCount < 0 OrElse IndexCount > Integer.MaxValue Then Throw New InvalidDataException("Invalid chunked stream index count.")

            Dim Index = ReadIndexTable(Fs, IndexOffset, CInt(IndexCount))
            Dim IndexMac = ComputeIndexMac(Index, PublicIntegrityKey)

            If Not FixedTimeEquals(IndexMac, 0, Header, IndexMacOffset, MacSize) Then
                Throw New CryptographicException("Chunked stream index MAC invalid.")
            End If

            Dim Result = New ChunkedStream(Fs, Header, Candidate.HeaderSequence, Candidate.HeaderCopyIndex, FileLength, IndexOffset, Index, Flags, EffectiveOptions)

            If Not Result.TryUnwrapFileMasterKey(EffectiveOptions.EncryptionInfo) Then
                Throw New EncryptionMismatchException("The supplied encryption information could not unwrap the file master key.")
            End If

            Result.RecoverState()

            Return Result

        End Function

        Private Shared Function CreateNew(Fs As Stream,
                                          Options As ChunkedStreamOptions) As ChunkedStream

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

            System.Buffer.BlockCopy(HeaderMagic, 0, Header, MagicOffset, HeaderMagic.Length)
            System.Buffer.BlockCopy(BitConverter.GetBytes(1L), 0, Header, HeaderSequenceOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CLng(Flags)), 0, Header, FlagsOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, Header, LengthOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkSize), 0, Header, ChunkSizeOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CLng(DataStartOffset)), 0, Header, IndexOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(0L), 0, Header, IndexCountOffset, 8)

            RandomNumberGeneratorFill(Header, FileSaltOffset, FileSaltSize)

            Dim Index As New List(Of ChunkIndexEntry)()
            Dim IndexMac = ComputeIndexMac(Index, PublicIntegrityKey)

            System.Buffer.BlockCopy(IndexMac, 0, Header, IndexMacOffset, MacSize)
            WriteHeaderMac(Header, PublicIntegrityKey)

            Fs.Position = 0
            Fs.Write(Header, 0, Header.Length)
            Fs.Position = HeaderSize
            Fs.Write(Header, 0, Header.Length)
            Fs.SetLength(DataStartOffset)
            FlushDurable(Fs)

            Dim Result = New ChunkedStream(Fs, Header, 1L, 0, 0L, DataStartOffset, Index, Flags, EffectiveOptions)

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

        ''' <summary>
        ''' Creates a data-only checkpoint.
        ''' </summary>
        ''' <remarks>
        ''' Writes and length changes made while the checkpoint is active are visible to reads immediately.
        ''' If Commit is called, changes are retained. If Commit is not called before disposal,
        ''' the stream rolls back to the checkpoint state.
        '''
        ''' Checkpoints may be nested, but must be committed or rolled back in LIFO order.
        ''' A committed nested checkpoint is still part of its parent checkpoint and will be rolled back
        ''' if the parent rolls back.
        '''
        ''' Checkpoints protect stream data only. Options, encryption information and file master key
        ''' wrapping changes are not rolled back.
        '''
        ''' Defragmentation is not allowed while a checkpoint is active.
        ''' </remarks>
        Public Function CreateCheckpoint() As ChunkedStreamCheckpoint

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Dim Checkpoint = New ChunkedStreamCheckpoint(Me, _CheckpointStack.Count + 1)

                _CheckpointStack.Add(Checkpoint)

                If _CheckpointStack.Count = 1 Then
                    WriteCheckpointRecoveryState()
                End If

                Return Checkpoint

            End SyncLock

        End Function

        Private Sub CommitCheckpoint(Checkpoint As ChunkedStreamCheckpoint,
                                     Durable As Boolean)

            SyncLock _SyncRoot

                ThrowIfDisposed()
                EnsureTopCheckpoint(Checkpoint)

                If _CheckpointStack.Count > 1 Then
                    _CheckpointStack.RemoveAt(_CheckpointStack.Count - 1)
                    Checkpoint.MarkCommitted()
                    Return
                End If

                Dim RecoveryArea(RecoveryAreaLength - 1) As Byte
                System.Buffer.BlockCopy(_Header, RecoveryAreaOffset, RecoveryArea, 0, RecoveryArea.Length)

                Try
                    ClearRecoveryAreaInMemory()

                    Dim CommitIndexOffset = Math.Max(_Fs.Length, GetDataEndFromIndex())
                    PersistIndexAndHeader(CommitIndexOffset, Durable)

                    _CheckpointStack.RemoveAt(_CheckpointStack.Count - 1)
                    Checkpoint.MarkCommitted()

                Catch
                    System.Buffer.BlockCopy(RecoveryArea, 0, _Header, RecoveryAreaOffset, RecoveryArea.Length)
                    Throw
                End Try

            End SyncLock

        End Sub

        Private Sub RollbackCheckpoint(Checkpoint As ChunkedStreamCheckpoint)

            SyncLock _SyncRoot

                ThrowIfDisposed()
                EnsureTopCheckpoint(Checkpoint)

                _Length = Checkpoint.State.LogicalLength
                _IndexOffset = Checkpoint.State.IndexOffset
                _HeaderFlags = Checkpoint.State.HeaderFlags

                _Index.Clear()
                _Index.AddRange(Checkpoint.State.Index)

                If _Fs.Length > Checkpoint.State.PhysicalLength Then
                    _Fs.SetLength(Checkpoint.State.PhysicalLength)
                End If

                _CheckpointStack.RemoveAt(_CheckpointStack.Count - 1)
                Checkpoint.MarkRolledBack()

                If _CheckpointStack.Count = 0 Then
                    ClearRecoveryState()
                End If

            End SyncLock

        End Sub

        Private Sub EnsureTopCheckpoint(Checkpoint As ChunkedStreamCheckpoint)

            If Checkpoint Is Nothing Then Throw New ArgumentNullException(NameOf(Checkpoint))

            If _CheckpointStack.Count = 0 OrElse Not Object.ReferenceEquals(_CheckpointStack(_CheckpointStack.Count - 1), Checkpoint) Then
                Throw New InvalidOperationException("Checkpoints must be committed or rolled back in LIFO order.")
            End If

        End Sub

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

        ''' <summary>
        ''' Reads plaintext from the logical stream at the specified offset.
        ''' </summary>
        Public Function Read(Offset As Long, Output As Byte()) As Integer

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Output Is Nothing Then Throw New ArgumentNullException(NameOf(Output))
                If Offset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Offset))
                If Output.Length = 0 OrElse Offset >= _Length Then Return 0

                Dim ToRead = CInt(Math.Min(CLng(Output.Length), _Length - Offset))
                Dim OutPos = 0
                Dim FirstChunk = Offset \ ChunkSize
                Dim LastChunk = (Offset + ToRead - 1) \ ChunkSize

                For ChunkIndex = FirstChunk To LastChunk

                    Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                    LoadChunk(ChunkIndex, _ChunkPlain)

                    Dim ChunkStart = ChunkIndex * CLng(ChunkSize)
                    Dim SrcOffset = CInt(Math.Max(0L, Offset - ChunkStart))
                    Dim CopyLength = Math.Min(ChunkSize - SrcOffset, ToRead - OutPos)

                    System.Buffer.BlockCopy(_ChunkPlain, SrcOffset, Output, OutPos, CopyLength)
                    OutPos += CopyLength

                Next

                Return OutPos

            End SyncLock

        End Function

        ''' <summary>
        ''' Writes plaintext data at the specified logical offset.
        ''' </summary>
        Public Function Write(Offset As Long, Input As Byte()) As Integer

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
                If Offset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Offset))
                If Input.Length = 0 Then Return 0

                Dim EndOffset = Offset + CLng(Input.Length)
                Dim NewLogicalLength = Math.Max(_Length, EndOffset)
                Dim InPos = 0
                Dim FirstChunk = Offset \ ChunkSize
                Dim LastChunk = (EndOffset - 1) \ ChunkSize

                EnsureIndexSize(CInt(LastChunk + 1))

                For ChunkIndex = FirstChunk To LastChunk

                    Dim ChunkStart = ChunkIndex * CLng(ChunkSize)
                    Dim DstOffset = CInt(Math.Max(0L, Offset - ChunkStart))
                    Dim CopyLength = Math.Min(ChunkSize - DstOffset, Input.Length - InPos)
                    Dim LogicalPlainLength = CInt(Math.Min(CLng(ChunkSize), Math.Max(0L, NewLogicalLength - ChunkStart)))
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
        Public Sub SetLength(Length As Long)

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

                _Length = Length

                Dim RequiredChunks = GetRequiredChunkCount(_Length)

                If _Index.Count > RequiredChunks Then
                    _Index.RemoveRange(RequiredChunks, _Index.Count - RequiredChunks)
                Else
                    EnsureIndexSize(RequiredChunks)
                End If

                If _Length > 0 AndAlso RequiredChunks > 0 Then

                    Dim LastChunkIndex = RequiredChunks - 1
                    Dim LastChunkStart = CLng(LastChunkIndex) * ChunkSize
                    Dim LastChunkPlainLength = CInt(_Length - LastChunkStart)

                    If LastChunkPlainLength > 0 AndAlso LastChunkPlainLength < ChunkSize Then
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                        LoadChunk(LastChunkIndex, _ChunkPlain)
                        Array.Clear(_ChunkPlain, LastChunkPlainLength, ChunkSize - LastChunkPlainLength)
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
        Public Sub Flush()

            SyncLock _SyncRoot
                ThrowIfDisposed()
                If _Fs.CanWrite Then _Fs.Flush()
            End SyncLock

        End Sub

        ''' <summary>
        ''' Releases resources owned by the ChunkedStream. The underlying stream is not disposed.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose

            SyncLock _SyncRoot

                If _Disposed Then Return

                Try
                    RemoveHandler Options.EncryptionInfoChanged, AddressOf Options_EncryptionInfoChanged

                    While _CheckpointStack.Count > 0
                        RollbackCheckpoint(_CheckpointStack(_CheckpointStack.Count - 1))
                    End While

                    If _Fs.CanWrite Then _Fs.Flush()

                Finally

                    _Disposed = True

                    _AesProvider.Dispose()
                    _Rng.Dispose()

                End Try

            End SyncLock

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

            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid index offset.")

            _IndexOffset = IndexOffset

            Dim NewLength = _IndexOffset + CLng(_Index.Count) * IndexEntrySize

            _Fs.Position = _IndexOffset

            Dim EntryBuffer(IndexEntrySize - 1) As Byte

            For Each Entry In _Index

                Array.Clear(EntryBuffer, 0, EntryBuffer.Length)

                System.Buffer.BlockCopy(BitConverter.GetBytes(Entry.Offset), 0, EntryBuffer, 0, 8)
                System.Buffer.BlockCopy(BitConverter.GetBytes(Entry.RecordLength), 0, EntryBuffer, 8, 4)

                _Fs.Write(EntryBuffer, 0, EntryBuffer.Length)

            Next

            If Durable Then FlushDurable(_Fs)

            UpdateHeader(Durable)

            _Fs.SetLength(NewLength)

            If Durable Then FlushDurable(_Fs)

        End Sub

        Private Sub UpdateHeader(Optional Durable As Boolean = False)

            _HeaderFlags = _HeaderFlags Or HeaderFlags.VariableChunkIndex

            If Not Options.StoreSparseChunks Then
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks
            End If

            System.Buffer.BlockCopy(BitConverter.GetBytes(CLng(_HeaderFlags)), 0, _Header, FlagsOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(_Length), 0, _Header, LengthOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkSize), 0, _Header, ChunkSizeOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(_IndexOffset), 0, _Header, IndexOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CLng(_Index.Count)), 0, _Header, IndexCountOffset, 8)

            Dim IndexMac = ComputeIndexMac(_Index, PublicIntegrityKey)
            System.Buffer.BlockCopy(IndexMac, 0, _Header, IndexMacOffset, MacSize)

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

        Private Shared Function ReadIndexTable(Fs As Stream,
                                               IndexOffset As Long,
                                               IndexCount As Integer) As List(Of ChunkIndexEntry)

            Dim Index As New List(Of ChunkIndexEntry)(IndexCount)

            If IndexCount = 0 Then Return Index

            Dim IndexBytesLength = CLng(IndexCount) * IndexEntrySize

            If IndexOffset + IndexBytesLength > Fs.Length Then
                Throw New InvalidDataException("Chunked stream index table extends beyond end of stream.")
            End If

            Fs.Position = IndexOffset

            Dim EntryBuffer(IndexEntrySize - 1) As Byte

            For EntryIndex = 0 To IndexCount - 1

                ReadExactly(Fs, EntryBuffer, 0, EntryBuffer.Length)

                Dim Entry As New ChunkIndexEntry With {
                    .Offset = BitConverter.ToInt64(EntryBuffer, 0),
                    .RecordLength = BitConverter.ToInt32(EntryBuffer, 8)
                }

                If Entry.Offset <> 0 OrElse Entry.RecordLength <> 0 Then
                    If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid index entry {EntryIndex}.")
                    If Entry.Offset + Entry.RecordLength > IndexOffset Then Throw New InvalidDataException($"Index entry {EntryIndex} points outside the chunked data area.")
                End If

                Index.Add(Entry)

            Next

            Return Index

        End Function

        Private Shared Function ComputeIndexMac(Index As List(Of ChunkIndexEntry), MacKey As Byte()) As Byte()

            Using Hmac As New HMACSHA256(MacKey)

                Dim EntryBuffer(IndexEntrySize - 1) As Byte

                For Each Entry In Index

                    Array.Clear(EntryBuffer, 0, EntryBuffer.Length)

                    System.Buffer.BlockCopy(BitConverter.GetBytes(Entry.Offset), 0, EntryBuffer, 0, 8)
                    System.Buffer.BlockCopy(BitConverter.GetBytes(Entry.RecordLength), 0, EntryBuffer, 8, 4)

                    Hmac.TransformBlock(EntryBuffer, 0, EntryBuffer.Length, Nothing, 0)

                Next

                Hmac.TransformFinalBlock(New Byte() {}, 0, 0)

                Return Hmac.Hash

            End Using

        End Function

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