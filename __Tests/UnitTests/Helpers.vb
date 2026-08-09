Imports StreamEncryption.Streams

Namespace Tests

    Public Module Helpers

        Friend Function CreateFragmentedContent(Cs As ChunkedStream) As Byte()

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

        Friend Function MakeBuffer(Length As Integer) As Byte()

            If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
            If Length = 0 Then Return New Byte() {}

            Return New Byte(Length - 1) {}

        End Function

        Friend Function MakePattern(Length As Integer,
                                     Seed As Integer) As Byte()

            Dim Result = MakeBuffer(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Index * 31) + Seed + (Index \ 7)) And &HFF)
            Next

            Return Result

        End Function

        Friend Function MakeRandomData(Length As Integer,
                                       Seed As Integer) As Byte()

            Dim Result = MakeBuffer(Length)

            Dim rng As New Random(Seed)

            For i = 0 To Result.Length - 1
                Result(i) = CByte(rng.Next(256))
            Next
            Return Result

        End Function

        Friend Function MakeCompressableData(CompressableRatio As Double,
                                             Length As Integer,
                                             Seed As Integer) As Byte()

            Dim RandomDataPartLen = CInt(Length * CompressableRatio)
            Dim PatternPartLen = Length - RandomDataPartLen
            Dim Result = MakeRandomData(RandomDataPartLen, Seed).Concat(
                         MakeRepeatingPattern(PatternPartLen, Seed)).
                         ToArray()

            Return Result

        End Function

        Friend Function MakeRepeatingPattern(Length As Integer,
                                              Period As Integer) As Byte()

            If Period <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Period))

            Dim Result = MakeBuffer(Length)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte((Index Mod Period) + 65)
            Next

            Return Result

        End Function

        Friend Function MakeKey(Seed As Integer) As Byte()

            Dim Result = MakeBuffer(32)

            For Index = 0 To Result.Length - 1
                Result(Index) = CByte(((Seed * 17) + (Index * 13)) And &HFF)
            Next

            Return Result

        End Function

        Friend Function BytesEqual(Left As Byte(),
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

        Friend Sub AssertBytesEqual(Expected As Byte(),
                                     Actual As Byte(),
                                     Message As String)

            If BytesEqual(Expected, Actual) Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertEqual(Of TValue)(Expected As TValue,
                                           Actual As TValue,
                                           Message As String)

            If EqualityComparer(Of TValue).Default.Equals(Expected, Actual) Then Return

            Throw New Exception($"{Message} Expected={Expected}, Actual={Actual}.")

        End Sub

        Friend Sub AssertTrue(Value As Boolean,
                               Message As String)

            If Value Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertNotNothing(Value As Object,
                                     Message As String)

            If Value IsNot Nothing Then Return

            Throw New Exception(Message)

        End Sub

        Friend Sub AssertThrows(Of T As Exception)(Action As Action,
                                                    Message As String)

            Try
                Action()
            Catch Ex As Exception When TypeOf Ex Is T
                Return
            End Try

            Throw New Exception(Message)

        End Sub

    End Module

End Namespace
