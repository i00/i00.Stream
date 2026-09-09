Imports System.IO
Imports i00.Streams
Imports i00
Imports UnitTester

Namespace Tests
    Partial Public NotInheritable Class StreamChunked

        <UnitTester.SimpleBenchmark()>
        Public Shared Function GraphicalDefragTest() As SimpleTest.BenchmarkResult
            Dim Options = New ChunkedStream.ChunkedStreamOptions() With {
                .ChunkSize = ChunkedStream.DefaultChunkSize,
                .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None,
                .NewIndexPageWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.Append
            }

            Dim GetDefragDisplay = Function(Cs As ChunkedStream) As String
                                       Dim Struct = Cs.GetStructure()
                                       Return String.Join("",
                                                          Struct.GetFragmentationBlocks(50, 1).
                                                                 Select(Function(x) $"{ConsoleEx.Format.Foreground.Custom(x.SuggestedColor)}▌"))
                                   End Function

            Dim Results As New List(Of (Fragmentation As Double, MaxFragmentation As Double, [Key] As String, Value As String))

            Using Ms As New MemoryStream()
                Using Cs = ChunkedStream.Open(Ms, Options)
                    PhysicalLayoutOperations.Defragmentation.CreateFragmentedStream(Cs, 1234)

                    Results.Add((Fragmentation:=Cs.GetFragmentation, MaxFragmentation:=1, [Key]:="Before", Value:=GetDefragDisplay(Cs)))
                End Using
                Dim PreDefragMs = Ms.ToArray()

                For Each DefragType In [Enum].GetValues(GetType(ChunkedStream.DefragTypes)).
                                              OfType(Of ChunkedStream.DefragTypes)()
                    Ms.Position = 0
                    Ms.SetLength(0)
                    Ms.Write(PreDefragMs, 0, PreDefragMs.Length)
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Dim PreDefrag = Cs.ToArray()
                        Cs.Defragment(DefragType)

                        Cs.Validate.ThrowIfErrors()

                        Dim PostDefrag = Cs.ToArray()
                        AssertBytesEqual(PreDefrag, PostDefrag, $"Data mismatch after defrag({DefragType}).")

                        Dim MaxFragmentation = 0.0
                        Select Case DefragType
                            Case ChunkedStream.DefragTypes.Move
                                MaxFragmentation = 0.1
                            Case ChunkedStream.DefragTypes.Sequence
                                MaxFragmentation = 0.01
                            Case Else 'ChunkedStream.DefragTypes.Rebuild
                                'already set
                        End Select
                        Results.Add((Fragmentation:=Cs.GetFragmentation, MaxFragmentation:=MaxFragmentation, [Key]:=$"After ({DefragType})", Value:=GetDefragDisplay(Cs)))

                    End Using

                Next
            End Using

            'Sb.AppendLine($"After {item}: {Cs.GetFragmentation():P2} {GetDefragDisplay(Cs)}{ConsoleEx.Format.Foreground.Default()}")
            Dim ResultType = TestRunner.ResultTypes.OK
            Dim ParsedResults = Results.Select(Function(x) New With {.Warning = x.Fragmentation > x.MaxFragmentation,
                                                                     .KeyText1 = $"{x.Key}: ",
                                                                     .KeyText2 = $"{x.Fragmentation:P2}",
                                                                     x.Value})
            If ParsedResults.Any(Function(x) x.Warning) Then
                ResultType = TestRunner.ResultTypes.Warning
            End If
            Dim MaxKeyTextLen = ParsedResults.Max(Function(x) x.KeyText1.Length + x.KeyText2.Length)
            Dim ParsedString = String.Join(Environment.NewLine, ParsedResults.Select(Function(x) $"{ConsoleEx.Format.Foreground.Default}{x.KeyText1}{New String(" "c, (MaxKeyTextLen - x.KeyText1.Length - x.KeyText2.Length) + 1)}{If(x.Warning, ConsoleEx.Format.Foreground.DarkYellow, ConsoleEx.Format.Foreground.Default)}{x.KeyText2} {x.Value}{ConsoleEx.Format.Foreground.Default}"))
            Return New SimpleTest.BenchmarkResult(ParsedString, ResultType)

        End Function

        <UnitTester.SimpleBenchmark()>
        Public Shared Function BaseFileSizesWithoutCompression() As UnitTester.SimpleTest.BenchmarkResult

            Dim Results As New Dictionary(Of String, String)

            Dim Options = New ChunkedStream.ChunkedStreamOptions() With {
                .ChunkSize = ChunkedStream.DefaultChunkSize,
                .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None
            }

            Dim Tests = {
                New With {.Test = "No Data",
                          .MaxSizeBytes = 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                    End Sub},
                New With {.Test = "Single Sparse Byte",
                          .MaxSizeBytes = 10 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        Cs.SetLength(1)
                                    End Sub},
                New With {.Test = "Single Byte",
                          .MaxSizeBytes = 10 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        Cs.Write(0, New Byte() {1})
                                    End Sub},
                New With {.Test = $"1 {CLng(Options.ChunkSize).FormatFileSizeFromBytes} Chunk",
                          .MaxSizeBytes = 150 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 1234))
                                    End Sub},
                New With {.Test = "1 Chunk + 1 Byte(demoting)",
                          .MaxSizeBytes = 150 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 1234))
                                        Cs.Write(Options.ChunkSize, New Byte() {5})
                                        Cs.SetLength(Options.ChunkSize)
                                    End Sub},
                New With {.Test = "1 Chunk + 1 Byte",
                          .MaxSizeBytes = 150 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        Cs.Write(0, GenerateRandomData(Options.ChunkSize + 1, 1234))
                                    End Sub},
                New With {.Test = "1 Chunk + 1 Byte(promoting)",
                          .MaxSizeBytes = 150 * 1024,
                          .Action = Sub(Cs As ChunkedStream)
                                        ' Two separate Write calls, not one combined write - this
                                        ' specifically forces the first write to land in the elided
                                        ' single-record state, then forces graduation to real paging
                                        ' mid-session on the second write, rather than only testing
                                        ' what a fresh two-record file looks like from scratch.
                                        Cs.Write(0, GenerateRandomData(Options.ChunkSize, 1234))
                                        Cs.Write(Options.ChunkSize, New Byte() {5})
                                    End Sub}
            }

            Dim ResultType = UnitTester.TestRunner.ResultTypes.OK

            For Each Test In Tests

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Test.Action.Invoke(Cs)

                        Expected = Cs.ToArray()

                        Cs.Validate().ThrowIfErrors()

                        Dim StructBeforeReopen = Cs.GetStructure()

                        'AssertEqual(
                        '    Test.ExpectedIndexPageCount,
                        '    StructBeforeReopen.Regions.Where(Function(r) r.RegionType = ChunkedStreamStructure.RegionTypes.IndexPage).Count,
                        '    $"[{Test.Test}] Unexpected IndexPage count before reopen - elision did Not engage/disengage As expected.")

                    End Using

                    ' The size numbers alone only prove the write side elided or graduated as
                    ' expected. Reopening is what actually exercises ReadPagedMetadata's
                    ' synthesis path for the elided case, and confirms the graduated case
                    ' didn't leave anything behind from the transition.
                    Using Reopened = ChunkedStream.Open(Ms, Options)

                        AssertBytesEqual(
                            Expected,
                            Reopened.ToArray(),
                            $"[{Test.Test}] Data mismatch after reopen.")

                        Reopened.Validate().ThrowIfErrors()

                        Reopened.Defragment(ChunkedStream.DefragTypes.Sequence)
                        Dim StructAfterReopen = Reopened.GetStructure()

                        Dim RegionString =
                            Join(StructAfterReopen.Regions.GroupBy(Function(x) x.RegionType).
                                                           Select(Function(x) $"{x.Key}({x.Count})").
                                                           ToArray(), ", ")

                        Dim SizeString = $"{Ms.Length:N0} B"
                        If Ms.Length >= Test.MaxSizeBytes Then
                            SizeString = $"{UnitTester.ConsoleEx.Format.Foreground.DarkYellow}{SizeString}{UnitTester.ConsoleEx.Format.Foreground.Default}"
                            ResultType = UnitTester.TestRunner.ResultTypes.Warning
                        End If
                        Results.Add(Test.Test, $"{SizeString} {RegionString}")

                    End Using

                End Using

            Next

            Return New UnitTester.SimpleTest.BenchmarkResult(
                $"{UnitTester.ConsoleEx.Format.Foreground.Default}{Join(Results.Select(Function(x) $"{x.Key}: {x.Value}").ToArray, Environment.NewLine)}", ResultType)

        End Function

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

        '    ''' <summary>
        '    ''' Benchmarks new chunk write-location policies.
        '    ''' </summary>
        '    <UnitTester.SimpleBenchmark({128, 1000})>
        '    <UnitTester.SimpleBenchmark({256, 1000})>
        '    <UnitTester.SimpleBenchmark({512, 1000})>
        '    <UnitTester.SimpleBenchmark({1024, 1000})>
        '    Public Shared Function NewChunkWriteLocationPolicyBenchmark(LogicalChunkCount As Integer,
        '                                                                DurationMs As Long) As UnitTester.SimpleTest.BenchmarkResult
        '        '<UnitTester.SimpleTest({2048, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '        '<UnitTester.SimpleTest({4096, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>
        '        '<UnitTester.SimpleTest({8192, 1000}, TestType:=UnitTester.SimpleTest.TestTypes.Benchmark)>

        '        Dim Messages As New List(Of String)

        '        '
        '        ' Pre-generate buffers so allocation and pattern generation are not included
        '        ' in the benchmark.
        '        '
        '        Dim DataSets(LogicalChunkCount - 1)() As Byte

        '        For ChunkIndex = 0 To DataSets.Length - 1
        '            DataSets(ChunkIndex) =
        '                GeneratePatternData(
        '                    ChunkedStream.DefaultChunkSize,
        '                    ChunkIndex + 1)
        '        Next

        '        Dim InitialWrite = True

        '        For Each Policy As ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies In
        '            [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies))

        '            Dim TotalBytesWritten As Long = 0

        '            Using Ms As New MemoryStream()

        '                Dim Options As New ChunkedStream.ChunkedStreamOptions With {
        '                    .NewChunkWriteLocationPolicy = Policy
        '                }

        '                Using Cs = ChunkedStream.Open(Ms, Options)

        '                    '
        '                    ' Build an initial file.
        '                    '
        '                    TotalBytesWritten = 0

        '                    Dim Sw = Stopwatch.StartNew()

        '                    For ChunkIndex = 0 To LogicalChunkCount - 1
        '                        Cs.Write(
        '                            CLng(ChunkIndex) * ChunkedStream.DefaultChunkSize,
        '                            DataSets(ChunkIndex))

        '                        TotalBytesWritten += DataSets(ChunkIndex).Length
        '                    Next

        '                    Sw.Stop()

        '                    Dim BytesPerSecond =
        '                        CLng(TotalBytesWritten /
        '                             Math.Max(0.001, Sw.Elapsed.TotalSeconds))

        '                    If InitialWrite Then
        '                        Dim StructInitial = Cs.GetStructure()

        '                        Messages.Add(
        '                            $"Initial: " &
        '                            $"{BytesPerSecond.FormatFileSizeFromBytes()}/s, " &
        '                            $"LiveData={StructInitial.LiveDataEndOffset.FormatFileSizeFromBytes()}, " &
        '                            $"Physical={Ms.Length.FormatFileSizeFromBytes()}")

        '                        InitialWrite = False
        '                    End If

        '                    '
        '                    ' Create some initial fragmentation.
        '                    '
        '                    GenerateFragmentedData(Cs)

        '                    '
        '                    ' Only measure metadata cost for the timed benchmark section.
        '                    '
        '                    Cs.DebugResetMetadataCounters()

        '                    Dim Rng As New Random(1)

        '                    TotalBytesWritten = 0
        '                    Sw.Restart()

        '                    Do While Sw.ElapsedMilliseconds < DurationMs
        '                        Dim ChunkIndex = Rng.Next(LogicalChunkCount)

        '                        Cs.Write(
        '                            CLng(ChunkIndex) * ChunkedStream.DefaultChunkSize,
        '                            DataSets(ChunkIndex))

        '                        TotalBytesWritten += DataSets(ChunkIndex).Length
        '                    Loop

        '                    Sw.Stop()

        '                    BytesPerSecond =
        '                        CLng(TotalBytesWritten /
        '                             Math.Max(0.001, Sw.Elapsed.TotalSeconds))

        '                    Dim Struct = Cs.GetStructure()

        '                    Messages.Add(
        '$"{Policy}: " &
        '$"{BytesPerSecond.FormatFileSizeFromBytes()}/s, " &
        '$"LiveData={Struct.LiveDataEndOffset.FormatFileSizeFromBytes()}, " &
        '$"Physical={Ms.Length.FormatFileSizeFromBytes()}, " &
        '$"Persists={Cs.DebugPersistCount:N0}, " &
        '$"AvgDirtyPages={Cs.DebugAveragePersistIndexPageCount:N2}, " &
        '$"PersistMs={Cs.DebugPersistMilliseconds:N0}, " &
        '$"AvgPersistMs={Cs.DebugAveragePersistMilliseconds:N3}, " &
        '$"IndexMs={Cs.DebugIndexPageMilliseconds:N0}, " &
        '$"DirMs={Cs.DebugDirectoryMilliseconds:N0}, " &
        '$"ChunkDirMs={Cs.DebugChunkDirectoryMilliseconds:N0}, " &
        '$"HoleDirMs={Cs.DebugHoleDirectoryMilliseconds:N0}, " &
        '$"RootMs={Cs.DebugRootMilliseconds:N0}, " &
        '$"HeaderMs={Cs.DebugHeaderMilliseconds:N0}")

        '                End Using

        '            End Using

        '        Next

        '        Return New UnitTester.SimpleTest.BenchmarkResult(
        '            String.Join(vbCrLf, Messages))

        '    End Function

    End Class

End Namespace