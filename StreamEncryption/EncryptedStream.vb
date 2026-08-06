Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text

Namespace Streams

    ' ================================================================================
    ' ChunkedStream
    ' ================================================================================
    '
    ' Compatibility
    '   - Designed for .NET Framework 4.8+.
    '   - Uses only APIs available in .NET Framework 4.8.
    '   - The underlying stream remains owned by the caller.
    '     i.e Disposing ChunkedStream does not dispose the underlying stream.
    '
    ' Overview
    '   - Random-access encrypted storage.
    '   - Append-only chunk updates.
    '   - Per-chunk authentication.
    '   - Optional per-chunk compression.
    '   - Sparse chunk support.
    '   - Defragmentation and crash recovery support.
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
    '   - This protects against process termination during a header write.
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
    '   100     64      Defrag Recovery Journal
    '
    '   164     316     Reserved For Future Use
    '
    '   480     32      Header HMAC-SHA256
    '
    ' Header HMAC covers bytes:
    '   0..479
    '
    ' Chunk Index Entry Layout (16 bytes)
    '
    '   Offset      Size      Description
    '   0           8         Chunk Record Offset
    '   8           4         Chunk Record Length
    '   12          4         Reserved
    '
    '   The index entry position determines the logical chunk number.
    '
    '   Empty index entry:
    '     Offset = 0
    '     Length = 0
    '
    '   means:
    '     Sparse / unallocated / removed chunk.
    '
    ' Chunk Record Layout
    '
    '   Offset      Size      Description
    '   0           8         Chunk Index
    '   8           4         Compression Method
    '   12          4         Plain Length
    '   16          4         Payload Length
    '   20          12        Reserved
    '
    '   32          16        IV / Counter Start
    '   48          N         Encrypted Payload
    '   48 + N      32        Chunk HMAC-SHA256
    '
    '   Chunk HMAC covers:
    '     Record header + IV + encrypted payload
    '
    ' Compression Methods
    '   0 = None
    '   1 = LZ4
    '   2 = Deflate
    '   3 = GZip
    '
    ' Encryption
    '   - AES-CTR style stream encryption implemented using AES-ECB counter blocks.
    '   - Separate encryption and MAC keys are derived from the master key and file salt.
    '
    ' Authentication
    '   - Header is authenticated with HMAC-SHA256.
    '   - Index table is authenticated with HMAC-SHA256.
    '   - Every chunk record is authenticated with HMAC-SHA256.
    '
    ' Sparse Chunks
    '   - If StoreSparseChunks is False, all-zero chunks are represented by empty index
    '     entries and no physical chunk record is stored.
    '   - Reads from sparse chunks return zero-filled data.
    '
    ' Defragmentation
    '   - Move:
    '       Fills holes using chunk records from later in the file where possible.
    '
    '   - Sequence:
    '       Reorders live chunk records into logical chunk order.
    '
    '   - Rebuild:
    '       Rewrites all live chunks using current compression and sparse settings.
    '
    ' Defrag Recovery
    '   - Defrag moves are journaled one chunk at a time.
    '   - If the application terminates during a move, Open() automatically completes or
    '     rolls back the interrupted move.
    '   - Old chunk records are not erased during moves; they simply become unreferenced
    '     until compaction removes them.
    '
    ' Fragmentation Visualisation
    '   - GenerateFragmentationBitmap() maps the encrypted stream left-to-right, then
    '     top-to-bottom.
    '   - Green = live chunk records.
    '   - Red = unreferenced / fragmented space.
    '   - Blue = current index table.
    '   - Black = unused area.
    '
    ' Thread Safety
    '   - Public operations are protected by SyncLock.
    '   - One ChunkedStream instance is thread-safe.
    '   - Multiple ChunkedStream instances against the same backing stream/file are not
    '     coordinated and should be avoided unless externally synchronised.
    '
    ' ================================================================================

    Friend Class General

        ''' <summary>
        ''' Derives a 256-bit master key from a passphrase using PBKDF2-SHA256.
        ''' </summary>
        ''' <param name="Passphrase">The passphrase to derive from.</param>
        ''' <param name="Salt">The salt. If shorter than eight bytes, it is padded for PBKDF2 compatibility.</param>
        ''' <param name="Iterations">The PBKDF2 iteration count.</param>
        ''' <returns>A 32-byte master key.</returns>
        Public Shared Function DeriveMasterKey(Passphrase As String,
                                               Salt As Byte(),
                                               Iterations As Integer) As Byte()

            If Passphrase Is Nothing Then Throw New ArgumentNullException(NameOf(Passphrase))
            If Salt Is Nothing Then Throw New ArgumentNullException(NameOf(Salt))
            If Iterations <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Iterations), "Iterations must be greater than zero.")

            Dim EffectiveSalt = DirectCast(Salt.Clone(), Byte())

            If EffectiveSalt.Length < 8 Then
                Array.Resize(EffectiveSalt, 8)
            End If

            Dim PasswordBytes = Encoding.UTF8.GetBytes(Passphrase)

            Try
                Using Kdf As New Rfc2898DeriveBytes(PasswordBytes, EffectiveSalt, Iterations, HashAlgorithmName.SHA256)
                    Return Kdf.GetBytes(32)
                End Using
            Finally
                Array.Clear(PasswordBytes, 0, PasswordBytes.Length)
            End Try

        End Function

    End Class

    Friend NotInheritable Class ChunkedStream
        Implements IDisposable

        ''' <summary>
        ''' Physical storage optimisation strategy used by Defragment.
        ''' </summary>
        Friend Enum DefragTypes

            ''' <summary>
            ''' Fast compaction mode. Moves later chunk records into earlier holes where they fit.
            ''' </summary>
            Move = 0

            ''' <summary>
            ''' Reorders live chunk records into logical chunk order.
            ''' </summary>
            Sequence = 1

            ''' <summary>
            ''' Fully rewrites all live chunk records using the current compression and sparse settings.
            ''' </summary>
            Rebuild = 2

        End Enum

        ''' <summary>
        ''' Cancellation token used by progress callbacks.
        ''' </summary>
        Friend NotInheritable Class CancellationToken

            ''' <summary>
            ''' Set to True from the callback to request cancellation.
            ''' </summary>
            Public Property Cancel As Boolean

        End Class

        Public Enum ProcessUnitTypes
            Bytes
            Chunks
            Arbitrary
        End Enum

        ''' <summary>
        ''' Some changes may only affect newly written chunks.
        ''' In this case existing chunk records retain their original
        ''' compression/sparse representation etc until rewritten.
        ''' </summary>
        Friend Class ChunkedStreamOptions

            ''' <summary>
            ''' Compression algorithm applied to individual chunk records.
            ''' </summary>
            Friend Enum CompressionMethods As Integer

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

        End Class

        ''' <summary>
        ''' Reports progress for long-running ChunkedStream operations.
        ''' </summary>
        ''' <param name="ProcessedUnits">The current progress value.</param>
        ''' <param name="TotalUnits">The maximum progress value.</param>
        ''' <param name="UnitType">What the units used for progress represent.</param>
        ''' <param name="CancellationToken">Token that may be set to request cancellation.</param>
        Friend Delegate Sub StreamProgressCallback(ProcessedUnits As Long,
                                                   TotalUnits As Long,
                                                   UnitType As ProcessUnitTypes,
                                                   CancellationToken As CancellationToken)

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

        Private Const HeaderMacOffset As Integer = 480
        Private Const HeaderMacCoveredSize As Integer = 480

        Private Const IndexEntrySize As Integer = 16

        Private Const ChunkRecordHeaderSize As Integer = 32
        Private Const ChunkRecordIvOffset As Integer = ChunkRecordHeaderSize
        Private Const ChunkRecordDataOffset As Integer = ChunkRecordHeaderSize + IvSize
        Private Const MinChunkRecordSize As Integer = ChunkRecordHeaderSize + IvSize + MacSize

        Private Shared ReadOnly HeaderMagic As Byte() = Encoding.ASCII.GetBytes("ESTRM001")

        <Flags>
        Private Enum HeaderFlags As Long
            None = 0
            VariableChunkIndex = 1
            CompressionLz4 = 2
            CompressionDeflate = 4
            CompressionGZip = 8
            SparseChunks = 16
        End Enum

        Private Enum DefragJournalState As Integer
            None = 0
            Copying = 1
            Copied = 2
        End Enum

        Private Structure ChunkIndexEntry
            Public Offset As Long
            Public RecordLength As Integer
        End Structure

        Private Structure DefragLiveEntry
            Public ChunkIndex As Integer
            Public Offset As Long
            Public RecordLength As Integer
        End Structure

        Private Structure DefragHole
            Public Offset As Long
            Public Length As Long
        End Structure

        Private Structure HeaderCandidate
            Public Header As Byte()
            Public HeaderSequence As Long
            Public EncryptionKey As Byte()
            Public MacKey As Byte()
            Public HeaderCopyIndex As Integer
        End Structure

        Private ReadOnly _Fs As Stream
        Private ReadOnly _EncryptionKey As Byte()
        Private ReadOnly _MacKey As Byte()
        Private ReadOnly _SyncRoot As New Object()
        Public ReadOnly Options As ChunkedStreamOptions

        Private ReadOnly _Header As Byte()
        Private ReadOnly _Index As List(Of ChunkIndexEntry)

        Private ReadOnly _ChunkPlain As Byte()
        Private ReadOnly _Counter As Byte()
        Private ReadOnly _KeyStream As Byte()

        Private ReadOnly _AesProvider As Aes
        Private ReadOnly _AesTransform As ICryptoTransform
        Private ReadOnly _ChunkHmac As HMACSHA256
        Private ReadOnly _Rng As RandomNumberGenerator

        Private _HeaderFlags As HeaderFlags
        Private _HeaderSequence As Long
        Private _ActiveHeaderCopy As Integer
        Private _IndexOffset As Long
        Private _Length As Long
        Private _Disposed As Boolean

        ''' <summary>
        ''' Gets the logical plaintext length of the encrypted stream.
        ''' </summary>
        Public ReadOnly Property Length As Long
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _Length
                End SyncLock
            End Get
        End Property

        Private Sub New(Fs As Stream,
                        Header As Byte(),
                        HeaderSequence As Long,
                        ActiveHeaderCopy As Integer,
                        EncryptionKey As Byte(),
                        MacKey As Byte(),
                        Length As Long,
                        IndexOffset As Long,
                        Index As List(Of ChunkIndexEntry),
                        HeaderFlags As HeaderFlags,
                        Options As ChunkedStreamOptions)

            _Fs = Fs
            _Header = Header
            _HeaderSequence = HeaderSequence
            _ActiveHeaderCopy = ActiveHeaderCopy
            _EncryptionKey = EncryptionKey
            _MacKey = MacKey
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
            _AesProvider.Key = _EncryptionKey
            _AesTransform = _AesProvider.CreateEncryptor()

            _ChunkHmac = New HMACSHA256(_MacKey)
            _Rng = RandomNumberGenerator.Create()

        End Sub

        ''' <summary>
        ''' Opens an existing encrypted stream or creates a new encrypted stream if the backing stream is empty.
        ''' </summary>
        ''' <param name="Fs">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="MasterKey">Master encryption key.</param>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(Fs As Stream, MasterKey As Byte()) As ChunkedStream
            Return Open(Fs, MasterKey, Nothing)
        End Function

        ''' <summary>
        ''' Opens an existing encrypted stream or creates a new encrypted stream if the backing stream is empty.
        ''' </summary>
        ''' <param name="Fs">Backing storage stream. The caller owns the stream lifetime.</param>
        ''' <param name="MasterKey">Master encryption key.</param>
        ''' <param name="Options">Options controlling newly written chunks.</param>
        ''' <returns>An opened ChunkedStream.</returns>
        Public Shared Function Open(Fs As Stream,
                                    MasterKey As Byte(),
                                    Optional Options As ChunkedStreamOptions = Nothing) As ChunkedStream

            If Fs Is Nothing Then Throw New ArgumentNullException(NameOf(Fs))
            If MasterKey Is Nothing Then Throw New ArgumentNullException(NameOf(MasterKey))

            If Fs.Length < DataStartOffset Then
                Return CreateNew(Fs, MasterKey, Options)
            End If

            Dim Candidate = ReadBestHeader(Fs, MasterKey)

            If Candidate.Header Is Nothing Then
                Throw New InvalidDataException("No valid encrypted stream header was found.")
            End If

            Dim Header = Candidate.Header
            Dim HeaderSequence = Candidate.HeaderSequence
            Dim ActiveHeaderCopy = Candidate.HeaderCopyIndex
            Dim EncryptionKey = Candidate.EncryptionKey
            Dim MacKey = Candidate.MacKey

            Dim FlagsValue = BitConverter.ToInt64(Header, FlagsOffset)
            Dim Flags = CType(FlagsValue, HeaderFlags)
            Dim SupportedFlags = HeaderFlags.VariableChunkIndex Or HeaderFlags.CompressionLz4 Or HeaderFlags.CompressionDeflate Or HeaderFlags.CompressionGZip Or HeaderFlags.SparseChunks

            If (FlagsValue And Not CLng(SupportedFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported encrypted stream flags: {FlagsValue}.")
            End If

            If (Flags And HeaderFlags.VariableChunkIndex) = 0 Then
                Throw New InvalidDataException("Encrypted stream does not contain a chunk index.")
            End If

            Dim StoredChunkSize = BitConverter.ToInt32(Header, ChunkSizeOffset)
            If StoredChunkSize <> ChunkSize Then Throw New InvalidDataException($"Unsupported encrypted stream chunk size: {StoredChunkSize}.")

            Dim IndexOffset = BitConverter.ToInt64(Header, IndexOffsetOffset)
            Dim IndexCount = BitConverter.ToInt64(Header, IndexCountOffset)
            Dim FileLength = BitConverter.ToInt64(Header, LengthOffset)

            If FileLength < 0 Then Throw New InvalidDataException("Invalid encrypted stream length.")
            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid encrypted stream index offset.")
            If IndexOffset > Fs.Length Then Throw New InvalidDataException("Encrypted stream index offset is beyond end of stream.")
            If IndexCount < 0 OrElse IndexCount > Integer.MaxValue Then Throw New InvalidDataException("Invalid encrypted stream index count.")

            Dim Index = ReadIndexTable(Fs, IndexOffset, CInt(IndexCount))
            Dim IndexMac = ComputeIndexMac(Index, MacKey)

            If Not FixedTimeEquals(IndexMac, 0, Header, IndexMacOffset, MacSize) Then
                Throw New CryptographicException("Encrypted stream index MAC invalid.")
            End If

            Dim Result = New ChunkedStream(Fs, Header, HeaderSequence, ActiveHeaderCopy, EncryptionKey, MacKey, FileLength, IndexOffset, Index, Flags, Options)
            Result.RecoverDefragJournal()
            Return Result

        End Function

        Private Shared Function CreateNew(Fs As Stream,
                                          MasterKey As Byte(),
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

            Dim FileSalt(FileSaltSize - 1) As Byte
            System.Buffer.BlockCopy(Header, FileSaltOffset, FileSalt, 0, FileSalt.Length)

            Dim EncryptionKey = DeriveKey(MasterKey, FileSalt, KeyPurpose.Enc)
            Dim MacKey = DeriveKey(MasterKey, FileSalt, KeyPurpose.Mac)
            Dim Index As New List(Of ChunkIndexEntry)()

            Dim IndexMac = ComputeIndexMac(Index, MacKey)
            System.Buffer.BlockCopy(IndexMac, 0, Header, IndexMacOffset, MacSize)

            WriteHeaderMac(Header, MacKey)

            Fs.Position = 0
            Fs.Write(Header, 0, Header.Length)
            Fs.Position = HeaderSize
            Fs.Write(Header, 0, Header.Length)
            Fs.SetLength(DataStartOffset)
            FlushDurable(Fs)

            Return New ChunkedStream(Fs, Header, 1L, 0, EncryptionKey, MacKey, 0L, DataStartOffset, Index, Flags, EffectiveOptions)

        End Function

        Private Shared Function ReadBestHeader(Fs As Stream, MasterKey As Byte()) As HeaderCandidate

            Dim Best As New HeaderCandidate()

            For HeaderCopyIndex = 0 To HeaderCopyCount - 1
                Dim Header(HeaderSize - 1) As Byte
                Dim HeaderOffset = HeaderCopyIndex * HeaderSize

                Fs.Position = HeaderOffset
                ReadExactly(Fs, Header, 0, Header.Length)

                If Not FixedTimeEquals(HeaderMagic, 0, Header, MagicOffset, MagicSize) Then
                    Continue For
                End If

                Dim FileSalt(FileSaltSize - 1) As Byte
                System.Buffer.BlockCopy(Header, FileSaltOffset, FileSalt, 0, FileSalt.Length)

                Dim EncryptionKey = DeriveKey(MasterKey, FileSalt, KeyPurpose.Enc)
                Dim MacKey = DeriveKey(MasterKey, FileSalt, KeyPurpose.Mac)

                If Not VerifyHeaderMac(Header, MacKey) Then
                    Continue For
                End If

                Dim HeaderSequence = BitConverter.ToInt64(Header, HeaderSequenceOffset)

                If Best.Header Is Nothing OrElse HeaderSequence > Best.HeaderSequence Then
                    Best.Header = Header
                    Best.HeaderSequence = HeaderSequence
                    Best.EncryptionKey = EncryptionKey
                    Best.MacKey = MacKey
                    Best.HeaderCopyIndex = HeaderCopyIndex
                End If
            Next

            Return Best

        End Function

        ''' <summary>
        ''' Reads decrypted plaintext from the logical stream at the specified offset.
        ''' </summary>
        ''' <param name="Offset">Logical plaintext offset.</param>
        ''' <param name="Output">Output buffer.</param>
        ''' <returns>The number of bytes read.</returns>
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

                For chunkIndex = FirstChunk To LastChunk
                    Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                    LoadChunk(chunkIndex, _ChunkPlain)

                    Dim ChunkStart = chunkIndex * CLng(ChunkSize)
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
        ''' <param name="Offset">Logical plaintext offset.</param>
        ''' <param name="Input">Plaintext bytes to write.</param>
        ''' <returns>The number of bytes written.</returns>
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

                For chunkIndex = FirstChunk To LastChunk
                    Dim ChunkStart = chunkIndex * CLng(ChunkSize)
                    Dim DstOffset = CInt(Math.Max(0L, Offset - ChunkStart))
                    Dim CopyLength = Math.Min(ChunkSize - DstOffset, Input.Length - InPos)
                    Dim LogicalPlainLength = CInt(Math.Min(CLng(ChunkSize), Math.Max(0L, NewLogicalLength - ChunkStart)))
                    Dim IsFullLogicalChunkWrite = DstOffset = 0 AndAlso CopyLength = LogicalPlainLength

                    If IsFullLogicalChunkWrite Then
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                        System.Buffer.BlockCopy(Input, InPos, _ChunkPlain, 0, CopyLength)
                    Else
                        Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                        LoadChunk(chunkIndex, _ChunkPlain)
                        System.Buffer.BlockCopy(Input, InPos, _ChunkPlain, DstOffset, CopyLength)
                    End If

                    InPos += CopyLength
                    WriteChunkRecord(chunkIndex, _ChunkPlain, LogicalPlainLength)
                Next

                _Length = NewLogicalLength
                PersistIndexAndHeader(_IndexOffset)

                Return Input.Length
            End SyncLock

        End Function

        ''' <summary>
        ''' Changes the logical plaintext length of the encrypted stream.
        ''' </summary>
        ''' <param name="Length">New logical plaintext length.</param>
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
                PersistIndexAndHeader(_IndexOffset)
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

                _Disposed = True

                Try
                    If _Fs.CanWrite Then _Fs.Flush()
                Finally
                    _ChunkHmac.Dispose()
                    _AesTransform.Dispose()
                    _AesProvider.Dispose()
                    _Rng.Dispose()
                End Try
            End SyncLock

        End Sub

        ''' <summary>
        ''' Returns fragmentation as a value between 0 and 1.
        ''' </summary>
        ''' <returns>0 means no fragmentation. 1 means fully fragmented.</returns>
        Public Function GetFragmentation() As Double

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim UsedBytes As Long = 0

                For Each entry In _Index
                    If entry.Offset <> 0 AndAlso entry.RecordLength > 0 Then
                        UsedBytes += entry.RecordLength
                    End If
                Next

                Dim DataEnd = GetDataEndFromIndex()
                Dim TotalStoredChunkBytes = Math.Max(0L, DataEnd - DataStartOffset)
                Dim WastedBytes = Math.Max(0L, TotalStoredChunkBytes - UsedBytes)

                If TotalStoredChunkBytes = 0 Then Return 0

                Return WastedBytes / CDbl(TotalStoredChunkBytes)
            End SyncLock

        End Function

        ''' <summary>
        ''' Validates all live chunk records.
        ''' </summary>
        ''' <param name="ProgressCallback">Optional progress callback.</param>
        ''' <exception cref="InvalidDataException">Thrown when stream contents are invalid.</exception>
        ''' <exception cref="CryptographicException">Thrown when authentication fails.</exception>
        Public Sub Validate(Optional ProgressCallback As StreamProgressCallback = Nothing)

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim CancellationToken As New CancellationToken()

                ValidateAllLiveChunkRecords(ProgressCallback, CancellationToken)
            End SyncLock

        End Sub

        ''' <summary>
        ''' Generates a bitmap visualisation of the physical storage layout.
        ''' </summary>
        ''' <param name="Width">Bitmap width.</param>
        ''' <param name="Height">Bitmap height.</param>
        ''' <returns>A bitmap showing live data, fragmented space and the index table.</returns>
        Public Function GenerateFragmentationBitmap(Width As Integer,
                                                    Height As Integer) As Bitmap

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Width))
                If Height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Height))

                Dim Result As New Bitmap(Width,
                                         Height,
                                         Imaging.PixelFormat.Format24bppRgb)

                Dim DataEnd = GetDataEndFromIndex()

                If DataEnd <= DataStartOffset Then

                    Using Graphics = System.Drawing.Graphics.FromImage(Result)
                        Graphics.Clear(Color.Black)
                    End Using

                    Return Result

                End If

                Dim Segments As New List(Of Tuple(Of Long,
                                                  Long,
                                                  Color))

                '
                ' Live chunks.
                '
                For Each Entry In _Index

                    If Entry.Offset = 0 OrElse
                       Entry.RecordLength <= 0 Then

                        Continue For

                    End If

                    Segments.Add(
                        Tuple.Create(
                            Entry.Offset,
                            Entry.Offset + CLng(Entry.RecordLength),
                            Color.LimeGreen))

                Next

                '
                ' Index table.
                '
                Dim IndexBytes =
                    CLng(_Index.Count) * IndexEntrySize

                If IndexBytes > 0 Then

                    Segments.Add(
                        Tuple.Create(
                            _IndexOffset,
                            _IndexOffset + IndexBytes,
                            Color.DodgerBlue))

                End If

                Segments.Sort(
                    Function(left, right)
                        Return left.Item1.CompareTo(right.Item1)
                    End Function)

                '
                ' Insert dead-space segments.
                '
                Dim RenderSegments As New List(Of Tuple(Of Long,
                                                        Long,
                                                        Color))

                Dim Cursor = CLng(DataStartOffset)

                For Each Segment In Segments

                    If Segment.Item1 > Cursor Then

                        RenderSegments.Add(
                            Tuple.Create(
                                Cursor,
                                Segment.Item1,
                                Color.Red))

                    End If

                    RenderSegments.Add(Segment)

                    Cursor =
                        Math.Max(
                            Cursor,
                            Segment.Item2)

                Next

                If Cursor < DataEnd Then

                    RenderSegments.Add(
                        Tuple.Create(
                            Cursor,
                            DataEnd,
                            Color.Red))

                End If

                '
                ' Black background.
                '
                Dim BitmapData =
                    Result.LockBits(
                        New Rectangle(0, 0, Width, Height),
                        Imaging.ImageLockMode.WriteOnly,
                        Imaging.PixelFormat.Format24bppRgb)

                Try

                    Dim Stride = BitmapData.Stride
                    Dim Buffer(Math.Abs(Stride) * Height - 1) As Byte

                    '
                    ' Buffer is already zero-initialised, giving us a black background.
                    '

                    Dim TotalPixels = Width * Height
                    Dim TotalBytes = CDbl(DataEnd)

                    For Each Segment In RenderSegments

                        Dim StartPixel =
                            CInt(
                                Math.Floor(
                                    (Segment.Item1 / TotalBytes) *
                                    TotalPixels))

                        Dim EndPixel =
                            CInt(
                                Math.Ceiling(
                                    (Segment.Item2 / TotalBytes) *
                                    TotalPixels))

                        If EndPixel <= StartPixel Then
                            EndPixel = StartPixel + 1
                        End If

                        If EndPixel > TotalPixels Then
                            EndPixel = TotalPixels
                        End If

                        Dim Colour = Segment.Item3

                        Dim Blue = Colour.B
                        Dim Green = Colour.G
                        Dim Red = Colour.R

                        For PixelIndex = StartPixel To EndPixel - 1

                            Dim X = PixelIndex Mod Width
                            Dim Y = PixelIndex \ Width

                            If Y >= Height Then Exit For

                            Dim BufferOffset =
                                (Y * Stride) +
                                (X * 3)

                            Buffer(BufferOffset) = Blue
                            Buffer(BufferOffset + 1) = Green
                            Buffer(BufferOffset + 2) = Red

                        Next

                    Next

                    Runtime.InteropServices.Marshal.Copy(
                        Buffer,
                        0,
                        BitmapData.Scan0,
                        Buffer.Length)

                Finally

                    Result.UnlockBits(BitmapData)

                End Try

                Return Result

            End SyncLock

        End Function
        ''' <summary>
        ''' Defragments the physical storage layout.
        ''' </summary>
        ''' <param name="Type">Defragmentation strategy.</param>
        ''' <param name="ProgressCallback">Optional progress callback. Set CancellationToken.Cancel to request cancellation.</param>
        ''' <returns>Number of bytes reclaimed, or -1 if cancelled.</returns>
        Public Function Defragment(Optional Type As DefragTypes = DefragTypes.Move,
                                   Optional ProgressCallback As StreamProgressCallback = Nothing) As Long

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim OriginalLength = _Fs.Length
                Dim CancellationToken As New CancellationToken()

                Select Case Type
                    Case DefragTypes.Move
                        DefragmentMove(ProgressCallback, CancellationToken)
                    Case DefragTypes.Sequence
                        DefragmentSequence(False, ProgressCallback, CancellationToken)
                    Case DefragTypes.Rebuild
                        DefragmentRebuild(ProgressCallback, CancellationToken)
                    Case Else
                        Throw New ArgumentOutOfRangeException(NameOf(Type))
                End Select

                If CancellationToken.Cancel Then Return -1

                Return Math.Max(0L, OriginalLength - _Fs.Length)
            End SyncLock

        End Function

        Private Sub RecoverDefragJournal()

            Dim State = CType(BitConverter.ToInt32(_Header, JournalStateOffset), DefragJournalState)

            If State = DefragJournalState.None Then Return

            Dim ChunkIndex = BitConverter.ToInt64(_Header, JournalChunkIndexOffset)
            Dim OldOffset = BitConverter.ToInt64(_Header, JournalOldOffsetOffset)
            Dim OldLength = BitConverter.ToInt32(_Header, JournalOldLengthOffset)
            Dim NewOffset = BitConverter.ToInt64(_Header, JournalNewOffsetOffset)
            Dim NewLength = BitConverter.ToInt32(_Header, JournalNewLengthOffset)

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then
                Throw New InvalidDataException("Invalid defrag journal chunk index.")
            End If

            EnsureIndexSize(CInt(ChunkIndex + 1))

            Select Case State
                Case DefragJournalState.Copying
                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = OldOffset, .RecordLength = OldLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearDefragJournal()
                        Return
                    End If

                    Throw New CryptographicException("Defrag recovery failed. Old chunk record is invalid.")

                Case DefragJournalState.Copied
                    If IsValidChunkRecordAt(CInt(ChunkIndex), NewOffset, NewLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = NewOffset, .RecordLength = NewLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearDefragJournal()
                        Return
                    End If

                    If IsValidChunkRecordAt(CInt(ChunkIndex), OldOffset, OldLength) Then
                        _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = OldOffset, .RecordLength = OldLength}
                        PersistIndexAndHeader(GetDataEndFromIndex())
                        ClearDefragJournal()
                        Return
                    End If

                    Throw New CryptographicException("Defrag recovery failed. Neither old nor new chunk record is valid.")

                Case Else
                    Throw New InvalidDataException($"Unknown defrag journal state: {CInt(State)}.")
            End Select

        End Sub

        Private Sub MoveChunkRecordJournaled(ChunkIndex As Integer, TargetOffset As Long)

            Dim Entry = _Index(ChunkIndex)

            If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Return
            If Entry.Offset = TargetOffset Then Return

            WriteDefragJournal(DefragJournalState.Copying,
                               ChunkIndex,
                               Entry.Offset,
                               Entry.RecordLength,
                               TargetOffset,
                               Entry.RecordLength)

            CopyChunkRecord(Entry.Offset, Entry.RecordLength, TargetOffset)
            FlushDurable(_Fs)

            If Not IsValidChunkRecordAt(ChunkIndex, TargetOffset, Entry.RecordLength) Then
                Throw New CryptographicException("Defrag copied chunk record failed validation.")
            End If

            WriteDefragJournal(DefragJournalState.Copied,
                               ChunkIndex,
                               Entry.Offset,
                               Entry.RecordLength,
                               TargetOffset,
                               Entry.RecordLength)

            _Index(ChunkIndex) = New ChunkIndexEntry With {
                .Offset = TargetOffset,
                .RecordLength = Entry.RecordLength
            }

            PersistIndexAndHeader(GetDataEndFromIndex(), True)
            ClearDefragJournal()

        End Sub

        Private Sub WriteDefragJournal(State As DefragJournalState,
                                       ChunkIndex As Long,
                                       OldOffset As Long,
                                       OldLength As Integer,
                                       NewOffset As Long,
                                       NewLength As Integer)

            Array.Clear(_Header, JournalAreaOffset, JournalAreaLength)

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(State)), 0, _Header, JournalStateOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkIndex), 0, _Header, JournalChunkIndexOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(OldOffset), 0, _Header, JournalOldOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(OldLength), 0, _Header, JournalOldLengthOffset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(NewOffset), 0, _Header, JournalNewOffsetOffset, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(NewLength), 0, _Header, JournalNewLengthOffset, 4)

            WriteHeaderCopies(True)

        End Sub

        Private Sub ClearDefragJournal()

            Array.Clear(_Header, JournalAreaOffset, JournalAreaLength)
            WriteHeaderCopies(True)

        End Sub

        Private Function IsValidChunkRecordAt(ChunkIndex As Integer,
                                              Offset As Long,
                                              RecordLength As Integer) As Boolean

            If Offset < DataStartOffset OrElse RecordLength < MinChunkRecordSize Then Return False
            If Offset + RecordLength > _Fs.Length Then Return False

            Try
                Dim Record(RecordLength - 1) As Byte
                _Fs.Position = Offset
                ReadExactly(_Fs, Record, 0, Record.Length)
                Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                DecryptChunkRecord(ChunkIndex, Record, _ChunkPlain)
                Return True
            Catch ex As IOException
                Return False
            Catch ex As InvalidDataException
                Return False
            Catch ex As CryptographicException
                Return False
            End Try

        End Function

        Private Sub LoadChunk(ChunkIndex As Long, Plain As Byte())

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If ChunkIndex >= _Index.Count Then Return

            Dim Entry = _Index(CInt(ChunkIndex))

            If Entry.Offset = 0 OrElse Entry.RecordLength = 0 Then Return
            If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk index entry for chunk {ChunkIndex}.")
            If Entry.Offset + Entry.RecordLength > _IndexOffset Then Throw New InvalidDataException($"Chunk {ChunkIndex} record extends beyond data area.")

            Dim Record(Entry.RecordLength - 1) As Byte
            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Record, 0, Record.Length)

            DecryptChunkRecord(ChunkIndex, Record, Plain)

        End Sub

        Private Sub WriteChunkRecord(ChunkIndex As Long, Plain As Byte(), PlainLength As Integer)

            If ChunkIndex < 0 OrElse ChunkIndex > Integer.MaxValue Then Throw New ArgumentOutOfRangeException(NameOf(ChunkIndex))
            If PlainLength < 0 OrElse PlainLength > ChunkSize Then Throw New ArgumentOutOfRangeException(NameOf(PlainLength))

            If PlainLength = 0 OrElse (Not Options.StoreSparseChunks AndAlso IsAllZero(Plain, PlainLength)) Then
                EnsureIndexSize(CInt(ChunkIndex + 1))
                _Index(CInt(ChunkIndex)) = New ChunkIndexEntry()
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks
                Return
            End If

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim Method = ChunkedStreamOptions.CompressionMethods.None

            If Options.CompressionMethod <> ChunkedStreamOptions.CompressionMethods.None Then
                Dim Compressed = CompressPayload(Options.CompressionMethod, Plain, PlainLength)

                If ShouldUseCompressed(PlainLength, Compressed.Length) Then
                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    Method = Options.CompressionMethod
                    MarkCompressionFlag(Method)
                End If
            End If

            Dim RecordLength = ChunkRecordDataOffset + PayloadLength + MacSize
            Dim Record(RecordLength - 1) As Byte

            System.Buffer.BlockCopy(BitConverter.GetBytes(ChunkIndex), 0, Record, 0, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(Method)), 0, Record, 8, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PlainLength), 0, Record, 12, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(PayloadLength), 0, Record, 16, 4)

            _Rng.GetBytes(_Counter)
            System.Buffer.BlockCopy(_Counter, 0, Record, ChunkRecordIvOffset, IvSize)

            CryptPayload(Payload, 0, PayloadLength, Record, ChunkRecordDataOffset)

            _ChunkHmac.Initialize()
            Dim Mac = _ChunkHmac.ComputeHash(Record, 0, ChunkRecordDataOffset + PayloadLength)
            System.Buffer.BlockCopy(Mac, 0, Record, ChunkRecordDataOffset + PayloadLength, MacSize)

            Dim NewRecordOffset = _IndexOffset

            _Fs.Position = NewRecordOffset
            _Fs.Write(Record, 0, Record.Length)

            EnsureIndexSize(CInt(ChunkIndex + 1))
            _Index(CInt(ChunkIndex)) = New ChunkIndexEntry With {.Offset = NewRecordOffset, .RecordLength = Record.Length}
            _IndexOffset = NewRecordOffset + Record.Length

        End Sub

        Private Sub DecryptChunkRecord(ExpectedChunkIndex As Long, Record As Byte(), Plain As Byte())

            If Record.Length < MinChunkRecordSize Then Throw New InvalidDataException("Encrypted chunk record is too small.")

            Dim ChunkIndex = BitConverter.ToInt64(Record, 0)
            If ChunkIndex <> ExpectedChunkIndex Then Throw New InvalidDataException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {ChunkIndex}.")

            Dim Method = CType(BitConverter.ToInt32(Record, 8), ChunkedStreamOptions.CompressionMethods)
            Dim PlainLength = BitConverter.ToInt32(Record, 12)
            Dim DataLength = BitConverter.ToInt32(Record, 16)

            If PlainLength < 0 OrElse PlainLength > ChunkSize Then Throw New InvalidDataException("Invalid chunk plain length.")
            If DataLength < 0 OrElse ChunkRecordDataOffset + DataLength + MacSize <> Record.Length Then Throw New InvalidDataException("Invalid chunk data length.")

            _ChunkHmac.Initialize()
            Dim ExpectedMac = _ChunkHmac.ComputeHash(Record, 0, ChunkRecordDataOffset + DataLength)

            If Not FixedTimeEquals(ExpectedMac, 0, Record, ChunkRecordDataOffset + DataLength, MacSize) Then
                Throw New CryptographicException("Chunk MAC invalid.")
            End If

            System.Buffer.BlockCopy(Record, ChunkRecordIvOffset, _Counter, 0, IvSize)

            Dim Decrypted = If(DataLength = 0, New Byte() {}, New Byte(DataLength - 1) {})

            If DataLength > 0 Then
                CryptPayload(Record, ChunkRecordDataOffset, DataLength, Decrypted, 0)
            End If

            Dim Restored = DecompressPayload(Method, Decrypted, PlainLength)

            If Restored.Length <> PlainLength Then Throw New InvalidDataException("Chunk decompressed/plain length mismatch.")
            If PlainLength > 0 Then System.Buffer.BlockCopy(Restored, 0, Plain, 0, PlainLength)

        End Sub

        Private Sub CryptPayload(Input As Byte(),
                                 InputOffset As Integer,
                                 Count As Integer,
                                 Output As Byte(),
                                 OutputOffset As Integer)

            For blockOffset = 0 To Count - 1 Step 16
                _AesTransform.TransformBlock(_Counter, 0, 16, _KeyStream, 0)

                Dim BytesToProcess = Math.Min(16, Count - blockOffset)

                For i = 0 To BytesToProcess - 1
                    Output(OutputOffset + blockOffset + i) = CByte(CInt(Input(InputOffset + blockOffset + i)) Xor CInt(_KeyStream(i)))
                Next

                IncrementCounter(_Counter)
            Next

        End Sub

        Private Sub ValidateAllLiveChunkRecords(ProgressCallback As StreamProgressCallback,
                                                CancellationToken As CancellationToken)

            Dim TotalChunks = _Index.Count
            Dim ProcessedChunks As Long = 0

            For ChunkIndex = 0 To _Index.Count - 1
                If CancellationToken.Cancel Then Return

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                ValidateChunkRecord(ChunkIndex)

                ProcessedChunks += 1

                ReportDefragProgress(ProgressCallback,
                                      ProcessedChunks,
                                      Math.Max(1, TotalChunks),
                                      ProcessUnitTypes.Chunks,
                                      CancellationToken)
            Next

        End Sub

        Private Sub ValidateChunkRecord(ExpectedChunkIndex As Integer)

            If ExpectedChunkIndex < 0 OrElse ExpectedChunkIndex >= _Index.Count Then
                Throw New ArgumentOutOfRangeException(NameOf(ExpectedChunkIndex))
            End If

            Dim Entry = _Index(ExpectedChunkIndex)

            If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Return

            If Entry.Offset < DataStartOffset Then Throw New InvalidDataException($"Invalid chunk offset for chunk {ExpectedChunkIndex}.")
            If Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk length for chunk {ExpectedChunkIndex}.")
            If Entry.Offset + Entry.RecordLength > _IndexOffset Then Throw New InvalidDataException($"Chunk {ExpectedChunkIndex} extends beyond data area.")

            Dim Record(Entry.RecordLength - 1) As Byte

            _Fs.Position = Entry.Offset
            ReadExactly(_Fs, Record, 0, Record.Length)

            Dim ChunkIndex = BitConverter.ToInt64(Record, 0)

            If ChunkIndex <> ExpectedChunkIndex Then
                Throw New InvalidDataException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {ChunkIndex}.")
            End If

            Dim DataLength = BitConverter.ToInt32(Record, 16)

            If DataLength < 0 Then Throw New InvalidDataException("Invalid chunk data length.")

            If ChunkRecordDataOffset + DataLength + MacSize <> Record.Length Then
                Throw New InvalidDataException("Invalid chunk record length.")
            End If

            _ChunkHmac.Initialize()

            Dim ExpectedMac = _ChunkHmac.ComputeHash(Record, 0, ChunkRecordDataOffset + DataLength)

            If Not FixedTimeEquals(ExpectedMac,
                                   0,
                                   Record,
                                   ChunkRecordDataOffset + DataLength,
                                   MacSize) Then
                Throw New CryptographicException($"Chunk MAC invalid for chunk {ExpectedChunkIndex}.")
            End If

        End Sub

        Private Sub DefragmentMove(ProgressCallback As StreamProgressCallback,
                                   CancellationToken As CancellationToken)

            Const ProgressScale As Long = 1000000

            ReportDefragProgress(ProgressCallback, 0, ProgressScale, ProcessUnitTypes.Arbitrary, CancellationToken)

            Do
                If CancellationToken.Cancel Then Return

                Dim LiveEntries = GetLiveEntriesSortedByOffset()
                Dim Holes = GetDeadHoles(LiveEntries)

                If Holes.Count = 0 Then Exit Do

                Dim MovedSomething = False

                'With the move operation the initial completed units are the chunks that are already "in place"
                'So progress could start at 800000 / 1000000 ... which is not ideal...
                'So instead we base the scale on 800000 -> 1000000 for ReportDefragProgress
                Dim OrigFragmentation As Double?

                For Each hole In Holes
                    If CancellationToken.Cancel Then Return

                    Dim CandidateIndex = FindLatestLiveEntryThatFitsHole(LiveEntries, hole)

                    If CandidateIndex < 0 Then Continue For

                    Dim Candidate = LiveEntries(CandidateIndex)

                    MoveChunkRecordJournaled(Candidate.ChunkIndex, hole.Offset)
                    MovedSomething = True

                    Dim CurrentFragmentation = GetFragmentation()
                    If OrigFragmentation.HasValue = False Then
                        OrigFragmentation = CurrentFragmentation
                    End If
                    Dim CompletedUnits = CLng(((OrigFragmentation - CurrentFragmentation) / OrigFragmentation) * ProgressScale)

                    ReportDefragProgress(ProgressCallback,
                                         CompletedUnits,
                                         ProgressScale,
                                         ProcessUnitTypes.Arbitrary,
                                         CancellationToken)

                    Exit For
                Next

                If Not MovedSomething Then Exit Do
            Loop

            CommitDefragCheckpoint(GetDataEndFromIndex())

            ReportDefragProgress(ProgressCallback,
                                  ProgressScale,
                                  ProgressScale,
                                  ProcessUnitTypes.Arbitrary,
                                  CancellationToken)

        End Sub

        Private Function FindLatestLiveEntryThatFitsHole(LiveEntries As List(Of DefragLiveEntry),
                                                         Hole As DefragHole) As Integer

            For index = LiveEntries.Count - 1 To 0 Step -1
                Dim Entry = LiveEntries(index)

                If Entry.Offset <= Hole.Offset Then Exit For
                If Entry.RecordLength <= Hole.Length Then Return index
            Next

            Return -1

        End Function

        Private Sub DefragmentSequence(ForceMoveAll As Boolean,
                                       ProgressCallback As StreamProgressCallback,
                                       CancellationToken As CancellationToken)

            Dim TotalBytes = _Index.Where(Function(entry) entry.Offset > 0 AndAlso entry.RecordLength > 0).
                                    Sum(Function(entry) CLng(entry.RecordLength))
            Dim ProcessedBytes As Long = 0
            Dim TargetOffset = CLng(DataStartOffset)

            For ChunkIndex = 0 To _Index.Count - 1
                If CancellationToken.Cancel Then Return

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                EnsureTargetRangeIsFree(TargetOffset, Entry.RecordLength, ChunkIndex, ProgressCallback, CancellationToken)
                If CancellationToken.Cancel Then Return

                Entry = _Index(ChunkIndex)

                If ForceMoveAll OrElse Entry.Offset <> TargetOffset Then
                    MoveChunkRecordJournaled(ChunkIndex, TargetOffset)
                End If

                ProcessedBytes += Entry.RecordLength
                ReportDefragProgress(ProgressCallback, ProcessedBytes, TotalBytes, ProcessUnitTypes.Bytes, CancellationToken)

                TargetOffset += Entry.RecordLength
            Next

            CommitDefragCheckpoint(GetDataEndFromIndex())

        End Sub

        Private Sub DefragmentRebuild(ProgressCallback As StreamProgressCallback,
                                      CancellationToken As CancellationToken)

            Dim TotalBytes = _Index.Where(Function(entry) entry.Offset > 0 AndAlso entry.RecordLength > 0).
                                    Sum(Function(entry) CLng(entry.RecordLength))
            Dim ProcessedBytes As Long = 0

            For ChunkIndex = 0 To _Index.Count - 1
                If CancellationToken.Cancel Then Return

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                Array.Clear(_ChunkPlain, 0, _ChunkPlain.Length)
                LoadChunk(ChunkIndex, _ChunkPlain)

                Dim ChunkStart = CLng(ChunkIndex) * ChunkSize
                Dim PlainLength = CInt(Math.Min(CLng(ChunkSize), Math.Max(0L, _Length - ChunkStart)))

                WriteChunkRecord(ChunkIndex, _ChunkPlain, PlainLength)
                PersistIndexAndHeader(_IndexOffset)

                ProcessedBytes += Entry.RecordLength
                ReportDefragProgress(ProgressCallback, ProcessedBytes, TotalBytes, ProcessUnitTypes.Bytes, CancellationToken)
            Next

            If CancellationToken.Cancel Then Return

            DefragmentSequence(True, ProgressCallback, CancellationToken)

        End Sub

        Private Sub EnsureTargetRangeIsFree(TargetOffset As Long,
                                            RecordLength As Integer,
                                            ExceptChunkIndex As Integer,
                                            ProgressCallback As StreamProgressCallback,
                                            CancellationToken As CancellationToken)

            While True
                If CancellationToken.Cancel Then Return

                Dim OverlapChunkIndex = FindOverlappingLiveChunk(TargetOffset, RecordLength, ExceptChunkIndex)

                If OverlapChunkIndex < 0 Then Return

                Dim AppendOffset = Math.Max(_Fs.Length, GetDataEndFromIndex())
                MoveChunkRecordJournaled(OverlapChunkIndex, AppendOffset)

                Dim Entry = _Index(OverlapChunkIndex)
            End While

        End Sub

        Private Function FindOverlappingLiveChunk(TargetOffset As Long,
                                                  RecordLength As Integer,
                                                  ExceptChunkIndex As Integer) As Integer

            Dim TargetEnd = TargetOffset + RecordLength

            For ChunkIndex = 0 To _Index.Count - 1
                If ChunkIndex = ExceptChunkIndex Then Continue For

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For

                Dim EntryEnd = Entry.Offset + Entry.RecordLength

                If TargetOffset < EntryEnd AndAlso TargetEnd > Entry.Offset Then Return ChunkIndex
            Next

            Return -1

        End Function

        Private Sub CopyChunkRecord(SourceOffset As Long,
                                    RecordLength As Integer,
                                    TargetOffset As Long)

            If SourceOffset < DataStartOffset Then Throw New InvalidDataException("Invalid source record offset.")
            If TargetOffset < DataStartOffset Then Throw New InvalidDataException("Invalid target record offset.")
            If RecordLength < MinChunkRecordSize Then Throw New InvalidDataException("Invalid record length.")

            Dim Record(RecordLength - 1) As Byte

            _Fs.Position = SourceOffset
            ReadExactly(_Fs, Record, 0, Record.Length)

            _Fs.Position = TargetOffset
            _Fs.Write(Record, 0, Record.Length)

        End Sub

        Private Sub CommitDefragCheckpoint(DataEnd As Long)

            If DataEnd < DataStartOffset Then Throw New InvalidDataException("Invalid defrag data end.")

            PersistIndexAndHeader(DataEnd, True)

        End Sub

        Private Function GetLiveEntriesSortedByOffset() As List(Of DefragLiveEntry)

            Dim Result As New List(Of DefragLiveEntry)()

            For ChunkIndex = 0 To _Index.Count - 1
                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then Continue For
                If Entry.Offset < DataStartOffset Then Throw New InvalidDataException($"Invalid chunk offset for chunk {ChunkIndex}.")
                If Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid chunk record length for chunk {ChunkIndex}.")

                Result.Add(New DefragLiveEntry With {
                    .ChunkIndex = ChunkIndex,
                    .Offset = Entry.Offset,
                    .RecordLength = Entry.RecordLength
                })
            Next

            Result.Sort(Function(left, right) left.Offset.CompareTo(right.Offset))

            Return Result

        End Function

        Private Function GetDeadHoles(LiveEntries As List(Of DefragLiveEntry)) As List(Of DefragHole)

            Dim Holes As New List(Of DefragHole)()
            Dim Cursor = CLng(DataStartOffset)

            For Each Entry In LiveEntries
                If Entry.Offset > Cursor Then
                    Holes.Add(New DefragHole With {
                        .Offset = Cursor,
                        .Length = Entry.Offset - Cursor
                    })
                End If

                Cursor = Math.Max(Cursor, Entry.Offset + CLng(Entry.RecordLength))
            Next

            Return Holes

        End Function

        Private Sub ReportDefragProgress(ProgressCallback As StreamProgressCallback,
                                         ProcessedUnits As Long,
                                         TotalUnits As Long,
                                         UnitType As ProcessUnitTypes,
                                         CancellationToken As CancellationToken)

            If ProgressCallback Is Nothing Then Return

            ProgressCallback(ProcessedUnits, TotalUnits, UnitType, CancellationToken)

        End Sub

        Private Function ShouldUseCompressed(PlainLength As Integer, CompressedLength As Integer) As Boolean

            If PlainLength <= 0 Then Return False
            If CompressedLength <= 0 OrElse CompressedLength >= PlainLength Then Return False

            Dim SavedPercent = ((PlainLength - CompressedLength) * 100.0R) / PlainLength
            Return SavedPercent >= Options.CompressionMinimumSavingsPercent

        End Function

        Private Sub MarkCompressionFlag(Method As ChunkedStreamOptions.CompressionMethods)

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionLz4
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionDeflate
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionGZip
            End Select

        End Sub

        Private Shared Function CompressPayload(Method As ChunkedStreamOptions.CompressionMethods, Input As Byte(), Count As Integer) As Byte()

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    Return Lz4Block.Compress(Input, 0, Count)
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    Return CompressWithFrameworkStream(Input, Count, ChunkedStreamOptions.CompressionMethods.Deflate)
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    Return CompressWithFrameworkStream(Input, Count, ChunkedStreamOptions.CompressionMethods.GZip)
                Case Else
                    Dim Output(Count - 1) As Byte
                    System.Buffer.BlockCopy(Input, 0, Output, 0, Count)
                    Return Output
            End Select

        End Function

        Private Shared Function DecompressPayload(Method As ChunkedStreamOptions.CompressionMethods, Input As Byte(), ExpectedLength As Integer) As Byte()

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.None
                    If Input.Length <> ExpectedLength Then Throw New InvalidDataException("Uncompressed data length does not match expected plain length.")
                    Return Input
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    Return Lz4Block.Decompress(Input, ExpectedLength)
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, ChunkedStreamOptions.CompressionMethods.Deflate)
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, ChunkedStreamOptions.CompressionMethods.GZip)
                Case Else
                    Throw New InvalidDataException($"Unsupported chunk compression method: {CInt(Method)}.")
            End Select

        End Function

        Private Shared Function CompressWithFrameworkStream(Input As Byte(), Count As Integer, Method As ChunkedStreamOptions.CompressionMethods) As Byte()

            Using Output As New MemoryStream()
                If Method = ChunkedStreamOptions.CompressionMethods.GZip Then
                    Using Compressor As New GZipStream(Output, CompressionLevel.Fastest, True)
                        Compressor.Write(Input, 0, Count)
                    End Using
                Else
                    Using Compressor As New DeflateStream(Output, CompressionLevel.Fastest, True)
                        Compressor.Write(Input, 0, Count)
                    End Using
                End If

                Return Output.ToArray()
            End Using

        End Function

        Private Shared Function DecompressWithFrameworkStream(Input As Byte(), ExpectedLength As Integer, Method As ChunkedStreamOptions.CompressionMethods) As Byte()

            If ExpectedLength = 0 Then
                If Input.Length <> 0 Then Throw New InvalidDataException("Compressed data exists for zero-length output.")
                Return New Byte() {}
            End If

            Dim Output(ExpectedLength - 1) As Byte

            Using InputMs As New MemoryStream(Input)
                If Method = ChunkedStreamOptions.CompressionMethods.GZip Then
                    Using Decompressor As New GZipStream(InputMs, CompressionMode.Decompress)
                        ReadExactlyFromDecompressor(Decompressor, Output, ExpectedLength)
                    End Using
                Else
                    Using Decompressor As New DeflateStream(InputMs, CompressionMode.Decompress)
                        ReadExactlyFromDecompressor(Decompressor, Output, ExpectedLength)
                    End Using
                End If
            End Using

            Return Output

        End Function

        Private Shared Sub ReadExactlyFromDecompressor(Source As Stream, Output As Byte(), ExpectedLength As Integer)

            Dim TotalRead = 0

            While TotalRead < ExpectedLength
                Dim ReadBytes = Source.Read(Output, TotalRead, ExpectedLength - TotalRead)

                If ReadBytes = 0 Then Throw New InvalidDataException("Compressed stream ended before expected length.")

                TotalRead += ReadBytes
            End While

            If Source.ReadByte() <> -1 Then Throw New InvalidDataException("Compressed stream contains trailing data.")

        End Sub

        Private Shared Function IsAllZero(Buffer As Byte(), Count As Integer) As Boolean

            For i = 0 To Count - 1
                If Buffer(i) <> 0 Then Return False
            Next

            Return True

        End Function

        Private Sub EnsureIndexSize(RequiredCount As Integer)

            If RequiredCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(RequiredCount))

            While _Index.Count < RequiredCount
                _Index.Add(New ChunkIndexEntry())
            End While

        End Sub

        Private Shared Function GetRequiredChunkCount(Length As Long) As Integer

            If Length <= 0 Then Return 0

            Dim Count = ((Length - 1) \ ChunkSize) + 1

            If Count > Integer.MaxValue Then Throw New InvalidDataException("Encrypted stream has too many chunks for this implementation.")

            Return CInt(Count)

        End Function

        Private Function GetDataEndFromIndex() As Long

            Dim DataEnd = CLng(DataStartOffset)

            For Each entry In _Index
                If entry.Offset > 0 AndAlso entry.RecordLength > 0 Then
                    DataEnd = Math.Max(DataEnd, entry.Offset + CLng(entry.RecordLength))
                End If
            Next

            Return DataEnd

        End Function

        Private Sub PersistIndexAndHeader(IndexOffset As Long,
                                          Optional Durable As Boolean = False)

            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid index offset.")

            _IndexOffset = IndexOffset

            Dim NewLength = _IndexOffset + CLng(_Index.Count) * IndexEntrySize

            _Fs.Position = _IndexOffset

            Dim EntryBuffer(IndexEntrySize - 1) As Byte

            For Each entry In _Index
                Array.Clear(EntryBuffer, 0, EntryBuffer.Length)

                System.Buffer.BlockCopy(BitConverter.GetBytes(entry.Offset), 0, EntryBuffer, 0, 8)
                System.Buffer.BlockCopy(BitConverter.GetBytes(entry.RecordLength), 0, EntryBuffer, 8, 4)

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

            Dim IndexMac = ComputeIndexMac(_Index, _MacKey)
            System.Buffer.BlockCopy(IndexMac, 0, _Header, IndexMacOffset, MacSize)

            WriteHeaderCopies(Durable)

        End Sub

        Private Sub WriteHeaderCopies(Durable As Boolean)

            _HeaderSequence += 1
            System.Buffer.BlockCopy(BitConverter.GetBytes(_HeaderSequence), 0, _Header, HeaderSequenceOffset, 8)

            WriteHeaderMac(_Header, _MacKey)

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
                Throw New InvalidDataException("Encrypted stream index table extends beyond end of stream.")
            End If

            Fs.Position = IndexOffset

            Dim EntryBuffer(IndexEntrySize - 1) As Byte

            For i = 0 To IndexCount - 1
                ReadExactly(Fs, EntryBuffer, 0, EntryBuffer.Length)

                Dim Entry As New ChunkIndexEntry With {
                    .Offset = BitConverter.ToInt64(EntryBuffer, 0),
                    .RecordLength = BitConverter.ToInt32(EntryBuffer, 8)
                }

                If Entry.Offset <> 0 OrElse Entry.RecordLength <> 0 Then
                    If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid index entry {i}.")
                    If Entry.Offset + Entry.RecordLength > IndexOffset Then Throw New InvalidDataException($"Index entry {i} points outside the encrypted data area.")
                End If

                Index.Add(Entry)
            Next

            Return Index

        End Function

        Private Shared Function ComputeIndexMac(Index As List(Of ChunkIndexEntry), MacKey As Byte()) As Byte()

            Using Hmac As New HMACSHA256(MacKey)
                Dim EntryBuffer(IndexEntrySize - 1) As Byte

                For Each entry In Index
                    Array.Clear(EntryBuffer, 0, EntryBuffer.Length)

                    System.Buffer.BlockCopy(BitConverter.GetBytes(entry.Offset), 0, EntryBuffer, 0, 8)
                    System.Buffer.BlockCopy(BitConverter.GetBytes(entry.RecordLength), 0, EntryBuffer, 8, 4)

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

        Private Enum KeyPurpose
            Mac
            Enc
        End Enum

        Private Shared Function DeriveKey(MasterKey As Byte(), FileSalt As Byte(), Purpose As KeyPurpose) As Byte()

            Dim PurposeBytes = Encoding.ASCII.GetBytes(Purpose.ToString())
            Dim Material(PurposeBytes.Length + FileSalt.Length - 1) As Byte

            System.Buffer.BlockCopy(PurposeBytes, 0, Material, 0, PurposeBytes.Length)
            System.Buffer.BlockCopy(FileSalt, 0, Material, PurposeBytes.Length, FileSalt.Length)

            Using Hmac = New HMACSHA256(MasterKey)
                Return Hmac.ComputeHash(Material)
            End Using

        End Function

        Private Shared Sub WriteHeaderMac(Header As Byte(), MacKey As Byte())

            Array.Clear(Header, HeaderMacOffset, MacSize)

            Using Hmac = New HMACSHA256(MacKey)
                Dim Mac = Hmac.ComputeHash(Header, 0, HeaderMacCoveredSize)
                System.Buffer.BlockCopy(Mac, 0, Header, HeaderMacOffset, MacSize)
            End Using

        End Sub

        Private Shared Function VerifyHeaderMac(Header As Byte(), MacKey As Byte()) As Boolean

            Using Hmac = New HMACSHA256(MacKey)
                Dim ExpectedMac = Hmac.ComputeHash(Header, 0, HeaderMacCoveredSize)
                Return FixedTimeEquals(ExpectedMac, 0, Header, HeaderMacOffset, MacSize)
            End Using

        End Function

        Private Shared Sub ReadExactly(Source As Stream,
                                       Buffer As Byte(),
                                       Offset As Integer,
                                       Count As Integer)

            Dim TotalRead = 0

            While TotalRead < Count
                Dim ReadBytes = Source.Read(Buffer, Offset + TotalRead, Count - TotalRead)

                If ReadBytes = 0 Then Throw New EndOfStreamException("Unexpected end of encrypted stream.")

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

            For i = 0 To Count - 1
                Diff = Diff Or (CInt(Left(LeftOffset + i)) Xor CInt(Right(RightOffset + i)))
            Next

            Return Diff = 0

        End Function

        Private Shared Sub IncrementCounter(Counter As Byte())

            For index = Counter.Length - 1 To 0 Step -1
                Counter(index) = CByte((CInt(Counter(index)) + 1) And &HFF)

                If Counter(index) <> 0 Then Exit For
            Next

        End Sub

        Private Shared Sub FlushDurable(Target As Stream)

            Dim FileStream = TryCast(Target, FileStream)

            If FileStream IsNot Nothing Then
                FileStream.Flush(True)
                Return
            End If

            Target.Flush()

        End Sub

    End Class

End Namespace