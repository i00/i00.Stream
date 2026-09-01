'i00 .Net ToolStrip As Menu
'©i00 Productions All rights reserved
'Created by Kris Bennett
'----------------------------------------------------------------------------------------------------
'All property in this file is and remains the property of i00 Productions, regardless of its usage,
'unless stated otherwise in writing from i00 Productions.
'
'i00 is not and shall not be held accountable for any damages directly or indirectly caused by the
'use or miss-use of this product.  This product is only a component and thus is intended to be used
'as part of other software, it is not a complete software package, thus i00 Productions is not
'responsible for any legal ramifications that software using this product breaches.

Public Class ToolStripAsMenu
    Inherits NativeWindow
    'Implements IMessageFilter

    Private WithEvents ToolStrip As ToolStrip

    Private Sub ToolStrip_ItemAdded(sender As Object, e As ToolStripItemEventArgs) Handles ToolStrip.ItemAdded
        Dim ToolStripDropDownButton = TryCast(e.Item, ToolStripDropDownButton)
        If ToolStripDropDownButton IsNot Nothing Then
            AddHandler ToolStripDropDownButton.Paint, AddressOf tsb_Paint
        End If
    End Sub

    Private Sub ToolStrip_ItemRemoved(sender As Object, e As ToolStripItemEventArgs) Handles ToolStrip.ItemRemoved
        RemoveHandler e.Item.Paint, AddressOf tsb_Paint
    End Sub

    Private Sub tsb_Paint(sender As Object, e As PaintEventArgs)
        Dim ToolStripDropDownButton = TryCast(sender, ToolStripDropDownButton)
        If ToolStripDropDownButton IsNot Nothing Then
            If ToolStripDropDownButton.DisplayStyle = ToolStripItemDisplayStyle.Text Then
                'don't show arrow
                Dim ToolStrip = ToolStripDropDownButton.GetCurrentParent()
                'e.Graphics.Clear(ToolStripDropDownButton.BackColor)

                'Draw the background - there are cases where the background of the renderer's DrawToolStripBackground is transparent
                '... so we will need to call PaintBackground to paint over what has already been drawn
                Dim ControlType = GetType(Control)
                'e As System.Windows.Forms.PaintEventArgs
                'rectangle As System.Drawing.Rectangle
                'backColor As System.Drawing.Color
                'scrollOffset As System.Drawing.Point
                Dim Method = ControlType.GetMethod("PaintBackground", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance, Nothing, {GetType(PaintEventArgs), GetType(Rectangle), GetType(Color), GetType(Point)}, Nothing)

                Dim RelBounds = ToolStripDropDownButton.Bounds
                Dim p = New PaintEventArgs(e.Graphics, RelBounds)

                Dim ScrollOffset = ToolStrip.AutoScrollOffset
                'Control is not scrollable ... so can't scroll like this:
                '   ScrollOffset.X -= RelBounds.X
                '   ScrollOffset.Y -= RelBounds.Y
                '... have to use a transform instead
                Using UsingLambda.Create(Function() e.Graphics.Save(),
                                         Sub(x)
                                             e.Graphics.Restore(x)
                                         End Sub)
                    e.Graphics.TranslateTransform(-RelBounds.X, -RelBounds.Y)
                    Method.Invoke(ToolStrip, {p, RelBounds, ToolStrip.BackColor, ScrollOffset})
                End Using

                ToolStrip.Renderer.DrawToolStripBackground(New ToolStripRenderEventArgs(e.Graphics, ToolStrip))
                ToolStrip.Renderer.DrawDropDownButtonBackground(New ToolStripItemRenderEventArgs(e.Graphics, ToolStripDropDownButton))
                ToolStrip.Renderer.DrawItemText(New ToolStripItemTextRenderEventArgs(e.Graphics, ToolStripDropDownButton, ToolStripDropDownButton.Text, e.ClipRectangle, ToolStripDropDownButton.ForeColor, ToolStripDropDownButton.Font, ToolStripDropDownButton.TextAlign))
            End If
        End If
    End Sub

    Private Sub ToolStrip_ParentChanged(sender As Object, e As EventArgs) Handles ToolStrip.ParentChanged
        WireUpHandle()
    End Sub

    Private Sub WireUpHandle()
        ReleaseHandle()
        If ToolStrip.FindForm IsNot Nothing Then
            AssignHandle(ToolStrip.FindForm.Handle)
        End If
    End Sub

    Public Sub New(ToolStrip As ToolStrip)
        Me.ToolStrip = ToolStrip
        For Each item In ToolStrip.Items.OfType(Of ToolStripDropDownButton)
            AddHandler item.Paint, AddressOf tsb_Paint

            AddHandler item.DropDownClosed, Sub(ss, ee)
                                                'delay invoke the dropping down of another menu item
                                                Dim a As Action = Sub()
                                                                      Try
                                                                          Dim SelectedItem = ToolStrip.Items.OfType(Of ToolStripDropDownButton).FirstOrDefault(Function(x) x.Selected)
                                                                          If SelectedItem IsNot Nothing AndAlso SelectedItem IsNot item Then
                                                                              SelectedItem.ShowDropDown()
                                                                          End If
                                                                      Catch ex As ObjectDisposedException

                                                                      End Try
                                                                  End Sub
                                                a.DelayInvoke
                                            End Sub
        Next
        WireUpHandle()
        'Application.AddMessageFilter(Create)
    End Sub

    Const WM_SYSCOMMAND As Integer = &H112
    Const SC_KEYMENU As Integer = &HF100

    Protected Overrides Sub WndProc(ByRef m As System.Windows.Forms.Message)

        Select Case m.Msg
            Case WM_SYSCOMMAND
                Select Case m.WParam.ToInt64
                    Case SC_KEYMENU
                        Static LastControl As Control
                        If ToolStrip.Focused() Then
                            If LastControl IsNot Nothing Then
                                Try
                                    LastControl.Focus()
                                Catch ex As Exception

                                End Try
                            End If
                        Else
                            If ToolStrip.FindForm IsNot Nothing Then
                                LastControl = ToolStrip.FindForm.ActiveControl
                                ToolStrip.Focus()
                                Dim tsi = ToolStrip.Items.OfType(Of ToolStripItem).OrderBy(Function(x) x.Alignment = ToolStripItemAlignment.Left = False).FirstOrDefault
                                If tsi IsNot Nothing Then
                                    tsi.Select()
                                End If
                            End If
                        End If
                    Case Else
                        MyBase.WndProc(m)
                End Select
            Case Else
                MyBase.WndProc(m)
        End Select

    End Sub

    'Public Function PreFilterMessage(ByRef m As Message) As Boolean Implements IMessageFilter.PreFilterMessage

    'End Function


End Class
