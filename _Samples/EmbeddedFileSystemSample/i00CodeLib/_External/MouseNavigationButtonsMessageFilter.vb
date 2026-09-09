Public Class MouseNavigationButtonsMessageFilter
    Implements IMessageFilter

    Private WithEvents Form As Form

    Private BackAction As Action
    Private ForwardAction As Action
    Public Sub New(Form As Form, BackAction As Action, ForwardAction As Action)
        Me.Form = Form
        Me.BackAction = BackAction
        Me.ForwardAction = ForwardAction
        Application.AddMessageFilter(Me)
    End Sub

    Private Const WM_XBUTTONDOWN As Integer = &H20B
    Private Const XBUTTON1 As Integer = 1 ' Back
    Private Const XBUTTON2 As Integer = 2 ' Forward

    Public Function PreFilterMessage(ByRef Message As Message) As Boolean Implements IMessageFilter.PreFilterMessage

        If Message.Msg <> WM_XBUTTONDOWN Then Return False

        If System.Windows.Forms.Form.ActiveForm IsNot Form Then Return False

        Dim Button = CInt((Message.WParam.ToInt64() >> 16) And &HFFFF)

        Select Case Button
            Case XBUTTON1
                BackAction?.Invoke()
            Case XBUTTON2
                ForwardAction?.Invoke()
        End Select

        Return False

    End Function

    Private Sub Form_Disposed(sender As Object, e As EventArgs) Handles Form.Disposed
        Application.RemoveMessageFilter(Me)
    End Sub
End Class