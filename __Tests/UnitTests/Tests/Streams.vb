Imports System.IO
Imports System.Text
Imports StreamEncryption.Streams

Namespace Streams

    Public NotInheritable Class ChunkedStreamTests

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Verifies that plaintext data can be written and read back at several offsets and lengths.
        ''' </summary>
        <UnitTester.SimpleTest({0, 0}, True)>
        <UnitTester.SimpleTest({1, 0}, True)>
        <UnitTester.SimpleTest({1000, 0}, True)>
        <UnitTester.SimpleTest({65536, 0}, True)>
        <UnitTester.SimpleTest({131072, 0}, True)>
        <UnitTester.SimpleTest({5000, 65436}, True)>
        <UnitTester.SimpleTest({100000, 12345}, True)>
        Public Shared Function PlainRoundTrip(Length As Integer,
                                              Offset As Integer) As Boolean

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Data = MakePattern(Length, 17)

                    Cs.Write(Offset, Data)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(Offset, Result)

                    If BytesRead <> Data.Length Then
                        Return False
                    End If

                    Return BytesEqual(Data, Result)

                End Using

            End Using

        End Function

        ''' <summary>
        ''' Verifies that every compression method except None round-trips correctly and compresses ideal input below 10% of the original size.
        ''' Future compression enum values are automatically included.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub CompressionRoundTrip()

            For Each CompressionMethod In GetCompressionMethodsToTest()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = CompressionMethod,
                        .CompressionMinimumSavingsPercent = 1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = MakeRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        If BytesRead <> Data.Length Then
                            Throw New Exception($"Unexpected byte count for compression method {CompressionMethod}. Expected={Data.Length}, Actual={BytesRead}.")
                        End If

                        AssertBytesEqual(Data, Result, $"Compressed round-trip failed for compression method {CompressionMethod}.")

                        Dim Struct = Cs.GetStructure()
                        Dim CompressionPercent = Struct.PayloadCompressionRatio

                        If CompressionPercent >= 0.1R Then
                            Throw New Exception($"Compression method {CompressionMethod} did not compress ideal input below 10%. Actual={CompressionPercent:P2}.")
                        End If

                        If Not Struct.Chunks.Any(Function(chunk) chunk.IsAllocated AndAlso chunk.IsCompressed) Then
                            Throw New Exception($"Compression method {CompressionMethod} did not produce any compressed chunks.")
                        End If

                    End Using

                End Using

            Next

        End Sub

        ''' <summary>
        ''' Verifies that encrypted data round-trips correctly for multiple lengths.
        ''' </summary>
        <UnitTester.SimpleTest({1000}, True)>
        <UnitTester.SimpleTest({65536}, True)>
        <UnitTester.SimpleTest({200000}, True)>
        Public Shared Function EncryptedRoundTrip(Length As Integer) As Boolean

            Using Ms As New MemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1))
                }

                Dim Data = MakePattern(Length, 29)

                Using Cs = ChunkedStream.Open(Ms, Options)
                    Cs.Write(0, Data)
                End Using

                Using Cs = ChunkedStream.Open(Ms, Options)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(0, Result)

                    If BytesRead <> Data.Length Then
                        Return False
                    End If

                    Return BytesEqual(Data, Result)

                End Using

            End Using

        End Function

        ''' <summary>
        ''' Verifies that sparse chunks read back as zero-filled data.
        ''' </summary>
        <UnitTester.SimpleTest({0}, True)>
        <UnitTester.SimpleTest({1}, True)>
        <UnitTester.SimpleTest({5}, True)>
        Public Shared Function SparseReadReturnsZeroes(ChunkOffset As Integer) As Boolean

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Cs.SetLength(CLng(ChunkedStream.ChunkSize) * 10L)

                    Dim Output = MakeBuffer(4096)
                    Dim ReadOffset = CLng(ChunkedStream.ChunkSize) * CLng(ChunkOffset)

                    Cs.Read(ReadOffset, Output)

                    For Each value In Output
                        If value <> 0 Then
                            Return False
                        End If
                    Next

                    Return True

                End Using

            End Using

        End Function

        ''' <summary>
        ''' Verifies that shrinking the logical stream length preserves the expected prefix bytes.
        ''' </summary>
        <UnitTester.SimpleTest({1000, 100}, True)>
        <UnitTester.SimpleTest({100000, 1000}, True)>
        <UnitTester.SimpleTest({200000, 65536}, True)>
        Public Shared Function SetLengthShrinkPreservesPrefix(InitialLength As Integer,
                                                              FinalLength As Integer) As Boolean

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Data = MakePattern(InitialLength, 41)

                    Cs.Write(0, Data)
                    Cs.SetLength(FinalLength)

                    If Cs.Length <> FinalLength Then
                        Return False
                    End If

                    Dim Result = MakeBuffer(FinalLength)
                    Dim BytesRead = Cs.Read(0, Result)

                    If BytesRead <> FinalLength Then
                        Return False
                    End If

                    For Index = 0 To FinalLength - 1
                        If Result(Index) <> Data(Index) Then
                            Return False
                        End If
                    Next

                    Return True

                End Using

            End Using

        End Function

        ''' <summary>
        ''' Verifies that an empty backing stream creates a valid empty ChunkedStream.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub CreateEmptyStream()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    AssertEqual(0L, Cs.Length, "Empty stream length was incorrect.")

                    Dim Struct = Cs.GetStructure()

                    AssertNotNothing(Struct, "Structure snapshot was Nothing.")
                    AssertEqual(0L, Struct.LogicalLength, "Structure logical length was incorrect.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that uncompressed plaintext data remains intact after closing and reopening the stream.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ReopenPreservesPlainData()

            Using Ms As New MemoryStream()

                Dim Data = Encoding.UTF8.GetBytes("ChunkedStream plain reopen test")

                Using Cs = ChunkedStream.Open(Ms)
                    Cs.Write(123, Data)
                End Using

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(123, Result)

                    AssertEqual(Data.Length, BytesRead, "Unexpected byte count after reopen.")
                    AssertBytesEqual(Data, Result, "Plain data changed after reopen.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that compressed data remains intact after closing and reopening the stream.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ReopenPreservesCompressedData()

            Using Ms As New MemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                    .CompressionMinimumSavingsPercent = 1
                }

                Dim Data = MakeRepeatingPattern(250000, 5)

                Using Cs = ChunkedStream.Open(Ms, Options)
                    Cs.Write(0, Data)
                End Using

                Using Cs = ChunkedStream.Open(Ms, Options)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(0, Result)

                    AssertEqual(Data.Length, BytesRead, "Unexpected byte count after compressed reopen.")
                    AssertBytesEqual(Data, Result, "Compressed data changed after reopen.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that opening a user-wrapped encrypted stream without encryption information fails.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ReopenEncryptedWithoutKeyFails()

            Using Ms As New MemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2))
                }

                Using Cs = ChunkedStream.Open(Ms, Options)
                    Cs.Write(0, MakePattern(10000, 7))
                End Using

                AssertThrows(Of StreamEncryption.Streams.ChunkedStream.EncryptionMismatchException)(
                    Sub()
                        Using Cs = ChunkedStream.Open(Ms)
                        End Using
                    End Sub,
                    "Opening an encrypted stream without encryption information should fail.")

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that opening a user-wrapped encrypted stream with the wrong key fails.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ReopenEncryptedWithWrongKeyFails()

            Using Ms As New MemoryStream()

                Dim CorrectOptions As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(3))
                }

                Dim WrongOptions As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(4))
                }

                Using Cs = ChunkedStream.Open(Ms, CorrectOptions)
                    Cs.Write(0, MakePattern(10000, 11))
                End Using

                AssertThrows(Of StreamEncryption.Streams.ChunkedStream.EncryptionMismatchException)(
                    Sub()
                        Using Cs = ChunkedStream.Open(Ms, WrongOptions)
                        End Using
                    End Sub,
                    "Opening with the wrong encryption key should fail.")

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that disabling encryption publicly wraps the file master key and allows reopening without encryption information.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub DisableEncryptionAllowsPublicReopen()

            Using Ms As New MemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(5))
                }

                Dim Data = MakePattern(120000, 13)

                Using Cs = ChunkedStream.Open(Ms, Options)

                    Cs.Write(0, Data)
                    Cs.Options.EncryptionInfo = Nothing

                End Using

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(0, Result)

                    AssertEqual(Data.Length, BytesRead, "Unexpected byte count after public reopen.")
                    AssertBytesEqual(Data, Result, "Encrypted data was not readable after public wrapping.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that enabling encryption after writing plaintext makes future chunks encrypted without breaking existing chunks.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub EnableEncryptionAfterPlainWritesKeepsBothReadable()

            Using Ms As New MemoryStream()

                Dim PlainData = MakePattern(ChunkedStream.ChunkSize, 19)
                Dim EncryptedData = MakePattern(ChunkedStream.ChunkSize, 23)

                Using Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, PlainData)
                    Cs.Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(6))
                    Cs.Write(ChunkedStream.ChunkSize, EncryptedData)

                    Dim PlainResult = MakeBuffer(PlainData.Length)
                    Dim EncryptedResult = MakeBuffer(EncryptedData.Length)

                    Cs.Read(0, PlainResult)
                    Cs.Read(ChunkedStream.ChunkSize, EncryptedResult)

                    AssertBytesEqual(PlainData, PlainResult, "Plain data changed after enabling encryption.")
                    AssertBytesEqual(EncryptedData, EncryptedResult, "Encrypted data did not round-trip after enabling encryption.")

                    Dim Struct = Cs.GetStructure()

                    AssertTrue(Struct.EncryptedChunkCount > 0, "Expected at least one encrypted chunk.")
                    AssertTrue(Struct.UnencryptedChunkCount > 0, "Expected at least one unencrypted chunk.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that expanding stream length creates zero-filled sparse areas.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub SetLengthExpandReadsZeroes()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Data = MakePattern(1000, 31)

                    Cs.Write(0, Data)
                    Cs.SetLength(CLng(ChunkedStream.ChunkSize) * 3L)

                    Dim Output = MakeBuffer(4096)
                    Cs.Read(ChunkedStream.ChunkSize * 2L, Output)

                    For Each value In Output
                        If value <> 0 Then
                            Throw New Exception("Expanded sparse area was not zero-filled.")
                        End If
                    Next

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that a partial overwrite across a chunk boundary does not corrupt unaffected bytes.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub PartialOverwritePreservesUnaffectedBytes()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Original = MakePattern(ChunkedStream.ChunkSize * 2, 37)
                    Dim Patch = MakePattern(1000, 43)
                    Dim PatchOffset = ChunkedStream.ChunkSize - 500

                    Cs.Write(0, Original)
                    Cs.Write(PatchOffset, Patch)

                    Dim Expected = DirectCast(Original.Clone(), Byte())
                    Buffer.BlockCopy(Patch, 0, Expected, PatchOffset, Patch.Length)

                    Dim Result = MakeBuffer(Expected.Length)
                    Cs.Read(0, Result)

                    AssertBytesEqual(Expected, Result, "Partial overwrite corrupted unaffected bytes.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that validation succeeds for a healthy stream.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ValidatePassesForHealthyStream()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, MakePattern(200000, 47))
                    Cs.Validate()

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that validation fails after a chunk record is corrupted.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub ValidateFailsAfterCorruption()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, MakePattern(100000, 53))

                    Dim Struct = Cs.GetStructure()
                    Dim FirstChunk = Struct.Chunks.First(Function(chunk) chunk.IsAllocated)

                    Dim CorruptOffset = FirstChunk.PhysicalOffset.Value + 50

                    Ms.Position = CorruptOffset

                    Dim OriginalByte = Ms.ReadByte()

                    If OriginalByte < 0 Then
                        Throw New Exception("Could not read byte to corrupt.")
                    End If

                    Ms.Position = CorruptOffset
                    Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                    AssertThrows(Of System.Security.Cryptography.CryptographicException)(
                        Sub()
                            Cs.Validate()
                        End Sub,
                        "Validation should fail after corrupting a chunk record.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that structure snapshots contain header, chunk and index regions after data is written.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub StructureSnapshotContainsExpectedRegions()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, MakePattern(ChunkedStream.ChunkSize * 2, 59))

                    Dim Struct = Cs.GetStructure()

                    AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Header),
                               "Structure did not contain header regions.")

                    AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Chunk),
                               "Structure did not contain chunk regions.")

                    AssertTrue(Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Index),
                               "Structure did not contain an index region.")

                    AssertTrue(Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                               "Structure did not contain allocated chunks.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that defragmentation preserves logical data.
        ''' </summary>

        <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Move})>
        <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Sequence})>
        <UnitTester.SimpleTest({ChunkedStream.DefragTypes.Rebuild})>
        Private Shared Sub DefragmentPreservesData(Type As ChunkedStream.DefragTypes)

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Expected = CreateFragmentedContent(Cs)

                    Cs.Defragment(Type)

                    Dim Result = MakeBuffer(Expected.Length)
                    Dim BytesRead = Cs.Read(0, Result)

                    AssertEqual(Expected.Length, BytesRead, $"Unexpected byte count after {Type}.")
                    AssertBytesEqual(Expected, Result, $"{Type} corrupted data.")

                    Cs.Validate()

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that Move defragmentation does not increase fragmentation and preserves data.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub DefragmentMoveDoesNotIncreaseFragmentation()

            Using Ms As New MemoryStream()

                Using Cs = ChunkedStream.Open(Ms)

                    Dim Expected = CreateFragmentedContent(Cs)
                    Dim Before = Cs.GetFragmentation()

                    Cs.Defragment(ChunkedStream.DefragTypes.Move)

                    Dim After = Cs.GetFragmentation()

                    If After > Before Then
                        Throw New Exception($"Move defrag increased fragmentation. Before={Before}, After={After}.")
                    End If

                    Dim Result = MakeBuffer(Expected.Length)
                    Cs.Read(0, Result)

                    AssertBytesEqual(Expected, Result, "Move defrag corrupted data.")

                End Using

            End Using

        End Sub

        ''' <summary>
        ''' Verifies that compression and encryption can be used together and survive reopen.
        ''' </summary>
        <UnitTester.SimpleTest()>
        Public Shared Sub CompressionAndEncryptionTogetherRoundTrip()

            Using Ms As New MemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.GZip,
                    .CompressionMinimumSavingsPercent = 1,
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7))
                }

                Dim Data = MakeRepeatingPattern(300000, 12)

                Using Cs = ChunkedStream.Open(Ms, Options)
                    Cs.Write(0, Data)
                End Using

                Using Cs = ChunkedStream.Open(Ms, Options)

                    Dim Result = MakeBuffer(Data.Length)
                    Dim BytesRead = Cs.Read(0, Result)

                    AssertEqual(Data.Length, BytesRead, "Unexpected compressed/encrypted byte count.")
                    AssertBytesEqual(Data, Result, "Compressed and encrypted data changed after reopen.")

                End Using

            End Using

        End Sub

        Private Shared Function GetCompressionMethodsToTest() As ChunkedStream.ChunkedStreamOptions.CompressionMethods()

            Return [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods)).
                          Cast(Of ChunkedStream.ChunkedStreamOptions.CompressionMethods)().
                          Where(Function(method) method <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None).
                          ToArray()

        End Function

        Private Shared Function CreateFragmentedContent(Cs As ChunkedStream) As Byte()

            Dim TotalLength = ChunkedStream.ChunkSize * 8
            Dim Expected = MakeBuffer(TotalLength)

            For ChunkIndex = 0 To 7

                Dim Data = MakePattern(ChunkedStream.ChunkSize, 100 + ChunkIndex)
                Dim Offset = ChunkIndex * ChunkedStream.ChunkSize

                Cs.Write(Offset, Data)
                Buffer.BlockCopy(Data, 0, Expected, Offset, Data.Length)

            Next

            For ChunkIndex = 0 To 7 Step 2

                Dim Data = MakePattern(ChunkedStream.ChunkSize, 200 + ChunkIndex)
                Dim Offset = ChunkIndex * ChunkedStream.ChunkSize

                Cs.Write(Offset, Data)
                Buffer.BlockCopy(Data, 0, Expected, Offset, Data.Length)

            Next

            For ChunkIndex = 1 To 7 Step 2

                Dim Data = MakePattern(ChunkedStream.ChunkSize, 300 + ChunkIndex)
                Dim Offset = ChunkIndex * ChunkedStream.ChunkSize

                Cs.Write(Offset, Data)
                Buffer.BlockCopy(Data, 0, Expected, Offset, Data.Length)

            Next

            Return Expected

        End Function

        Private Shared Function MakeBuffer(Length As Integer) As Byte()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 Then Return New Byte() {}

            Return New Byte(Length - 1) {}

        End Function

        Private Shared Function MakePattern(Length As Integer,
                                            Seed As Integer) As Byte()

            Dim Result = MakeBuffer(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Index * 31) + Seed + (Index \ 7)) And &HFF)
            Next

            Return Result

        End Function

        Private Shared Function MakeRepeatingPattern(Length As Integer,
                                                     Period As Integer) As Byte()

            If Period <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Period))

            Dim Result = MakeBuffer(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte((Index Mod Period) + 65)
            Next

            Return Result

        End Function

        Private Shared Function MakeKey(Seed As Integer) As Byte()

            Dim Result = MakeBuffer(32)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Seed * 17) + (Index * 13)) And &HFF)
            Next

            Return Result

        End Function

        Private Shared Function BytesEqual(Left As Byte(),
                                           Right As Byte()) As Boolean

            If Left Is Nothing OrElse Right Is Nothing Then Return False
            If Left.Length <> Right.Length Then Return False

            For Index = 0 To Left.Length - 1
                If Left(Index) <> Right(Index) Then
                    Return False
                End If
            Next

            Return True

        End Function

        Private Shared Sub AssertBytesEqual(Expected As Byte(),
                                            Actual As Byte(),
                                            Message As String)

            If BytesEqual(Expected, Actual) Then Return

            Throw New Exception(Message)

        End Sub

        Private Shared Sub AssertEqual(Of TValue)(Expected As TValue,
                                                  Actual As TValue,
                                                  Message As String)

            If EqualityComparer(Of TValue).Default.Equals(Expected, Actual) Then Return

            Throw New Exception($"{Message} Expected={Expected}, Actual={Actual}.")

        End Sub

        Private Shared Sub AssertTrue(Value As Boolean,
                                      Message As String)

            If Value Then Return

            Throw New Exception(Message)

        End Sub

        Private Shared Sub AssertNotNothing(Value As Object,
                                            Message As String)

            If Value IsNot Nothing Then Return

            Throw New Exception(Message)

        End Sub

        Private Shared Sub AssertThrows(Of T As Exception)(Action As Action,
                                                           Message As String)

            Try
                Action()
            Catch Ex As Exception When TypeOf Ex Is T
                Return
            End Try

            Throw New Exception(Message)

        End Sub

    End Class

End Namespace