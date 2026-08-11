Imports System.IO
Imports System.Reflection
Imports StreamEncryption.Streams

Namespace Tests

    Partial Class StreamChunked

        Public NotInheritable Class ChunkedStreamRecoveryInteractionTests

            Private Sub New()
            End Sub

            ''' <summary>
            ''' Verifies that an active checkpoint is recovered by rolling back uncommitted data on next open.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryCheckpointActiveRollsBackOnOpen()

                Using Ms As New MemoryStream()

                    Dim Original = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 1)
                    Dim Updated = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 2)

                    Dim Cs = ChunkedStream.Open(Ms)

                    Cs.Write(0, Original)

                    Dim AbandonedCheckpoint = Cs.CreateCheckpoint()

                    Cs.Write(0, Updated)

                    GC.KeepAlive(AbandonedCheckpoint)
                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(Original,
                                         Reopened.ToArray(),
                                         "Checkpoint recovery did not roll back uncommitted data.")

                        AssertEqual(Original.Length,
                                    CInt(Reopened.Length),
                                    "Recovered stream length was incorrect.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that incomplete chunk-size rebuild recovery truncates abandoned rebuild output on next open.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryChunkSizeRebuildRollsBackOnOpen()

                Using Ms As New MemoryStream()

                    Dim Original = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 3)
                    Dim OriginalPhysicalLength As Long

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Original)

                        OriginalPhysicalLength = Ms.Length

                        'TODO: revise this:
                        InvokePrivateSub(Cs,
                                         "WriteChunkSizeRebuildRecoveryState",
                                         OriginalPhysicalLength)

                        Ms.Position = Ms.Length

                        Dim AbandonedData = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 4)

                        Ms.Write(AbandonedData, 0, AbandonedData.Length)

                        AssertTrue(Ms.Length > OriginalPhysicalLength,
                                   "Test setup failed to append abandoned rebuild data.")

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(OriginalPhysicalLength,
                                    Ms.Length,
                                    "Chunk-size rebuild recovery did not truncate abandoned rebuild output.")

                        AssertBytesEqual(Original,
                                         Reopened.ToArray(),
                                         "Chunk-size rebuild recovery corrupted committed data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encryption removal inside a rolled-back checkpoint keeps encrypted data readable.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointEncryptionRemovalRollbackKeepsEncryptedDataReadable()

                Using Ms As New MemoryStream()

                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 5)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(Helpers.MakeKey(5))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()

                        AssertTrue(Before.EncryptedChunkCount > 0,
                                   "Expected encrypted chunks before checkpoint.")

                        AssertTrue(Before.HasFileMasterKey,
                                   "Expected file master key before checkpoint.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Options.EncryptionInfo = Nothing

                            Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                            AssertTrue(Result.EncryptionChanges > 0,
                                       "Expected encryption changes inside checkpoint.")

                            Dim DuringCheckpoint = Cs.GetStructure()

                            AssertEqual(0,
                                        DuringCheckpoint.EncryptedChunkCount,
                                        "Expected active checkpoint state to contain no encrypted chunks.")

                            AssertTrue(DuringCheckpoint.HasFileMasterKey,
                                       "Expected key removal to be deferred while checkpoint is active.")

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertTrue(AfterRollback.EncryptedChunkCount > 0,
                                   "Expected encrypted chunks to be restored by checkpoint rollback.")

                        AssertTrue(AfterRollback.HasFileMasterKey,
                                   "Expected file master key to remain after rollback.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Data was not readable after encryption rollback.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions compression inside a checkpoint is rolled back when the checkpoint is disposed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointApplyOptionsCompressionRollback()

                Using Ms As New MemoryStream()

                    Dim Data = Helpers.GenerateCompressableData(0.1, ChunkedStream.DefaultChunkSize * 3, 6)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()

                        AssertEqual(0,
                                    Before.CompressedChunkCount,
                                    "Expected no compressed chunks before ApplyOptions.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                            Cs.Options.CompressionRatioThreshold = 0.95R

                            Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                            AssertTrue(Result.CompressionChanges > 0,
                                       "Expected compression changes inside checkpoint.")

                            AssertTrue(Cs.GetStructure().CompressedChunkCount > 0,
                                       "Expected compressed chunks inside checkpoint.")

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertEqual(0,
                                    AfterRollback.CompressedChunkCount,
                                    "Expected compression changes to be rolled back.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Data mismatch after checkpoint rollback.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions compression inside a committed checkpoint survives checkpoint disposal.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointApplyOptionsCompressionCommitSurvivesDispose()

                Using Ms As New MemoryStream()

                    Dim Data = Helpers.GenerateCompressableData(0.1, ChunkedStream.DefaultChunkSize * 3, 7)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Data)

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                            Cs.Options.CompressionRatioThreshold = 0.95R

                            Dim Result = Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                            AssertTrue(Result.CompressionChanges > 0,
                                       "Expected compression changes inside checkpoint.")

                            Checkpoint.Commit()

                        End Using

                        Dim AfterCommit = Cs.GetStructure()

                        AssertTrue(AfterCommit.CompressedChunkCount > 0,
                                   "Expected compressed chunks to survive committed checkpoint.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Data mismatch after committed checkpoint.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening an existing stream updates the supplied options to the stored chunk size.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OpenExistingStreamUpdatesOptionsChunkSize()

                Using Ms As New MemoryStream()

                    Dim OriginalOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 32 * 1024
                    }

                    Dim Data = Helpers.GenerateRandomData(200000, 8)

                    Using Cs = ChunkedStream.Open(Ms, OriginalOptions)

                        AssertEqual(32 * 1024,
                                    Cs.ChunkSize,
                                    "Initial chunk size was incorrect.")

                        Cs.Write(0, Data)

                    End Using

                    Dim ReopenOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 512 * 1024
                    }

                    Using Reopened = ChunkedStream.Open(Ms, ReopenOptions)

                        AssertEqual(32 * 1024,
                                    Reopened.ChunkSize,
                                    "Reopened stream did not use stored chunk size.")

                        AssertEqual(32 * 1024,
                                    ReopenOptions.ChunkSize,
                                    "Open did not update supplied options to stored chunk size.")

                        AssertBytesEqual(Data,
                                         Reopened.ToArray(),
                                         "Reopened data mismatch.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rebuilding with a new chunk size preserves data and updates stored format metadata.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RebuildAppliesChunkSizeAndPreservesData()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 32 * 1024
                    }

                    Dim Data = Helpers.GenerateRandomData(500000, 9)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Data)

                        AssertEqual(32 * 1024,
                                    Cs.ChunkSize,
                                    "Unexpected initial chunk size.")

                        Cs.Options.ChunkSize = 128 * 1024

                        Cs.Defragment(ChunkedStream.DefragTypes.Rebuild)

                        AssertEqual(128 * 1024,
                                    Cs.ChunkSize,
                                    "Rebuild did not apply new chunk size.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Data mismatch after chunk-size rebuild.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(128 * 1024,
                                    Reopened.ChunkSize,
                                    "Reopened stream did not persist rebuilt chunk size.")

                        AssertBytesEqual(Data,
                                         Reopened.ToArray(),
                                         "Reopened rebuilt stream data mismatch.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that chunks written under different options retain their expected per-chunk metadata.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MixedPerChunkOptionsRoundTripAndMetadata()

                Using Ms As New MemoryStream()

                    Dim EncryptionInfo = New ChunkedStream.EncryptionInfo(Helpers.MakeKey(10))

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Chunk0 = Helpers.GenerateRandomData(Cs.ChunkSize, 10)
                        Dim Chunk1 = Helpers.GenerateRandomData(Cs.ChunkSize, 11)
                        Dim Chunk2 = Helpers.GenerateCompressableData(0.1, Cs.ChunkSize, 12)

                        Cs.Write(0 * Cs.ChunkSize, Chunk0)

                        Cs.Options.EncryptionInfo = EncryptionInfo

                        Cs.Write(1 * Cs.ChunkSize, Chunk1)

                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                        Cs.Options.CompressionRatioThreshold = 0.95R

                        Cs.Write(2 * Cs.ChunkSize, Chunk2)

                        Dim Expected = CombineArrays(Chunk0, Chunk1, Chunk2)

                        Dim Struct = Cs.GetStructure()

                        AssertEqual(3,
                                    Struct.ChunkCount,
                                    "Unexpected chunk count.")

                        AssertEqual(2,
                                    Struct.EncryptedChunkCount,
                                    "Expected two encrypted chunks.")

                        AssertEqual(1,
                                    Struct.CompressedChunkCount,
                                    "Expected one compressed chunk.")

                        AssertTrue(Not Struct.Chunks(0).IsEncrypted,
                           "Chunk 0 should not be encrypted.")

                        AssertTrue(Not Struct.Chunks(0).IsCompressed,
                           "Chunk 0 should not be compressed.")

                        AssertTrue(Struct.Chunks(1).IsEncrypted,
                           "Chunk 1 should be encrypted.")

                        AssertTrue(Not Struct.Chunks(1).IsCompressed,
                           "Chunk 1 should not be compressed.")

                        AssertTrue(Struct.Chunks(2).IsEncrypted,
                           "Chunk 2 should be encrypted.")

                        AssertTrue(Struct.Chunks(2).IsCompressed,
                           "Chunk 2 should be compressed.")

                        AssertEqual(ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            Struct.Chunks(2).CompressionEvaluatedMethod,
                            "Chunk 2 compression evaluated method was incorrect.")

                        AssertBytesEqual(Expected,
                                 Cs.ToArray(),
                                 "Mixed per-chunk option data mismatch.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that mixed compression, encryption, sparseness and rebuild operations remain valid after reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FormatEvolutionMixedOptionsValidateAfterReopen()

                Using Ms As New MemoryStream()

                    Dim Data0 = Helpers.GenerateCompressableData(0.2, ChunkedStream.DefaultChunkSize, 20)
                    Dim Data1 = New Byte(ChunkedStream.DefaultChunkSize - 1) {}
                    Dim Data2 = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 21)
                    Dim Expected = CombineArrays(Data0, Data1, Data2)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(Helpers.MakeKey(20)),
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0 * Cs.ChunkSize, Data0)
                        Cs.Write(1 * Cs.ChunkSize, Data1)
                        Cs.Write(2 * Cs.ChunkSize, Data2)

                        Cs.Options.EncryptionInfo = Nothing

                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                        Cs.Options.ChunkSize = 128 * 1024

                        Cs.Defragment(ChunkedStream.DefragTypes.Rebuild)

                        AssertBytesEqual(Expected,
                                         Cs.ToArray(),
                                         "Mixed format evolution data mismatch before reopen.")

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(128 * 1024,
                                    Reopened.ChunkSize,
                                    "Reopened stream did not retain rebuilt chunk size.")

                        AssertBytesEqual(Expected,
                                         Reopened.ToArray(),
                                         "Mixed format evolution data mismatch after reopen.")

                        Dim Struct = Reopened.GetStructure()

                        AssertEqual(0,
                                    Struct.EncryptedChunkCount,
                                    "Expected encryption removal to persist.")

                        AssertTrue(Not Struct.HasWrappedFileMasterKey,
                                   "Expected unused file master key to be removed.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            Private Shared Function CombineArrays(ParamArray Arrays()() As Byte) As Byte()
                Dim TotalLength = Arrays.Sum(Function(x) x.Length)
                Dim Result(TotalLength - 1) As Byte
                Dim Offset = 0
                For Each Array In Arrays
                    If Array.Length > 0 Then
                        Buffer.BlockCopy(Array,
                             0,
                             Result,
                             Offset,
                             Array.Length)
                        Offset += Array.Length
                    End If
                Next
                Return Result
            End Function

            Private Shared Sub InvokePrivateSub(Target As Object,
                                                MethodName As String,
                                                ParamArray Arguments() As Object)

                If Target Is Nothing Then Throw New ArgumentNullException(NameOf(Target))

                Dim Method = Target.GetType().GetMethod(MethodName,
                                                        BindingFlags.Instance Or BindingFlags.NonPublic)

                AssertTrue(Method IsNot Nothing,
                           $"Private method not found: {MethodName}.")

                Method.Invoke(Target, Arguments)

            End Sub

        End Class
    End Class
End Namespace