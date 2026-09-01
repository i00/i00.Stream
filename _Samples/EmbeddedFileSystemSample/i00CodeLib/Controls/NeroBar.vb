Imports System.ComponentModel
Imports System.Drawing.Drawing2D

Namespace Controls

    <System.ComponentModel.DesignerCategory("")>
    <Serializable()>
    <DefaultProperty("Value")>
    Public Class NeroBar
        Inherits UserControl

#Region "Credits"

        'The codeproject article for this control can be found at:
        'http://www.codeproject.com/KB/progress/NeroBar.aspx

        'This control is a vastly enhanced version based on the VistaProgressBar found at:
        'http://www.openwinforms.com/vista_style_progress_bar.html

        'Glow animation based on code found at:
        'http://www.codeproject.com/KB/cpp/VistaProgressBar.aspx
        'Thanks to Xasthom

        'Color correction code (original author unknown) provided by Four13 Designs.
        'Thanks a lot! And thanks to the original author as well!

#End Region

#Region "Version History"

        'VERSION HISTORY:
        'VERSION 1.3 (December 10th, 2008)
        'Added:
        '    This version history
        '    Enhancement: 100% user defined colors
        '    Enhancement: When setting "Value" to a value higher than "MaxValue", the percentagetext will show "> 100%" (can be more than 100% as well depending on the PercentageBasedOn setting)
        '    Enhancement: Added alpha channel transparency to user selected GlowColor to "smoothen" the glow
        '    Enhancement: Progress percentage text can now be aligned usign the TextAlign property
        '    New Feature: Progress percentage calculation can be based on segments - not just the whole control width
        '    New Feature: ColorChangeMode - you can now choose if the whole bar should change color when a threshold is passed - or only the segments as before
        '    New Feature: RightToLeft mode
        '    New Properties: PercentageBasedOn, RightToLeft, ColorChangeMode, TextAlign
        '    Custom Visual Studio toobox icon
        '    Custom icon for Demo application
        'Removed:
        '    The six hardcoded colors
        'Bug fix:
        '   Control crashed when Value>0 and Value<0.5
        'Irrelevant inherited base properties hidden from designer property grid
        'Updated demo project

        'VERSION 1.2 (December 4th, 2008)
        'Added:
        '    Animated glow
        '    Progress percentage
        '    New Properties: GlowMode, GlowSpeed, GlowPause, GlowColor, PercentageShow
        '    Progress percetage text can be customized using the NeroBar's Font and ForeColor Properties
        'Updated demo project
        'Code restructured with Regions for better readbility

        'VERSION 1.1 (November 26th, 2008)
        'Added: 
        '    Two new colors: Cyan and Purple
        '    A NeroBarToolStripMenuItem control 
        '    New property: ColorThresholds 
        'Updated demo project

        'VERSION 1.0 (November 25th, 2008) 
        'Initial release 

#End Region

#Region "Public Enums"

        Public Enum NeroBarSegments
            One = 1
            Two = 2
            Three = 3
        End Enum

        Public Enum NeroBarGlowModes
            None = 0
            ProgressOnly = 1
            WholeBar = 2
        End Enum

        Public Enum NeroBarPercentageCalculationModes
            Segment_1 = 1
            Segments_1_2 = 2
            WholeControl = 3
        End Enum

        Public Enum NeroBarColorChangeModes
            Segments = 0
            WholeBar = 1
        End Enum

#End Region

#Region "Private variables etc"

        Private _borderColor As Color = Color.FromArgb(178, 178, 178)
        Private _backRemain1 As Color = Color.FromArgb(202, 202, 202)
        Private _backRemain2 As Color = Color.FromArgb(234, 234, 234)
        Private _backRemain3 As Color = Color.FromArgb(219, 219, 219)
        Private _backRemain4 As Color = Color.FromArgb(243, 243, 243)
        Private _segment1Color As Color = SystemColors.Highlight
        Private _segment2Color As Color = Color.FromArgb(252, 252, 0)
        Private _segment3Color As Color = Color.FromArgb(252, 0, 0)
        Private _glowColor As Color = SystemColors.Highlight

        Private _segmentCount As NeroBarSegments = NeroBarSegments.One
        Private _value As Double = 0
        Private _maxValue As Double = 100
        Private _minValue As Double = 0
        Private _segment2StartThreshold As Double = 80
        Private _segment3StartThreshold As Double = 90
        Private _drawThresholds As Boolean = True
        Private _colorThresholds As Boolean = False
        Private _glowMode As NeroBarGlowModes = NeroBarGlowModes.None
        Private _glowSpeed As Integer = 1
        Private _glowPause As Integer = 1
        Private _glowPosition As Integer = -GlowWidth
        Private _percentageMode As NeroBarPercentageCalculationModes = NeroBarPercentageCalculationModes.WholeControl
        Private _rightToLeft As Boolean = False
        Private _changeMode As NeroBarColorChangeModes = NeroBarColorChangeModes.Segments

        Private WithEvents tmrAnimateGlow As New System.Timers.Timer
        Private WithEvents tmrGlowPause As New System.Timers.Timer
        Private lblPercent As New Label
        Private sTooLarge As String = ""

#End Region

