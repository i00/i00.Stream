Imports System.IO
Imports System.Linq
Imports StreamEncryption.Streams

Namespace Tests

    Partial Class DeveloperConfidence

        Public NotInheritable Class Diagnostics

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Chunk-cache behaviour
            ' ================================================================================

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
            ''' Verifies that disabling the chunk read cache does not alter logical read behaviour.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkCacheCanBeDisabled()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .UseChunkReadCache = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 8,
                                1002)

                        Cs.Write(0, Data)

                        For ReadIndex = 1 To 100

                            Dim Actual =
                                GenerateZeroedData(Data.Length)

                            Dim BytesRead =
                                Cs.Read(0, Actual)

                            AssertEqual(
                                Data.Length,
                                BytesRead,
                                $"Unexpected byte count with cache disabled on read {ReadIndex}.")

                            AssertBytesEqual(
                                Data,
                                Actual,
                                $"Cache-disabled read {ReadIndex} returned incorrect data.")

                        Next

                    End Using

                End Using

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

                        Reopened.Validate()

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

                        Cs.Validate()

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

                        Reopened.Validate()

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