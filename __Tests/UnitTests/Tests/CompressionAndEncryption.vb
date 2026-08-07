Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests
    Partial Class StreamChunked

        Public NotInheritable Class CompressionAndEncryption

            ''' <summary>
            ''' Verifies that the PlaintextAllZero flag is preserved on physically stored compressed and encrypted zero chunks.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkFlagsZeroChunkWithCompressionAndEncryption()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .StoreSparseChunks = True,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        .CompressionMinimumSavingsPercent = 1,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(77))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, MakeBuffer(ChunkedStream.ChunkSize))

                        Dim Struct = Cs.GetStructure()
                        Dim Chunk = Struct.Chunks.First()

                        AssertTrue(Chunk.IsAllocated, "Expected zero chunk to be physically allocated.")
                        AssertTrue(Chunk.IsCompressed, "Expected zero chunk to be compressed.")
                        AssertTrue(Chunk.IsEncrypted, "Expected zero chunk to be encrypted.")
                        AssertTrue(Chunk.IsPlaintextAllZero, "Compressed/encrypted zero chunk did not expose PlaintextAllZero.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that every compression method except None round-trips correctly and compresses ideal input below 10% of the original size.
            ''' Future compression enum values are automatically included.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionRoundTrip()

                For Each CompressionMethod In GetCompressionMethodsToTest()

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                                .CompressionMethod = CompressionMethod,
                                .CompressionMinimumSavingsPercent = 1
                            }

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Dim Data = MakeRepeatingPattern(200000, 8)

                            Cs.Write(0, Data)

                            Dim Result = MakeBuffer(Data.Length)
                            Dim BytesRead = Cs.Read(0, Result)

                            If BytesRead <> Data.Length Then
                                Throw New Exception($"Unexpected byte count for compression method {CompressionMethod}. Expected={Data.Length}, Actual={BytesRead}.")
                            End If

                            AssertBytesEqual(Data, Result, $"Compressed round-trip failed for compression method {CompressionMethod}.")

                            Dim Struct = Cs.GetStructure()
                            Dim CompressionPercent = Struct.PayloadCompressionRatio

                            If CompressionPercent >= 0.1R Then
                                Throw New Exception($"Compression method {CompressionMethod} did not compress ideal input below 10%. Actual={CompressionPercent:P2}.")
                            End If

                            If Not Struct.Chunks.Any(Function(chunk) chunk.IsAllocated AndAlso chunk.IsCompressed) Then
                                Throw New Exception($"Compression method {CompressionMethod} did not produce any compressed chunks.")
                            End If

                        End Using

                    End Using

                Next

            End Sub

            ''' <summary>
            ''' Verifies that encrypted data round-trips correctly for multiple lengths.
            ''' </summary>
            <UnitTester.SimpleTest({1000}, True)>
            <UnitTester.SimpleTest({65536}, True)>
            <UnitTester.SimpleTest({200000}, True)>
            Public Shared Function EncryptedRoundTrip(Length As Integer) As Boolean

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(1))
                        }

                    Dim Data = MakePattern(Length, 29)

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Cs.Write(0, Data)
                    End Using

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        If BytesRead <> Data.Length Then
                            Return False
                        End If

                        Return BytesEqual(Data, Result)

                    End Using

                End Using

            End Function

            Private Shared Function GetCompressionMethodsToTest() As ChunkedStream.ChunkedStreamOptions.CompressionMethods()

                Return [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods)).
                                  Cast(Of ChunkedStream.ChunkedStreamOptions.CompressionMethods)().
                                  Where(Function(method) method <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None).
                                  ToArray()

            End Function

            ''' <summary>
            ''' Verifies that compressed data remains intact after closing and reopening the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenPreservesCompressedData()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                            .CompressionMinimumSavingsPercent = 1
                        }

                    Dim Data = MakeRepeatingPattern(250000, 5)

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Cs.Write(0, Data)
                    End Using

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        AssertEqual(Data.Length, BytesRead, "Unexpected byte count after compressed reopen.")
                        AssertBytesEqual(Data, Result, "Compressed data changed after reopen.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening a user-wrapped encrypted stream without encryption information fails.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenEncryptedWithoutKeyFails()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(2))
                        }

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Cs.Write(0, MakePattern(10000, 7))
                    End Using

                    AssertThrows(Of StreamEncryption.Streams.ChunkedStream.EncryptionMismatchException)(
                            Sub()
                                Using Cs = ChunkedStream.Open(Ms)
                                End Using
                            End Sub,
                            "Opening an encrypted stream without encryption information should fail.")

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that opening a user-wrapped encrypted stream with the wrong key fails.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReopenEncryptedWithWrongKeyFails()

                Using Ms As New MemoryStream()

                    Dim CorrectOptions As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(3))
                        }

                    Dim WrongOptions As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(4))
                        }

                    Using Cs = ChunkedStream.Open(Ms, CorrectOptions)
                        Cs.Write(0, MakePattern(10000, 11))
                    End Using

                    AssertThrows(Of StreamEncryption.Streams.ChunkedStream.EncryptionMismatchException)(
                            Sub()
                                Using Cs = ChunkedStream.Open(Ms, WrongOptions)
                                End Using
                            End Sub,
                            "Opening with the wrong encryption key should fail.")

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that disabling encryption publicly wraps the file master key and allows reopening without encryption information.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DisableEncryptionAllowsPublicReopen()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(5))
                        }

                    Dim Data = MakePattern(120000, 13)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Data)
                        Cs.Options.EncryptionInfo = Nothing

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        AssertEqual(Data.Length, BytesRead, "Unexpected byte count after public reopen.")
                        AssertBytesEqual(Data, Result, "Encrypted data was not readable after public wrapping.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that enabling encryption after writing plaintext makes future chunks encrypted without breaking existing chunks.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EnableEncryptionAfterPlainWritesKeepsBothReadable()

                Using Ms As New MemoryStream()

                    Dim PlainData = MakePattern(ChunkedStream.ChunkSize, 19)
                    Dim EncryptedData = MakePattern(ChunkedStream.ChunkSize, 23)

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, PlainData)
                        Cs.Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(6))
                        Cs.Write(ChunkedStream.ChunkSize, EncryptedData)

                        Dim PlainResult = MakeBuffer(PlainData.Length)
                        Dim EncryptedResult = MakeBuffer(EncryptedData.Length)

                        Cs.Read(0, PlainResult)
                        Cs.Read(ChunkedStream.ChunkSize, EncryptedResult)

                        AssertBytesEqual(PlainData, PlainResult, "Plain data changed after enabling encryption.")
                        AssertBytesEqual(EncryptedData, EncryptedResult, "Encrypted data did not round-trip after enabling encryption.")

                        Dim Struct = Cs.GetStructure()

                        AssertTrue(Struct.EncryptedChunkCount > 0, "Expected at least one encrypted chunk.")
                        AssertTrue(Struct.UnencryptedChunkCount > 0, "Expected at least one unencrypted chunk.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that compression and encryption can be used together and survive reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionAndEncryptionTogetherRoundTrip()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                            .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.GZip,
                            .CompressionMinimumSavingsPercent = 1,
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7))
                        }

                    Dim Data = MakeRepeatingPattern(300000, 12)

                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Cs.Write(0, Data)
                    End Using

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Result = MakeBuffer(Data.Length)
                        Dim BytesRead = Cs.Read(0, Result)

                        AssertEqual(Data.Length, BytesRead, "Unexpected compressed/encrypted byte count.")
                        AssertBytesEqual(Data, Result, "Compressed and encrypted data changed after reopen.")

                    End Using

                End Using

            End Sub

        End Class

    End Class
End Namespace
