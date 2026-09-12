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

            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateAndGetStructureFlushThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the write should still be buffered.")

                        Dim Report = Cs.Validate()
                        Report.ThrowIfErrors()

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Validate should have flushed the pending buffer first.")

                        Dim StreamStructure = Cs.GetStructure()
                        AssertEqual(3L, StreamStructure.LogicalLength, "GetStructure should see the (now committed) full length.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate

                        Cs.Write(0, New Byte() {1, 2, 3})

                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "ApplyOptions should have flushed the pending buffer before scanning physical records.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3}, ReadBack, "The data should be intact after ApplyOptions ran.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        Cs.Defragment()

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Defragment should have flushed the pending buffer first.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3}, ReadBack, "The data should be intact after defragmenting.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReplaceFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        Cs.Replace(0, 3, New Byte() {7, 8, 9})

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Replace should have flushed the pending buffer before resolving the range to replace.")

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {7, 8, 9}, ReadBack, "The replacement should have applied correctly.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CreateCheckpointFlushesThePendingBufferFirstAndSurvivesRollback()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        Using Checkpoint = Cs.CreateCheckpoint()

                            AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Creating a checkpoint should have flushed the pending buffer into its baseline.")

                            Cs.Write(3, New Byte() {4, 5})
                            Checkpoint.Rollback()

                        End Using

                        Dim ReadBack(2) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3}, ReadBack, "The pre-checkpoint data should survive the rollback.")
                        AssertEqual(3L, Cs.Length, "The post-checkpoint write should have been rolled back.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeferPublishFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        Using Scope = Cs.DeferPublish()

                            AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Opening a DeferPublish scope should have flushed and published the pending buffer first.")

                            Cs.Write(3, New Byte() {4, 5})
                            Scope.Publish()

                        End Using

                        Dim ReadBack(4) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3, 4, 5}, ReadBack, "Both the pre-scope and in-scope data should be intact.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ToArrayFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the write should still be buffered.")

                        Dim Whole = Cs.ToArray()
                        AssertBytesEqual(New Byte() {1, 2, 3}, Whole, "ToArray() should see the buffered data.")
                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "ToArray() should have flushed the pending buffer.")

                        Cs.Write(3, New Byte() {4, 5})
                        Dim Ranged = Cs.ToArray(3, 2)
                        AssertBytesEqual(New Byte() {4, 5}, Ranged, "ToArray(Offset, Length) should see buffered data in range too.")
                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "ToArray(Offset, Length) should have flushed the pending buffer.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CloneFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the write should still be buffered.")

                        ' Clones the buffered [0,3) range and appends the copy at the end.
                        Cs.Clone(0, 3, 3)

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Clone should have flushed the pending buffer before resolving the source range.")

                        Dim ReadBack(5) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3, 1, 2, 3}, ReadBack, "The cloned range should match the original.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CreateAnchorFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the write should still be buffered.")

                        Dim NewAnchor = Cs.CreateAnchor(New Byte() {9, 9})

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "CreateAnchor should have flushed the pending buffer before appending the anchored data.")
                        AssertEqual(3L, Cs.GetAnchorOffset(NewAnchor.AnchorId), "The new anchor should sit right after the previously-buffered data.")

                        Dim ReadBack(4) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 2, 3, 9, 9}, ReadBack, "Both the buffered and anchored data should be intact.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub InsertFlushesThePendingBufferFirst()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True
                        Cs.Write(0, New Byte() {1, 2, 3})

                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "Sanity check: the write should still be buffered.")

                        ' Inserts in the middle of the still-buffered range.
                        Cs.Insert(1, New Byte() {9})

                        AssertFalse(Cs.Debug_HasPendingChunkWriteCache(), "Insert should have flushed the pending buffer before resolving the insertion point.")

                        Dim ReadBack(3) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {1, 9, 2, 3}, ReadBack, "The insertion should have landed correctly within the previously-buffered data.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub OrdinaryWritesStayBufferedAcrossMultiplePublishes()

                ' Regression guard: an earlier attempt at a defence-in-depth flush inside the
                ' shared metadata-publish primitive broke caching outright, since WriteCoreAsync
                ' publishes at the end of every call - including ones that legitimately left bytes
                ' buffered - which would have force-flushed the buffer every single time.
                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True

                        Cs.Write(0, New Byte() {1})
                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "The first write should still be buffered after its own publish.")

                        Cs.Write(1, New Byte() {2})
                        AssertTrue(Cs.Debug_HasPendingChunkWriteCache(), "The second write should still be buffered after its own publish.")
                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "Nothing should have committed across either publish.")

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' The write-cache buffer's own commit must apply Options.StoreSparseChunks'
            ''' optimisation too, the same as any other extent-building path - a fully-zero
            ''' buffered chunk should become a sparse extent rather than a wasted real record just
            ''' because it happened to pass through the buffer first.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnAllZeroBufferedChunkBecomesSparseOnCommit()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.CurrentChunkWriteCaching = True

                        Cs.Write(0, New Byte() {0})
                        Cs.Write(1, New Byte() {0, 0, 0})

                        Cs.FlushCurrentChunkWriteCache()

                        AssertEqual(0, Cs.Debug_GetPhysicalRecordCount(), "An all-zero buffered chunk should become sparse, not a real record, on commit.")

                        Dim ReadBack(3) As Byte
                        Cs.Read(0, ReadBack)

                        AssertBytesEqual(New Byte() {0, 0, 0, 0}, ReadBack, "The sparse chunk should still read back as all zero.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
