Public NotInheritable Class SimpleTest
    Inherits TestRunner
    Protected Overrides Sub GetTestsInternal(Tests As TestCreator)
        Dim TestMethods = GetAllTestMethods().Select(Function(x) New With {.Method = x,
                                                                         .UnitTests = x.GetCustomAttributes(False).OfType(Of SimpleTestAttribute)}).
                                            Where(Function(x) x.UnitTests.Any()).
                                            Select(Function(x) New With {x.Method,
                                                                         x.UnitTests,
                                                                         .TypeNames = x.Method.DeclaringType.Recurse(Function(y) {y.DeclaringType}, True).Select(Function(y) y.Name).Reverse().ToArray(),
                                                                         .TypeName = Join(.TypeNames, ".")}).
                                            OrderBy(Function(x) x.TypeName).
                                            ThenBy(Function(x) x.Method.Name).
                                            ToArray()

        For Each testMethod In TestMethods
            Dim MethodParameters = testMethod.Method.GetParameters()

            Dim ThisTree As New List(Of String) From {testMethod.Method.DeclaringType.Assembly.GetName.Name}
            ThisTree.AddRange(testMethod.TypeNames)
            ThisTree.Add($"{testMethod.Method.Name}({Join(MethodParameters.Select(Function(x) $"{x.Name} As {x.ParameterType.Name}").ToArray, ", ")})")

            Dim ReturnsData = testMethod.Method.ReturnType IsNot GetType(Void)

            Tests.Add(ThisTree.ToArray(),
                         Sub(Result)
                             Dim FunctionText As String = Nothing

                             If testMethod.Method.IsStatic = False Then
                                 Result.Add(ResultTypes.Failure, , "Cannot run on a method that is not shared")
                             ElseIf testMethod.Method.ContainsGenericParameters Then
                                 Result.Add(ResultTypes.Failure, , "Cannot run on a method that contains generic parameters")
                             Else
                                 For Each test In testMethod.UnitTests
                                     FunctionText = ""
                                     If test.InputParameters IsNot Nothing Then
                                         FunctionText = $"({Join(test.InputParameters.Select(Function(x) Misc.ConvertToDotNetEntryData(x)).ToArray, ", ")})"
                                     End If

                                     Dim ExpectedResult As Object = Nothing
                                     If test.ExpectedValueSet Then
                                         ExpectedResult = Misc.MultiCast(test.ExpectedValue, testMethod.Method.ReturnType)
                                         FunctionText = $"{If(FunctionText = "", "()", FunctionText)} = {Misc.ConvertToDotNetEntryData(ExpectedResult)}"
                                     End If

                                     If test.ExpectedValueSet AndAlso ReturnsData = False Then
                                         Result.Add(ResultTypes.Failure, FunctionText, $"An {NameOf(SimpleTestAttribute.ExpectedValue)} was set, but this method is not a function")
                                     ElseIf MethodParameters.Count() <> test.InputParameters?.Count() Then
                                         Result.Add(ResultTypes.Failure, FunctionText, $"{test.InputParameters?.Count()} {NameOf(SimpleTestAttribute.InputParameters)} were specified in {NameOf(SimpleTestAttribute)}, but the method only accepts {MethodParameters.Count()}")
                                     Else
                                         Try
                                             'ConvertedTypeParameters exists as the reflection .Invoke method is very strict and will not allow an Integer to be inferred to a Decimal for eg.
                                             Dim ConvertedTypeParameters As List(Of Object) = Nothing
                                             If test.InputParameters?.Any Then
                                                 ConvertedTypeParameters = New List(Of Object)
                                                 For Each param In test.InputParameters.IndexPositionJoin(MethodParameters, Function(x, y) New With {.TestParameter = x, .MethodParameter = y})
                                                     ConvertedTypeParameters.Add(Misc.MultiCast(param.TestParameter, param.MethodParameter.ParameterType))
                                                 Next
                                             End If
                                             Dim TestResult = testMethod.Method.Invoke(Nothing, ConvertedTypeParameters?.ToArray())
                                             If test.ExpectedValueSet Then
                                                 If Object.Equals(TestResult, ExpectedResult) Then
                                                     Result.Add(ResultTypes.OK, FunctionText)
                                                 Else
                                                     Result.Add(ResultTypes.Failure, FunctionText, $"Return was not expected: {Misc.ConvertToDotNetEntryData(TestResult)}")
                                                 End If
                                             Else
                                                 Result.Add(ResultTypes.OK, FunctionText)
                                             End If
                                         Catch ex As System.Reflection.TargetInvocationException
                                             If TypeOf ex.InnerException Is WarningException Then
                                                 Result.Add(ResultTypes.Warning, FunctionText, $"{ex.InnerException.Message}")
                                             Else
                                                 Result.Add(ResultTypes.Failure, FunctionText, $"{ex.InnerException.GetType.Name}: {ex.InnerException.Message}")
                                             End If
                                         Catch ex As Exception
                                             If ex.GetType Is GetType(Exception) Then
                                                 Result.Add(ResultTypes.Failure, FunctionText, $"{ex.Message}")
                                             Else
                                                 Result.Add(ResultTypes.Failure, FunctionText, $"{ex.GetType.Name}: {ex.Message}")
                                             End If
                                         End Try
                                     End If
                                 Next
                             End If
                         End Sub)
        Next
    End Sub

    Public Class WarningException
        Inherits Exception
        Public Sub New(Message As String)
            MyBase.New(Message)
        End Sub
    End Class

End Class

<AttributeUsage(AttributeTargets.Method, AllowMultiple:=True, Inherited:=True)>
Public Class SimpleTestAttribute
    Inherits Attribute

    ''' <summary>
    ''' Indicates that this method is for unit testing; a failure will occur if an unhandled Exception is thrown
    ''' </summary>
    Public Sub New()

    End Sub

    ''' <summary>
    ''' Indicates that this method is for unit testing
    ''' </summary>
    ''' <param name="InputParameters">Parameters that will be used to test the method</param>
    Public Sub New(InputParameters() As Object)
        Me.InputParameters = InputParameters
    End Sub

    ''' <summary>
    ''' Indicates that this method is for unit testing
    ''' </summary>
    ''' <param name="InputParameters">Parameters that will be used to test the method</param>
    ''' <param name="ExpectedValue">The expected result, other results being returned will result in a failure</param>
    Public Sub New(InputParameters() As Object, ExpectedValue As Object)
        Me.InputParameters = InputParameters
        Me.ExpectedValue = ExpectedValue
        ExpectedValueSet = True
    End Sub

    ''' <summary>
    ''' Indicates that this method is for unit testing
    ''' </summary>
    ''' <param name="ExpectedValue">The expected result, other results being returned will result in a failure</param>
    Public Sub New(ExpectedValue As Object)
        Me.ExpectedValue = ExpectedValue
        ExpectedValueSet = True
    End Sub


    Friend InputParameters As Object()
    Friend ExpectedValue As Object
    Friend ExpectedValueSet As Boolean
End Class