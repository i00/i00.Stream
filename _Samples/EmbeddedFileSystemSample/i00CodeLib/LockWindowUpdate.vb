Public Module extLockWindowUpdate

    <System.Runtime.InteropServices.DllImport("user32.dll")> _
    Private Function SendMessage(hWnd As IntPtr, wMsg As Int32, wParam As Boolean, lParam As Int32) As Integer
    End Function
    Private Const WM_SETREDRAW As Integer = 11

    Public Property AllowLockWindowUpdate As Boolean = True

    <System.Runtime.CompilerServices.Extension()> _
    Public Sub LockWindowUpdate(Control As Control)
        LockWindowUpdate(Control.Handle)
    End Sub

    <System.Runtime.CompilerServices.Extension()>
    Public Sub UnlockWindowUpdate(Control As Control)
        UnlockWindowUpdate(Control.Handle)
    End Sub

    <System.Runtime.CompilerServices.Extension()>
    Public Sub LockWindowUpdate(Handle As IntPtr)
        If AllowLockWindowUpdate Then
            SendMessage(Handle, WM_SETREDRAW, False, 0)
        End If
    End Sub

    <System.Runtime.CompilerServices.Extension()>
    Public Sub UnlockWindowUpdate(Handle As IntPtr)
        If AllowLockWindowUpdate Then
            SendMessage(Handle, WM_SETREDRAW, True, 0)
        End If
    End Sub

End Module
