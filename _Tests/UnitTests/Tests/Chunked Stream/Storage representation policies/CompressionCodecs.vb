Imports System.IO
Imports i00.Compression

Namespace Tests

    Partial Class StorageRepresentationPolicies

        ''' <summary>
        ''' Direct tests for the custom LZ4, Snappy, Fse and Zstd block codecs:
        ''' round-tripping and, more importantly, that every malformed or truncated block
        ''' surfaces as an
        ''' <see cref="InvalidDataException" /> rather than an <see cref="OverflowException" />,
        ''' an <see cref="IndexOutOfRangeException" /> or a silently wrong result. Chunk
        ''' payloads are MAC-checked before decompression on the normal path, but a
        ''' public-key-authenticated unencrypted chunk can still be forged, so the codecs
        ''' must be robust against hostile input on their own.
        ''' </summary>
        Public NotInheritable Class CompressionCodecs

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Round-trip
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub Lz4RoundTripsEveryInputShape()

                For Each Sample In CodecSamples()
                    Dim Compressed = Lz4.Compress(Sample)
                    Dim Restored = Lz4.Decompress(Compressed, Sample.Length)
                    AssertBytesEqual(Sample, Restored, $"LZ4 round-trip failed for a {Sample.Length}-byte sample.")
                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SnappyRoundTripsEveryInputShape()

                For Each Sample In CodecSamples()
                    Dim Compressed = Snappy.Compress(Sample)
                    Dim Restored = Snappy.Decompress(Compressed)
                    AssertBytesEqual(Sample, Restored, $"Snappy round-trip failed for a {Sample.Length}-byte sample.")
                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseRoundTripsEveryInputShape()

                For Each Sample In CodecSamples()
                    Dim Compressed = Fse.Compress(Sample)
                    Dim Restored = Fse.Decompress(Compressed, Sample.Length)
                    AssertBytesEqual(Sample, Restored, $"Fse round-trip failed for a {Sample.Length}-byte sample.")
                Next

            End Sub

            ''' <summary>
            ''' Regression test for a real bug a model-based fuzz run found: when the lazy
            ''' matcher deferred to a better match at Position + 1, it added the deferred
            ''' byte to the literals buffer immediately without moving Anchor past it - so
            ''' the same byte was added a second time once the eventual literal run was
            ''' later sliced from Anchor, leaving Literals longer than what the recorded
            ''' sequences actually consume. This 22-byte input is hand-built to force
            ''' exactly that: a length-4 match at one position whose lazy lookahead finds a
            ''' longer match one byte later.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdLazyMatchDeferralDoesNotDuplicateTheDeferredLiteral()

                Dim Sample As Byte() = {1, 2, 3, 4, 5, 2, 3, 4, 6, 7, 8, 9, 10, 1, 2, 3, 4, 6, 7, 8, 9, 10}

                Dim Compressed = Zstd.Compress(Sample, Zstd.CompressionEffort.Normal)
                Dim Restored = Zstd.Decompress(Compressed, Sample.Length)

                AssertBytesEqual(Sample, Restored, "Zstd mis-handled a lazy-match deferral.")

            End Sub

            ''' <summary>
            ''' Every <see cref="Zstd.CompressionEffort" /> tier changes the LZ77 search
            ''' (chain depth, lazy matching), so each one needs its own round-trip pass - a
            ''' bug specific to, say, lazy matching would not show up at Fastest.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdRoundTripsEveryInputShapeAtEveryEffortTier()

                For Each Effort In {Zstd.CompressionEffort.Fastest,
                                    Zstd.CompressionEffort.Fast,
                                    Zstd.CompressionEffort.Normal,
                                    Zstd.CompressionEffort.High,
                                    Zstd.CompressionEffort.Best,
                                    Zstd.CompressionEffort.Maximum}

                    For Each Sample In CodecSamples()
                        Dim Compressed = Zstd.Compress(Sample, Effort)
                        Dim Restored = Zstd.Decompress(Compressed, Sample.Length)
                        AssertBytesEqual(Sample, Restored, $"Zstd({Effort}) round-trip failed for a {Sample.Length}-byte sample.")
                    Next

                Next

            End Sub

            ' ================================================================================
            ' Hostile input
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub Lz4DecompressRejectsTruncatedBlocks()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 71)
                Dim Compressed = Lz4.Compress(Plain)

                For Each Cut In TruncationPoints(Compressed.Length)

                    AssertCodecRejects(
                        Sub() Lz4.Decompress(Slice(Compressed, 0, Cut), Plain.Length),
                        $"LZ4 accepted or mis-handled a block truncated to {Cut} bytes.")

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SnappyDecompressRejectsTruncatedBlocks()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 72)
                Dim Compressed = Snappy.Compress(Plain)

                For Each Cut In TruncationPoints(Compressed.Length)

                    AssertCodecRejects(
                        Sub() Snappy.Decompress(Slice(Compressed, 0, Cut)),
                        $"Snappy accepted or mis-handled a block truncated to {Cut} bytes.")

                Next

            End Sub

            ''' <summary>
            ''' A correctness round-trip alone would still pass if Zstd silently fell back
            ''' to storing everything as raw literals - this checks the codec is actually
            ''' shrinking compressible data, exercising the LZ77 and FSE stages for real
            ''' rather than just their plumbing.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdShrinksCompressibleDataMeaningfully()

                Dim RepeatingPlain = GenerateRepeatingPatternData(20000, 37)
                Dim RepeatingCompressed = Zstd.Compress(RepeatingPlain, Zstd.CompressionEffort.Normal)

                If RepeatingCompressed.Length >= RepeatingPlain.Length \ 10 Then
                    Throw New Exception(
                        $"Zstd only compressed a highly repetitive {RepeatingPlain.Length}-byte sample to {RepeatingCompressed.Length} bytes.")
                End If

                Dim SkewedPlain = GeneratePartiallyCompressibleDataForLength(0.85R, 20000, 4096, 91)
                Dim SkewedCompressed = Zstd.Compress(SkewedPlain, Zstd.CompressionEffort.Normal)

                If SkewedCompressed.Length >= SkewedPlain.Length Then
                    Throw New Exception(
                        $"Zstd did not shrink an 85%-compressible {SkewedPlain.Length}-byte sample (got {SkewedCompressed.Length} bytes).")
                End If

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseDecompressRejectsTruncatedBlocks()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 75)
                Dim Compressed = Fse.Compress(Plain)

                For Each Cut In TruncationPoints(Compressed.Length)

                    AssertCodecRejects(
                        Sub() Fse.Decompress(Slice(Compressed, 0, Cut), Plain.Length),
                        $"Fse accepted or mis-handled a block truncated to {Cut} bytes.")

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FseDecompressRejectsTrailingGarbage()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 76)
                Dim Compressed = Fse.Compress(Plain)
                Dim WithTrailingGarbage = Concat(Compressed, New Byte() {1, 2, 3})

                AssertThrows(Of InvalidDataException)(
                    Sub() Fse.Decompress(WithTrailingGarbage, Plain.Length),
                    "Fse should reject a compressed block with trailing garbage.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdDecompressRejectsTruncatedBlocks()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 73)
                Dim Compressed = Zstd.Compress(Plain)

                For Each Cut In TruncationPoints(Compressed.Length)

                    AssertCodecRejects(
                        Sub() Zstd.Decompress(Slice(Compressed, 0, Cut), Plain.Length),
                        $"Zstd accepted or mis-handled a block truncated to {Cut} bytes.")

                Next

            End Sub

            ''' <summary>
            ''' Complements the truncation test: appending garbage after an otherwise valid
            ''' block must also be rejected, since <see cref="Zstd.Decompress" /> relies on
            ''' explicit section lengths rather than consuming input until it runs out.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdDecompressRejectsTrailingGarbage()

                Dim Plain = GeneratePartiallyCompressibleDataForLength(0.5R, 6000, 1024, 74)
                Dim Compressed = Zstd.Compress(Plain)
                Dim WithTrailingGarbage = Concat(Compressed, New Byte() {1, 2, 3})

                AssertThrows(Of InvalidDataException)(
                    Sub() Zstd.Decompress(WithTrailingGarbage, Plain.Length),
                    "Zstd should reject a compressed block with trailing garbage.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CodecsRejectRandomGarbage()

                Dim Rng = CreateDeterministicRandom(9090)

                For Trial = 0 To 39

                    Dim Garbage = GenerateRandomData(Rng.Next(1, 129), 9100 + Trial)

                    AssertCodecRejects(
                        Sub() Lz4.Decompress(Garbage, Rng.Next(0, 512)),
                        $"LZ4 mis-handled random garbage on trial {Trial}.")

                    '
                    ' Force the Snappy preamble to a small single-byte length so the codec
                    ' cannot be told to allocate a multi-gigabyte output buffer from a
                    ' forged varint; the rest of the bytes remain a garbage tag stream.
                    '
                    Garbage(0) = CByte(Garbage(0) And &H3F)

                    AssertCodecRejects(
                        Sub() Snappy.Decompress(Garbage),
                        $"Snappy mis-handled random garbage on trial {Trial}.")

                    AssertCodecRejects(
                        Sub() Zstd.Decompress(Garbage, Rng.Next(0, 512)),
                        $"Zstd mis-handled random garbage on trial {Trial}.")

                    AssertCodecRejects(
                        Sub() Fse.Decompress(Garbage, Rng.Next(0, 512)),
                        $"Fse mis-handled random garbage on trial {Trial}.")

                Next

            End Sub

            ''' <summary>
            ''' A literal-length token of 15 followed by a long run of 0xFF continuation
            ''' bytes must be rejected as invalid data, not raise an OverflowException from
            ''' the extended-length accumulator.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub Lz4RejectsExtendedLengthRunWithoutOverflowing()

                Dim Block(4000) As Byte
                Block(0) = &HF0                      ' token: literal length = 15 (extended), match length nibble = 0

                For Index = 1 To Block.Length - 1
                    Block(Index) = 255               ' continuation bytes, never terminating
                Next

                AssertThrows(Of InvalidDataException)(
                    Sub() Lz4.Decompress(Block, 1 << 20),
                    "An unterminated LZ4 extended-length run should throw InvalidDataException.")

            End Sub

            ' ================================================================================
            ' Compression effort
            ' ================================================================================

            ''' <summary>
            ''' <see cref="Zstd.ClampEffort" /> must floor any raw value that falls between two
            ''' named <see cref="Zstd.CompressionEffort" /> tiers down to the lower tier, and
            ''' clamp anything outside [Fastest, Maximum] to that nearer bound, rather than
            ''' throwing or picking the nearest tier by distance.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZstdClampEffortFloorsToTheNearestLowerTierAndClampsOutOfRange()

                Dim Cases As (Raw As Integer, Expected As Zstd.CompressionEffort)() = {
                    (-1000, Zstd.CompressionEffort.Fastest),
                    (0, Zstd.CompressionEffort.Fastest),
                    (1, Zstd.CompressionEffort.Fastest),
                    (3, Zstd.CompressionEffort.Fastest),
                    (4, Zstd.CompressionEffort.Fast),
                    (7, Zstd.CompressionEffort.Fast),
                    (8, Zstd.CompressionEffort.Normal),
                    (11, Zstd.CompressionEffort.Normal),
                    (12, Zstd.CompressionEffort.High),
                    (16, Zstd.CompressionEffort.High),
                    (17, Zstd.CompressionEffort.Best),
                    (21, Zstd.CompressionEffort.Best),
                    (22, Zstd.CompressionEffort.Maximum),
                    (23, Zstd.CompressionEffort.Maximum),
                    (9999, Zstd.CompressionEffort.Maximum)
                }

                For Each Trial In Cases

                    Dim Resolved = Zstd.ClampEffort(CType(Trial.Raw, Zstd.CompressionEffort))

                    If Resolved <> Trial.Expected Then
                        Throw New Exception(
                            $"ClampEffort({Trial.Raw}) returned {Resolved} but {Trial.Expected} was expected.")
                    End If

                Next

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Iterator Function CodecSamples() As IEnumerable(Of Byte())

                Yield New Byte() {}
                Yield New Byte() {0}
                Yield New Byte() {7, 7, 7}
                Yield GenerateZeroedData(4096)
                Yield GenerateRepeatingPatternData(9000, 3)
                Yield GenerateRepeatingPatternData(9000, 190)
                Yield GenerateRandomData(9000, 4242)
                Yield GeneratePatternData(20000, 11)
                Yield GeneratePartiallyCompressibleDataForLength(0.2R, 12000, 4096, 51)
                Yield GeneratePartiallyCompressibleDataForLength(0.8R, 12000, 4096, 52)

            End Function

            ''' <summary>
            ''' A small fixed set of truncation lengths for a block of the given size: every
            ''' length in the first 24 bytes, then about 30 points spread across the rest.
            ''' </summary>
            Private Shared Iterator Function TruncationPoints(BlockLength As Integer) As IEnumerable(Of Integer)

                Dim Head = Math.Min(24, BlockLength)

                For Cut = 0 To Head - 1
                    Yield Cut
                Next

                Dim Step_ = Math.Max(1, (BlockLength - Head) \ 30)

                For Cut = Head To BlockLength - 1 Step Step_
                    Yield Cut
                Next

            End Function

            ''' <summary>
            ''' Fails only if the action throws something other than InvalidDataException -
            ''' an OverflowException, an IndexOutOfRangeException, an ArgumentException from a
            ''' bad Buffer.BlockCopy, and so on all count as the codec mis-handling the input.
            ''' Returning normally is allowed (a truncated block can be a valid shorter one).
            ''' </summary>
            Private Shared Function Concat(First As Byte(), Second As Byte()) As Byte()

                Dim Result(First.Length + Second.Length - 1) As Byte

                Buffer.BlockCopy(First, 0, Result, 0, First.Length)
                Buffer.BlockCopy(Second, 0, Result, First.Length, Second.Length)

                Return Result

            End Function

            Private Shared Sub AssertCodecRejects(Action As Action, Message As String)

                Try
                    Action()
                Catch Ex As InvalidDataException
                    ' Expected rejection.
                Catch Ex As Exception
                    Throw New Exception($"{Message} Got {Ex.GetType().Name}: {Ex.Message}", Ex)
                End Try

            End Sub

        End Class

    End Class

End Namespace
