Public MustInherit Class TestRunner

    'Public NotInheritable Class TestEg
    '    Inherits TestRunner

    '    Protected Overrides Sub RunInternal()
    '        ResultsInternal.Add(New ResultListItem({"TestPart", "1"}, ResultTypes.OK, "OK test"))
    '        ResultsInternal.Add(New ResultListItem({"TestPart", "2"}, ResultTypes.Warning, "Warning test"))
    '        ResultsInternal.Add(New ResultListItem({"TestPart2"}, ResultTypes.Failure, "Failure test"))
    '        ResultsInternal.Add(New ResultListItem({"TestPart2"}, ResultTypes.Failure, "Failure test 2"))
    '    End Sub
    'End Class

    Public NotInheritable Class Test
        Public Sub Run()
            Results = New List(Of Result)
            Try
                Action.Invoke(New ResultCreator(Me))
                If Results.Any = False Then Results.Add(New Result())
            Catch ex As Exception
                Results.Add(New Result(ResultTypes.Failure, , $"{ex.GetType.Name}: {ex.Message}"))
            End Try
        End Sub
        Public ReadOnly Property CategoryPath As String()
        Public ReadOnly Property Action As Action(Of ResultCreator)
        Public Property Results As List(Of Result)
        Public Class Result
            Public Property Result As ResultTypes = ResultTypes.OK
            Public Property Name As String
            Public Property Message As String
            Friend Sub New(Optional Result As ResultTypes = ResultTypes.OK, Optional Name As String = Nothing, Optional Message As String = Nothing)
                Me.Result = Result
                Me.Name = Name
                Me.Message = Message
            End Sub
        End Class
        Friend Sub New(CategoryPath As String(), Action As Action(Of ResultCreator))
            Me.CategoryPath = CategoryPath
            Me.Action = Action
        End Sub

        Public NotInheritable Class ResultCreator
            Public ReadOnly Property Test As Test
            Public Sub Add(Optional Result As ResultTypes = ResultTypes.OK, Optional Name As String = Nothing, Optional Message As String = Nothing)
                Test.Results.Add(New Test.Result(Result, Name, Message))
            End Sub
            Friend Sub New(Test As Test)
                Me.Test = Test
            End Sub
        End Class
    End Class

    Public NotInheritable Class TestCreator
        Private ReadOnly Property TestList As List(Of Test)
        Public Sub Add(CategoryPath As String(), Action As Action(Of Test.ResultCreator))
            TestList.Add(New Test(CategoryPath, Action))
        End Sub
        Friend Sub New(TestList As List(Of Test))
            Me.TestList = TestList
        End Sub
    End Class

    Protected MustOverride Sub GetTestsInternal(Tests As TestCreator)

    Public Function GetTests() As List(Of Test)
        Static LastTestPath As String = Nothing
        Static Tests As List(Of Test)
        If LastTestPath Is Nothing OrElse LastTestPath <> TestPath & "" OrElse Tests Is Nothing Then
            Tests = New List(Of Test)
            GetTestsInternal(New TestCreator(Tests))
            Tests.OrderBy(Function(x) x.CategoryPath, New Misc.IEnumerableContentComparer(Of String()))

            LastTestPath = TestPath & ""
        End If
        Return Tests
    End Function

    Private Shared _TestPath As String
    Public Shared Property TestPath As String
        Get
            Return _TestPath
        End Get
        Set
            If _TestPath <> Value Then
                SyncLock AssemblyCache
                    AssemblyCache.Clear()
                    TypeCache.Clear()
                    MethodCache.Clear()
                    _TestPath = Value
                End SyncLock
            End If
        End Set
    End Property

    Private Shared AssemblyCache As New List(Of System.Reflection.Assembly)
    Public Shared Function GetTestAssemblies() As ObjectModel.ReadOnlyCollection(Of System.Reflection.Assembly)
        If AssemblyCache.Count = 0 Then
            'need to load cache
            SyncLock AssemblyCache
                'recheck this as could have waited for another to fill the cache!
                If AssemblyCache.Count = 0 Then
                    'TODO: make this only load the assemblies for the TestPath incase others were later loaded?
                    AssemblyCache.AddRange(PluginManager(Of TestRunner).LoadedAssembliesByPath.Select(Function(x) x.Value))
                End If
            End SyncLock
        End If
        Return AssemblyCache.AsReadOnly()
    End Function

    Public Shared Function GetAllTestTypes(Of T)() As IEnumerable(Of Type)
        Return GetAllTestTypes().Where(Function(x) GetType(T).IsAssignableFrom(x) AndAlso x.IsAbstract = False AndAlso x.IsVisible = True)
    End Function

    Private Shared TypeCache As New List(Of Type)
    Public Shared Function GetAllTestTypes() As ObjectModel.ReadOnlyCollection(Of Type)
        If TypeCache.Count = 0 Then
            'need to load cache
            SyncLock TypeCache
                'recheck this as could have waited for another to fill the cache!
                If TypeCache.Count = 0 Then
                    TypeCache.AddRange(GetTestAssemblies().AsParallel.
                                                           SelectMany(Function(x) x.GetTypes))
                End If
            End SyncLock
        End If
        Return TypeCache.AsReadOnly()
    End Function


    Private Shared MethodCache As New List(Of Reflection.MethodInfo)
    Public Shared Function GetAllTestMethods() As ObjectModel.ReadOnlyCollection(Of Reflection.MethodInfo)
        If MethodCache.Count = 0 Then
            'need to load cache
            SyncLock MethodCache
                'recheck this as could have waited for another to fill the cache!
                If MethodCache.Count = 0 Then
                    Dim Methods = GetAllTestTypes().SelectMany(Function(x) x.GetMethods(Reflection.BindingFlags.Instance Or Reflection.BindingFlags.Static Or Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Public)).
                                                    ToArray()
                    MethodCache.AddRange(Methods)
                End If
            End SyncLock
        End If
        Return MethodCache.AsReadOnly()
    End Function

    Public Shared Function GetPlugins(Optional predicate As Func(Of TestRunner, Boolean) = Nothing) As TestRunner()
        Dim OtherLocation = TestPath <> ""
        Dim Returner = PluginManager(Of TestRunner).CreatePlugins(TestPath).AsEnumerable()
        If OtherLocation Then
            'also test own location
            '.. can't use union as we are creating instances of TestRunner ... so instead lets concat...
            Returner = Returner.Concat(PluginManager(Of TestRunner).CreatePlugins().AsEnumerable())
            '... then get rid of duplicates based on the plugin's type
            '... do this based on name as we can have two different assembly locations - e.g. i00CodeLib exists 2x in each location!
            Returner = Returner.Distinct(Misc.NestedObjectEqualityComparer.Create(Function(x As TestRunner) x.GetType))
        End If

        If predicate IsNot Nothing Then
            Returner = Returner.Where(predicate)
        End If

        Return Returner.OrderBy(Function(x) x.GetType.FullName).
                        ToArray()
    End Function

    Public Enum ResultTypes
        OK
        Warning
        Failure
    End Enum

End Class
