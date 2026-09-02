' ================================================================================
' ChunkedStream Crypto
' ================================================================================
'
' Purpose
'   - Encryption, key derivation, key wrapping and chunk cryptography.
'
' Design
'   - A file master key protects encrypted chunk data.
'   - Chunk encryption and MAC keys are derived from the file master key.
'   - AES-CTR is used for chunk encryption.
'   - HMAC-SHA256 authenticates encrypted chunk records.
'
' File Master Key
'   - May be publicly wrapped.
'   - May be wrapped using user-supplied encryption information.
'   - Can be removed automatically when no encrypted chunks remain.
'
' Notes
'   - Compression occurs before encryption.
'   - Decryption occurs before decompression.
'
' ================================================================================

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

Namespace Streams

    Partial Class ChunkedStream

        ''' <summary>
        ''' Encryption state stored on an individual chunk record.
        ''' </summary>
        Public Enum ChunkEncryptionMethods As Integer

            ''' <summary>
            ''' The chunk payload is not encrypted.
            ''' </summary>
            None = 0

            ''' <summary>
            ''' The chunk payload is encrypted using keys derived from the file master key.
            ''' </summary>
            AesCtrFileMasterKey = 1

        End Enum

        ''' <summary>
        ''' How the file master key is wrapped in the header.
        ''' </summary>
        Private Enum MasterKeyWrapModes As Integer

            ''' <summary>
            ''' No file master key exists.
            ''' </summary>
            None = 0

            ''' <summary>
            ''' The file master key is wrapped using the public integrity key.
            ''' </summary>
            PublicWrap = 1

            ''' <summary>
            ''' The file master key is wrapped using user-supplied encryption information.
            ''' </summary>
            UserWrap = 2

        End Enum

        Private Enum KeyPurpose
            Mac
            Enc
        End Enum

        ''' <summary>
        ''' Describes the user key used to protect the stream file master key.
        ''' </summary>
        Public NotInheritable Class EncryptionInfo

            ''' <summary>
            ''' Default PBKDF2 iteration count used by the passphrase-based key derivation
            ''' when no explicit iteration count is supplied.
            ''' </summary>
            Public Shared Property DefaultPBKDF2Iterations As Integer = 600000

            ''' <summary>
            ''' Derives a 256-bit key from a passphrase using PBKDF2-SHA256.
            ''' </summary>
            ''' <param name="Passphrase">The passphrase to derive from.</param>
            ''' <param name="Salt">The salt. If shorter than eight bytes, it is padded for PBKDF2 compatibility.</param>
            ''' <param name="Iterations">The PBKDF2 iteration count.</param>
            ''' <returns>A 32-byte key.</returns>
            ''' <remarks>
            ''' Salt is optional and rarely needed. The file master key is always wrapped using a
            ''' random, per-file WrapSalt (stored in the header, generated automatically) - so two
            ''' files encrypted with the same passphrase and no Salt here still get distinct wrapped
            ''' keys on disk. The same passphrase will still open either file - that's inherent to
            ''' passphrase-based wrapping, not something Salt controls.
            '''
            ''' An explicit Salt only adds resistance to precomputed (rainbow-table) attacks against
            ''' the PBKDF2 step itself, amortised across many files/deployments sharing the same
            ''' default. At 600,000 iterations that's already an expensive attack; Salt exists for
            ''' callers who want extra defence-in-depth on top of that, not because omitting it
            ''' weakens per-file security.
            ''' </remarks>
            Public Shared Function DeriveMasterKey(Passphrase As String,
                                                   Optional Salt As Byte() = Nothing,
                                                   Optional Iterations As Integer? = Nothing) As Byte()

                If Iterations.HasValue = False Then Iterations = DefaultPBKDF2Iterations

                If Passphrase Is Nothing Then Throw New ArgumentNullException(NameOf(Passphrase))

                ' DeriveMasterKey used to throw if the Salt was empty:
                '   If Salt Is Nothing Then Throw New ArgumentNullException(NameOf(Salt))
                ' But this was changed to remove this requirement, this is not an oversight
                ' per-file key uniqueness is already guaranteed by WrapSalt (see <remarks>)
                If Salt Is Nothing Then Salt = {}
                If Iterations <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Iterations), "Iterations must be greater than zero.")

                Dim EffectiveSalt = DirectCast(Salt.Clone(), Byte())

                If EffectiveSalt.Length < 8 Then
                    Array.Resize(EffectiveSalt, 8)
                End If

                Dim PasswordBytes = Encoding.UTF8.GetBytes(Passphrase)

                Try
                    Using Kdf As New Rfc2898DeriveBytes(PasswordBytes, EffectiveSalt, Iterations.Value, HashAlgorithmName.SHA256)
                        Return Kdf.GetBytes(32)
                    End Using
                Finally
                    Array.Clear(PasswordBytes, 0, PasswordBytes.Length)
                End Try

            End Function

            ''' <summary>
            ''' Derives a 256-bit key from a passphrase using PBKDF2-SHA256.
            ''' </summary>
            ''' <param name="Passphrase">The passphrase to derive from.</param>
            ''' <param name="Salt">The salt.</param>
            ''' <param name="Iterations">The PBKDF2 iteration count.</param>
            ''' <returns>A 32-byte key.</returns>
            Public Shared Function DeriveMasterKey(Passphrase As String,
                                                   Optional Salt As String = "",
                                                   Optional Iterations As Integer? = Nothing) As Byte()
                Return DeriveMasterKey(Passphrase, Encoding.UTF8.GetBytes(Salt & ""), Iterations)
            End Function

            Private ReadOnly _KeyMaterial As Byte()

            ''' <summary>
            ''' Creates encryption information from raw key bytes.
            ''' </summary>
            Public Sub New(KeyMaterial As Byte())

                If KeyMaterial Is Nothing Then Throw New ArgumentNullException(NameOf(KeyMaterial))
                If KeyMaterial.Length = 0 Then Throw New ArgumentException("Key material must not be empty.", NameOf(KeyMaterial))

                _KeyMaterial = DirectCast(KeyMaterial.Clone(), Byte())

            End Sub

            ''' <summary>
            ''' Creates encryption information from a Passphrase
            ''' </summary>
            Public Sub New(Passphrase As String, Salt As String, Optional Iterations As Integer? = Nothing)
                Me.New(DeriveMasterKey(Passphrase, Salt, Iterations))
            End Sub

            ''' <summary>
            ''' Creates encryption information from a Passphrase
            ''' </summary>
            Public Sub New(Passphrase As String, Optional Salt As Byte() = Nothing, Optional Iterations As Integer? = Nothing)
                Me.New(DeriveMasterKey(Passphrase, Salt, Iterations))
            End Sub

            ''' <summary>
            ''' Gets a defensive copy of the key material.
            ''' </summary>
            Friend Function GetKeyMaterial() As Byte()
                Return DirectCast(_KeyMaterial.Clone(), Byte())
            End Function

        End Class

        ''' <summary>
        ''' Thrown when supplied encryption options do not match the stream encryption state.
        ''' </summary>
        Public NotInheritable Class EncryptionMismatchException
            Inherits InvalidOperationException

            Friend Sub New(Message As String)
                MyBase.New(Message)
            End Sub

        End Class

        Private Sub InitialiseEncryptionForNewStream(EncryptionInfo As EncryptionInfo)

            If EncryptionInfo Is Nothing Then
                _CurrentWriteEncryptionEnabled = False
                Return
            End If

            If _FileMasterKey Is Nothing Then
                _FileMasterKey = New Byte(WrappedFileMasterKeySize - 1) {}
                _Rng.GetBytes(_FileMasterKey)
                DeriveFileMasterKeys()
            End If

            WrapFileMasterKey(MasterKeyWrapModes.UserWrap, EncryptionInfo)
            _CurrentWriteEncryptionEnabled = True
            UpdateHeader(True)

        End Sub

        Private Sub Options_EncryptionInfoChanged(OldValue As EncryptionInfo, NewValue As EncryptionInfo)

            Using EnterStateLock()
                Options_EncryptionInfoChangedCore(OldValue, NewValue)
            End Using

        End Sub

        Private Sub Options_EncryptionInfoChangedCore(OldValue As EncryptionInfo, NewValue As EncryptionInfo)

            ThrowIfDisposed()

            If NewValue Is Nothing Then
                If _FileMasterKey IsNot Nothing Then
                    WrapFileMasterKey(MasterKeyWrapModes.PublicWrap, Nothing)
                End If

                _CurrentWriteEncryptionEnabled = False
                UpdateHeader(True)
                Return
            End If

            If _FileMasterKey Is Nothing Then
                _FileMasterKey = New Byte(WrappedFileMasterKeySize - 1) {}
                _Rng.GetBytes(_FileMasterKey)
                DeriveFileMasterKeys()
            End If

            WrapFileMasterKey(MasterKeyWrapModes.UserWrap, NewValue)
            _CurrentWriteEncryptionEnabled = True
            UpdateHeader(True)

        End Sub

        Private Sub DeriveFileMasterKeys()

            If _FileMasterKey Is Nothing Then
                _ChunkEncryptionKey = Nothing
                _ChunkMacKey = Nothing
                RebuildChunkCipherTransform()
                Return
            End If

            Dim FileSalt = GetHeaderFileSalt()

            _ChunkEncryptionKey = DeriveKey(_FileMasterKey, FileSalt, KeyPurpose.Enc)
            _ChunkMacKey = DeriveKey(_FileMasterKey, FileSalt, KeyPurpose.Mac)

            RebuildChunkCipherTransform()

        End Sub

        '
        ' Rebuilds the cached AES-ECB encryptor for the current _ChunkEncryptionKey. Called
        ' only when the chunk encryption key changes, so CryptPayload never sets Aes.Key or
        ' allocates a transform per call.
        '
        Private Sub RebuildChunkCipherTransform()

            If _ChunkCipherTransform IsNot Nothing Then
                _ChunkCipherTransform.Dispose()
                _ChunkCipherTransform = Nothing
            End If

            If _ChunkEncryptionKey Is Nothing Then Return

            _AesProvider.Key = _ChunkEncryptionKey
            _ChunkCipherTransform = _AesProvider.CreateEncryptor()

        End Sub

        Private Function GetHeaderFileSalt() As Byte()

            Dim FileSalt(FileSaltSize - 1) As Byte
            System.Buffer.BlockCopy(_Header, FileSaltOffset, FileSalt, 0, FileSalt.Length)
            Return FileSalt

        End Function

        Private Sub WrapFileMasterKey(WrapMode As MasterKeyWrapModes,
                                      EncryptionInfo As EncryptionInfo)

            If _FileMasterKey Is Nothing Then
                Array.Clear(_Header, MasterKeyWrapAreaOffset, MasterKeyWrapAreaLength)
                Return
            End If

            Dim WrapSalt(MasterKeyWrapSaltSize - 1) As Byte
            _Rng.GetBytes(WrapSalt)

            Dim WrapKey = DeriveWrapKey(WrapMode, EncryptionInfo, WrapSalt)
            Dim WrappedKey(WrappedFileMasterKeySize - 1) As Byte

            For Index = 0 To _FileMasterKey.Length - 1
                WrappedKey(Index) = CByte(CInt(_FileMasterKey(Index)) Xor CInt(WrapKey(Index)))
            Next

            Using Hmac As New HMACSHA256(WrapKey)
                Dim Mac = Hmac.ComputeHash(WrappedKey)
                System.Buffer.BlockCopy(Mac, 0, _Header, WrappedFileMasterKeyMacOffset, WrappedFileMasterKeyMacSize)
            End Using

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(WrapMode)), 0, _Header, MasterKeyWrapModeOffset, 4)
            System.Buffer.BlockCopy(WrapSalt, 0, _Header, MasterKeyWrapSaltOffset, MasterKeyWrapSaltSize)
            System.Buffer.BlockCopy(WrappedKey, 0, _Header, WrappedFileMasterKeyOffset, WrappedFileMasterKeySize)

        End Sub

        Private Function TryUnwrapFileMasterKey(EncryptionInfo As EncryptionInfo) As Boolean

            Dim WrapMode = CType(BitConverter.ToInt32(_Header, MasterKeyWrapModeOffset), MasterKeyWrapModes)

            If WrapMode = MasterKeyWrapModes.None Then
                _FileMasterKey = Nothing
                DeriveFileMasterKeys()
                _CurrentWriteEncryptionEnabled = False
                Return True
            End If

            If WrapMode = MasterKeyWrapModes.UserWrap AndAlso EncryptionInfo Is Nothing Then Return False
            If WrapMode = MasterKeyWrapModes.PublicWrap AndAlso EncryptionInfo IsNot Nothing Then Return False

            Dim WrapSalt(MasterKeyWrapSaltSize - 1) As Byte
            Dim WrappedKey(WrappedFileMasterKeySize - 1) As Byte
            Dim StoredMac(WrappedFileMasterKeyMacSize - 1) As Byte

            System.Buffer.BlockCopy(_Header, MasterKeyWrapSaltOffset, WrapSalt, 0, WrapSalt.Length)
            System.Buffer.BlockCopy(_Header, WrappedFileMasterKeyOffset, WrappedKey, 0, WrappedKey.Length)
            System.Buffer.BlockCopy(_Header, WrappedFileMasterKeyMacOffset, StoredMac, 0, StoredMac.Length)

            Dim WrapKey = DeriveWrapKey(WrapMode, EncryptionInfo, WrapSalt)

            Using Hmac As New HMACSHA256(WrapKey)
                Dim ExpectedMac = Hmac.ComputeHash(WrappedKey)

                If Not FixedTimeEquals(ExpectedMac, 0, StoredMac, 0, StoredMac.Length) Then
                    Return False
                End If
            End Using

            _FileMasterKey = New Byte(WrappedFileMasterKeySize - 1) {}

            For Index = 0 To _FileMasterKey.Length - 1
                _FileMasterKey(Index) = CByte(CInt(WrappedKey(Index)) Xor CInt(WrapKey(Index)))
            Next

            DeriveFileMasterKeys()

            _CurrentWriteEncryptionEnabled = EncryptionInfo IsNot Nothing

            Return True

        End Function

        Private Shared Function DeriveWrapKey(WrapMode As MasterKeyWrapModes,
                                              EncryptionInfo As EncryptionInfo,
                                              WrapSalt As Byte()) As Byte()

            Dim Material As Byte()

            Select Case WrapMode
                Case MasterKeyWrapModes.PublicWrap
                    Material = PublicIntegrityKey

                Case MasterKeyWrapModes.UserWrap
                    If EncryptionInfo Is Nothing Then
                        Throw New EncryptionMismatchException("Encryption information is required to unwrap the file master key.")
                    End If

                    Material = EncryptionInfo.GetKeyMaterial()

                Case Else
                    Throw New InvalidDataException($"Unsupported master key wrap mode: {CInt(WrapMode)}.")
            End Select

            Using Hmac As New HMACSHA256(Material)
                Return Hmac.ComputeHash(WrapSalt)
            End Using

        End Function

        '
        ' AES-CTR over the cached AES-ECB transform. The keystream for the whole payload is
        ' produced by one TransformBlock over a buffer of successive big-endian counter
        ' blocks, then XORed into the output. Byte-for-byte identical to the previous
        ' per-block implementation, so existing encrypted records still decrypt.
        '
        Private Sub CryptPayload(Input As Byte(),
                                 InputOffset As Integer,
                                 Count As Integer,
                                 Output As Byte(),
                                 OutputOffset As Integer)

            If Count <= 0 Then Return

            If _ChunkCipherTransform Is Nothing Then
                Throw New EncryptionMismatchException("Chunk encryption requested but no file master key is available.")
            End If

            Dim BlockCount = (Count + IvSize - 1) \ IvSize
            Dim KeyStreamLength = BlockCount * IvSize

            If _CtrKeyStreamScratch Is Nothing OrElse _CtrKeyStreamScratch.Length < KeyStreamLength Then
                _CtrCounterScratch = New Byte(KeyStreamLength - 1) {}
                _CtrKeyStreamScratch = New Byte(KeyStreamLength - 1) {}
            End If

            ' Block 0 is the current counter; each later block is the previous block + 1.
            Buffer.BlockCopy(_Counter, 0, _CtrCounterScratch, 0, IvSize)

            For BlockIndex = 1 To BlockCount - 1
                Buffer.BlockCopy(_CtrCounterScratch, (BlockIndex - 1) * IvSize, _CtrCounterScratch, BlockIndex * IvSize, IvSize)
                IncrementCounter(_CtrCounterScratch, BlockIndex * IvSize)
            Next

            _ChunkCipherTransform.TransformBlock(_CtrCounterScratch, 0, KeyStreamLength, _CtrKeyStreamScratch, 0)

            For Index = 0 To Count - 1
                Output(OutputOffset + Index) =
                    CByte(Input(InputOffset + Index) Xor _CtrKeyStreamScratch(Index))
            Next

            ' Leave _Counter advanced by the blocks consumed, as the per-block loop did.
            For BlockIndex = 0 To BlockCount - 1
                IncrementCounter(_Counter)
            Next

        End Sub

        Private Function RemoveUnusedFileMasterKeyIfPossible() As Boolean

            If _CurrentWriteEncryptionEnabled Then Return False
            If _FileMasterKey Is Nothing Then Return False

            ' If a checkpoint is active, a rollback may restore encrypted chunks.
            ' Defer key removal until the outermost checkpoint is closed.
            If HasOpenCheckpoint Then Return False

            If HasEncryptedChunks() Then Return False

            _FileMasterKey = Nothing
            _ChunkEncryptionKey = Nothing
            _ChunkMacKey = Nothing
            RebuildChunkCipherTransform()

            Array.Clear(_Header, MasterKeyWrapAreaOffset, MasterKeyWrapAreaLength)

            Return True

        End Function

        Private Function HasEncryptedChunks() As Boolean

            For Each pair In _PhysicalRecords

                Dim Record = pair.Value

                If Record.RefCount <= 0 Then Continue For
                If Record.PhysicalOffset < DataStartOffset OrElse Record.PhysicalLength < MinChunkRecordSize Then Throw New InvalidDataException($"Invalid physical record entry for record {Record.RecordId}.")
                If Record.PhysicalOffset + Record.PhysicalLength > _IndexOffset Then Throw New InvalidDataException($"Physical record {Record.RecordId} extends beyond data area.")

                Dim Header(ChunkRecordHeaderSize - 1) As Byte

                ReadAt(Record.PhysicalOffset, Header, 0, Header.Length)

                Dim StoredRecordId = BitConverter.ToInt64(Header, 0)

                If StoredRecordId <> Record.RecordId Then
                    Throw New InvalidDataException($"Physical record id mismatch. Expected {Record.RecordId}, found {StoredRecordId}.")
                End If

                Dim EncryptionMethod =
                    CType(BitConverter.ToInt32(Header, ChunkEncryptionMethodOffset),
                          ChunkEncryptionMethods)

                If EncryptionMethod <> ChunkEncryptionMethods.None Then
                    Return True
                End If

            Next

            Return False

        End Function

        Private Sub DecryptPhysicalRecord(ExpectedRecordId As Long, Record As Byte(), Plain As Byte())

            If Record Is Nothing Then Throw New ArgumentNullException(NameOf(Record))
            If Plain Is Nothing Then Throw New ArgumentNullException(NameOf(Plain))
            If Record.Length < MinChunkRecordSize Then Throw New InvalidDataException("Physical record is too small.")

            Dim RecordId = BitConverter.ToInt64(Record, 0)

            If RecordId <> ExpectedRecordId Then
                Throw New InvalidDataException($"Physical record id mismatch. Expected {ExpectedRecordId}, found {RecordId}.")
            End If

            Dim CompressionMethod =
                CType(BitConverter.ToInt32(Record, ChunkCompressionMethodOffset),
                      ChunkedStreamOptions.CompressionMethods)

            Dim EncryptionMethod =
                CType(BitConverter.ToInt32(Record, ChunkEncryptionMethodOffset),
                      ChunkEncryptionMethods)

            Dim PlainLength = BitConverter.ToInt32(Record, ChunkPlainLengthOffset)
            Dim PayloadLength = BitConverter.ToInt32(Record, ChunkPayloadLengthOffset)

            Dim Flags =
                CType(BitConverter.ToInt32(Record, ChunkFlagsOffset),
                      ChunkFlags)

            Dim CompressionEvaluatedMethod =
                CType(BitConverter.ToInt32(Record, ChunkCompressionEvaluatedMethodOffset),
                      ChunkedStreamOptions.CompressionMethods)

            Dim CompressionEvaluatedPercent =
                CInt(Record(ChunkCompressionEvaluatedPercentOffset))

            If PlainLength < 0 OrElse PlainLength > Plain.Length Then
                Throw New InvalidDataException("Invalid physical record plain length.")
            End If

            If PayloadLength < 0 OrElse ChunkRecordDataOffset + PayloadLength + MacSize <> Record.Length Then
                Throw New InvalidDataException("Invalid physical record payload length.")
            End If

            If (CInt(Flags) And Not CInt(SupportedChunkFlags)) <> 0 Then
                Throw New InvalidDataException($"Unsupported physical record flags: {CInt(Flags)}.")
            End If

            If CompressionEvaluatedPercent < MinimumCompressionEvaluatedPercent OrElse
               CompressionEvaluatedPercent > MaximumCompressionEvaluatedPercent Then

                Throw New InvalidDataException($"Invalid compression evaluated percent: {CompressionEvaluatedPercent}.")

            End If

            If [Enum].IsDefined(GetType(ChunkedStreamOptions.CompressionMethods), CompressionEvaluatedMethod) = False Then
                Throw New InvalidDataException($"Unsupported evaluated compression method: {CInt(CompressionEvaluatedMethod)}.")
            End If

            Dim RecordMacKey =
                If(EncryptionMethod = ChunkEncryptionMethods.AesCtrFileMasterKey,
                   _ChunkMacKey,
                   PublicIntegrityKey)

            If RecordMacKey Is Nothing Then
                Throw New EncryptionMismatchException("Encrypted physical record exists but no file master key is available.")
            End If

            Using Hmac As New HMACSHA256(RecordMacKey)

                Dim ExpectedMac = Hmac.ComputeHash(Record, 0, ChunkRecordDataOffset + PayloadLength)

                If FixedTimeEquals(ExpectedMac, 0, Record, ChunkRecordDataOffset + PayloadLength, MacSize) = False Then
                    Throw New CryptographicException("Physical record MAC invalid.")
                End If

            End Using

            Dim Payload =
                If(PayloadLength = 0,
                   New Byte() {},
                   New Byte(PayloadLength - 1) {})

            Select Case EncryptionMethod

                Case ChunkEncryptionMethods.None

                    If PayloadLength > 0 Then
                        Buffer.BlockCopy(Record, ChunkRecordDataOffset, Payload, 0, PayloadLength)
                    End If

                Case ChunkEncryptionMethods.AesCtrFileMasterKey

                    If _ChunkEncryptionKey Is Nothing Then
                        Throw New EncryptionMismatchException("Encrypted physical record exists but no file master key is available.")
                    End If

                    Buffer.BlockCopy(Record, ChunkRecordIvOffset, _Counter, 0, IvSize)

                    CryptPayload(Record, ChunkRecordDataOffset, PayloadLength, Payload, 0)

                Case Else

                    Throw New InvalidDataException($"Unsupported chunk encryption method: {CInt(EncryptionMethod)}.")

            End Select

            Dim Restored = DecompressPayload(CompressionMethod, Payload, PlainLength)

            If Restored.Length <> PlainLength Then
                Throw New InvalidDataException("Physical record decompressed/plain length mismatch.")
            End If

            If PlainLength > 0 Then
                Buffer.BlockCopy(Restored, 0, Plain, 0, PlainLength)
            End If

        End Sub

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

        Private Shared Sub IncrementCounter(Counter As Byte())

            IncrementCounter(Counter, 0)

        End Sub

        Private Shared Sub IncrementCounter(Buffer As Byte(), Offset As Integer)

            For Index = Offset + IvSize - 1 To Offset Step -1
                Buffer(Index) = CByte((CInt(Buffer(Index)) + 1) And &HFF)

                If Buffer(Index) <> 0 Then Exit For
            Next

        End Sub

    End Class

End Namespace