#Region "Properties"

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(CDbl(0))> _
        <Category("NeroBar")> _
        <Description("The NeroBar's value.")> _
        Public Property Value() As Double
            Get
                Return _value
            End Get
            Set(ByVal value As Double)
                If value <> _value Then
                    _value = value
                    sTooLarge = ""
                    If _value > _maxValue Then
                        _value = _maxValue
                        sTooLarge = "> "
                    End If
                    If _value < _minValue Then
                        _value = _minValue
                    End If
                    Me.Invalidate()
                End If
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(CDbl(100))> _
        <Category("NeroBar")> _
        <Description("The NeroBar's maximum value.")> _
        Public Property MaxValue() As Double
            Get
                Return _maxValue
            End Get
            Set(ByVal value As Double)
                _maxValue = value
                If _value > value Then
                    _value = value
                End If
                If _segment2StartThreshold > value Then
                    _segment2StartThreshold = value
                End If
                If _segment3StartThreshold > value Then
                    _segment3StartThreshold = value
                End If
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(CDbl(0))> _
        <Category("NeroBar")> _
        <Description("The NeroBar's minimum value.")> _
        Public Property MinValue() As Double
            Get
                Return _minValue
            End Get
            Set(ByVal value As Double)
                _minValue = value
                If _value < value Then
                    _value = value
                End If
                If _segment2StartThreshold < value Then
                    _segment2StartThreshold = value
                End If
                If _segment3StartThreshold < value Then
                    _segment3StartThreshold = value
                End If
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(True)> _
        <Category("NeroBar")> _
        <Description("Determines if the thresholds should be shown or not.")> _
        Public Property DrawThresholds() As Boolean
            Get
                Return _drawThresholds
            End Get
            Set(ByVal value As Boolean)
                _drawThresholds = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(False)> _
        <Category("NeroBar")> _
        <Description("Determines if the thresholds should be colored according to the selected segment color or not.")> _
        Public Property ColorThresholds() As Boolean
            Get
                Return _colorThresholds
            End Get
            Set(ByVal value As Boolean)
                _colorThresholds = value
                Me.Invalidate()
            End Set
        End Property


        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(GetType(NeroBar.NeroBarSegments), "One")> _
        <Category("NeroBar")> _
        <Description("The number of segments - between one and three.")> _
        Public Property SegmentCount() As NeroBarSegments
            Get
                Return _segmentCount
            End Get
            Set(ByVal value As NeroBarSegments)
                _segmentCount = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("NeroBar")> _
        <DefaultValue(GetType(Color), "Highlight")> _
        <Description("The color of the first segment.")> _
        Public Property Segment1Color() As Color
            Get
                Return _segment1Color
            End Get
            Set(ByVal value As Color)
                _segment1Color = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("NeroBar")> _
        <DefaultValue(GetType(Color), "252, 252, 0")> _
        <Description("The color of the second segment.")> _
        Public Property Segment2Color() As Color
            Get
                Return _segment2Color
            End Get
            Set(ByVal value As Color)
                _segment2Color = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("NeroBar")> _
        <DefaultValue(GetType(Color), "252, 0, 0")> _
        <Description("The color of the third segment.")> _
        Public Property Segment3Color() As Color
            Get
                Return _segment3Color
            End Get
            Set(ByVal value As Color)
                _segment3Color = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("NeroBar")> _
        <DefaultValue(GetType(NeroBarColorChangeModes), "Segments")> _
        <Description("Determines if the WHOLE bar should change color when threshold is passed or only the next segment.")> _
        Public Property ColorChangeMode() As NeroBarColorChangeModes
            Get
                Return _changeMode
            End Get
            Set(ByVal value As NeroBarColorChangeModes)
                _changeMode = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(CDbl(80))> _
        <Category("NeroBar")> _
        <Description("The lower threshold (starting position) of the second segment.")> _
        Public Property Segment2StartThreshold() As Double
            Get
                Return _segment2StartThreshold
            End Get
            Set(ByVal value As Double)
                If _segmentCount <> NeroBarSegments.One Then
                    _segment2StartThreshold = value
                    Me.Invalidate()
                End If
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(CDbl(90))> _
        <Category("NeroBar")> _
        <Description("The lower threshold (starting position) of the third segment.")> _
        Public Property Segment3StartThreshold() As Double
            Get
                Return _segment3StartThreshold
            End Get
            Set(ByVal value As Double)
                If _segmentCount = NeroBarSegments.Three Then
                    _segment3StartThreshold = value
                    Me.Invalidate()
                End If
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(GetType(NeroBarGlowModes), "None")> _
        <Category("NeroBar")> _
        <Description("Determines if the NeroBar should have an animated glow or not.")> _
        Public Property GlowMode() As NeroBarGlowModes
            Get
                Return _glowMode
            End Get
            Set(ByVal value As NeroBarGlowModes)
                _glowMode = value
                If _glowMode <> NeroBarGlowModes.None Then
                    If Me.Visible AndAlso tmrAnimateGlow IsNot Nothing Then
                        tmrAnimateGlow.Enabled = True
                    End If
                    tmrGlowPause.Enabled = False
                Else
                    If _rightToLeft Then
                        _glowPosition = Me.Width + GlowWidth
                    Else
                        _glowPosition = -GlowWidth
                    End If
                    tmrAnimateGlow.Enabled = False
                    tmrGlowPause.Enabled = False
                End If
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(1)> _
        <Category("NeroBar")> _
        <Description("The time between the glow advances in milliseconds. Lower value -> Higher Speed etc.")> _
        Public Property GlowSpeed() As Integer
            Get
                Return _glowSpeed
            End Get
            Set(ByVal value As Integer)
                If value <= 0 Then
                    value = 1
                End If
                _glowSpeed = value
                If tmrAnimateGlow.Enabled Then
                    tmrAnimateGlow.Enabled = False
                    tmrAnimateGlow.Interval = _glowSpeed
                    If Me.Visible Then
                        tmrAnimateGlow.Enabled = True
                    End If
                Else
                    tmrAnimateGlow.Interval = _glowSpeed
                End If
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(1)> _
        <Category("NeroBar")> _
        <Description("The pause between the glow animations in milliseconds.")> _
        Public Property GlowPause() As Integer
            Get
                Return _glowPause
            End Get
            Set(ByVal value As Integer)
                If value <= 0 Then
                    value = 1
                End If
                _glowPause = value
                If tmrGlowPause.Enabled Then
                    tmrGlowPause.Enabled = False
                    tmrGlowPause.Interval = _glowPause
                    If Me.Visible Then
                        tmrGlowPause.Enabled = True
                    End If
                Else
                    tmrGlowPause.Interval = _glowPause
                End If
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("NeroBar")> _
        <DefaultValue(GetType(Color), "Highlight")> _
        <Description("The color of the animated glow.")> _
        Public Property GlowColor() As Color
            Get
                Return _glowColor
            End Get
            Set(ByVal value As Color)
                _glowColor = Color.FromArgb(150, value.R, value.G, value.B)
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(False)> _
        <Category("NeroBar")> _
        <Description("Determines if progress percentage should be shown or not.")> _
        Public Property PercentageShow() As Boolean
            Get
                Return lblPercent.Visible
            End Get
            Set(ByVal value As Boolean)
                lblPercent.Visible = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(GetType(NeroBarPercentageCalculationModes), "Segments_1_2_3")> _
        <Category("NeroBar")> _
        <Description("Determines if progress percentage should be calculated based on the segment first segment(s) or the whole control width.")> _
        Public Property PercentageBasedOn() As NeroBarPercentageCalculationModes
            Get
                Return _percentageMode
            End Get
            Set(ByVal value As NeroBarPercentageCalculationModes)
                _percentageMode = value
                Me.Invalidate()
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(GetType(Color), "ControlText")> _
        <Category("Appearance")> _
        <Description("The forecolor of the Percentage text.")> _
        Public Overrides Property ForeColor() As Color
            Get
                Return MyBase.ForeColor
            End Get
            Set(ByVal value As Color)
                MyBase.ForeColor = value
                lblPercent.ForeColor = value
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("Appearance")> _
        <Description("The Percentage text font.")> _
        Public Overrides Property Font() As Font
            Get
                Return MyBase.Font
            End Get
            Set(ByVal value As Font)
                MyBase.Font = value
                lblPercent.Font = value
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <Category("Appearance")> _
        <Description("The alignment of the Percentage text.")> _
        Public Property TextAlign() As System.Drawing.ContentAlignment
            Get
                Return lblPercent.TextAlign
            End Get
            Set(ByVal value As System.Drawing.ContentAlignment)
                lblPercent.TextAlign = value
            End Set
        End Property

        <Localizable(False)> _
        <Bindable(False)> _
        <Browsable(True)> _
        <DefaultValue(False)> _
        <Category("Nerobar")> _
        <Description("If true, the bar will be filled from right to left instead of left to right.")> _
        Public Shadows Property RightToLeft() As Boolean
            Get
                Return _rightToLeft
            End Get
            Set(ByVal value As Boolean)
                _rightToLeft = value
                If _glowPosition = -GlowWidth Then _glowPosition = Me.Width + GlowWidth
                Me.Invalidate()
            End Set
        End Property

