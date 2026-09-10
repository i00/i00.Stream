Imports System.Text.RegularExpressions

Friend NotInheritable Class Misc

    Private Sub New()

    End Sub

    Friend NotInheritable Class LambdaComparable(Of T)
        Implements IComparer(Of T)

        Dim Comparer As Func(Of T, T, Integer)

        Friend Sub New(Comparer As Func(Of T, T, Integer))
            Me.Comparer = Comparer
        End Sub

        Public Function Compare(x As T, y As T) As Integer Implements IComparer(Of T).Compare
            Return Comparer.Invoke(x, y)
        End Function
    End Class

    Public Shared Function getValueFromString(ByVal theString As String, ByVal theField As String, Optional ByVal DelimiterMajor As String = ";", Optional ByVal DelimiterMinor As String = ":") As String
        Dim escDelimiterMinor = Regex.Escape(DelimiterMinor)
        Dim escDelimiterMajor = Regex.Escape(DelimiterMajor)

        Dim mc = Regex.Match(theString, $"(?<={escDelimiterMajor}{theField}(?={escDelimiterMinor})).*?(?={escDelimiterMajor}|$)", RegexOptions.IgnoreCase)

        If mc.Success = False Then
            getValueFromString = Nothing
        Else
            getValueFromString = mc.Value

            If getValueFromString <> "" AndAlso getValueFromString.StartsWith(DelimiterMinor) Then
                getValueFromString = getValueFromString.Substring(Len(DelimiterMinor))
            End If
        End If
    End Function

    Public Shared Function getValuesFromString(ByVal theString As String, ByVal theField As String, Optional ByVal DelimiterMajor As String = ";", Optional ByVal DelimiterMinor As String = ":") As String()
        Dim escDelimiterMinor = Regex.Escape(DelimiterMinor)
        Dim escDelimiterMajor = Regex.Escape(DelimiterMajor)

        Dim Pattern = $"(?<={escDelimiterMajor}{theField}{escDelimiterMinor}).*?(?={escDelimiterMajor}|$)"
        Dim mc = Regex.Matches(theString, Pattern, RegexOptions.IgnoreCase).OfType(Of Match)
        Return mc.Select(Function(x) x.Value).ToArray
    End Function

    Public Shared Function setValueInString(ByVal theString As String, ByVal theField As String, Value As String, Optional ByVal DelimiterMajor As String = ";", Optional ByVal DelimiterMinor As String = ":") As String
        Dim escDelimiterMajor = Regex.Escape(DelimiterMajor)

        Dim Replacements As Boolean
        setValueInString = Regex.Replace(theString, $"(?<={escDelimiterMajor}{theField}).*?(?={escDelimiterMajor}|$)", Function(m)
                                                                                                                           Replacements = True
                                                                                                                           Return If(Value = "", "", DelimiterMinor & Value)
                                                                                                                       End Function, RegexOptions.IgnoreCase)
        If Replacements = False Then
            'replacements were not made ... lets add it to the end...
            setValueInString = setValueInString & DelimiterMajor & theField & If(Value = "", "", DelimiterMinor & Value)
        End If
    End Function

    Public Shared Function getValueFromStringExists(ByVal theString As String, ByVal theField As String, Optional ByVal DelimiterMajor As String = ";", Optional ByVal DelimiterMinor As String = ":") As Boolean
        theString = DelimiterMajor & theString & DelimiterMajor

        DelimiterMinor = Regex.Escape(DelimiterMinor)
        DelimiterMajor = Regex.Escape(DelimiterMajor)

        Dim Pattern = $"{DelimiterMajor}{theField}({DelimiterMajor}|{DelimiterMinor}|$)"
        Return Regex.IsMatch(theString, Pattern, RegexOptions.IgnoreCase)
    End Function

    Public NotInheritable Class NestedObjectEqualityComparer

        Private Sub New()

        End Sub

        Public Shared Function Create(Of T, TOut)(ObjectGetterForCompare As Func(Of T, TOut)) As NestedObjectEqualityComparer(Of T, TOut)
            Return New NestedObjectEqualityComparer(Of T, TOut)(ObjectGetterForCompare)
        End Function
    End Class

    Public NotInheritable Class NestedObjectEqualityComparer(Of T, TOut)
        Implements IEqualityComparer(Of T)

        Public Property ObjectGetterForCompare As Func(Of T, TOut)
        Friend Sub New(ObjectGetterForCompare As Func(Of T, TOut))
            Me.ObjectGetterForCompare = ObjectGetterForCompare
        End Sub
        Public Function IEqualityComparer_Equals(x As T, y As T) As Boolean Implements IEqualityComparer(Of T).Equals
            Return Object.Equals(ObjectGetterForCompare(x), ObjectGetterForCompare(y))
        End Function

        Public Function IEqualityComparer_GetHashCode(obj As T) As Integer Implements IEqualityComparer(Of T).GetHashCode
            Return (ObjectGetterForCompare(obj)?.GetHashCode()).GetValueOrDefault(0)
        End Function
    End Class

    Public Class IEnumerableContentComparer(Of T As IEnumerable(Of IComparable))
        Implements IComparer(Of T)

        Public Function Compare(x As T, y As T) As Integer Implements IComparer(Of T).Compare
            For Each item In IndexPositionJoin(x, y, Function(xx, yy) New With {.x = xx, .y = yy})
                Dim Result = If(item.x IsNot Nothing, item.x.CompareTo(item.y), item.y.CompareTo(item.x) * -1)
                If Result <> 0 Then
                    Return Result
                End If
            Next
            Return 0
        End Function
    End Class


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

    Public Shared Function MakePathAbsolute(File As String, Optional Path As String = "") As String
        If Path = "" Then
            'use application path
            Path = IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly.Location)
        End If

        Return IO.Path.GetFullPath(IO.Path.Combine(Path, File))
    End Function

End Class
