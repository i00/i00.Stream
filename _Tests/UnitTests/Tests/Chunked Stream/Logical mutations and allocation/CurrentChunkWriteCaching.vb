Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        ''' <summary>
        ''' Verifies Options.CurrentChunkWriteCaching: appends accumulate in memory without
        ''' committing a physical record until a chunk boundary, an explicit
        ''' FlushCurrentChunkWriteCache, a read into the buffered range, a non-contiguous write,
        ''' SetLength, or Dispose forces it out - and that the buffered bytes always read back
        ''' correctly regardless of whether they've been committed yet.
        ''' </summary>
        Public NotInheritable Class CurrentChunkWriteCaching

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SmallAppendsStayBufferedUntilTheChunkFills()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Options.ChunkSize = 8

                        Cs.Write(0, New Byte() {1})
                        Cs.Write(1, New Byte() {2})
                        Cs.Write(2, New Byte() {3})

                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "Nothing should be committed while the chunk is still under capacity.")
                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "A buffer should be open after buffered appends.")
                        AssertEqual(3, Cs.Debug_PendingChunkWriteCacheLength(), "The buffer should hold exactly the bytes written so far.")

                        ' Filling the rest of the chunk should commit it automatically.
                        Cs.Write(3, New Byte() {4, 5, 6, 7, 8})

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "Filling the chunk exactly should commit it.")
                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "No buffer should remain open once the chunk is committed.")

                        Dim ReadBack(7) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3, 4, 5, 6, 7, 8}, ReadBack, "All eight bytes should read back correctly.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub BufferedDataReadsBackCorrectlyAndCommitsOnRead()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True

                        Cs.Write(0, New Byte() {10, 20, 30})

                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: nothing committed yet.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {10, 20, 30}, ReadBack, "Buffered, uncommitted data should still read back correctly.")
                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "A read into the buffered range should have forced it to commit.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ExplicitFlushCommitsAnUnderfullChunk()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Options.ChunkSize = 64

                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: nothing committed yet.")

                        Cs.FlushCurrentChunkWriteCache()

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "An explicit flush should commit the under-full buffer.")
                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "No buffer should remain open after an explicit flush.")

                        ' A flush with nothing buffered should be a harmless no-op.
                        Cs.FlushCurrentChunkWriteCache()

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "Flushing again with nothing buffered should change nothing.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3}, ReadBack, "The flushed data should read back correctly.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub NonContiguousWriteFlushesThePendingBuffer()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True

                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the append should still be buffered.")

                        ' Overwriting the whole buffered range is not a continuation of it, so it
                        ' must commit what's pending before it can proceed.
                        Cs.Write(0, New Byte() {7, 8, 9})

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "The pending buffer should have been committed and closed before the overwrite.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {7, 8, 9}, ReadBack, "The overwrite should apply on top of the committed data.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DisposeCommitsThePendingBuffer()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {5, 6, 7})

                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: nothing committed before Dispose.")

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(1, Reopened.Debug_GetPhysicalRecordCount(), "Dispose should have committed the pending buffer.")

                        Dim ReadBack(2) As Byte
                        Reopened.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {5, 6, 7}, ReadBack, "The data committed on Dispose should survive reopen.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthFlushesThePendingBuffer()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True

                        Cs.Write(0, New Byte() {1, 2, 3})
                        Cs.SetLength(5)

                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "SetLength should have flushed the pending buffer first.")
                        AssertEqual(5, Cs.Length, "The stream should be extended to the requested length.")

                        Dim ReadBack(4) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3, 0, 0}, ReadBack, "The original bytes should be preserved, with the extension zero-filled.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CachingIsOffByDefault()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, New Byte() {1})

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Nothing should ever be buffered unless CurrentChunkWriteCaching is explicitly turned on.")
                        AssertEqual(1, Cs.Debug_GetPhysicalRecordCount(), "The write should have committed immediately, exactly like before this option existed.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