#Region "Hide irrelevant base properties"

        <Browsable(False)> _
        Public Overrides Property AllowDrop() As Boolean
            Get
                Return MyBase.AllowDrop
            End Get
            Set(ByVal value As Boolean)
                MyBase.AllowDrop = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property AutoScroll() As Boolean
            Get
                Return MyBase.AutoScroll
            End Get
            Set(ByVal value As Boolean)
                MyBase.AutoScroll = value
            End Set
        End Property

        <Browsable(False)> _
        Public Shadows Property AutoScrollMargin() As Size
            Get
                Return MyBase.AutoScrollMargin
            End Get
            Set(ByVal value As Size)
                MyBase.AutoScrollMargin = value
            End Set
        End Property

        <Browsable(False)> _
        Public Shadows Property AutoScrollMinSize() As Size
            Get
                Return MyBase.AutoScrollMinSize
            End Get
            Set(ByVal value As Size)
                MyBase.AutoScrollMinSize = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property AutoScrollOffset() As System.Drawing.Point
            Get
                Return MyBase.AutoScrollOffset
            End Get
            Set(ByVal value As System.Drawing.Point)
                MyBase.AutoScrollOffset = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property AutoSize() As Boolean
            Get
                Return MyBase.AutoSize
            End Get
            Set(ByVal value As Boolean)
                MyBase.AutoSize = value
            End Set
        End Property

        <Browsable(False)> _
        Public Shadows Property AutoSizeMode() As Windows.Forms.AutoSizeMode
            Get
                Return MyBase.AutoSizeMode
            End Get
            Set(ByVal value As Windows.Forms.AutoSizeMode)
                MyBase.AutoSizeMode = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property AutoValidate() As System.Windows.Forms.AutoValidate
            Get
                Return MyBase.AutoValidate
            End Get
            Set(ByVal value As System.Windows.Forms.AutoValidate)
                MyBase.AutoValidate = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property BackColor() As System.Drawing.Color
            Get
                Return MyBase.BackColor
            End Get
            Set(ByVal value As System.Drawing.Color)
                MyBase.BackColor = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property BackgroundImage() As System.Drawing.Image
            Get
                Return MyBase.BackgroundImage
            End Get
            Set(ByVal value As System.Drawing.Image)
                MyBase.BackgroundImage = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property BackgroundImageLayout() As System.Windows.Forms.ImageLayout
            Get
                Return MyBase.BackgroundImageLayout
            End Get
            Set(ByVal value As System.Windows.Forms.ImageLayout)
                MyBase.BackgroundImageLayout = value
            End Set
        End Property

        <Browsable(False)> _
        Public Shadows Property BorderStyle() As System.Windows.Forms.BorderStyle
            Get
                Return MyBase.BorderStyle
            End Get
            Set(ByVal value As System.Windows.Forms.BorderStyle)
                MyBase.BorderStyle = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property ContextMenuStrip() As System.Windows.Forms.ContextMenuStrip
            Get
                Return MyBase.ContextMenuStrip
            End Get
            Set(ByVal value As System.Windows.Forms.ContextMenuStrip)
                MyBase.ContextMenuStrip = value
            End Set
        End Property

        <Browsable(False)> _
        Public Shadows Property CausesValidation() As Boolean
            Get
                Return MyBase.CausesValidation
            End Get
            Set(ByVal value As Boolean)
                MyBase.CausesValidation = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property MaximumSize() As System.Drawing.Size
            Get
                Return MyBase.MaximumSize
            End Get
            Set(ByVal value As System.Drawing.Size)
                MyBase.MaximumSize = value
            End Set
        End Property

        <Browsable(False)> _
        Public Overrides Property MinimumSize() As System.Drawing.Size
            Get
                Return MyBase.MinimumSize
            End Get
            Set(ByVal value As System.Drawing.Size)
                MyBase.MinimumSize = value
            End Set
        End Property

