Imports System.IO
Imports System.Text
Imports StreamEncryption.Streams
Imports System.IO.Compression

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        Public NotInheritable Class Extents

            ''' <summary>
            ''' Verifies that splitting a physical record into multiple extents preserves the
            ''' physical record until all referencing extents are removed.
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
                                8)

                        Cs.Write(0, Data)

                        Dim Patch =
                            GenerateRandomData(
                                100,
                                9)

                        Cs.Write(500, Patch)

                        Dim Expected = DirectCast(Data.Clone(), Byte())
                        Buffer.BlockCopy(Patch, 0, Expected, 500, Patch.Length)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Split extent overwrite corrupted data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sequence defrag preserves shared physical records and logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SequenceDefragPreservesSharedPhysicalRecords()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 100,
                                8)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Options.ChunkSize * 10,
                            Options.ChunkSize,
                            Data.Length)

                        Dim Expected = Cs.ToArray()

                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Sequence)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Sequence defrag corrupted shared-record data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub




















            ''' <summary>
            ''' Verifies that a clone created inside a checkpoint is fully discarded by rollback,
            ''' including the associated shared physical-record references.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRollbackRestoresRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Original =
                            GenerateRandomData(
                                Options.ChunkSize * 3,
                                8)

                        Cs.Write(0, Original)

                        Using cp = Cs.CreateCheckpoint()
                            Cs.Clone(
                                0,
                                Original.Length,
                                Original.Length)

                            Dim DuringClone = Cs.GetStructure()

                            AssertEqual(
                                6,
                                DuringClone.Chunks.Count,
                                "Expected cloned extents during checkpoint.")

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertEqual(
                            3,
                            AfterRollback.Chunks.Count,
                            "Rollback should remove cloned extents.")

                        Dim Patch =
                            GenerateRandomData(
                                Options.ChunkSize,
                                99)

                        Cs.Write(
                            Options.ChunkSize,
                            Patch)

                        Dim Expected =
                            DirectCast(Original.Clone(), Byte())

                        Buffer.BlockCopy(
                            Patch,
                            0,
                            Expected,
                            Options.ChunkSize,
                            Patch.Length)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Rollback failed to restore original shared-reference state.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub





            ''' <summary>
            ''' Verifies that shrinking the stream via SetLength removes cloned extents and
            ''' updates shared physical-record references correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthShrinkRemovesSharedCloneExtents()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 4,
                                8)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Options.ChunkSize,
                            Options.ChunkSize * 2,
                            Data.Length)

                        Cs.SetLength(Data.Length)

                        AssertBytesEqual(
                            Data,
                            Cs.ToArray(),
                            "SetLength removed original data when truncating clone area.")

                        Dim Patch =
                            GenerateRandomData(
                                Options.ChunkSize,
                                88)

                        Cs.Write(
                            Options.ChunkSize,
                            Patch)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Buffer.BlockCopy(
                            Patch,
                            0,
                            Expected,
                            Options.ChunkSize,
                            Patch.Length)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "SetLength failed to release cloned shared references correctly.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub


            ''' <summary>
            ''' Verifies that SetLength performed inside a checkpoint correctly restores
            ''' shared physical-record references after rollback.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthRollbackRestoresSharedReferences()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            GenerateRandomData(
                                Options.ChunkSize * 4,
                                8)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Options.ChunkSize,
                            Options.ChunkSize,
                            Data.Length)

                        Dim ClonedLength = Cs.Length

                        Using cp = Cs.CreateCheckpoint()

                            Cs.SetLength(Data.Length)

                            AssertEqual(
                                CLng(Data.Length),
                                Cs.Length,
                                "Expected clone to be truncated during checkpoint.")

                        End Using


                        AssertEqual(
                            ClonedLength,
                            Cs.Length,
                            "Rollback should restore truncated cloned extent.")

                        Dim Expected =
                            New Byte(Data.Length + Options.ChunkSize - 1) {}

                        Buffer.BlockCopy(
                            Data,
                            0,
                            Expected,
                            0,
                            Data.Length)

                        Buffer.BlockCopy(
                            Data,
                            Options.ChunkSize,
                            Expected,
                            Data.Length,
                            Options.ChunkSize)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Rollback failed to restore shared cloned extent.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub


            ''' <summary>
            ''' Verifies that a clone created inside a checkpoint is discarded by checkpoint rollback,
            ''' including the shared physical-record references created by the clone.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneCheckpointRollbackRestoresRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 3, 8)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()
                        Dim BBefore = Before.Chunks.Single(Function(chunk) chunk.LogicalOffset = Options.ChunkSize)

                        AssertTrue(BBefore.PhysicalRecordId.HasValue,
                                   "Original B extent should have a physical record id before checkpoint clone.")

                        Dim BRecordId = BBefore.PhysicalRecordId.Value

                        AssertEqual(1,
                                    Before.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = BRecordId).Count,
                                    "Before checkpoint clone, B physical record should have exactly one visible extent reference.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Clone(0, Data.Length, Data.Length)

                            Dim DuringClone = Cs.GetStructure()

                            AssertEqual(6,
                                        DuringClone.Chunks.Count,
                                        "Checkpoint clone should create three additional cloned extents.")

                            AssertEqual(2,
                                        DuringClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = BRecordId).Count,
                                        "During checkpoint clone, B physical record should have two visible extent references.")

                            Cs.Validate()

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertEqual(3,
                                    AfterRollback.Chunks.Count,
                                    "Disposing the checkpoint should roll back the cloned extents.")

                        AssertEqual(1,
                                    AfterRollback.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = BRecordId).Count,
                                    "After checkpoint rollback, B physical record should have exactly one visible extent reference.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Checkpoint rollback after clone corrupted original data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a cloned range decrements the shared physical-record references
            ''' and leaves the original source range readable.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveSharedCloneUpdatesRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 4, 8)

                        Cs.Write(0, Data)

                        Dim SourceOffset = CLng(Options.ChunkSize)
                        Dim CloneLength = Options.ChunkSize * 2
                        Dim CloneOffset = CLng(Data.Length)

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim AfterClone = Cs.GetStructure()
                        Dim SourceB = AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        AssertTrue(SourceB.PhysicalRecordId.HasValue,
                                   "Source B extent should have a physical record id after clone.")

                        Dim SourceBRecordId = SourceB.PhysicalRecordId.Value

                        AssertEqual(2,
                                    AfterClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After clone, source B physical record should have two visible extent references.")

                        Cs.Remove(CloneOffset, CloneLength)

                        Dim AfterRemove = Cs.GetStructure()

                        AssertEqual(1,
                                    AfterRemove.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After removing the clone, source B physical record should have exactly one visible extent reference.")

                        AssertBytesEqual(Data,
                                         Cs.ToArray(),
                                         "Removing cloned shared range damaged original data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shrinking away a cloned range with SetLength decrements shared
            ''' physical-record references and leaves the original data intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthShrinkSharedCloneUpdatesRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                    .ChunkSize = 1024
                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 4, 8)

                        Cs.Write(0, Data)

                        Dim SourceOffset = CLng(Options.ChunkSize)
                        Dim CloneLength = Options.ChunkSize * 2
                        Dim CloneOffset = CLng(Data.Length)

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim AfterClone = Cs.GetStructure()
                        Dim SourceB = AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        AssertTrue(SourceB.PhysicalRecordId.HasValue,
                               "Source B extent should have a physical record id after clone.")

                        Dim SourceBRecordId = SourceB.PhysicalRecordId.Value

                        AssertEqual(2,
                                AfterClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                "After clone, source B physical record should have two visible extent references.")

                        Cs.SetLength(Data.Length)

                        Dim AfterSetLength = Cs.GetStructure()

                        AssertEqual(1,
                                AfterSetLength.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                "After SetLength removes the clone, source B physical record should have exactly one visible extent reference.")

                        AssertBytesEqual(Data,
                                     Cs.ToArray(),
                                     "SetLength shrink removed or damaged original data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a shared clone inside a checkpoint is undone by checkpoint rollback,
            ''' including restoration of the shared physical-record references.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveSharedCloneCheckpointRollbackRestoresRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 4, 8)

                        Cs.Write(0, Data)

                        Dim SourceOffset = CLng(Options.ChunkSize)
                        Dim CloneLength = Options.ChunkSize
                        Dim CloneOffset = CLng(Data.Length)

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim AfterClone = Cs.GetStructure()
                        Dim SourceB = AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        AssertTrue(SourceB.PhysicalRecordId.HasValue,
                                   "Source B extent should have a physical record id after clone.")

                        Dim SourceBRecordId = SourceB.PhysicalRecordId.Value

                        AssertEqual(2,
                                    AfterClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After clone, source B physical record should have two visible extent references.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Remove(CloneOffset, CloneLength)

                            Dim DuringRemove = Cs.GetStructure()

                            AssertEqual(1,
                                        DuringRemove.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                        "During checkpoint remove, source B physical record should have one visible extent reference.")

                            Cs.Validate()

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertEqual(2,
                                    AfterRollback.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After checkpoint rollback, removed clone reference should be restored.")

                        Dim Expected = New Byte(Data.Length + CloneLength - 1) {}
                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(CloneOffset), CloneLength)

                        AssertBytesEqual(Expected,
                                         Cs.ToArray(),
                                         "Checkpoint rollback after remove failed to restore cloned data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shrinking away a shared clone inside a checkpoint is undone by checkpoint rollback,
            ''' including restoration of the shared physical-record references.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthSharedCloneCheckpointRollbackRestoresRefCounts()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 4, 8)

                        Cs.Write(0, Data)

                        Dim SourceOffset = CLng(Options.ChunkSize)
                        Dim CloneLength = Options.ChunkSize
                        Dim CloneOffset = CLng(Data.Length)

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim AfterClone = Cs.GetStructure()
                        Dim SourceB = AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        AssertTrue(SourceB.PhysicalRecordId.HasValue,
                                   "Source B extent should have a physical record id after clone.")

                        Dim SourceBRecordId = SourceB.PhysicalRecordId.Value
                        Dim ClonedLength = Cs.Length

                        AssertEqual(2,
                                    AfterClone.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After clone, source B physical record should have two visible extent references.")

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.SetLength(Data.Length)

                            Dim DuringSetLength = Cs.GetStructure()

                            AssertEqual(CLng(Data.Length),
                                        Cs.Length,
                                        "During checkpoint SetLength, logical length should be truncated.")

                            AssertEqual(1,
                                        DuringSetLength.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                        "During checkpoint SetLength, source B physical record should have one visible extent reference.")

                            Cs.Validate()

                        End Using

                        Dim AfterRollback = Cs.GetStructure()

                        AssertEqual(ClonedLength,
                                    Cs.Length,
                                    "Checkpoint rollback should restore the cloned logical length.")

                        AssertEqual(2,
                                    AfterRollback.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceBRecordId).Count,
                                    "After checkpoint rollback, SetLength should restore the cloned shared reference.")

                        Dim Expected = New Byte(Data.Length + CloneLength - 1) {}
                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(CloneOffset), CloneLength)

                        AssertBytesEqual(Expected,
                                         Cs.ToArray(),
                                         "Checkpoint rollback after SetLength failed to restore cloned data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub







            ''' <summary>
            ''' Verifies that cloning from the middle of B to the middle of D shares the fully consumed C extent
            ''' instead of physically duplicating it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneMidBToMidDSharesConsumedMiddleExtent()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024,
                        .BisectLimit = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 5, 8)

                        Dim BOffset = CLng(Options.ChunkSize)
                        Dim COffset = CLng(Options.ChunkSize * 2)
                        Dim DOffset = CLng(Options.ChunkSize * 3)

                        Dim SourceOffset = BOffset + 200
                        Dim SourceEnd = DOffset + 600
                        Dim CloneLength = CInt(SourceEnd - SourceOffset)
                        Dim TargetOffset = CLng(Data.Length)

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()
                        Dim PhysicalBytesBefore = Before.PhysicalChunkRecordBytes

                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim After = Cs.GetStructure()

                        AssertEqual(
                            PhysicalBytesBefore,
                            After.PhysicalChunkRecordBytes,
                            "Clone from mid B to mid D should not physically duplicate consumed source records.")

                        Dim OriginalC =
                            After.Chunks.Single(Function(chunk) chunk.LogicalOffset = COffset AndAlso
                                                               chunk.PlainLength = Options.ChunkSize)

                        Dim ClonedCOffset = TargetOffset + (COffset - SourceOffset)

                        Dim ClonedC =
                            After.Chunks.Single(Function(chunk) chunk.LogicalOffset = ClonedCOffset AndAlso
                                                               chunk.PlainLength = Options.ChunkSize)

                        AssertTrue(
                            OriginalC.PhysicalRecordId.HasValue,
                            "Original C extent should have a physical record id.")

                        AssertTrue(
                            ClonedC.PhysicalRecordId.HasValue,
                            "Cloned C extent should have a physical record id.")

                        AssertEqual(
                            OriginalC.PhysicalRecordId.Value,
                            ClonedC.PhysicalRecordId.Value,
                            "Fully consumed C extent should be shared by the clone.")

                        AssertEqual(
                            OriginalC.PhysicalRecordOffset.Value,
                            ClonedC.PhysicalRecordOffset.Value,
                            "Fully consumed C extent should reference the same physical-record plaintext offset.")

                        Dim CReferenceCount =
                            After.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                               chunk.PhysicalRecordId.Value = OriginalC.PhysicalRecordId.Value).
                                         Count()

                        AssertEqual(
                            2,
                            CReferenceCount,
                            "The physical record for C should be referenced by exactly the original C extent and the cloned C extent.")

                        Dim Expected = New Byte(Data.Length + CloneLength - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(TargetOffset), CloneLength)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Clone from mid B to mid D returned incorrect logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing to the cloned copy of the fully consumed C extent performs copy-on-write
            ''' and does not modify the original C extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneMidBToMidDWriteToClonedMiddleExtentCreatesCopy()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024,
                        .BisectLimit = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 5, 8)

                        Dim BOffset = CLng(Options.ChunkSize)
                        Dim COffset = CLng(Options.ChunkSize * 2)
                        Dim DOffset = CLng(Options.ChunkSize * 3)

                        Dim SourceOffset = BOffset + 200
                        Dim SourceEnd = DOffset + 600
                        Dim CloneLength = CInt(SourceEnd - SourceOffset)
                        Dim TargetOffset = CLng(Data.Length)

                        Dim ClonedCOffset = TargetOffset + (COffset - SourceOffset)

                        Cs.Write(0, Data)
                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim AfterClone = Cs.GetStructure()

                        Dim OriginalCBefore =
                            AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = COffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        Dim ClonedCBefore =
                            AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = ClonedCOffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        AssertEqual(
                            OriginalCBefore.PhysicalRecordId.Value,
                            ClonedCBefore.PhysicalRecordId.Value,
                            "Before write, cloned C should share the original C physical record.")

                        Dim Patch = GenerateRandomData(Options.ChunkSize, 99)

                        Cs.Write(ClonedCOffset, Patch)

                        Dim AfterWrite = Cs.GetStructure()

                        Dim OriginalCAfter =
                            AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = COffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        Dim ClonedCAfter =
                            AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = ClonedCOffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        AssertEqual(
                            OriginalCBefore.PhysicalRecordId.Value,
                            OriginalCAfter.PhysicalRecordId.Value,
                            "Writing to cloned C should not change the original C physical record.")

                        AssertTrue(
                            ClonedCAfter.PhysicalRecordId.Value <> OriginalCAfter.PhysicalRecordId.Value,
                            "Writing to cloned C should create a new physical record for the cloned extent.")

                        Dim OriginalCReferenceCount =
                            AfterWrite.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                    chunk.PhysicalRecordId.Value = OriginalCAfter.PhysicalRecordId.Value).
                                              Count()

                        AssertEqual(
                            1,
                            OriginalCReferenceCount,
                            "After writing to cloned C, the original C physical record should have exactly one visible extent reference.")

                        Dim ClonedCReferenceCount =
                            AfterWrite.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                    chunk.PhysicalRecordId.Value = ClonedCAfter.PhysicalRecordId.Value).
                                              Count

                        AssertEqual(
                            1,
                            ClonedCReferenceCount,
                            "After writing to cloned C, the cloned C replacement physical record should have exactly one visible extent reference.")

                        Dim Expected = New Byte(Data.Length + CloneLength - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(TargetOffset), CloneLength)
                        Buffer.BlockCopy(Patch, 0, Expected, CInt(ClonedCOffset), Patch.Length)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Writing to cloned C corrupted original or cloned data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing to the original fully consumed C extent performs copy-on-write
            ''' and does not modify the cloned C extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneMidBToMidDWriteToOriginalMiddleExtentCreatesCopy()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024,
                        .BisectLimit = 0
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data = GenerateRandomData(Options.ChunkSize * 5, 8)

                        Dim BOffset = CLng(Options.ChunkSize)
                        Dim COffset = CLng(Options.ChunkSize * 2)
                        Dim DOffset = CLng(Options.ChunkSize * 3)

                        Dim SourceOffset = BOffset + 200
                        Dim SourceEnd = DOffset + 600
                        Dim CloneLength = CInt(SourceEnd - SourceOffset)
                        Dim TargetOffset = CLng(Data.Length)

                        Dim ClonedCOffset = TargetOffset + (COffset - SourceOffset)

                        Cs.Write(0, Data)
                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim AfterClone = Cs.GetStructure()

                        Dim OriginalCBefore =
                            AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = COffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        Dim ClonedCBefore =
                            AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = ClonedCOffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        AssertEqual(
                            OriginalCBefore.PhysicalRecordId.Value,
                            ClonedCBefore.PhysicalRecordId.Value,
                            "Before write, original C and cloned C should share the same physical record.")

                        Dim Patch = GenerateRandomData(Options.ChunkSize, 123)

                        Cs.Write(COffset, Patch)

                        Dim AfterWrite = Cs.GetStructure()

                        Dim OriginalCAfter =
                            AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = COffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        Dim ClonedCAfter =
                            AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = ClonedCOffset AndAlso
                                                                    chunk.PlainLength = Options.ChunkSize)

                        AssertTrue(
                            OriginalCAfter.PhysicalRecordId.Value <> ClonedCAfter.PhysicalRecordId.Value,
                            "Writing to original C should create a new physical record for the original extent.")

                        AssertEqual(
                            ClonedCBefore.PhysicalRecordId.Value,
                            ClonedCAfter.PhysicalRecordId.Value,
                            "Writing to original C should not change the cloned C physical record.")

                        Dim OriginalCReferenceCount =
                            AfterWrite.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                    chunk.PhysicalRecordId.Value = OriginalCAfter.PhysicalRecordId.Value).
                                              Count()

                        AssertEqual(
                            1,
                            OriginalCReferenceCount,
                            "After writing to original C, the replacement physical record should have exactly one visible extent reference.")

                        Dim ClonedCReferenceCount =
                            AfterWrite.Chunks.Where(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso
                                                                    chunk.PhysicalRecordId.Value = ClonedCAfter.PhysicalRecordId.Value).
                                              Count()

                        AssertEqual(
                            1,
                            ClonedCReferenceCount,
                            "After writing to original C, the cloned C physical record should have exactly one visible extent reference.")

                        Dim Expected = New Byte(Data.Length + CloneLength - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(TargetOffset), CloneLength)
                        Buffer.BlockCopy(Patch, 0, Expected, CInt(COffset), Patch.Length)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Writing to original C corrupted original or cloned data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a full chunk-aligned clone shares existing physical records instead of physically duplicating them.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneFullExtentsSharesPhysicalRecords()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                            Tests.Helpers.GenerateRandomData(
                                Options.ChunkSize * 10,
                                8).ToArray()

                        Cs.Write(0, Data)

                        Dim Before = Cs.GetStructure()
                        Dim PhysicalBytesBefore = Before.PhysicalChunkRecordBytes

                        Dim SourceOffset = Options.ChunkSize * 2L
                        Dim CloneLength = Options.ChunkSize * 3L
                        Dim TargetOffset = Data.Length

                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim After = Cs.GetStructure()

                        AssertEqual(
                            PhysicalBytesBefore,
                            After.PhysicalChunkRecordBytes,
                            "Chunk-aligned clone should not physically duplicate existing physical records.")

                        For iIndex = 0 To 2

                            Dim Index = iIndex
                            Dim SourceChunk =
                                After.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset + (Index * Options.ChunkSize))

                            Dim ClonedChunk =
                                After.Chunks.Single(Function(chunk) chunk.LogicalOffset = TargetOffset + (Index * Options.ChunkSize))

                            AssertTrue(
                                SourceChunk.PhysicalRecordId.HasValue,
                                $"Source chunk {Index} should have a physical record id.")

                            AssertTrue(
                                ClonedChunk.PhysicalRecordId.HasValue,
                                $"Cloned chunk {Index} should have a physical record id.")

                            AssertEqual(
                                SourceChunk.PhysicalRecordId.Value,
                                ClonedChunk.PhysicalRecordId.Value,
                                $"Cloned chunk {Index} should reference the same physical record as the source chunk.")

                            AssertEqual(
                                SourceChunk.PhysicalRecordOffset.Value,
                                ClonedChunk.PhysicalRecordOffset.Value,
                                $"Cloned chunk {Index} should reference the same physical-record offset as the source chunk.")

                        Next

                        Dim Expected = New Byte(Data.Length + CInt(CloneLength) - 1) {}
                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(TargetOffset), CInt(CloneLength))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Chunk-aligned clone returned incorrect logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing to a cloned shared extent performs copy-on-write and does not corrupt the source extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneWriteCreatesCopyAndPreservesSource()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 8,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim SourceOffset = Options.ChunkSize * 2L
                        Dim CloneLength = Options.ChunkSize
                        Dim TargetOffset = Data.Length

                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim AfterClone = Cs.GetStructure()

                        Dim SourceBefore =
                                        AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        Dim CloneBefore =
                                        AfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = TargetOffset)

                        AssertEqual(
                                        SourceBefore.PhysicalRecordId.Value,
                                        CloneBefore.PhysicalRecordId.Value,
                                        "Before write, cloned extent should share the source physical record.")

                        Dim Patch =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize,
                                            99).ToArray()

                        Cs.Write(TargetOffset, Patch)

                        Dim AfterWrite = Cs.GetStructure()

                        Dim SourceAfter =
                                        AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset)

                        Dim CloneAfter =
                                        AfterWrite.Chunks.Single(Function(chunk) chunk.LogicalOffset = TargetOffset)

                        AssertTrue(
                                        SourceAfter.PhysicalRecordId.HasValue,
                                        "Source extent should still have a physical record after writing to clone.")

                        AssertTrue(
                                        CloneAfter.PhysicalRecordId.HasValue,
                                        "Clone extent should still have a physical record after write.")

                        AssertEqual(
                                        SourceBefore.PhysicalRecordId.Value,
                                        SourceAfter.PhysicalRecordId.Value,
                                        "Writing to the clone should not change the source physical record.")

                        AssertTrue(
                                        CloneAfter.PhysicalRecordId.Value <> SourceAfter.PhysicalRecordId.Value,
                                        "Writing to the clone should create a new physical record for the cloned extent.")

                        Dim Expected = New Byte(Data.Length + CInt(CloneLength) - 1) {}
                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Patch, 0, Expected, CInt(TargetOffset), Patch.Length)

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Writing to cloned extent corrupted source or clone data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that cloning a small adjacent cross-boundary range materialises a new physical record instead of sharing tiny boundary fragments.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneSmallAdjacentBoundaryRangeMaterialisesNewRecord()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                .ChunkSize = 1024,
                                .BisectLimit = 256
                            }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                    Tests.Helpers.GenerateRandomData(
                                        Options.ChunkSize * 4,
                                        8).ToArray()

                        Cs.Write(0, Data)

                        Dim SourceOffset = Options.ChunkSize - 100L
                        Dim CloneLength = 200L
                        Dim TargetOffset = Data.Length

                        Dim Before = Cs.GetStructure()

                        Dim LeftSource =
                                    Before.Chunks.Single(Function(chunk) chunk.LogicalOffset = 0)

                        Dim RightSource =
                                    Before.Chunks.Single(Function(chunk) chunk.LogicalOffset = Options.ChunkSize)

                        Cs.Clone(SourceOffset, CloneLength, TargetOffset)

                        Dim After = Cs.GetStructure()

                        Dim ClonedExtent =
                                    After.Chunks.Single(Function(chunk) chunk.LogicalOffset = TargetOffset)

                        AssertEqual(
                                    CInt(CloneLength),
                                    ClonedExtent.PlainLength,
                                    "Small adjacent boundary clone should be represented by one materialised extent.")

                        AssertTrue(
                                    ClonedExtent.PhysicalRecordId.HasValue,
                                    "Materialised clone extent should have a physical record.")

                        AssertTrue(
                                    ClonedExtent.PhysicalRecordId.Value <> LeftSource.PhysicalRecordId.Value,
                                    "Materialised clone should not share the left source physical record.")

                        AssertTrue(
                                    ClonedExtent.PhysicalRecordId.Value <> RightSource.PhysicalRecordId.Value,
                                    "Materialised clone should not share the right source physical record.")

                        Dim Expected = New Byte(Data.Length + CInt(CloneLength) - 1) {}
                        Buffer.BlockCopy(Data, 0, Expected, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), Expected, CInt(TargetOffset), CInt(CloneLength))

                        AssertBytesEqual(
                                    Expected,
                                    Cs.ToArray(),
                                    "Small adjacent boundary clone returned incorrect logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that inserting data into the middle of an extent preserves bytes before and after the insertion.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertIntoMiddleOfExtentPreservesSurroundingBytes()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 5,
                                            8).ToArray()

                        Dim InsertData =
                                        Tests.Helpers.GenerateRandomData(
                                            333,
                                            44).ToArray()

                        Dim InsertOffset = Options.ChunkSize + 123

                        Cs.Write(0, Data)
                        Cs.Insert(InsertOffset, InsertData)

                        Dim Expected = New Byte(Data.Length + InsertData.Length - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, InsertOffset)
                        Buffer.BlockCopy(InsertData, 0, Expected, InsertOffset, InsertData.Length)
                        Buffer.BlockCopy(Data, InsertOffset, Expected, InsertOffset + InsertData.Length, Data.Length - InsertOffset)

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Insert into middle of extent corrupted surrounding bytes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that inserting data before cloned shared extents preserves shared records where possible and keeps logical data correct.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertBeforeSharedClonePreservesSharedRecordsAndData()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 6,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim SourceOffset = Options.ChunkSize * 2L
                        Dim CloneLength = Options.ChunkSize * 2L
                        Dim CloneOffset = Data.Length

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim BeforeInsert = Cs.GetStructure()

                        Dim SourceRecordId =
                                        BeforeInsert.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset).PhysicalRecordId.Value

                        Dim CloneRecordId =
                                        BeforeInsert.Chunks.Single(Function(chunk) chunk.LogicalOffset = CloneOffset).PhysicalRecordId.Value

                        AssertEqual(
                                        SourceRecordId,
                                        CloneRecordId,
                                        "Clone should share source record before insert.")

                        Dim InsertData =
                                        Tests.Helpers.GenerateRandomData(
                                            500,
                                            77).ToArray()

                        Cs.Insert(Options.ChunkSize, InsertData)

                        Dim ExpectedBeforeInsert = New Byte(Data.Length + CInt(CloneLength) - 1) {}
                        Buffer.BlockCopy(Data, 0, ExpectedBeforeInsert, 0, Data.Length)
                        Buffer.BlockCopy(Data, CInt(SourceOffset), ExpectedBeforeInsert, CInt(CloneOffset), CInt(CloneLength))

                        Dim Expected = New Byte(ExpectedBeforeInsert.Length + InsertData.Length - 1) {}
                        Buffer.BlockCopy(ExpectedBeforeInsert, 0, Expected, 0, Options.ChunkSize)
                        Buffer.BlockCopy(InsertData, 0, Expected, Options.ChunkSize, InsertData.Length)
                        Buffer.BlockCopy(ExpectedBeforeInsert,
                                                     Options.ChunkSize,
                                                     Expected,
                                                     Options.ChunkSize + InsertData.Length,
                                                     ExpectedBeforeInsert.Length - Options.ChunkSize)

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Insert before shared clone corrupted logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a middle range across extent boundaries preserves the remaining prefix and suffix bytes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveAcrossExtentBoundariesPreservesRemainingBytes()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 6,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim RemoveOffset = Options.ChunkSize - 200
                        Dim RemoveLength = Options.ChunkSize + 400

                        Cs.Remove(RemoveOffset, RemoveLength)

                        Dim Expected = New Byte(Data.Length - RemoveLength - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, RemoveOffset)
                        Buffer.BlockCopy(Data,
                                                     RemoveOffset + RemoveLength,
                                                     Expected,
                                                     RemoveOffset,
                                                     Data.Length - RemoveOffset - RemoveLength)

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Remove across extent boundaries corrupted remaining bytes.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a range can materialise small adjacent boundary fragments into a single replacement extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveMaterialisesSmallAdjacentBoundaryFragments()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024,
                                    .BisectLimit = 256
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 3,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim RemoveOffset = 100
                        Dim RemoveEnd = (Options.ChunkSize * 3) - 124
                        Dim RemoveLength = RemoveEnd - RemoveOffset

                        Cs.Remove(RemoveOffset, RemoveLength)

                        Dim ExpectedLength = 224
                        Dim Expected = New Byte(ExpectedLength - 1) {}

                        Buffer.BlockCopy(Data, 0, Expected, 0, 100)
                        Buffer.BlockCopy(Data, RemoveEnd, Expected, 100, 124)

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Remove materialisation corrupted logical data.")

                        Dim Struct = Cs.GetStructure()

                        AssertEqual(
                                        1,
                                        Struct.Chunks.Count,
                                        "Remove with small adjacent boundary fragments should materialise one replacement extent.")

                        Dim OnlyChunk = Struct.Chunks.Single()

                        AssertFalse(
                            OnlyChunk.IsSparse,
                            "Materialised remove boundary extent should be allocated.")

                        AssertEqual(
                                        ExpectedLength,
                                        OnlyChunk.PlainLength,
                                        "Materialised remove boundary extent should have the combined boundary length.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing a cloned shared extent decrements references without deleting the source physical record.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveSharedCloneLeavesOriginalReadable()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 5,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim SourceOffset = Options.ChunkSize
                        Dim CloneLength = Options.ChunkSize * 2
                        Dim CloneOffset = Data.Length

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim StructureAfterClone = Cs.GetStructure()

                        Dim SourceRecordId =
                                        StructureAfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = SourceOffset).PhysicalRecordId.Value

                        Dim CloneRecordId =
                                        StructureAfterClone.Chunks.Single(Function(chunk) chunk.LogicalOffset = CloneOffset).PhysicalRecordId.Value

                        AssertEqual(
                                        SourceRecordId,
                                        CloneRecordId,
                                        "Clone should share the source physical record before remove.")

                        Cs.Remove(CloneOffset, CloneLength)

                        AssertBytesEqual(
                                        Data,
                                        Cs.ToArray(),
                                        "Removing cloned shared extent should leave the original data intact.")

                        Dim StructureAfterRemove = Cs.GetStructure()

                        AssertTrue(
                                        StructureAfterRemove.Chunks.Any(Function(chunk) chunk.PhysicalRecordId.HasValue AndAlso chunk.PhysicalRecordId.Value = SourceRecordId),
                                        "Original physical record should still be referenced after removing cloned extent.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies clone, insert and remove together on an encrypted extent stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneInsertRemoveCombinedPreservesData()

                Using Ms As New MemoryStream()

                    Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                                    .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                                    .ChunkSize = 1024,
                                    .BisectLimit = 256
                                }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Data =
                                        Tests.Helpers.GenerateRandomData(
                                            Options.ChunkSize * 20,
                                            8).ToArray()

                        Cs.Write(0, Data)

                        Dim Expected = DirectCast(Data.Clone(), Byte())

                        Dim CloneOffset = Expected.Length
                        Dim SourceOffset = Options.ChunkSize * 3
                        Dim CloneLength = Options.ChunkSize * 4

                        Cs.Clone(SourceOffset, CloneLength, CloneOffset)

                        Dim AfterClone = New Byte(Expected.Length + CloneLength - 1) {}
                        Buffer.BlockCopy(Expected, 0, AfterClone, 0, Expected.Length)
                        Buffer.BlockCopy(Expected, SourceOffset, AfterClone, CloneOffset, CloneLength)
                        Expected = AfterClone

                        Dim InsertData =
                                        Tests.Helpers.GenerateRandomData(
                                            777,
                                            123).ToArray()

                        Dim InsertOffset = Options.ChunkSize * 2 + 50

                        Cs.Insert(InsertOffset, InsertData)

                        Dim AfterInsert = New Byte(Expected.Length + InsertData.Length - 1) {}
                        Buffer.BlockCopy(Expected, 0, AfterInsert, 0, InsertOffset)
                        Buffer.BlockCopy(InsertData, 0, AfterInsert, InsertOffset, InsertData.Length)
                        Buffer.BlockCopy(Expected,
                                                     InsertOffset,
                                                     AfterInsert,
                                                     InsertOffset + InsertData.Length,
                                                     Expected.Length - InsertOffset)
                        Expected = AfterInsert

                        Dim RemoveOffset = Options.ChunkSize * 5 + 33
                        Dim RemoveLength = Options.ChunkSize + 123

                        Cs.Remove(RemoveOffset, RemoveLength)

                        Dim AfterRemove = New Byte(Expected.Length - RemoveLength - 1) {}
                        Buffer.BlockCopy(Expected, 0, AfterRemove, 0, RemoveOffset)
                        Buffer.BlockCopy(Expected,
                                                     RemoveOffset + RemoveLength,
                                                     AfterRemove,
                                                     RemoveOffset,
                                                     Expected.Length - RemoveOffset - RemoveLength)
                        Expected = AfterRemove

                        AssertBytesEqual(
                                        Expected,
                                        Cs.ToArray(),
                                        "Combined clone, insert and remove operation corrupted data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class
End Namespace