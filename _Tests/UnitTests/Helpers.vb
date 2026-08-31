Imports System.IO
Imports i00.Streams

Namespace Tests

    Public Module Helpers

        '<System.Runtime.CompilerServices.Extension>
        'Public Function FormatFileSizeFromBytes(Size As Long, Optional DecimalPlaces As Integer = 1) As String
        '    Return i00.Extensions.FormatFileSizeFromBytes(Size, DecimalPlaces)
        '    'For Index As Integer = 0 To FormatFileSizeLimits.Length - 1
        '    '    If Size >= FormatFileSizeLimits(Index) Then
        '    '        Return String.Format(
        '    '            "{0:#,##0." & New String("#"c, DecimalPlaces) & "} " & FormatFileSizeUnits(Index),
        '    '            Size / CDbl(FormatFileSizeLimits(Index)))
        '    '    End If
        '    'Next

        '    'Return "< 1 KB"

        'End Function

#Region "Primitive data generators"

        Friend Function GenerateZeroedData(Length As Integer) As Byte()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 Then Return New Byte() {}

            Return New Byte(Length - 1) {}

        End Function

        Friend Function GeneratePatternData(Length As Integer,
                                            Seed As Integer) As Byte()

            Dim Result = GenerateZeroedData(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Index * 31) + Seed + (Index \ 7)) And &HFF)
            Next

            Return Result

        End Function

        Friend Function GenerateRandomData(Length As Integer,
                                           Seed As Integer) As Byte()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 Then Return New Byte() {}

            Dim Result = GenerateZeroedData(Length)
            Dim Rng = CreateDeterministicRandom(Seed)

            Rng.NextBytes(Result)

            Return Result

        End Function

        Friend Function GenerateRepeatingPatternData(Length As Integer,
                                                     Period As Integer) As Byte()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Period <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Period))
            If Length = 0 Then Return New Byte() {}

            Dim Result = GenerateZeroedData(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte((Index Mod Period) + 65)
            Next

            Return Result

        End Function

        ''' <summary>
        ''' Generates deterministic partially compressible data where each chunk has roughly
        ''' the requested compressible ratio.
        '''
        ''' This is intentionally chunk-aware. For example, 10 chunks at 50% produces each
        ''' chunk as 50% repeating-pattern bytes and 50% random bytes, rather than producing
        ''' five fully compressible chunks followed by five random chunks.
        ''' </summary>
        Friend Function GeneratePartiallyCompressibleData(CompressibleRatio As Double,
                                                          ChunkSize As Integer,
                                                          ChunkCount As Integer,
                                                          Seed As Integer) As Byte()

            If CompressibleRatio < 0.0R OrElse CompressibleRatio > 1.0R Then
                Throw New ArgumentOutOfRangeException(NameOf(CompressibleRatio), "CompressibleRatio must be between 0 and 1.")
            End If

            If ChunkSize <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkSize))
            If ChunkCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkCount))
            If ChunkCount = 0 Then Return New Byte() {}

            Dim TotalLengthLong = CLng(ChunkSize) * CLng(ChunkCount)
            If TotalLengthLong > Integer.MaxValue Then Throw New OverflowException("Requested data length exceeds maximum array size.")
            Dim Result(CInt(TotalLengthLong) - 1) As Byte
            Dim OutputOffset = 0

            For ChunkIndex = 0 To ChunkCount - 1

                Dim Chunk =
                    GeneratePartiallyCompressibleChunk(
                        CompressibleRatio,
                        ChunkSize,
                        Seed,
                        ChunkIndex)

                Buffer.BlockCopy(
                    Chunk,
                    0,
                    Result,
                    OutputOffset,
                    Chunk.Length)

                OutputOffset += Chunk.Length

            Next

            Return Result

        End Function

        ''' <summary>
        ''' Generates deterministic partially compressible data for an arbitrary length.
        ''' Each full or partial chunk is generated with the requested compressible ratio.
        ''' </summary>
        Friend Function GeneratePartiallyCompressibleDataForLength(CompressibleRatio As Double,
                                                                   Length As Integer,
                                                                   ChunkSize As Integer,
                                                                   Seed As Integer) As Byte()

            If CompressibleRatio < 0.0R OrElse CompressibleRatio > 1.0R Then
                Throw New ArgumentOutOfRangeException(NameOf(CompressibleRatio), "CompressibleRatio must be between 0 and 1.")
            End If

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If ChunkSize <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkSize))
            If Length = 0 Then Return New Byte() {}

            Dim Result(Length - 1) As Byte
            Dim Remaining = Length
            Dim OutputOffset = 0
            Dim ChunkIndex = 0

            While Remaining > 0

                Dim CurrentChunkLength = Math.Min(ChunkSize, Remaining)

                Dim Chunk =
                    GeneratePartiallyCompressibleChunk(
                        CompressibleRatio,
                        CurrentChunkLength,
                        Seed,
                        ChunkIndex)

                Buffer.BlockCopy(
                    Chunk,
                    0,
                    Result,
                    OutputOffset,
                    Chunk.Length)

                OutputOffset += Chunk.Length
                Remaining -= Chunk.Length
                ChunkIndex += 1

            End While

            Return Result

        End Function

        Private Function GeneratePartiallyCompressibleChunk(CompressibleRatio As Double,
                                                            ChunkLength As Integer,
                                                            Seed As Integer,
                                                            ChunkIndex As Integer) As Byte()

            If ChunkLength < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkLength))
            If ChunkLength = 0 Then Return New Byte() {}

            Dim CompressibleLength =
                CInt(Math.Round(ChunkLength * CompressibleRatio, MidpointRounding.AwayFromZero))

            If CompressibleLength < 0 Then CompressibleLength = 0
            If CompressibleLength > ChunkLength Then CompressibleLength = ChunkLength

            Dim RandomLength = ChunkLength - CompressibleLength
            Dim Result(ChunkLength - 1) As Byte

            If CompressibleLength > 0 Then

                Dim Period =
                    Math.Max(
                        1,
                        Math.Min(
                            251,
                            3 + Math.Abs((Seed + (ChunkIndex * 17)) Mod 29)))

                Dim Compressible =
                    GenerateRepeatingPatternData(
                        CompressibleLength,
                        Period)

                Buffer.BlockCopy(
                    Compressible,
                    0,
                    Result,
                    0,
                    Compressible.Length)

            End If

            If RandomLength > 0 Then

                Dim Random =
                    GenerateRandomData(
                        RandomLength,
                        Seed + (ChunkIndex * 7919) + 101)

                Buffer.BlockCopy(
                    Random,
                    0,
                    Result,
                    CompressibleLength,
                    Random.Length)

            End If

            Return Result

        End Function

        Friend Function MakeKey(Seed As Integer) As Byte()

            Dim Result = GenerateZeroedData(32)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Seed * 17) + (Index * 13)) And &HFF)
            Next

            Return Result

        End Function

        Friend Function CreateDeterministicRandom(Seed As Integer) As Random

            Return New Random(Seed)

        End Function