#End Region

#End Region

#Region "Constructor"

        Public Sub New()
            Me.Name = "NeroBar"
            Me.Size = New System.Drawing.Size(307, 15)
            Me.SetStyle(ControlStyles.DoubleBuffer, True)
            Me.SetStyle(ControlStyles.AllPaintingInWmPaint, True)
            Me.SetStyle(ControlStyles.ResizeRedraw, True)
            Me.SetStyle(ControlStyles.UserPaint, True)
            Me.SetStyle(ControlStyles.SupportsTransparentBackColor, True)
            Me.BackColor = Color.Transparent
            Me.tmrAnimateGlow.Interval = _glowSpeed
            Me.tmrAnimateGlow.Enabled = False
            Me.tmrGlowPause.Interval = _glowPause
            Me.tmrGlowPause.Enabled = False
            Me.SuspendLayout()
            With lblPercent
                .Dock = System.Windows.Forms.DockStyle.Fill
                .ForeColor = System.Drawing.SystemColors.ControlText
                .Location = New System.Drawing.Point(0, 0)
                .Name = "PercentLabel"
                .Size = New System.Drawing.Size(307, 15)
                .TabIndex = 0
                .TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                .Visible = False
            End With
            Me.Controls.Add(lblPercent)
            Me.ResumeLayout(False)
        End Sub

#End Region

