Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class CopyOnWrite

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Clone isolation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that modifying the source range after cloning does not modify the clone.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ModifySourceDoesNotModifyClone()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                1001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Patch =
                            GenerateRandomData(
                                256,
                                1002)

                        Cs.Write(100, Patch)

                        Dim Expected =
                            CombineArrays(
                                DirectCast(Data.Clone(), Byte()),
                                Data)

                        Overlay(
                            Expected,
                            Patch,
                            100)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Modifying source data modified cloned data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that modifying the clone does not modify the source.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ModifyCloneDoesNotModifySource()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                1101)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Patch =
                            GenerateRandomData(
                                256,
                                1102)

                        Cs.Write(
                            Data.Length + 100,
                            Patch)

                        Dim Expected =
                            CombineArrays(
                                Data,
                                DirectCast(Data.Clone(), Byte()))

                        Overlay(
                            Expected,
                            Patch,
                            Data.Length + 100)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Modifying clone data modified source data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Multi-reference behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that copy-on-write correctly isolates multiple logical references
            ''' to the same physical record.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CopyOnWriteSupportsMultipleLogicalReferences()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                2001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length * 2L)

                        Dim Patch =
                            GenerateRandomData(
                                512,
                                2002)

                        Cs.Write(
                            Data.Length + 50,
                            Patch)

                        Dim Expected =
                            CombineArrays(
                                Data,
                                DirectCast(Data.Clone(), Byte()),
                                Data)

                        Overlay(
                            Expected,
                            Patch,
                            Data.Length + 50)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Copy-on-write failed with multiple logical references.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Partial extent behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that modifying a small range inside a shared extent only affects
            ''' the modified logical range.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub PartialWriteOnlyIsolatesModifiedRange()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.Options.ChunkSize * 2,
                                3001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Cs.Options.ChunkSize \ 2,
                            Cs.Options.ChunkSize,
                            Data.Length)

                        Dim Patch =
                            GenerateRandomData(
                                128,
                                3002)

                        Cs.Write(
                            (Cs.Options.ChunkSize \ 2) + 100,
                            Patch)

                        Dim Expected =
                            CombineArrays(
                                DirectCast(Data.Clone(), Byte()),
                                Slice(
                                    Data,
                                    Cs.Options.ChunkSize \ 2,
                                    Cs.Options.ChunkSize))

                        Overlay(
                            Expected,
                            Patch,
                            (Cs.Options.ChunkSize \ 2) + 100)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Partial copy-on-write modified unrelated logical ranges.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Refcount behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that cloning increases physical-record sharing.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneCreatesSharedPhysicalRecords()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 2,
                                4001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Struct =
                            Cs.GetStructure()

                        Dim PhysicalRecords = Struct.Chunks.GroupBy(Function(x) x.PhysicalRecordId)

                        AssertTrue(
                            PhysicalRecords.Any(Function(record) record.Count > 1),
                            "Clone did not create any shared physical records.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a copy-on-write modification reduces sharing for the modified
            ''' record while preserving logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CopyOnWriteReducesSharingForModifiedRecord()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                4101)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Before =
                            Cs.GetStructure()

                        Dim BeforePhysical = Before.Chunks.GroupBy(Function(x) x.PhysicalRecordId)

                        AssertTrue(
                            BeforePhysical.Any(Function(x) x.Count > 1),
                            "Test setup failed to create shared physical records.")

                        Cs.Write(
                            100,
                            GenerateRandomData(
                                256,
                                4102))

                        Cs.Validate()

                        Dim After =
                            Cs.GetStructure()

                        Dim AfterPhysical = After.Chunks.GroupBy(Function(x) x.PhysicalRecordId)

                        AssertTrue(
                            AfterPhysical.Count > BeforePhysical.Count,
                            "Copy-on-write did not create replacement physical records.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Checkpoint interaction
            ' ================================================================================

            ''' <summary>
            ''' Verifies that copy-on-write changes inside a checkpoint are rolled back
            ''' correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CopyOnWriteRollbackRestoresOriginalSharing()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.Options.ChunkSize,
                                5001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Expected =
                            Cs.ToArray()

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Write(
                                100,
                                GenerateRandomData(
                                    256,
                                    5002))

                        End Using

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Checkpoint rollback did not restore original copy-on-write state.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Reopen behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that shared physical records survive reopening and that a subsequent
            ''' write triggers copy-on-write rather than modifying the shared record.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CopyOnWriteStillWorksAfterReopen()

                Using Ms As New MemoryStream()

                    Dim ChunkSize As Integer

                    Using Cs = ChunkedStream.Open(Ms)

                        ChunkSize = Cs.Options.ChunkSize

                        Dim Data =
                            GeneratePatternData(
                                ChunkSize,
                                6001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Before =
                            Cs.GetStructure()

                        Dim BeforePhysical = Before.Chunks.GroupBy(Function(x) x.PhysicalRecordId.GetValueOrDefault())

                        Dim Patch =
                            GenerateRandomData(
                                256,
                                6002)

                        Cs.Write(
                            100,
                            Patch)

                        Dim After =
                            Cs.GetStructure()

                        Dim AfterPhysical = After.Chunks.GroupBy(Function(x) x.PhysicalRecordId.GetValueOrDefault())

                        AssertTrue(
                            AfterPhysical.Count > BeforePhysical.Count,
                            "Copy-on-write did not create a replacement physical record after reopen.")

                        AssertTrue(
                            BeforePhysical.All(Function(x) AfterPhysical.Any(Function(y) x.Key = y.Key)),
                            "Original shared physical record disappeared unexpectedly.")

                        Dim Expected =
                            CombineArrays(
                                GeneratePatternData(
                                    ChunkSize,
                                    6001),
                                GeneratePatternData(
                                    ChunkSize,
                                    6001))

                        Overlay(
                            Expected,
                            Patch,
                            100)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Copy-on-write after reopen corrupted logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace