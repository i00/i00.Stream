Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class StorageRepresentationPolicies

        Public NotInheritable Class PolicyMigration

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Compression migration
            ' ================================================================================

            ''' <summary>
            ''' Verifies that every compression method can be applied to existing data without
            ''' changing the logical stream contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CompressionMigrationPreservesData()

                For Each CompressionMethod As ChunkedStream.ChunkedStreamOptions.CompressionMethods In
                    [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods))

                    If CompressionMethod =
                        ChunkedStream.ChunkedStreamOptions.CompressionMethods.None Then

                        Continue For

                    End If

                    Using Ms As New MemoryStream()

                        Using Cs = ChunkedStream.Open(Ms)

                            Dim Expected =
                                GeneratePartiallyCompressibleData(
                                    0.8R,
                                    Cs.Options.ChunkSize,
                                    8,
                                    1000 + CInt(CompressionMethod))

                            Cs.Write(0, Expected)

                            Cs.Options.CompressionMethod =
                                CompressionMethod

                            Cs.Options.CompressionRatioThreshold =
                                0.95R

                            Cs.ApplyOptions(
                                ChunkedStream.ApplyOptionTypes.Compression)

                            AssertBytesEqual(
                                Expected,
                                Cs.ToArray(),
                                $"Compression migration changed logical data. CompressionMethod={CompressionMethod}")

                            Cs.Validate()

                        End Using

                    End Using

                Next

            End Sub

            ' ================================================================================
            ' Encryption migration
            ' ================================================================================

            ''' <summary>
            ''' Verifies that encryption can be applied to existing data without changing
            ''' logical contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptionMigrationPreservesData()

                Using Ms As New MemoryStream()



                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 8,
                                2001)

                        Cs.Write(0, Expected)

                        Cs.Options.EncryptionInfo =
                            New ChunkedStream.
                                EncryptionInfo(
                                    MakeKey(2002))

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Encryption)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Encryption migration changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that encrypted data can be migrated back to unencrypted storage.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptionRemovalPreservesData()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo =
                            New ChunkedStream.
                                EncryptionInfo(
                                    MakeKey(2102))
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Expected =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 8,
                                2101)

                        Cs.Write(0, Expected)

                        Cs.Options.EncryptionInfo = Nothing

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Encryption)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Encryption removal changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that once every chunk has been rewritten as unencrypted, the file
            ''' master key and its header wrapping are actually removed - not just unused -
            ''' and the stream reopens with no encryption information.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub EncryptionRemovalClearsWrappedMasterKey()

                Using Ms As New MemoryStream()

                    Dim Key = MakeKey(2152)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(Key)
                    }

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Expected = GenerateRandomData(Cs.Options.ChunkSize * 6, 2151)
                        Cs.Write(0, Expected)

                        AssertTrue(
                            Cs.GetStructure().HasWrappedFileMasterKey,
                            "The encrypted stream should have a wrapped file master key.")

                        Cs.Options.EncryptionInfo = Nothing
                        Cs.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)

                        Dim After = Cs.GetStructure()

                        AssertFalse(
                            After.HasFileMasterKey,
                            "The file master key should be gone after every chunk was rewritten unencrypted.")

                        AssertFalse(
                            After.HasWrappedFileMasterKey,
                            "The wrapped file master key should be cleared from the header.")

                        AssertEqual(
                            0,
                            After.EncryptedChunkCount,
                            "No encrypted chunks should remain.")

                        Cs.Validate()

                    End Using

                    ' Reopening with the old key must now fail - the wrapping is gone.
                    Using WronglyKeyed As New MemoryStream(Ms.ToArray())

                        AssertThrows(Of ChunkedStream.EncryptionMismatchException)(
                            Sub()
                                Using Reopened = ChunkedStream.Open(
                                    WronglyKeyed,
                                    New ChunkedStream.ChunkedStreamOptions With {
                                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(Key)
                                    })
                                End Using
                            End Sub,
                            "Opening the de-encrypted stream with the old key should be rejected.")

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            "De-encrypted data did not survive reopen without a key.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Sparse migration
            ' ================================================================================

            ''' <summary>
            ''' Verifies that sparse migration converts eligible zero ranges without
            ''' changing logical contents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SparsenessMigrationPreservesData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 8)

                        Cs.Write(0, Expected)

                        Cs.Options.StoreSparseChunks = True

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Sparseness migration changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Chunk-size migration
            ' ================================================================================

            ''' <summary>
            ''' Verifies that rebuild applies a new chunk size while preserving data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkSizeMigrationPreservesData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 16,
                                3001)

                        Cs.Write(0, Expected)

                        Dim OrigChunkSize = Cs.Options.ChunkSize

                        Dim Before = Cs.GetStructure()

                        Cs.Options.ChunkSize =
                            Cs.Options.ChunkSize \ 2

                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Rebuild)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Chunk-size migration changed logical data.")

                        Dim After = Cs.GetStructure()

                        AssertNotEqual(
                            Before.Chunks.First.PayloadLength,
                            After.Chunks.First.PayloadLength,
                            "Chunk-size migration did not change the requested chunk size.")

                        AssertEqual(
                            OrigChunkSize,
                            Before.Chunks.First.PayloadLength,
                            "Chunk-size pre-migration was not the correct chunk size.")

                        AssertEqual(
                            Cs.Options.ChunkSize,
                            After.Chunks.First.PayloadLength,
                            "Chunk-size migration did not apply the requested chunk size.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Mixed migrations
            ' ================================================================================

            ''' <summary>
            ''' Verifies that compression, encryption and sparseness migrations can be
            ''' applied sequentially without corrupting data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SequentialPolicyMigrationsPreserveData()

                Using Ms As New MemoryStream()



                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected =
                            GeneratePartiallyCompressibleData(
                                0.7R,
                                Cs.Options.ChunkSize,
                                16,
                                4001)

                        Cs.Write(0, Expected)

                        Cs.Options.CompressionMethod =
                            ChunkedStream.
                                ChunkedStreamOptions.
                                CompressionMethods.Deflate

                        Cs.Options.CompressionRatioThreshold =
                            0.95R

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression)

                        Cs.Options.EncryptionInfo =
                            New ChunkedStream.
                                EncryptionInfo(
                                    MakeKey(4002))

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Encryption)

                        Cs.Options.StoreSparseChunks = True

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Sequential policy migrations changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Existing-stream option adoption
            ' ================================================================================

            ''' <summary>
            ''' Verifies that reopening an existing stream updates Options.ChunkSize to
            ''' the persisted chunk size.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub OpenExistingStreamUpdatesOptionsChunkSize()

                Using Ms As New MemoryStream()

                    Dim CreateOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 4096
                    }

                    Using Cs = ChunkedStream.Open(Ms, CreateOptions)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                4096 * 4,
                                5001))

                    End Using

                    Dim OpenOptions As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 12345
                    }

                    Using Reopened = ChunkedStream.Open(Ms, OpenOptions)

                        AssertEqual(
                            4096,
                            Reopened.ChunkSize,
                            "Stored chunk size was not loaded.")

                        AssertEqual(
                            4096,
                            OpenOptions.ChunkSize,
                            "Options.ChunkSize was not updated from the stored value.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Format evolution scenarios
            ' ================================================================================

            ''' <summary>
            ''' Verifies that data written under mixed historical policies can be migrated
            ''' to current policies without corruption.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FormatEvolutionMixedOptionsRoundTrip()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        Expected =
                            GenerateZeroedData(
                                Cs.Options.ChunkSize * 12)

                        For ChunkIndex = 0 To 11

                            Dim Data As Byte()

                            If ChunkIndex Mod 2 = 0 Then

                                Data =
                                    GeneratePartiallyCompressibleData(
                                        0.75R,
                                        Cs.Options.ChunkSize,
                                        1,
                                        6000 + ChunkIndex)

                            Else

                                Data =
                                    GenerateRandomData(
                                        Cs.Options.ChunkSize,
                                        7000 + ChunkIndex)

                            End If

                            Dim Offset =
                                ChunkIndex * Cs.Options.ChunkSize

                            Cs.Write(Offset, Data)

                            Overlay(Expected, Data, Offset)

                        Next

                        Cs.Options.CompressionMethod =
                            ChunkedStream.
                                ChunkedStreamOptions.
                                CompressionMethods.Deflate

                        Cs.Options.CompressionRatioThreshold =
                            0.95R

                        Cs.Options.EncryptionInfo =
                            New ChunkedStream.
                                EncryptionInfo(
                                    MakeKey(6001))

                        Cs.Options.StoreSparseChunks = True

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression Or
                            ChunkedStream.ApplyOptionTypes.Encryption Or
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Format-evolution migration changed logical data.")

                        Cs.Validate()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace