Public Class AlphaNumericSorter
    Implements IComparer(Of String)

    Declare Unicode Function StrCmpLogicalW Lib "shlwapi.dll" (ByVal s1 As String, ByVal s2 As String) As Int32

    Public Function Compare(ByVal x As String, ByVal y As String) As Integer Implements IComparer(Of String).Compare
        'This will error otherwise: (if the string is nothing)
        x = x & ""
        y = y & ""

        Return StrCmpLogicalW(x, y)
    End Function

End Class