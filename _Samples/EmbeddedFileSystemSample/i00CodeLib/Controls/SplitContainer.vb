Namespace Controls

    <ComponentModel.DesignerCategory("")>
    Public Class SplitContainer
        Inherits System.Windows.Forms.SplitContainer

        <ComponentModel.DefaultValue(False)>
        Public Property DrawSplitBorder1 As Boolean
        <ComponentModel.DefaultValue(False)>
        Public Property DrawSplitBorder2 As Boolean
        <ComponentModel.DefaultValue(5)>
        Public Property GripBallCount As Integer = 5
        <ComponentModel.DefaultValue(1.5)>
        Public Property GripSpacing As Single = 1.5F
        <ComponentModel.DefaultValue(True)>
        Public Property ShowContentOnSplitterSize As Boolean = True

        <ComponentModel.DefaultValue(6)>
        Public Shadows Property SplitterWidth As Integer
            Get
                Return MyBase.SplitterWidth
            End Get
            Set(value As Integer)
                MyBase.SplitterWidth = value
            End Set
        End Property

        Public Sub New()
            MyBase.SplitterWidth = 6
            SetStyle(ControlStyles.AllPaintingInWmPaint Or
                       ControlStyles.DoubleBuffer Or
                       ControlStyles.ResizeRedraw Or
                       ControlStyles.UserPaint,
                       True)
        End Sub

        Friend Shared Sub DrawGrip(g As Graphics, Rect As Rectangle, GripBallCount As Integer, Spacing As Single)
            Dim BallRect As Rectangle
            Dim Vert As Boolean = Rect.Width < Rect.Height
            If Vert Then
                BallRect = New Rectangle(Rect.X, 0, Rect.Width, CInt((Rect.Width * Spacing * GripBallCount) - ((Spacing - 1) * Rect.Width)) + 1)
                BallRect.Y = (Rect.Height - BallRect.Height) \ 2
            Else
                BallRect = New Rectangle(0, Rect.Y, CInt((Rect.Height * ((GripBallCount * Spacing) - 1)) + 1), Rect.Height)
                BallRect.X = (Rect.Width - BallRect.Width) \ 2
            End If

            Dim BallSize = Math.Min(BallRect.Width, BallRect.Height)
            If BallSize <= 0 Then Return
            Using b As New Bitmap(CInt(BallSize * If(Vert, 1, Spacing)), CInt(BallSize * If(Vert, Spacing, 1)))
                Using gBall = Graphics.FromImage(b)
                    'gBall.SmoothingMode = Drawing2D.SmoothingMode.HighQuality
                    Using bball = Drawing.DrawBall(SystemColors.ControlLightLight, BallSize)
                        Using m As New System.Drawing.Drawing2D.Matrix
                            m.RotateAt(90, New PointF(CSng(bball.Width / 2), CSng(bball.Height / 2)))
                            gBall.Transform = m
                            gBall.DrawImageUnscaled(bball, New Point(0, 0))
                        End Using
                    End Using
                    'gBall.FillEllipse(SystemBrushes.ControlDark, New Rectangle(0, 0, BallSize, BallSize))
                End Using
                Using tb As New TextureBrush(b)
                    tb.TranslateTransform(BallRect.X, BallRect.Y)
                    g.FillRectangle(tb, BallRect)
                End Using
            End Using

        End Sub

        Public Event PrePaint As EventHandler(Of PaintEventArgs)

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            RaiseEvent PrePaint(Me, e)

            Dim Rect As New Rectangle(Me.SplitterDistance, 0, Me.SplitterWidth - 1, Me.ClientSize.Height - 1)

            If Orientation = Windows.Forms.Orientation.Horizontal Then
                Using m As New System.Drawing.Drawing2D.Matrix
                    'm.RotateAt(90, New Point(Me.ClientSize.Width \ 2, Me.ClientSize.Height \ 2))
                    m.Rotate(90)
                    m.Translate(Me.ClientSize.Width - 1, 0, Drawing2D.MatrixOrder.Append)
                    e.Graphics.Transform = m
                    Rect = New Rectangle(Me.SplitterDistance, 0, Me.SplitterWidth - 1, Me.ClientSize.Width - 1)
                End Using
            End If

            If DrawSplitBorder1 Then
                e.Graphics.DrawLine(SystemPens.ControlDark, Rect.Location, New Point(Rect.X, Rect.Bottom))
                Rect.X += 1
                Rect.Width -= 1
            End If
            If DrawSplitBorder2 Then
                e.Graphics.DrawLine(SystemPens.ControlDark, New Point(Rect.Right, Rect.Y), New Point(Rect.Right, Rect.Bottom))
                Rect.Width -= 1
            End If
            If GripBallCount > 0 Then
                DrawGrip(e.Graphics, Rect, GripBallCount, GripSpacing)
            End If

            'If Orientation = Windows.Forms.Orientation.Vertical Then
            '    Dim Rect As New Rectangle(Me.SplitterDistance, 0, Me.SplitterWidth - 1, Me.ClientSize.Height - 1)
            '    If DrawSplitBorder1 Then
            '        e.Graphics.DrawLine(SystemPens.ControlDark, Rect.Location, New Point(Rect.X, Rect.Bottom))
            '        Rect.X += 1
            '        Rect.Width -= 1
            '    End If
            '    If DrawSplitBorder2 Then
            '        e.Graphics.DrawLine(SystemPens.ControlDark, New Point(Rect.Right, Rect.Y), New Point(Rect.Right, Rect.Bottom))
            '        Rect.Width -= 1
            '    End If
            '    If GripBallCount > 0 Then
            '        DrawGrip(e.Graphics, Rect, GripBallCount, GripSpacing)
            '    End If
            'Else

            'End If

            MyBase.OnPaint(e)

        End Sub

        'to prevent panel 2 moving "outside" the client area!
        Private Sub i00SplitContainer_Resize(sender As Object, e As EventArgs) Handles Me.Resize
            'grr can't do it this way as SplitContainer1.Panel2 is always min width if the view area is actually smaller :(
            'Dim Pnl2Diff = SplitContainer1.Panel2MinSize - SplitContainer1.Panel2.Width
            If Me.Visible AndAlso Panel1Collapsed = False AndAlso Panel2Collapsed = False Then
                If Me.Orientation = Windows.Forms.Orientation.Vertical Then
                    If FixedPanel = Windows.Forms.FixedPanel.Panel2 Then
                        If Panel1.Width + Panel2.Width + SplitterWidth > Me.ClientSize.Width Then
                            If Me.ClientSize.Width > Panel1MinSize + Panel2MinSize + SplitterWidth Then
                                FixedPanel = Windows.Forms.FixedPanel.None
                                SplitterDistance = Panel1MinSize
                                FixedPanel = Windows.Forms.FixedPanel.Panel2
                            End If
                        End If
                    ElseIf FixedPanel = Windows.Forms.FixedPanel.Panel1 Then
                        Dim Pnl2Diff = Me.Panel2MinSize - (Me.ClientSize.Width - Me.Panel1.Width - Me.SplitterWidth)

                        If Pnl2Diff > 0 Then
                            'need to resize splitter to ensure panel 2 is the right size ...
                            '... but first make sure that it's not making panel1 smaller than its min size :( - but this doesn't seem to work here so do check in size changed instead
                            Dim ToBeDistance = Me.SplitterDistance - Pnl2Diff
                            If ToBeDistance < Me.Panel1MinSize Then
                                'let it use its inbuilt sizing
                            Else
                                Me.SplitterDistance = ToBeDistance
                            End If
                        End If
                    End If
                Else
                    If FixedPanel = Windows.Forms.FixedPanel.Panel2 Then
                        If Panel1.Height + Panel2.Height + SplitterWidth > Me.ClientSize.Height Then
                            If Me.ClientSize.Height > Panel1MinSize + Panel2MinSize + SplitterWidth Then
                                FixedPanel = Windows.Forms.FixedPanel.None
                                SplitterDistance = Panel1MinSize
                                FixedPanel = Windows.Forms.FixedPanel.Panel2
                            End If
                        End If
                    ElseIf FixedPanel = Windows.Forms.FixedPanel.Panel1 Then
                        Dim Pnl2Diff = Me.Panel2MinSize - (Me.ClientSize.Height - Me.Panel1.Height - Me.SplitterWidth)

                        If Pnl2Diff > 0 Then
                            Dim ToBeDistance = Me.SplitterDistance - Pnl2Diff
                            If ToBeDistance < Me.Panel1MinSize Then
                                'let it use its inbuilt sizing
                            Else
                                Me.SplitterDistance = ToBeDistance
                            End If
                        End If
                    End If
                End If
            End If
        End Sub

#Region "Show content when sizing"

        Dim OKToSize As Boolean

        Dim MousePosOffset As Integer

        Private Sub splitCont_MouseDown(sender As Object, e As MouseEventArgs) Handles Me.MouseDown
            ' This disables the normal move behaviour
            If e.Button = Windows.Forms.MouseButtons.Left Then
                If ShowContentOnSplitterSize Then
                    OKToSize = Me.IsSplitterFixed = False
                    If OKToSize Then
                        If Me.Orientation = Orientation.Vertical Then
                            MousePosOffset = e.Location.X - Me.SplitterDistance
                        Else
                            MousePosOffset = e.Location.Y - Me.SplitterDistance
                        End If
                        Me.IsSplitterFixed = True
                    End If
                End If
            End If
        End Sub

        ''' <summary>
        ''' This was created as SplitterMoved fires for every change when ShowContentOnSplitterSize = True
        ''' This event could not be intercepted and made to work as expected as OnSplitterMoveCompleted is not Overridable
        ''' </summary>
        Public Event SplitterMoveCompleted As SplitterEventHandler

        Protected Overridable Sub OnSplitterMoveCompleted(e As SplitterEventArgs)
            RaiseEvent SplitterMoveCompleted(Me, e)
        End Sub

        'assign this to the SplitContainer's MouseUp event
        Private Sub splitCont_MouseUp(sender As Object, e As MouseEventArgs) Handles Me.MouseUp
            ' This allows the splitter to be moved normally again
            If OKToSize Then
                Me.IsSplitterFixed = False
                Dim SplitterEventArgs = New SplitterEventArgs(e.X, e.Y, Maths.Cap(Panel1MinSize, e.X, Me.ClientSize.Width - Panel2MinSize), Maths.Cap(Panel1MinSize, e.Y, Me.ClientSize.Height - Panel2MinSize))
                OnSplitterMoved(SplitterEventArgs)
                OnSplitterMoveCompleted(SplitterEventArgs)
            End If
        End Sub

        'assign this to the SplitContainer's MouseMove event
        Private Sub splitCont_MouseMove(sender As Object, e As MouseEventArgs) Handles Me.MouseMove
            If Me.IsSplitterFixed AndAlso OKToSize Then
                If e.Button.Equals(MouseButtons.Left) Then
                    Dim SplitterCancelEventArgs = New SplitterCancelEventArgs(e.X,
                                                                              e.Y,
                                                                              Maths.Cap(Panel1MinSize, e.X - MousePosOffset, Me.ClientSize.Width - Panel2MinSize),
                                                                              Maths.Cap(Panel1MinSize, e.Y - MousePosOffset, Me.ClientSize.Height - Panel2MinSize))
                    MyBase.OnSplitterMoving(SplitterCancelEventArgs)
                    If SplitterCancelEventArgs.Cancel Then
                    Else
                        If Me.Orientation = Orientation.Vertical Then
                            If SplitterCancelEventArgs.SplitX > 0 AndAlso SplitterCancelEventArgs.SplitX < Me.Width Then
                                Me.LockWindowUpdate()
                                Me.SplitterDistance = SplitterCancelEventArgs.SplitX
                                Me.UnlockWindowUpdate()
                                Me.Refresh()
                            End If
                        Else
                            If SplitterCancelEventArgs.SplitY > 0 AndAlso SplitterCancelEventArgs.SplitY < Me.Height Then
                                Me.LockWindowUpdate()
                                Me.SplitterDistance = SplitterCancelEventArgs.SplitY
                                Me.UnlockWindowUpdate()
                                Me.Refresh()
                            End If
                        End If
                    End If
                Else
                    Me.IsSplitterFixed = False
                End If
            End If
        End Sub


#End Region

    End Class

End Namespace