#Region "Overridden Methods"

        Protected Overrides Sub OnPaintBackground(ByVal e As System.Windows.Forms.PaintEventArgs)
            MyBase.OnPaintBackground(e)

            Dim rectUpper = New RectangleF(0, 0, Me.Width, CSng(Me.Height / 2 + 2))
            Dim rectLower = New RectangleF(0, CSng(Me.Height / 2), Me.Width, CSng(Me.Height - (Me.Height / 2)))

            Dim pathLower As GraphicsPath = GetRoundPath(rectLower, 2)
            Dim pathUpper As GraphicsPath = GetRoundPath(rectUpper, 2)

            Using brushUpper As Brush = New LinearGradientBrush(rectUpper, _backRemain4, _backRemain3, LinearGradientMode.Vertical)
                e.Graphics.FillPath(brushUpper, pathUpper)
            End Using

            Using brushLower As Brush = New LinearGradientBrush(rectLower, _backRemain1, _backRemain2, LinearGradientMode.Vertical)
                e.Graphics.FillPath(brushLower, pathLower)
            End Using

            If _drawThresholds Then
                Dim linePen As Pen = Nothing
                Dim pos As Integer

                If _segmentCount = NeroBarSegments.Two Or SegmentCount = NeroBarSegments.Three Then
                    If _colorThresholds Then
                        linePen = New Pen(_segment2Color, 1)
                    Else
                        linePen = New Pen(_borderColor, 1)
                    End If

                    If _rightToLeft Then
                        pos = Me.Width - CInt(((CDbl(Me.Width) - 2) * (_segment2StartThreshold - _minValue)) / (_maxValue - _minValue))
                    Else
                        pos = CInt(((CDbl(Me.Width) - 2) * (_segment2StartThreshold - _minValue)) / (_maxValue - _minValue))
                    End If

                    e.Graphics.DrawLine(linePen, pos, 0, pos, Me.Height)
                End If
                If _segmentCount = NeroBarSegments.Three Then
                    If _colorThresholds Then
                        linePen = New Pen(_segment3Color, 1)
                    Else
                        linePen = New Pen(_borderColor, 1)
                    End If

                    If _rightToLeft Then
                        pos = Me.Width - CInt(((CDbl(Me.Width) - 2) * (_segment3StartThreshold - _minValue)) / (_maxValue - _minValue))
                    Else
                        pos = CInt(((CDbl(Me.Width) - 2) * (_segment3StartThreshold - _minValue)) / (_maxValue - _minValue))
                    End If

                    e.Graphics.DrawLine(linePen, pos, 0, pos, Me.Height)
                End If
            End If
        End Sub

        Private ReadOnly Property GlowWidth As Integer
            Get
                Return Me.Width
            End Get
        End Property

        Protected Overrides Sub OnPaint(ByVal e As System.Windows.Forms.PaintEventArgs)

            Dim backActive1 As Color
            Dim backActive2 As Color
            Dim backActive3 As Color
            Dim backActive4 As Color
            Dim width As Double
            Dim prevWidth As Double
            Dim offset As Double
            Dim rectUpper As RectangleF
            Dim pathUpper As GraphicsPath
            Dim rectLower As RectangleF
            Dim pathLower As GraphicsPath
            Dim Percent As Integer = 0

            Dim corrFactor2To1 As Single = -0.225
            Dim corrFactor2To3 As Single = 0.5
            Dim corrFactor2To4 As Single = 0.8

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic

            'Draw segment 1
            If (_maxValue - _minValue) = 0 Then
                width = 0
            Else
                width = ((CDbl(Me.Width) - 2) * (_value - _minValue)) / (_maxValue - _minValue)
            End If

            If _changeMode = NeroBarColorChangeModes.Segments Then
                If _segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three Then
                    If _value > _segment2StartThreshold Then
                        width = ((CDbl(Me.Width) - 2) * (_segment2StartThreshold - _minValue)) / (_maxValue - _minValue)
                    End If
                End If
            End If

            prevWidth = width

            If CInt(width) > 0 Then
                If _rightToLeft Then
                    rectUpper = New RectangleF(CSng(Me.Width - width), 1, CSng(width), CSng(Me.Height / 2 + 1))
                    If _segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three Then
                        If _value < _segment2StartThreshold Then
                            pathUpper = GetRoundPath(rectUpper, 1)
                        Else
                            pathUpper = GetRightRoundPath(rectUpper, 1)
                        End If
                    Else
                        pathUpper = GetRoundPath(rectUpper, 1)
                    End If

                    rectLower = New RectangleF(CSng(Me.Width - width), CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                    If _segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three Then
                        If _value < _segment2StartThreshold Then
                            pathLower = GetRoundPath(rectLower, 1)
                        Else
                            pathLower = GetRightRoundPath(rectLower, 1)
                        End If
                    Else
                        pathLower = GetRoundPath(rectLower, 1)
                    End If
                Else
                    rectUpper = New RectangleF(1, 1, CSng(width), CSng(Me.Height / 2 + 1))
                    If _segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three Then
                        If _value < _segment2StartThreshold Then
                            pathUpper = GetRoundPath(rectUpper, 1)
                        Else
                            pathUpper = GetLeftRoundPath(rectUpper, 1)
                        End If
                    Else
                        pathUpper = GetRoundPath(rectUpper, 1)
                    End If

                    rectLower = New RectangleF(1, CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                    If _segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three Then
                        If _value < _segment2StartThreshold Then
                            pathLower = GetRoundPath(rectLower, 1)
                        Else
                            pathLower = GetLeftRoundPath(rectLower, 1)
                        End If
                    Else
                        pathLower = GetRoundPath(rectLower, 1)
                    End If
                End If

                backActive1 = CreateColorWithCorrectedLightness(_segment1Color, corrFactor2To1)
                backActive2 = _segment1Color
                backActive3 = CreateColorWithCorrectedLightness(_segment1Color, corrFactor2To3)
                backActive4 = CreateColorWithCorrectedLightness(_segment1Color, corrFactor2To4)

                If _changeMode = NeroBarColorChangeModes.WholeBar And _segmentCount <> NeroBarSegments.One Then
                    If _segmentCount = NeroBarSegments.Two Then
                        If _value > _segment2StartThreshold Then
                            backActive1 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To1)
                            backActive2 = _segment2Color
                            backActive3 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To3)
                            backActive4 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To4)
                        End If
                    ElseIf _segmentCount = NeroBarSegments.Three Then
                        If _value > _segment2StartThreshold Then
                            backActive1 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To1)
                            backActive2 = _segment2Color
                            backActive3 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To3)
                            backActive4 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To4)
                        End If
                        If _value > _segment3StartThreshold Then
                            backActive1 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To1)
                            backActive2 = _segment3Color
                            backActive3 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To3)
                            backActive4 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To4)
                        End If
                    End If
                End If

                Using brushUpper As Brush = New LinearGradientBrush(rectUpper, backActive4, backActive3, LinearGradientMode.Vertical)
                    e.Graphics.FillPath(brushUpper, pathUpper)
                End Using

                Using brushLower As Brush = New LinearGradientBrush(rectLower, backActive1, backActive2, LinearGradientMode.Vertical)
                    e.Graphics.FillPath(brushLower, pathLower)
                End Using
            End If

            If (_segmentCount = NeroBarSegments.Two Or _segmentCount = NeroBarSegments.Three) And _changeMode = NeroBarColorChangeModes.Segments Then

                'Draw segment 2
                width = ((CDbl(Me.Width) - 2) * (_value - _minValue)) / (_maxValue - _minValue)

                If _segmentCount = NeroBarSegments.Three Then
                    If _value > _segment3StartThreshold Then
                        width = ((CDbl(Me.Width) - 2) * (_segment3StartThreshold - _minValue)) / (_maxValue - _minValue)
                    End If
                End If

                width = width - prevWidth
                offset = prevWidth + 1
                prevWidth = width + prevWidth

                If CInt(width) > 0 Then
                    If _rightToLeft Then
                        rectUpper = New RectangleF(CSng(Me.Width - width - offset), 1, CSng(width), CSng(Me.Height / 2 + 1))
                        If _segmentCount = NeroBarSegments.Three Then
                            If _value < _segment3StartThreshold Then
                                pathUpper = GetLeftRoundPath(rectUpper, 1)
                            Else
                                pathUpper = GetNoRoundPath(rectUpper, 1)
                            End If
                        Else
                            pathUpper = GetLeftRoundPath(rectUpper, 1)
                        End If

                        rectLower = New RectangleF(CSng(Me.Width - width - offset), CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                        If _segmentCount = NeroBarSegments.Three Then
                            If _value < _segment3StartThreshold Then
                                pathLower = GetLeftRoundPath(rectLower, 1)
                            Else
                                pathLower = GetNoRoundPath(rectLower, 1)
                            End If
                        Else
                            pathLower = GetLeftRoundPath(rectLower, 1)
                        End If
                    Else
                        rectUpper = New RectangleF(CSng(offset), 1, CSng(width), CSng(Me.Height / 2 + 1))
                        If _segmentCount = NeroBarSegments.Three Then
                            If _value < _segment3StartThreshold Then
                                pathUpper = GetRightRoundPath(rectUpper, 1)
                            Else
                                pathUpper = GetNoRoundPath(rectUpper, 1)
                            End If
                        Else
                            pathUpper = GetRightRoundPath(rectUpper, 1)
                        End If

                        rectLower = New RectangleF(CSng(offset), CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                        If _segmentCount = NeroBarSegments.Three Then
                            If _value < _segment3StartThreshold Then
                                pathLower = GetRightRoundPath(rectLower, 1)
                            Else
                                pathLower = GetNoRoundPath(rectLower, 1)
                            End If
                        Else
                            pathLower = GetRightRoundPath(rectLower, 1)
                        End If
                    End If

                    backActive1 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To1)
                    backActive2 = _segment2Color
                    backActive3 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To3)
                    backActive4 = CreateColorWithCorrectedLightness(_segment2Color, corrFactor2To4)

                    Using brushUpper As Brush = New LinearGradientBrush(rectUpper, backActive4, backActive3, LinearGradientMode.Vertical)
                        e.Graphics.FillPath(brushUpper, pathUpper)
                    End Using

                    Using brushLower As Brush = New LinearGradientBrush(rectLower, backActive1, backActive2, LinearGradientMode.Vertical)
                        e.Graphics.FillPath(brushLower, pathLower)
                    End Using
                End If
            End If

            If _segmentCount = NeroBarSegments.Three And _changeMode = NeroBarColorChangeModes.Segments Then

                'Draw segment 3
                width = ((CDbl(Me.Width) - 2) * (_value - _minValue)) / (_maxValue - _minValue)
                width = width - prevWidth

                offset = prevWidth + 1

                If CInt(width) > 0 Then
                    If _rightToLeft Then
                        rectUpper = New RectangleF(CSng(Me.Width - width - offset), 1, CSng(width), CSng(Me.Height / 2 + 1))
                        pathUpper = GetLeftRoundPath(rectUpper, 1)

                        rectLower = New RectangleF(CSng(Me.Width - width - offset), CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                        pathLower = GetLeftRoundPath(rectLower, 1)
                    Else
                        rectUpper = New RectangleF(CSng(offset), 1, CSng(width), CSng(Me.Height / 2 + 1))
                        pathUpper = GetRightRoundPath(rectUpper, 1)

                        rectLower = New RectangleF(CSng(offset), CSng(Me.Height / 2), CSng(width), CSng(Me.Height - (Me.Height / 2) - 1))
                        pathLower = GetRightRoundPath(rectLower, 1)
                    End If

                    backActive1 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To1)
                    backActive2 = _segment3Color
                    backActive3 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To3)
                    backActive4 = CreateColorWithCorrectedLightness(_segment3Color, corrFactor2To4)

                    Using brushUpper As Brush = New LinearGradientBrush(rectUpper, backActive4, backActive3, LinearGradientMode.Vertical)
                        e.Graphics.FillPath(brushUpper, pathUpper)
                    End Using

                    Using brushLower As Brush = New LinearGradientBrush(rectLower, backActive1, backActive2, LinearGradientMode.Vertical)
                        e.Graphics.FillPath(brushLower, pathLower)
                    End Using
                End If
            End If

            'Draw border
            Dim rectFull As New Rectangle(0, 0, Me.Width - 1, Me.Height - 1)
            Dim pathFull As GraphicsPath = GetRoundPath(rectFull, 2)

            Using pen As New Pen(_borderColor)
                e.Graphics.DrawPath(pen, pathFull)
            End Using

            'Draw glow
            If _glowMode <> NeroBarGlowModes.None And Not Me.DesignMode Then
                Dim r As New Rectangle(_glowPosition, 0, GlowWidth, Me.Height)
                Dim lgb As New LinearGradientBrush(r, Color.White, Color.White, LinearGradientMode.Horizontal)

                Dim cb As New ColorBlend(4)
                cb.Colors = {Color.Transparent, _glowColor, _glowColor, Color.Transparent}
                cb.Positions = {0.0F, 0.5F, 0.6F, 1.0F}
                lgb.InterpolationColors = cb

                Dim clip As New Rectangle(1, 2, Me.Width - 3, Me.Height - 3)

                If GlowMode = NeroBarGlowModes.ProgressOnly Then
                    clip.Width = CInt(_value * 1.0F / (_maxValue - _minValue) * Me.Width)
                End If
                If _rightToLeft Then
                    clip.X = Me.Width - clip.Width
                End If

                If clip.Width > 0 And clip.Height > 0 And r.Width > 0 And r.Height > 0 Then
                    If GlowMode = NeroBarGlowModes.ProgressOnly Then
                        e.Graphics.SetClip(clip)
                    End If
                    e.Graphics.FillRectangle(lgb, r)
                    e.Graphics.ResetClip()
                End If
            End If

            If lblPercent.Visible Then
                Try
                    Select Case _percentageMode
                        Case NeroBarPercentageCalculationModes.Segment_1
                            If _segmentCount = NeroBarSegments.One Then
                                Percent = CInt((_value * 100) / (_maxValue - _minValue))
                            Else
                                Percent = CInt((_value * 100) / (_segment2StartThreshold - _minValue))
                            End If
                        Case NeroBarPercentageCalculationModes.Segments_1_2
                            If _segmentCount <> NeroBarSegments.Three Then
                                Percent = CInt((_value * 100) / (_maxValue - _minValue))
                            Else
                                Percent = CInt((_value * 100) / (_segment3StartThreshold - _minValue))
                            End If
                        Case NeroBarPercentageCalculationModes.WholeControl
                            Percent = CInt((_value * 100) / (_maxValue - _minValue))
                    End Select
                Catch
                    'Shouldn't happen, but just in case
                End Try

                lblPercent.Text = sTooLarge & String.Format("{0}%", Percent)
                lblPercent.Width = Me.Width
            End If
        End Sub

        'Public Overrides Function GetPreferredSize(ByVal proposedSize As System.Drawing.Size) As System.Drawing.Size
        '    If proposedSize.Width < 100 Then proposedSize.Width = 100
        '    proposedSize.Height = 16
        '    Return proposedSize
        'End Function

