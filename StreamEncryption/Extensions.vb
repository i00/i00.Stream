Public Module Extensions

    Private FormatFileSizeLimits As Long() = New Long() {1099511627776, 1073741824, 1048576, 1024}
    Private FormatFileSizeUnits As String() = New String() {"TB", "GB", "MB", "KB"}
    <System.Runtime.CompilerServices.Extension>
    Friend Function FormatFileSizeFromBytes(size As Long) As String

        For i As Integer = 0 To FormatFileSizeLimits.Length - 1
            If size >= FormatFileSizeLimits(i) Then
                Return [String].Format("{0:#,##0.#} " + FormatFileSizeUnits(i), (size / FormatFileSizeLimits(i)))
            End If
        Next

        Return "< 1 KB"
    End Function

    <System.Runtime.CompilerServices.Extension>
    Friend Function FormatFileSizeFromBytes(size As Integer) As String
        Return FormatFileSizeFromBytes(CLng(size))
    End Function

End Module