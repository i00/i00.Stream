Imports System.Runtime.InteropServices

Partial Class ConsoleEx

#Region "APIs"

    <DllImport("kernel32.dll", CharSet:=CharSet.Auto, SetLastError:=True)>
    Private Shared Function FillConsoleOutputCharacter(ByVal hConsoleOutput As IntPtr, ByVal character As Char, ByVal nLength As Integer, ByVal dwWriteCoord As COORD, <Out> ByRef pNumCharsWritten As Integer) As Boolean

    End Function
    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function FillConsoleOutputAttribute(ByVal hConsoleOutput As IntPtr, ByVal wColorAttribute As Short, ByVal numCells As Integer, ByVal startCoord As COORD, <Out> ByRef pNumBytesWritten As Integer) As Boolean

    End Function
    <StructLayout(LayoutKind.Sequential)>
    Private Structure COORD
        Public X As Short
        Public Y As Short
        Public Sub New(ByVal X As Short, ByVal Y As Short)
            Me.X = X
            Me.Y = Y
        End Sub
    End Structure

#End Region

    Public Shared Function ClearCharacters(Count As Integer, Optional X As Integer? = Nothing, Optional Y As Integer? = Nothing) As Boolean
        'need to fill the actual console with Chr 32 the console behaves properly when resized

        If X.HasValue = False Then X = Console.CursorLeft
        If Y.HasValue = False Then Y = Console.CursorTop

        Dim cord = New COORD With {.X = CShort(X), .Y = CShort(Y)}
        Dim CellsWritten = 0
        Dim success = FillConsoleOutputCharacter(OutputHandle, Chr(32), Count, cord, CellsWritten)
        If success = False Then Return False
        success = FillConsoleOutputAttribute(OutputHandle, 0, Count, cord, CellsWritten)
        Return success
    End Function

    Public Shared Function GetCursorOffset(Offset As Integer, Optional X As Integer? = Nothing, Optional Y As Integer? = Nothing) As Point
        If X.HasValue = False Then X = Console.CursorLeft
        If Y.HasValue = False Then Y = Console.CursorTop

        Offset += X.Value
        Dim BufferWidth = Console.BufferWidth
        Dim XFinal = Offset Mod BufferWidth
        If XFinal < 0 Then XFinal = BufferWidth + XFinal
        Dim YFinal = Y.Value + (Offset \ BufferWidth) + If(Offset < 0, -1, 0)
        Return New Point(XFinal, YFinal)
    End Function

    Private Class HoldCursor
        Implements IDisposable
        ReadOnly Left As Integer
        ReadOnly Top As Integer
        Public Sub New()
            Me.Left = Console.CursorLeft
            Me.Top = Console.CursorTop
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            Console.SetCursorPosition(Left, Top)
        End Sub
    End Class

End Class