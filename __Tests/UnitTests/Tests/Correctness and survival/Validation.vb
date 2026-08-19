Imports System.IO
Imports System.Security.Cryptography
Imports StreamEncryption.Streams

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

                        Cs.Validate()

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

                        Reopened.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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
                                Cs.Validate()
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

                        AssertThrows(Of InvalidDataException)(
                            Sub()
                                Cs.Validate()
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

                        AssertThrows(Of CryptographicException)(
                            Sub()
                                Cs.Validate()
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
                            ChunkedStream.Debug_ChunkFlagsOffset,
                            &H7FFFFFFF)

                        AssertThrows(Of InvalidDataException)(
                            Sub()
                                Cs.Validate()
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
                            ChunkedStream.Debug_ChunkCompressionEvaluatedPercentOffset,
                            255)

                        AssertThrows(Of InvalidDataException)(
                            Sub()
                                Cs.Validate()
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

                        AssertThrows(Of InvalidDataException)(
                            Sub()
                                Cs.Validate()
                            End Sub,
                            "Validation should fail when stored metadata refcounts do not match extent usage.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace