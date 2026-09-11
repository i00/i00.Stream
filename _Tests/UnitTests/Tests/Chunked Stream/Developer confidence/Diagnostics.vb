Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class DeveloperConfidence

        Public NotInheritable Class Diagnostics

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Chunk-cache behaviour
            ' ================================================================================

            ''' <summary>The physical-record id currently backing the chunk at a logical offset.</summary>
            Private Shared Function RecordIdAt(Cs As ChunkedStream, LogicalOffset As Long) As Long

                Return Cs.GetStructure().Chunks.
                    First(Function(Chunk) Chunk.LogicalOffset = LogicalOffset).
                    PhysicalRecordId.Value

            End Function

            ''' <summary>
            ''' Verifies that repeated reads of the same logical chunk return identical data
            ''' while the chunk read cache is enabled.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkCacheRepeatedReads()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1001)

                        Cs.Write(0, Data)

                        Dim Expected =
                            Slice(
                                Data,
                                Cs.options.ChunkSize,
                                Cs.options.ChunkSize)

                        For ReadIndex = 1 To 1000

                            Dim Actual =
                                GenerateZeroedData(Expected.Length)

                            Dim BytesRead =
                                Cs.Read(
                                    Cs.options.ChunkSize,
                                    Actual)

                            AssertEqual(
                                Expected.Length,
                                BytesRead,
                                $"Unexpected byte count on repeated cached read {ReadIndex}.")

                            AssertBytesEqual(
                                Expected,
                                Actual,
                                $"Repeated cached read {ReadIndex} returned incorrect data.")

                        Next

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing to a cached chunk invalidates the old cached plaintext.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkCacheInvalidatedByWrite()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original =
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                100)

                        Cs.Write(0, Original)

                        Dim FirstRead =
                            GenerateZeroedData(Original.Length)

                        Cs.Read(0, FirstRead)

                        AssertBytesEqual(
                            Original,
                            FirstRead,
                            "Initial cached read returned incorrect data.")

                        Dim Updated =
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                200)

                        Cs.Write(0, Updated)

                        Dim SecondRead =
                            GenerateZeroedData(Updated.Length)

                        Cs.Read(0, SecondRead)

                        AssertBytesEqual(
                            Updated,
                            SecondRead,
                            "Chunk cache was not invalidated after write.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that checkpoint rollback invalidates cached chunk data and restores
            ''' the original logical contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkCacheInvalidatedByCheckpointRollback()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Original =
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                100)

                        Cs.Write(0, Original)

                        Dim FirstRead =
                            GenerateZeroedData(Original.Length)

                        Cs.Read(0, FirstRead)

                        AssertBytesEqual(
                            Original,
                            FirstRead,
                            "Initial cached read returned incorrect data.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Dim Modified =
                                GeneratePatternData(
                                    Cs.options.ChunkSize,
                                    200)

                            Cs.Write(0, Modified)

                            Dim DuringCheckpoint =
                                GenerateZeroedData(Modified.Length)

                            Cs.Read(0, DuringCheckpoint)

                            AssertBytesEqual(
                                Modified,
                                DuringCheckpoint,
                                "Checkpoint write was not visible before rollback.")

                        End Using

                        Dim AfterRollback =
                            GenerateZeroedData(Original.Length)

                        Cs.Read(0, AfterRollback)

                        AssertBytesEqual(
                            Original,
                            AfterRollback,
                            "Checkpoint rollback did not invalidate cached modified data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that setting <c>ChunkReadBlockCache = 0</c> disables the cache: reads
            ''' still return correct data and nothing is retained between them.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkCacheCanBeDisabled()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkReadBlockCache = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkSize = Cs.options.ChunkSize

                        Dim Data =
                            GenerateRandomData(
                                ChunkSize * 4,
                                1002)

                        Cs.Write(0, Data)

                        Dim Expected =
                            Slice(Data, ChunkSize, ChunkSize)

                        For ReadIndex = 1 To 100

                            Dim Actual =
                                GenerateZeroedData(ChunkSize)

                            Dim BytesRead =
                                Cs.Read(ChunkSize, Actual)

                            AssertEqual(
                                ChunkSize,
                                BytesRead,
                                $"Unexpected byte count with cache disabled on read {ReadIndex}.")

                            AssertBytesEqual(
                                Expected,
                                Actual,
                                $"Cache-disabled read {ReadIndex} returned incorrect data.")

                        Next

                        AssertEqual(
                            0,
                            Cs.Debug_ChunkReadCacheEntryCount(),
                            "A disabled chunk read cache retained an entry.")

                        AssertEqual(
                            0L,
                            Cs.Debug_ChunkReadCacheHitCount(),
                            "A disabled chunk read cache reported a hit.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a repeated serial read of the same chunk is served from the
            ''' cache: the record is retained and later reads count as hits, not fills.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadCacheServesRepeatReadsFromMemory()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.ChunkSizeVariance = 0
                        Dim ChunkSize = Cs.options.ChunkSize

                        Cs.Write(0, GenerateRandomData(ChunkSize * 4, 7001))

                        Dim Buffer = GenerateZeroedData(ChunkSize)

                        Cs.Read(ChunkSize, Buffer)

                        AssertEqual(
                            1,
                            Cs.Debug_ChunkReadCacheEntryCount(),
                            "The first read did not populate the chunk read cache.")

                        AssertEqual(
                            1L,
                            Cs.Debug_ChunkReadCacheFillCount(),
                            "The first read did not count as a cache fill.")

                        Cs.Debug_ResetChunkReadCacheCounters()

                        For ReadIndex = 1 To 50
                            Cs.Read(ChunkSize, Buffer)
                        Next

                        AssertEqual(
                            50L,
                            Cs.Debug_ChunkReadCacheHitCount(),
                            "Repeated reads of a cached chunk were not served from the cache.")

                        AssertEqual(
                            0L,
                            Cs.Debug_ChunkReadCacheFillCount(),
                            "A repeated read of a cached chunk decrypted the record again.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies the cache is bounded to <c>ChunkReadBlockCache</c> records and evicts
            ''' the least recently used one.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadCacheEvictsLeastRecentlyUsedRecord()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkReadBlockCache = 2,
                        .ChunkSizeVariance = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkSize = Cs.options.ChunkSize

                        Cs.Write(0, GenerateRandomData(ChunkSize * 4, 7002))

                        Dim Buffer = GenerateZeroedData(ChunkSize)

                        Cs.Read(ChunkSize * 0, Buffer)
                        Cs.Read(ChunkSize * 1, Buffer)
                        Cs.Read(ChunkSize * 2, Buffer)

                        AssertEqual(
                            2,
                            Cs.Debug_ChunkReadCacheEntryCount(),
                            "The chunk read cache grew past ChunkReadBlockCache.")

                        Cs.Debug_ResetChunkReadCacheCounters()

                        ' Chunk 1 and 2 are still cached; chunk 0 was evicted.
                        Cs.Read(ChunkSize * 2, Buffer)
                        AssertEqual(1L, Cs.Debug_ChunkReadCacheHitCount(), "The most recently read chunk was not still cached.")
                        AssertEqual(0L, Cs.Debug_ChunkReadCacheFillCount(), "Re-reading a cached chunk decrypted it again.")

                        Cs.Read(ChunkSize * 0, Buffer)
                        AssertEqual(1L, Cs.Debug_ChunkReadCacheHitCount(), "The evicted chunk unexpectedly produced a second hit.")
                        AssertEqual(1L, Cs.Debug_ChunkReadCacheFillCount(), "The evicted chunk was not re-read from the backing store.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that an overwrite evicts only the records it supersedes and leaves
            ''' every other cached chunk warm.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadCacheEvictsOnlyTheRecordsAWriteSupersedes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Options.ChunkSizeVariance = 0
                        Dim ChunkSize = Cs.options.ChunkSize

                        Cs.Write(0, GenerateRandomData(ChunkSize * 4, 7003))

                        Dim Buffer = GenerateZeroedData(ChunkSize)

                        Cs.Read(ChunkSize * 0, Buffer)
                        Cs.Read(ChunkSize * 1, Buffer)
                        Cs.Read(ChunkSize * 2, Buffer)

                        Dim RecordAtChunk0 = RecordIdAt(Cs, 0)
                        Dim RecordAtChunk1 = RecordIdAt(Cs, ChunkSize * 1)
                        Dim RecordAtChunk2 = RecordIdAt(Cs, ChunkSize * 2)

                        AssertEqual(3, Cs.Debug_ChunkReadCacheEntryCount(), "Reads did not populate the chunk read cache.")

                        ' Overwrite chunk 1 only.
                        Cs.Write(ChunkSize * 1, GenerateRandomData(ChunkSize, 7004))

                        AssertEqual(2, Cs.Debug_ChunkReadCacheEntryCount(), "An overwrite did not evict its superseded record.")
                        AssertFalse(Cs.Debug_ChunkReadCacheContainsRecord(RecordAtChunk1), "The superseded record was left in the cache.")
                        AssertTrue(Cs.Debug_ChunkReadCacheContainsRecord(RecordAtChunk0), "An untouched chunk was evicted by an unrelated write.")
                        AssertTrue(Cs.Debug_ChunkReadCacheContainsRecord(RecordAtChunk2), "An untouched chunk was evicted by an unrelated write.")

                        Cs.Debug_ResetChunkReadCacheCounters()

                        Cs.Read(ChunkSize * 0, Buffer)
                        Cs.Read(ChunkSize * 2, Buffer)
                        AssertEqual(2L, Cs.Debug_ChunkReadCacheHitCount(), "Untouched chunks were not still served from the cache after the write.")

                        Cs.Read(ChunkSize * 1, Buffer)
                        AssertEqual(1L, Cs.Debug_ChunkReadCacheFillCount(), "The rewritten chunk was not re-read from the backing store.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a large multi-chunk read (the parallel decrypt path) both fills
            ''' the cache and is served from it on a repeat.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadCacheServesTheParallelReadPath()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkReadBlockCache = 64,
                        .MaxCryptoParallelism = 4,
                        .ChunkSizeVariance = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkSize = Cs.options.ChunkSize

                        ' 16 chunks - comfortably past ParallelChunkCryptoMinChunks (8).
                        Dim Data = GenerateRandomData(ChunkSize * 16, 7006)
                        Cs.Write(0, Data)

                        Dim Whole = GenerateZeroedData(Data.Length)

                        Cs.Read(0, Whole)
                        AssertBytesEqual(Data, Whole, "The first large read returned incorrect data.")
                        AssertEqual(16, Cs.Debug_ChunkReadCacheEntryCount(), "The parallel read path did not fill the cache.")

                        Cs.Debug_ResetChunkReadCacheCounters()

                        Cs.Read(0, Whole)
                        AssertBytesEqual(Data, Whole, "The repeated large read returned incorrect data.")
                        AssertEqual(16L, Cs.Debug_ChunkReadCacheHitCount(), "The parallel read path did not serve from the cache.")
                        AssertEqual(0L, Cs.Debug_ChunkReadCacheFillCount(), "The repeated large read decrypted records again.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that with multiple sub-blocks a partial read does not fill the cache,
            ''' but once a full read has cached the record a later partial read is served from
            ''' it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadCacheHandlesSubBlockRecords()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256 * 1024,
                        .SubBlockSize = 64 * 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim ChunkSize = Cs.options.ChunkSize

                        Dim Data = GenerateRandomData(ChunkSize, 7005)
                        Cs.Write(0, Data)

                        ' A sub-chunk read touches one sub-block and does not cache the record.
                        Dim Small = GenerateZeroedData(4096)
                        Cs.Read(1024, Small)

                        AssertBytesEqual(Slice(Data, 1024, 4096), Small, "Partial sub-block read returned incorrect data.")
                        AssertEqual(0, Cs.Debug_ChunkReadCacheEntryCount(), "A partial sub-block read filled the cache.")

                        ' A full-record read caches it.
                        Dim Whole = GenerateZeroedData(ChunkSize)
                        Cs.Read(0, Whole)
                        AssertEqual(1, Cs.Debug_ChunkReadCacheEntryCount(), "A full record read did not fill the cache.")

                        Cs.Debug_ResetChunkReadCacheCounters()

                        ' Now the partial read is served from the cached whole record.
                        Cs.Read(1024, Small)
                        AssertBytesEqual(Slice(Data, 1024, 4096), Small, "Cached partial read returned incorrect data.")
                        AssertEqual(1L, Cs.Debug_ChunkReadCacheHitCount(), "A partial read was not served from the cached whole record.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a negative <c>ChunkReadBlockCache</c> is rejected.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkReadBlockCacheRejectsNegativeValues()

                AssertThrows(Of ArgumentOutOfRangeException)(
                    Sub()
                        Dim Options As New ChunkedStream.ChunkedStreamOptions
                        Options.ChunkReadBlockCache = -1
                    End Sub,
                    "A negative ChunkReadBlockCache was accepted.")

            End Sub

            ' ================================================================================
            ' Structure diagnostics
            ' ================================================================================

            ''' <summary>
            ''' Verifies that structure snapshots contain the expected high-level physical
            ''' regions for a populated stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StructureSnapshotContainsExpectedRegions()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                Cs.options.ChunkSize * 2,
                                2001))

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Header),
                            "Structure snapshot did not contain header regions.")

                        AssertTrue(
                            Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.Chunk),
                            "Structure snapshot did not contain chunk regions.")

                        AssertTrue(
                            Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.IndexPage),
                            "Structure snapshot did not contain index-page regions.")

                        AssertTrue(
                            Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.MetadataRoot),
                            "Structure snapshot did not contain a metadata-root region.")

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                            "Structure snapshot did not contain any allocated chunks.")

                        AssertEqual(
                            CLng(Cs.options.ChunkSize * 2),
                            Struct.LogicalLength,
                            "Structure snapshot reported an unexpected logical length.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that structure snapshots remain available and internally useful after
            ''' closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StructureSnapshotSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GeneratePatternData(
                            ChunkedStream.DefaultChunkSize * 3,
                            2002)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data)
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        Dim Struct =
                            Reopened.GetStructure()

                        AssertEqual(
                            CLng(Data.Length),
                            Struct.LogicalLength,
                            "Structure snapshot after reopen reported an unexpected logical length.")

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) chunk.IsAllocated),
                            "Structure snapshot after reopen did not contain allocated chunks.")

                        AssertTrue(
                            Struct.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.MetadataRoot),
                            "Structure snapshot after reopen did not contain a metadata-root region.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a structure snapshot is an immutable diagnostic view and does
            ''' not change when the stream is later modified.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub StructureSnapshotIsImmutableAfterSubsequentWrites()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                2003))

                        Dim Before =
                            Cs.GetStructure()

                        Dim BeforeChunkCount =
                            Before.ChunkCount

                        Dim BeforeLogicalLength =
                            Before.LogicalLength

                        Cs.Write(
                            Cs.options.ChunkSize,
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                2004))

                        Dim After =
                            Cs.GetStructure()

                        AssertEqual(
                            BeforeChunkCount,
                            Before.ChunkCount,
                            "Previous structure snapshot chunk count changed after later write.")

                        AssertEqual(
                            BeforeLogicalLength,
                            Before.LogicalLength,
                            "Previous structure snapshot logical length changed after later write.")

                        AssertTrue(
                            After.ChunkCount >= BeforeChunkCount,
                            "New structure snapshot did not reflect later write.")

                        AssertEqual(
                            CLng(Cs.options.ChunkSize * 2),
                            After.LogicalLength,
                            "New structure snapshot reported an unexpected logical length.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Fragmentation diagnostics
            ' ================================================================================

            ''' <summary>
            ''' Verifies that fragmentation statistics can be produced for a stream that has
            ''' had physical records rewritten.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FragmentationStatisticsProducedForRewrittenStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            CreateRewrittenLayoutForDiagnostics(Cs)

                        Dim Fragmentation =
                            Cs.GetFragmentation()

                        AssertTrue(
                            Fragmentation >= 0.0R,
                            "Fragmentation ratio should not be negative.")

                        AssertTrue(
                            Fragmentation <= 1.0R,
                            "Fragmentation ratio should not exceed 1.")

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Diagnostic rewritten-layout setup corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that fragmentation statistics remain available after reopening a
            ''' stream whose physical layout has been rewritten.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FragmentationStatisticsSurviveReopen()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)
                        Expected = CreateRewrittenLayoutForDiagnostics(Cs)
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        Dim Fragmentation =
                            Reopened.GetFragmentation()

                        AssertTrue(
                            Fragmentation >= 0.0R,
                            "Fragmentation ratio after reopen should not be negative.")

                        AssertTrue(
                            Fragmentation <= 1.0R,
                            "Fragmentation ratio after reopen should not exceed 1.")

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Reopened rewritten-layout stream did not preserve logical data.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Diagnostic layout helpers
            ' ================================================================================

            ''' <summary>
            ''' Creates a simple rewritten physical layout for diagnostics tests.
            '''
            ''' This helper is intentionally private to Diagnostics so we do not reintroduce
            ''' a general fragmented-layout helper into the shared Helpers module.
            ''' </summary>
            Private Shared Function CreateRewrittenLayoutForDiagnostics(Cs As ChunkedStream) As Byte()

                If Cs Is Nothing Then Throw New ArgumentNullException(NameOf(Cs))

                Dim ChunkCount = 8
                Dim TotalLength = Cs.options.ChunkSize * ChunkCount
                Dim Expected = GenerateZeroedData(TotalLength)

                For ChunkIndex = 0 To ChunkCount - 1

                    Dim Data =
                        GeneratePatternData(
                            Cs.options.ChunkSize,
                            3000 + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                For ChunkIndex = 0 To ChunkCount - 1 Step 2

                    Dim Data =
                        GeneratePatternData(
                            Cs.options.ChunkSize,
                            4000 + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                For ChunkIndex = 1 To ChunkCount - 1 Step 2

                    Dim Data =
                        GeneratePatternData(
                            Cs.options.ChunkSize,
                            5000 + ChunkIndex)

                    Dim Offset =
                        ChunkIndex * Cs.options.ChunkSize

                    Cs.Write(Offset, Data)
                    Overlay(Expected, Data, Offset)

                Next

                Return Expected

            End Function

        End Class

    End Class

End Namespace