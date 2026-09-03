Imports System.IO
Imports System.Linq
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class Compression

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Basic compression behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that every compression method except None round-trips correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionRoundTrip()

                For Each CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods))

                    If CompressionMethod =
                        ChunkedStream.ChunkedStreamOptions.CompressionMethods.None Then

                        Continue For

                    End If

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .CompressionMethod = CompressionMethod,
                            .CompressionRatioThreshold = 0.95R
                        }

                        Dim Expected =
                            GeneratePartiallyCompressibleData(
                                0.8R,
                                Options.ChunkSize,
                                8,
                                1000 + CInt(CompressionMethod))

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Write(0, Expected)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Compression round-trip failed. CompressionMethod={CompressionMethod}")

                            Dim Struct =
                                Cs.GetStructure()

                            AssertTrue(
                                Struct.CompressedChunkCount > 0,
                                $"Expected compressed chunks. CompressionMethod={CompressionMethod}")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that compressed data survives reopening.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenPreservesCompressedData()

                For Each CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods))

                    If CompressionMethod =
                        ChunkedStream.ChunkedStreamOptions.CompressionMethods.None Then

                        Continue For

                    End If

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .CompressionMethod = CompressionMethod,
                            .CompressionRatioThreshold = 0.95R
                        }

                        Dim Expected =
                            GeneratePartiallyCompressibleData(
                                0.8R,
                                Options.ChunkSize,
                                8,
                                2000 + CInt(CompressionMethod))

                        Using Cs = ChunkedStream.Open(Ms, Options)
                            Cs.Write(0, Expected)
                        End Using

                        Using Reopened = ChunkedStream.Open(Ms, Options)

                            AssertBytesEqual(
                                Expected,
                                Reopened.ToArray(),
                                $"Compressed data did not survive reopen. CompressionMethod={CompressionMethod}")

                            Reopened.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Compression threshold behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that chunks remain uncompressed when their evaluated ratio does not
            ''' satisfy the compression threshold.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionThresholdPreventsCompression()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GeneratePartiallyCompressibleData(
                                0.5R,
                                Cs.Options.ChunkSize,
                                1,
                                3001)

                        Cs.Write(0, Data)

                        Dim Chunk =
                            Cs.
                            GetStructure().
                            Chunks.
                            Single()

                        AssertFalse(
                            Chunk.IsCompressed,
                            "Chunk should not have been stored compressed.")

                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            Chunk.CompressionEvaluatedMethod,
                            "Compression evaluated method was not recorded.")

                        AssertTrue(
                            Chunk.CompressionEvaluatedRatio > Options.CompressionRatioThreshold,
                            "Test data unexpectedly satisfied the compression threshold.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Compression-threshold test corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that lowering the compression threshold can cause a previously
            ''' evaluated chunk to become compressed without corrupting data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionThresholdLoweredCompressesExistingChunk()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GeneratePartiallyCompressibleData(
                                0.5R,
                                Cs.Options.ChunkSize,
                                1,
                                3101)

                        Cs.Write(0, Data)

                        Dim Before =
                            Cs.GetStructure().Chunks.Single()

                        AssertFalse(
                            Before.IsCompressed,
                            "Initial chunk should not have been compressed.")

                        Cs.Options.CompressionRatioThreshold = 0.95R

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                        AssertTrue(
                            Result.RewrittenChunks > 0,
                            "Expected threshold change to rewrite the chunk.")

                        Dim After =
                            Cs.GetStructure().Chunks.Single()

                        AssertTrue(
                            After.IsCompressed,
                            "Chunk should have become compressed after lowering the effective threshold.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Compression threshold migration corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Compression-evaluation metadata
            ' ================================================================================

            ''' <summary>
            ''' Verifies that ApplyOptions does not repeatedly rewrite chunks whose compression
            ''' decision is already evaluated and still satisfies policy.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionAlreadyEvaluatedNoOp()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GeneratePartiallyCompressibleData(
                                0.5R,
                                Cs.Options.ChunkSize,
                                1,
                                3201)

                        Cs.Write(0, Data)

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                        AssertEqual(
                            0,
                            Result.RewrittenChunks,
                            "Already evaluated chunk should not have been rewritten.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "No-op compression ApplyOptions corrupted data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions only rewrites chunks whose compression decision
            ''' changes after the threshold changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionSelectiveRewrite()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.7R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkCount = 6

                        Dim Expected =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * ChunkCount)

                        For ChunkIndex = 0 To ChunkCount - 1

                            Dim Ratio =
                                CDbl(ChunkIndex) / CDbl(ChunkCount - 1)

                            Dim Data =
                                GeneratePartiallyCompressibleData(
                                    Ratio,
                                    Cs.Options.ChunkSize,
                                    1,
                                    3300 + ChunkIndex)

                            Dim Offset =
                                ChunkIndex * Cs.Options.ChunkSize

                            Cs.Write(Offset, Data)
                            Overlay(Expected, Data, Offset)

                        Next

                        Dim Before =
                            Cs.GetStructure()

                        Dim InitiallyCompressed =
                            Before.
                            Chunks.
                            Where(Function(chunk) chunk.IsCompressed).
                            Count

                        Cs.Options.CompressionRatioThreshold = 0.3R

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                        Dim After =
                            Cs.GetStructure()

                        Dim UltimatelyCompressed =
                            After.
                            Chunks.
                            Where(Function(chunk) chunk.IsCompressed).
                            Count

                        AssertEqual(
                            Math.Abs(InitiallyCompressed - UltimatelyCompressed),
                            Result.RewrittenChunks,
                            "Unexpected number of chunks rewritten after compression threshold change.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Selective compression rewrite corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Zero chunk behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that physically stored all-zero chunks retain the PlaintextAllZero
            ''' diagnostic flag when compression is enabled.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ZeroChunkWithCompressionExposesPlaintextAllZero()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = True,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateZeroedData(
                                Cs.Options.ChunkSize))

                        Dim Chunk =
                            Cs.
                            GetStructure().
                            Chunks.
                            Single()

                        AssertTrue(
                            Chunk.IsAllocated,
                            "Expected zero chunk to be physically allocated.")

                        AssertTrue(
                            Chunk.IsPlaintextAllZero,
                            "Compressed zero chunk did not expose PlaintextAllZero.")

                        AssertBytesEqual(
                            GenerateZeroedData(Cs.Options.ChunkSize),
                            Cs.ToArray(),
                            "Compressed zero chunk did not read back as zeroes.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Sampled compression evaluation
            ' ================================================================================

            ''' <summary>
            ''' With Sampled evaluation, incompressible chunks are stored as plaintext with an
            ''' estimated evaluation flag, and compressible chunks are still compressed with an
            ''' exact evaluation. Everything round-trips and survives reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SampledEvaluationFlagsSkippedChunksAndStillRoundTrips()

                Dim ChunkSize = 64 * 1024

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.8R,
                        .CompressionEvaluation = ChunkedStream.ChunkedStreamOptions.CompressionEvaluationStates.Sampled
                    }

                    ' Chunk 0: incompressible. Chunk 1: highly compressible.
                    Dim Incompressible = GenerateRandomData(ChunkSize, 7100)
                    Dim Compressible = GeneratePatternData(ChunkSize, 7101)
                    Dim Expected = CombineArrays(Incompressible, Compressible)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)
                        AssertBytesEqual(Expected, Cs.ToArray(), "Sampled-evaluation stream did not round-trip.")

                        Dim Chunks = Cs.GetStructure().Chunks.ToList()

                        AssertTrue(Chunks(0).IsCompressionEvaluationEstimated, "Incompressible chunk was not flagged as an estimated evaluation.")
                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.None,
                            Chunks(0).CompressionMethod,
                            "Incompressible chunk under Sampled evaluation should be stored as plaintext.")

                        AssertTrue(Chunks(1).IsCompressionEvaluationEstimated = False, "Compressible chunk should carry an exact (non-estimated) evaluation.")
                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            Chunks(1).CompressionMethod,
                            "Compressible chunk should still be stored compressed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Sampled-evaluation data did not survive reopen.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' ApplyOptions re-evaluates every estimated chunk in full, so after it runs no
            ''' chunk carries the estimated flag and the stored representation matches an exact
            ''' evaluation against the current options.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsResolvesSampledEstimates()

                Dim ChunkSize = 64 * 1024

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.8R,
                        .CompressionEvaluation = ChunkedStream.ChunkedStreamOptions.CompressionEvaluationStates.Sampled
                    }

                    ' A chunk whose leading sample looks incompressible but whose body compresses well:
                    ' Sampled skips and estimates it, a full evaluation would compress it.
                    Dim Head = GenerateRandomData(16 * 1024, 7200)
                    Dim Body = GeneratePatternData(ChunkSize - Head.Length, 7201)
                    Dim Expected = CombineArrays(Head, Body)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)
                        AssertTrue(Cs.GetStructure().Chunks.Single().IsCompressionEvaluationEstimated, "Expected the chunk to be estimated under Sampled evaluation.")

                        Options.CompressionEvaluation = ChunkedStream.ChunkedStreamOptions.CompressionEvaluationStates.Always
                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                        Dim Chunk = Cs.GetStructure().Chunks.Single()
                        AssertTrue(Chunk.IsCompressionEvaluationEstimated = False, "ApplyOptions left the chunk flagged as an estimated evaluation.")
                        AssertBytesEqual(Expected, Cs.ToArray(), "ApplyOptions changed the logical content of a sampled stream.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Data lost after ApplyOptions on a sampled stream.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace