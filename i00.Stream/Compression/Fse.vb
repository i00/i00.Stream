Imports System.IO

Namespace Compression

    ''' <summary>
    ''' A from-scratch tANS/FSE (Finite State Entropy) byte-stream entropy coder. Used
    ''' directly as its own <c>ChunkedStreamOptions.CompressionMethods.Fse</c> chunk
    ''' compression method, and by <see cref="Zstd" /> to compress the literal bytes left
    ''' over after LZ77 matching. This models the same core algorithm real Zstandard uses
    ''' for its entropy stage, but is an independent implementation with its own
    ''' table-description and bitstream framing - it does not produce or consume a real
    ''' zstd-compatible bitstream, since nothing outside this codec ever needs to read one.
    ''' </summary>
    Friend NotInheritable Class Fse

        Public Const MinTableLog As Integer = 5
        Public Const MaxTableLog As Integer = 12

        Private Sub New()
        End Sub

        ''' <summary>
        ''' One physical slot of the decode table: which symbol to emit at this state, how
        ''' many bits to read next, and the baseline the bits just read are added to in
        ''' order to reach the following state.
        ''' </summary>
        Public Structure DecodeEntry
            Public Symbol As Byte
            Public NbBits As Byte
            Public Baseline As Integer
        End Structure

        ''' <summary>
        ''' Everything needed to encode or decode against one FSE table. Decoding only ever
        ''' reads <see cref="DecodeTable" />; the remaining fields exist to make encoding an
        ''' O(1)-per-symbol lookup instead of a search.
        ''' </summary>
        Public NotInheritable Class Tables
            Public TableLog As Integer
            Public TableSize As Integer
            Public DecodeTable As DecodeEntry()

            ' Encode-only, one entry per possible byte value (0 when the symbol is absent).
            Public NormalizedCount As Integer()
            Public CumulativeFreq As Integer()      ' length 257; CumulativeFreq(256) = TableSize
            Public NbBitsHigh As Byte()
            Public Threshold As Integer()

            ' Flat, indexed by CumulativeFreq(symbol) + (rank - NormalizedCount(symbol)).
            Public NextStateForRank As Integer()
        End Class

        ' ================================================================================
        ' Table construction
        ' ================================================================================

        ''' <summary>
        ''' Builds a normalized frequency table from a raw byte histogram: every symbol that
        ''' actually occurs gets at least one slot, and the normalized counts sum to exactly
        ''' <c>1 &lt;&lt; TableLog</c>. <paramref name="RequestedTableLog" /> is a starting
        ''' point only - it is raised as needed so every distinct symbol fits, and capped at
        ''' <see cref="MaxTableLog" />.
        ''' </summary>
        Public Shared Function NormalizeHistogram(Histogram As Integer(),
                                                  Total As Integer,
                                                  RequestedTableLog As Integer) As (Counts As Integer(), TableLog As Integer)

            If Total <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Total))

            Dim DistinctCount = 0

            For s = 0 To 255
                If Histogram(s) > 0 Then DistinctCount += 1
            Next

            Dim MinRequired = 0

            While (1 << MinRequired) < DistinctCount
                MinRequired += 1
            End While

            Dim TableLog = Math.Min(MaxTableLog, Math.Max(RequestedTableLog, Math.Max(MinTableLog, MinRequired)))
            Dim TableSize = 1 << TableLog

            Dim Counts(255) As Integer
            Dim Sum = 0

            For s = 0 To 255
                If Histogram(s) > 0 Then
                    Dim Estimate = CInt(Math.Round(Histogram(s) * CDbl(TableSize) / Total))
                    If Estimate < 1 Then Estimate = 1
                    Counts(s) = Estimate
                    Sum += Estimate
                End If
            Next

            ' Largest-remainder style correction: nudge the biggest bucket up or down, one
            ' slot at a time, until the normalized counts sum to exactly TableSize. Always
            ' terminates: TableLog was raised so TableSize >= DistinctCount, so there is
            ' always a symbol whose count can move without going below 1.
            While Sum <> TableSize

                Dim TargetSymbol = -1

                If Sum > TableSize Then

                    Dim BiggestCount = 1

                    For s = 0 To 255
                        If Counts(s) > BiggestCount Then
                            BiggestCount = Counts(s)
                            TargetSymbol = s
                        End If
                    Next

                    Counts(TargetSymbol) -= 1
                    Sum -= 1

                Else

                    Dim BiggestCount = -1

                    For s = 0 To 255
                        If Counts(s) > BiggestCount Then
                            BiggestCount = Counts(s)
                            TargetSymbol = s
                        End If
                    Next

                    Counts(TargetSymbol) += 1
                    Sum += 1

                End If

            End While

            Return (Counts, TableLog)

        End Function

        ''' <summary>
        ''' Builds the decode table plus the auxiliary per-symbol arrays that make encoding
        ''' O(1) per symbol, from a normalized count array (see <see cref="NormalizeHistogram" />,
        ''' or one parsed from a compressed block's table description).
        ''' </summary>
        Public Shared Function BuildTables(NormalizedCount As Integer(), TableLog As Integer) As Tables

            If TableLog < MinTableLog OrElse TableLog > MaxTableLog Then
                Throw New InvalidDataException("Invalid FSE table log.")
            End If

            Dim TableSize = 1 << TableLog

            Dim Result As New Tables With {
                .TableLog = TableLog,
                .TableSize = TableSize,
                .DecodeTable = New DecodeEntry(TableSize - 1) {},
                .NormalizedCount = NormalizedCount,
                .CumulativeFreq = New Integer(256) {},
                .NbBitsHigh = New Byte(255) {},
                .Threshold = New Integer(255) {},
                .NextStateForRank = New Integer(TableSize - 1) {}
            }

            Dim Running = 0

            For s = 0 To 255

                If NormalizedCount(s) < 0 OrElse NormalizedCount(s) > TableSize Then
                    Throw New InvalidDataException("Invalid FSE symbol frequency.")
                End If

                Result.CumulativeFreq(s) = Running
                Running += NormalizedCount(s)

            Next

            Result.CumulativeFreq(256) = Running

            If Running <> TableSize Then
                Throw New InvalidDataException("FSE normalized counts do not sum to the table size.")
            End If

            For s = 0 To 255

                Dim Ns = NormalizedCount(s)

                If Ns > 0 Then

                    Dim BitLength = FloorLog2(Ns)
                    Dim NbBitsHighForSymbol = TableLog - BitLength

                    Result.NbBitsHigh(s) = CByte(NbBitsHighForSymbol)
                    Result.Threshold(s) = (Ns << NbBitsHighForSymbol) - TableSize

                End If

            Next

            Dim SpreadStep = (TableSize >> 1) + (TableSize >> 3) + 3
            Dim Mask = TableSize - 1
            Dim Pos = 0
            Dim SeenCount(255) As Integer

            For s = 0 To 255

                Dim Ns = NormalizedCount(s)

                For i = 1 To Ns

                    Dim Rank = Ns + SeenCount(s)
                    Dim FlatIndex = Result.CumulativeFreq(s) + SeenCount(s)

                    SeenCount(s) += 1

                    Dim NbBits = TableLog - FloorLog2(Rank)
                    Dim Baseline = (Rank << NbBits) - TableSize

                    Result.DecodeTable(Pos) = New DecodeEntry With {.Symbol = CByte(s), .NbBits = CByte(NbBits), .Baseline = Baseline}
                    Result.NextStateForRank(FlatIndex) = Pos

                    Pos = (Pos + SpreadStep) And Mask

                Next

            Next

            If Pos <> 0 Then
                Throw New InvalidDataException("FSE table spread did not cover every slot; the table description is invalid.")
            End If

            Return Result

        End Function

        Private Shared Function FloorLog2(Value As Integer) As Integer

            Dim Result = 0
            Dim Remaining = Value

            While Remaining > 1
                Remaining >>= 1
                Result += 1
            End While

            Return Result

        End Function

        ' ================================================================================
        ' Encode / decode
        ' ================================================================================

        ''' <summary>
        ''' Entropy-codes <paramref name="Count" /> bytes starting at <paramref name="Start" />
        ''' against <paramref name="Tables" />. Symbols are processed last-to-first (an FSE
        ''' state transition can only be inverted going backwards), and the resulting bits
        ''' are then written out in the order a forward-reading decoder needs them.
        ''' </summary>
        Public Shared Function Encode(Data As Byte(), Start As Integer, Count As Integer, Tables As Tables) As (InitialState As Integer, Bits As Byte(), TotalBits As Integer)

            Dim Steps As New List(Of (Value As Integer, NbBits As Integer))(Count)
            Dim State = 0
            Dim TableSize = Tables.TableSize

            For i = Start + Count - 1 To Start Step -1

                Dim Symbol = Data(i)
                Dim Threshold = Tables.Threshold(Symbol)
                Dim NbBitsHigh = CInt(Tables.NbBitsHigh(Symbol))

                Dim NbBits = If(State >= Threshold, NbBitsHigh, NbBitsHigh - 1)
                Dim Rank = (State + TableSize) >> NbBits
                Dim Baseline = (Rank << NbBits) - TableSize
                Dim ExtraBits = State - Baseline

                Dim Ns = Tables.NormalizedCount(Symbol)
                Dim FlatIndex = Tables.CumulativeFreq(Symbol) + (Rank - Ns)

                Steps.Add((ExtraBits, NbBits))

                State = Tables.NextStateForRank(FlatIndex)

            Next

            Dim Writer As New BitWriter()

            For i = Steps.Count - 1 To 0 Step -1
                Writer.WriteBits(Steps(i).Value, Steps(i).NbBits)
            Next

            Dim Packed = Writer.ToArray()

            Return (State, Packed.Bytes, Packed.TotalBits)

        End Function

        ''' <summary>
        ''' Reverses <see cref="Encode" />: replays the state machine forward from
        ''' <paramref name="InitialState" />, emitting exactly <paramref name="Count" />
        ''' bytes. Every bit read is bounds-checked, and the initial state is validated
        ''' against the table size, so hostile/truncated input surfaces as
        ''' <see cref="InvalidDataException" /> rather than corrupting memory.
        ''' </summary>
        Public Shared Function Decode(Bits As Byte(), TotalBits As Integer, InitialState As Integer, Count As Integer, Tables As Tables) As Byte()

            If InitialState < 0 OrElse InitialState >= Tables.TableSize Then
                Throw New InvalidDataException("Invalid FSE initial state.")
            End If

            Dim Output(Count - 1) As Byte
            Dim Reader As New BitReader(Bits, TotalBits)
            Dim State = InitialState

            For i = 0 To Count - 1

                Dim Entry = Tables.DecodeTable(State)

                Output(i) = Entry.Symbol

                Dim Value = Reader.ReadBits(Entry.NbBits)

                State = Entry.Baseline + Value

            Next

            Return Output

        End Function

        ' ================================================================================
        ' Block codec (self-contained Compress/Decompress, in the same shape as
        ' Lz4.Compress/Decompress and Snappy.Compress/Decompress)
        ' ================================================================================

        Public Shared Function Compress(Data As Byte()) As Byte()

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            Return Compress(Data, 0, Data.Length)

        End Function

        ''' <summary>
        ''' Entropy-codes <paramref name="Count" /> bytes, falling back to storing them raw
        ''' whenever that turns out smaller - a uniform byte distribution (already-compressed
        ''' or encrypted-looking data) carries no exploitable skew, and the table description
        ''' itself has a small fixed cost that is not worth paying in that case.
        ''' </summary>
        Public Shared Function Compress(Data As Byte(), Start As Integer, Count As Integer) As Byte()

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            If Start < 0 OrElse Count < 0 OrElse Start > Data.Length - Count Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If

            If Count = 0 Then Return New Byte() {}

            Dim Histogram(255) As Integer

            For i = Start To Start + Count - 1
                Histogram(Data(i)) += 1
            Next

            Dim RequestedTableLog = Math.Min(MaxTableLog, Math.Max(MinTableLog, CeilLog2(Count)))
            Dim Normalized = NormalizeHistogram(Histogram, Count, RequestedTableLog)
            Dim BuiltTables = BuildTables(Normalized.Counts, Normalized.TableLog)
            Dim Encoded = Encode(Data, Start, Count, BuiltTables)

            Dim Candidate As New List(Of Byte)

            Candidate.Add(CByte(Normalized.TableLog))

            Dim DistinctCount = 0

            For s = 0 To 255
                If Normalized.Counts(s) > 0 Then DistinctCount += 1
            Next

            WriteVarint(Candidate, DistinctCount)

            For s = 0 To 255
                If Normalized.Counts(s) > 0 Then
                    Candidate.Add(CByte(s))
                    WriteVarint(Candidate, Normalized.Counts(s))
                End If
            Next

            WriteVarint(Candidate, Encoded.InitialState)
            WriteVarint(Candidate, Encoded.TotalBits)
            WriteVarint(Candidate, Encoded.Bits.Length)
            Candidate.AddRange(Encoded.Bits)

            Dim Output As New List(Of Byte)

            If Candidate.Count < Count Then
                Output.Add(1)
                Output.AddRange(Candidate)
            Else
                Output.Add(0)
                Dim Raw(Count - 1) As Byte
                Buffer.BlockCopy(Data, Start, Raw, 0, Count)
                Output.AddRange(Raw)
            End If

            Return Output.ToArray()

        End Function

        Public Shared Function Decompress(Data As Byte(), ExpectedLength As Integer) As Byte()

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))
            If ExpectedLength < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ExpectedLength))

            Dim Index = 0
            Dim Result = Decompress(Data, Index, ExpectedLength)

            If Index <> Data.Length Then
                Throw New InvalidDataException("Fse compressed block contains trailing data.")
            End If

            Return Result

        End Function

        ''' <summary>
        ''' The embeddable form of <see cref="Decompress" />: parses one Fse block starting
        ''' at <paramref name="Index" />, which is advanced past exactly the bytes consumed,
        ''' without requiring the block to be the only thing in <paramref name="Data" />.
        ''' <see cref="Zstd" /> uses this to read its literals section inline from a larger
        ''' compressed block.
        ''' </summary>
        Public Shared Function Decompress(Data As Byte(), ByRef Index As Integer, ExpectedLength As Integer) As Byte()

            If ExpectedLength = 0 Then Return New Byte() {}

            If Index >= Data.Length Then Throw New InvalidDataException("Truncated Fse block.")

            Dim Flag = Data(Index)
            Index += 1

            If Flag = 0 Then

                If ExpectedLength > Data.Length - Index Then Throw New InvalidDataException("Raw Fse payload exceeds compressed input.")

                Dim Result(ExpectedLength - 1) As Byte
                Buffer.BlockCopy(Data, Index, Result, 0, ExpectedLength)
                Index += ExpectedLength
                Return Result

            ElseIf Flag = 1 Then

                If Index >= Data.Length Then Throw New InvalidDataException("Truncated Fse table description.")

                Dim TableLog = CInt(Data(Index))
                Index += 1

                Dim DistinctCount = ReadVarint(Data, Index)

                If DistinctCount < 1 OrElse DistinctCount > 256 Then
                    Throw New InvalidDataException("Invalid FSE distinct symbol count.")
                End If

                Dim Counts(255) As Integer
                Dim Seen(255) As Boolean

                For i = 1 To DistinctCount

                    If Index >= Data.Length Then Throw New InvalidDataException("Truncated FSE table description.")

                    Dim Symbol = Data(Index)
                    Index += 1

                    If Seen(Symbol) Then Throw New InvalidDataException("Duplicate symbol in FSE table description.")

                    Seen(Symbol) = True

                    Dim Count = ReadVarint(Data, Index)

                    If Count < 1 Then Throw New InvalidDataException("Invalid FSE symbol frequency.")

                    Counts(Symbol) = Count

                Next

                Dim BuiltTables = BuildTables(Counts, TableLog)

                Dim InitialState = ReadVarint(Data, Index)
                Dim TotalBits = ReadVarint(Data, Index)
                Dim ByteLength = ReadVarint(Data, Index)

                If ByteLength < 0 OrElse ByteLength > Data.Length - Index Then
                    Throw New InvalidDataException("FSE bitstream exceeds compressed input.")
                End If

                If TotalBits < 0 OrElse TotalBits > ByteLength * 8L Then
                    Throw New InvalidDataException("Invalid FSE bit length.")
                End If

                Dim Bits(ByteLength - 1) As Byte
                Buffer.BlockCopy(Data, Index, Bits, 0, ByteLength)
                Index += ByteLength

                Return Decode(Bits, TotalBits, InitialState, ExpectedLength, BuiltTables)

            Else

                Throw New InvalidDataException("Invalid Fse block flag.")

            End If

        End Function

        Private Shared Function CeilLog2(Value As Integer) As Integer

            Dim Result = 0
            Dim Remaining = Value - 1

            While Remaining > 0
                Remaining >>= 1
                Result += 1
            End While

            Return Result

        End Function

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

                If Index >= Input.Length Then Throw New InvalidDataException("Invalid Fse varint.")

                Dim Value = CInt(Input(Index))

                Index += 1

                Dim Payload = CULng(Value And &H7F)

                If Shift = 28 AndAlso Payload > 15UL Then
                    Throw New InvalidDataException("Invalid Fse varint.")
                End If

                Result = Result Or (Payload << Shift)

                If (Value And &H80) = 0 Then

                    If Result > CULng(Integer.MaxValue) Then
                        Throw New InvalidDataException("Fse varint is too large for this implementation.")
                    End If

                    Return CInt(Result)

                End If

                Shift += 7

            Next

            Throw New InvalidDataException("Invalid Fse varint.")

        End Function

        ' ================================================================================
        ' Bit-level I/O
        ' ================================================================================

        Private NotInheritable Class BitWriter

            Private ReadOnly Bytes As New List(Of Byte)
            Private Current As Byte
            Private BitsFilled As Integer

            Public Sub WriteBits(Value As Integer, NbBits As Integer)

                For i = NbBits - 1 To 0 Step -1

                    Dim Bit = (Value >> i) And 1

                    Current = Current Or CByte(Bit << (7 - BitsFilled))
                    BitsFilled += 1

                    If BitsFilled = 8 Then
                        Bytes.Add(Current)
                        Current = 0
                        BitsFilled = 0
                    End If

                Next

            End Sub

            Public Function ToArray() As (Bytes As Byte(), TotalBits As Integer)

                Dim TotalBits = Bytes.Count * 8 + BitsFilled

                If BitsFilled > 0 Then
                    Bytes.Add(Current)
                End If

                Return (Bytes.ToArray(), TotalBits)

            End Function

        End Class

        Private NotInheritable Class BitReader

            Private ReadOnly Bytes As Byte()
            Private ReadOnly TotalBits As Integer
            Private BitPosition As Integer

            Public Sub New(Bytes As Byte(), TotalBits As Integer)
                Me.Bytes = Bytes
                Me.TotalBits = TotalBits
            End Sub

            Public Function ReadBits(NbBits As Integer) As Integer

                If NbBits = 0 Then Return 0

                If NbBits > TotalBits - BitPosition Then
                    Throw New InvalidDataException("FSE bitstream ended before the expected number of bits.")
                End If

                Dim Result = 0

                For i = 0 To NbBits - 1

                    Dim ByteIndex = BitPosition >> 3
                    Dim BitIndex = 7 - (BitPosition And 7)
                    Dim Bit = (Bytes(ByteIndex) >> BitIndex) And 1

                    Result = (Result << 1) Or Bit
                    BitPosition += 1

                Next

                Return Result

            End Function

        End Class

    End Class

End Namespace
