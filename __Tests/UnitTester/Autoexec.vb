Imports i00CodeLib

Public Class Autoexec

    'ARGS:
    'Command                        Desc                                                                            Eg
    '-------------------------------------------------------------------------------------------------------------------------------
    '/TestAssemblyPath X            Specifies the relative path that will be used to load the TestRunner plugins    /TestAssemblyPath ..\..\..\..\GEKCoAgedCare\bin\Debug
    '/Wait [X]                      The Period of time to pause, in seconds, after running a set of tests           /Wait 3
    '                               If wait is specified with no parameters, the process will wait indefinitely
    '/WaitFail [X]                  Functions like wait, but will wait for this period if there is a failure        /WaitFail
    '/Action X                      Specifies the action to perform Run is default                                  /Action List
    '                               Can be:
    '                                   - List: Lists the plugins that are available
    '                                   - Run:  Runs the tests
    '/PreTestScriptFile             This specifies a file to be executed to run code before tests commence          /PreTestScriptFile ..\..\..\..\PreTestRunner.txt

    'TODO:Filter criteria for tests
    '   /Test X                        Can occur multiple times and lists the tests to run, will run all if not set    /Test i00CodeLib.PrintForm+Test /Test Database.UnitTests
    '   /-Test X                       Can occur multiple times and lists the tests to exclude                         /-Test i00CodeLib.PrintForm+Test /-Test Database.UnitTests

    Public Shared Property ExitCode As Integer = i00CodeLib.i00Debug.TestRunner.ResultTypes.OK '<< this returns the worst test value

    Public Shared Function Main() As Integer
        Dim CommandLine As String = " " & Interaction.Command & " /"

        i00CodeLib.i00Debug.TestRunner.TestPath = Strings.getValueFromString(CommandLine, "TestAssemblyPath", " /", " ")

        'Dim PreTestScriptFile = Strings.getValueFromString(CommandLine, "PreTestScriptFile", " /", " ")
        'If PreTestScriptFile <> "" Then
        '    PreTestScriptFile = FileSystem.MakePathAbsolute(PreTestScriptFile)
        '    If My.Computer.FileSystem.FileExists(PreTestScriptFile) Then
        '        Dim PreTestScript = My.Computer.FileSystem.ReadAllText(PreTestScriptFile)
        '        PreTestScript &= vbCrLf & "Return True"
        '        Dim sc As New i00ScriptCompiler.ScriptCompiler(i00ScriptCompiler.ScriptCompiler.Languages.VBNet)
        '        sc.AddDefaultReferences()
        '        sc.References.AddRange(i00CodeLib.i00Debug.TestRunner.GetTestAssemblies.Select(Function(x) x.Location))
        '        Try
        '            sc.Eval(PreTestScript)
        '        Catch ex As Exception
        '            Dim Errors = sc.CompilerResults.Errors.OfType(Of System.CodeDom.Compiler.CompilerError).Where(Function(x) x.IsWarning = False).ToArray
        '            If Errors.Any Then
        '                Throw New Exception("Error processing the PreTestScript:" & vbCrLf & Join(Errors.Select(Function(x) x.ErrorNumber & ": " & x.ErrorText).ToArray, vbCrLf))
        '            Else
        '                Throw New Exception(Language.English.ReplaceAWithAn($"A {ex.GetType.Name} occurred running the PreTestScript:{vbCrLf}{ex.Message}"), ex)
        '            End If
        '        End Try

        '    Else
        '        Throw New IO.FileNotFoundException($"Could not find file ""{PreTestScriptFile}""", PreTestScriptFile)
        '    End If
        'End If

        'Dim Predicates As New List(Of Func(Of i00Debug.TestRunner, Boolean))
        'Dim TestsInc = Strings.getValuesFromString(CommandLine, "Test", " /", " ")
        'If TestsInc.Any Then
        '    Predicates.Add(Function(x) TestsInc.Contains(x.GetType.FullName))
        'End If
        'Dim TestsEx = Strings.getValuesFromString(CommandLine, "-Test", " /", " ")
        'If TestsEx.Any Then
        '    Predicates.Add(Function(x) TestsEx.Contains(x.GetType.FullName) = False)
        'End If
        'Dim Predicate As Func(Of i00Debug.TestRunner, Boolean) = Nothing
        'If Predicates.Any Then
        '    Predicate = Function(x) Predicates.All(Function(y) y.Invoke(x))
        'End If

        i00CodeLib.i00Debug.TestRunner.TestPath = IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly.Location)
        Dim TestData = i00CodeLib.i00Debug.TestRunner.GetPlugins().Where(Function(x) True).'TODO:Add criteria here to filter
                                                                   Select(Function(x) New With {.TestRunnerName = x.GetType.FullName,
                                                                                                .TestRunner = x,
                                                                                                .Tests = x.GetTests().Where(Function(y) True).'TODO:Add criteria here to filter
                                                                                                                      ToArray()}).
                                                                   ToArray()

        Dim TestsFailed = 0
        Dim TestsWarnings = 0
        Dim TestsPassed = 0
        Dim Action = Strings.getValueFromString(CommandLine, "Action", " /", " ")
        Select Case LCase(Action)
            Case "list"
                For Each iTestRunner In TestData
                    i00CodeLib.i00Debug.Write(iTestRunner.TestRunnerName)
                    For Each iTest In iTestRunner.Tests
                        i00CodeLib.i00Debug.Write(Join(iTest.CategoryPath, "."), 1)
                    Next
                Next
            Case Else '"run"
                For Each iTestRunner In TestData
                    i00CodeLib.i00Debug.Write(iTestRunner.TestRunnerName)

                    Dim LastCategory As New List(Of String)
                    Dim BaseIndent = 1

                    For Each iTest In iTestRunner.Tests
                        iTest.Run()

                        'get the category difference...
                        Dim ThisCategory = iTest.CategoryPath.ToList
                        Dim JoinedCats = LastCategory.FullOuterJoin(ThisCategory, Function(x) LastCategory.IndexOf(x), Function(y) ThisCategory.IndexOf(y), Function(x, y) New With {.Last = x, .Current = y}).
                                                      Where(Function(x) x.Current IsNot Nothing).
                                                      ToArray
                        Dim Difference = False
                        Dim iCat As Integer
                        For iCat = LBound(JoinedCats) To UBound(JoinedCats)
                            Dim Cat = JoinedCats(iCat)
                            If Difference = False Then Difference = Cat.Current <> Cat.Last
                            If Difference = True Then
                                'print the cat
                                i00CodeLib.i00Debug.Write(Cat.Current & ":", iCat + BaseIndent)
                            End If
                        Next
                        'now print any results
                        For Each iResult In iTest.Results
                            Dim Color = i00CodeLib.i00Debug.DefaultConsoleColor
                            Select Case iResult.Result
                                Case i00CodeLib.i00Debug.TestRunner.ResultTypes.Failure
                                    Color = ConsoleColor.DarkRed
                                    TestsFailed += 1
                                Case i00CodeLib.i00Debug.TestRunner.ResultTypes.Warning
                                    Color = ConsoleColor.DarkYellow
                                    TestsWarnings += 1
                                Case i00CodeLib.i00Debug.TestRunner.ResultTypes.OK
                                    Color = ConsoleColor.DarkGreen
                                    TestsPassed += 1
                            End Select
                            Dim ExtraIndent = 0
                            If iResult.Name <> "" Then
                                i00CodeLib.i00Debug.Write(iResult.Name, iCat + BaseIndent + ExtraIndent)
                                ExtraIndent += 1
                            End If
                            i00CodeLib.i00Debug.Write($"{iResult.Result}{If(iResult.Message = "", "", ": " & iResult.Message)}", iCat + BaseIndent + ExtraIndent, Color)
                        Next

                        LastCategory = ThisCategory
                    Next

                    i00CodeLib.i00Debug.Write("")
                Next

                'Dim IteratorResults = i00Debug.TestRunner.RunAll(Predicate)
                'Dim Results As New List(Of i00Debug.TestRunner)
                'Dim st As New Stopwatch()
                'st.Start()
                'For Each item In IteratorResults
                '    i00Debug.Write("")
                '    i00Debug.Write(item.GetType.FullName)
                '    i00Debug.Write(New String("-"c, 50))
                '    i00Debug.Write("")
                '    item.DebugPrint(1)
                '    Results.Add(item)
                'Next
                'st.Stop()

                'i00Debug.Write("")
                'i00Debug.Write(New String("-"c, 50))
                'i00Debug.Write("Summary")
                'i00Debug.Write(New String("-"c, 50))
                'Dim dicResults As New Dictionary(Of String, String)
                'Dim PrintResults = Sub(Clear As Boolean)
                '                       If dicResults.Any Then
                '                           Dim LargestText = dicResults.Max(Function(x) Len(x.Key))
                '                           For Each SummaryItem In dicResults
                '                               i00Debug.Write(SummaryItem.Key & New String(" "c, LargestText - Len(SummaryItem.Key)) & ": " & SummaryItem.Value, 1)
                '                           Next
                '                           If Clear Then dicResults.Clear()
                '                       End If
                '                   End Sub
                'dicResults.Add("Total Tests", $"{Results.Count}")
                'dicResults.Add("Total Time", $"{st.ElapsedMilliseconds} ms")
                'PrintResults(True)
                'i00Debug.Write("")
                'If Results.Any Then
                '    Dim SubTests = Results.SelectMany(Function(x) x.Results).Select(Function(x) New With {.TestName = Join(x.Category, " > "), .Result = x}).GroupBy(Function(x) x.TestName).ToArray
                '    dicResults.Add("Sub Tests", $"{SubTests.Count}")
                '    Dim TestResultsByResult = SubTests.Select(Function(x) x.OrderBy(Function(y) y.Result.Result).Last).GroupBy(Function(x) x.Result.Result).OrderBy(Function(x) x.Key).ToArray
                '    For Each item In TestResultsByResult
                '        dicResults.Add(item.Key.GetDescriptionAttribute, $"{item.Count}")
                '    Next
                '    PrintResults(True)
                '    'Dim FailedTests = TestResultsByResult.FirstOrDefault(Function(x) x.Key = i00Debug.TestRunner.ResultTypes.Failure)
                '    'If FailedTests IsNot Nothing Then
                '    '    i00Debug.Write("")
                '    '    i00Debug.Write("Tests with failures:", 1)
                '    '    For Each item In FailedTests
                '    '        i00Debug.Write(item.TestName, 2)
                '    '    Next
                '    '    'qwertyuiop - fill failed tests here
                '    '    'PrintResults(True)
                '    'End If
                'End If

                ''Worst result:
                'ExitCode = Results.SelectMany(Function(x) x.Results).Select(Function(x) x.Result).OrderBy(Function(x) x).LastOrDefault
        End Select

        i00CodeLib.i00Debug.Write($"Results")
        i00CodeLib.i00Debug.Write($"Passed: {TestsPassed}", 1, ConsoleColor.DarkGreen)
        i00CodeLib.i00Debug.Write($"Warning: {TestsWarnings}", 1, ConsoleColor.DarkYellow)
        i00CodeLib.i00Debug.Write($"Failed: {TestsFailed}", 1, ConsoleColor.DarkRed)

        Dim HasWaitFail = Strings.getValueFromStringExists(CommandLine, "WaitFail", " /", " ")

        Dim WaitCommandToUse = "Wait"
        If HasWaitFail AndAlso TestsFailed > 0 Then
            WaitCommandToUse &= "Fail"
        End If

        Dim ShouldWait = Strings.getValueFromStringExists(CommandLine, WaitCommandToUse, " /", " ")
        If ShouldWait Then
            Dim Wait As Decimal
            If Decimal.TryParse(Strings.getValueFromString(CommandLine, WaitCommandToUse, " /", " "), Wait) Then
                'wait filled
                System.Threading.Thread.Sleep(CInt(Wait * 1000))
            Else
                'wait not filled
                'wait forever
                i00CodeLib.i00Debug.Write("")
                i00CodeLib.i00Debug.Write("Press any key to continue . . .")
                Console.ReadKey(True)
            End If
        End If

        Return ExitCode
    End Function

End Class
