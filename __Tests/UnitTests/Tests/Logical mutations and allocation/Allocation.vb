Imports System.IO
Imports System.Linq
Imports StreamEncryption.Streams

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
                            .NewChunkWriteLocationPolicy = Policy
                        }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            For ChunkIndex = 0 To 3

                                Cs.Write(
                                    CLng(ChunkIndex) * CLng(Cs.Options.ChunkSize),
                                    GeneratePatternData(
                                        Cs.Options.ChunkSize,
                                        3000 + ChunkIndex))

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
                                    4001))

                            Cs.Write(
                                Cs.Options.ChunkSize * 4L,
                                GeneratePatternData(
                                    Cs.Options.ChunkSize,
                                    4004))

                            Dim Struct =
                                Cs.GetStructure()

                            Dim Chunk4 =
                                Struct.
                                Chunks.
                                Single(Function(chunk) chunk.Index = 4)

                            Select Case Policy

                                Case ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append

                                    AssertTrue(
                                        Chunk4.PhysicalOffset.Value <> OriginalChunk1Offset,
                                        $"Append policy should not reuse the freed chunk-1 hole. Policy={Policy}")

                                Case Else

                                    AssertEqual(
                                        OriginalChunk1Offset,
                                        Chunk4.PhysicalOffset.Value,
                                        $"Hole-filling policy should reuse the freed chunk-1 hole. Policy={Policy}")

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
                        ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles)

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