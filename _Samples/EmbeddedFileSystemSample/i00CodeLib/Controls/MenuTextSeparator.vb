'i00 .Net Text Separator
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

Namespace Controls

    <ComponentModel.DesignerCategory("")>
    Public Class MenuTextSeparator
        Inherits ToolStripMenuItem 'cannot inherit from ToolStripSeparator as can't make this persistent

#Region "Properties"

#Region "Overridden Properties"

        Public Overrides ReadOnly Property CanSelect() As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property HasDropDownItems() As Boolean
            Get
                Return True
            End Get
        End Property

        Protected Overrides Sub OnOwnerChanged(ByVal e As System.EventArgs)
            MyBase.OnOwnerChanged(e)
            MyBase.Width = OwnerWidth
        End Sub

        Protected Overrides Sub OnPaint(ByVal e As System.Windows.Forms.PaintEventArgs)
            'MyBase.OnPaint(e)
            MenuTextSeparator_Paint(Me, e)
        End Sub

        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Always)>
        <ComponentModel.Bindable(True)>
        <ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)>
        <ComponentModel.BrowsableAttribute(True)>
        Overrides Property Text() As String
            Get
                Return MyBase.Text
            End Get
            Set(ByVal value As String)
                MyBase.Text = value
            End Set
        End Property

        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Always)>
        <ComponentModel.Bindable(True)>
        <ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)>
        <ComponentModel.BrowsableAttribute(True)>
        Overrides Property Font() As Font
            Get
                Return MyBase.Font
            End Get
            Set(ByVal value As Font)
                MyBase.Font = value
                Me.Invalidate()
            End Set
        End Property

        <ComponentModel.DefaultValue(GetType(Color), "ControlDark")>
        Public Overrides Property BackColor() As System.Drawing.Color
            Get
                Return MyBase.BackColor
            End Get
            Set(ByVal value As System.Drawing.Color)
                MyBase.BackColor = value
            End Set
        End Property

        <ComponentModel.DefaultValue(GetType(Color), "ControlLight")>
        Public Overrides Property ForeColor() As System.Drawing.Color
            Get
                Return MyBase.ForeColor
            End Get
            Set(ByVal value As System.Drawing.Color)
                MyBase.ForeColor = value
            End Set
        End Property

        <ComponentModel.BrowsableAttribute(False)>
        Public Overrides Property Size() As System.Drawing.Size
            Get
                Return New Size(OwnerWidth, SeparatorHeight)
            End Get
            Set(ByVal value As System.Drawing.Size)
                MyBase.Size = Me.Size
            End Set
        End Property

#End Region

        Private ReadOnly Property OwnerWidth() As Integer
            Get
                Dim LargestScreenWidth = Screen.AllScreens.Max(Function(x As Screen) x.Bounds.Width)
                Return LargestScreenWidth
                'OwnerWidth = 16 'text width?
                'If Me.Owner IsNot Nothing Then
                '    OwnerWidth = Me.Owner.DisplayRectangle.Right
                'End If
            End Get
        End Property

        Dim mc_UseSystemRender As Boolean = True
        <ComponentModel.DefaultValue(True)>
        Public Property UseSystemRender() As Boolean
            Get
                Return mc_UseSystemRender
            End Get
            Set(ByVal value As Boolean)
                mc_UseSystemRender = value
                Me.Invalidate()
            End Set
        End Property

        Dim mc_LineColor As Color = Color.Transparent
        <ComponentModel.DefaultValue(GetType(Color), "Transparent")>
        Public Property LineColor() As Color
            Get
                Return mc_LineColor
            End Get
            Set(ByVal value As Color)
                mc_LineColor = value
                Me.Invalidate()
            End Set
        End Property

        Dim mc_SeparatorHeight As Integer = 18
        <ComponentModel.DefaultValue(18)>
        Public Property SeparatorHeight() As Integer
            Get
                Return mc_SeparatorHeight
            End Get
            Set(ByVal value As Integer)
                mc_SeparatorHeight = value
            End Set
        End Property

#End Region

#Region "Constructors"

        Public Sub New()
            MyBase.AutoSize = False
            MyBase.Height = 18
        End Sub

        Public Sub New(ByVal Text As String)
            Me.New()
            MyBase.Text = Text
        End Sub

#End Region

#Region "Custom Draw"

        Private Sub MenuTextSeparator_Paint(ByVal sender As Object, ByVal e As System.Windows.Forms.PaintEventArgs) Handles Me.Paint
            On Error Resume Next
            Dim fColor As Color = MyBase.ForeColor
            Dim lColor As Color = Me.LineColor
            Dim bColor As Color = Me.BackColor
            Dim theFont As Font = MyBase.Font
            Dim DisplayText As String = MyBase.Text
            If UseSystemRender Then
                fColor = Drawing.BlendColor(SystemColors.MenuHighlight, SystemColors.MenuText)
                lColor = Drawing.AlphaColor(fColor, 63)
                bColor = Drawing.BlendColor(SystemColors.Menu, fColor, 15)
                'default ToolStripItem font ... but bold
                Using tsi As New ToolStripMenuItem
                    theFont = tsi.Font
                End Using
                If theFont.FontFamily.IsStyleAvailable(FontStyle.Bold) Then
                    theFont = New Font(theFont, FontStyle.Bold)
                End If
                'make the font nice...
                DisplayText = Trim(DisplayText)
                If DisplayText <> "" AndAlso DisplayText.EndsWith(":") = False Then
                    DisplayText &= ":"
                End If
            End If

            Using sb As New SolidBrush(bColor)
                e.Graphics.FillRectangle(sb, New Rectangle(0, 0, OwnerWidth, Me.Height))
            End Using
            Using p As New Pen(lColor)
                e.Graphics.DrawLine(p, 0, Me.Height - 1, OwnerWidth - 1, Me.Height - 1)
            End Using

            Dim OffsetX = 0
            If Me.Image IsNot Nothing Then
                e.Graphics.DrawImage(Me.Image, New Rectangle((Me.Height - 16) \ 2, (Me.Height - 16) \ 2, 16, 16))
                OffsetX += Me.Height
            End If

            Dim FontSizeVert = e.Graphics.MeasureString("Ag", MyBase.Font).Height
            Dim Scale = MyBase.Height / FontSizeVert
            'e.Graphics.ScaleTransform(Scale, Scale)
            Using sb As New SolidBrush(fColor)
                Using f As New Font(theFont.FontFamily, theFont.Size * Scale, theFont.Style, theFont.Unit)
                    e.Graphics.DrawString(DisplayText, f, sb, New Point(OffsetX, 0))
                End Using
            End Using
        End Sub

#End Region

    End Class

End Namespace