'Codes: https://docs.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences

Partial Class ConsoleEx

    Public Shared ReadOnly Property Format As Formatter
        Get
            Static mc_ As Formatter = New Formatter
            Return mc_
        End Get
    End Property

    Public NotInheritable Class Formatter
        Shared Sub New()
            Try
                Const ENABLE_VIRTUAL_TERMINAL_PROCESSING As UInteger = 4      ' Enables parsing for VT100 ANSI Escape sequences
                'Get the console windows current processing mode
                Dim _ConsoleMode As UInteger = Nothing
                GetConsoleMode(OutputHandle, _ConsoleMode)
                _ConsoleMode = _ConsoleMode Or ENABLE_VIRTUAL_TERMINAL_PROCESSING
                mc_FormattingSupported = SetConsoleMode(OutputHandle, _ConsoleMode)
            Catch ex As Exception
                'does it really matter if we fail? ... just set supported = False
                mc_FormattingSupported = False
            End Try
        End Sub

        Public Class Point
            Public ReadOnly Property Index As Integer
            Public ReadOnly Property ConsoleFormat As String
            Public Sub New(Index As Integer, ConsoleFormat As String)
                If Text.RegularExpressions.Regex.IsMatch(ConsoleFormat, "^(\x1B\[[^m]+m)+$") = False Then
                    Throw New NotSupportedException($"{ConsoleFormat} is not a valid console format string")
                End If
                Me.Index = Index
                Me.ConsoleFormat = ConsoleFormat
            End Sub
        End Class

#Region "APIs"

        <Runtime.InteropServices.DllImport("kernel32.dll")>
        Private Shared Function GetConsoleMode(ByVal hConsoleHandle As IntPtr, ByRef lpMode As UInteger) As Boolean
        End Function

        <Runtime.InteropServices.DllImport("kernel32.dll")>
        Private Shared Function SetConsoleMode(ByVal hConsoleHandle As IntPtr, ByVal dwMode As UInteger) As Boolean
        End Function

