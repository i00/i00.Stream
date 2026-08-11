Imports System.IO
Imports StreamEncryption.Streams

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        '''' <summary>
        '''' Measures the cost of publishing metadata as index size grows.
        '''' </summary>
        '<UnitTester.SimpleTest({128, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '<UnitTester.SimpleTest({256, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '<UnitTester.SimpleTest({512, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '<UnitTester.SimpleTest({1024, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '<UnitTester.SimpleTest({2048, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        'Public Shared Function BenchmarkMetadataPublish(LogicalChunkCount As Integer,
        '                                                DurationMs As Long) As UnitTester.SimpleTest.BenchmarkResult

        '    If LogicalChunkCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(LogicalChunkCount))
        '    If DurationMs <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(DurationMs))

        '    Dim TotalBytesWritten As Long = 0
        '    Dim WriteCount As Long = 0

        '    Using Ms As New MemoryStream()

        '        Dim Options As New ChunkedStream.ChunkedStreamOptions With {
        '    .NewChunkWriteLocationPolicy =
        '        ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append
        '}

        '        Using Cs = ChunkedStream.Open(Ms, Options)

        '            '
        '            ' Build a stream containing the requested number of chunks.
        '            '
        '            For ChunkIndex = 0 To LogicalChunkCount - 1

        '                Cs.Write(
        '            CLng(ChunkIndex) * ChunkedStream.DefaultChunkSize,
        '            GeneratePatternData(
        '                ChunkedStream.DefaultChunkSize,
        '                ChunkIndex + 1))

        '            Next

        '            '
        '            ' Benchmark repeatedly rewriting the SAME chunk.
        '            '
        '            ' LogicalChunkCount changes the size of the index,
        '            ' but not the amount of user data written.
        '            '
        '            Dim Buffer =
        '        GeneratePatternData(
        '            ChunkedStream.DefaultChunkSize,
        '            12345)

        '            Dim Sw = Stopwatch.StartNew()

        '            Do While Sw.ElapsedMilliseconds < DurationMs

        '                Cs.Write(0, Buffer)

        '                TotalBytesWritten += Buffer.Length
        '                WriteCount += 1

        '            Loop

        '            Sw.Stop()

        '            Dim Throughput =
        '        CLng(TotalBytesWritten /
        '             Math.Max(0.001,
        '                      Sw.Elapsed.TotalSeconds))

        '            Return New UnitTester.SimpleTest.BenchmarkResult(
        '        $"IndexEntries={LogicalChunkCount:N0}, " &
        '        $"Speed={Throughput.FormatFileSizeFromBytes()}/s, " &
        '        $"Writes/s={(WriteCount / Math.Max(0.001, Sw.Elapsed.TotalSeconds)):N0}")

        '        End Using

        '    End Using

        'End Function

        ''' <summary>
        ''' Benchmarks new chunk write-location policies.
        ''' </summary>
        <UnitTester.SimpleTest({128, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        <UnitTester.SimpleTest({256, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        <UnitTester.SimpleTest({512, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        <UnitTester.SimpleTest({1024, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        <UnitTester.SimpleTest({2048, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
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

            For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
                [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

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