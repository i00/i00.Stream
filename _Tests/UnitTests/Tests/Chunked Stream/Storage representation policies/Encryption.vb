Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class Encryption

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Basic encryption behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that encrypted data round-trips correctly for representative lengths.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptedRoundTrip()

                Dim ChunkSize = ChunkedStream.DefaultChunkSize

                Dim Lengths =
                    New Integer() {
                        0,
                        1,
                        1000,
                        ChunkSize,
                        ChunkSize * 3
                    }

                For Each Length In Lengths

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1001 + Length)),
                            .ChunkSize = ChunkSize
                        }

                        Dim Expected =
                            GenerateRandomData(
                                Length,
                                2000 + Length)

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Write(0, Expected)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Encrypted in-memory round-trip failed. Length={Length}")

                            Cs.Validate().ThrowIfErrors()

                            Dim Struct =
                                Cs.GetStructure()

                            If Length > 0 Then
                                AssertTrue(
                                    Struct.EncryptedChunkCount > 0,
                                    $"Expected encrypted chunks. Length={Length}")
                            End If

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that encrypted streams can be reopened with the correct key.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenEncryptedWithCorrectKeySucceeds()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1101))
                    }

                    Dim Expected =
                        GenerateRandomData(
                            Options.ChunkSize * 4,
                            1102)

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Cs.Write(0, Expected)
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Encrypted data did not survive reopen with the correct key.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening an encrypted stream without encryption information fails.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenEncryptedWithoutKeyFails()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1201))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                1202))

                    End Using

                    AssertThrows(Of ChunkedStream.EncryptionMismatchException)(
                        Sub()
                            Using Reopened = ChunkedStream.Open(Ms)
                            End Using
                        End Sub,
                        "Opening an encrypted stream without encryption information should fail.")

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening an encrypted stream with the wrong key fails.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenEncryptedWithWrongKeyFails()

                Using Ms As New MemoryStream()

                    Dim CorrectOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1301))
                    }

                    Dim WrongOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1302))
                    }

                    Using Cs = ChunkedStream.Open(Ms, CorrectOptions)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                1303))

                    End Using

                    AssertThrows(Of ChunkedStream.EncryptionMismatchException)(
                        Sub()
                            Using Reopened = ChunkedStream.Open(Ms, WrongOptions)
                            End Using
                        End Sub,
                        "Opening an encrypted stream with the wrong key should fail.")

                End Using

            End Sub

            ' ================================================================================
            ' Key derivation / salt behaviour
            ' ================================================================================

            ''' <summary>
            ''' Documents current behaviour: a separately constructed EncryptionInfo built from the
            ''' same passphrase, with no salt supplied, unlocks a file written by another
            ''' EncryptionInfo instance built the same way. Two callers who both use the no-salt
            ''' overload with the same passphrase get interchangeable keys. Flagging this so it's a
            ''' conscious choice - update or remove this test if per-file salting becomes mandatory.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SamePassphraseWithoutSaltProducesInterchangeableKeysAcrossInstances()

                Using Ms As New MemoryStream()

                    Dim WriteOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("shared passphrase")
                    }

                    Dim Expected =
                        GenerateRandomData(
                            WriteOptions.ChunkSize,
                            6201)

                    Using Cs = ChunkedStream.Open(Ms, WriteOptions)
                        Cs.Write(0, Expected)
                    End Using

                    Dim ReopenOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo("shared passphrase")
                    }

                    Using Reopened = ChunkedStream.Open(Ms, ReopenOptions)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Expected a second EncryptionInfo built from the same no-salt passphrase to also unlock the file.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Encryption transitions
            ' ================================================================================

            ''' <summary>
            ''' Verifies that enabling encryption after plaintext writes encrypts future
            ''' chunks without breaking existing plaintext chunks.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EnableEncryptionAfterPlainWritesKeepsBothReadable()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim PlainData =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                2001)

                        Dim EncryptedData =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                2002)

                        Cs.Write(0, PlainData)

                        Cs.Options.EncryptionInfo =
                            New ChunkedStream.EncryptionInfo(MakeKey(2003))

                        Cs.Write(
                            Cs.Options.ChunkSize,
                            EncryptedData)

                        Dim Expected =
                            CombineArrays(
                                PlainData,
                                EncryptedData)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Enabling encryption after plaintext writes corrupted logical data.")

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.EncryptedChunkCount > 0,
                            "Expected at least one encrypted chunk after enabling encryption.")

                        AssertTrue(
                            Struct.UnencryptedChunkCount > 0,
                            "Expected at least one unencrypted chunk after enabling encryption.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that disabling encryption publicly wraps the file master key and
            ''' allows reopening without encryption information while encrypted chunks remain.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DisableEncryptionAllowsPublicReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2101))
                    }

                    Dim Expected =
                        GenerateRandomData(
                            Options.ChunkSize * 4,
                            2102)

                    Using Cs = ChunkedStream.Open(Ms, Options)


                        Cs.Write(0, Expected)

                        Cs.Options.EncryptionInfo = Nothing

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Disabling encryption configuration corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "Publicly wrapped encrypted data was not readable after reopen.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encryption and compression can be combined without changing
            ''' logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptionAndCompressionTogetherRoundTrip()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionRatioThreshold = 0.95R,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2201))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Expected =
                            GeneratePartiallyCompressibleData(
                                0.8R,
                                Cs.Options.ChunkSize,
                                8,
                                2202)

                        Cs.Write(0, Expected)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Compressed encrypted stream did not round-trip.")

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.EncryptedChunkCount > 0,
                            "Expected encrypted chunks.")

                        AssertTrue(
                            Struct.CompressedChunkCount > 0,
                            "Expected compressed chunks.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing and reading a large stream with per-chunk crypto on the
            ''' worker pool (Options.MaxCryptoParallelism > 1) produces exactly the same logical
            ''' bytes as the fully serial path - including a sparse chunk in the middle and a
            ''' reopen - and that a corrupt chunk still surfaces its CryptographicException
            ''' rather than an AggregateException.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ParallelChunkCryptoMatchesSerialAndSurfacesCorruption()

                Dim Expected As Byte()

                Using SerialMs As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2301)),
                        .MaxCryptoParallelism = 1
                    }

                    Using Cs = ChunkedStream.Open(SerialMs, Options)
                        Expected = GeneratePartiallyCompressibleData(0.7R, Cs.Options.ChunkSize, 40, 2302)
                        Array.Clear(Expected, Cs.Options.ChunkSize * 15, Cs.Options.ChunkSize) ' a sparse chunk mid-stream
                        Cs.Write(0, Expected)
                        AssertBytesEqual(Expected, Cs.ToArray(), "Serial read did not round-trip.")
                    End Using
                End Using

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2301)),
                        .MaxCryptoParallelism = 8
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)

                        AssertBytesEqual(Expected, Cs.ToArray(), "Parallel write + read did not match the serial result.")
                        Cs.Validate().ThrowIfErrors()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2301))})
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Reopen after a parallel write lost data.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim RangeStart = Cs.Options.ChunkSize \ 2
                        Dim RangeLength = Expected.Length - Cs.Options.ChunkSize
                        Dim ExpectedRange(RangeLength - 1) As Byte
                        Array.Copy(Expected, RangeStart, ExpectedRange, 0, RangeLength)
                        AssertBytesEqual(ExpectedRange, Cs.ToArray(RangeStart, RangeLength), "Parallel ranged read did not match.")

                        Cs.Validate().ThrowIfErrors()

                        ' Corrupt one chunk's MAC and confirm the exception is not wrapped.
                        Dim Chunk =
                            Cs.GetStructure().Chunks.
                               First(Function(item) item.PhysicalOffset.HasValue AndAlso item.LogicalOffset = Cs.Options.ChunkSize * 10)

                        Dim MacOffset = Chunk.PhysicalOffset.Value + Chunk.PhysicalLength.Value - ChunkedStream.MacSize
                        Ms.Position = MacOffset
                        Dim OriginalByte = Ms.ReadByte()
                        Ms.Position = MacOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        AssertThrows(Of Security.Cryptography.CryptographicException)(
                            Sub() Cs.ToArray(),
                            "A corrupt chunk should surface a CryptographicException from the parallel read.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace