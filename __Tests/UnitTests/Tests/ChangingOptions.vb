Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests
    Partial Class StreamChunked
        Public NotInheritable Class ChangingOptions

            ''' <summary>
            ''' Verifies that ApplyOptions can apply compression without corrupting data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsCompressionOnlyCompressesEligibleChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakeRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                        Cs.Options.CompressionMinimumSavingsPercent = 1

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
            ''' Verifies that ApplyOptions can encrypt existing allocated chunks without changing logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsEncryptionOnlyEncryptsExistingChunks()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data = MakePattern(200000, 88)

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

                        Cs.Write(0, MakeBuffer(ChunkedStream.ChunkSize))

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

                        Cs.SetLength(ChunkedStream.ChunkSize)

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
                        .CompressionMinimumSavingsPercent = 1
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = MakeRepeatingPattern(200000, 8)

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

                        Dim Data = MakePattern(100000, 44)

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

                        Dim Data = MakeRepeatingPattern(200000, 8)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                            Cs.Options.CompressionMinimumSavingsPercent = 1

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

        End Class
    End Class
End Namespace
