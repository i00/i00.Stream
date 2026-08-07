Imports BrightIdeasSoftware2012
Imports i00CodeLib

Public Class UnitTestTabControl

    Private Shared Function GetBranchSaveFile() As String
        Dim GitPath = Misc.GetGitPath()
        If GitPath <> "" Then
            Dim JobNo = Misc.GetGitJobNo()
            If JobNo.HasValue Then
                Return IO.Path.Combine(GitPath, "__Branch UnitTest Selection", $"{JobNo}")
            End If
        End If
        Return Nothing
    End Function

    Public Sub New()
        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Dim DebugWindow = i00Debug.GetDebuggerWindow()
        If DebugWindow IsNot Nothing Then
            Dim SaveFile = GetBranchSaveFile()
            If SaveFile <> "" AndAlso My.Computer.FileSystem.FileExists(SaveFile) Then
                DelayInvoke(Sub()
                                NotifyIcon.Visible = True
                                tt.ToolTipTitle = "Unit tests exist for this branch"
                                tt.ShowHTML("Click to view selected tests", NotifyIcon, 5000)
                            End Sub)
            End If
        End If
    End Sub

    Private Sub NotifyIcon_MouseClick(sender As Object, e As MouseEventArgs) Handles NotifyIcon.MouseClick
    End Sub

    Private WithEvents _tt As i00CodeLib.Controls.HTMLToolTip
    Public ReadOnly Property tt As i00CodeLib.Controls.HTMLToolTip
        Get
            If _tt Is Nothing Then _tt = New i00CodeLib.Controls.HTMLToolTip With {.IsBalloon = True, .ToolTipIcon = ToolTipIcon.Info}
            Return _tt
        End Get
    End Property

    Private Sub UnitTestTabControl_Disposed(sender As Object, e As EventArgs) Handles Me.Disposed
        _tt?.Dispose()
        tRunTests?.Abort()
    End Sub

    Private Sub _tt_TipClosed(sender As Object, e As EventArgs) Handles _tt.TipClosed
        NotifyIcon.Visible = False
    End Sub

    Private Sub _tt_TipClick(sender As Object, e As i00CodeLib.Controls.HTMLToolTip.TipClickEventArgs) Handles _tt.TipClick
        Dim DebugWindow = i00Debug.GetDebuggerWindow()
        If DebugWindow IsNot Nothing Then
            Dim MyTab = DebugWindow.TabButtons.FirstOrDefault(Function(x) TypeOf x.Plugin Is UnitTestTab)
            If MyTab IsNot Nothing Then
                i00CodeLib.Shell.Window.RestoreAndFocus(DebugWindow)
                MyTab.Click()
            End If
        End If
    End Sub

    Dim colTest As OLVColumn = New OLVColumn("Test", NameOf(DataObject.Text)) With {.FillsFreeSpace = True}

    Private Sub UnitTestTabControl_Load(sender As Object, e As EventArgs) Handles Me.Load

        tsiRun.DefaultItem = tsiRunSelected
        tsiRunSelected.Font = i00CodeLib.MakeBold(tsiRunSelected.Font)

        Dim JobNo = Misc.GetGitJobNo()
        If JobNo.HasValue Then
            tsiWorkItem.Text = $"Select unit tests for: #{Misc.GetGitJobNo()}"
            tsiSave.Visible = True
        End If

        'Tree:
        tvTests.HierarchicalCheckboxes = True
        tvTests.CheckBoxes = True
        tvTests.HeaderStyle = ColumnHeaderStyle.None
        tvTests.UseCellFormatEvents = True
        tvTests.SelectionMode = ObjectListView.SelectionModes.FullRowSelect
        tvTests.TreeColumnRenderer.UseTriangles = True
        tvTests.TreeColumnRenderer.LinePen = New Pen(Drawing.BlendColor(tvTests.BackColor, tvTests.ForeColor))
        tvTests.UseFiltering = True
        tvTests.MultiSelect = True


        tvTests.ChildrenGetter = Function(model) DirectCast(model, DataObject).Children
        tvTests.CanExpandGetter = Function(model) DirectCast(model, DataObject).HasChildren
        tvTests.Columns.Add(colTest)
        tvTests.CheckBoxEnabledOnRowGetter = Function(model) DirectCast(model, DataObject).CanBeChecked

        i00CodeLib.i00Debug.TestRunner.TestPath = IO.Path.GetDirectoryName(Reflection.Assembly.GetEntryAssembly.Location)
        Dim Roots As New List(Of DataObject)
        For Each iTestRunner In i00CodeLib.i00Debug.TestRunner.GetPlugins()
            Roots.Add(New DataObject(Nothing) With {.TestRunner = iTestRunner})
        Next
        tvTests.Roots = Roots


        Dim SaveFile = GetBranchSaveFile()
        If SaveFile <> "" AndAlso My.Computer.FileSystem.FileExists(SaveFile) Then
            Dim CheckedItems As New List(Of DataObject)

            Using r = My.Computer.FileSystem.OpenTextFileReader(SaveFile)
                Do
                    Dim Line = r.ReadLine()
                    If Line Is Nothing Then Exit Do
                    Dim DataObject = GetDataObjectByTreePath(Line)
                    If DataObject IsNot Nothing Then CheckedItems.Add(DataObject)
                Loop
            End Using

            'now expand to the items
            For Each iChecked In CheckedItems
                Dim itemTree = iChecked.Recurse(Function(x) {x.Parent}).
                                        Reverse()
                For Each iTreeItem In itemTree
                    tvTests.Expand(iTreeItem)
                Next

                'and check them
                tvTests.CheckObject(iChecked)
            Next
        End If

    End Sub

    Private Function GetDataObjectByTreePath(TreePath As String) As DataObject
        Dim PathSections = Split(TreePath, vbTab)

        Dim SubBranches = tvTests.Roots.OfType(Of DataObject)
        For iSection = 0 To PathSections.Count - 1
            Dim SectionKey = PathSections(iSection)
            Dim SubSection = SubBranches.FirstOrDefault(Function(x) x.Text = SectionKey)
            If SubSection Is Nothing Then
                'key no longer found
                Return Nothing
            Else
                Dim isLastSection = iSection = PathSections.Count - 1
                If isLastSection Then
                    'return the subsection ...
                    Return SubSection
                Else
                    'go into the SubBranches
                    SubBranches = SubSection.Children
                End If
            End If
        Next
        Return Nothing
    End Function

    Public Class DataObject

        Public Property TestItemForeColor As Color?

        Public ReadOnly Property Text() As String
            Get
                If TestRunner IsNot Nothing Then
                    Return Replace(TestRunner.GetType.FullName(), vbTab, "    ")
                ElseIf StubPath IsNot Nothing Then
                    Return Replace(StubPath.Last, vbTab, "    ")
                ElseIf Test IsNot Nothing Then
                    Return Replace(Test.CategoryPath.Last, vbTab, "    ")
                ElseIf Result IsNot Nothing Then
                    Return Misc.AddSeperator(": ", Result.Name, $"{Result.Result}", Result.Message)
                Else
                    'will not happen
                    Throw New NotSupportedException()
                End If
            End Get
        End Property

        Public ReadOnly Property CanBeChecked As Boolean
            Get
                'results cannot be checked
                Return Result Is Nothing
            End Get
        End Property

        Public Function GetTests() As List(Of i00CodeLib.i00Debug.TestRunner.Test)
            Static _Cache As List(Of i00CodeLib.i00Debug.TestRunner.Test) = TestRunner.GetTests()
            Return _Cache
        End Function

        Public ReadOnly Property RootItem As DataObject
        Public ReadOnly Property Parent As DataObject

        Public Sub New(Parent As DataObject)
            If Parent Is Nothing Then
                RootItem = Me
            Else
                Me.Parent = Parent
                RootItem = Parent.RootItem
            End If
        End Sub

        Public ReadOnly Property HasChildren As Boolean
            Get
                If RootItem Is Me Then
                    Return True
                Else
                    Return Children.Any()
                End If
            End Get
        End Property

        Public Sub ClearChildCache()
            _Children = Nothing
        End Sub

        Public Function ChildrenLoaded() As Boolean
            Return _Children IsNot Nothing
        End Function

        Dim _Children As List(Of DataObject)
        Public ReadOnly Property Children As List(Of DataObject)
            Get
                If _Children Is Nothing Then
                    _Children = New List(Of DataObject)
                    If TestRunner IsNot Nothing OrElse StubPath IsNot Nothing Then
                        Dim Tests = RootItem.GetTests().AsEnumerable()
                        Dim TestRunnerStubPath = Me.StubPath
                        If TestRunnerStubPath Is Nothing Then
                            TestRunnerStubPath = {}
                        Else
                            Tests = Tests.Where(Function(x) Misc.IEnumerableStartMatchCount(x.CategoryPath, Me.StubPath) = TestRunnerStubPath.Length)
                        End If
                        'now eliminate all tests that are multiple directories deeper
                        Dim TestsByType = Tests.Select(Function(x) New With {.IsTest = x.CategoryPath.Length = TestRunnerStubPath.Length + 1,
                                                                             .Test = x}).
                                                ToArray()
                        For Each iStub In TestsByType.Where(Function(x) x.IsTest = False).
                                                      Select(Function(x) x.Test.CategoryPath(TestRunnerStubPath.Length)).
                                                      Distinct()
                            _Children.Add(New DataObject(Me) With {.StubPath = TestRunnerStubPath.Concat({iStub}).ToArray()})
                        Next
                        For Each iTest In TestsByType.Where(Function(x) x.IsTest).
                                                      Select(Function(x) x.Test)
                            _Children.Add(New DataObject(Me) With {.Test = iTest})
                        Next
                    ElseIf Test IsNot Nothing Then
                        If Test.Results IsNot Nothing Then
                            For Each iResult In Test.Results
                                _Children.Add(New DataObject(Me) With {.Result = iResult})
                            Next
                        End If
                    ElseIf Result IsNot Nothing Then
                        'results can't have children
                    Else
                        'will not happen
                        Throw New NotSupportedException()
                    End If
                End If
                Return _Children
            End Get
        End Property

        'Public Property Checked As Boolean
        Public Property Test As i00CodeLib.i00Debug.TestRunner.Test
        Public Property Result As i00CodeLib.i00Debug.TestRunner.Test.Result
        Public Property TestRunner As i00CodeLib.i00Debug.TestRunner
        Public Property StubPath As String()
    End Class

    Private Sub tsiRunSelected_Click(sender As Object, e As EventArgs) Handles tsiRunSelected.Click

        'first lets clear all of the existing results
        Dim AllLoadedDataObjects = tvTests.Roots.OfType(Of DataObject).
                                                 SelectMany(Function(x) x.Recurse(Function(y) If(y.ChildrenLoaded(), y.Children, Nothing), True))
        For Each DataObject In AllLoadedDataObjects.Where(Function(x) (x.Test?.Results?.Any()).GetValueOrDefault())
            DataObject.ClearChildCache()
            DataObject.Test.Results?.Clear()
        Next
        tvTests.RebuildAll(True)

        'now to run the tests
        StartSelectedTests()


    End Sub

    Private Sub StartSelectedTests()
        tRunTests?.Abort()

        tsiStop.Visible = True
        tRunTests = i00CodeLib.i00Debug.Thread.Create("UnitTests",
                                                      Sub()
                                                          For Each iDataObject In GetSelected(SelectionTypes.Tests)
                                                              iDataObject.Test.Run()
                                                              iDataObject.ClearChildCache()
                                                          Next
                                                          Me.Invoke(Sub()
                                                                        PostTestsFinishedRunning()
                                                                    End Sub)
                                                      End Sub,
                                                      New i00CodeLib.i00Debug.Thread.ThreadOptions() With {.AllowAbort = False,
                                                                                                           .CloseWithApp = True})

        tRunTests.Start()
        tvTests.RebuildAll(True)
    End Sub

    Public Sub PostTestsFinishedRunning()
        tvTests.RebuildAll(True)
        tsiStop.Visible = False

        Dim FirstErrorNode As DataObject = Nothing

        Dim SelectedTests = GetSelected(SelectionTypes.Tests)
        Dim NodesToExpand = New List(Of DataObject)
        For Each iResult In SelectedTests.SelectMany(Function(x) x.Children.Where(Function(y) y.Result.Result = i00CodeLib.i00Debug.TestRunner.ResultTypes.Failure OrElse y.Result.Result = i00CodeLib.i00Debug.TestRunner.ResultTypes.Warning))
            If FirstErrorNode Is Nothing Then FirstErrorNode = iResult

            'color the node
            Select Case iResult.Result.Result
                Case i00CodeLib.i00Debug.TestRunner.ResultTypes.Warning
                    iResult.TestItemForeColor = Color.Yellow
                Case i00CodeLib.i00Debug.TestRunner.ResultTypes.Failure
                    iResult.TestItemForeColor = Color.Red
            End Select

            'expand to this node
            Dim ResultPath = iResult.Recurse(Function(x) {x.Parent}, False).Reverse().ToArray()
            NodesToExpand.AddRange(ResultPath)
        Next
        NodesToExpand = NodesToExpand.Distinct().ToList()
        For Each iNode In NodesToExpand
            tvTests.Expand(iNode)
        Next

        If FirstErrorNode IsNot Nothing Then
            tvTests.EnsureModelVisible(FirstErrorNode)
            tvTests.SelectedObject = FirstErrorNode
        End If

        'Not 100% how to show this .. would we want the test results .. or the sub test results??

        'Dim PassedTestCount = SelectedTests.Where(Function(x) x.Children.All(Function(y) y.Result.Result = i00CodeLib.i00Debug.TestRunner.ResultTypes.OK OrElse y.Result.Result = i00CodeLib.i00Debug.TestRunner.ResultTypes.Warning)).Count()
        'tsiResults.Text = $"{PassedTestCount} / {SelectedTests.Count} {Language.English.PluraliseIf("test", SelectedTests.Count)} passed"
        'tsiResults.Visible = True

    End Sub

    Dim tRunTests As System.Threading.Thread

    Private Enum SelectionTypes
        ''' <summary>
        ''' Returns all items that are checked in the tree (including unloaded nodes)
        ''' </summary>
        SelectedNodes
        ''' <summary>
        ''' Returns the selected node parents only - items checked under a parent that is checked will not be returned
        ''' </summary>
        SaveItems
        ''' <summary>
        ''' Returns checked test items only
        ''' </summary>
        Tests
    End Enum

    Private Function GetSelected(SelectionType As SelectionTypes) As DataObject()
        Dim DataObjects = New List(Of DataObject)
        Dim CheckedTreeItems = tvTests.CheckedObjects.OfType(Of DataObject)
        Select Case SelectionType
            Case SelectionTypes.SaveItems
                Dim ObjectsToSave = CheckedTreeItems.ToArray()
                'remove all of the redundant children (if a parent is checked then we don't require the sub items to also be saved!)
                DataObjects = ObjectsToSave.Where(Function(x) ObjectsToSave.Contains(x.Parent) = False).
                                            ToList()
            Case SelectionTypes.Tests, SelectionTypes.SelectedNodes
                For Each iDataObject In CheckedTreeItems
                    For Each iSubDataObject In iDataObject.Recurse(Function(x) x.Children, True, False)
                        If SelectionType <> SelectionTypes.Tests OrElse iSubDataObject.Test IsNot Nothing Then
                            DataObjects.Add(iSubDataObject)
                        End If
                    Next
                Next
            Case Else
                'should not happen
                Throw New NotSupportedException()
        End Select
        Return DataObjects.Distinct.ToArray()
    End Function

    Dim RowColors As New Dictionary(Of DataObject, Color?)
    Private Sub tvTests_FormatCell(sender As Object, e As FormatCellEventArgs) Handles tvTests.FormatCell
        Dim DataObject = DirectCast(e.Item.RowObject, DataObject)
        If e.Column Is colTest Then
            If DataObject.TestItemForeColor.HasValue Then
                e.Item.ForeColor = Drawing.BlendColor(DataObject.TestItemForeColor.Value, e.Item.ForeColor)
                e.Item.SelectedForeColor = Drawing.BlendColor(DataObject.TestItemForeColor.Value, e.Item.SelectedForeColor.GetValueOrDefault(tvTests.SelectedForeColorOrDefault))
                e.Item.Font = e.Item.Font.MakeBold()
            End If
        End If
    End Sub

    Private Sub tsiSave_Click(sender As Object, e As EventArgs) Handles tsiSave.Click
        Try
            Dim SaveFile = GetBranchSaveFile()
            If SaveFile <> "" Then
                Dim ObjectsToSave = GetSelected(SelectionTypes.SaveItems)

                If ObjectsToSave.Any = False Then
                    'we need to delete the save file as we don't want any items ticked
                    If My.Computer.FileSystem.FileExists(SaveFile) Then
                        My.Computer.FileSystem.DeleteFile(SaveFile)
                    End If
                Else
                    Dim SaveLines As New List(Of String)
                    For Each item In ObjectsToSave
                        SaveLines.Add(Join(item.Recurse(Function(x) {x.Parent}, True).Reverse.Select(Function(x) x.Text).ToArray(), vbTab))
                    Next
                    My.Computer.FileSystem.WriteAllText(SaveFile, Join(SaveLines.ToArray, vbCrLf), False)
                End If
            End If
        Catch ex As Exception
            MsgBox(Me, i00CodeLib.Language.English.ReplaceAWithAn($"A {ex.GetType.FullName} occurred while saving: {ex.Message}"), MsgBoxStyle.Critical)
        End Try
    End Sub

    Private Sub tsiStop_Click(sender As Object, e As EventArgs) Handles tsiStop.Click
        tRunTests.Abort()
        tvTests.RebuildAll(True)
        tsiStop.Visible = False
    End Sub
End Class
