Public Class frmProgress

    Dim ProgressSub As ProgressFormArgs
    Dim Parameter As Object
    Dim ThreadName As String
    Public Sub New(ProgressSub As ProgressFormArgs, Parameter As Object, Optional ThreadName As String = Nothing)
        InitializeComponent()
        Me.Parameter = Parameter
        Me.ProgressSub = ProgressSub
        Me.ThreadName = ThreadName
    End Sub

    Dim thread As System.Threading.Thread

    Private Sub frmProgress_FormClosed(sender As Object, e As FormClosedEventArgs) Handles Me.FormClosed
        If thread IsNot Nothing AndAlso thread.IsAlive Then
            thread.Abort()
        End If
    End Sub

    Private Sub frmProgress_Load(sender As Object, e As EventArgs) Handles Me.Load
        nbProgress.GlowMode = EmbeddedFileSystemSample.Controls.NeroBar.NeroBarGlowModes.WholeBar
        Dim ProgressReport As New ProgressReport With {.frmProgress = Me}
        thread = i00Debug.Thread.Create(If(ThreadName = "", "Progress", ThreadName), Sub()
                                                                                         ProgressSub.Invoke(Parameter, ProgressReport)
                                                                                         Me.Invoke(Sub()
                                                                                                       Me.DialogResult = ProgressReport.DialogReturn
                                                                                                       ProgressReport.CloseEnabled = True
                                                                                                       Me.Close()
                                                                                                   End Sub)
                                                                                     End Sub)
        thread.IsBackground = True '<< this is background as this form will be open for the duration of the thread anyway
        thread.Start()
    End Sub

    Delegate Sub ProgressFormArgs(Parameter As Object, ProgressReport As ProgressReport)

    Public Sub SetProgress(Value As Long, MaxValue As Long)

        If Value < 0 OrElse MaxValue < 0 Then
            nbProgress.GlowMode = EmbeddedFileSystemSample.Controls.NeroBar.NeroBarGlowModes.WholeBar
        ElseIf Value = 0 Then
            nbProgress.GlowMode = EmbeddedFileSystemSample.Controls.NeroBar.NeroBarGlowModes.None
            nbProgress.Value = 0
            nbProgress.MaxValue = 1
        ElseIf Value > 0 Then
            nbProgress.GlowMode = EmbeddedFileSystemSample.Controls.NeroBar.NeroBarGlowModes.None
            If nbProgress.MaxValue <> MaxValue Then nbProgress.MaxValue = MaxValue
            nbProgress.Value = Value
            'If frmProgress.proProgress.Maximum <> MaxValue Then
            '    frmProgress.proProgress.Maximum = MaxValue
            'End If
            'frmProgress.proProgress.Value = Value
        End If
    End Sub

    Private Sub frmProgress_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If CloseEnabled = False AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
        End If
    End Sub

    Property CloseEnabled As Boolean
        Get
            Return btnCancel.Enabled
        End Get
        Set(value As Boolean)
            btnCancel.Enabled = value
            If value Then
                Me.EnableCloseButton
            Else
                Me.DisableCloseButton
            End If
        End Set
    End Property

    Public Class ProgressReport

        Public Property DialogReturn As DialogResult = Windows.Forms.DialogResult.OK
        Public Property frmProgress As frmProgress
        Public Event SettingText(Text As String)
        Public Sub SetText(Text As String, Optional FireEvent As Boolean = True)
            If FireEvent Then RaiseEvent SettingText(Text)
            Try
                frmProgress.Invoke(Sub()
                                       frmProgress.lblProgress.Text = Text
                                   End Sub)
            Catch ex As Threading.ThreadAbortException
                Throw ex
            End Try
        End Sub
        Public Function ShowMessageBox(Prompt As Object, Optional Buttons As Microsoft.VisualBasic.MsgBoxStyle = MsgBoxStyle.ApplicationModal, Optional Title As Object = Nothing, Optional CustomButtons As IEnumerable(Of MessageBox.MsgBoxButtonBase) = Nothing, Optional CustomImage As Image = Nothing, Optional OnCreated As Action(Of MessageBox) = Nothing) As Microsoft.VisualBasic.MsgBoxResult
            ShowMessageBox = MsgBoxResult.Ok
            frmProgress.Invoke(Sub()
                                   ShowMessageBox = MsgBox(frmProgress, Prompt, Buttons, Title, CustomButtons, CustomImage, OnCreated)
                               End Sub)
        End Function
        Public Sub SetProgress(Value As Long, MaxValue As Long)
            Try
                frmProgress.Invoke(Sub()
                                       frmProgress.SetProgress(Value, MaxValue)
                                   End Sub)
            Catch ex As Threading.ThreadAbortException
                Throw ex
            End Try
        End Sub
        Public Property CloseEnabled As Boolean
            Get
                Return frmProgress.CloseEnabled
            End Get
            Set(value As Boolean)
                Try
                    frmProgress.Invoke(Sub()
                                           frmProgress.CloseEnabled = value
                                       End Sub)
                Catch ex As Threading.ThreadAbortException
                    Throw ex
                End Try
            End Set
        End Property
    End Class
End Class