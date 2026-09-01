Namespace Shell

    Partial Public Class Window

        <System.Runtime.InteropServices.DllImport("User32.dll")>
        Private Shared Function IsIconic(ByVal handle As IntPtr) As Boolean
        End Function

        Public Shared Function IsMinimized(window As IWin32Window) As Boolean
            Return IsMinimized(window.Handle)
        End Function

        Public Shared Function IsMinimized(handle As IntPtr) As Boolean
            Return IsIconic(handle)
        End Function

    End Class

End Namespace