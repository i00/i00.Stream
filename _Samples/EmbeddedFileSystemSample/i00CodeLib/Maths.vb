Public NotInheritable Class Maths

    Private Sub New()

    End Sub

    Public Shared Function Cap(Of T As IComparable)(Value As T, Min As T, Max As T) As T
        'flip if needed:
        FlipIf(Min.CompareTo(Max) > 0, Min, Max)
        If Value.CompareTo(Min) < 0 Then
            Return Min
        Else
            If Value.CompareTo(Max) > 0 Then
                Return Max
            Else
                Return Value
            End If
        End If
    End Function

    Public Shared Sub FlipIf(Of T)(Validator As Boolean, ByRef Value1 As T, ByRef Value2 As T)
        If Validator Then
            Flip(Value1, Value2)
        End If
    End Sub

    Public Shared Sub Flip(Of T)(ByRef Value1 As T, ByRef Value2 As T)
        Dim Temp = Value1
        Value1 = Value2
        Value2 = Temp
    End Sub

End Class