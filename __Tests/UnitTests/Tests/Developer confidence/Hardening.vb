Imports System.IO
Imports System.Reflection
Imports StreamEncryption.Streams

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        Public NotInheritable Class Hardening
            Private Sub New()
            End Sub

            ' ================================================================================
            ' Header-copy corruption / selection tests
            ' ================================================================================

            ''' <summary>
            ''' Verifies that the second header copy is sufficient to open the stream when
            ''' the first header copy is corrupt.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesSecondCopyWhenFirstHeaderCorrupt()
                Using Ms As New MemoryStream()
                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 1001)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data)
                        Cs.Validate()
                    End Using

                    CorruptHeaderCopy(Ms, 0)

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "Opening with corrupt header copy 0 did not recover from header copy 1.")

                        Reopened.Validate()
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
                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 1002)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data)
                        Cs.Validate()
                    End Using

                    CorruptHeaderCopy(Ms, 1)

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "Opening with corrupt header copy 1 did not recover from header copy 0.")

                        Reopened.Validate()
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
                        Cs.Write(0, Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 1003))
                        Cs.Validate()
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
            ''' using the newer valid copy.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesNewestValidCopyWhenOlderCopyIsCorrupt()
                Using Ms As New MemoryStream()
                    Dim Data0 = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 1004)
                    Dim Data1 = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 1005)
                    Dim Expected = New Byte((ChunkedStream.DefaultChunkSize * 2) - 1) {}

                    Buffer.BlockCopy(Data0, 0, Expected, 0, Data0.Length)
                    Buffer.BlockCopy(Data1, 0, Expected, Data0.Length, Data1.Length)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data0)
                        Cs.Write(Data0.Length, Data1)
                        Cs.Validate()
                    End Using

                    Dim OlderCopy = GetOlderHeaderCopyIndex(Ms)
                    CorruptHeaderCopy(Ms, OlderCopy)

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Opening after corrupting the older header copy did not select the newer valid copy.")

                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ''' <summary>
            ''' Verifies that corrupting the newer header copy still allows the stream to open
            ''' using the older valid copy.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub HeaderRecoveryUsesOlderValidCopyWhenNewestCopyIsCorrupt()
                Using Ms As New MemoryStream()
                    Dim Data0 = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 1006)
                    Dim Data1 = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize, 1007)
                    Dim Expected = New Byte((ChunkedStream.DefaultChunkSize * 2) - 1) {}

                    Buffer.BlockCopy(Data0, 0, Expected, 0, Data0.Length)
                    Buffer.BlockCopy(Data1, 0, Expected, Data0.Length, Data1.Length)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Data0)
                        Cs.Write(Data0.Length, Data1)
                        Cs.Validate()
                    End Using

                    Dim NewerCopy = GetNewerHeaderCopyIndex(Ms)
                    CorruptHeaderCopy(Ms, NewerCopy)

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Opening after corrupting the newest header copy did not fall back to the older valid copy.")

                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ' ================================================================================
            ' Physical-record move crash recovery tests
            ' ================================================================================

            ''' <summary>
            ''' Verifies that recovery state CopyingPhysicalRecord restores the old physical
            ''' record location when the copy was not completed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CrashRecoveryPhysicalRecordMoveCopyingRestoresOldRecord()
                Using Ms As New MemoryStream()
                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 2001)

                    Dim Cs = ChunkedStream.Open(Ms)
                    Cs.Write(0, Data)
                    Cs.Validate()

                    Dim Struct = Cs.GetStructure()
                    Dim Chunk =
                        Struct.Chunks.
                               First(Function(x) x.IsAllocated AndAlso
                                                 x.PhysicalRecordId.HasValue AndAlso
                                                 x.PhysicalOffset.HasValue AndAlso
                                                 x.PhysicalLength.HasValue)

                    Dim OldOffset = Chunk.PhysicalOffset.Value
                    Dim OldLength = Chunk.PhysicalLength.Value
                    Dim NewOffset = Math.Max(Ms.Length, Struct.MetadataRootEndOffset) + 4096L

                    InvokePrivateSub(
                        Cs,
                        "WritePhysicalRecordMoveRecoveryState",
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
                            Reopened.GetStructure().
                                     Chunks.
                                     First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                                       x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            OldOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "CopyingPhysicalRecord recovery should keep the old physical record location.")

                        Reopened.Validate()
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
                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 2002)

                    Dim Cs = ChunkedStream.Open(Ms)
                    Cs.Write(0, Data)
                    Cs.Validate()

                    Dim Struct = Cs.GetStructure()
                    Dim Chunk =
                        Struct.Chunks.
                               First(Function(x) x.IsAllocated AndAlso
                                                 x.PhysicalRecordId.HasValue AndAlso
                                                 x.PhysicalOffset.HasValue AndAlso
                                                 x.PhysicalLength.HasValue)

                    Dim OldOffset = Chunk.PhysicalOffset.Value
                    Dim OldLength = Chunk.PhysicalLength.Value
                    Dim NewOffset = Math.Max(Ms.Length, Struct.MetadataRootEndOffset) + 4096L

                    CopyPhysicalBytes(Ms, OldOffset, NewOffset, OldLength)

                    InvokePrivateSub(
                        Cs,
                        "WritePhysicalRecordMoveRecoveryState",
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
                            Reopened.GetStructure().
                                     Chunks.
                                     First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                                       x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            NewOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "PhysicalRecordCopied recovery should publish the new physical record location.")

                        Reopened.Validate()
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
                    Dim Data = Helpers.GenerateRandomData(ChunkedStream.DefaultChunkSize * 4, 2003)

                    Dim Cs = ChunkedStream.Open(Ms)
                    Cs.Write(0, Data)
                    Cs.Validate()

                    Dim Struct = Cs.GetStructure()
                    Dim Chunk =
                        Struct.Chunks.
                               First(Function(x) x.IsAllocated AndAlso
                                                 x.PhysicalRecordId.HasValue AndAlso
                                                 x.PhysicalOffset.HasValue AndAlso
                                                 x.PhysicalLength.HasValue)

                    Dim OldOffset = Chunk.PhysicalOffset.Value
                    Dim OldLength = Chunk.PhysicalLength.Value
                    Dim NewOffset = Math.Max(Ms.Length, Struct.MetadataRootEndOffset) + 4096L

                    CopyPhysicalBytes(Ms, OldOffset, NewOffset, OldLength)

                    Ms.Position = NewOffset + Math.Min(50, OldLength - 1)
                    Dim OriginalByte = Ms.ReadByte()
                    If OriginalByte < 0 Then Throw New Exception("Could not read copied record byte to corrupt.")
                    Ms.Position = NewOffset + Math.Min(50, OldLength - 1)
                    Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                    InvokePrivateSub(
                        Cs,
                        "WritePhysicalRecordMoveRecoveryState",
                        ChunkedStream.RecoveryStates.PhysicalRecordCopied,
                        Chunk.PhysicalRecordId.Value,
                        OldOffset,
                        OldLength,
                        NewOffset,
                        OldLength)

                    Cs = Nothing

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertEqual(
                            ChunkedStream.AutoRecoveryStates.Repaired,
                            Reopened.AutoRecoveryState,
                            "Expected copied physical-record recovery to repair using the old record.")

                        AssertBytesEqual(
                            Data,
                            Reopened.ToArray(),
                            "PhysicalRecordCopied fallback recovery corrupted logical data.")

                        Dim RecoveredChunk =
                            Reopened.GetStructure().
                                     Chunks.
                                     First(Function(x) x.PhysicalRecordId.HasValue AndAlso
                                                       x.PhysicalRecordId.Value = Chunk.PhysicalRecordId.Value)

                        AssertEqual(
                            OldOffset,
                            RecoveredChunk.PhysicalOffset.Value,
                            "PhysicalRecordCopied recovery should fall back to the old record when the copied record is corrupt.")

                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ' ================================================================================
            ' Model-based deterministic fuzz test
            ' ================================================================================

            ''' <summary>
            ''' Performs deterministic random logical operations against a simple byte-array
            ''' model and verifies ChunkedStream after each operation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ModelBasedRandomOperationsMatchByteArrayModel()
                Using Ms As New MemoryStream()
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .IndexPageEntryCount = 8,
                        .IndexDirectoryEntryCount = 8,
                        .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHolesFromStart
                    }

                    Dim Model As New List(Of Byte)()
                    Dim Checkpoints As New Stack(Of FuzzCheckpoint)()
                    Dim Rng As New Random(123456)

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        For OperationIndex = 0 To 399
                            Dim Operation = Rng.Next(0, If(Checkpoints.Count = 0, 10, 8))

                            Select Case Operation
                                Case 0
                                    FuzzWrite(Cs, Model, Rng)

                                Case 1
                                    FuzzInsert(Cs, Model, Rng)

                                Case 2
                                    FuzzRemove(Cs, Model, Rng)

                                Case 3
                                    FuzzSetLength(Cs, Model, Rng)

                                Case 4
                                    FuzzClone(Cs, Model, Rng)

                                Case 5
                                    Checkpoints.Push(
                                        New FuzzCheckpoint With {
                                            .Checkpoint = Cs.CreateCheckpoint(),
                                            .ModelSnapshot = Model.ToArray()
                                        })

                                Case 6
                                    If Checkpoints.Count > 0 Then
                                        Dim Top = Checkpoints.Pop()
                                        Top.Checkpoint.Commit()
                                        Top.Checkpoint.Dispose()
                                    End If

                                Case 7
                                    If Checkpoints.Count > 0 Then
                                        Dim Top = Checkpoints.Pop()
                                        Top.Checkpoint.Dispose()
                                        Model = New List(Of Byte)(Top.ModelSnapshot)
                                    End If

                                Case 8
                                    FuzzApplyOptions(Cs, Rng)

                                Case 9
                                    If Checkpoints.Count = 0 Then
                                        FuzzDefrag(Cs, Rng)
                                    End If
                            End Select

                            AssertBytesEqual(
                                Model.ToArray(),
                                Cs.ToArray(),
                                $"Model mismatch after operation {OperationIndex}.")

                            Cs.Validate()
                        Next

                        While Checkpoints.Count > 0
                            Dim Top = Checkpoints.Pop()
                            Top.Checkpoint.Dispose()
                            Model = New List(Of Byte)(Top.ModelSnapshot)

                            AssertBytesEqual(
                                Model.ToArray(),
                                Cs.ToArray(),
                                "Model mismatch after final checkpoint unwind.")
                        End While

                        Cs.Validate()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(
                            Model.ToArray(),
                            Reopened.ToArray(),
                            "Model mismatch after reopening fuzzed stream.")

                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ' ================================================================================
            ' Metadata paging / directory stress tests
            ' ================================================================================

            ''' <summary>
            ''' Verifies that many extents and physical records spanning many metadata pages
            ''' survive reopen, validation and sequence defragmentation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MetadataPagingManyExtentPagesSurviveReopenAndDefrag()
                Using Ms As New MemoryStream()
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4,
                        .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles
                    }

                    Dim Expected As New List(Of Byte)()

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        For ChunkIndex = 0 To 159
                            Dim Data = Helpers.GenerateRandomData(Options.ChunkSize, 3000 + ChunkIndex)
                            Cs.Write(CLng(ChunkIndex) * CLng(Options.ChunkSize), Data)
                            Expected.AddRange(Data)
                        Next

                        ' Create additional extent fragmentation and physical-record churn.
                        For ChunkIndex = 10 To 40 Step 3
                            Dim Patch = Helpers.GenerateRandomData(17, 4000 + ChunkIndex)
                            Dim Offset = (ChunkIndex * Options.ChunkSize) + 31
                            Cs.Write(Offset, Patch)

                            For i = 0 To Patch.Length - 1
                                Expected(Offset + i) = Patch(i)
                            Next
                        Next

                        Dim Before = Cs.GetStructure()

                        AssertTrue(
                            Before.ChunkCount > Options.IndexPageEntryCount * Options.IndexDirectoryEntryCount,
                            "Test setup did not create enough chunks to force paged metadata.")

                        AssertTrue(
                            Before.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.IndexPage),
                            "Expected index-page regions in paged metadata stream.")

                        AssertTrue(
                            Before.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.ChunkIndexDirectoryPage),
                            "Expected index-directory-page regions in paged metadata stream.")

                        AssertBytesEqual(Expected.ToArray(), Cs.ToArray(), "Paged metadata data mismatch before reopen.")
                        Cs.Validate()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Expected.ToArray(), Reopened.ToArray(), "Paged metadata data mismatch after reopen.")
                        Reopened.Validate()

                        Reopened.Defragment(ChunkedStream.DefragTypes.Sequence)

                        AssertBytesEqual(Expected.ToArray(), Reopened.ToArray(), "Paged metadata data mismatch after sequence defrag.")
                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ''' <summary>
            ''' Verifies that hole-directory metadata spanning multiple pages survives reopen
            ''' and does not cause subsequent FillHoles allocation to overlap live records.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub MetadataPagingHoleDirectorySurvivesReopenAndReuse()
                Using Ms As New MemoryStream()
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 256,
                        .IndexPageEntryCount = 4,
                        .IndexDirectoryEntryCount = 4,
                        .HoleDirectoryMode = ChunkedStream.ChunkedStreamOptions.HoleDirectoryModes.Always,
                        .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles
                    }

                    Dim Expected As New List(Of Byte)()

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        For ChunkIndex = 0 To 127
                            Dim Data = Helpers.GenerateRandomData(Options.ChunkSize, 5000 + ChunkIndex)
                            Cs.Write(CLng(ChunkIndex) * CLng(Options.ChunkSize), Data)
                            Expected.AddRange(Data)
                        Next

                        For ChunkIndex = 1 To 120 Step 3
                            Dim Data = Helpers.GenerateRandomData(Options.ChunkSize, 6000 + ChunkIndex)
                            Cs.Write(CLng(ChunkIndex) * CLng(Options.ChunkSize), Data)

                            For i = 0 To Data.Length - 1
                                Expected((ChunkIndex * Options.ChunkSize) + i) = Data(i)
                            Next
                        Next

                        Cs.Validate()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)
                        AssertBytesEqual(Expected.ToArray(), Reopened.ToArray(), "Hole-directory stream mismatch after reopen.")

                        Dim Before = Reopened.GetStructure()

                        AssertTrue(
                            Before.Regions.Any(Function(region) region.RegionType = ChunkedStreamStructure.RegionTypes.HoleDirectoryPage),
                            "Expected persisted hole-directory metadata pages.")

                        For ChunkIndex = 128 To 159
                            Dim Data = Helpers.GenerateRandomData(Options.ChunkSize, 7000 + ChunkIndex)
                            Reopened.Write(CLng(ChunkIndex) * CLng(Options.ChunkSize), Data)
                            Expected.AddRange(Data)
                        Next

                        AssertBytesEqual(Expected.ToArray(), Reopened.ToArray(), "Hole-directory reuse corrupted logical data.")
                        Reopened.Validate()
                    End Using
                End Using
            End Sub

            ' ================================================================================
            ' Header helpers
            ' ================================================================================

            Private Shared Sub CorruptHeaderCopy(Stream As MemoryStream, HeaderCopyIndex As Integer)
                If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
                If HeaderCopyIndex < 0 OrElse HeaderCopyIndex > 1 Then Throw New ArgumentOutOfRangeException(NameOf(HeaderCopyIndex))

                Dim HeaderSize = GetPrivateConst(Of Integer)("HeaderSize")
                Dim CorruptOffset = CLng(HeaderCopyIndex * HeaderSize) + 32L

                Stream.Position = CorruptOffset
                Dim Original = Stream.ReadByte()
                If Original < 0 Then Throw New InvalidDataException("Could not read header byte to corrupt.")

                Stream.Position = CorruptOffset
                Stream.WriteByte(CByte(Original Xor &HFF))
            End Sub

            Private Shared Function GetHeaderSequence(Stream As MemoryStream, HeaderCopyIndex As Integer) As Long
                Dim HeaderSize = GetPrivateConst(Of Integer)("HeaderSize")
                Dim HeaderSequenceOffset = GetPrivateConst(Of Integer)("HeaderSequenceOffset")

                Dim Buffer(7) As Byte
                Stream.Position = CLng(HeaderCopyIndex * HeaderSize) + HeaderSequenceOffset
                Stream.Read(Buffer, 0, Buffer.Length)
                Return BitConverter.ToInt64(Buffer, 0)
            End Function

            Private Shared Function GetOlderHeaderCopyIndex(Stream As MemoryStream) As Integer
                Dim Seq0 = GetHeaderSequence(Stream, 0)
                Dim Seq1 = GetHeaderSequence(Stream, 1)

                If Seq0 <= Seq1 Then Return 0
                Return 1
            End Function

            Private Shared Function GetNewerHeaderCopyIndex(Stream As MemoryStream) As Integer
                Dim Seq0 = GetHeaderSequence(Stream, 0)
                Dim Seq1 = GetHeaderSequence(Stream, 1)

                If Seq0 >= Seq1 Then Return 0
                Return 1
            End Function

            Private Shared Function GetPrivateConst(Of T)(Name As String) As T

                Dim Field =
                GetType(ChunkedStream).GetField(
                    Name,
                    BindingFlags.NonPublic Or
                    BindingFlags.Public Or
                    BindingFlags.Static)

                AssertTrue(
                Field IsNot Nothing,
                $"Expected private constant '{Name}' was not found.")

                Return DirectCast(Field.GetRawConstantValue(), T)

            End Function

            ' ================================================================================
            ' Physical record recovery helpers
            ' ================================================================================

            Private Shared Sub CopyPhysicalBytes(Stream As MemoryStream, SourceOffset As Long, DestinationOffset As Long, Length As Integer)
                If Length <= 0 Then Return

                Dim Buffer(Length - 1) As Byte

                Stream.Position = SourceOffset
                Dim ReadBytes = Stream.Read(Buffer, 0, Buffer.Length)
                If ReadBytes <> Buffer.Length Then Throw New EndOfStreamException("Could not read complete physical record.")

                Stream.Position = DestinationOffset
                Stream.Write(Buffer, 0, Buffer.Length)
            End Sub

            Private Shared Sub InvokePrivateSub(Target As Object, MethodName As String, ParamArray Arguments() As Object)
                If Target Is Nothing Then Throw New ArgumentNullException(NameOf(Target))

                Dim Method =
                    Target.GetType().GetMethod(
                        MethodName,
                        BindingFlags.Instance Or BindingFlags.NonPublic)

                AssertTrue(Method IsNot Nothing, $"Private method not found: {MethodName}.")

                Try
                    Method.Invoke(Target, Arguments)
                Catch ex As TargetInvocationException
                    If ex.InnerException IsNot Nothing Then Throw ex.InnerException
                    Throw
                End Try
            End Sub

            ' ================================================================================
            ' Fuzz helpers
            ' ================================================================================

            Private NotInheritable Class FuzzCheckpoint
                Public Property Checkpoint As ChunkedStream.ChunkedStreamCheckpoint
                Public Property ModelSnapshot As Byte()
            End Class

            Private Shared Sub FuzzWrite(Cs As ChunkedStream, ByRef Model As List(Of Byte), Rng As Random)
                Dim Offset = Rng.Next(0, Math.Max(1, Model.Count + 2048))
                Dim Length = Rng.Next(1, 2049)
                Dim Data = MakeRandomBytes(Rng, Length)

                Cs.Write(Offset, Data)

                EnsureModelLength(Model, Offset + Length)

                For i = 0 To Length - 1
                    Model(Offset + i) = Data(i)
                Next
            End Sub

            Private Shared Sub FuzzInsert(Cs As ChunkedStream, ByRef Model As List(Of Byte), Rng As Random)
                Dim Offset = Rng.Next(0, Model.Count + 1)
                Dim Length = Rng.Next(1, 1025)
                Dim Data = MakeRandomBytes(Rng, Length)

                Cs.Insert(Offset, Data)
                Model.InsertRange(Offset, Data)
            End Sub

            Private Shared Sub FuzzRemove(Cs As ChunkedStream, ByRef Model As List(Of Byte), Rng As Random)
                If Model.Count = 0 Then Return

                Dim Offset = Rng.Next(0, Model.Count)
                Dim Length = Rng.Next(1, Math.Min(1024, Model.Count - Offset) + 1)

                Cs.Remove(Offset, Length)
                Model.RemoveRange(Offset, Length)
            End Sub

            Private Shared Sub FuzzSetLength(Cs As ChunkedStream, ByRef Model As List(Of Byte), Rng As Random)
                Dim NewLength = Rng.Next(0, Math.Max(1, Model.Count + 2048))

                Cs.SetLength(NewLength)
                EnsureModelLength(Model, NewLength)

                If Model.Count > NewLength Then
                    Model.RemoveRange(NewLength, Model.Count - NewLength)
                End If
            End Sub

            Private Shared Sub FuzzClone(Cs As ChunkedStream, ByRef Model As List(Of Byte), Rng As Random)
                If Model.Count = 0 Then Return

                Dim SourceOffset = Rng.Next(0, Model.Count)
                Dim Length = Rng.Next(1, Math.Min(1024, Model.Count - SourceOffset) + 1)
                Dim TargetOffset = Rng.Next(0, Model.Count + 1)

                Dim CloneData = Model.GetRange(SourceOffset, Length).ToArray()

                Cs.Clone(SourceOffset, Length, TargetOffset)

                If TargetOffset > Model.Count Then
                    EnsureModelLength(Model, TargetOffset)
                End If

                Model.InsertRange(TargetOffset, CloneData)
            End Sub

            Private Shared Sub FuzzApplyOptions(Cs As ChunkedStream, Rng As Random)
                Select Case Rng.Next(0, 4)
                    Case 0
                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                    Case 1
                        Cs.Options.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                        Cs.Options.CompressionRatioThreshold = If(Rng.Next(0, 2) = 0, 0.25R, 0.95R)
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Compression)

                    Case 2
                        Cs.Options.StoreSparseChunks = False
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Sparseness)

                    Case 3
                        Cs.Options.StoreSparseChunks = True
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Sparseness)
                End Select
            End Sub

            Private Shared Sub FuzzDefrag(Cs As ChunkedStream, Rng As Random)
                Select Case Rng.Next(0, 3)
                    Case 0
                        Cs.Defragment(ChunkedStream.DefragTypes.Move)

                    Case 1
                        Cs.Defragment(ChunkedStream.DefragTypes.Sequence)

                    Case 2
                        Cs.Defragment(ChunkedStream.DefragTypes.Rebuild)
                End Select
            End Sub

            Private Shared Sub EnsureModelLength(Model As List(Of Byte), Length As Integer)
                While Model.Count < Length
                    Model.Add(0)
                End While
            End Sub

            Private Shared Function MakeRandomBytes(Rng As Random, Length As Integer) As Byte()
                Dim Result(Length - 1) As Byte
                Rng.NextBytes(Result)
                Return Result
            End Function

        End Class
    End Class
End Namespace