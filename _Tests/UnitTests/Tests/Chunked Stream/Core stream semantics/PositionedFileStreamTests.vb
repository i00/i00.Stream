Imports System.IO
Imports System.Threading
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        ''' <summary>
        ''' PositionedFileStream is the real, file-backed IPositionedStreamAsync
        ''' implementation - the piece that lets Options.MaxPhysicalWriteParallelism (and the
        ''' existing lock-free concurrent-read path) actually do something against real
        ''' storage, since a plain FileStream declares neither capability. These tests exercise
        ''' it directly (ReadAt/WriteAt semantics) and through ChunkedStream, against a real
        ''' temp file - not the in-memory test doubles used elsewhere.
        ''' </summary>
        Public NotInheritable Class PositionedFileStreamTests

            Private Sub New()
            End Sub

            Private Shared Function NewTempPath() As String
                Return Path.Combine(Path.GetTempPath(), "cs-positioned-file-" & Guid.NewGuid().ToString("N") & ".bin")
            End Function

            ''' <summary>
            ''' Declares full lock-free capability - the whole point of this class over a plain
            ''' FileStream, which declares neither.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DeclaresFullLockFreeCapability()

                Dim TempPath = NewTempPath()
                Try
                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)
                        AssertEqual(PositionedIoCapabilities.Full, Pfs.PositionedIoCapabilities, "Expected full lock-free capability.")
                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            ''' <summary>
            ''' ReadAt/WriteAt round-trip directly (no ChunkedStream involved), including a
            ''' short read at end of file and a zero-length request - the exact contract
            ''' IPositionedStream documents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAtWriteAtRoundTripDirectly()

                Dim TempPath = NewTempPath()
                Try
                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)

                        Dim DataA = GenerateRandomData(50000, 7701)
                        Dim DataB = GenerateRandomData(30000, 7702)

                        ' Deliberately written out of offset order and interleaved, since
                        ' WriteAt must never depend on being called in ascending order.
                        Pfs.WriteAt(60000, DataB, 0, DataB.Length)
                        Pfs.WriteAt(0, DataA, 0, DataA.Length)

                        Dim ReadBackA(DataA.Length - 1) As Byte
                        Dim ReadA = Pfs.ReadAt(0, ReadBackA, 0, ReadBackA.Length)
                        AssertEqual(DataA.Length, ReadA, "Short ReadAt for region A.")
                        AssertBytesEqual(DataA, ReadBackA, "ReadAt/WriteAt round trip mismatch for region A.")

                        Dim ReadBackB(DataB.Length - 1) As Byte
                        Dim ReadB = Pfs.ReadAt(60000, ReadBackB, 0, ReadBackB.Length)
                        AssertEqual(DataB.Length, ReadB, "Short ReadAt for region B.")
                        AssertBytesEqual(DataB, ReadBackB, "ReadAt/WriteAt round trip mismatch for region B.")

                        ' Zero-length request: a no-op, not an error.
                        AssertEqual(0, Pfs.ReadAt(0, ReadBackA, 0, 0), "Zero-length ReadAt should return 0.")

                        ' A read starting at end of file returns 0, not an exception.
                        AssertEqual(0, Pfs.ReadAt(Pfs.Length, ReadBackA, 0, ReadBackA.Length), "ReadAt at EOF should return 0.")

                        ' A read starting inside the data but extending past EOF returns a
                        ' genuine short read.
                        Dim Tail(9999) As Byte
                        Dim TailRead = Pfs.ReadAt(Pfs.Length - 100, Tail, 0, Tail.Length)
                        AssertEqual(100, TailRead, "A read extending past EOF should return only the bytes actually present.")

                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            ''' <summary>
            ''' A full ChunkedStream round trip through a real file, including reopening via a
            ''' fresh PositionedFileStream instance on the same path.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkedStreamRoundTripsThroughARealFile()

                Dim TempPath = NewTempPath()
                Try
                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 6 + 777, 7801)

                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)
                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7802))
                        }
                        Using Cs = ChunkedStream.Open(Pfs, Options, FlushDurableAction:=Sub(s) s.FlushDurable())
                            Cs.Write(0, Expected)
                            Cs.Flush()
                            Cs.Validate().ThrowIfErrors()
                            AssertBytesEqual(Expected, Cs.ToArray(), "Real-file round trip mismatch.")
                        End Using
                    End Using

                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Using Reopened = ChunkedStream.Open(Pfs, New ChunkedStream.ChunkedStreamOptions With {
                                .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7802))})
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
            ''' path, against a real PositionedFileStream with MaxPhysicalWriteParallelism > 1 -
            ''' proving the concurrent-write path (already proven to genuinely overlap against
            ''' the gated test double in Concurrency.vb) also produces correct data end to end
            ''' against real Win32 positioned I/O, not just the mock.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AsyncBatchedWriteRoundTripsThroughARealFileWithWriteParallelism()

                Dim TempPath = NewTempPath()
                Try
                    Const ChunkSize As Integer = 4096
                    Const ChunkCount As Integer = 32 ' well past ParallelChunkCryptoMinChunks (8)

                    Dim Expected = GenerateRandomData(ChunkSize * ChunkCount, 7901)

                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = ChunkSize,
                            .MaxCryptoParallelism = 8,
                            .MaxPhysicalWriteParallelism = 8,
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7902))
                        }

                        Using Cs = ChunkedStream.Open(Pfs, Options, FlushDurableAction:=Sub(s) s.FlushDurable())

                            Dim Written = Cs.WriteAsync(0, Expected).GetAwaiter().GetResult()
                            AssertEqual(Expected.Length, Written, "Async batched write reported the wrong count.")

                            Cs.Flush()
                            Cs.Validate().ThrowIfErrors()
                            AssertBytesEqual(Expected, Cs.ToArray(), "Async batched write against a real file lost data.")

                        End Using

                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            Private Shared Sub TryDelete(Path As String)
                Try
                    If File.Exists(Path) Then File.Delete(Path)
                Catch
                End Try
            End Sub

        End Class

    End Class

End Namespace