#End Region

#Region "Scenario builders"

        ''' <summary>
        ''' Creates a fragmented physical layout by writing every chunk, then rewriting even
        ''' chunks, then rewriting odd chunks. The returned byte array is the expected logical
        ''' stream content.
        ''' </summary>
        Friend Function CreateFragmentedLayout(Cs As ChunkedStream,
                                               Optional ChunkCount As Integer = 8) As Byte()

            If Cs Is Nothing Then Throw New ArgumentNullException(NameOf(Cs))
            If ChunkCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ChunkCount))
            If ChunkCount = 0 Then Return New Byte() {}

            Dim TotalLength = Cs.Options.ChunkSize * ChunkCount
            Dim Expected = GenerateZeroedData(TotalLength)

            For ChunkIndex = 0 To ChunkCount - 1

                Dim Data =
                    GeneratePatternData(
                        Cs.Options.ChunkSize,
                        100 + ChunkIndex)

                Dim Offset = ChunkIndex * Cs.Options.ChunkSize

                Cs.Write(Offset, Data)
                Overlay(Expected, Data, Offset)

            Next

            For ChunkIndex = 0 To ChunkCount - 1 Step 2

                Dim Data =
                    GeneratePatternData(
                        Cs.Options.ChunkSize,
                        200 + ChunkIndex)

                Dim Offset = ChunkIndex * Cs.Options.ChunkSize

                Cs.Write(Offset, Data)
                Overlay(Expected, Data, Offset)

            Next

            For ChunkIndex = 1 To ChunkCount - 1 Step 2

                Dim Data =
                    GeneratePatternData(
                        Cs.Options.ChunkSize,
                        300 + ChunkIndex)

                Dim Offset = ChunkIndex * Cs.Options.ChunkSize

                Cs.Write(Offset, Data)
                Overlay(Expected, Data, Offset)

            Next

            Return Expected

        End Function

#End Region

#Region "Array helpers"

        Friend Function CombineArrays(ParamArray Arrays()() As Byte) As Byte()

            If Arrays Is Nothing Then Throw New ArgumentNullException(NameOf(Arrays))

            Dim TotalLength =
                Arrays.
                Where(Function(x) x IsNot Nothing).
                Sum(Function(x) x.Length)

            If TotalLength = 0 Then Return New Byte() {}

            Dim Result(TotalLength - 1) As Byte
            Dim Offset = 0

            For Each Item In Arrays

                If Item Is Nothing OrElse Item.Length = 0 Then Continue For

                Buffer.BlockCopy(
                    Item,
                    0,
                    Result,
                    Offset,
                    Item.Length)

                Offset += Item.Length

            Next

            Return Result

        End Function

        Friend Sub Overlay(Destination As Byte(),
                           Source As Byte(),
                           Offset As Integer)

            If Destination Is Nothing Then Throw New ArgumentNullException(NameOf(Destination))
            If Source Is Nothing Then Throw New ArgumentNullException(NameOf(Source))
            If Offset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Offset))
            If Offset > Destination.Length Then Throw New ArgumentException("Offset exceeds destination length.", NameOf(Offset))
            If Source.Length > Destination.Length - Offset Then Throw New ArgumentException("Source does not fit in destination at the requested offset.")

            If Source.Length = 0 Then Return

            Buffer.BlockCopy(
                Source,
                0,
                Destination,
                Offset,
                Source.Length)

        End Sub

        Friend Function Slice(Source As Byte(),
                              Offset As Integer,
                              Length As Integer) As Byte()

            If Source Is Nothing Then Throw New ArgumentNullException(NameOf(Source))
            If Offset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Offset))
            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Offset > Source.Length Then Throw New ArgumentException("Offset exceeds source length.", NameOf(Offset))
            If Length > Source.Length - Offset Then Throw New ArgumentException("Length exceeds source bounds.", NameOf(Length))
            If Length = 0 Then Return New Byte() {}

            Dim Result(Length - 1) As Byte

            Buffer.BlockCopy(
                Source,
                Offset,
                Result,
                0,
                Length)

            Return Result

        End Function

#End Region

#Region "Assertions"

        Friend Function BytesEqual(Left As Byte(),
                                   Right As Byte()) As Boolean

            If Left Is Nothing OrElse Right Is Nothing Then Return False
            If Left.Length <> Right.Length Then Return False

            For Index = 0 To Left.Length - 1
                If Left(Index) <> Right(Index) Then Return False
            Next

            Return True

        End Function

        Friend Sub AssertBytesEqual(Expected As Byte(),
                                    Actual As Byte(),
                                    Message As String)

            If Expected Is Nothing AndAlso Actual Is Nothing Then Return

            If Expected Is Nothing Then
                Throw New Exception($"{Message} Expected was Nothing, Actual length={Actual.Length}.")
            End If

            If Actual Is Nothing Then
                Throw New Exception($"{Message} Actual was Nothing, Expected length={Expected.Length}.")
            End If

            If Expected.Length <> Actual.Length Then
                Throw New Exception($"{Message} Expected length={Expected.Length}, Actual length={Actual.Length}.")
            End If

            For Index = 0 To Expected.Length - 1
                If Expected(Index) <> Actual(Index) Then
                    Throw New Exception(
                        $"{Message} First mismatch at byte {Index}. Expected={Expected(Index)}, Actual={Actual(Index)}.")
                End If
            Next

        End Sub

        Friend Sub AssertEqual(Of TValue)(Expected As TValue,
                                          Actual As TValue,
                                          Message As String)

            If EqualityComparer(Of TValue).Default.Equals(Expected, Actual) Then Return

            Throw New Exception($"{Message} Expected={Expected}, Actual={Actual}.")

        End Sub

        Friend Sub AssertNotEqual(Of TValue)(NotExpected As TValue,
                                             Actual As TValue,
                                             Message As String)

            If EqualityComparer(Of TValue).Default.Equals(NotExpected, Actual) = False Then Return

            Throw New Exception($"{Message} Value={Actual}.")

        End Sub

        Friend Sub AssertTrue(Value As Boolean,
                              Message As String)

            If Value Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertFalse(Value As Boolean,
                               Message As String)

            If Value = False Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertNotNothing(Value As Object,
                                    Message As String)

            If Value IsNot Nothing Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertThrows(Of T As Exception)(Action As Action,
                                                   Message As String)

            If Action Is Nothing Then Throw New ArgumentNullException(NameOf(Action))

            Try
                Action()

            Catch Ex As Exception When TypeOf Ex Is T
                Return

            Catch Ex As Exception
                Throw New Exception(
                    $"{Message} Expected exception type {GetType(T).FullName}, but got {Ex.GetType().FullName}.",
                    Ex)
            End Try

            Throw New Exception(
                $"{Message} Expected exception type {GetType(T).FullName}, but no exception was thrown.")

        End Sub

#End Region

#Region "Fault injection"

        ''' <summary>
        ''' An in-memory stream that throws on a chosen Write call, to drive the
        ''' backing-store failure paths.
        ''' </summary>
        Friend NotInheritable Class FailingMemoryStream
            Inherits MemoryStream

            ''' <summary>1-based index of the Write call that should throw; 0 disables the failure.</summary>
            Public Property FailOnWriteNumber As Integer

            ''' <summary>1-based index from which every Write call should throw; 0 disables it.</summary>
            Public Property FailFromWriteNumber As Integer

            Public Property WriteCount As Integer

            Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)

                WriteCount += 1

                If FailOnWriteNumber > 0 AndAlso WriteCount = FailOnWriteNumber Then
                    Throw New IOException("Simulated backing-store write failure.")
                End If

                If FailFromWriteNumber > 0 AndAlso WriteCount >= FailFromWriteNumber Then
                    Throw New IOException("Simulated backing-store write failure.")
                End If

                MyBase.Write(Buffer, Offset, Count)

            End Sub

        End Class

        ''' <summary>
        ''' Opens a ChunkedStream on a <see cref="FailingMemoryStream" />, writes a baseline,
        ''' then forces one backing write to fail so the stream faults. Returns the faulted,
        ''' still-open stream with failure injection disabled again.
        ''' </summary>
        Friend Function CreateFaultedChunkedStream(Seed As Integer) As ChunkedStream

            Dim Backing As New FailingMemoryStream()

            Dim Cs = ChunkedStream.Open(Backing)
            Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 4, Seed))
            Cs.Flush()

            Backing.FailOnWriteNumber = Backing.WriteCount + 1

            Try
                Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 4, Seed + 1))
                Throw New Exception("CreateFaultedChunkedStream expected the injected write failure to throw.")
            Catch Ex As IOException
            End Try

            Backing.FailOnWriteNumber = 0

            Return Cs

        End Function

#End Region

#Region "Corruption"

        Friend Sub CorruptPhysicalRecordByteField(Stream As Stream,
                                         PhysicalOffset As Long,
                                         FieldOffset As Integer,
                                         Value As Byte)

            If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))

            Stream.Position =
                PhysicalOffset + FieldOffset

            Stream.WriteByte(Value)

        End Sub

        Friend Sub CorruptPhysicalRecordInt32Field(Stream As Stream,
                                                  PhysicalOffset As Long,
                                                  FieldOffset As Integer,
                                                  Value As Integer)

            If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))

            Stream.Position =
                PhysicalOffset + FieldOffset

            Dim Buffer =
                BitConverter.GetBytes(Value)

            Stream.Write(Buffer, 0, Buffer.Length)

        End Sub

#End Region

    End Module

End Namespace