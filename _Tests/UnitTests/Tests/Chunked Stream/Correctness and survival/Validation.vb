Imports System.IO
Imports System.Security.Cryptography
Imports i00.Streams

Namespace Tests

    Partial Class CorrectnessAndSurvival

        Public NotInheritable Class Validation

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Healthy stream validation
            ' ================================================================================

            ''' <summary>
            ''' Verifies that validation succeeds for a healthy stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForHealthyStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1001))

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that validation succeeds after reopening an existing stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesAfterReopen()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1002))

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encrypted streams validate successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForEncryptedStream()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(
                            Helpers.MakeKey(123))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1003))

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that compressed streams validate successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForCompressedStream()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GeneratePartiallyCompressibleData(
                                0.8R,
                                Cs.options.ChunkSize,
                                8,
                                1004))

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that sparse streams validate successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForSparseStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(
                            Cs.options.ChunkSize * 16)

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shared physical records created through cloning validate successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesForSharedPhysicalRecords()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 4,
                                1005)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Cs.options.ChunkSize,
                            Cs.options.ChunkSize * 2,
                            Cs.options.ChunkSize)

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that defragmented streams validate successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesAfterDefragmentation()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 8,
                                1006)

                        Cs.Write(0, Data)

                        For ChunkIndex = 0 To 7 Step 2

                            Cs.Write(
                                ChunkIndex * Cs.options.ChunkSize,
                                GeneratePatternData(
                                    Cs.options.ChunkSize,
                                    2000 + ChunkIndex))

                        Next

                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Sequence)

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that ApplyOptions migrations produce a stream that validates successfully.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidatePassesAfterApplyOptionsMigration()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GeneratePartiallyCompressibleData(
                                0.7R,
                                Cs.options.ChunkSize,
                                8,
                                1007))

                        Cs.Options.CompressionMethod =
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate

                        Cs.Options.CompressionRatioThreshold = 0.95R

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression)

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Corruption detection
            ' ================================================================================

            ''' <summary>
            ''' Verifies that physically corrupting a chunk record causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsAfterCorruption()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                2001))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk =
                            Struct.Chunks.
                                   First(Function(x) x.PhysicalOffset.HasValue)

                        Ms.Position = Chunk.PhysicalOffset.Value + 50

                        Dim OriginalByte =
                            Ms.ReadByte()

                        Ms.Position = Chunk.PhysicalOffset.Value + 50
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        AssertThrows(Of Exception)(
                            Sub()
                                Cs.Validate().ThrowIfErrors()
                            End Sub,
                            "Validation should fail for corrupted chunk data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting a chunk record identifier causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsWhenPhysicalRecordIdCorrupted()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                2002))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk =
                            Struct.Chunks.
                                   First(Function(x) x.PhysicalOffset.HasValue)

                        Ms.Position = Chunk.PhysicalOffset.Value

                        Dim Original(7) As Byte
                        Ms.Read(Original, 0, Original.Length)

                        Ms.Position = Chunk.PhysicalOffset.Value
                        Ms.WriteByte(CByte(Original(0) Xor &HFF))

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub()
                                Cs.Validate().ThrowIfErrors()
                            End Sub,
                            "Validation should fail when the stored physical-record id is corrupted.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting a record MAC causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsWhenChunkMacCorrupted()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                2003))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk =
                            Struct.Chunks.
                                   First(Function(x) x.PhysicalOffset.HasValue AndAlso
                                                     x.PhysicalLength.HasValue)

                        Dim MacOffset =
                            Chunk.PhysicalOffset.Value +
                            Chunk.PhysicalLength.Value -
                            ChunkedStream.MacSize

                        Ms.Position = MacOffset

                        Dim OriginalByte =
                            Ms.ReadByte()

                        Ms.Position = MacOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable),
                            "Validation should report an unreadable physical record when the chunk MAC is corrupted.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub()
                                Report.ThrowIfErrors()
                            End Sub,
                            "Validation should fail when the chunk MAC is corrupted.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Progress reporting
            ' ================================================================================

            ''' <summary>
            ''' Verifies that validation reports progress while validating live records.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateReportsProgress()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 16,
                                3001))

                        Dim CallbackCount As Integer = 0

                        Cs.Validate(
                            Sub(ProcessedUnits,
                                TotalUnits,
                                UnitType,
                                Token)

                                CallbackCount += 1

                            End Sub)

                        AssertTrue(
                            CallbackCount > 0,
                            "Validate did not report progress.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that the validation callback can request cancellation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateProgressCallbackCanCancelValidation()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize * 32,
                                3002))

                        Dim CallbackCount As Integer = 0

                        Cs.Validate(
                            Sub(ProcessedUnits,
                                TotalUnits,
                                UnitType,
                                Token)

                                CallbackCount += 1

                                If CallbackCount = 1 Then
                                    Token.Cancel = True
                                End If

                            End Sub)

                        AssertEqual(
                            1,
                            CallbackCount,
                            "Validation cancellation did not stop validation after the first callback.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting chunk flags causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsWhenChunkFlagsCorrupted()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.options.ChunkSize,
                                4001))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk =
                            Struct.Chunks.
                                   First(Function(x) x.PhysicalOffset.HasValue)

                        CorruptPhysicalRecordInt32Field(
                            Ms,
                            Chunk.PhysicalOffset.Value,
                            ChunkedStream.ChunkFlagsOffset,
                            &H7FFFFFFF)

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub()
                                Cs.Validate().ThrowIfErrors()
                            End Sub,
                            "Validation should fail when unsupported chunk flags are present.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that an invalid compression evaluated percent causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsWhenCompressionEvaluatedPercentCorrupted()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GeneratePartiallyCompressibleData(
                                0.8R,
                                Cs.options.ChunkSize,
                                4,
                                4002))

                        Dim Struct =
                            Cs.GetStructure()

                        Dim Chunk =
                            Struct.Chunks.
                                   First(Function(x) x.PhysicalOffset.HasValue)

                        CorruptPhysicalRecordByteField(
                            Ms,
                            Chunk.PhysicalOffset.Value,
                            ChunkedStream.ChunkCompressionEvaluatedPercentOffset,
                            255)

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub()
                                Cs.Validate().ThrowIfErrors()
                            End Sub,
                            "Validation should fail when compression evaluated percent exceeds 100.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that corrupting physical-record metadata refcounts causes validation to fail.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateFailsWhenPhysicalRecordRefCountCorrupted()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                4003)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize * 2,
                            Cs.Options.ChunkSize)

                        Dim Struct =
                            Cs.GetStructure()

                        Dim SharedRecord =
                            Struct.Chunks.
                                   GroupBy(Function(x) x.PhysicalRecordId).
                                   First(Function(x) x.Count > 1)

                        Cs.Debug_CorruptPhysicalRecordMetadataRefCount(SharedRecord.Key.Value, 12345)

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub()
                                Cs.Validate().ThrowIfErrors()
                            End Sub,
                            "Validation should fail when stored metadata refcounts do not match extent usage.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Open-time auto-repair
            ' ================================================================================

            ''' <summary>
            ''' Verifies that opening a stream whose header carries a stale index-offset hint
            ''' (past the end of the backing stream) recomputes it, records the correction in
            ''' <see cref="ChunkedStream.AutoRepairs" />, and persists the corrected value.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OpenRecomputesAStalePersistedIndexOffset()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Dim Cs = ChunkedStream.Open(Ms)
                    Expected = GenerateRandomData(Cs.Options.ChunkSize * 6, 4100)
                    Cs.Write(0, Expected)
                    Cs.Flush()
                    Cs.Debug_CorruptPersistedHeaderIndexOffset(Ms.Length + 500000)
                    Cs = Nothing ' abandon without disposing so the stale header stays on disk

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertTrue(
                            Reopened.AutoRepairs.Any(Function(repair) repair.Field = "IndexOffset"),
                            "Open should record an IndexOffset auto-repair.")

                        AssertBytesEqual(Expected, Reopened.ToArray(), "Auto-repair changed the logical data.")
                        Reopened.Validate().ThrowIfErrors()

                        ' A subsequent write still works and persists the corrected header.
                        Reopened.Write(0, GenerateZeroedData(16))
                        Overlay(Expected, GenerateZeroedData(16), 0)

                    End Using

                    Ms.Position = 0
                    Using Again = ChunkedStream.Open(Ms)
                        AssertEqual(0, Again.AutoRepairs.Count, "The corrected header should persist, so the next open is clean.")
                        AssertBytesEqual(Expected, Again.ToArray(), "Reopen after auto-repair lost data.")
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Report-driven repair
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a drifted physical-record reference count is reported and that a
            ''' non-lossy <see cref="ChunkedStream.ValidationReport.Repair" /> reconciles it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RepairReconcilesADriftedReferenceCount()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        Expected = GenerateRandomData(Cs.Options.ChunkSize * 4, 4200)
                        Cs.Write(0, Expected)

                        Dim RecordId =
                            Cs.GetStructure().Chunks.
                               First(Function(chunk) chunk.PhysicalOffset.HasValue).PhysicalRecordId.Value

                        Cs.Debug_CorruptPhysicalRecordMetadataRefCount(RecordId, 999)

                        Dim Report = Cs.Validate()

                        AssertTrue(Report.HasErrors, "A refcount mismatch should be an error.")
                        AssertTrue(
                            Report.Errors.All(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.RefCountMismatch),
                            "The only problem should be the refcount mismatch.")

                        Dim Outcome = Report.Repair()

                        AssertEqual(0L, Outcome.BytesZeroed, "Reconciling a reference count discards no data.")
                        AssertEqual(1, Outcome.Repaired.Count, "The refcount problem should have been repaired.")

                        Cs.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Cs.ToArray(), "Repair changed the logical data.")

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Reopen after repair lost data.")
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that an unreadable chunk is left alone by a non-lossy repair, and that
            ''' <see cref="ChunkedStream.RepairScope.IncludeDataLoss" /> replaces its logical
            ''' range with zeros while leaving every other range intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RepairZeroFillsAnUnreadableChunkOnlyWhenDataLossIsAllowed()

                Using Ms As New MemoryStream()

                    Dim ChunkSize As Integer
                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        ChunkSize = Cs.Options.ChunkSize
                        Expected = GenerateRandomData(ChunkSize * 4, 4300)
                        Cs.Write(0, Expected)

                        Dim Chunk =
                            Cs.GetStructure().Chunks.
                               First(Function(item) item.PhysicalOffset.HasValue AndAlso item.LogicalOffset = ChunkSize)

                        Dim MacOffset = Chunk.PhysicalOffset.Value + Chunk.PhysicalLength.Value - ChunkedStream.MacSize
                        Ms.Position = MacOffset
                        Dim OriginalByte = Ms.ReadByte()
                        Ms.Position = MacOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        Dim Report = Cs.Validate()

                        Dim Problem =
                            Report.Errors.Single(Function(item) item.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable)

                        AssertTrue(Problem.RepairIsLossy, "Repairing an unreadable chunk discards data.")
                        AssertEqual(CLng(ChunkSize), Problem.DataLossBytes, "The whole chunk would be lost.")

                        Dim NonLossy = Report.Repair(ChunkedStream.RepairScope.NonLossy)
                        AssertEqual(0, NonLossy.Repaired.Count, "A non-lossy repair must not touch a lossy problem.")
                        AssertEqual(1, NonLossy.Skipped.Count, "The lossy problem should be reported as skipped.")

                        Dim Lossy = Cs.Validate().Repair(ChunkedStream.RepairScope.IncludeDataLoss)
                        AssertEqual(CLng(ChunkSize), Lossy.BytesZeroed, "The unreadable chunk's range should be zeroed.")

                        Cs.Validate().ThrowIfErrors()

                        Dim ExpectedAfter = CType(Expected.Clone(), Byte())
                        Array.Clear(ExpectedAfter, ChunkSize, ChunkSize)
                        AssertBytesEqual(ExpectedAfter, Cs.ToArray(), "Repair should have zeroed only the unreadable chunk.")

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies the predicate overload: with both a non-lossy and a lossy problem present,
            ''' <c>Repair(Function(p) p.DataLossBytes = 0)</c> fixes only the non-lossy one, and a
            ''' second unrestricted pass then handles the rest.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RepairSelectorChoosesWhichProblemsToFix()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim ChunkSize = Cs.Options.ChunkSize
                        Cs.Write(0, GenerateRandomData(ChunkSize * 4, 4400))

                        Dim Chunks = Cs.GetStructure().Chunks.Where(Function(chunk) chunk.PhysicalOffset.HasValue).ToList()

                        ' problem 1 (non-lossy): drift a refcount.  problem 2 (lossy): corrupt a MAC.
                        Cs.Debug_CorruptPhysicalRecordMetadataRefCount(Chunks(0).PhysicalRecordId.Value, 42)

                        Dim MacOffset = Chunks(2).PhysicalOffset.Value + Chunks(2).PhysicalLength.Value - ChunkedStream.MacSize
                        Ms.Position = MacOffset
                        Dim OriginalByte = Ms.ReadByte()
                        Ms.Position = MacOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        Dim Report = Cs.Validate()
                        AssertEqual(2, Report.Errors.Count, "Both problems should be reported.")

                        Dim NonLossyOnly = Report.Repair(Function(problem) problem.DataLossBytes = 0)
                        AssertEqual(1, NonLossyOnly.Repaired.Count, "Only the non-lossy problem should be repaired.")
                        AssertEqual(1, NonLossyOnly.Skipped.Count, "The lossy problem should be skipped.")
                        AssertEqual(0L, NonLossyOnly.BytesZeroed, "No data should have been zeroed yet.")

                        Dim Remaining = Cs.Validate()
                        AssertEqual(1, Remaining.Errors.Count, "The lossy problem should remain.")
                        AssertEqual(ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable, Remaining.Errors(0).Kind, "...")

                        Dim Rest = Remaining.Repair(Function(problem) True)
                        AssertEqual(CLng(ChunkSize), Rest.BytesZeroed, "The remaining lossy problem should now be zeroed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace