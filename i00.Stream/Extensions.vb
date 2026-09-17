Friend Module Extensions

    Private ReadOnly FormatFileSizeLimits As Long() = New Long() {1099511627776, 1073741824, 1048576, 1024}
    Private ReadOnly FormatFileSizeUnits As String() = New String() {"TB", "GB", "MB", "KB"}
    <System.Runtime.CompilerServices.Extension>
    Friend Function FormatFileSizeFromBytes(size As Long, Optional DecimalPlaces As Integer = 1) As String
        For Index As Integer = 0 To FormatFileSizeLimits.Length - 1
            If size >= FormatFileSizeLimits(Index) Then
                Return String.Format(
                    "{0:#,##0." & New String("#"c, DecimalPlaces) & "} " & FormatFileSizeUnits(Index),
                    size / CDbl(FormatFileSizeLimits(Index)))
            End If
        Next

        Return $"{size} B"
    End Function

    <System.Runtime.CompilerServices.Extension>
    Friend Function FormatFileSizeFromBytes(size As Integer) As String
        Return FormatFileSizeFromBytes(CLng(size))
    End Function

End Module