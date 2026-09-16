Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

Public NotInheritable Class ClipboardMessageFilter
    Implements IMessageFilter
    Implements IDisposable

    Private Const WM_CLIPBOARDUPDATE As Integer = &H31D

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function AddClipboardFormatListener(HWnd As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function RemoveClipboardFormatListener(HWnd As IntPtr) As Boolean
    End Function

    Private WithEvents AttachedForm As Form

    Private _WindowHandle As IntPtr
    Private _MessageFilterInstalled As Boolean
    Private _Disposed As Boolean

    Public Event ClipboardChanged As EventHandler

    Public Sub Attach(Window As Form)
        If Window Is Nothing Then Throw New ArgumentNullException(NameOf(Window))
        If _Disposed Then Throw New ObjectDisposedException(NameOf(ClipboardMessageFilter))

        If AttachedForm IsNot Nothing Then
            Throw New InvalidOperationException("The clipboard message filter is already attached.")
        End If

        AttachedForm = Window

        If AttachedForm.IsDisposed Then
            AttachedForm = Nothing
            Throw New ObjectDisposedException(NameOf(Window))
        End If

        If AttachedForm.IsHandleCreated Then
            Install(AttachedForm.Handle)
        End If
    End Sub

    Private Sub Install(WindowHandle As IntPtr)
        If _WindowHandle <> IntPtr.Zero Then Return

        If WindowHandle = IntPtr.Zero Then
            Throw New InvalidOperationException("The window handle has not been created.")
        End If

        If AddClipboardFormatListener(WindowHandle) = False Then
            Throw New Win32Exception(Marshal.GetLastWin32Error())
        End If

        _WindowHandle = WindowHandle

        If _MessageFilterInstalled = False Then
            Application.AddMessageFilter(Me)
            _MessageFilterInstalled = True
        End If
    End Sub

    Private Sub Uninstall()
        If _WindowHandle = IntPtr.Zero Then Return

        RemoveClipboardFormatListener(_WindowHandle)
        _WindowHandle = IntPtr.Zero
    End Sub

    Public Function PreFilterMessage(ByRef M As Message) As Boolean Implements IMessageFilter.PreFilterMessage
        If M.Msg = WM_CLIPBOARDUPDATE AndAlso M.HWnd = _WindowHandle Then
            RaiseEvent ClipboardChanged(Me, EventArgs.Empty)
        End If

        Return False
    End Function

    Private Sub AttachedForm_HandleCreated(Sender As Object, E As EventArgs) Handles AttachedForm.HandleCreated
        Install(AttachedForm.Handle)
    End Sub

    Private Sub AttachedForm_HandleDestroyed(Sender As Object, E As EventArgs) Handles AttachedForm.HandleDestroyed
        Uninstall()
    End Sub

    Private Sub AttachedForm_Disposed(Sender As Object, E As EventArgs) Handles AttachedForm.Disposed
        Dispose()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _Disposed Then Return

        _Disposed = True

        Uninstall()

        If _MessageFilterInstalled Then
            Application.RemoveMessageFilter(Me)
            _MessageFilterInstalled = False
        End If

        AttachedForm = Nothing
    End Sub

End Class
