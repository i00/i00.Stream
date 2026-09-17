Imports System.IO

Namespace Compression

    Friend NotInheritable Class Snappy

        Private Const MinMatch As Integer = 4

        ' 12 bits gives a 4096-entry hash table.
        Private Const HashBits As Integer = 12
        Private Const HashSize As Integer = 1 << HashBits
        Private Const HashShift As Integer = 32 - HashBits

        Private Const MaxCopy1Offset As Integer = 2047
        Private Const MaxCopy2Offset As Integer = 65535

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

            If InputOffset < 0 OrElse
               Count < 0 OrElse
               InputOffset > Input.Length - Count Then

                Throw New ArgumentOutOfRangeException(NameOf(Count))

            End If

            Dim Output As New List(Of Byte)(Count + (Count \ 6) + 32)

            WritePreamble(Output, Count)

            If Count = 0 Then
                Return Output.ToArray()
            End If

            If Count < MinMatch Then
                EncodeLiteral(Output, Input, InputOffset, Count)
                Return Output.ToArray()
            End If

            Dim HashTable(HashSize - 1) As Integer

            For i = 0 To HashTable.Length - 1
                HashTable(i) = -1
            Next

            Dim Anchor = InputOffset
            Dim Position = InputOffset
            Dim EndPosition = InputOffset + Count
            Dim Limit = EndPosition - MinMatch

            While Position <= Limit

                Dim Sequence = ReadUInt32(Input, Position)
                Dim Hash = HashSequence(Sequence)

                Dim Reference = HashTable(Hash)

                HashTable(Hash) = Position

                If Reference >= InputOffset AndAlso
                   Position > Reference AndAlso
                   ReadUInt32(Input, Reference) = Sequence Then

                    Dim MatchLength = FindMatch(Input, Reference, Position, EndPosition)

                    If MatchLength >= MinMatch Then

                        Dim LiteralLength = Position - Anchor

                        If LiteralLength > 0 Then
                            EncodeLiteral(Output, Input, Anchor, LiteralLength)
                        End If

                        EncodeCopy(Output, Position - Reference, MatchLength)

                        Dim MatchEnd = Position + MatchLength

                        ' Add positions inside the match to the hash table.
                        ' This improves compression of repeated patterns after
                        ' a match while preserving the normal greedy approach.
                        Dim InsertPosition = Position + 1

                        While InsertPosition <= MatchEnd - MinMatch

                            Dim InsertSequence = ReadUInt32(Input, InsertPosition)

                            HashTable(HashSequence(InsertSequence)) = InsertPosition

                            InsertPosition += 1

                        End While

                        Position = MatchEnd
                        Anchor = Position

                        Continue While

                    End If

                End If

                Position += 1

            End While

            If Anchor < EndPosition Then
                EncodeLiteral(Output, Input, Anchor, EndPosition - Anchor)
            End If

            Return Output.ToArray()

        End Function

        Public Shared Function Decompress(Input As Byte()) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))

            Dim InputIndex = 0
            Dim ExpectedSize = ReadPreamble(Input, InputIndex)

            If ExpectedSize = 0 Then

                If InputIndex <> Input.Length Then
                    Throw New InvalidDataException("Compressed block contains data for a zero-length output.")
                End If

                Return New Byte() {}

            End If

            Dim Output(ExpectedSize - 1) As Byte
            Dim OutputIndex = 0

            While InputIndex < Input.Length

                Dim Tag = CInt(Input(InputIndex))

                InputIndex += 1

                Select Case Tag And &H3

                    Case 0

                        Dim LiteralLength = ReadLiteralLength(Input, InputIndex, Tag)

                        If LiteralLength <= 0 Then
                            Throw New InvalidDataException("Invalid Snappy literal length.")
                        End If

                        If LiteralLength > Input.Length - InputIndex Then
                            Throw New InvalidDataException("Snappy literal exceeds compressed input.")
                        End If

                        If LiteralLength > Output.Length - OutputIndex Then
                            Throw New InvalidDataException("Snappy literal exceeds expected output length.")
                        End If

                        Buffer.BlockCopy(Input, InputIndex, Output, OutputIndex, LiteralLength)

                        InputIndex += LiteralLength
                        OutputIndex += LiteralLength

                    Case 1

                        ' COPY_1:
                        '   bits 0-1 = 01
                        '   bits 2-4 = length - 4
                        '   bits 5-7 = offset bits 8-10
                        '   next byte = offset bits 0-7
                        If Input.Length - InputIndex < 1 Then
                            Throw New InvalidDataException("Invalid Snappy COPY_1 offset.")
                        End If

                        Dim MatchLength = ((Tag >> 2) And &H7) + 4
                        Dim Offset = ((Tag >> 5) << 8) Or CInt(Input(InputIndex))

                        InputIndex += 1

                        CopyFromOutput(Output, OutputIndex, Offset, MatchLength)

                        OutputIndex += MatchLength

                    Case 2

                        ' COPY_2:
                        '   bits 0-1 = 10
                        '   bits 2-7 = length - 1
                        '   next two bytes = little-endian offset
                        If Input.Length - InputIndex < 2 Then
                            Throw New InvalidDataException("Invalid Snappy COPY_2 offset.")
                        End If

                        Dim MatchLength = (Tag >> 2) + 1
                        Dim Offset = CInt(Input(InputIndex)) Or
                                     (CInt(Input(InputIndex + 1)) << 8)

                        InputIndex += 2

                        CopyFromOutput(Output, OutputIndex, Offset, MatchLength)

                        OutputIndex += MatchLength

                    Case 3

                        ' COPY_4:
                        '   bits 0-1 = 11
                        '   bits 2-7 = length - 1
                        '   next four bytes = little-endian offset
                        If Input.Length - InputIndex < 4 Then
                            Throw New InvalidDataException("Invalid Snappy COPY_4 offset.")
                        End If

                        Dim MatchLength = (Tag >> 2) + 1

                        Dim OffsetValue As ULong =
                            CULng(Input(InputIndex)) Or
                            (CULng(Input(InputIndex + 1)) << 8) Or
                            (CULng(Input(InputIndex + 2)) << 16) Or
                            (CULng(Input(InputIndex + 3)) << 24)

                        InputIndex += 4

                        ' A .NET Byte() cannot contain more than Int32.MaxValue
                        ' bytes, so such an offset cannot be valid here.
                        If OffsetValue > CULng(Integer.MaxValue) Then
                            Throw New InvalidDataException("Invalid Snappy COPY_4 offset.")
                        End If

                        CopyFromOutput(Output, OutputIndex, CInt(OffsetValue), MatchLength)

                        OutputIndex += MatchLength

                End Select

            End While

            If OutputIndex <> ExpectedSize Then
                Throw New InvalidDataException($"Snappy output length mismatch. Expected {ExpectedSize}, got {OutputIndex}.")
            End If

            Return Output

        End Function

        Private Shared Function HashSequence(Sequence As ULong) As Integer

            Return CInt(((Sequence * &H1E35A7BDUL) >> HashShift) And CULng(HashSize - 1))

        End Function

        Private Shared Function FindMatch(Input As Byte(),
                                          Reference As Integer,
                                          Position As Integer,
                                          EndPosition As Integer) As Integer

            Dim Length = 0

            While Position + Length < EndPosition AndAlso
                  Input(Reference + Length) = Input(Position + Length)

                Length += 1

                ' The reference may overlap the current position.
                ' This is intentional and provides the required RLE behaviour.
                '
                ' Once Reference + Length reaches Position, the comparison
                ' naturally starts comparing against bytes already matched.

            End While

            Return Length

        End Function

        Private Shared Sub EncodeLiteral(Output As List(Of Byte),
                                         Input As Byte(),
                                         Start As Integer,
                                         Length As Integer)

            If Length <= 0 Then Return

            EncodeLiteralChunk(Output, Input, Start, Length)

        End Sub

        Private Shared Sub EncodeLiteralChunk(Output As List(Of Byte),
                                              Input As Byte(),
                                              Start As Integer,
                                              Length As Integer)

            If Length <= 0 Then Return

            Dim EncodedLength = Length - 1

            If EncodedLength < 60 Then

                Output.Add(CByte(EncodedLength << 2))

            Else

                Dim LengthBytes = GetLengthByteCount(EncodedLength)

                Output.Add(CByte((59 + LengthBytes) << 2))

                For i = 0 To LengthBytes - 1
                    Output.Add(CByte((EncodedLength >> (8 * i)) And &HFF))
                Next

            End If

            For i = 0 To Length - 1
                Output.Add(Input(Start + i))
            Next

        End Sub

        Private Shared Sub EncodeCopy(Output As List(Of Byte),
                                      Offset As Integer,
                                      Length As Integer)

            If Offset <= 0 Then
                Throw New InvalidDataException("Invalid Snappy copy offset.")
            End If

            If Length < MinMatch Then
                Throw New InvalidDataException("Invalid Snappy copy length.")
            End If

            While Length > 0

                ' COPY_1 is the most compact representation, but only supports
                ' lengths 4..11 and offsets 1..2047.
                If Offset <= MaxCopy1Offset AndAlso
                   Length >= 4 AndAlso
                   Length <= 11 Then

                    EncodeCopy1(Output, Offset, Length)

                    Length = 0

                    Continue While

                End If

                ' COPY_2 supports lengths 1..64 and offsets up to 65535.
                If Offset <= MaxCopy2Offset Then

                    Dim ChunkLength = Math.Min(Length, 64)

                    EncodeCopy2(Output, Offset, ChunkLength)

                    Length -= ChunkLength

                    Continue While

                End If

                ' COPY_4 supports the full 32-bit offset range.
                Dim Copy4Length = Math.Min(Length, 64)

                EncodeCopy4(Output, Offset, Copy4Length)

                Length -= Copy4Length

            End While

        End Sub

        Private Shared Sub EncodeCopy1(Output As List(Of Byte),
                                       Offset As Integer,
                                       Length As Integer)

            If Offset < 1 OrElse Offset > MaxCopy1Offset Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Length < 4 OrElse Length > 11 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            Dim Tag = 1 Or
                      ((Length - 4) << 2) Or
                      ((Offset >> 8) << 5)

            Output.Add(CByte(Tag))
            Output.Add(CByte(Offset And &HFF))

        End Sub

        Private Shared Sub EncodeCopy2(Output As List(Of Byte),
                                       Offset As Integer,
                                       Length As Integer)

            If Offset < 1 OrElse Offset > MaxCopy2Offset Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Length < 1 OrElse Length > 64 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            Dim Tag = 2 Or
                      ((Length - 1) << 2)

            Output.Add(CByte(Tag))
            Output.Add(CByte(Offset And &HFF))
            Output.Add(CByte((Offset >> 8) And &HFF))

        End Sub

        Private Shared Sub EncodeCopy4(Output As List(Of Byte),
                                       Offset As Integer,
                                       Length As Integer)

            If Offset < 1 Then
                Throw New ArgumentOutOfRangeException(NameOf(Offset))
            End If

            If Length < 1 OrElse Length > 64 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            Dim Tag = 3 Or
                      ((Length - 1) << 2)

            Output.Add(CByte(Tag))

            Output.Add(CByte(Offset And &HFF))
            Output.Add(CByte((Offset >> 8) And &HFF))
            Output.Add(CByte((Offset >> 16) And &HFF))
            Output.Add(CByte((Offset >> 24) And &HFF))

        End Sub

        Private Shared Sub CopyFromOutput(Output As Byte(),
                                          OutputIndex As Integer,
                                          Offset As Integer,
                                          MatchLength As Integer)

            If Offset <= 0 Then
                Throw New InvalidDataException("Invalid Snappy copy offset.")
            End If

            If Offset > OutputIndex Then
                Throw New InvalidDataException("Snappy copy references data before the beginning of the output.")
            End If

            If MatchLength <= 0 Then
                Throw New InvalidDataException("Invalid Snappy copy length.")
            End If

            If MatchLength > Output.Length - OutputIndex Then
                Throw New InvalidDataException("Snappy copy exceeds expected output length.")
            End If

            Dim MatchPosition = OutputIndex - Offset

            ' Do NOT use Buffer.BlockCopy here.
            '
            ' Snappy explicitly permits match lengths larger than the offset.
            ' The copy therefore has to behave like an overlapping memmove/RLE
            ' operation.
            For i = 0 To MatchLength - 1
                Output(OutputIndex + i) = Output(MatchPosition + i)
            Next

        End Sub

        Private Shared Sub WritePreamble(Output As List(Of Byte),
                                         Length As Integer)

            Dim Value As UInteger = CUInt(Length)

            While Value >= 128UI

                Output.Add(CByte((Value And 127UI) Or 128UI))

                Value >>= 7

            End While

            Output.Add(CByte(Value))

        End Sub

        Private Shared Function ReadPreamble(Input As Byte(),
                                             ByRef InputIndex As Integer) As Integer

            Dim Result As ULong = 0UL
            Dim Shift = 0

            For i = 0 To 4

                If InputIndex >= Input.Length Then
                    Throw New InvalidDataException("Invalid Snappy preamble.")
                End If

                Dim Value = CInt(Input(InputIndex))

                InputIndex += 1

                Dim Payload = CULng(Value And &H7F)

                If Shift >= 32 Then
                    Throw New InvalidDataException("Invalid Snappy preamble length.")
                End If

                ' The fifth byte can only contain the low four bits of
                ' the uint32 value.
                If Shift = 28 AndAlso Payload > 15UL Then
                    Throw New InvalidDataException("Invalid Snappy preamble length.")
                End If

                Result = Result Or
                         (Payload << Shift)

                If (Value And &H80) = 0 Then

                    ' Byte() lengths are signed Int32 in .NET.
                    If Result > CULng(Integer.MaxValue) Then
                        Throw New InvalidDataException("Snappy output is too large for this implementation.")
                    End If

                    Return CInt(Result)

                End If

                Shift += 7

            Next

            Throw New InvalidDataException("Invalid Snappy preamble length.")

        End Function

        Private Shared Function ReadLiteralLength(Input As Byte(),
                                                  ByRef InputIndex As Integer,
                                                  Tag As Integer) As Integer

            Dim LengthCode = Tag >> 2

            If LengthCode < 60 Then
                Return LengthCode + 1
            End If

            Dim LengthBytes = LengthCode - 59

            If LengthBytes < 1 OrElse LengthBytes > 4 Then
                Throw New InvalidDataException("Invalid Snappy literal length.")
            End If

            If LengthBytes > Input.Length - InputIndex Then
                Throw New InvalidDataException("Invalid Snappy literal length.")
            End If

            Dim EncodedLength As ULong = 0UL

            For i = 0 To LengthBytes - 1

                EncodedLength =
                    EncodedLength Or
                    (CULng(Input(InputIndex + i)) << (8 * i))

            Next

            InputIndex += LengthBytes

            If EncodedLength >= CULng(Integer.MaxValue) Then
                Throw New InvalidDataException("Invalid Snappy literal length.")
            End If

            Return CInt(EncodedLength) + 1

        End Function

        Private Shared Function GetLengthByteCount(Length As Integer) As Integer

            If Length < (1 << 8) Then Return 1
            If Length < (1 << 16) Then Return 2
            If Length < (1 << 24) Then Return 3

            Return 4

        End Function

        Private Shared Function ReadUInt32(Buffer As Byte(),
                                           Index As Integer) As ULong

            Return CULng(Buffer(Index)) Or
                   (CULng(Buffer(Index + 1)) << 8) Or
                   (CULng(Buffer(Index + 2)) << 16) Or
                   (CULng(Buffer(Index + 3)) << 24)

        End Function

    End Class

End Namespace