'7/4/2021
' - Initial
'9/4/2021
' - Added ReadLineOptions
' - Moved highlighting to ReadLineOptions
' - Added support for Undo / redo
' - Fixed bug when resizing console
' - Fixed bug with clearing pixels resulting in "pushing" when console was resized
' - Added standard jump support (currently only supports jump back as there Is no pattern to jump forward yet)
' - Fixed a bug when completing where it would only goto the next line down from the cursor, rather than the line following the data
'10/4/2020
' - Added standard jump forward pattern
' - Added ability to delete as well as backspace
Public NotInheritable Class ConsoleEx
    Private Sub New()
    End Sub

#Region "APIs"

    Private Enum STD_HANDLES
        STD_INPUT_HANDLE = -10
        STD_OUTPUT_HANDLE = -11
        STD_ERROR_HANDLE = -12
    End Enum

    <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetStdHandle(ByVal nStdHandle As STD_HANDLES) As IntPtr
    End Function

#End Region

    Public Shared ReadOnly Property OutputHandle As IntPtr

    Private Shared Event Initialize()

    Shared Sub New()
        OutputHandle = GetStdHandle(STD_HANDLES.STD_OUTPUT_HANDLE)

        RaiseEvent Initialize()
    End Sub

End Class