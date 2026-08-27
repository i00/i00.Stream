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

                            Cs.Validate()

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

                                    Cs.Write(Cs.Length, GeneratePatternData(Cs.Options.ChunkSize \ 2, 1234))

                                    Dim PlaceBefore = Struct.Chunks.First.PhysicalOffset.Value

                                    Dim After2 = Cs.GetStructure()
                                    NewChunks = After2.Chunks.Where(Function(x) After.Chunks.All(Function(y) x.PhysicalOffset.Value <> y.PhysicalOffset.Value)).
                                                              ToArray()
                                    AssertEqual(1, NewChunks.Count, "The number of newly created chunks was not correct")

                                    AssertTrue(NewChunks.Single.PhysicalOffset.Value < PlaceBefore, $"{NameOf(Policy)} policy should be < {PlaceBefore}")

                                Case Else
                                    Throw New NotSupportedException($"No test found for {Policy}")
                            End Select


                            Cs.Validate()

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

                                Cs.Validate()

                            Next

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Checkpoint allocation behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that allocation inside a checkpoint does not reuse committed holes,
            ''' even when a hole-filling allocation policy is selected.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AllocationInsideCheckpointDoesNotReuseCommittedHole()

                For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

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
                                        6000 + ChunkIndex))

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
                                    7001))

                            Using Checkpoint = Cs.CreateCheckpoint()

                                Cs.Write(
                                    Cs.Options.ChunkSize * 4L,
                                    GeneratePatternData(
                                        Cs.Options.ChunkSize,
                                        7004))

                                Dim DuringCheckpoint =
                                    Cs.GetStructure()

                                Dim Chunk4 =
                                    DuringCheckpoint.
                                    Chunks.
                                    Single(Function(chunk) chunk.Index = 4)

                                AssertTrue(
                                    Chunk4.PhysicalOffset.Value <> OriginalChunk1Offset,
                                    $"Checkpoint allocation should not reuse committed chunk hole. Policy={Policy}")

                            End Using

                            Cs.Validate()

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

                        Cs.Validate()

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
                        .ChunkSize = 1024
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

                        Cs.Validate()

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
                        .ChunkSize = 1024
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

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

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

                        Cs.Validate()

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