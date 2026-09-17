Imports System.IO
Imports i00.Compression

Namespace Tests

    Partial Class StorageRepresentationPolicies

        ''' <summary>
        ''' Direct tests for the hand-rolled FSE (tANS) entropy coder that backs
        ''' <see cref="Zstd" />'s literal stream. These exercise table construction and the
        ''' encode/decode state machine in isolation from LZ77 matching, since a subtle bug
        ''' in either would otherwise be very hard to localize once buried inside a full
        ''' block round-trip.
        ''' </summary>
        Public NotInheritable Class EntropyCoding

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Table construction
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub NormalizeHistogramAlwaysSumsToTheTableSize()

                Dim Rng = CreateDeterministicRandom(5501)

                For Trial = 0 To 49

                    Dim Histogram(255) As Integer
                    Dim DistinctCount = Rng.Next(1, 257)
                    Dim Total = 0

                    For i = 0 To DistinctCount - 1
                        Dim Count = Rng.Next(1, 5000)
                        Histogram(i) = Count
                        Total += Count
                    Next

                    Dim Built = Fse.NormalizeHistogram(Histogram, Total, 10)

                    Dim Sum = 0

                    For s = 0 To 255

                        If Histogram(s) > 0 Then

                            If Built.Counts(s) < 1 Then
                                Throw New Exception($"Trial {Trial}: present symbol {s} got a normalized count below 1.")
                            End If

                        ElseIf Built.Counts(s) <> 0 Then

                            Throw New Exception($"Trial {Trial}: absent symbol {s} got a non-zero normalized count.")

                        End If

                        Sum += Built.Counts(s)

                    Next

                    If Sum <> (1 << Built.TableLog) Then
                        Throw New Exception($"Trial {Trial}: normalized counts summed to {Sum}, expected {1 << Built.TableLog}.")
                    End If

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub NormalizeHistogramRaisesTableLogToFitEveryDistinctSymbol()

                ' 200 distinct symbols cannot each get >= 1 slot in a table smaller than 256,
                ' so this must raise TableLog well past the requested value of 5.
                Dim Histogram(255) As Integer

                For s = 0 To 199
                    Histogram(s) = 1
                Next

                Dim Built = Fse.NormalizeHistogram(Histogram, 200, 5)

                If (1 << Built.TableLog) < 200 Then
                    Throw New Exception($"TableLog {Built.TableLog} gives a table too small for 200 distinct symbols.")
                End If

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub BuildTablesRejectsCountsNotSummingToTheTableSize()

                Dim Counts(255) As Integer
                Counts(0) = 10 ' TableLog 5 needs the counts to sum to 32.

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.BuildTables(Counts, 5),
                    "BuildTables should reject normalized counts that do not sum to the table size.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub BuildTablesRejectsOutOfRangeTableLog()

                Dim Counts(255) As Integer
                Counts(0) = 1 << 30

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.BuildTables(Counts, 30),
                    "BuildTables should reject a TableLog above MaxTableLog.")

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.BuildTables(Counts, 0),
                    "BuildTables should reject a TableLog below MinTableLog.")

            End Sub

            ' ================================================================================
            ' Encode/decode round-trip
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub FseRoundTripsEverySampleDistribution()

                For Each Sample In EntropySamples()

                    If Sample.Length = 0 Then Continue For

                    Dim Tables = BuildTablesFor(Sample)

                    Dim Encoded = Fse.Encode(Sample, 0, Sample.Length, Tables)
                    Dim Decoded = Fse.Decode(Encoded.Bits, Encoded.TotalBits, Encoded.InitialState, Sample.Length, Tables)

                    AssertBytesEqual(Sample, Decoded, $"FSE round-trip failed for a {Sample.Length}-byte sample.")

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseHandlesASingleRepeatedSymbolWithZeroBitsPerOccurrence()

                Dim Sample = GenerateRepeatingPatternData(5000, 1)
                Dim Tables = BuildTablesFor(Sample)

                Dim Encoded = Fse.Encode(Sample, 0, Sample.Length, Tables)

                If Encoded.TotalBits <> 0 Then
                    Throw New Exception($"A single-symbol alphabet should need zero payload bits, but used {Encoded.TotalBits}.")
                End If

                Dim Decoded = Fse.Decode(Encoded.Bits, Encoded.TotalBits, Encoded.InitialState, Sample.Length, Tables)

                AssertBytesEqual(Sample, Decoded, "FSE round-trip failed for a single-symbol alphabet.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseDecodeRejectsATruncatedBitstream()

                Dim Sample = GeneratePartiallyCompressibleDataForLength(0.3R, 4000, 512, 61)
                Dim Tables = BuildTablesFor(Sample)
                Dim Encoded = Fse.Encode(Sample, 0, Sample.Length, Tables)

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.Decode(Encoded.Bits, Encoded.TotalBits - 1, Encoded.InitialState, Sample.Length, Tables),
                    "Decode should reject a bitstream one bit shorter than what encoding actually used.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseDecodeRejectsAnOutOfRangeInitialState()

                Dim Sample = GeneratePartiallyCompressibleDataForLength(0.3R, 4000, 512, 62)
                Dim Tables = BuildTablesFor(Sample)
                Dim Encoded = Fse.Encode(Sample, 0, Sample.Length, Tables)

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.Decode(Encoded.Bits, Encoded.TotalBits, Tables.TableSize, Sample.Length, Tables),
                    "Decode should reject an initial state at or beyond the table size.")

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.Decode(Encoded.Bits, Encoded.TotalBits, -1, Sample.Length, Tables),
                    "Decode should reject a negative initial state.")

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Function BuildTablesFor(Sample As Byte()) As Fse.Tables

                Dim Histogram(255) As Integer

                For Each B In Sample
                    Histogram(B) += 1
                Next

                Dim Normalized = Fse.NormalizeHistogram(Histogram, Sample.Length, 10)

                Return Fse.BuildTables(Normalized.Counts, Normalized.TableLog)

            End Function

            Private Shared Iterator Function EntropySamples() As IEnumerable(Of Byte())

                Yield New Byte() {}
                Yield New Byte() {42}
                Yield GenerateRepeatingPatternData(5000, 1)
                Yield GenerateRepeatingPatternData(5000, 2)
                Yield GenerateRepeatingPatternData(9000, 3)
                Yield GeneratePatternData(20000, 11)
                Yield GenerateRandomData(9000, 7171)
                Yield GeneratePartiallyCompressibleDataForLength(0.2R, 12000, 4096, 81)
                Yield GeneratePartiallyCompressibleDataForLength(0.8R, 12000, 4096, 82)

                ' Every one of the 256 possible byte values present exactly once: the
                ' degenerate "uniform distribution" case where entropy coding cannot help.
                Dim AllBytes(255) As Byte

                For i = 0 To 255
                    AllBytes(i) = CByte(i)
                Next

                Yield AllBytes

            End Function

        End Class

    End Class

End Namespace
