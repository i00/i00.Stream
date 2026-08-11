Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests
    Partial Class StreamChunked
        Public NotInheritable Class ChangingOptions

            ''' <summary>
            ''' Verifies that FillHoles reuses freed chunk-record space whereas Append always
            ''' extends the physical chunk data area.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub NewChunkWriteLocationPolicy_FillHolesUsesFreedSpace()

                Dim FillStream =
                    Function(Cs As ChunkedStream) As FillStreamResult

                        For ChunkIndex = 0 To 3

                            Cs.Write(
                                ChunkIndex * ChunkedStream.DefaultChunkSize,
                                GeneratePatternData(
                                    ChunkedStream.DefaultChunkSize,
                                    100 + ChunkIndex))

                        Next

                        Dim OriginalChunk1Offset =
                            Cs.GetStructure().
                               Chunks.
                               Single(Function(chunk) chunk.Index = 1).
                               PhysicalOffset.Value

                        '
                        ' Rewrite chunk 1.
                        '
                        Cs.Write(
                            ChunkedStream.DefaultChunkSize,
                            GeneratePatternData(
                                ChunkedStream.DefaultChunkSize,
                                999))

                        '
                        ' Add a brand-new chunk.
                        '
                        Cs.Write(
                            ChunkedStream.DefaultChunkSize * 4L,
                            GeneratePatternData(
                                ChunkedStream.DefaultChunkSize,
                                1234))

                        Dim Struct = Cs.GetStructure()

                        Dim Chunk1 =
                            Struct.Chunks.
                                   Single(Function(chunk) chunk.Index = 1)

                        Dim Chunk4 =
                            Struct.Chunks.
                                   Single(Function(chunk) chunk.Index = 4)

                        Return New FillStreamResult With {
                            .OriginalChunk1Offset = OriginalChunk1Offset,
                            .Chunk1Offset = Chunk1.PhysicalOffset.Value,
                            .Chunk4Offset = Chunk4.PhysicalOffset.Value,
                            .LiveDataEndOffset = Struct.LiveDataEndOffset
                        }

                    End Function

                Dim AppendResult As FillStreamResult

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .NewChunkWriteLocationPolicy =
                            ChunkedStream.ChunkedStreamOptions.NewChunkWriteLocationPolicies.Append
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        AppendResult = FillStream(Cs)

                    End Using

                End Using

                Dim FillHolesResult As FillStreamResult

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .NewChunkWriteLocationPolicy =
                            ChunkedStream.ChunkedStreamOptions.NewChunkWriteLocationPolicies.FillHoles
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        FillHolesResult = FillStream(Cs)

                    End Using

                End Using

                '
                ' Rewrites should always move the chunk.
                '
                AssertTrue(
                    AppendResult.Chunk1Offset <> AppendResult.OriginalChunk1Offset,
                    "Append mode rewrite should move chunk 1 to a new physical location.")

                AssertTrue(
                    FillHolesResult.Chunk1Offset <> FillHolesResult.OriginalChunk1Offset,
                    "FillHoles mode rewrite should move chunk 1 to a new physical location.")

                '
                ' Append must not reuse the original chunk 1 hole.
                '
                AssertTrue(
                    AppendResult.Chunk4Offset <> AppendResult.OriginalChunk1Offset,
                    "Append mode should not reuse the hole created by the original chunk 1.")

                '
                ' FillHoles should reuse the original chunk 1 hole.
                '
                AssertEqual(
                    FillHolesResult.OriginalChunk1Offset,
                    FillHolesResult.Chunk4Offset,
                    "FillHoles mode should reuse the hole created by the original chunk 1.")

                '
                ' FillHoles should use less physical space.
                '
                AssertTrue(
                    FillHolesResult.LiveDataEndOffset < AppendResult.LiveDataEndOffset,
                    $"FillHoles should use less physical space. Append={AppendResult.LiveDataEndOffset}, FillHoles={FillHolesResult.LiveDataEndOffset}.")

            End Sub

            Private NotInheritable Class FillStreamResult

                Public Property OriginalChunk1Offset As Long

                Public Property Chunk1Offset As Long

                Public Property Chunk4Offset As Long

                Public Property LiveDataEndOffset As Long

            End Class


            ''' <summary>
            ''' Verifies that ApplyOptions can apply compression without corrupting data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionOnlyCompressesEligibleChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GenerateRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                        Cs.Options.CompressionRatioThreshold = 0.99

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                        AssertTrue(Result.RewrittenChunks > 0, "Expected ApplyOptions to rewrite at least one chunk for compression.")
                        AssertTrue(Result.CompressionChanges > 0, "Expected compression changes.")

                        Dim Struct = Cs.GetStructure()

                        AssertTrue(Struct.Chunks.Any(Function(chunk) chunk.IsCompressed),
                                   "Expected at least one compressed chunk after ApplyOptions.")

                        AssertBytesEqual(Data, Cs.ToArray(), "ApplyOptions compression corrupted data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that disabling encryption and applying encryption options removes the unused file master key.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsEncryptionDisabledRemovesUnusedFileMasterKey()

                Using Ms As New MemoryStream()

                    Dim Data = Helpers.GeneratePatternData(ChunkedStream.DefaultChunkSize * 3, 42)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(Helpers.MakeKey(42))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()

                        AssertTrue(Before.EncryptedChunkCount > 0, "Expected encrypted chunks before ApplyOptions.")
                        AssertTrue(Before.HasFileMasterKey, "Expected a file master key before ApplyOptions.")
                        AssertTrue(Before.HasWrappedFileMasterKey, "Expected a wrapped file master key before ApplyOptions.")

                        Cs.Options.EncryptionInfo = Nothing

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                        AssertTrue(Result.RewrittenChunks > 0, "Expected encrypted chunks to be rewritten.")
                        AssertTrue(Result.EncryptionChanges > 0, "Expected encryption changes.")

                        Dim After = Cs.GetStructure()

                        AssertEqual(0, After.EncryptedChunkCount, "Expected all chunks to be unencrypted after ApplyOptions.")
                        AssertTrue(Not After.HasFileMasterKey, "Expected the unused file master key to be removed.")
                        AssertTrue(Not After.HasWrappedFileMasterKey, "Expected the wrapped file master key to be removed.")

                        AssertBytesEqual(Data, Cs.ToArray(), "Data was corrupted after removing encryption.")

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Reopened = Cs.ToArray()

                        AssertBytesEqual(Data, Reopened, "Reopened unencrypted stream data mismatch.")

                        Dim ReopenedStructure = Cs.GetStructure()

                        AssertEqual(0, ReopenedStructure.EncryptedChunkCount, "Expected reopened stream to contain no encrypted chunks.")
                        AssertTrue(Not ReopenedStructure.HasFileMasterKey, "Expected reopened stream to have no file master key.")
                        AssertTrue(Not ReopenedStructure.HasWrappedFileMasterKey, "Expected reopened stream to have no wrapped file master key.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions can encrypt existing allocated chunks without changing logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsEncryptionOnlyEncryptsExistingChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GeneratePatternData(200000, 88)

                        Cs.Write(0, Data)

                        Cs.Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(88))

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                        AssertTrue(Result.RewrittenChunks > 0, "Expected ApplyOptions to rewrite at least one chunk for encryption.")
                        AssertTrue(Result.EncryptionChanges > 0, "Expected encryption changes.")

                        Dim Struct = Cs.GetStructure()

                        AssertTrue(Struct.EncryptedChunkCount > 0, "Expected encrypted chunks after ApplyOptions.")
                        AssertBytesEqual(Data, Cs.ToArray(), "ApplyOptions encryption corrupted data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions can convert physically stored zero chunks into sparse chunks.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsSparsenessConvertsStoredZeroChunkToSparse()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = True
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, GenerateZeroedData(ChunkedStream.DefaultChunkSize))

                        Dim Before = Cs.GetStructure()

                        AssertTrue(Before.Chunks.First().IsAllocated, "Expected zero chunk to initially be allocated.")
                        AssertTrue(Before.Chunks.First().IsPlaintextAllZero, "Expected zero chunk to expose PlaintextAllZero.")

                        Cs.Options.StoreSparseChunks = False

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Sparseness)

                        AssertTrue(Result.SparsenessChanges > 0, "Expected sparseness changes.")
                        AssertTrue(Result.NewlySparseChunks > 0, "Expected newly sparse chunks.")

                        Dim After = Cs.GetStructure()

                        AssertTrue(After.Chunks.First().IsSparse, "Expected zero chunk to become sparse.")
                        AssertTrue(After.Chunks.First().IsPlaintextAllZero, "Expected sparse chunk to expose PlaintextAllZero.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions can materialise sparse chunks into physical zero chunk records.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsSparsenessMaterialisesSparseChunk()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(ChunkedStream.DefaultChunkSize)

                        Dim Before = Cs.GetStructure()

                        AssertTrue(Before.Chunks.First().IsSparse, "Expected chunk to initially be sparse.")

                        Cs.Options.StoreSparseChunks = True

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Sparseness)

                        AssertTrue(Result.SparsenessChanges > 0, "Expected sparseness changes.")
                        AssertTrue(Result.NewlyAllocatedChunks > 0, "Expected newly allocated chunks.")

                        Dim After = Cs.GetStructure()

                        AssertTrue(After.Chunks.First().IsAllocated, "Expected sparse chunk to become allocated.")
                        AssertTrue(After.Chunks.First().IsPlaintextAllZero, "Expected materialised chunk to expose PlaintextAllZero.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that applying encryption only preserves existing compression metadata.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsEncryptionOnlyPreservesCompression()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.99
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()

                        AssertTrue(Before.Chunks.Any(Function(chunk) chunk.IsCompressed),
                                   "Expected compressed chunks before encryption apply.")

                        Cs.Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(91))
                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                        AssertTrue(Result.EncryptionChanges > 0, "Expected encryption changes.")

                        Dim After = Cs.GetStructure()

                        AssertTrue(After.Chunks.Any(Function(chunk) chunk.IsCompressed),
                                   "Compression should have been preserved when applying encryption only.")

                        AssertTrue(After.EncryptedChunkCount > 0, "Expected encrypted chunks after applying encryption.")
                        AssertBytesEqual(Data, Cs.ToArray(), "ApplyOptions encryption-only corrupted data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions returns a no-op result when requested options already match.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsNoOpWhenAlreadyMatching()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GeneratePatternData(100000, 44)

                        Cs.Write(0, Data)

                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.All)

                        AssertEqual(0, Result.RewrittenChunks, "Expected ApplyOptions to be a no-op.")
                        AssertBytesEqual(Data, Cs.ToArray(), "No-op ApplyOptions corrupted data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions participates correctly in an outer checkpoint.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsInsideCheckpointRollsBackWithParent()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = GenerateRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                            Cs.Options.CompressionRatioThreshold = 0.99

                            Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                            AssertTrue(Result.RewrittenChunks > 0, "Expected ApplyOptions to rewrite chunks inside checkpoint.")
                            AssertTrue(Cs.GetStructure().Chunks.Any(Function(chunk) chunk.IsCompressed),
                                       "Expected compressed chunks inside checkpoint.")

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertTrue(Not AfterRollback.Chunks.Any(Function(chunk) chunk.IsCompressed),
                                   "ApplyOptions changes should have rolled back with the parent checkpoint.")

                        AssertBytesEqual(Data, Cs.ToArray(), "Rollback after ApplyOptions corrupted data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that chunks remain uncompressed when the compression threshold is not met.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionThresholdPreventsCompression()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            Helpers.GenerateCompressableData(
                                0.5,
                                ChunkedStream.DefaultChunkSize,
                                8)

                        Cs.Write(0, Data)

                        Dim Chunk =
                            Cs.GetStructure().
                                Chunks.
                                Single()

                        AssertFalse(
                            Chunk.IsCompressed,
                            "Chunk should not have been compressed.")

                        AssertEqual(
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            Chunk.CompressionEvaluatedMethod,
                            "Compression evaluation metadata lost.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions does not repeatedly re-evaluate already-evaluated chunks.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionAlreadyEvaluatedNoOp()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            Helpers.GenerateCompressableData(
                                0.5,
                                ChunkedStream.DefaultChunkSize,
                                8)

                        Cs.Write(0, Data)

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                        AssertEqual(
                            0,
                            Result.RewrittenChunks,
                            "Chunk should already satisfy compression policy.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that lowering the threshold causes a previously evaluated chunk to become compressed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionThresholdLowered()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            Helpers.GenerateCompressableData(
                                0.5,
                                ChunkedStream.DefaultChunkSize,
                                8)

                        Cs.Write(0, Data)

                        Cs.Options.CompressionRatioThreshold = 0.8

                        Dim Result =
                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                        AssertTrue(
                            Result.RewrittenChunks > 0,
                            "Expected ApplyOptions to rewrite chunk.")

                        Dim Chunk =
                            Cs.GetStructure().
                                Chunks.
                                Single()

                        AssertTrue(
                            Chunk.IsCompressed,
                            "Chunk should now be compressed.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Data corrupted.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions only rewrites chunks whose compression decision changes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionSelectiveRewrite()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.7
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Chunks = 6
                        For i = 0 To (Chunks - 1)
                            Cs.Write(i * ChunkedStream.DefaultChunkSize,
                                 Helpers.GenerateCompressableData(
                                     i / (Chunks - 1),
                                     ChunkedStream.DefaultChunkSize,
                                     1))
                        Next

                        Dim Before = Cs.GetStructure()
                        AssertEqual(Chunks, Before.ChunkCount, "Unexpected chunk count.")

                        Dim ExpectedCompressedInitial = Before.Chunks.Where(Function(x) x.CompressionEvaluatedRatio <= Cs.Options.CompressionRatioThreshold).Count
                        AssertEqual(Before.Chunks.Where(Function(x) x.IsCompressed).Count, ExpectedCompressedInitial, $"Expected {ExpectedCompressedInitial} chunks to be initially compressed.")

                        Cs.Options.CompressionRatioThreshold = 0.3
                        Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)
                        AssertTrue(Result.RewrittenChunks <> 0, "This test should require chunks to be rewritten")
                        Dim After = Cs.GetStructure()

                        Dim ExpectedCompressedUltimately = Cs.GetStructure.Chunks.Where(Function(x) x.CompressionEvaluatedRatio <= Cs.Options.CompressionRatioThreshold).Count
                        Dim ExpectedRewrittenChunks = Math.Abs(ExpectedCompressedInitial - ExpectedCompressedUltimately)
                        AssertTrue(Result.RewrittenChunks = ExpectedRewrittenChunks, $"Expected {ExpectedRewrittenChunks} chunks to change compression state.")

                        AssertEqual(After.Chunks.Where(Function(x) x.IsCompressed).Count, ExpectedCompressedUltimately, $"Expected {ExpectedCompressedUltimately} chunks to be ultimately compressed.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rebuild defragmentation applies a changed chunk size.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentRebuildAppliesChangedChunkSize()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 32 * 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = Helpers.GenerateRandomData(300000, 123)

                        Cs.Write(0, Data)

                        AssertEqual(32 * 1024, Cs.ChunkSize, "Unexpected initial chunk size.")

                        Cs.Options.ChunkSize = 128 * 1024

                        Cs.Defragment(ChunkedStream.DefragTypes.Rebuild)

                        AssertEqual(128 * 1024, Cs.ChunkSize, "Rebuild did not apply the new chunk size.")
                        AssertBytesEqual(Data, Cs.ToArray(), "Data was corrupted after chunk-size rebuild.")

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        AssertEqual(128 * 1024, Cs.ChunkSize, "Reopened stream did not retain rebuilt chunk size.")

                    End Using

                End Using

            End Sub

        End Class
    End Class
End Namespace
