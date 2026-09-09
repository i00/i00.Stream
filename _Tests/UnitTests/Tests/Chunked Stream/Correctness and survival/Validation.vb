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

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable),
                            "Corrupted chunk data should be reported as an unreadable physical record.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub() Report.ThrowIfErrors(),
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

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable),
                            "A corrupted stored record id should be reported as an unreadable physical record.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub() Report.ThrowIfErrors(),
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

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable),
                            "Unsupported chunk flags should be reported as an unreadable physical record.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub() Report.ThrowIfErrors(),
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

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.PhysicalRecordUnreadable),
                            "An out-of-range compression-evaluated percent should be reported as an unreadable physical record.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub() Report.ThrowIfErrors(),
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

                        Dim Report = Cs.Validate()

                        AssertTrue(
                            Report.Errors.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.RefCountMismatch AndAlso
                                                                problem.PhysicalRecordId.GetValueOrDefault() = SharedRecord.Key.Value),
                            "The drifted reference count should be reported against its record.")

                        AssertThrows(Of ChunkedStream.ValidationException)(
                            Sub() Report.ThrowIfErrors(),
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

            ' ================================================================================
            ' Cache-coherence invariants (D6)
            ' ================================================================================

            ''' <summary>
            ''' Verifies that a cached physical-data end that has silently drifted (no producer
            ''' flagged it stale) is reported as a <see cref="ChunkedStream.ValidationProblemKind.CacheInconsistency" />
            ''' and reconciled by a non-lossy repair.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateReportsAndRepairsADriftedPhysicalDataEndCache()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        Expected = GenerateRandomData(Cs.Options.ChunkSize * 6, 4400)
                        Cs.Write(0, Expected)

                        Cs.Debug_CorruptCachedPhysicalDataEnd(Ms.Length + 4_000_000)

                        Dim Report = Cs.Validate()

                        Dim Problem =
                            Report.Warnings.Single(Function(item) item.Kind = ChunkedStream.ValidationProblemKind.CacheInconsistency)
                        AssertTrue(Problem.CanRepair AndAlso Problem.RepairIsLossy = False, "A cache drift is a non-lossy repair.")

                        Dim Outcome = Report.Repair()
                        AssertEqual(1, Outcome.Repaired.Count, "The cache problem should have been repaired.")
                        AssertEqual(0L, Outcome.BytesZeroed, "Reconciling a cache discards no data.")

                        Cs.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Cs.ToArray(), "Repair changed the logical data.")

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that a next-anchor-id allocator that has fallen behind an id already in
            ''' use is reported and repaired, and that anchor creation is safe afterwards.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateReportsAndRepairsAStaleNextAnchorId()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 3, 4410))
                        Dim ExistingAnchor = Cs.CreateAnchor(Cs.Options.ChunkSize).AnchorId

                        Cs.Debug_CorruptNextAnchorId(1)

                        Dim Report = Cs.Validate()
                        AssertTrue(
                            Report.Warnings.Any(Function(problem) problem.Kind = ChunkedStream.ValidationProblemKind.CacheInconsistency),
                            "A stale next-anchor-id should be a cache-inconsistency warning.")

                        Report.Repair()

                        Cs.Validate().ThrowIfErrors()

                        Dim NewAnchor = Cs.CreateAnchor(Cs.Options.ChunkSize * 2).AnchorId
                        AssertTrue(NewAnchor <> ExistingAnchor, "A repaired allocator must not re-issue an id in use.")
                        Cs.Validate().ThrowIfErrors()

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' Verifies the drift producer is fixed: removing the trailing chunk inside a
            ''' checkpoint (where the reclaim, and its recompute, are held until commit) flags the
            ''' cached physical-data end stale rather than leaving it silently wrong, and the next
            ''' read recomputes it before any consumer sees the stale value.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemovingTheTrailingChunkInACheckpointFlagsThePhysicalDataEndStale()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim ChunkSize = Cs.Options.ChunkSize
                        Cs.Write(0, GenerateRandomData(ChunkSize * 8, 4420))

                        Dim EndBefore = Cs.Debug_GetCachedPhysicalDataEnd()

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Cs.Remove(ChunkSize * 7, ChunkSize)

                            AssertTrue(Cs.Debug_PhysicalDataEndIsStale(), "Removing the trailing record should flag the cached end stale.")
                            AssertEqual(EndBefore, Cs.Debug_GetCachedPhysicalDataEnd(), "The reclaim (and its recompute) is held until commit, so the raw value is unchanged for now.")

                            Dim LiveEnd = Cs.GetStructure().LiveDataEndOffset

                            AssertTrue(Cs.Debug_PhysicalDataEndIsStale() = False, "Reading the end should have recomputed it.")
                            AssertTrue(Cs.Debug_GetCachedPhysicalDataEnd() < EndBefore, "The recomputed end should be lower.")
                            AssertEqual(LiveEnd, Cs.Debug_GetCachedPhysicalDataEnd(), "GetStructure and the cache should agree.")

                            Checkpoint.Commit()

                        End Using

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Memory footprint
            ' ================================================================================

            ''' <summary>
            ''' Verifies that validation reads physical records one at a time as it inspects
            ''' them, rather than buffering every live record into memory up front - the latter
            ''' put the whole archive in RAM for a large stream. The progress callback fires
            ''' once per record, so by the first callback only the first record can have been
            ''' read, and reads must keep arriving as later callbacks fire.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ValidateReadsPhysicalRecordsOneAtATimeNotAllUpFront()

                Const ChunkCount As Integer = 12

                Using Backing As New ReadCountingStream()

                    Using Cs = ChunkedStream.Open(Backing)

                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * ChunkCount, 4500))

                        Dim BaselineReads = Backing.ReadAtCount
                        Dim ReadsAtFirstCallback As Integer = -1
                        Dim CallbackCount As Integer = 0

                        Cs.Validate(
                            Sub(ProcessedUnits, TotalUnits, UnitType, Token)
                                CallbackCount += 1
                                If CallbackCount = 1 Then ReadsAtFirstCallback = Backing.ReadAtCount - BaselineReads
                            End Sub)

                        Dim TotalValidateReads = Backing.ReadAtCount - BaselineReads

                        AssertTrue(
                            CallbackCount >= ChunkCount,
                            "Validation should report progress once per physical record.")

                        AssertTrue(
                            ReadsAtFirstCallback >= 1,
                            "The first record's bytes should have been read before its progress callback.")

                        AssertTrue(
                            ReadsAtFirstCallback <= 2,
                            $"Validation read {ReadsAtFirstCallback} records before its first progress callback - it is buffering the whole archive up front.")

                        AssertTrue(
                            TotalValidateReads > ReadsAtFirstCallback,
                            "Physical records should still be read as validation reports progress, not all before it starts.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' A seekable in-memory backing store that counts positioned reads, so a test can
            ''' observe when validation reads physical-record bytes.
            ''' </summary>
            Private NotInheritable Class ReadCountingStream
                Inherits Stream
                Implements IPositionedStream

                Private ReadOnly _Inner As New MemoryStream()
                Private _ReadAtCount As Integer

                Public ReadOnly Property ReadAtCount As Integer
                    Get
                        Return _ReadAtCount
                    End Get
                End Property

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return PositionedIoCapabilities.None
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long,
                                       Buffer As Byte(),
                                       BufferOffset As Integer,
                                       Count As Integer) As Integer Implements IPositionedStream.ReadAt

                    _ReadAtCount += 1
                    If PhysicalOffset >= _Inner.Length Then Return 0
                    _Inner.Position = PhysicalOffset
                    Return _Inner.Read(Buffer, BufferOffset, Count)

                End Function

                Public Sub WriteAt(PhysicalOffset As Long,
                                   Buffer As Byte(),
                                   BufferOffset As Integer,
                                   Count As Integer) Implements IPositionedStream.WriteAt

                    If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                    _Inner.Position = PhysicalOffset
                    _Inner.Write(Buffer, BufferOffset, Count)

                End Sub

                Public Overrides ReadOnly Property CanRead As Boolean
                    Get
                        Return True
                    End Get
                End Property

                Public Overrides ReadOnly Property CanSeek As Boolean
                    Get
                        Return True
                    End Get
                End Property

                Public Overrides ReadOnly Property CanWrite As Boolean
                    Get
                        Return True
                    End Get
                End Property

                Public Overrides ReadOnly Property Length As Long
                    Get
                        Return _Inner.Length
                    End Get
                End Property

                Public Overrides Property Position As Long
                    Get
                        Return _Inner.Position
                    End Get
                    Set
                        _Inner.Position = Value
                    End Set
                End Property

                Public Overrides Sub Flush()
                    _Inner.Flush()
                End Sub

                Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
                    Return _Inner.Read(Buffer, Offset, Count)
                End Function

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
                    _Inner.Write(Buffer, Offset, Count)
                End Sub

                Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                    Return _Inner.Seek(Offset, Origin)
                End Function

                Public Overrides Sub SetLength(Value As Long)
                    _Inner.SetLength(Value)
                End Sub

                Protected Overrides Sub Dispose(Disposing As Boolean)
                    If Disposing Then _Inner.Dispose()
                    MyBase.Dispose(Disposing)
                End Sub

            End Class

        End Class

    End Class

End Namespace