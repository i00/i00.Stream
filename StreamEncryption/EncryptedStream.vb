Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text

Namespace Encryption

    ' ================================================================================
    ' EncryptedStream
    ' ================================================================================
    '
    ' Compatibility:
    '   - Designed for .NET Framework 4.8.
    '   - No Span(Of T), ArrayPool(Of T), RandomNumberGenerator.Fill, or newer runtime APIs.
    '   - Uses Byte() buffers and System.Buffer.BlockCopy.
    '   - Uses Rfc2898DeriveBytes with SHA256.
    '   - The underlying stream passed to Open() remains owned by the caller and is not disposed.
    '
    ' Header model:
    '   - Two 512-byte header copies are stored at the beginning of the stream.
    '   - Header 0 is at offset 0.
    '   - Header 1 is at offset 512.
    '   - Data records begin at offset 1024.
    '   - On open, both headers are validated and the valid header with the highest sequence wins.
    '   - This protects against process death during a header write.
    '
    ' Header:
    '   Bytes 0..7:
    '       Magic value: "ESTRM001"
    '
    '   Bytes 8..15:
    '       Header sequence number as Int64
    '
    '   Bytes 16..23:
    '       Flags as Int64
    '
    '   Bytes 24..31:
    '       Plaintext file length as Int64
    '
    '   Bytes 32..47:
    '       Per-file random salt used to derive the encryption and MAC keys
    '
    '   Bytes 48..51:
    '       Chunk size as Int32
    '
    '   Bytes 52..59:
    '       Chunk index table offset as Int64
    '
    '   Bytes 60..67:
    '       Chunk index table entry count as Int64
    '
    '   Bytes 68..99:
    '       HMAC-SHA256 of the current index table entries
    '
    '   Bytes 128..191:
    '       Defrag journal
    '
    '   Bytes 480..511:
    '       Header HMAC-SHA256 over bytes 0..479
    '
    ' Features:
    '   - Random access read/write.
    '   - Variable-sized encrypted chunk records.
    '   - Authenticated header, index and chunk records.
    '   - Optional per-chunk LZ4, Deflate or GZip compression.
    '   - Sparse chunks can be omitted from storage.
    '   - Append-on-write chunk updates.
    '   - Defrag with Move, Sequence and Rebuild modes.
    '   - Defrag journal recovery during Open().
    '   - Thread-safe per instance using SyncLock.
    '
    ' Crash recovery:
    '   - Defrag moves are journaled one chunk at a time.
    '   - If the process is killed during a move, Open() recovers or rolls back that one move.
    '   - Old chunk records are not erased during moves; they simply become unreferenced.
    '
    ' ================================================================================

    Friend Class General

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

    Friend Enum CompressionMethod As Integer
        None = 0
        Lz4 = 1
        Deflate = 2
        GZip = 3
    End Enum

    Friend Enum DefragType
        Move = 0
        Sequence = 1
        Rebuild = 2
    End Enum

    Friend NotInheritable Class CancellationToken
        Public Property Cancel As Boolean
    End Class

    Friend Delegate Sub StreamProgressCallback(BytesProcessed As Long,
                                               TotalBytes As Long,
                                               CancellationToken As CancellationToken)

    Friend Class EncryptedStreamOptions
        Public Property CompressionMethod As CompressionMethod = CompressionMethod.None
        Public Property CompressionMinimumSavingsPercent As Integer = 5
        Public Property StoreSparseChunks As Boolean = False
    End Class

    Friend NotInheritable Class EncryptedStream
        Implements IDisposable

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

        Private Const JournalStateOffset As Integer = 128
        Private Const JournalChunkIndexOffset As Integer = 136
        Private Const JournalOldOffsetOffset As Integer = 144
        Private Const JournalOldLengthOffset As Integer = 152
        Private Const JournalNewOffsetOffset As Integer = 160
        Private Const JournalNewLengthOffset As Integer = 168

        Private Const JournalAreaOffset As Integer = 128
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

        Private ReadOnly _Fs As Stream
        Private ReadOnly _EncryptionKey As Byte()
        Private ReadOnly _MacKey As Byte()
        Private ReadOnly _SyncRoot As New Object()
        Private ReadOnly _Options As EncryptedStreamOptions

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
        Private _IndexOffset As Long
        Private _Length As Long
        Private _Disposed As Boolean

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
                        EncryptionKey As Byte(),
                        MacKey As Byte(),
                        Length As Long,
                        IndexOffset As Long,
                        Index As List(Of ChunkIndexEntry),
                        HeaderFlags As HeaderFlags,
                        Options As EncryptedStreamOptions)

            _Fs = Fs
            _Header = Header
            _HeaderSequence = HeaderSequence
            _EncryptionKey = EncryptionKey
            _MacKey = MacKey
            _Length = Length
            _IndexOffset = IndexOffset
            _Index = Index
            _HeaderFlags = HeaderFlags
            _Options = If(Options, New EncryptedStreamOptions())

            If _Options.CompressionMinimumSavingsPercent < 0 Then _Options.CompressionMinimumSavingsPercent = 0
            If _Options.CompressionMinimumSavingsPercent > 100 Then _Options.CompressionMinimumSavingsPercent = 100

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

        Public Shared Function Open(Fs As Stream, MasterKey As Byte()) As EncryptedStream
            Return Open(Fs, MasterKey, Nothing)
        End Function

        Public Shared Function Open(Fs As Stream,
                                    MasterKey As Byte(),
                                    Options As EncryptedStreamOptions) As EncryptedStream

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

            Dim Result = New EncryptedStream(Fs, Header, HeaderSequence, EncryptionKey, MacKey, FileLength, IndexOffset, Index, Flags, Options)
            Result.RecoverDefragJournal()
            Return Result

        End Function

        Private Shared Function CreateNew(Fs As Stream,
                                          MasterKey As Byte(),
                                          Options As EncryptedStreamOptions) As EncryptedStream

            Dim Header(HeaderSize - 1) As Byte
            Dim EffectiveOptions = If(Options, New EncryptedStreamOptions())
            Dim Flags = HeaderFlags.VariableChunkIndex

            If Not EffectiveOptions.StoreSparseChunks Then
                Flags = Flags Or HeaderFlags.SparseChunks
            End If

            Select Case EffectiveOptions.CompressionMethod
                Case CompressionMethod.Lz4
                    Flags = Flags Or HeaderFlags.CompressionLz4
                Case CompressionMethod.Deflate
                    Flags = Flags Or HeaderFlags.CompressionDeflate
                Case CompressionMethod.GZip
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

            Dim EncryptionKey = DeriveKey(MasterKey, FileSalt, "ENC")
            Dim MacKey = DeriveKey(MasterKey, FileSalt, "MAC")
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

            Return New EncryptedStream(Fs, Header, 1L, EncryptionKey, MacKey, 0L, DataStartOffset, Index, Flags, EffectiveOptions)

        End Function

        Private Structure HeaderCandidate
            Public Header As Byte()
            Public HeaderSequence As Long
            Public EncryptionKey As Byte()
            Public MacKey As Byte()
        End Structure

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

                Dim EncryptionKey = DeriveKey(MasterKey, FileSalt, "ENC")
                Dim MacKey = DeriveKey(MasterKey, FileSalt, "MAC")

                If Not VerifyHeaderMac(Header, MacKey) Then
                    Continue For
                End If

                Dim HeaderSequence = BitConverter.ToInt64(Header, HeaderSequenceOffset)

                If Best.Header Is Nothing OrElse HeaderSequence > Best.HeaderSequence Then
                    Best.Header = Header
                    Best.HeaderSequence = HeaderSequence
                    Best.EncryptionKey = EncryptionKey
                    Best.MacKey = MacKey
                End If
            Next

            Return Best

        End Function

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

        Public Sub Flush()

            SyncLock _SyncRoot
                ThrowIfDisposed()
                If _Fs.CanWrite Then _Fs.Flush()
            End SyncLock

        End Sub

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
        ''' Throws an exception if validation fails.
        ''' </summary>
        ''' <exception cref="InvalidDataException">
        ''' Thrown when stream contents are invalid.
        ''' </exception>
        Public Sub Validate(Optional ProgressCallback As StreamProgressCallback = Nothing)

            SyncLock _SyncRoot

                ThrowIfDisposed()

                Dim CancellationToken As New CancellationToken()

                ValidateAllLiveChunkRecords(
                    ProgressCallback,
                    CancellationToken)

            End SyncLock

        End Sub

        Public Function GenerateFragmentationBitmap(Width As Integer,
                                                    Height As Integer) As Bitmap

            SyncLock _SyncRoot

                ThrowIfDisposed()

                If Width <= 0 Then
                    Throw New ArgumentOutOfRangeException(NameOf(Width))
                End If

                If Height <= 0 Then
                    Throw New ArgumentOutOfRangeException(NameOf(Height))
                End If

                Dim Result As New Bitmap(
                    Width,
                    Height,
                    Imaging.PixelFormat.Format24bppRgb)

                Using Graphics = System.Drawing.Graphics.FromImage(Result)

                    Graphics.Clear(Color.Black)

                End Using

                Dim DataEnd = GetDataEndFromIndex()

                If DataEnd <= DataStartOffset Then
                    Return Result
                End If

                Dim TotalPixels = Width * Height
                Dim TotalBytes = DataEnd

                '
                ' Build segment list.
                '
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

                '
                ' Sort by position.
                '
                Segments.Sort(
                    Function(left, right)
                        Return left.Item1.CompareTo(right.Item1)
                    End Function)

                '
                ' Fill gaps (dead space).
                '
                Dim RenderSegments As New List(Of Tuple(Of Long,
                                                        Long,
                                                        Color))

                Dim Cursor As Long = DataStartOffset

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
                ' Draw.
                '
                For Each Segment In RenderSegments

                    Dim StartPixel =
                        CInt(
                            Math.Floor(
                                (Segment.Item1 / CDbl(TotalBytes)) *
                                TotalPixels))

                    Dim EndPixel =
                        CInt(
                            Math.Ceiling(
                                (Segment.Item2 / CDbl(TotalBytes)) *
                                TotalPixels))

                    If EndPixel <= StartPixel Then
                        EndPixel = StartPixel + 1
                    End If

                    If EndPixel > TotalPixels Then
                        EndPixel = TotalPixels
                    End If

                    For PixelIndex = StartPixel To EndPixel - 1

                        Dim X = PixelIndex Mod Width
                        Dim Y = PixelIndex \ Width

                        If X >= 0 AndAlso
                           X < Width AndAlso
                           Y >= 0 AndAlso
                           Y < Height Then

                            Result.SetPixel(
                                X,
                                Y,
                                Segment.Item3)

                        End If

                    Next

                Next

                Return Result

            End SyncLock

        End Function

        Public Function Defragment(Optional Type As DefragType = DefragType.Move,
                                   Optional ProgressCallback As StreamProgressCallback = Nothing) As Long

            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim OriginalLength = _Fs.Length
                Dim CancellationToken As New CancellationToken()

                Select Case Type
                    Case DefragType.Move
                        DefragmentMove(ProgressCallback, CancellationToken)
                    Case DefragType.Sequence
                        DefragmentSequence(False, ProgressCallback, CancellationToken)
                    Case DefragType.Rebuild
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

            If PlainLength = 0 OrElse (Not _Options.StoreSparseChunks AndAlso IsAllZero(Plain, PlainLength)) Then
                EnsureIndexSize(CInt(ChunkIndex + 1))
                _Index(CInt(ChunkIndex)) = New ChunkIndexEntry()
                _HeaderFlags = _HeaderFlags Or HeaderFlags.SparseChunks
                Return
            End If

            Dim Payload As Byte() = Plain
            Dim PayloadLength = PlainLength
            Dim Method = CompressionMethod.None

            If _Options.CompressionMethod <> CompressionMethod.None Then
                Dim Compressed = CompressPayload(_Options.CompressionMethod, Plain, PlainLength)

                If ShouldUseCompressed(PlainLength, Compressed.Length) Then
                    Payload = Compressed
                    PayloadLength = Compressed.Length
                    Method = _Options.CompressionMethod
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
            If ChunkIndex <> ExpectedChunkIndex Then Throw New CryptographicException($"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {ChunkIndex}.")

            Dim Method = CType(BitConverter.ToInt32(Record, 8), CompressionMethod)
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

        Private Sub ValidateAllLiveChunkRecords(
                    ProgressCallback As StreamProgressCallback,
                    CancellationToken As CancellationToken)

            Dim TotalChunks =
                        _Index.Count

            Dim ProcessedChunks As Long = 0

            For ChunkIndex = 0 To _Index.Count - 1

                If CancellationToken.Cancel Then
                    Return
                End If

                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso
                           Entry.RecordLength = 0 Then

                    Continue For

                End If

                ValidateChunkRecord(
                            ChunkIndex)

                ProcessedChunks += 1

                ReportDefragProgress(
                            ProgressCallback,
                            ProcessedChunks,
                            Math.Max(1, TotalChunks),
                            CancellationToken)

            Next

        End Sub

        Private Sub ValidateChunkRecord(ExpectedChunkIndex As Integer)

            If ExpectedChunkIndex < 0 OrElse
               ExpectedChunkIndex >= _Index.Count Then

                Throw New ArgumentOutOfRangeException(
                    NameOf(ExpectedChunkIndex))

            End If

            Dim Entry = _Index(ExpectedChunkIndex)

            If Entry.Offset = 0 AndAlso
               Entry.RecordLength = 0 Then

                Return

            End If

            If Entry.Offset < DataStartOffset Then
                Throw New InvalidDataException(
                    $"Invalid chunk offset for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.RecordLength < MinChunkRecordSize Then
                Throw New InvalidDataException(
                    $"Invalid chunk length for chunk {ExpectedChunkIndex}.")
            End If

            If Entry.Offset + Entry.RecordLength > _IndexOffset Then
                Throw New InvalidDataException(
                    $"Chunk {ExpectedChunkIndex} extends beyond data area.")
            End If

            Dim Record(Entry.RecordLength - 1) As Byte

            _Fs.Position = Entry.Offset

            ReadExactly(
                _Fs,
                Record,
                0,
                Record.Length)

            Dim ChunkIndex =
                BitConverter.ToInt64(
                    Record,
                    0)

            If ChunkIndex <> ExpectedChunkIndex Then

                Throw New InvalidDataException(
                    $"Chunk index mismatch. Expected {ExpectedChunkIndex}, found {ChunkIndex}.")

            End If

            Dim DataLength =
                BitConverter.ToInt32(
                    Record,
                    16)

            If DataLength < 0 Then

                Throw New InvalidDataException(
                    "Invalid chunk data length.")

            End If

            If ChunkRecordDataOffset +
               DataLength +
               MacSize <> Record.Length Then

                Throw New InvalidDataException(
                    "Invalid chunk record length.")
            End If

            _ChunkHmac.Initialize()

            Dim ExpectedMac =
                _ChunkHmac.ComputeHash(
                    Record,
                    0,
                    ChunkRecordDataOffset + DataLength)

            If Not FixedTimeEquals(
                ExpectedMac,
                0,
                Record,
                ChunkRecordDataOffset + DataLength,
                MacSize) Then

                Throw New CryptographicException(
                    $"Chunk MAC invalid for chunk {ExpectedChunkIndex}.")

            End If

        End Sub

        Private Sub DefragmentMove(ProgressCallback As StreamProgressCallback,
                                   CancellationToken As CancellationToken)

            Dim InitialFragmentation = GetFragmentation()

            Dim TotalProgressUnits =
                Math.Max(1L,
                         CLng(InitialFragmentation * 1000000.0R))

            ReportDefragProgress(
                ProgressCallback,
                0,
                TotalProgressUnits,
                CancellationToken)

            Do

                If CancellationToken.Cancel Then
                    Return
                End If

                Dim LiveEntries = GetLiveEntriesSortedByOffset()
                Dim Holes = GetDeadHoles(LiveEntries)

                If Holes.Count = 0 Then
                    Exit Do
                End If

                Dim MovedSomething = False

                For Each Hole In Holes

                    If CancellationToken.Cancel Then
                        Return
                    End If

                    Dim CandidateIndex =
                        FindLatestLiveEntryThatFitsHole(
                            LiveEntries,
                            Hole)

                    If CandidateIndex < 0 Then
                        Continue For
                    End If

                    Dim Candidate = LiveEntries(CandidateIndex)

                    MoveChunkRecordJournaled(
                        Candidate.ChunkIndex,
                        Hole.Offset)

                    MovedSomething = True

                    Dim CurrentFragmentation =
                        GetFragmentation()

                    Dim CurrentUnits =
                        CLng(CurrentFragmentation * 1000000.0R)

                    Dim CompletedUnits =
                        TotalProgressUnits - CurrentUnits

                    If CompletedUnits < 0 Then
                        CompletedUnits = 0
                    End If

                    If CompletedUnits > TotalProgressUnits Then
                        CompletedUnits = TotalProgressUnits
                    End If

                    ReportDefragProgress(
                        ProgressCallback,
                        CompletedUnits,
                        TotalProgressUnits,
                        CancellationToken)

                    Exit For

                Next

                If Not MovedSomething Then
                    Exit Do
                End If

            Loop

            CommitDefragCheckpoint(GetDataEndFromIndex())

            ReportDefragProgress(
                ProgressCallback,
                TotalProgressUnits,
                TotalProgressUnits,
                CancellationToken)

        End Sub

        Private Function FindLatestLiveEntryThatFitsHole(LiveEntries As List(Of DefragLiveEntry),
                                                         Hole As DefragHole) As Integer

            For Index = LiveEntries.Count - 1 To 0 Step -1

                Dim Entry = LiveEntries(Index)

                If Entry.Offset <= Hole.Offset Then
                    Exit For
                End If

                If Entry.RecordLength <= Hole.Length Then
                    Return Index
                End If

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
                ReportDefragProgress(ProgressCallback, ProcessedBytes, TotalBytes, CancellationToken)

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
                ReportDefragProgress(ProgressCallback, ProcessedBytes, TotalBytes, CancellationToken)
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
                ReportDefragProgress(ProgressCallback, Entry.RecordLength, Entry.RecordLength, CancellationToken)
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

            If DataEnd < DataStartOffset Then
                Throw New InvalidDataException("Invalid defrag data end.")
            End If

            PersistIndexAndHeader(DataEnd, True)

        End Sub

        Private Function GetLiveEntriesSortedByOffset() As List(Of DefragLiveEntry)

            Dim Result As New List(Of DefragLiveEntry)()

            For ChunkIndex = 0 To _Index.Count - 1
                Dim Entry = _Index(ChunkIndex)

                If Entry.Offset = 0 AndAlso Entry.RecordLength = 0 Then
                    Continue For
                End If

                If Entry.Offset < DataStartOffset Then
                    Throw New InvalidDataException($"Invalid chunk offset for chunk {ChunkIndex}.")
                End If

                If Entry.RecordLength < MinChunkRecordSize Then
                    Throw New InvalidDataException($"Invalid chunk record length for chunk {ChunkIndex}.")
                End If

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
                                         BytesProcessed As Long,
                                         TotalBytes As Long,
                                         CancellationToken As CancellationToken)

            If ProgressCallback Is Nothing Then Return

            ProgressCallback(BytesProcessed, TotalBytes, CancellationToken)

        End Sub

        Private Function ShouldUseCompressed(PlainLength As Integer, CompressedLength As Integer) As Boolean

            If PlainLength <= 0 Then Return False
            If CompressedLength <= 0 OrElse CompressedLength >= PlainLength Then Return False

            Dim SavedPercent = ((PlainLength - CompressedLength) * 100.0R) / PlainLength
            Return SavedPercent >= _Options.CompressionMinimumSavingsPercent

        End Function

        Private Sub MarkCompressionFlag(Method As CompressionMethod)

            Select Case Method
                Case CompressionMethod.Lz4
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionLz4
                Case CompressionMethod.Deflate
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionDeflate
                Case CompressionMethod.GZip
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionGZip
            End Select

        End Sub

        Private Shared Function CompressPayload(Method As CompressionMethod, Input As Byte(), Count As Integer) As Byte()

            Select Case Method
                Case CompressionMethod.Lz4
                    Return Lz4Block.Compress(Input, 0, Count)
                Case CompressionMethod.Deflate
                    Return CompressWithFrameworkStream(Input, Count, CompressionMethod.Deflate)
                Case CompressionMethod.GZip
                    Return CompressWithFrameworkStream(Input, Count, CompressionMethod.GZip)
                Case Else
                    Dim Output(Count - 1) As Byte
                    System.Buffer.BlockCopy(Input, 0, Output, 0, Count)
                    Return Output
            End Select

        End Function

        Private Shared Function DecompressPayload(Method As CompressionMethod, Input As Byte(), ExpectedLength As Integer) As Byte()

            Select Case Method
                Case CompressionMethod.None
                    If Input.Length <> ExpectedLength Then Throw New InvalidDataException("Uncompressed data length does not match expected plain length.")
                    Return Input
                Case CompressionMethod.Lz4
                    Return Lz4Block.Decompress(Input, ExpectedLength)
                Case CompressionMethod.Deflate
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, CompressionMethod.Deflate)
                Case CompressionMethod.GZip
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, CompressionMethod.GZip)
                Case Else
                    Throw New InvalidDataException($"Unsupported chunk compression method: {CInt(Method)}.")
            End Select

        End Function

        Private Shared Function CompressWithFrameworkStream(Input As Byte(), Count As Integer, Method As CompressionMethod) As Byte()

            Using Output As New MemoryStream()
                If Method = CompressionMethod.GZip Then
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

        Private Shared Function DecompressWithFrameworkStream(Input As Byte(), ExpectedLength As Integer, Method As CompressionMethod) As Byte()

            If ExpectedLength = 0 Then
                If Input.Length <> 0 Then Throw New InvalidDataException("Compressed data exists for zero-length output.")
                Return New Byte() {}
            End If

            Dim Output(ExpectedLength - 1) As Byte

            Using InputMs As New MemoryStream(Input)
                If Method = CompressionMethod.GZip Then
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

            If IndexOffset < DataStartOffset Then
                Throw New InvalidDataException("Invalid index offset.")
            End If

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

            If Durable Then
                FlushDurable(_Fs)
            End If

            UpdateHeader(Durable)

            _Fs.SetLength(NewLength)

            If Durable Then
                FlushDurable(_Fs)
            End If

        End Sub

        Private Sub UpdateHeader(Optional Durable As Boolean = False)

            _HeaderFlags = _HeaderFlags Or HeaderFlags.VariableChunkIndex

            If Not _Options.StoreSparseChunks Then
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

            _Fs.Position = 0
            _Fs.Write(_Header, 0, _Header.Length)

            _Fs.Position = HeaderSize
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

            If _Disposed Then Throw New ObjectDisposedException(GetType(EncryptedStream).FullName)

        End Sub

        Private Shared Sub RandomNumberGeneratorFill(Buffer As Byte(), Offset As Integer, Length As Integer)

            Using Rng = RandomNumberGenerator.Create()
                Dim Temp(Length - 1) As Byte
                Rng.GetBytes(Temp)
                System.Buffer.BlockCopy(Temp, 0, Buffer, Offset, Length)
            End Using

        End Sub

        Private Shared Function DeriveKey(MasterKey As Byte(), FileSalt As Byte(), Purpose As String) As Byte()

            Dim PurposeBytes = Encoding.ASCII.GetBytes(Purpose)
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