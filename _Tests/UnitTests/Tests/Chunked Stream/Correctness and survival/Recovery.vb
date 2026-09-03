Imports System.IO
Imports System.Security.Cryptography
Imports i00.Streams

Namespace Tests

    Partial Class CorrectnessAndSurvival

        Public NotInheritable Class Recovery

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Header-copy recovery
            ' ================================================================================

            ''' <summary>
            ''' Verifies that the second header copy is sufficient to open the stream when
            ''' the first header copy is corrupt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesSecondCopyWhenFirstHeaderCorrupt()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 3,
                            1001)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data)
                        Cs.Validate().ThrowIfErrors()
                    End Using

                    CorruptHeaderCopy(Ms, 0)

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "Opening with corrupt header copy 0 did not recover from header copy 1.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that the first header copy is sufficient to open the stream when
            ''' the second header copy is corrupt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesFirstCopyWhenSecondHeaderCorrupt()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 3,
                            1002)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data)
                        Cs.Validate().ThrowIfErrors()
                    End Using

                    CorruptHeaderCopy(Ms, 1)

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "Opening with corrupt header copy 1 did not recover from header copy 0.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening fails when both header copies are corrupt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryFailsWhenBothHeaderCopiesAreCorrupt()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                1003))

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    CorruptHeaderCopy(Ms, 0)
                    CorruptHeaderCopy(Ms, 1)

                    AssertThrows(Of InvalidDataException)(
                        Sub()
                            Using Reopened = ChunkedStream.Open(Ms)
                            End Using
                        End Sub,
                        "Opening should fail when both header copies are corrupt.")

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting the older header copy still allows the stream to open
            ''' using the newest valid header copy.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesNewestValidCopyWhenOlderCopyIsCorrupt()

                Using Ms As New MemoryStream()

                    Dim Data0 =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            1004)

                    Dim Data1 =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            1005)

                    Dim Expected =
                        CombineArrays(
                            Data0,
                            Data1)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Data0)
                        Cs.Write(Data0.Length, Data1)
                        Cs.Validate().ThrowIfErrors()

                    End Using

                    CorruptHeaderCopy(
                        Ms,
                        GetOlderHeaderCopyIndex(Ms))

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Opening after corrupting the older header copy did not select the newest valid copy.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting the newest header copy still allows the stream to open
            ''' using an older valid header copy.
            '''
            ''' The older header may represent an earlier committed state, so this test accepts
            ''' any known committed state rather than requiring the latest logical state.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesOlderValidCopyWhenNewestCopyIsCorrupt()

                Using Ms As New MemoryStream()

                    Dim Data0 =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            1006)

                    Dim Data1 =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            1007)

                    Dim FirstCommitted =
                        DirectCast(Data0.Clone(), Byte())

                    Dim LatestCommitted =
                        CombineArrays(
                            Data0,
                            Data1)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Data0)
                        Cs.Write(Data0.Length, Data1)
                        Cs.Validate().ThrowIfErrors()

                    End Using

                    CorruptHeaderCopy(
                        Ms,
                        GetNewerHeaderCopyIndex(Ms))

                    Using Reopened = ChunkedStream.Open(Ms)

                        Dim Actual =
                            Reopened.ToArray()

                        AssertTrue(
                            BytesEqual(FirstCommitted, Actual) OrElse
                            BytesEqual(LatestCommitted, Actual),
                            "Opening after corrupting the newest header copy did not fall back to a known committed state.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Checkpoint crash recovery
            ' ================================================================================

            ''' <summary>
            ''' Verifies that an active checkpoint is recovered by rolling back uncommitted
            ''' data on the next open.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryCheckpointActiveRollsBackOnOpen()

                Using Ms As New MemoryStream()

                    Dim Original =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 2,
                            2001)

                    Dim Updated =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 2,
                            2002)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Original)

                    Dim AbandonedCheckpoint =
                        Cs.CreateCheckpoint()

                    Cs.Write(0, Updated)

                    GC.KeepAlive(AbandonedCheckpoint)
                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.CheckpointActive,
                            Reopened.RecoveryStateAtOpen,
                            "Expected checkpoint recovery state at open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected checkpoint recovery to repair the stream.")

                        AssertBytesEqual(
                            Original,
                            Reopened.ToArray(),
                            "Checkpoint recovery did not roll back uncommitted data.")

                        AssertEqual(
                            CLng(Original.Length),
                            Reopened.Length,
                            "Recovered stream length was incorrect.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening a stream with active checkpoint recovery state fails
            ''' when the backing stream is read-only.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryCheckpointActiveRequiresWritableStream()

                Using Ms As New MemoryStream()

                    Dim Original =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize,
                            2003)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Original)

                    Dim AbandonedCheckpoint =
                        Cs.CreateCheckpoint()

                    Cs.Write(
                        0,
                        GenerateRandomData(
                            Cs.options.ChunkSize,
                            2004))

                    GC.KeepAlive(AbandonedCheckpoint)
                    Cs = Nothing

                    Using ReadOnlyStream As New MemoryStream(Ms.ToArray(), False)

                        AssertThrows(Of NotSupportedException)(
                            Sub()
                                Using Reopened = ChunkedStream.Open(ReadOnlyStream)
                                End Using
                            End Sub,
                            "Opening a stream pending recovery should require a writable backing stream.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Uncommitted-churn crash safety
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a long run of mutations that are never published durably cannot
            ''' overwrite storage that an earlier, still-selectable header generation
            ''' references. Simulates a crash whose newest header copy is unusable, forcing
            ''' Open to fall back to the previous copy, and requires that fallback generation
            ''' to load cleanly and to be a state the stream genuinely passed through.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub UncommittedChurnDoesNotCorruptEarlierHeaderGeneration()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 47

                            Cs.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(Options.ChunkSize, 9000 + ChunkIndex))

                        Next

                    End Using

                    '
                    ' Reopen and churn without ever publishing durably. Every metadata
                    ' publish here is non-durable and must therefore be append-only: it
                    ' cannot touch storage that either on-disk header copy still points at.
                    '
                    Dim Snapshots As New List(Of Byte())()
                    Dim Live = ChunkedStream.Open(Ms, Options)

                    Snapshots.Add(Live.ToArray())

                    For Pass = 0 To 15

                        For ChunkIndex = 0 To 47 Step 2

                            Live.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(Options.ChunkSize, 40000 + (Pass * 100) + ChunkIndex))

                            '
                            ' Every individual write is its own non-durable publish and
                            ' its own header-copy flip, so any of these states can become
                            ' the fallback generation after the crash.
                            '
                            Snapshots.Add(Live.ToArray())

                        Next

                    Next

                    GC.KeepAlive(Live)
                    Live = Nothing

                    '
                    ' Simulate the crash-recovery fallback path: the newest header copy is
                    ' unusable (torn write or a lost write-buffer tail), so Open must select
                    ' the older copy.
                    '
                    CorruptHeaderCopy(Ms, GetNewerHeaderCopyIndex(Ms))

                    Using Reopened = ChunkedStream.Open(Ms)

                        Reopened.Validate().ThrowIfErrors()

                        Dim Recovered = Reopened.ToArray()

                        AssertTrue(
                            Snapshots.Any(Function(snapshot) BytesEqual(snapshot, Recovered)),
                            "Falling back to the earlier header generation did not yield a state the stream actually passed through, so uncommitted churn had overwritten its storage.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that when the newest header is itself valid but the generation it
            ''' describes was only partly written before the crash, Open falls back to the
            ''' previous header copy and still opens a coherent state rather than failing.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OpenFallsBackToPreviousHeaderWhenNewestGenerationMetadataIsTruncated()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 47

                            Cs.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(Options.ChunkSize, 8100 + ChunkIndex))

                        Next

                    End Using

                    Dim Snapshots As New List(Of Byte())()
                    Dim Live = ChunkedStream.Open(Ms, Options)

                    Snapshots.Add(Live.ToArray())

                    For Pass = 0 To 10

                        For ChunkIndex = 0 To 47 Step 2

                            Live.Write(
                                ChunkIndex * Options.ChunkSize,
                                GenerateRandomData(Options.ChunkSize, 50000 + (Pass * 100) + ChunkIndex))

                            Snapshots.Add(Live.ToArray())

                        Next

                    Next

                    GC.KeepAlive(Live)
                    Live = Nothing

                    '
                    ' Simulate a crash that lost the tail of the backing store: the newest
                    ' generation's appended metadata root is truncated away, but its header
                    ' copy (written at the front of the file) survived and still validates.
                    '
                    Ms.SetLength(Ms.Length - 64)

                    Using Reopened = ChunkedStream.Open(Ms)

                        Reopened.Validate().ThrowIfErrors()

                        Dim Recovered = Reopened.ToArray()

                        AssertTrue(
                            Snapshots.Any(Function(snapshot) BytesEqual(snapshot, Recovered)),
                            "Open did not fall back to a coherent earlier generation after the newest generation's metadata was truncated.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Chunk-size rebuild recovery
            ' ================================================================================

            ''' <summary>
            ''' Verifies that incomplete chunk-size rebuild recovery truncates abandoned
            ''' rebuild output on the next open.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryChunkSizeRebuildRollsBackOnOpen()

                Using Ms As New MemoryStream()

                    Dim Original =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            3001)

                    Dim OriginalPhysicalLength As Long

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Original)
                        Cs.Validate().ThrowIfErrors()

                        OriginalPhysicalLength = Ms.Length

                        Cs.WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength)

                        Dim AbandonedData =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 3,
                                3002)

                        Ms.Position = Ms.Length
                        Ms.Write(AbandonedData, 0, AbandonedData.Length)

                        AssertTrue(
                            Ms.Length > OriginalPhysicalLength,
                            "Test setup failed to append abandoned rebuild data.")

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.ChunkSizeRebuildActive,
                            Reopened.RecoveryStateAtOpen,
                            "Expected chunk-size rebuild recovery state at open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected chunk-size rebuild recovery to repair the stream.")

                        AssertEqual(
                            OriginalPhysicalLength,
                            Ms.Length,
                            "Chunk-size rebuild recovery did not truncate abandoned rebuild output.")

                        AssertBytesEqual(
                            Original,
                            Reopened.ToArray(),
                            "Chunk-size rebuild recovery corrupted committed data.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that invalid chunk-size rebuild recovery state can be opened for
            ''' diagnostics when recovery failure is explicitly allowed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryFailureCanBeOpenedForDiagnostics()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                3003))
                        Cs.WriteChunkSizeRebuildRecoveryState(Ms.Length + Cs.Options.ChunkSize)

                    End Using

                    Using Reopened =
                        ChunkedStream.Open(
                            Ms,
                            Nothing,
                            True)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.ChunkSizeRebuildActive,
                            Reopened.RecoveryStateAtOpen,
                            "Expected chunk-size rebuild recovery state at diagnostic open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Failed,
                            Reopened.AutoRecoveryState,
                            "Expected diagnostic open to record failed recovery.")

                        AssertNotNothing(
                            Reopened.AutoRecoveryException,
                            "Expected diagnostic open to expose the recovery exception.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Physical-record move recovery
            ' ================================================================================

            ''' <summary>
            ''' Verifies that recovery state CopyingPhysicalRecord restores the old physical
            ''' record location when the copy was not completed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryPhysicalRecordMoveCopyingRestoresOldRecord()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            4001)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Data)
                    Cs.Validate().ThrowIfErrors()

                    Dim Chunk =
                        GetFirstAllocatedChunk(Cs)

                    Dim OldOffset =
                        Chunk.PhysicalOffset.Value

                    Dim OldLength =
                        Chunk.PhysicalLength.Value

                    Dim NewOffset =
                        Ms.Length + 4096L

                    Cs.WritePhysicalRecordMoveRecoveryState(
                        ChunkedStream.RecoveryStates.CopyingPhysicalRecord,
                        Chunk.PhysicalRecordId.Value,
                        OldOffset,
                        OldLength,
                        NewOffset,
                        OldLength)

                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.CopyingPhysicalRecord,
                            Reopened.RecoveryStateAtOpen,
                            "Expected CopyingPhysicalRecord recovery state at open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected physical-record copying recovery to repair the stream.")

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "CopyingPhysicalRecord recovery corrupted logical data.")

                        Dim RecoveredChunk =
                            Reopened.
                            GetStructure().
                            Chunks.
                            First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                              x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            OldOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "CopyingPhysicalRecord recovery should keep the old physical record location.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that recovery state PhysicalRecordCopied publishes the copied physical
            ''' record location when the copied record is valid.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryPhysicalRecordMoveCopiedPublishesNewRecord()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            4002)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Data)
                    Cs.Validate().ThrowIfErrors()

                    Dim Chunk =
                        GetFirstAllocatedChunk(Cs)

                    Dim OldOffset =
                        Chunk.PhysicalOffset.Value

                    Dim OldLength =
                        Chunk.PhysicalLength.Value

                    Dim NewOffset =
                        Ms.Length + 4096L

                    CopyPhysicalBytes(
                        Ms,
                        OldOffset,
                        NewOffset,
                        OldLength)

                    Cs.WritePhysicalRecordMoveRecoveryState(
                        ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                        Chunk.PhysicalRecordId.Value,
                        OldOffset,
                        OldLength,
                        NewOffset,
                        OldLength)

                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                            Reopened.RecoveryStateAtOpen,
                            "Expected PhysicalRecordCopied recovery state at open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected copied physical-record recovery to repair the stream.")

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "PhysicalRecordCopied recovery corrupted logical data.")

                        Dim RecoveredChunk =
                            Reopened.
                            GetStructure().
                            Chunks.
                            First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                              x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            NewOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "PhysicalRecordCopied recovery should publish the new physical record location.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that PhysicalRecordCopied recovery falls back to the old physical
            ''' record location when the copied record is corrupt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryPhysicalRecordMoveCopiedFallsBackWhenNewRecordIsCorrupt()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            4003)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Data)
                    Cs.Validate().ThrowIfErrors()

                    Dim Chunk =
                        GetFirstAllocatedChunk(Cs)

                    Dim OldOffset =
                        Chunk.PhysicalOffset.Value

                    Dim OldLength =
                        Chunk.PhysicalLength.Value

                    Dim NewOffset =
                        Ms.Length + 4096L

                    CopyPhysicalBytes(
                        Ms,
                        OldOffset,
                        NewOffset,
                        OldLength)

                    Dim CorruptOffset =
                        NewOffset + Math.Min(50, OldLength - 1)

                    Ms.Position = CorruptOffset

                    Dim OriginalByte =
                        Ms.ReadByte()

                    If OriginalByte < 0 Then Throw New Exception("Could not read copied record byte to corrupt.")

                    Ms.Position = CorruptOffset
                    Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                    Cs.WritePhysicalRecordMoveRecoveryState(
                        ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                        Chunk.PhysicalRecordId.Value,
                        OldOffset,
                        OldLength,
                        NewOffset,
                        OldLength)

                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertEqual(
                            ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                            Reopened.RecoveryStateAtOpen,
                            "Expected PhysicalRecordCopied recovery state at open.")

                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected copied physical-record recovery to repair using the old record.")

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "PhysicalRecordCopied fallback recovery corrupted logical data.")

                        Dim RecoveredChunk =
                            Reopened.
                            GetStructure().
                            Chunks.
                            First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                              x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            OldOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "PhysicalRecordCopied recovery should fall back to the old record when the copied record is corrupt.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that physical-record move recovery fails when neither the old nor
            ''' new physical record is valid.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryPhysicalRecordMoveFailsWhenBothRecordsInvalid()

                Using Ms As New MemoryStream()

                    Dim Data =
                        GenerateRandomData(
                            ChunkedStream.DefaultChunkSize * 4,
                            4004)

                    Dim Cs =
                        ChunkedStream.Open(Ms)

                    Cs.Write(0, Data)
                    Cs.Validate().ThrowIfErrors()

                    Dim Chunk =
                        GetFirstAllocatedChunk(Cs)

                    Dim OldOffset =
                        Chunk.PhysicalOffset.Value

                    Dim OldLength =
                        Chunk.PhysicalLength.Value

                    Dim NewOffset =
                        Ms.Length + 4096L

                    CopyPhysicalBytes(
                        Ms,
                        OldOffset,
                        NewOffset,
                        OldLength)

                    CorruptPhysicalByte(
                        Ms,
                        OldOffset + 50)

                    CorruptPhysicalByte(
                        Ms,
                        NewOffset + 50)

                    Cs.WritePhysicalRecordMoveRecoveryState(
                        ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                        Chunk.PhysicalRecordId.Value,
                        OldOffset,
                        OldLength,
                        NewOffset,
                        OldLength)

                    Cs = Nothing

                    AssertThrows(Of CryptographicException)(
                        Sub()
                            Using Reopened = ChunkedStream.Open(Ms)
                            End Using
                        End Sub,
                        "Physical-record move recovery should fail when both old and new records are invalid.")

                End Using

            End Sub

            ' ================================================================================
            ' Auto-recovery after a fault (Options.AutoRecoverOnFault / Recover)
            ' ================================================================================

            ''' <summary>
            ''' With Options.AutoRecoverOnFault set, the first call after an operation faults
            ''' the stream reloads the last durably published generation from the backing
            ''' store: the stream is usable again with no dispose-and-reopen, the faulting
            ''' operation's work is discarded and the reload is counted.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AutoRecoverOnFaultReloadsAfterAFaultedWrite()

                Dim Backing As New FailingMemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .AutoRecoverOnFault = True
                }

                Using Cs = ChunkedStream.Open(Backing, Options)

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 5100)
                    Cs.Write(0, Baseline)
                    Cs.Flush()

                    Backing.FailOnWriteNumber = Backing.WriteCount + 1

                    AssertThrows(Of IOException)(
                        Sub() Cs.Write(0, GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 5101)),
                        "Expected the injected backing failure to surface.")

                    Backing.FailOnWriteNumber = 0

                    Dim AfterRecovery = GenerateRandomData(ChunkedStream.DefaultChunkSize, 5102)
                    Cs.Write(0, AfterRecovery)

                    AssertEqual(1, Cs.FaultRecoveryCount, "The faulted write should have triggered exactly one auto-recovery.")
                    AssertTrue(Cs.FaultRecoveryException Is Nothing, "A successful auto-recovery should leave no FaultRecoveryException.")

                    Dim Expected = DirectCast(Baseline.Clone(), Byte())
                    Overlay(Expected, AfterRecovery, 0)

                    AssertBytesEqual(Expected, Cs.ToArray(), "The auto-recovered stream did not reflect the last durable generation plus the new write.")
                    Cs.Validate().ThrowIfErrors()

                End Using

                Backing.Position = 0

                Using Reopened = ChunkedStream.Open(Backing)
                    Reopened.Validate().ThrowIfErrors()
                End Using

            End Sub

            ''' <summary>
            ''' Recover reloads a faulted stream from the backing store on demand even when
            ''' Options.AutoRecoverOnFault is left at its default of False.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RecoverRestoresAFaultedStreamOnDemand()

                Dim Backing As New FailingMemoryStream()

                Using Cs = ChunkedStream.Open(Backing)

                    Dim Baseline = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 5200)
                    Cs.Write(0, Baseline)
                    Cs.Flush()

                    Backing.FailOnWriteNumber = Backing.WriteCount + 1

                    AssertThrows(Of IOException)(
                        Sub() Cs.Write(0, GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 5201)),
                        "Expected the injected backing failure to surface.")

                    Backing.FailOnWriteNumber = 0

                    AssertThrows(Of InvalidOperationException)(
                        Sub() Cs.Write(0, New Byte(15) {}),
                        "A faulted stream without AutoRecoverOnFault should refuse further writes.")

                    Cs.Recover()

                    AssertEqual(1, Cs.FaultRecoveryCount, "Recover should have reloaded the stream once.")
                    AssertBytesEqual(Baseline, Cs.ToArray(), "Recover did not restore the last durable generation.")
                    Cs.Validate().ThrowIfErrors()

                    Cs.Write(0, GenerateRandomData(64, 5202))
                    Cs.Validate().ThrowIfErrors()

                End Using

            End Sub

            ''' <summary>
            ''' Recover is rejected while a checkpoint is open - the checkpoint owns the
            ''' rollback baseline and clears the fault itself when it closes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RecoverIsRejectedWhileACheckpointIsOpen()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(ChunkedStream.DefaultChunkSize, 5300))

                        Using Cs.CreateCheckpoint()

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Cs.Recover(),
                                "Recover should be rejected while a checkpoint is open.")

                        End Using

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' When the backing store cannot be reopened into a consistent image the stream
            ''' stays faulted, the reload failure is exposed through FaultRecoveryException
            ''' and it is not counted as a recovery.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AutoRecoverLeavesStreamFaultedWhenTheReloadCannotComplete()

                Dim Backing As New FailingMemoryStream()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .AutoRecoverOnFault = True
                }

                Using Cs = ChunkedStream.Open(Backing, Options)

                    Cs.Write(0, GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 5400))
                    Cs.Flush()

                    Backing.FailOnWriteNumber = Backing.WriteCount + 1

                    AssertThrows(Of IOException)(
                        Sub() Cs.Write(0, GenerateRandomData(ChunkedStream.DefaultChunkSize * 2, 5401)),
                        "Expected the injected backing failure to surface.")

                    Backing.FailOnWriteNumber = 0

                    CorruptHeaderCopy(Backing, 0)
                    CorruptHeaderCopy(Backing, 1)

                    AssertThrows(Of InvalidOperationException)(
                        Sub() Cs.Write(0, New Byte(15) {}),
                        "A stream whose reload fails should stay faulted.")

                    AssertTrue(Cs.FaultRecoveryException IsNot Nothing,
                               "A failed reload should record its exception in FaultRecoveryException.")
                    AssertEqual(0, Cs.FaultRecoveryCount, "A failed reload should not count as a recovery.")

                End Using

            End Sub

            ' ================================================================================
            ' Recovery helpers
            ' ================================================================================

            Private Shared Function GetFirstAllocatedChunk(Cs As ChunkedStream) As ChunkedStreamStructure.Chunk

                If Cs Is Nothing Then Throw New ArgumentNullException(NameOf(Cs))

                Return Cs.
                    GetStructure().
                    Chunks.
                    First(Function(x) x.IsAllocated AndAlso
                                      x.PhysicalRecordId.HasValue AndAlso
                                      x.PhysicalOffset.HasValue AndAlso
                                      x.PhysicalLength.HasValue)

            End Function

            Private Shared Sub CorruptHeaderCopy(Stream As MemoryStream,
                                                 HeaderCopyIndex As Integer)

                If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
                If HeaderCopyIndex < 0 OrElse HeaderCopyIndex > 1 Then Throw New ArgumentOutOfRangeException(NameOf(HeaderCopyIndex))

                Dim HeaderSize = ChunkedStream.HeaderSize

                Dim CorruptOffset =
                    CLng(HeaderCopyIndex * HeaderSize) + 32L

                CorruptPhysicalByte(
                    Stream,
                    CorruptOffset)

            End Sub

            Private Shared Function GetHeaderSequence(Stream As MemoryStream,
                                                      HeaderCopyIndex As Integer) As Long

                If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
                If HeaderCopyIndex < 0 OrElse HeaderCopyIndex > 1 Then Throw New ArgumentOutOfRangeException(NameOf(HeaderCopyIndex))

                Dim HeaderSize = ChunkedStream.HeaderSize

                Dim HeaderSequenceOffset = ChunkedStream.HeaderSequenceOffset

                Dim Buffer(7) As Byte

                Stream.Position =
                    CLng(HeaderCopyIndex * HeaderSize) + HeaderSequenceOffset

                Dim ReadBytes =
                    Stream.Read(Buffer, 0, Buffer.Length)

                If ReadBytes <> Buffer.Length Then Throw New EndOfStreamException("Could not read header sequence.")

                Return BitConverter.ToInt64(Buffer, 0)

            End Function

            Private Shared Function GetOlderHeaderCopyIndex(Stream As MemoryStream) As Integer

                Dim Seq0 =
                    GetHeaderSequence(Stream, 0)

                Dim Seq1 =
                    GetHeaderSequence(Stream, 1)

                If Seq0 <= Seq1 Then Return 0

                Return 1

            End Function

            Private Shared Function GetNewerHeaderCopyIndex(Stream As MemoryStream) As Integer

                Dim Seq0 =
                    GetHeaderSequence(Stream, 0)

                Dim Seq1 =
                    GetHeaderSequence(Stream, 1)

                If Seq0 >= Seq1 Then Return 0

                Return 1

            End Function

            Private Shared Sub CopyPhysicalBytes(Stream As MemoryStream,
                                                 SourceOffset As Long,
                                                 DestinationOffset As Long,
                                                 Length As Integer)

                If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
                If SourceOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(SourceOffset))
                If DestinationOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(DestinationOffset))
                If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
                If Length = 0 Then Return

                Dim Buffer(Length - 1) As Byte

                Stream.Position = SourceOffset

                Dim ReadBytes =
                    Stream.Read(Buffer, 0, Buffer.Length)

                If ReadBytes <> Buffer.Length Then Throw New EndOfStreamException("Could not read complete physical record.")

                Stream.Position = DestinationOffset
                Stream.Write(Buffer, 0, Buffer.Length)

            End Sub

            Private Shared Sub CorruptPhysicalByte(Stream As MemoryStream,
                                                   Offset As Long)

                If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
                If Offset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Offset))

                Stream.Position = Offset

                Dim OriginalByte =
                    Stream.ReadByte()

                If OriginalByte < 0 Then Throw New EndOfStreamException("Could not read byte to corrupt.")

                Stream.Position = Offset
                Stream.WriteByte(CByte(OriginalByte Xor &HFF))

            End Sub

        End Class

    End Class

End Namespace