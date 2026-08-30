Imports System.IO
Imports i00.Compression

Namespace Tests

    Partial Class StorageRepresentationPolicies

        ''' <summary>
        ''' Direct tests for the custom LZ4 and Snappy block codecs: round-tripping and, more
        ''' importantly, that every malformed or truncated block surfaces as an
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
