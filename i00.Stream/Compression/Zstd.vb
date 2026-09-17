Imports System.IO

Namespace Compression

    ''' <summary>
    ''' A hand-rolled, Zstandard-inspired block codec: LZ77 hash-chain match finding,
    ''' with the resulting literal bytes entropy-coded via <see cref="Fse" /> and the
    ''' match descriptions (sequences) stored as plain varints. In the same spirit as
    ''' <see cref="Lz4" /> and <see cref="Snappy" />, but with an entropy stage layered on
    ''' top of the match output - this is the main reason Zstandard beats LZ4/Snappy on
    ''' ratio. This is an independent implementation, not a real zstd-compatible
    ''' bitstream: nothing outside this codec ever needs to read one.
    ''' </summary>
    Friend NotInheritable Class Zstd

        Private Const MinMatch As Integer = 4
        Private Const NiceMatchLength As Integer = 4096

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Practical compression effort presets, loosely modelled on zstd's numeric
        ''' levels. Named members are stable, but the numeric values deliberately leave
        ''' gaps between them: a caller can <c>CType</c> an arbitrary raw integer (e.g. a
        ''' user-facing "1-22" preference) into this type without matching a named member,
        ''' and <see cref="ClampEffort" /> resolves it to a real tier.
        ''' </summary>
        Public Enum CompressionEffort As Integer

            ''' <summary>Greedy matching, shallow chain search, small window. Roughly LZ4/Snappy-grade speed.</summary>
            Fastest = 1

            ''' <summary>Greedy matching with a deeper chain search.</summary>
            Fast = 4

            ''' <summary>Lazy (one-step lookahead) matching, moderate window and chain depth.</summary>
            Normal = 8

            ''' <summary>Lazy matching with a larger window and deeper search.</summary>
            High = 12

            ''' <summary>Full lazy matching, deep chains, largest practical window.</summary>
            Best = 17

            ''' <summary>Exhaustive/near-optimal parse. Significantly slower for marginal ratio gain.</summary>
            Maximum = 22

        End Enum

        ''' <summary>
        ''' Resolves any <see cref="CompressionEffort" /> value - including one built by
        ''' casting a raw integer that does not match a named member - to the nearest
        ''' defined tier at or below it. Values at or below <see cref="CompressionEffort.Fastest" />
        ''' clamp up to <see cref="CompressionEffort.Fastest" />; values at or above
        ''' <see cref="CompressionEffort.Maximum" /> clamp down to <see cref="CompressionEffort.Maximum" />,
        ''' matching zstd's own out-of-range clamping rather than throwing.
        ''' </summary>
        ''' <remarks>
        ''' A fixed cascade of comparisons against the (small, known) set of named tiers,
        ''' not a loop over <see cref="[Enum].GetValues" /> - that reflects over the enum's
        ''' metadata and allocates a new array on every call, which matters here since this
        ''' runs once per compressed block.
        ''' </remarks>
        Public Shared Function ClampEffort(Effort As CompressionEffort) As CompressionEffort

            Dim Raw = CInt(Effort)

            If Raw < CInt(CompressionEffort.Fast) Then Return CompressionEffort.Fastest
            If Raw < CInt(CompressionEffort.Normal) Then Return CompressionEffort.Fast
            If Raw < CInt(CompressionEffort.High) Then Return CompressionEffort.Normal
            If Raw < CInt(CompressionEffort.Best) Then Return CompressionEffort.High
            If Raw < CInt(CompressionEffort.Maximum) Then Return CompressionEffort.Best

            Return CompressionEffort.Maximum

        End Function

        Private Structure EffortParameters
            Public HashBits As Integer
            Public ChainDepth As Integer
            Public UseLazyMatching As Boolean
        End Structure

        Private Shared Function GetParameters(Effort As CompressionEffort) As EffortParameters

            Select Case ClampEffort(Effort)

                Case CompressionEffort.Fastest
                    Return New EffortParameters With {.HashBits = 15, .ChainDepth = 1, .UseLazyMatching = False}

                Case CompressionEffort.Fast
                    Return New EffortParameters With {.HashBits = 15, .ChainDepth = 8, .UseLazyMatching = False}

                Case CompressionEffort.Normal
                    Return New EffortParameters With {.HashBits = 16, .ChainDepth = 32, .UseLazyMatching = True}

                Case CompressionEffort.High
                    Return New EffortParameters With {.HashBits = 17, .ChainDepth = 128, .UseLazyMatching = True}

                Case CompressionEffort.Best
                    Return New EffortParameters With {.HashBits = 17, .ChainDepth = 512, .UseLazyMatching = True}

                Case Else ' Maximum
                    Return New EffortParameters With {.HashBits = 18, .ChainDepth = 4096, .UseLazyMatching = True}

            End Select

        End Function

        ' ================================================================================
        ' Public API
        ' ================================================================================

        Public Shared Function Compress(Input As Byte(), Optional Effort As CompressionEffort = CompressionEffort.Normal) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))

            Return Compress(Input, 0, Input.Length, Effort)

        End Function

        Public Shared Function Compress(Input As Byte(),
                                        InputOffset As Integer,
                                        Count As Integer,
                                        Optional Effort As CompressionEffort = CompressionEffort.Normal) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))

            If InputOffset < 0 OrElse
               Count < 0 OrElse
               InputOffset > Input.Length - Count Then

                Throw New ArgumentOutOfRangeException(NameOf(Count))

            End If

            Dim Output As New List(Of Byte)

            WriteVarint(Output, Count)

            If Count = 0 Then Return Output.ToArray()

            Dim Parameters = GetParameters(Effort)
            Dim Matched = RunLz77(Input, InputOffset, Count, Parameters)

            WriteVarint(Output, Matched.Literals.Length)
            WriteVarint(Output, Matched.Sequences.Count)

            WriteLiteralsSection(Output, Matched.Literals)
            WriteSequences(Output, Matched.Sequences)

            Return Output.ToArray()

        End Function

        Public Shared Function Decompress(Input As Byte(), ExpectedLength As Integer) As Byte()

            If Input Is Nothing Then Throw New ArgumentNullException(NameOf(Input))
            If ExpectedLength < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ExpectedLength))

            Dim Index = 0
            Dim OriginalLength = ReadVarint(Input, Index)

            If OriginalLength <> ExpectedLength Then
                Throw New InvalidDataException("Zstd block's original length does not match the expected length.")
            End If

            If ExpectedLength = 0 Then
                If Index <> Input.Length Then Throw New InvalidDataException("Zstd block contains data for a zero-length output.")
                Return New Byte() {}
            End If

            Dim LiteralsLength = ReadVarint(Input, Index)
            Dim SequenceCount = ReadVarint(Input, Index)

            If LiteralsLength < 0 OrElse LiteralsLength > ExpectedLength Then
                Throw New InvalidDataException("Invalid Zstd literals length.")
            End If

            If SequenceCount < 0 Then
                Throw New InvalidDataException("Invalid Zstd sequence count.")
            End If

            Dim Literals = ReadLiteralsSection(Input, Index, LiteralsLength)
            Dim Result = ReadSequencesAndReconstruct(Input, Index, SequenceCount, Literals, ExpectedLength)

            If Index <> Input.Length Then
                Throw New InvalidDataException("Zstd compressed block contains trailing data.")
            End If

            Return Result

        End Function

        ' ================================================================================
        ' LZ77 stage
        ' ================================================================================

        Private Shared Function RunLz77(Input As Byte(), InputOffset As Integer, Count As Integer, Parameters As EffortParameters) _
            As (Literals As Byte(), Sequences As List(Of (LiteralRunLength As Integer, MatchLength As Integer, MatchOffset As Integer)))

            Dim Literals As New List(Of Byte)(Count)
            Dim Sequences As New List(Of (LiteralRunLength As Integer, MatchLength As Integer, MatchOffset As Integer))
            Dim EndPosition = InputOffset + Count

            If Count < MinMatch Then
                Literals.AddRange(SliceBytes(Input, InputOffset, Count))
                Sequences.Add((Count, 0, 0))
                Return (Literals.ToArray(), Sequences)
            End If

            Dim HashBits = Parameters.HashBits
            Dim HashSize = 1 << HashBits
            Dim HashShift = 32 - HashBits
            Dim HashMask = HashSize - 1

            Dim HashTable(HashSize - 1) As Integer

            For i = 0 To HashTable.Length - 1
                HashTable(i) = -1
            Next

            Dim Chain(Count - 1) As Integer

            Dim Anchor = InputOffset
            Dim Position = InputOffset
            Dim MatchLimit = EndPosition - MinMatch

            While Position <= MatchLimit

                Dim Best = FindBestMatch(Input, Position, EndPosition, InputOffset, HashTable, Chain, HashShift, HashMask, Parameters.ChainDepth)

                InsertPosition(Input, Position, InputOffset, HashTable, Chain, HashShift, HashMask)

                If Best.Length >= MinMatch Then

                    If Parameters.UseLazyMatching AndAlso Position + 1 <= MatchLimit Then

                        Dim NextBest = FindBestMatch(Input, Position + 1, EndPosition, InputOffset, HashTable, Chain, HashShift, HashMask, Parameters.ChainDepth)

                        If NextBest.Length > Best.Length Then
                            ' Defer to the better match at Position + 1. Anchor stays put,
                            ' so the byte at Position is picked up later by the ordinary
                            ' Anchor-based literal run slice - it must not be added here
                            ' too, or it gets double-counted into Literals.
                            Position += 1
                            Continue While
                        End If

                    End If

                    Dim LiteralRunLength = Position - Anchor

                    If LiteralRunLength > 0 Then
                        Literals.AddRange(SliceBytes(Input, Anchor, LiteralRunLength))
                    End If

                    Sequences.Add((LiteralRunLength, Best.Length, Best.Offset))

                    Dim MatchEnd = Position + Best.Length
                    Dim InsertPos = Position + 1
                    Dim InsertLimit = Math.Min(MatchEnd - 1, MatchLimit)

                    While InsertPos <= InsertLimit
                        InsertPosition(Input, InsertPos, InputOffset, HashTable, Chain, HashShift, HashMask)
                        InsertPos += 1
                    End While

                    Position = MatchEnd
                    Anchor = Position

                    Continue While

                End If

                Position += 1

            End While

            If Anchor < EndPosition Then
                Dim LiteralRunLength = EndPosition - Anchor
                Literals.AddRange(SliceBytes(Input, Anchor, LiteralRunLength))
                Sequences.Add((LiteralRunLength, 0, 0))
            End If

            Return (Literals.ToArray(), Sequences)

        End Function

        Private Shared Function FindBestMatch(Input As Byte(),
                                              Position As Integer,
                                              EndPosition As Integer,
                                              InputOffset As Integer,
                                              HashTable As Integer(),
                                              Chain As Integer(),
                                              HashShift As Integer,
                                              HashMask As Integer,
                                              ChainDepth As Integer) As (Length As Integer, Offset As Integer)

            Dim Hash = ComputeHash(Input, Position, HashShift, HashMask)
            Dim Candidate = HashTable(Hash)
            Dim BestLength = 0
            Dim BestOffset = 0
            Dim Steps = 0
            Dim MaxPossibleLength = EndPosition - Position

            While Candidate >= InputOffset AndAlso Candidate < Position AndAlso Steps < ChainDepth

                Dim Length = MeasureMatch(Input, Candidate, Position, EndPosition)

                If Length > BestLength Then
                    BestLength = Length
                    BestOffset = Position - Candidate

                    If BestLength >= MaxPossibleLength OrElse BestLength >= NiceMatchLength Then
                        Exit While
                    End If

                End If

                Candidate = Chain(Candidate - InputOffset)
                Steps += 1

            End While

            Return (BestLength, BestOffset)

        End Function

        Private Shared Sub InsertPosition(Input As Byte(),
                                          Position As Integer,
                                          InputOffset As Integer,
                                          HashTable As Integer(),
                                          Chain As Integer(),
                                          HashShift As Integer,
                                          HashMask As Integer)

            Dim Hash = ComputeHash(Input, Position, HashShift, HashMask)

            Chain(Position - InputOffset) = HashTable(Hash)
            HashTable(Hash) = Position

        End Sub

        Private Shared Function ComputeHash(Input As Byte(), Position As Integer, HashShift As Integer, HashMask As Integer) As Integer

            Dim Sequence = ReadUInt32(Input, Position)

            Return CInt(((Sequence * &H9E3779B1UL) >> HashShift) And CULng(HashMask))

        End Function

        Private Shared Function MeasureMatch(Input As Byte(), Reference As Integer, Position As Integer, EndPosition As Integer) As Integer

            Dim Length = 0

            While Position + Length < EndPosition AndAlso
                  Input(Reference + Length) = Input(Position + Length)

                Length += 1

            End While

            Return Length

        End Function

        Private Shared Function SliceBytes(Input As Byte(), Start As Integer, Length As Integer) As Byte()

            Dim Result(Length - 1) As Byte
            Buffer.BlockCopy(Input, Start, Result, 0, Length)
            Return Result

        End Function

        Private Shared Function ReadUInt32(Buffer As Byte(), Index As Integer) As ULong

            Return CULng(Buffer(Index)) Or
                   (CULng(Buffer(Index + 1)) << 8) Or
                   (CULng(Buffer(Index + 2)) << 16) Or
                   (CULng(Buffer(Index + 3)) << 24)

        End Function

        ' ================================================================================
        ' Literals section - delegates entirely to Fse's own block codec, which already
        ' implements "raw or FSE-coded, whichever is smaller" plus the table-description
        ' framing and validation.
        ' ================================================================================

        Private Shared Sub WriteLiteralsSection(Output As List(Of Byte), Literals As Byte())

            Output.AddRange(Fse.Compress(Literals, 0, Literals.Length))

        End Sub

        Private Shared Function ReadLiteralsSection(Input As Byte(), ByRef Index As Integer, LiteralsLength As Integer) As Byte()

            Return Fse.Decompress(Input, Index, LiteralsLength)

        End Function

        ' ================================================================================
        ' Sequences section
        ' ================================================================================

        Private Shared Sub WriteSequences(Output As List(Of Byte),
                                          Sequences As List(Of (LiteralRunLength As Integer, MatchLength As Integer, MatchOffset As Integer)))

            For Each Sequence In Sequences

                WriteVarint(Output, Sequence.LiteralRunLength)
                WriteVarint(Output, Sequence.MatchLength)

                If Sequence.MatchLength > 0 Then
                    WriteVarint(Output, Sequence.MatchOffset)
                End If

            Next

        End Sub

        Private Shared Function ReadSequencesAndReconstruct(Input As Byte(),
                                                            ByRef Index As Integer,
                                                            SequenceCount As Integer,
                                                            Literals As Byte(),
                                                            ExpectedLength As Integer) As Byte()

            Dim Output(ExpectedLength - 1) As Byte
            Dim OutputIndex = 0
            Dim LiteralsIndex = 0

            For SequenceIndex = 1 To SequenceCount

                Dim LiteralRunLength = ReadVarint(Input, Index)

                If LiteralRunLength < 0 Then Throw New InvalidDataException("Invalid Zstd literal run length.")
                If LiteralRunLength > Literals.Length - LiteralsIndex Then Throw New InvalidDataException("Zstd sequence consumes more literals than are available.")
                If LiteralRunLength > Output.Length - OutputIndex Then Throw New InvalidDataException("Zstd literal run exceeds expected output length.")

                Buffer.BlockCopy(Literals, LiteralsIndex, Output, OutputIndex, LiteralRunLength)

                LiteralsIndex += LiteralRunLength
                OutputIndex += LiteralRunLength

                Dim MatchLength = ReadVarint(Input, Index)

                If MatchLength < 0 Then Throw New InvalidDataException("Invalid Zstd match length.")

                If MatchLength > 0 Then

                    Dim MatchOffset = ReadVarint(Input, Index)

                    If MatchOffset <= 0 Then Throw New InvalidDataException("Invalid Zstd match offset.")
                    If MatchOffset > OutputIndex Then Throw New InvalidDataException("Zstd match references data before the beginning of the output.")
                    If MatchLength > Output.Length - OutputIndex Then Throw New InvalidDataException("Zstd match exceeds expected output length.")

                    Dim MatchPosition = OutputIndex - MatchOffset

                    ' Matches may legitimately overlap the current position (RLE-style), so
                    ' this cannot use Buffer.BlockCopy - see Snappy.CopyFromOutput.
                    For i = 0 To MatchLength - 1
                        Output(OutputIndex + i) = Output(MatchPosition + i)
                    Next

                    OutputIndex += MatchLength

                End If

            Next

            If OutputIndex <> ExpectedLength Then
                Throw New InvalidDataException($"Zstd output length mismatch. Expected {ExpectedLength}, got {OutputIndex}.")
            End If

            If LiteralsIndex <> Literals.Length Then
                Throw New InvalidDataException("Zstd block did not consume every literal byte.")
            End If

            Return Output

        End Function

        ' ================================================================================
        ' Varint helpers
        ' ================================================================================

        Private Shared Sub WriteVarint(Output As List(Of Byte), Value As Integer)

            Dim Remaining As UInteger = CUInt(Value)

            While Remaining >= 128UI

                Output.Add(CByte((Remaining And 127UI) Or 128UI))

                Remaining >>= 7

            End While

            Output.Add(CByte(Remaining))

        End Sub

        Private Shared Function ReadVarint(Input As Byte(), ByRef Index As Integer) As Integer

            Dim Result As ULong = 0UL
            Dim Shift = 0

            For i = 0 To 4

                If Index >= Input.Length Then Throw New InvalidDataException("Invalid Zstd varint.")

                Dim Value = CInt(Input(Index))

                Index += 1

                Dim Payload = CULng(Value And &H7F)

                If Shift = 28 AndAlso Payload > 15UL Then
                    Throw New InvalidDataException("Invalid Zstd varint.")
                End If

                Result = Result Or (Payload << Shift)

                If (Value And &H80) = 0 Then

                    If Result > CULng(Integer.MaxValue) Then
                        Throw New InvalidDataException("Zstd varint is too large for this implementation.")
                    End If

                    Return CInt(Result)

                End If

                Shift += 7

            Next

            Throw New InvalidDataException("Invalid Zstd varint.")

        End Function

    End Class

End Namespace
