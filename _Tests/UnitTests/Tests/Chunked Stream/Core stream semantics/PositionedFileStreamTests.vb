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
            ''' Regression test for a real bug in MakeOverlapped: it built the OVERLAPPED
            ''' structure's OffsetLow field with a checked CInt of a masked (unsigned) 32-bit
            ''' value, which throws OverflowException for any offset with the low DWORD's sign
            ''' bit set - i.e. every offset from 2 GiB up, recurring every 4 GiB after that.
            ''' WriteAt/ReadAt were therefore completely unusable past the 2 GiB mark until the
            ''' fix (LowDWordBits reinterprets the bit pattern instead of range-checking it).
            ''' SetLength grows the file first - on NTFS this only updates file-size metadata
            ''' rather than physically writing the whole range, so this stays fast without
            ''' needing multiple gigabytes of real disk I/O.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAtWriteAtWorkAtOffsetsPastTwoGigabytes()

                Dim TempPath = NewTempPath()
                Try
                    Using Pfs As New PositionedFileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)

                        Pfs.SetLength(5L * 1024 * 1024 * 1024)

                        ' Offsets around the low DWORD's sign bit (>= 2 GiB) and the 4 GiB
                        ' wraparound - every one of these threw OverflowException before the fix.
                        Dim Offsets() As Long = {
                            CLng(Integer.MaxValue) + 1L,
                            3L * 1024 * 1024 * 1024,
                            4L * 1024 * 1024 * 1024 - 1L,
                            4L * 1024 * 1024 * 1024,
                            4L * 1024 * 1024 * 1024 + 500L
                        }

                        For Each Offset In Offsets
                            Dim Data = GenerateRandomData(500, CInt(Offset Mod 10000) + 1)
                            Pfs.WriteAt(Offset, Data, 0, Data.Length)

                            Dim ReadBack(Data.Length - 1) As Byte
                            Dim ReadCount = Pfs.ReadAt(Offset, ReadBack, 0, ReadBack.Length)
                            AssertEqual(Data.Length, ReadCount, $"ReadAt at offset {Offset} returned the wrong count.")
                            AssertBytesEqual(Data, ReadBack, $"ReadAt/WriteAt round trip mismatch at offset {Offset}.")
                        Next

                    End Using
                Finally
                    TryDelete(TempPath)
                End Try

            End Sub

            ''' <summary>
            ''' A full ChunkedStream round trip through a real file, including reopening via a
            ''' fresh PositionedFileStream instance on the same path. No FlushDurableAction is
            ''' passed - PositionedFileStream implements IDurableFlush, so Cs.Flush() already
            ''' gets a genuine durable flush automatically.
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
                        Using Cs = ChunkedStream.Open(Pfs, Options)
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

                        Using Cs = ChunkedStream.Open(Pfs, Options)

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

            ''' <summary>
            ''' A backing store that implements IDurableFlush (PositionedFileStream is the real
            ''' example, but the mechanism itself is general - see IDurableFlush's own remarks)
            ''' gets a genuine durable flush automatically from ChunkedStream.Open, with no
            ''' FlushDurableAction parameter needed. Uses a minimal MemoryStream-backed mock
            ''' instead of a real PositionedFileStream so this tests exactly the fallback logic
            ''' in ChunkedStream's FlushDurable/FlushDurableAsync, independent of P/Invoke.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DurableFlushIsUsedAutomaticallyWithoutFlushDurableAction()

                Using Backing As New DurableFlushCountingStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8001))
                    }

                    ' Deliberately no FlushDurableAction argument here. Open() itself already
                    ' durably publishes the initial empty structure, so the baseline is taken
                    ' after Open() rather than assumed to be zero.
                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Dim CountAfterOpen = Backing.DurableFlushCalls
                        AssertTrue(CountAfterOpen > 0, "Open() should already have used the backing store's IDurableFlush for its initial publish.")

                        ' A plain Write() durably publishes on its own (every mutating call is
                        ' its own durable publish unless a DeferPublish scope holds it back) -
                        ' so to get a clean "held back, then released" signal, hold the publish
                        ' back with a scope and compare the count across its own Publish() call.
                        Using Scope = Cs.DeferPublish()
                            Cs.Write(0, GenerateRandomData(5000, 8002))
                            AssertEqual(CountAfterOpen, Backing.DurableFlushCalls, "Durable flush should not happen while a DeferPublish scope holds the publish back.")
                            Scope.Publish()
                        End Using

                        AssertTrue(Backing.DurableFlushCalls > CountAfterOpen, "ChunkedStream did not use the backing store's IDurableFlush automatically for the deferred publish.")

                    End Using

                End Using

            End Sub

            Private NotInheritable Class DurableFlushCountingStream
                Inherits MemoryStream
                Implements IDurableFlush

                Public Property DurableFlushCalls As Integer

                Public Sub FlushDurable() Implements IDurableFlush.FlushDurable
                    DurableFlushCalls += 1
                End Sub

            End Class

            Private Shared Sub TryDelete(Path As String)
                Try
                    If File.Exists(Path) Then File.Delete(Path)
                Catch
                End Try
            End Sub

        End Class

    End Class

End Namespace
