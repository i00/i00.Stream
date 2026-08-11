Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        ''' <summary>
        ''' Benchmarks new chunk write-location policies.
        ''' </summary>
        <UnitTester.SimpleTest({128, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        <UnitTester.SimpleTest({1024, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        Public Shared Function NewChunkWriteLocationPolicyBenchmark(LogicalChunkCount As Integer, DurationMs As Long) As UnitTester.SimpleTest.BenchmarkResult

            Dim Messages As New List(Of String)

            '
            ' Pre-generate buffers so allocation and pattern generation are not included
            ' in the benchmark.
            '
            Dim DataSets(LogicalChunkCount - 1)() As Byte

            For ChunkIndex = 0 To DataSets.Length - 1
                DataSets(ChunkIndex) =
                    GeneratePatternData(
                        ChunkedStream.DefaultChunkSize,
                        ChunkIndex + 1)
            Next

            For Each Policy As ChunkedStream.ChunkedStreamOptions.NewChunkWriteLocationPolicies In
                [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewChunkWriteLocationPolicies))

                Dim TotalBytesWritten As Long = 0

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .NewChunkWriteLocationPolicy = Policy
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        '
                        ' Build an initial file.
                        '
                        For ChunkIndex = 0 To LogicalChunkCount - 1

                            Cs.Write(
                                ChunkIndex * ChunkedStream.DefaultChunkSize,
                                DataSets(ChunkIndex))

                        Next

                        '
                        ' Create some initial fragmentation.
                        '
                        GenerateFragmentedData(Cs)

                        Dim Rng As New Random(1)

                        Dim Sw = Stopwatch.StartNew()

                        Do While Sw.ElapsedMilliseconds < DurationMs

                            Dim ChunkIndex = Rng.Next(LogicalChunkCount)

                            Cs.Write(
                                CLng(ChunkIndex) * ChunkedStream.DefaultChunkSize,
                                DataSets(ChunkIndex))

                            TotalBytesWritten += DataSets(ChunkIndex).Length

                        Loop

                        Sw.Stop()

                        Dim BytesPerSecond =
                            CLng(TotalBytesWritten /
                                 Math.Max(0.001, Sw.Elapsed.TotalSeconds))

                        Dim Struct = Cs.GetStructure()

                        Messages.Add(
    $"{Policy}: " &
    $"{BytesPerSecond.FormatFileSizeFromBytes()}/s, " &
    $"LiveData={Struct.LiveDataEndOffset.FormatFileSizeFromBytes()}")
                        'Messages.Add(
                        '    $"{Policy}: {BytesPerSecond.FormatFileSizeFromBytes()}/s")

                    End Using

                End Using

            Next

            Return New UnitTester.SimpleTest.BenchmarkResult(
                String.Join(vbCrLf, Messages))

        End Function

    End Class

End Namespace