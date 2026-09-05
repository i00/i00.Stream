Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        ''' <summary>
        ''' PooledPositionedFileStream gets genuinely non-blocking (IOCP-backed) async
        ''' positioned I/O via a pool of FileStream handles opened with
        ''' FileOptions.Asynchronous, rather than PositionedFileStream's P/Invoke +
        ''' thread-pool-offload approach. See its file header remarks for why (VB.NET cannot
        ''' call the pointer-typed ThreadPoolBoundHandle API a single-handle implementation
        ''' would need).
        ''' </summary>
        Public NotInheritable Class PooledPositionedFileStreamTests

            Private Sub New()
            End Sub

            Private Shared Function NewTempPath() As String
                Return Path.Combine(Path.GetTempPath(), "cs-pooled-positioned-file-" & Guid.NewGuid().ToString("N") & ".bin")
            End Function

            Private Shared Sub TryDelete(TempPath As String)
                Try
                    If File.Exists(TempPath) Then File.Delete(TempPath)
                Catch
                End Try
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeclaresFullLockFreeCapability()

                Dim TempPath = NewTempPath()
                Try
                    Using Ppfs As New PooledPositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
                        AssertEqual(PositionedIoCapabilities.Full, Ppfs.PositionedIoCapabilities, "Expected full lock-free capability.")
                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAtWriteAtRoundTripDirectlyIncludingAsync()

                Dim TempPath = NewTempPath()
                Try
                    Using Ppfs As New PooledPositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)

                        Dim DataA = GenerateRandomData(50000, 8401)
                        Dim DataB = GenerateRandomData(30000, 8402)

                        Ppfs.WriteAtAsync(60000, DataB, 0, DataB.Length, Threading.CancellationToken.None).GetAwaiter().GetResult()
                        Ppfs.WriteAt(0, DataA, 0, DataA.Length)

                        Dim ReadBackA(DataA.Length - 1) As Byte
                        Dim ReadA = Ppfs.ReadAtAsync(0, ReadBackA, 0, ReadBackA.Length, Threading.CancellationToken.None).GetAwaiter().GetResult()
                        AssertEqual(DataA.Length, ReadA, "Short async ReadAt for region A.")
                        AssertBytesEqual(DataA, ReadBackA, "ReadAt/WriteAt round trip mismatch for region A.")

                        Dim ReadBackB(DataB.Length - 1) As Byte
                        Dim ReadB = Ppfs.ReadAt(60000, ReadBackB, 0, ReadBackB.Length)
                        AssertEqual(DataB.Length, ReadB, "Short ReadAt for region B.")
                        AssertBytesEqual(DataB, ReadBackB, "ReadAt/WriteAt round trip mismatch for region B.")

                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkedStreamRoundTripsThroughARealFileWithoutExplicitFlushDurableAction()

                Dim TempPath = NewTempPath()
                Try
                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 6 + 555, 8501)

                    Using Ppfs As New PooledPositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8502))
                        }
                        ' No FlushDurableAction - PooledPositionedFileStream implements
                        ' IDurableFlush, so this should already be durable.
                        Using Cs = ChunkedStream.Open(Ppfs, Options)
                            Cs.Write(0, Expected)
                            Cs.Flush()
                            Cs.Validate().ThrowIfErrors()
                            AssertBytesEqual(Expected, Cs.ToArray(), "Real-file round trip mismatch.")
                        End Using
                    End Using

                    Using Ppfs As New PooledPositionedFileStream(TempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
                        Using Reopened = ChunkedStream.Open(Ppfs, New ChunkedStream.ChunkedStreamOptions With {
                                .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8502))})
                            AssertBytesEqual(Expected, Reopened.ToArray(), "Real-file data did not survive reopen.")
                            Reopened.Validate().ThrowIfErrors()
                        End Using
                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            ''' <summary>
            ''' A single async write spanning enough chunks to trigger the parallel-CPU-prep
            ''' and concurrent-write paths, and a single async bulk read spanning enough chunks
            ''' to trigger the concurrent-read-fetch path, both against a real file through the
            ''' pool - proving the genuinely-async (IOCP-backed) route produces correct data
            ''' under real concurrency, not just the P/Invoke route already proven elsewhere.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AsyncBatchedWriteAndReadRoundTripThroughARealFileWithParallelism()

                Dim TempPath = NewTempPath()
                Try
                    Const ChunkSize As Integer = 4096
                    Const ChunkCount As Integer = 32 ' well past ParallelChunkCryptoMinChunks (8)

                    Dim Expected = GenerateRandomData(ChunkSize * ChunkCount, 8601)

                    Using Ppfs As New PooledPositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, PoolSize:=8)

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = ChunkSize,
                            .MaxCryptoParallelism = 8,
                            .MaxPhysicalWriteParallelism = 8,
                            .MaxPhysicalReadParallelism = 8,
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8602))
                        }

                        Using Cs = ChunkedStream.Open(Ppfs, Options)

                            Dim Written = Cs.WriteAsync(0, Expected).GetAwaiter().GetResult()
                            AssertEqual(Expected.Length, Written, "Async batched write reported the wrong count.")
                            Cs.Flush()
                            Cs.Validate().ThrowIfErrors()

                            Dim ReadBuffer(Expected.Length - 1) As Byte
                            Dim ReadCount = Cs.ReadAsync(0, ReadBuffer).GetAwaiter().GetResult()
                            AssertEqual(Expected.Length, ReadCount, "Async batched read reported the wrong count.")
                            AssertBytesEqual(Expected, ReadBuffer, "Async batched read/write round trip against a real file lost data.")

                        End Using

                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

        End Class

    End Class

End Namespace
