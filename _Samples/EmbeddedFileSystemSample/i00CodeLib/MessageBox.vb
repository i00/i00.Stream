Public Class MessageBox

    Dim mc_CancelItem As MessageBox.MsgBoxButtonBase
    Public Property CancelItem As MessageBox.MsgBoxButtonBase
        Get
            Return mc_CancelItem
        End Get
        Set(value As MessageBox.MsgBoxButtonBase)
            If mc_CancelItem IsNot value Then
                mc_CancelItem = value
                If value IsNot Nothing Then
                    Me.AllowClose = True
                Else
                    Me.AllowClose = False
                End If
            End If
        End Set
    End Property

    Dim mc_AcceptItem As MessageBox.MsgBoxButtonBase
    Public Property AcceptItem As MessageBox.MsgBoxButtonBase
        Get
            Return mc_AcceptItem
        End Get
        Set(value As MessageBox.MsgBoxButtonBase)
            If mc_AcceptItem IsNot value Then
                mc_AcceptItem = value
            End If
        End Set
    End Property

    Private Sub rtbMessage_GotFocus(sender As Object, e As EventArgs) Handles rtbMessage.GotFocus
        HideCaret(rtbMessage.Handle)
    End Sub

    Private Sub rtbMessage_MouseDown(sender As Object, e As MouseEventArgs) Handles rtbMessage.MouseDown, rtbMessage.MouseUp
        HideCaret(rtbMessage.Handle)
    End Sub

    Private Sub rtbMessage_LinkClicked(sender As Object, e As LinkClickedEventArgs) Handles rtbMessage.LinkClicked
        Try
            Cursor.Current = Cursors.WaitCursor
            Process.Start(e.LinkText)
        Catch ex As Exception

        Finally
            Cursor.Current = Cursors.Default
        End Try
    End Sub

    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function HideCaret(hwnd As IntPtr) As Integer
    End Function

    Private Sub MessageBox_FormClosed(sender As Object, e As FormClosedEventArgs) Handles Me.FormClosed
        If CloseEvent IsNot Nothing Then
            CloseEvent.Invoke(sender, e)
        End If
    End Sub

    Private Sub MessageBox_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If AllowClose = False Then
            e.Cancel = True
        End If
    End Sub

    'Did this as when a message box is triggered on the escape press of a form the escape would be picked up on the KeyUp otherwise
    Dim EscapeDown As Boolean
    Private Sub MessageBox_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        If e.KeyCode = Keys.Escape Then
            EscapeDown = True
        End If
    End Sub
    Private Sub MessageBox_LostFocus(sender As Object, e As EventArgs) Handles Me.LostFocus
        EscapeDown = False
    End Sub

    Private Sub MessageBox_KeyUp(sender As Object, e As KeyEventArgs) Handles Me.KeyUp
        If EscapeDown AndAlso e.KeyCode = Keys.Escape Then
            If Me.CancelItem IsNot Nothing Then
                CancelItem.ClickAction(CancelItem, EventArgs.Empty)
            End If
        End If
        EscapeDown = False
    End Sub

    Private AllowClose As Boolean

    Public Interface iTextControl
        ReadOnly Property Control As Control
        Function PrepareLayout(Text As String, MaxSize As Size) As Size
    End Interface

    Public Property TextControl As iTextControl

    Private Sub MessageBox_Load(sender As Object, e As EventArgs) Handles Me.Load
        Dim CenterPoint = Point.Round(Middle(Bounds))
        If TextControl Is Nothing Then
            rtbMessage.ReadOnly = True
            HideCaret(rtbMessage.Handle)
        Else
            rtbMessage.Visible = False
            pnlContentHolder.Controls.Add(TextControl.Control)
        End If

        Dim ButtonWidthWithSpacing = pnlButtons.Controls.OfType(Of Control).Select(Function(x) x.Width).Sum + pnlButtons.Padding.Left + pnlButtons.Padding.Right

        Dim IconSize = picIcon.Image?.Size
        If IconSize IsNot Nothing Then
            picIcon.MaximumSize = New Size(0, 0)
            picIcon.Width = IconSize.Value.Width
        End If

        Dim ExtraSpacingWidth = pnlContentHolder.Padding.Left + pnlContentHolder.Padding.Right + If(picIcon.Visible, picIcon.Width + pnlImageSpacer.Width, 0) + SystemInformation.VerticalScrollBarWidth

        Dim OnScreen = Screen.FromPoint(CenterPoint) '< this always returns the closest screen ... so no need to check Nothing
        Dim WorkingArea = OnScreen.WorkingArea

        Dim MaxTextSize = New Size(CInt(WorkingArea.Width * 0.5 - ((Me.Width - Me.ClientSize.Width) + ExtraSpacingWidth)), Integer.MaxValue)

        Dim TextSize As Size
        If TextControl Is Nothing Then
            TextSize = TextRenderer.MeasureText(rtbMessage.Text, rtbMessage.Font, MaxTextSize, TextFormatFlags.NoPrefix Or TextFormatFlags.WordBreak)
        Else
            TextSize = TextControl.PrepareLayout(rtbMessage.Text, MaxTextSize)
            TextControl.Control.Dock = DockStyle.Fill
            TextControl.Control.BringToFront()
        End If
        Dim TextSizeWithSpacing = TextSize 'New Size(TextSize.Width + (Me.ClientSize.Width - rtbMessage.Width) + SystemInformation.VerticalScrollBarWidth, TextSize.Height)
        TextSizeWithSpacing.Height = Math.Max((IconSize?.Height).GetValueOrDefault(0), TextSizeWithSpacing.Height) + pnlContentHolder.Padding.Bottom + pnlContentHolder.Padding.Top + pnlButtons.Height
        TextSizeWithSpacing.Width = ExtraSpacingWidth + TextSizeWithSpacing.Width

        Dim ToBeSize = New Size(Math.Max(TextSizeWithSpacing.Width, ButtonWidthWithSpacing), TextSizeWithSpacing.Height)
        ToBeSize.Width = Math.Min(CInt(WorkingArea.Width * ScreenBoundsLimitWidthRatio), ToBeSize.Width)
        ToBeSize.Height = Math.Min(CInt(WorkingArea.Height * ScreenBoundsLimitHeightRatio), ToBeSize.Height)

        Me.ClientSize = ToBeSize
        Dim ToBeBounds = Rectangle.Round(SetMiddle(Bounds, CenterPoint))

        If WorkingArea.Contains(ToBeBounds) Then
            'all good - screen completly contains the message box
        Else
            If ToBeBounds.Right > WorkingArea.Right Then
                ToBeBounds.X = WorkingArea.Right - ToBeBounds.Width
            End If
            If ToBeBounds.X < WorkingArea.X Then
                ToBeBounds.X = WorkingArea.X
            End If
            If ToBeBounds.Bottom > WorkingArea.Bottom Then
                ToBeBounds.Y = WorkingArea.Bottom - ToBeBounds.Height
            End If
            If ToBeBounds.Y < WorkingArea.Y Then
                ToBeBounds.Y = WorkingArea.Y
            End If
        End If

        Me.SetBounds(ToBeBounds.X, ToBeBounds.Y, ToBeBounds.Width, ToBeBounds.Height)
    End Sub

    Public ScreenBoundsLimitWidthRatio As Double = 0.5
    Public ScreenBoundsLimitHeightRatio As Double = 0.5

    Public Property Prompt As Object
        Get
            Return rtbMessage.Text
        End Get
        Set(value As Object)
            If value IsNot Nothing Then
                rtbMessage.Text = value.ToString
            Else
                rtbMessage.Text = ""
            End If
        End Set
    End Property

    Protected Overrides ReadOnly Property CreateParams As CreateParams
        Get
            Return MyBase.CreateParams
        End Get
    End Property

    Public Property CloseEvent As EventHandler

    Public MustInherit Class MsgBoxButtonBase
        MustOverride ReadOnly Property Control As Control
        Public Property Cancel As Boolean
        Friend MessageBoxForm As MessageBox

        Public Overridable Sub ClickAction(sender As Object, e As EventArgs)
            CloseForm()
        End Sub

        Public Sub CloseForm()
            MessageBoxForm.CloseEvent = Nothing
            MessageBoxForm.AllowClose = True
            MessageBoxForm.Close()
        End Sub
    End Class

    'Public Class MsgBoxDropDownButton
    '    Inherits MsgBoxButtonBase

    '    Dim ClickEvent As EventHandler
    '    Dim Menu As ToolStripDropDown
    '    Dim MustDropDown As Boolean

    '    Public Sub New(Text As String, ClickEvent As EventHandler, Menu As ToolStripDropDown, Optional MustDropDown As Boolean = False)
    '        Me.Menu = Menu
    '        Me.MustDropDown = MustDropDown

    '        Menu.Tag = Me

    '        If MustDropDown Then
    '            Dim SplitButton = DirectCast(Control, Controls.SplitButton)
    '            SplitButton.Style = i00CodeLib.Controls.SplitButton.Styles.DropDown
    '        Else
    '            Me.ClickEvent = ClickEvent
    '        End If
    '        Me.Control.Text = Text
    '    End Sub

    '    Public Shared Sub CloseMessageBox(ToolStripItem As ToolStripItem)
    '        Dim MsgBoxDropDownButton = TryCast(ToolStripItem.GetCurrentParent.Tag, MsgBoxDropDownButton)
    '        If MsgBoxDropDownButton IsNot Nothing Then
    '            MsgBoxDropDownButton.CloseForm()
    '        End If
    '    End Sub

    '    Public Overrides Sub ClickAction(sender As Object, e As EventArgs)
    '        If ClickEvent IsNot Nothing Then
    '            ClickEvent(sender, e)
    '        End If
    '        MyBase.ClickAction(sender, e)
    '    End Sub

    '    Private WithEvents mc_Control As Controls.SplitButton

    '    Public Overrides ReadOnly Property Control As Control
    '        Get
    '            If mc_Control Is Nothing Then
    '                mc_Control = New Controls.SplitButton
    '                mc_Control.AutoSize = True
    '            End If
    '            Return mc_Control
    '        End Get
    '    End Property

    '    Private Sub mc_Control_Click(sender As Object, e As EventArgs) Handles mc_Control.Click
    '        ClickAction(sender, e)
    '    End Sub

    '    Private Sub mc_Control_ClickDropdown(sender As Object, e As EventArgs) Handles mc_Control.ClickDropdown
    '        If Menu IsNot Nothing Then
    '            Menu.Show(mc_Control, 0, mc_Control.Height)
    '        End If
    '    End Sub
    'End Class

    Public Class MsgBoxButton
        Inherits MsgBoxButtonBase
        'Inherits Button
        'Implements iMsgBoxButton
        'Friend ClickEvent As EventHandler
        Public Property ClickEvent As EventHandler
        Public Property AutoClose As Boolean = True

        Public Sub New(Text As String, ClickEvent As EventHandler)
            Me.Control.Text = Text
            Me.ClickEvent = ClickEvent
            AddHandler Me.Control.Click, AddressOf ClickAction
        End Sub

        Public Overrides Sub ClickAction(sender As Object, e As EventArgs)
            If ClickEvent IsNot Nothing Then
                ClickEvent()(sender, e)
            End If
            If AutoClose Then
                MyBase.ClickAction(sender, e)
            End If
        End Sub

        Dim mc_Control As Button
        Public Overrides ReadOnly Property Control As Control
            Get
                If mc_Control Is Nothing Then
                    mc_Control = New Button
                    mc_Control.AutoSize = True
                End If
                Return mc_Control
            End Get
        End Property
    End Class

    Public NotInheritable Class Converter

        Private Sub New()

        End Sub

        Public NotInheritable Class MsgBoxStyleToMessageBoxTypeReturner
            Friend Sub New()

            End Sub
            Public Property MessageBoxButtons As MessageBoxButtons
            Public Property MessageBoxIcon As MessageBoxIcon
        End Class
        Public Shared Function MsgBoxStyleToMessageBoxType(Style As Microsoft.VisualBasic.MsgBoxStyle) As MsgBoxStyleToMessageBoxTypeReturner
            Dim r = New MsgBoxStyleToMessageBoxTypeReturner
            If (Style Or MsgBoxStyle.Information) = Style Then
                r.MessageBoxIcon = MessageBoxIcon.Information
            ElseIf (Style Or MsgBoxStyle.Exclamation) = Style Then
                r.MessageBoxIcon = MessageBoxIcon.Exclamation
            ElseIf (Style Or MsgBoxStyle.Critical) = Style Then
                r.MessageBoxIcon = MessageBoxIcon.Error
            ElseIf (Style Or MsgBoxStyle.Question) = Style Then
                r.MessageBoxIcon = MessageBoxIcon.Question
            End If
            If (Style Or MsgBoxStyle.OkCancel) = Style Then
                r.MessageBoxButtons = MessageBoxButtons.OKCancel
            ElseIf (Style Or MsgBoxStyle.AbortRetryIgnore) = Style Then
                r.MessageBoxButtons = MessageBoxButtons.AbortRetryIgnore
            ElseIf (Style Or MsgBoxStyle.YesNoCancel) = Style Then
                r.MessageBoxButtons = MessageBoxButtons.YesNoCancel
            ElseIf (Style Or MsgBoxStyle.YesNo) = Style Then
                r.MessageBoxButtons = MessageBoxButtons.YesNo
            ElseIf (Style Or MsgBoxStyle.RetryCancel) = Style Then
                r.MessageBoxButtons = MessageBoxButtons.RetryCancel
            End If
            Return r
        End Function
        Public Shared Function DialogResultToMsgBoxResult(DialogResult As DialogResult) As MsgBoxResult
            Select Case DialogResult
                Case DialogResult.Abort
                    Return MsgBoxResult.Abort
                Case DialogResult.Cancel
                    Return MsgBoxResult.Cancel
                Case DialogResult.Ignore
                    Return MsgBoxResult.Ignore
                Case DialogResult.No
                    Return MsgBoxResult.No
                Case DialogResult.Retry
                    Return MsgBoxResult.Retry
                Case DialogResult.Yes
                    Return MsgBoxResult.Yes
                Case Else 'DialogResult.None, DialogResult.OK
                    Return MsgBoxResult.Ok
            End Select
        End Function
    End Class

    Delegate Function MsgBoxHandlerDelegate(Owner As IWin32Window, Prompt As String, Buttons As Microsoft.VisualBasic.MsgBoxStyle, Title As String, CustomButtons As IEnumerable(Of MessageBox.MsgBoxButtonBase), CustomImage As Image, OnCreated As Action(Of MessageBox)) As Microsoft.VisualBasic.MsgBoxResult

    Private Shared mc_MsgBoxHandlerDelegate As MsgBoxHandlerDelegate
    Public Shared Property MsgBoxHandler As MsgBoxHandlerDelegate
        Get
            If mc_MsgBoxHandlerDelegate Is Nothing Then
                mc_MsgBoxHandlerDelegate = Function(Owner, Prompt, Buttons, Title, CustomButtons, CustomImage, OnCreated)
                                               Dim Returner = MsgBoxResult.Ok
                                               Using frm As New MessageBox
                                                   frm.Prompt = Prompt
                                                   frm.Text = Title
                                                   frm.rtbMessage.Font = SystemFonts.MessageBoxFont

                                                   If CustomImage Is Nothing Then
                                                       If Buttons.HasFlag(MsgBoxStyle.Information) Then
                                                           System.Media.SystemSounds.Asterisk.Play()
                                                           frm.picIcon.Image = SystemIcons.Information.ToBitmap()
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.Exclamation) Then
                                                           System.Media.SystemSounds.Exclamation.Play()
                                                           frm.picIcon.Image = SystemIcons.Exclamation.ToBitmap()
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.Question) Then
                                                           System.Media.SystemSounds.Question.Play()
                                                           frm.picIcon.Image = SystemIcons.Question.ToBitmap()
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.Critical) Then
                                                           System.Media.SystemSounds.Hand.Play()
                                                           frm.picIcon.Image = SystemIcons.Error.ToBitmap()
                                                       Else
                                                           frm.picIcon.Visible = False
                                                           frm.pnlImageSpacer.Visible = False
                                                       End If
                                                   Else
                                                       frm.picIcon.Image = CustomImage
                                                   End If
                                                   If CustomButtons IsNot Nothing AndAlso CustomButtons.Any Then
                                                       'add custom buttons
                                                   Else
                                                       Dim lstCustomButtons As New List(Of MsgBoxButton)
                                                       If Buttons.HasFlag(MsgBoxStyle.RetryCancel) Then
                                                           lstCustomButtons.Add(New MsgBoxButton("&Retry", Sub(ss, ee) Returner = MsgBoxResult.Retry))
                                                           lstCustomButtons.Add(New MsgBoxButton("&Cancel", Sub(ss, ee) Returner = MsgBoxResult.Cancel) With {.Cancel = True})
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.YesNo) Then
                                                           lstCustomButtons.Add(New MsgBoxButton("&Yes", Sub(ss, ee) Returner = MsgBoxResult.Yes))
                                                           lstCustomButtons.Add(New MsgBoxButton("&No", Sub(ss, ee) Returner = MsgBoxResult.No))
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.YesNoCancel) Then
                                                           lstCustomButtons.Add(New MsgBoxButton("&Yes", Sub(ss, ee) Returner = MsgBoxResult.Yes))
                                                           lstCustomButtons.Add(New MsgBoxButton("&No", Sub(ss, ee) Returner = MsgBoxResult.No))
                                                           lstCustomButtons.Add(New MsgBoxButton("Cancel", Sub(ss, ee) Returner = MsgBoxResult.Cancel) With {.Cancel = True})
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.AbortRetryIgnore) Then
                                                           lstCustomButtons.Add(New MsgBoxButton("&Abort", Sub(ss, ee) Returner = MsgBoxResult.Abort))
                                                           lstCustomButtons.Add(New MsgBoxButton("&Retry", Sub(ss, ee) Returner = MsgBoxResult.Retry))
                                                           lstCustomButtons.Add(New MsgBoxButton("&Ignore", Sub(ss, ee) Returner = MsgBoxResult.Ignore))
                                                       ElseIf Buttons.HasFlag(MsgBoxStyle.OkCancel) Then
                                                           lstCustomButtons.Add(New MsgBoxButton("OK", Sub(ss, ee) Returner = MsgBoxResult.Ok))
                                                           lstCustomButtons.Add(New MsgBoxButton("Cancel", Sub(ss, ee) Returner = MsgBoxResult.Cancel) With {.Cancel = True})
                                                       Else
                                                           'MsgBoxStyle.OkOnly
                                                           lstCustomButtons.Add(New MsgBoxButton("OK", Sub(ss, ee) Returner = MsgBoxResult.Ok) With {.Cancel = True})
                                                       End If
                                                       CustomButtons = lstCustomButtons
                                                   End If
                                                   Dim HasCancelButton = False
                                                   For i = 0 To CustomButtons.Count - 1
                                                       Dim item = CustomButtons(i)
                                                       item.Control.Dock = DockStyle.Right
                                                       item.MessageBoxForm = frm

                                                       If item.Cancel AndAlso frm.CancelButton Is Nothing Then
                                                           Dim MsgBoxButton = TryCast(item, MsgBoxButton)
                                                           If MsgBoxButton IsNot Nothing Then
                                                               'we can use this as a cancel button
                                                               frm.CancelItem = item
                                                               'do this on close...
                                                               frm.CloseEvent = MsgBoxButton.ClickEvent
                                                               frm.AllowClose = True
                                                               HasCancelButton = True
                                                           End If
                                                       End If

                                                       frm.pnlButtons.Controls.Add(item.Control)
                                                       If (i = 2 AndAlso Buttons.HasFlag(vbDefaultButton3)) OrElse (i = 1 AndAlso Buttons.HasFlag(vbDefaultButton2)) Then
                                                           frm.AcceptItem = item
                                                       End If

                                                       If i <> CustomButtons.Count - 1 Then
                                                           frm.pnlButtons.Controls.Add(New Panel With {.Dock = DockStyle.Right, .Width = frm.pnlButtons.Padding.Right})
                                                       End If
                                                   Next
                                                   If HasCancelButton = False Then
                                                       frm.DisableCloseButton()
                                                   End If
                                                   If frm.AcceptItem Is Nothing Then
                                                       frm.AcceptItem = CustomButtons.FirstOrDefault
                                                   End If
                                                   If Owner Is Nothing Then
                                                       frm.StartPosition = FormStartPosition.CenterScreen
                                                   Else
                                                       Dim ctl = TryCast(Owner, Control)
                                                       If ctl Is Nothing Then
                                                           'Native IWin32Window
                                                           If Shell.Window.IsMinimized(Owner.Handle) Then
                                                               frm.StartPosition = FormStartPosition.CenterScreen
                                                           Else
                                                               Try
                                                                   frm.StartPosition = FormStartPosition.Manual
                                                                   Dim CenterPoint = Owner.Handle.GetWindowRectangle().MidPoint
                                                                   Dim Bounds = frm.Bounds
                                                                   Bounds = Rectangle.Round(SetMiddle(Bounds, CenterPoint))
                                                                   frm.Bounds = Bounds
                                                               Catch ex As Exception
                                                                   frm.StartPosition = FormStartPosition.CenterScreen
                                                               End Try
                                                           End If
                                                       Else
                                                           Dim OwnerForm = ctl.FindForm
                                                           If OwnerForm Is Nothing Then
                                                               'control is not on a form
                                                               frm.StartPosition = FormStartPosition.CenterScreen
                                                           Else
                                                               If OwnerForm.WindowState = FormWindowState.Minimized Then
                                                                   frm.StartPosition = FormStartPosition.CenterScreen
                                                               Else
                                                                   frm.StartPosition = FormStartPosition.CenterParent
                                                               End If
                                                           End If
                                                       End If
                                                   End If
                                                   OnCreated?.Invoke(frm)
                                                   If frm.ShowDialog(Owner) = DialogResult.Cancel Then
                                                       'we need to select the cancel button

                                                   End If
                                               End Using
                                               Return Returner
                                           End Function

            End If
            Return mc_MsgBoxHandlerDelegate
        End Get
        Set(value As MsgBoxHandlerDelegate)
            mc_MsgBoxHandlerDelegate = value
        End Set
    End Property

    Public Shared Property DefaultTitle As String = ""

    <System.Diagnostics.DebuggerStepThrough()>
    Public Shared Function MsgBox(Owner As IWin32Window, Prompt As Object, Optional Buttons As Microsoft.VisualBasic.MsgBoxStyle = MsgBoxStyle.ApplicationModal, Optional Title As Object = Nothing, Optional CustomButtons As IEnumerable(Of MsgBoxButtonBase) = Nothing, Optional CustomImage As Image = Nothing, Optional OnCreated As Action(Of MessageBox) = Nothing) As Microsoft.VisualBasic.MsgBoxResult
        Dim fOwner = TryCast(Owner, Control)
        If fOwner IsNot Nothing AndAlso fOwner.InvokeRequired Then
            Return DirectCast(fOwner.Invoke(Function() MsgBox(Owner, Prompt, Buttons, Title, CustomButtons, CustomImage, OnCreated)), MsgBoxResult)
        Else
            Dim NiceTitle = ""
            If Title IsNot Nothing Then
                NiceTitle = Title.ToString
            End If
            If NiceTitle = "" Then
                If DefaultTitle = "" Then
                    Dim Assembly = System.Reflection.Assembly.GetEntryAssembly
                    If Assembly Is Nothing Then '< Assembly can actually be nothing here if called from the designer!...
                        Assembly = System.Reflection.Assembly.GetCallingAssembly
                    End If
                    NiceTitle = Assembly?.GetName.Name
                Else
                    NiceTitle = DefaultTitle
                End If
            End If
            Dim NicePrompt = Prompt?.ToString

            Return MsgBoxHandler().Invoke(Owner, NicePrompt, Buttons, NiceTitle, CustomButtons, CustomImage, OnCreated)
        End If
    End Function

End Class

Public Module modMessageBox

    <System.Diagnostics.DebuggerStepThrough()>
    Public Function MsgBox(Owner As IWin32Window, Prompt As Object, Optional Buttons As Microsoft.VisualBasic.MsgBoxStyle = MsgBoxStyle.ApplicationModal, Optional Title As Object = Nothing, Optional CustomButtons As IEnumerable(Of MessageBox.MsgBoxButtonBase) = Nothing, Optional CustomImage As Image = Nothing, Optional OnCreated As Action(Of MessageBox) = Nothing) As Microsoft.VisualBasic.MsgBoxResult
        Return MessageBox.MsgBox(Owner, Prompt, Buttons, Title, CustomButtons, CustomImage, OnCreated)
    End Function

End Module