#End Region

#Region "Private Methods"

        Private Function GetLeftRoundPath(ByVal r As RectangleF, ByVal depth As Integer) As GraphicsPath
            Dim graphPath As New GraphicsPath

            graphPath.AddArc(r.X, r.Y, depth, depth, 180, 90)
            graphPath.AddLine(r.X + r.Width - depth, r.Y, r.X + r.Width, r.Y)
            graphPath.AddLine(r.X + r.Width, r.Y, r.X + r.Width, r.Y + depth)
            graphPath.AddLine(r.X + r.Width, r.Y + r.Height - depth, r.X + r.Width, r.Y + r.Height)
            graphPath.AddLine(r.X + r.Width, r.Y + r.Height, r.X + r.Width - depth, r.Y + r.Height)
            graphPath.AddArc(r.X, r.Y + r.Height - depth, depth, depth, 90, 90)

            Return graphPath
        End Function

        Private Function GetRightRoundPath(ByVal r As RectangleF, ByVal depth As Integer) As GraphicsPath
            Dim graphPath As New GraphicsPath

            graphPath.AddLine(r.X, r.Y + depth, r.X, r.Y)
            graphPath.AddLine(r.X, r.Y, r.X + depth, r.Y)
            graphPath.AddArc(r.X + r.Width - depth, r.Y, depth, depth, 270, 90)
            graphPath.AddArc(r.X + r.Width - depth, r.Y + r.Height - depth, depth, depth, 0, 90)
            graphPath.AddLine(r.X + depth, r.Y + r.Height, r.X, r.Y + r.Height)
            graphPath.AddLine(r.X, r.Y + r.Height, r.X, r.Y + r.Height - depth)

            Return graphPath
        End Function

        Private Function GetNoRoundPath(ByVal r As RectangleF, ByVal depth As Integer) As GraphicsPath
            Dim graphPath As New GraphicsPath

            graphPath.AddLine(r.X, r.Y + depth, r.X, r.Y)
            graphPath.AddLine(r.X, r.Y, r.X + depth, r.Y)
            graphPath.AddLine(r.X + r.Width - depth, r.Y, r.X + r.Width, r.Y)
            graphPath.AddLine(r.X + r.Width, r.Y, r.X + r.Width, r.Y + depth)
            graphPath.AddLine(r.X + r.Width, r.Y + r.Height - depth, r.X + r.Width, r.Y + r.Height)
            graphPath.AddLine(r.X + r.Width, r.Y + r.Height, r.X + r.Width - depth, r.Y + r.Height)
            graphPath.AddLine(r.X + depth, r.Y + r.Height, r.X, r.Y + r.Height)
            graphPath.AddLine(r.X, r.Y + r.Height, r.X, r.Y + r.Height - depth)

            Return graphPath
        End Function

        Private Function GetRoundPath(ByVal r As RectangleF, ByVal depth As Integer) As GraphicsPath
            Dim graphPath As New GraphicsPath

            graphPath.AddArc(r.X, r.Y, depth, depth, 180, 90)
            graphPath.AddArc(r.X + r.Width - depth, r.Y, depth, depth, 270, 90)
            graphPath.AddArc(r.X + r.Width - depth, r.Y + r.Height - depth, depth, depth, 0, 90)
            graphPath.AddArc(r.X, r.Y + r.Height - depth, depth, depth, 90, 90)
            graphPath.AddLine(r.X, r.Y + r.Height - depth, r.X, r.Y + depth \ 2)

            Return graphPath
        End Function

        Private Function CreateColorWithCorrectedLightness(ByVal originalColor As Color, ByVal correctionFactor As Single) As Color
            If correctionFactor = 0 Then
                Return originalColor
            End If

            Dim red As Single = CSng(originalColor.R)
            Dim green As Single = CSng(originalColor.G)
            Dim blue As Single = CSng(originalColor.B)

            If correctionFactor < 0 Then
                red = red * (1 + correctionFactor)
                green = green * (1 + correctionFactor)
                blue = blue * (1 + correctionFactor)
            Else
                red = (255 - red) * correctionFactor + red
                green = (255 - green) * correctionFactor + green
                blue = (255 - blue) * correctionFactor + blue
            End If

            If red > 255 Then red = 255
            If green > 255 Then green = 255
            If blue > 255 Then blue = 255

            Return Color.FromArgb(originalColor.A, CInt(red), CInt(green), CInt(blue))
        End Function