#End Region

        Public Class FormatValueInfo
            Inherits Attribute
            Implements ICloneable
            Private Function CloneInternal() As Object Implements ICloneable.Clone
                Return Me.MemberwiseClone()
            End Function
            Friend Function Clone() As FormatValueInfo
                Return DirectCast(CloneInternal(), FormatValueInfo)
            End Function
            Public ReadOnly Property Type As Types
            Public Enum Types
                Negator
                Color
                Bold
                Underline
                Negative
            End Enum
            Public Sub New(Type As Types)
                Me.Type = Type
            End Sub
            Friend _Value As FormatValues
            Public ReadOnly Property Value As FormatValues
                Get
                    Return _Value
                End Get
            End Property
            Public ReadOnly Property NegatorConsoleFormat As String
                Get
                    Return Escape(Negator)
                End Get
            End Property
            Public ReadOnly Property Negator As FormatValues
                Get
                    Select Case Type
                        Case Types.Color
                            If IsBackgroundColor Then
                                Return DirectCast(FormatValues.DefaultColor + BackgroundOffset, FormatValues)
                            Else
                                Return FormatValues.DefaultColor
                            End If
                        Case Types.Bold
                            Return FormatValues.NoBold
                        Case Types.Underline
                            Return FormatValues.NoUnderline
                        Case Types.Negative
                            Return FormatValues.Positive
                        Case Else
                            'shouldn't happen
                            Return FormatValues.Default
                    End Select
                End Get
            End Property
            Friend _IsBackgroundColor As Boolean
            Public ReadOnly Property IsBackgroundColor As Boolean
                Get
                    Return _IsBackgroundColor
                End Get
            End Property
        End Class

        Const BackgroundOffset = 10
        Public Enum FormatValues
            <FormatValueInfo(FormatValueInfo.Types.Negator)>
            [Default] = 0
            <FormatValueInfo(FormatValueInfo.Types.Negative)>
            Negative = 7
            <FormatValueInfo(FormatValueInfo.Types.Negator)>
            Positive = 27

            <FormatValueInfo(FormatValueInfo.Types.Bold)>
            Bold = 1
            <FormatValueInfo(FormatValueInfo.Types.Underline)>
            Underline = 4
            <FormatValueInfo(FormatValueInfo.Types.Negator)>
            NoBold = 22
            <FormatValueInfo(FormatValueInfo.Types.Negator)>
            NoUnderline = 24

            'BaseColors (Foreground - background is Foreground + BackgroundOffset):
            <FormatValueInfo(FormatValueInfo.Types.Negator)>
            [DefaultColor] = 39
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Extended = 38
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Black = 30
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkRed = 31
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkGreen = 32
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkYellow = 33
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkBlue = 34
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkMagenta = 35
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkCyan = 36
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            DarkGray = 90
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Gray = 37
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Red = 91
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Green = 92
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Yellow = 93
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Blue = 94
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Magenta = 95
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            Cyan = 96
            <FormatValueInfo(FormatValueInfo.Types.Color)>
            White = 97
        End Enum

        Const FormatMatchPattern = "\x1B\[(?<Content>\d+)[^m]*m"
        Public Shared Function GetNegatorConsoleFormat(ConsoleFormat As String) As String
            Static mc_AllNegatorsCount As Integer = FormatInfoDictionary.Where(Function(x) x.Value.Type = FormatValueInfo.Types.Negator).
                                                                         Select(Function(x) x.Value.Value).
                                                                         Where(Function(x) x <> FormatValues.Default).
                                                                         Distinct().'< will be anyway!
                                                                         Count()
            Dim Required = Text.RegularExpressions.Regex.Matches(ConsoleFormat, FormatMatchPattern).
                                                         OfType(Of Text.RegularExpressions.Match).
                                                         Select(Function(x) Convert.ToInt32(x.Groups("Content").Value)).
                                                         Distinct().
                                                         Join(FormatInfoDictionary,
                                                              Function(x) x,
                                                              Function(y) y.Key,
                                                              Function(x, y) y.Value).
                                                         Select(Function(x) x.Negator).
                                                         Distinct().
                                                         Where(Function(x) x <> FormatValues.Default).
                                                         ToArray()
            'bundle the negators into one Default, if we are invalidating ALL of them!
            If mc_AllNegatorsCount = Required.Count Then
                Return Escape(FormatValues.Default)
            Else
                Return Join(Required.Select(Function(x) Escape(x)).ToArray, "")
            End If
        End Function
        Public Shared Function GetFormatInfo(ConsoleFormat As String) As FormatValueInfo
            Dim m = Text.RegularExpressions.Regex.Match(ConsoleFormat, FormatMatchPattern)
            If m.Success Then
                Return GetFormatInfo(Convert.ToInt32(m.Groups("Content").Value))
            Else
                Return Nothing
            End If
        End Function

        Private Shared Function FormatInfoDictionary() As Dictionary(Of Integer, FormatValueInfo)
            Static mc_ As Dictionary(Of Integer, FormatValueInfo)
            If mc_ Is Nothing Then
                mc_ = New Dictionary(Of Integer, FormatValueInfo)
                For Each e In [Enum].GetValues(GetType(FormatValues)).OfType(Of FormatValues)
                    Dim Info = e.GetAttribute(Of FormatValueInfo)()
                    Info._Value = e
                    mc_.Add(Info.Value, Info)
                    If Info.Type = FormatValueInfo.Types.Color OrElse e = FormatValues.DefaultColor Then
                        'also create the background one!
                        Dim BackgroundInfo = Info.Clone()
                        BackgroundInfo._Value = DirectCast(BackgroundInfo._Value + BackgroundOffset, FormatValues)
                        BackgroundInfo._IsBackgroundColor = True
                        mc_.Add(BackgroundInfo.Value, BackgroundInfo)
                    End If
                Next
            End If
            Return mc_
        End Function
        Friend Shared Function GetFormatInfo(ConsoleFormat As Integer) As FormatValueInfo
            Dim Returner As FormatValueInfo = Nothing
            FormatInfoDictionary.TryGetValue(ConsoleFormat, Returner)
            Return Returner
        End Function

        Private Shared mc_FormattingSupported As Boolean
        Public Shared ReadOnly Property FormattingSupported As Boolean
            Get
                Return mc_FormattingSupported
            End Get
        End Property

        Const EscapeStart = ChrW(27) & "["
        Const EscapeEnd = "m"
        Private Shared Function Escape(Code As Object) As String
            If FormattingSupported Then
                If TypeOf Code Is FormatValues Then
                    Return $"{EscapeStart}{CInt(Code)}{EscapeEnd}"
                Else
                    Return $"{EscapeStart}{Code}{EscapeEnd}"
                End If
            Else
                'Don't want to print literal rubbish to the console!
                Return ""
            End If
        End Function

        Public NotInheritable Class Styles
            Friend Sub New()
            End Sub
            Public ReadOnly Property Bold As String = Escape(FormatValues.Bold)
            Public ReadOnly Property Underline As String = Escape(FormatValues.Underline)
            Public ReadOnly Property NoBold As String = Escape(FormatValues.NoBold)
            Public ReadOnly Property NoUnderline As String = Escape(FormatValues.NoUnderline)
        End Class
        Public NotInheritable Class Colors
            Private Sub New()
            End Sub

            ''Set of colors is listed here:
            ''https://en.wikipedia.org/wiki/ANSI_escape_code

            Dim Offset As Integer = 0
            Friend Sub New(IsBackground As Boolean)
                If IsBackground Then Offset = BackgroundOffset
                [Default] = Escape(FormatValues.DefaultColor + Offset)
                Black = Escape(FormatValues.Black + Offset)
                DarkRed = Escape(FormatValues.DarkRed + Offset)
                DarkGreen = Escape(FormatValues.DarkGreen + Offset)
                DarkYellow = Escape(FormatValues.DarkYellow + Offset)
                DarkBlue = Escape(FormatValues.DarkBlue + Offset)
                DarkMagenta = Escape(FormatValues.DarkMagenta + Offset)
                DarkCyan = Escape(FormatValues.DarkCyan + Offset)
                DarkGray = Escape(FormatValues.DarkGray + Offset)
                Gray = Escape(FormatValues.Gray + Offset)
                Red = Escape(FormatValues.Red + Offset)
                Green = Escape(FormatValues.Green + Offset)
                Yellow = Escape(FormatValues.Yellow + Offset)
                Blue = Escape(FormatValues.Blue + Offset)
                Magenta = Escape(FormatValues.Magenta + Offset)
                Cyan = Escape(FormatValues.Cyan + Offset)
                White = Escape(FormatValues.White + Offset)
            End Sub
            ''' <summary>
            ''' Applies an extended color value to the background
            ''' </summary>
            ''' <returns></returns>
            Public Function Custom(Color As Color) As String
                'Extended:
                Return Escape($"{FormatValues.Extended + Offset};2;{Color.R};{Color.G};{Color.B}")
            End Function

            ''' <summary>
            ''' Resets the color back to the console's default
            ''' </summary>
            ''' <returns></returns>
            Public ReadOnly Property [Default] As String
            Public ReadOnly Property Black As String
            Public ReadOnly Property DarkRed As String
            Public ReadOnly Property DarkGreen As String
            Public ReadOnly Property DarkYellow As String
            Public ReadOnly Property DarkBlue As String
            Public ReadOnly Property DarkMagenta As String
            Public ReadOnly Property DarkCyan As String
            Public ReadOnly Property DarkGray As String
            Public ReadOnly Property Gray As String
            Public ReadOnly Property Red As String
            Public ReadOnly Property Green As String
            Public ReadOnly Property Yellow As String
            Public ReadOnly Property Blue As String
            Public ReadOnly Property Magenta As String
            Public ReadOnly Property Cyan As String
            Public ReadOnly Property White As String
        End Class

        ''' <summary>
        ''' Returns all attributes to the default state prior to modification
        ''' </summary>
        ''' <returns></returns>
        Public ReadOnly Property [Default] As String = Escape(FormatValues.Default)
        Public ReadOnly Property Negative As String = Escape(FormatValues.Negative)
        Public ReadOnly Property Positive As String = Escape(FormatValues.Positive)
        Public ReadOnly Property Style As Styles = New Styles()
        Public ReadOnly Property Foreground As Colors = New Colors(False)
        Public ReadOnly Property Background As Colors = New Colors(True)

    End Class

End Class