Public Class Autoexec

    'ARGS:
    'Command                        Desc                                                                            Eg
    '-------------------------------------------------------------------------------------------------------------------------------
    '/TestAssemblyPath X            Specifies the relative path that will be used to load the TestRunner plugins    /TestAssemblyPath ..\..\..\..\GEKCoAgedCare\bin\Debug
    '/Wait [X]                      The Period of time to pause, in seconds, after running a set of tests           /Wait 3
    '                               If wait is specified with no parameters, the process will wait indefinitely
    '/WaitWarn [X]                  Functions like wait, but will wait for this period if there is a warning        /WaitWarn
    '/WaitFail [X]                  Functions like wait, but will wait for this period if there is a failure        /WaitFail
    '/Action X                      Specifies the action to perform Run is default                                  /Action List
    '                               Can be:
    '                                   - List: Lists the plugins that are available
    '                                   - Run:  Runs the tests

    'TODO:Filter criteria for tests
    '   /Test X                        Can occur multiple times and lists the tests to run, will run all if not set    /Test i00CodeLib.PrintForm+Test /Test Database.UnitTests
    '   /-Test X                       Can occur multiple times and lists the tests to exclude                         /-Test i00CodeLib.PrintForm+Test /-Test Database.UnitTests

    Public Shared Property ExitCode As Integer = TestRunner.ResultTypes.OK '<< this returns the worst test value

    Public Shared Function Main() As Integer
        Dim CommandLine As String = " " & Interaction.Command & " /"

        TestRunner.TestPath = Misc.getValueFromString(CommandLine, "TestAssemblyPath", " /", " ")

        TestRunner.TestPath = IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly.Location)
        Dim TestData = TestRunner.GetPlugins().Where(Function(x) True).'TODO:Add criteria here to filter
                                               Select(Function(x) New With {.TestRunnerName = x.GetType.FullName,
                                                                            .TestRunner = x,
                                                                            .Tests = x.GetTests().Where(Function(y) True).'TODO:Add criteria here to filter
                                                                                                  ToArray()}).
                                               ToArray()

        Dim TestsFailed = 0
        Dim TestsWarnings = 0
        Dim TestsPassed = 0
        Dim Action = Misc.getValueFromString(CommandLine, "Action", " /", " ")
        Select Case LCase(Action)
            Case "list"
                For Each iTestRunner In TestData
                    Write(iTestRunner.TestRunnerName)
                    For Each iTest In iTestRunner.Tests
                        Write(Join(iTest.CategoryPath, "."), 1)
                    Next
                Next
            Case Else '"run"
                For Each iTestRunner In TestData
                    Write(iTestRunner.TestRunnerName)

                    Dim LastCategory As New List(Of String)
                    Dim BaseIndent = 1

                    For Each iTest In iTestRunner.Tests
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
                                Write(Cat.Current & ":", iCat + BaseIndent)
                            End If
                        Next
                        iTest.Run()

                        'now print any results
                        For Each iResult In iTest.Results
                            Dim Color = DefaultConsoleColor
                            Select Case iResult.Result
                                Case TestRunner.ResultTypes.Failure
                                    Color = ConsoleColor.DarkRed
                                    TestsFailed += 1
                                Case TestRunner.ResultTypes.Warning
                                    Color = ConsoleColor.DarkYellow
                                    TestsWarnings += 1
                                Case TestRunner.ResultTypes.OK
                                    Color = ConsoleColor.DarkGreen
                                    TestsPassed += 1
                            End Select
                            Dim ExtraIndent = 0
                            If iResult.Name <> "" Then
                                Write(iResult.Name, iCat + BaseIndent + ExtraIndent)
                                ExtraIndent += 1
                            End If
                            Write($"{iResult.Result}{If(iResult.Message = "", "", ": " & iResult.Message.Indent(New String(" "c, Len($"{iResult.Result}: ")), True))}", iCat + BaseIndent + ExtraIndent, Color)
                        Next

                        LastCategory = ThisCategory
                    Next

                    Write("")
                Next

        End Select

        If TestsFailed > 0 Then
            ExitCode = TestRunner.ResultTypes.Failure
        ElseIf TestsWarnings > 0 Then
            ExitCode = TestRunner.ResultTypes.Warning
        End If

        Write($"Results")
        Write($"Passed: {TestsPassed}", 1, ConsoleColor.DarkGreen)
        Write($"Warning: {TestsWarnings}", 1, ConsoleColor.DarkYellow)
        Write($"Failed: {TestsFailed}", 1, ConsoleColor.DarkRed)

        Dim HasWaitFail = Misc.getValueFromStringExists(CommandLine, "WaitFail", " /", " ")
        Dim HasWaitWarn = Misc.getValueFromStringExists(CommandLine, "WaitWarn", " /", " ")

        Dim WaitCommandToUse = "Wait"
        If HasWaitFail AndAlso TestsFailed > 0 Then
            WaitCommandToUse &= "Fail"
        ElseIf HasWaitWarn AndAlso TestsWarnings > 0 Then
            WaitCommandToUse &= "Warn"
        End If

        Dim ShouldWait = Misc.getValueFromStringExists(CommandLine, WaitCommandToUse, " /", " ")
        If ShouldWait Then
            Dim Wait As Decimal
            If Decimal.TryParse(Misc.getValueFromString(CommandLine, WaitCommandToUse, " /", " "), Wait) Then
                'wait filled
                System.Threading.Thread.Sleep(CInt(Wait * 1000))
            Else
                'wait not filled
                'wait forever
                Write("")
                Write("Press any key to continue . . .")
                Console.ReadKey(True)
            End If
        End If

        Return ExitCode
    End Function

    Friend Shared ReadOnly Property DefaultConsoleColor As ConsoleColor = Console.ForegroundColor
    Friend Shared Sub Write(Text As String, Optional Indent As Integer = 0, Optional Color As ConsoleColor = DirectCast(-1, ConsoleColor))
        If Color = DirectCast(-1, ConsoleColor) Then Color = DefaultConsoleColor
        'Text = New String(" "c, Indent * 4) & Text
        Text = Text.Indent(New String(" "c, Indent * 4))
        Console.ForegroundColor = Color
        Console.WriteLine(Text)
        Debug.Print($"##[{Color}]##{Text}##[\{Color}]##")
        Console.ForegroundColor = DefaultConsoleColor
    End Sub

End Class