#End Region

#Region "Timer Event Handlers"

        Private Sub tmrGlowPause_Elapsed(ByVal sender As Object, ByVal e As System.Timers.ElapsedEventArgs) Handles tmrGlowPause.Elapsed
            tmrGlowPause.Enabled = False
            If _glowMode <> NeroBarGlowModes.None Then
                If Me.Visible Then
                    tmrAnimateGlow.Enabled = True
                End If
            Else
                tmrAnimateGlow.Enabled = False
                tmrGlowPause.Enabled = False
                If _rightToLeft Then
                    _glowPosition = Me.Width + GlowWidth
                Else
                    _glowPosition = -GlowWidth
                End If
            End If
        End Sub

        Private Sub tmrAnimateGlow_Elapsed(ByVal sender As Object, ByVal e As System.Timers.ElapsedEventArgs) Handles tmrAnimateGlow.Elapsed
            If Me.Visible Then

                tmrAnimateGlow.Enabled = False
                If _glowMode <> NeroBarGlowModes.None Then
                    If _rightToLeft Then
                        _glowPosition -= 2
                        If _glowPosition < -GlowWidth Then
                            _glowPosition = Me.Width + GlowWidth
                            tmrGlowPause.Enabled = True
                        Else
                            tmrAnimateGlow.Enabled = True
                        End If
                    Else
                        _glowPosition += 2
                        If _glowPosition > Me.Width Then
                            _glowPosition = -GlowWidth
                            tmrGlowPause.Enabled = True
                        Else
                            tmrAnimateGlow.Enabled = True
                        End If
                    End If
                    Me.Invalidate()
                Else
                    tmrAnimateGlow.Enabled = False
                    tmrGlowPause.Enabled = False
                    If _rightToLeft Then
                        _glowPosition = Me.Width + GlowWidth
                    Else
                        _glowPosition = -GlowWidth
                    End If
                End If
            End If
        End Sub

#End Region

        Private Sub NeroBar_Disposed(sender As Object, e As EventArgs) Handles Me.Disposed
            If tmrAnimateGlow IsNot Nothing Then
                tmrAnimateGlow.Dispose()
                tmrAnimateGlow = Nothing
            End If
            If tmrGlowPause IsNot Nothing Then
                tmrGlowPause.Dispose()
                tmrAnimateGlow = Nothing
            End If
        End Sub

        Private Sub NeroBar_VisibleChanged(sender As Object, e As EventArgs) Handles Me.VisibleChanged
            If Me.Visible Then
                GlowMode = GlowMode '< starts the timers
            Else
                tmrAnimateGlow.Enabled = False
                tmrGlowPause.Enabled = False
            End If
        End Sub
    End Class

End Namespace


