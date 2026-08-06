Imports System
Imports System.Collections.Generic
Imports System.IO

Namespace Streams

    Friend NotInheritable Class Lz4Block

        Private Const MinMatch As Integer = 4
        Private Const HashBits As Integer = 12
        Private Const HashSize As Integer = 1 << HashBits
        Private Const HashShift As Integer = 32 - HashBits

        Private Sub New()
        End Sub

        Public Shared Function Compress(Input As Byte()) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))

            Return Compress(Input, 0, Input.Length)

        End Function

        Public Shared Function Compress(Input As Byte(),
                                        InputOffset As Integer,
                                        Count As Integer) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
            If InputOffset < 0 OrElse Count < 0 OrElse Input.Length - InputOffset < Count Then Throw New ArgumentOutOfRangeException(NameOf(Count))
            If Count = 0 Then Return New Byte() {}

            If Count < MinMatch Then
                Return EncodeOnlyLiterals(Input, InputOffset, Count)
            End If

            Dim HashTable(HashSize - 1) As Integer

            For i = 0 To HashTable.Length - 1
                HashTable(i) = -1
            Next

            Dim Output As New List(Of Byte)(Count + (Count \ 255) + 16)

            Dim Anchor = InputOffset
            Dim Position = InputOffset
            Dim EndPosition = InputOffset + Count
            Dim Limit = EndPosition - MinMatch

            While Position <= Limit
                Dim Sequence = ReadUInt32(Input, Position)
                Dim Hash = CInt(((Sequence * &H9E3779B1UL) >> HashShift) And CULng(HashSize - 1))

                Dim Reference = HashTable(Hash)
                HashTable(Hash) = Position

                If Reference >= InputOffset AndAlso Position - Reference <= UInt16.MaxValue AndAlso ReadUInt32(Input, Reference) = Sequence Then
                    Dim LiteralLength = Position - Anchor
                    Dim MatchLength = MinMatch

                    While Position + MatchLength < EndPosition AndAlso Input(Reference + MatchLength) = Input(Position + MatchLength)
                        MatchLength += 1
                    End While

                    EncodeSequence(Output, Input, Anchor, LiteralLength, Position - Reference, MatchLength)

                    Position += MatchLength
                    Anchor = Position
                Else
                    Position += 1
                End If
            End While

            EncodeLastLiterals(Output, Input, Anchor, EndPosition - Anchor)

            Return Output.ToArray()

        End Function

        Public Shared Function Decompress(Input As Byte(), ExpectedSize As Integer) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
            If ExpectedSize < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ExpectedSize))

            If ExpectedSize = 0 Then
                If Input.Length <> 0 Then Throw New InvalidDataException("Compressed block contains data for a zero-length output.")
                Return New Byte() {}
            End If

            Dim Output(ExpectedSize - 1) As Byte
            Dim InputIndex = 0
            Dim OutputIndex = 0

            While InputIndex < Input.Length
                Dim Token = CInt(Input(InputIndex))
                InputIndex += 1

                Dim LiteralLength = Token >> 4

                If LiteralLength = 15 Then
                    LiteralLength += ReadExtendedLength(Input, InputIndex)
                End If

                If InputIndex + LiteralLength > Input.Length Then Throw New InvalidDataException("Invalid LZ4 literal length.")
                If OutputIndex + LiteralLength > Output.Length Then Throw New InvalidDataException("LZ4 literal exceeds expected output length.")

                System.Buffer.BlockCopy(Input, InputIndex, Output, OutputIndex, LiteralLength)

                InputIndex += LiteralLength
                OutputIndex += LiteralLength

                If InputIndex >= Input.Length Then Exit While

                If InputIndex + 2 > Input.Length Then Throw New InvalidDataException("Invalid LZ4 match offset.")

                Dim Offset = CInt(Input(InputIndex)) Or (CInt(Input(InputIndex + 1)) << 8)
                InputIndex += 2

                If Offset <= 0 OrElse Offset > OutputIndex Then Throw New InvalidDataException("Invalid LZ4 match offset.")

                Dim MatchLength = Token And &HF

                If MatchLength = 15 Then
                    MatchLength += ReadExtendedLength(Input, InputIndex)
                End If

                MatchLength += MinMatch

                If OutputIndex + MatchLength > Output.Length Then Throw New InvalidDataException("LZ4 match exceeds expected output length.")

                Dim MatchPosition = OutputIndex - Offset

                For i = 0 To MatchLength - 1
                    Output(OutputIndex + i) = Output(MatchPosition + i)
                Next

                OutputIndex += MatchLength
            End While

            If OutputIndex <> ExpectedSize Then
                Throw New InvalidDataException($"LZ4 output length mismatch. Expected {ExpectedSize}, got {OutputIndex}.")
            End If

            Return Output

        End Function

        Private Shared Function EncodeOnlyLiterals(Input As Byte(),
                                                   Start As Integer,
                                                   Length As Integer) As Byte()

            Dim Output As New List(Of Byte)(Length + 16)
            EncodeLastLiterals(Output, Input, Start, Length)
            Return Output.ToArray()

        End Function

        Private Shared Sub EncodeSequence(Output As List(Of Byte),
                                          Input As Byte(),
                                          LiteralStart As Integer,
                                          LiteralLength As Integer,
                                          Offset As Integer,
                                          MatchLength As Integer)

            Dim TokenIndex = Output.Count
            Output.Add(0)

            Dim Token As Integer

            If LiteralLength < 15 Then
                Token = LiteralLength << 4
            Else
                Token = 15 << 4
            End If

            If LiteralLength >= 15 Then
                WriteLength(Output, LiteralLength - 15)
            End If

            For i = 0 To LiteralLength - 1
                Output.Add(Input(LiteralStart + i))
            Next

            Output.Add(CByte(Offset And &HFF))
            Output.Add(CByte((Offset >> 8) And &HFF))

            Dim EncodedMatchLength = MatchLength - MinMatch

            If EncodedMatchLength < 15 Then
                Token = Token Or EncodedMatchLength
            Else
                Token = Token Or 15
            End If

            Output(TokenIndex) = CByte(Token)

            If EncodedMatchLength >= 15 Then
                WriteLength(Output, EncodedMatchLength - 15)
            End If

        End Sub

        Private Shared Sub EncodeLastLiterals(Output As List(Of Byte),
                                              Input As Byte(),
                                              Start As Integer,
                                              Length As Integer)

            If Length < 15 Then
                Output.Add(CByte(Length << 4))
            Else
                Output.Add(&HF0)
                WriteLength(Output, Length - 15)
            End If

            For i = 0 To Length - 1
                Output.Add(Input(Start + i))
            Next

        End Sub

        Private Shared Sub WriteLength(Output As List(Of Byte), Length As Integer)

            While Length >= 255
                Output.Add(255)
                Length -= 255
            End While

            Output.Add(CByte(Length))

        End Sub

        Private Shared Function ReadExtendedLength(Input As Byte(),
                                                   ByRef InputIndex As Integer) As Integer

            Dim Length = 0

            While True
                If InputIndex >= Input.Length Then Throw New InvalidDataException("Invalid LZ4 extended length.")

                Dim Value = CInt(Input(InputIndex))
                InputIndex += 1

                Length += Value

                If Value <> 255 Then Exit While
            End While

            Return Length

        End Function

        Private Shared Function ReadUInt32(Buffer As Byte(), Index As Integer) As ULong

            Return CULng(Buffer(Index)) Or
                   (CULng(Buffer(Index + 1)) << 8) Or
                   (CULng(Buffer(Index + 2)) << 16) Or
                   (CULng(Buffer(Index + 3)) << 24)

        End Function

    End Class

End Namespace