Imports i00CodeLib

Friend NotInheritable Class Misc

    Private Sub New()

    End Sub

    Friend Shared Function ConvertToDotNetEntryData(DataObject As Object) As String
        'TODO: make this code format the data ... so string is enclosed in "" etc
        '... this can really only be basic types; as they are on attributes... so lets just handle string for now
        If DataObject Is Nothing Then
            Return "Nothing"
        ElseIf TypeOf DataObject Is String Then
            Return $"""{DataObject}"""
        Else
            Return $"{DataObject}"
        End If
    End Function

    Friend Shared Function MultiCast(o As Object, TypeTo As Type, Optional ThrowInvalidCastException As Boolean = True) As Object
        If o Is Nothing Then
            Return Nothing
        Else
            If TypeTo.IsAssignableFrom(o.GetType) Then
                'DirectCast support
                Return Convert.ChangeType(o, TypeTo)
            Else
                'Widening / Narrowing Operator support
                Dim FromMethods = o.GetType.GetMethods(Reflection.BindingFlags.Static Or Reflection.BindingFlags.Public Or Reflection.BindingFlags.FlattenHierarchy)
                Dim ToMethods = TypeTo.GetMethods(Reflection.BindingFlags.Static Or Reflection.BindingFlags.Public Or Reflection.BindingFlags.FlattenHierarchy)
                'take op_Implicit(Widening) in preference ... don't know why the framework lets you even do this
                '... but the framework allows the Widening / Narrowing operators to be used somewhat incorrectly (interchangeably)
                '... when one side widen operator matches a narrow operator on the other side it takes the wide one in preference
                Dim FromConverter = FromMethods.Union(ToMethods).
                                                Where(Function(x) (x.Name = "op_Implicit" OrElse x.Name = "op_Explicit") AndAlso
                                                                   x.ReturnType Is TypeTo AndAlso
                                                                   x.GetParameters.First.ParameterType Is o.GetType).
                                                OrderByDescending(Function(x) x.Name = "op_Implicit"). 'take op_Implicit(Widening) in preference
                                                FirstOrDefault
                If FromConverter IsNot Nothing Then
                    Return FromConverter.Invoke(Nothing, {o})
                Else
                    'TypeConverter support
                    Dim TypeConverter = System.ComponentModel.TypeDescriptor.GetConverter(o.GetType)
                    If TypeConverter IsNot Nothing Then
                        If TypeConverter.CanConvertTo(TypeTo) Then
                            Return TypeConverter.ConvertTo(o, TypeTo)
                        End If
                    End If
                    TypeConverter = System.ComponentModel.TypeDescriptor.GetConverter(TypeTo)
                    If TypeConverter IsNot Nothing Then
                        If TypeConverter.CanConvertFrom(o.GetType) Then
                            Return TypeConverter.ConvertFrom(o)
                        End If
                    End If
                End If
            End If
        End If

        If ThrowInvalidCastException Then
            Throw New InvalidCastException($"{o}({o.GetType.Name}) cannot be converted to {TypeTo}")
        Else
            Return Nothing
        End If
    End Function

    Public Shared Function AddSeperator(Seperator As String, ParamArray Values As String()) As String
        Return Join(Values.Where(Function(x) x <> "").ToArray, Seperator)
    End Function

    Public Shared Function GetGitPath() As String
        Static Attempted As Boolean
        Static _cache As String
        If Attempted = False Then
            Try
                Dim DirInfo = FileIO.FileSystem.GetDirectoryInfo(System.Reflection.Assembly.GetEntryAssembly.Location)
                Do
                    Dim GitPath = IO.Path.Combine(DirInfo.FullName, ".git")
                    If FileIO.FileSystem.DirectoryExists(GitPath) Then
                        'this is the git path...
                        _cache = DirInfo.FullName
                        Exit Do
                    Else
                        If DirInfo Is Nothing Then
                            'Git not found :(
                            Exit Do
                        End If
                        DirInfo = DirInfo.Parent
                    End If
                Loop
            Catch
                'permissions etc - non crit
            Finally
                Attempted = True
            End Try
        End If
        Return _cache
    End Function

    Public Shared Function GetRemote() As String
        Static Attempted As Boolean
        Static _cache As String
        If Attempted = False Then
            Try
                Dim GitPath = Misc.GetGitPath()
                If GitPath <> "" Then
                    Dim GitHeadFile = IO.Path.Combine(GitPath, ".git", "HEAD")
                    If My.Computer.FileSystem.FileExists(GitHeadFile) Then
                        Dim GitHeadInfo = My.Computer.FileSystem.ReadAllText(GitHeadFile)
                        Dim Ref = System.Text.RegularExpressions.Regex.Match(GitHeadInfo, "(?<=^ref:\s*\b).*?(?=\s*$)", System.Text.RegularExpressions.RegexOptions.Multiline)
                        If Ref.Success Then
                            _cache = Ref.Value
                        End If
                    End If
                End If
            Catch
                'permissions etc - non crit
            Finally
                Attempted = True
            End Try
        End If
        Return _cache
    End Function

    Public Shared Function GetGitJobNo() As Integer?
        Static Attempted As Boolean
        Static _cache As Integer?
        If Attempted = False Then
            Try
                Dim Remote = GetRemote()
                If Remote <> "" Then
                    Dim Job = Text.RegularExpressions.Regex.Match(Remote, "(?<!\d.*)\d+(?!.*/)")
                    If Job.Success Then
                        _cache = CInt(Job.Value)
                    End If
                End If
            Catch
                'int overflow etc - non crit
            Finally
                Attempted = True
            End Try
        End If
        Return _cache
    End Function

    Public Shared Function IEnumerableStartMatchCount(Of T)(IEnumerableToCheck As IEnumerable(Of T), IEnumerableStub As IEnumerable(Of T)) As Integer
        Dim Returner As Integer = 0
        For Each item In IEnumerableToCheck.IndexPositionJoin(IEnumerableStub, Function(x, y) New With {.Check = x, .Stub = y})
            If item.Stub Is Nothing Then
                'we have run through the stub with all items matching to this point
                Exit For
            ElseIf item.Check Is Nothing Then
                'the stub has more items that the check - so we can't start with it
                Exit For
            Else
                If Object.Equals(item.Stub, item.Check) Then
                    'same sofar
                    Returner += 1
                Else
                    'items are different
                    Exit For
                End If
            End If
        Next
        Return Returner
    End Function

End Class
