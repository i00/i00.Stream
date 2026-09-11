Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class Allocation

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Allocation policy behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that every chunk-record allocation policy preserves logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AllocationPoliciesPreserveData()

                For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .NewChunkWriteLocationPolicy = Policy
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Dim Expected =
                                GenerateZeroedData(
                                    Cs.Options.ChunkSize * 8)

                            For ChunkIndex = 0 To 7

                                Dim Data =
                                    GenerateRandomData(
                                        Cs.Options.ChunkSize,
                                        1000 + CInt(Policy) + ChunkIndex)

                                Dim Offset =
                                    ChunkIndex * Cs.Options.ChunkSize

                                Cs.Write(Offset, Data)
                                Overlay(Expected, Data, Offset)

                            Next

                            For ChunkIndex = 0 To 7 Step 2

                                Dim Data =
                                    GeneratePatternData(
                                        Cs.Options.ChunkSize,
                                        2000 + CInt(Policy) + ChunkIndex)

                                Dim Offset =
                                    ChunkIndex * Cs.Options.ChunkSize

                                Cs.Write(Offset, Data)
                                Overlay(Expected, Data, Offset)

                            Next

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Allocation policy corrupted logical data. Policy={Policy}")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that Append does not reuse freed chunk-record space, while hole-filling
            ''' policies can reuse an eligible freed chunk-record hole.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AllocationPolicyControlsFreedChunkRecordReuse()

                For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .NewChunkWriteLocationPolicy = Policy,
                            .ChunkSizeVariance = 0,
                            .HoleDirectoryMode = If(Policy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFitScan OrElse
                                                    Policy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan,
                                                    ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Never,
                                                    ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Always)
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            'First write ... lets say 10 chunks
                            Cs.Write(0, GeneratePatternData(Cs.Options.ChunkSize * 10, 1234))
                            'Chunks: 0123456789

                            Dim Struct = Cs.GetStructure()
                            'check we have expected chunks
                            AssertEqual(10, Struct.ChunkCount, "Written data did not create expected number of chunks")

                            'now lets clear the first 5 to create a big hole
                            Cs.Remove(0, Cs.Options.ChunkSize * 5)
                            'Chunks: 56789

                            'check we have expected chunks
                            AssertEqual(5, Cs.GetStructure().ChunkCount, "Cleared data did not result in expected number of chunks")

                        End Using

                        ' This is closed and then opened again to ensure that FillHolesFromStart fills holes in the newly cleared
                        ' area that is from the next Remove() call rather than writing into the initial free space

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Remove(Cs.Options.ChunkSize * 4, Cs.Options.ChunkSize)
                            ' Chunks: 5678

                            Dim Struct = Cs.GetStructure()
                            AssertEqual(4, Struct.ChunkCount, "Cleared data did not result in expected number of chunks")

                            ' Lets add a chunk to ... somewhere ;)
                            Cs.Write(Cs.Length, GeneratePatternData(Cs.Options.ChunkSize \ 2, 1234))
                            ' Chunks: 5678x

                            ' Check we have expected chunks
                            Dim After = Cs.GetStructure()
                            AssertEqual(5, After.ChunkCount, "Newly written data did not result in expected number of chunks")

                            Dim NewChunks = After.Chunks.Where(Function(x) Struct.Chunks.All(Function(y) x.PhysicalOffset.Value <> y.PhysicalOffset.Value)).
                                                         ToArray()

                            AssertEqual(1, NewChunks.Count, "The number of newly created chunks was not correct")

                            Dim OldEnd = Struct.Regions.Max(Function(x) x.PhysicalOffset + x.PhysicalLength)

                            Select Case Policy
                                Case ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append


                                    AssertTrue(NewChunks.Single.PhysicalOffset.Value >= OldEnd, $"{NameOf(Policy)} policy should be >= {OldEnd}")

                                Case ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                                     ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FirstFit

                                    AssertTrue(NewChunks.Single.PhysicalOffset.Value < OldEnd, $"{NameOf(Policy)} policy should be < {OldEnd}")

                                Case ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFitScan,
                                     ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FirstFitScan

                                    ' The first write (rebuilding the map by scanning) fills the big
                                    ' initial hole, below the first live chunk.
                                    Dim PlaceBefore = Struct.Chunks.First.PhysicalOffset.Value

                                    AssertTrue(NewChunks.Single.PhysicalOffset.Value < PlaceBefore, $"{NameOf(Policy)} policy should be < {PlaceBefore}")

                                    ' A second write must still reuse freed space rather than append
                                    ' past the end. Which freed hole it picks (the rest of the initial
                                    ' hole, or the record hole from this session's own Remove once it
                                    ' leaves the crash-recovery window) is up to the fit policy.
                                    Cs.Write(Cs.Length, GeneratePatternData(Cs.Options.ChunkSize \ 2, 1234))

                                    Dim After2 = Cs.GetStructure()
                                    NewChunks = After2.Chunks.Where(Function(x) After.Chunks.All(Function(y) x.PhysicalOffset.Value <> y.PhysicalOffset.Value)).
                                                              ToArray()
                                    AssertEqual(1, NewChunks.Count, "The number of newly created chunks was not correct")

                                    AssertTrue(NewChunks.Single.PhysicalOffset.Value < OldEnd, $"{NameOf(Policy)} policy should be < {OldEnd}")

                                Case Else
                                    Throw New NotSupportedException($"No test found for {Policy}")
                            End Select


                            Cs.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that FillHoles uses less physical live-data space than Append when
            ''' an eligible chunk-record hole exists.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FillHolesUsesLessPhysicalSpaceThanAppend()

                Dim AppendResult =
                    CreateHoleReuseResult(
                        ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append)

                Dim FillHolesResult =
                    CreateHoleReuseResult(
                        ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit)

                AssertTrue(
                    FillHolesResult.LiveDataEndOffset < AppendResult.LiveDataEndOffset,
                    $"FillHoles should use less physical space. Append={AppendResult.LiveDataEndOffset}, FillHoles={FillHolesResult.LiveDataEndOffset}.")

            End Sub

            ''' <summary>
            ''' Verifies that repeated freed-space reuse remains valid and does not overlap
            ''' live physical records.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AllocationAfterManyReuseCyclesRemainsValid()

                For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 1024,
                            .NewChunkWriteLocationPolicy = Policy
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Dim Expected =
                                GenerateZeroedData(
                                    Options.ChunkSize * 32)

                            For PassIndex = 0 To 4

                                For ChunkIndex = 0 To 31

                                    Dim Data =
                                        GenerateRandomData(
                                            Options.ChunkSize,
                                            5000 + (PassIndex * 100) + ChunkIndex)

                                    Dim Offset =
                                        ChunkIndex * Options.ChunkSize

                                    Cs.Write(Offset, Data)
                                    Overlay(Expected, Data, Offset)

                                Next

                                AssertBytesEqual(
                                    Expected,
                                    Cs.ToArray(),
                                    $"Allocation reuse cycle corrupted data. Policy={Policy}, PassIndex={PassIndex}")

                                Cs.Validate().ThrowIfErrors()

                            Next

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that a long run of shuffled random-offset writes with no durable
            ''' publish (no checkpoint, no flush, no dispose) still packs tightly under
            ''' BestFit. Freed spans are only withheld from reuse for HeaderCopyCount
            ''' publishes, so churn inside one uncommitted write stays compact rather than
            ''' degrading to append-only.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RandomWriteChurnStaysCompactWithoutADurablePublish()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 4096,
                        .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                        .NewIndexPageWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9911))
                    }

                    Const TotalBytes As Integer = 6 * 1024 * 1024

                    Dim Expected = GenerateRandomData(TotalBytes, 9910)

                    Dim Randomizer As New Random(12345)
                    Dim Segments As New List(Of Tuple(Of Long, Integer))()
                    Dim Cursor = 0

                    While Cursor < TotalBytes
                        Dim BlockSize = Math.Min(Randomizer.Next(1024, 256 * 1024), TotalBytes - Cursor)
                        Segments.Add(Tuple.Create(CLng(Cursor), BlockSize))
                        Cursor += BlockSize
                    End While

                    Segments = Segments.OrderBy(Function(x) Randomizer.Next()).ToList()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For Each Segment In Segments
                            Dim Buffer(Segment.Item2 - 1) As Byte
                            System.Buffer.BlockCopy(Expected, CInt(Segment.Item1), Buffer, 0, Segment.Item2)
                            Cs.Write(Segment.Item1, Buffer)
                        Next

                        Dim Fragmentation = Cs.GetFragmentation()

                        AssertBytesEqual(Expected, Cs.ToArray(), "Random-write churn corrupted logical data.")
                        Cs.Validate().ThrowIfErrors()

                        AssertTrue(
                            Fragmentation < 0.1R,
                            $"Uncommitted random-write churn should stay compact under BestFit, but fragmentation was {Fragmentation:P1}.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Checkpoint allocation behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that chunk allocation inside a checkpoint preserves logical data
            ''' under every placement policy, whether the checkpoint reuses a free hole or
            ''' appends, and whether it is committed or rolled back.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointChunkAllocationPreservesDataUnderEveryPolicy()

                For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .NewChunkWriteLocationPolicy = Policy
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            ' Eight chunks, then free the middle two and mature the hole.
                            Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 8, 6000 + CInt(Policy)))
                            Cs.Remove(Cs.Options.ChunkSize * 3L, Cs.Options.ChunkSize * 2L)

                            For MatureIndex = 1 To 4
                                Cs.Write(Cs.Length, GenerateRandomData(32, 6100 + MatureIndex))
                            Next

                            Dim Baseline = Cs.ToArray()
                            Dim NewData = GenerateRandomData(Cs.Options.ChunkSize * 2, 6200 + CInt(Policy))

                            ' Rollback: the in-checkpoint write is fully undone.
                            Using Checkpoint = Cs.CreateCheckpoint()
                                Cs.Write(Cs.Length, NewData)
                                Checkpoint.Rollback()
                            End Using

                            AssertBytesEqual(
                                Baseline,
                                Cs.ToArray(),
                                $"Rollback of an in-checkpoint chunk write changed data. Policy={Policy}")

                            Cs.Validate().ThrowIfErrors()

                            ' Commit: the in-checkpoint write is kept, data intact.
                            Using Checkpoint = Cs.CreateCheckpoint()
                                Cs.Write(Cs.Length, NewData)
                                Checkpoint.Commit()
                            End Using

                            AssertBytesEqual(
                                Baseline.Concat(NewData).ToArray(),
                                Cs.ToArray(),
                                $"Commit of an in-checkpoint chunk write changed data. Policy={Policy}")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        Using Reopened = ChunkedStream.Open(Ms, Options)
                            Reopened.Validate().ThrowIfErrors()
                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Refcount / reclamation behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that splitting an extent preserves the original physical record until
            ''' all referencing extents are removed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ExtentSplitMaintainsPhysicalRecordRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 10,
                                8001)

                        Cs.Write(0, Data)

                        Dim Patch =
                            GenerateRandomData(
                                100,
                                8002)

                        Cs.Write(500, Patch)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(Expected, Patch, 500)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Split extent overwrite corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a cloned range decrements shared physical-record
            ''' references and leaves the original source data readable.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveSharedCloneUpdatesRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .ChunkSizeVariance = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 4,
                                9001)

                        Cs.Write(0, Data)

                        Dim SourceOffset =
                            CLng(Options.ChunkSize)

                        Dim CloneLength =
                            Options.ChunkSize * 2

                        Dim CloneOffset =
                            CLng(Data.Length)

                        Cs.Clone(
                            SourceOffset,
                            CloneLength,
                            CloneOffset)

                        Dim AfterClone =
                            Cs.GetStructure()

                        Dim SourceRecordId =
                            AfterClone.
                            Chunks.
                            Single(Function(chunk) chunk.LogicalOffset = SourceOffset).
                            PhysicalRecordId.
                            Value

                        AssertEqual(
                            2,
                            AfterClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                               chunk.PhysicalRecordId.Value = SourceRecordId).Count(),
                            "After clone, source physical record should have two visible extent references.")

                        Cs.Remove(
                            CloneOffset,
                            CloneLength)

                        Dim AfterRemove =
                            Cs.GetStructure()

                        AssertEqual(
                            1,
                            AfterRemove.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                chunk.PhysicalRecordId.Value = SourceRecordId).Count(),
                            "After removing the clone, source physical record should have one visible extent reference.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "Removing cloned shared range damaged original data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shrinking away a cloned range decrements shared physical-record
            ''' references and leaves the original data intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthShrinkSharedCloneUpdatesRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .ChunkSizeVariance = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 4,
                                10001)

                        Cs.Write(0, Data)

                        Dim SourceOffset =
                            CLng(Options.ChunkSize)

                        Dim CloneLength =
                            Options.ChunkSize * 2

                        Dim CloneOffset =
                            CLng(Data.Length)

                        Cs.Clone(
                            SourceOffset,
                            CloneLength,
                            CloneOffset)

                        Dim AfterClone =
                            Cs.GetStructure()

                        Dim SourceRecordId =
                            AfterClone.
                            Chunks.
                            Single(Function(chunk) chunk.LogicalOffset = SourceOffset).
                            PhysicalRecordId.
                            Value

                        Cs.SetLength(Data.Length)

                        Dim AfterSetLength =
                            Cs.GetStructure()

                        AssertEqual(
                            1,
                            AfterSetLength.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                    chunk.PhysicalRecordId.Value = SourceRecordId).Count(),
                            "After SetLength removes the clone, source physical record should have one visible extent reference.")

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "SetLength shrink removed or damaged original data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Scan reclamation frees exactly the same physical storage as
            ''' RefCount reclamation for an identical write, overwrite, remove and shrink
            ''' workload.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ScanExtentReclaimMatchesRefCountReclaim()

                Dim RefCountResult = RunReclaimWorkload(ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.RefCount)
                Dim ScanResult = RunReclaimWorkload(ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.Scan)

                AssertBytesEqual(RefCountResult.Data, ScanResult.Data, "Scan reclamation changed the logical data.")
                AssertEqual(RefCountResult.ChunkCount, ScanResult.ChunkCount, "Scan reclamation produced a different chunk count.")
                AssertEqual(RefCountResult.AllocatedChunkCount, ScanResult.AllocatedChunkCount, "Scan reclamation produced a different allocated-chunk count.")
                AssertEqual(RefCountResult.LivePhysicalRecordCount, ScanResult.LivePhysicalRecordCount, "Scan reclamation left a different number of live physical records.")
                AssertEqual(RefCountResult.PhysicalChunkRecordBytes, ScanResult.PhysicalChunkRecordBytes, "Scan reclamation left a different live chunk-record byte count.")
                AssertEqual(RefCountResult.LiveDataEndOffset, ScanResult.LiveDataEndOffset, "Scan reclamation left a different live-data end offset.")

            End Sub

            ''' <summary>
            ''' A single Remove that frees hundreds of physical records is reclaimed as one
            ''' batch - one ordinal-map rebuild for the whole edit, not one per record.
            ''' Verifies both reclaim modes leave an identical, valid, leak-free stream and
            ''' that the surviving data is intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub LargeRangeRemovalBatchesPhysicalRecordReclaims()

                Dim RefCountResult = RunLargeDeleteWorkload(ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.RefCount)
                Dim ScanResult = RunLargeDeleteWorkload(ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.Scan)

                AssertBytesEqual(RefCountResult.Data, ScanResult.Data, "Batched reclaim changed the logical data between modes.")
                AssertEqual(RefCountResult.ChunkCount, ScanResult.ChunkCount, "Batched reclaim produced a different chunk count between modes.")
                AssertEqual(RefCountResult.LivePhysicalRecordCount, ScanResult.LivePhysicalRecordCount, "Batched reclaim left a different live physical-record count between modes.")
                AssertEqual(RefCountResult.PhysicalChunkRecordBytes, ScanResult.PhysicalChunkRecordBytes, "Batched reclaim left a different live chunk-record byte count between modes.")
                AssertEqual(RefCountResult.LiveDataEndOffset, ScanResult.LiveDataEndOffset, "Batched reclaim left a different live-data end offset between modes.")

            End Sub

            ''' <summary>
            ''' Verifies that Scan reclamation identifies an unreferenced physical record from
            ''' the extent table even when its maintained reference count has drifted, a case
            ''' RefCount reclamation cannot recover from.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ScanExtentReclaimToleratesReferenceCountDrift()

                For Each ReclaimType In {ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.RefCount,
                                         ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.Scan}

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .ChunkSize = 1024,
                            .ChunkSizeVariance = 0,
                            .ExtentReclaimType = ReclaimType
                        }

                        Dim Expected = GenerateRandomData(Options.ChunkSize * 4, 13000)

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Write(0, Expected)

                            Dim TargetRecordId =
                                Cs.GetStructure().
                                   Chunks.
                                   Single(Function(chunk) chunk.Index = 1).
                                   PhysicalRecordId.
                                   Value

                            Dim RecordBytesBeforeDrift = Cs.GetStructure().PhysicalChunkRecordBytes

                            ' Simulate reference-count drift on the record backing chunk 1.
                            Cs.Debug_CorruptPhysicalRecordMetadataRefCount(TargetRecordId, 5000)

                            Dim Replacement = GenerateRandomData(Options.ChunkSize, 13099)
                            Overlay(Expected, Replacement, Options.ChunkSize)

                            ' Overwrite chunk 1 so no extent references the drifted record any more.
                            Cs.Write(Options.ChunkSize, Replacement)

                            Select Case ReclaimType

                                Case ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes.Scan

                                    ' The scan reclaims the record despite the drifted count, so the
                                    ' stream stays consistent and no storage is leaked.
                                    Cs.Validate().ThrowIfErrors()
                                    AssertBytesEqual(Expected, Cs.ToArray(), "Scan reclamation corrupted logical data.")
                                    AssertEqual(RecordBytesBeforeDrift,
                                                Cs.GetStructure().PhysicalChunkRecordBytes,
                                                "Scan reclamation leaked the drifted record's storage.")

                                Case Else

                                    ' RefCount reclamation trusts the drifted count and cannot tell
                                    ' the record is now unreferenced.
                                    AssertThrows(Of ChunkedStream.ValidationException)(
                                        Sub() Cs.Validate().ThrowIfErrors(),
                                        "RefCount reclamation should leave the drifted reference count inconsistent.")

                            End Select

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private NotInheritable Class ReclaimWorkloadResult

                Public Property Data As Byte()

                Public Property ChunkCount As Integer

                Public Property AllocatedChunkCount As Integer

                Public Property LivePhysicalRecordCount As Integer

                Public Property PhysicalChunkRecordBytes As Long

                Public Property LiveDataEndOffset As Long

            End Class

            Private Shared Function RunReclaimWorkload(ReclaimType As ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes) As ReclaimWorkloadResult

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .ExtentReclaimType = ReclaimType
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 15
                            Cs.Write(CLng(ChunkIndex) * Options.ChunkSize,
                                     GenerateRandomData(Options.ChunkSize, 14000 + ChunkIndex))
                        Next

                        ' Overwrite a run of chunks, freeing their original records.
                        Cs.Write(Options.ChunkSize * 4L, GenerateRandomData(Options.ChunkSize * 4, 14100))

                        ' Remove a middle range.
                        Cs.Remove(Options.ChunkSize * 9L, Options.ChunkSize * 3L)

                        ' Clone a shared range then drop it again.
                        Cs.Clone(Options.ChunkSize, Options.ChunkSize * 2L, Cs.Length)
                        Cs.Remove(Cs.Length - Options.ChunkSize * 2L, Options.ChunkSize * 2L)

                        ' Shrink away the tail.
                        Cs.SetLength(Options.ChunkSize * 6L)

                        Cs.Validate().ThrowIfErrors()

                        Dim Struct = Cs.GetStructure()

                        Return New ReclaimWorkloadResult With {
                            .Data = Cs.ToArray(),
                            .ChunkCount = Struct.ChunkCount,
                            .AllocatedChunkCount = Struct.AllocatedChunkCount,
                            .LivePhysicalRecordCount = Struct.Chunks.
                                                             Where(Function(chunk) chunk.PhysicalRecordId.HasValue).
                                                             Select(Function(chunk) chunk.PhysicalRecordId.Value).
                                                             Distinct().
                                                             Count(),
                            .PhysicalChunkRecordBytes = Struct.PhysicalChunkRecordBytes,
                            .LiveDataEndOffset = Struct.LiveDataEndOffset
                        }

                    End Using

                End Using

            End Function

            Private Shared Function RunLargeDeleteWorkload(ReclaimType As ChunkedStream.ChunkedStreamOptions.ExtentReclaimTypes) As ReclaimWorkloadResult

                Const ChunkCount As Integer = 800
                Const KeptHead As Integer = 40
                Const KeptTail As Integer = 30

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 512,
                        .ExtentReclaimType = ReclaimType,
                        .StoreSparseChunks = False
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To ChunkCount - 1
                            Cs.Write(CLng(ChunkIndex) * Options.ChunkSize,
                                     GenerateRandomData(Options.ChunkSize, 21000 + ChunkIndex))
                        Next

                        Dim Expected = Cs.ToArray()

                        ' One Remove that frees every record between the head and tail we keep.
                        Dim RemoveOffset = CInt(CLng(KeptHead) * Options.ChunkSize)
                        Dim RemoveLength = CInt(CLng(ChunkCount - KeptHead - KeptTail) * Options.ChunkSize)

                        ' Frees ~730 physical records; the reclaim rebuilds the ordinal map once
                        ' for the batch, not once per record.
                        Cs.Remove(RemoveOffset, RemoveLength)

                        Dim ExpectedAfter = CombineArrays(Slice(Expected, 0, RemoveOffset),
                                                          Slice(Expected, RemoveOffset + RemoveLength, Expected.Length - RemoveOffset - RemoveLength))

                        Cs.Validate().ThrowIfErrors()
                        AssertBytesEqual(ExpectedAfter, Cs.ToArray(), "The large range removal damaged the surviving data.")

                        Dim Struct = Cs.GetStructure()

                        Dim ReferencedRecordCount = Struct.Chunks.
                                                          Where(Function(chunk) chunk.PhysicalRecordId.HasValue).
                                                          Select(Function(chunk) chunk.PhysicalRecordId.Value).
                                                          Distinct().
                                                          Count()

                        Return New ReclaimWorkloadResult With {
                            .Data = Cs.ToArray(),
                            .ChunkCount = Struct.ChunkCount,
                            .AllocatedChunkCount = Struct.AllocatedChunkCount,
                            .LivePhysicalRecordCount = ReferencedRecordCount,
                            .PhysicalChunkRecordBytes = Struct.PhysicalChunkRecordBytes,
                            .LiveDataEndOffset = Struct.LiveDataEndOffset
                        }

                    End Using

                End Using

            End Function

            Private NotInheritable Class HoleReuseResult

                Public Property OriginalChunk1Offset As Long

                Public Property Chunk1Offset As Long

                Public Property Chunk4Offset As Long

                Public Property LiveDataEndOffset As Long

            End Class

            Private Shared Function CreateHoleReuseResult(Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies) As HoleReuseResult

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .NewChunkWriteLocationPolicy = Policy
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 3

                            Cs.Write(
                                CLng(ChunkIndex) * CLng(Cs.Options.ChunkSize),
                                GeneratePatternData(
                                    Cs.Options.ChunkSize,
                                    11000 + ChunkIndex))

                        Next

                        Dim OriginalChunk1Offset =
                            Cs.
                            GetStructure().
                            Chunks.
                            Single(Function(chunk) chunk.Index = 1).
                            PhysicalOffset.
                            Value

                        Cs.Write(
                            Cs.Options.ChunkSize,
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                12001))

                        ' Space freed by superseding a chunk record is held back until the
                        ' next durable publish, so a crash cannot fall back onto storage a
                        ' later write has reused. Commit a checkpoint to release it before
                        ' the reuse write below.
                        Using Checkpoint = Cs.CreateCheckpoint()
                            Checkpoint.Commit()
                        End Using

                        Cs.Write(
                            Cs.Options.ChunkSize * 4L,
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                12004))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk1 =
                            Struct.
                            Chunks.
                            Single(Function(chunk) chunk.Index = 1)

                        Dim Chunk4 =
                            Struct.
                            Chunks.
                            Single(Function(chunk) chunk.Index = 4)

                        Cs.Validate().ThrowIfErrors()

                        Return New HoleReuseResult With {
                            .OriginalChunk1Offset = OriginalChunk1Offset,
                            .Chunk1Offset = Chunk1.PhysicalOffset.Value,
                            .Chunk4Offset = Chunk4.PhysicalOffset.Value,
                            .LiveDataEndOffset = Struct.LiveDataEndOffset
                        }

                    End Using

                End Using

            End Function

        End Class

    End Class

End Namespace