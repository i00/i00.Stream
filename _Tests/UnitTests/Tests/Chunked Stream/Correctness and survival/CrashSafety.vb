Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class CorrectnessAndSurvival

        ''' <summary>
        ''' Crash-safety tests that use a real <see cref="FileStream" /> (so the durable
        ''' <c>Flush(True)</c> path runs) and a swept tail-truncation harness: after a lost
        ''' write-buffer tail, opening the file must always yield a state the stream really
        ''' passed through, or throw cleanly - never a silently wrong result.
        ''' </summary>
        Public NotInheritable Class CrashSafety

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RealFileStreamRoundTripSurvivesReopen()

                Dim Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "chunkedstream-crashsafety-" & Guid.NewGuid().ToString("N") & ".bin")

                Try

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7701)),
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                    }

                    Dim Expected = GeneratePartiallyCompressibleDataForLength(0.5R, 200000, 65536, 7702)

                    Using Fs As New FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
                        Using Cs = ChunkedStream.Open(Fs, Options)

                            Cs.Write(0, Expected)

                            Using Checkpoint = Cs.CreateCheckpoint()
                                Cs.Write(0, GenerateRandomData(4096, 7703))
                                Checkpoint.Rollback()
                            End Using

                            Dim Patch = GenerateRandomData(9000, 7704)
                            Cs.Write(50000, Patch)
                            Overlay(Expected, Patch, 50000)

                            Cs.Flush()
                            Cs.Validate()

                        End Using
                    End Using

                    Using Fs As New FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        Using Reopened = ChunkedStream.Open(
                            Fs,
                            New ChunkedStream.ChunkedStreamOptions With {
                                .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7701))
                            })

                            AssertEqual(
                                ChunkedStream.RecoveryStates.None,
                                Reopened.RecoveryStateAtOpen,
                                "A cleanly closed FileStream-backed stream should not be pending recovery.")

                            AssertBytesEqual(Expected, Reopened.ToArray(), "FileStream-backed data did not survive reopen.")
                            Reopened.Validate()

                        End Using
                    End Using

                Finally

                    If File.Exists(Path) Then File.Delete(Path)

                End Try

            End Sub

            ''' <summary>
            ''' Builds a file through a run of non-durable generations, then for a spread of
            ''' lost-tail lengths asserts that opening the truncated file either reproduces a
            ''' state the stream actually passed through, or throws a recognised error.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub TruncatedTailAlwaysOpensToAKnownStateOrThrows()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                    .ChunkSize = 256,
                    .IndexPageEntryCount = 4,
                    .IndexDirectoryEntryCount = 4
                }

                Dim Snapshots As New HashSet(Of String)()
                Dim FullBytes As Byte()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For ChunkIndex = 0 To 23
                            Cs.Write(ChunkIndex * Options.ChunkSize,
                                     GenerateRandomData(Options.ChunkSize, 7800 + ChunkIndex))
                            Snapshots.Add(Convert.ToBase64String(Cs.ToArray()))
                        Next

                        For Pass = 0 To 5
                            For ChunkIndex = 0 To 23 Step 2
                                Cs.Write(ChunkIndex * Options.ChunkSize,
                                         GenerateRandomData(Options.ChunkSize, 40000 + (Pass * 100) + ChunkIndex))
                                Snapshots.Add(Convert.ToBase64String(Cs.ToArray()))
                            Next
                        Next

                        Cs.Validate()

                    End Using

                    FullBytes = Ms.ToArray()

                End Using

                Dim OpenedCount = 0
                Dim Span = FullBytes.Length - ChunkedStream.DataStartOffset
                Dim Step_ = Math.Max(1, Span \ 50)

                Dim Cut = 1
                While Cut < Span

                    Dim Truncated As Byte() = Slice(FullBytes, 0, FullBytes.Length - Cut)

                    Dim Backing As New MemoryStream()
                    Backing.Write(Truncated, 0, Truncated.Length)
                    Backing.Position = 0

                    Try

                        Using Recovered = ChunkedStream.Open(Backing)

                            Recovered.Validate()

                            AssertTrue(
                                Snapshots.Contains(Convert.ToBase64String(Recovered.ToArray())),
                                $"Opening the file truncated by {Cut} bytes returned a state the stream never passed through.")

                            OpenedCount += 1

                        End Using

                    Catch Ex As InvalidDataException
                        ' Acceptable: the tail loss left no usable generation.
                    Catch Ex As EndOfStreamException
                        ' Acceptable.
                    Catch Ex As System.Security.Cryptography.CryptographicException
                        ' Acceptable.
                    End Try

                    Cut = If(Cut < 24, Cut + 1, Cut + Step_)

                End While

                AssertTrue(
                    OpenedCount > 0,
                    "No truncation offset produced a readable earlier generation - the fallback path was never exercised.")

            End Sub

        End Class

    End Class

End Namespace
