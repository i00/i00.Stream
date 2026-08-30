Partial Class ConsoleEx

    Public Shared Property DefaultReadLineOptions As ReadLineOptions

    Public Class ReadLineOptions
        Public Enum EditAction
            None
            Initial
            Move
            Add
            UnDo
            ReDo
            Delete
            Finish
        End Enum

#Region "Jump"

        Public Property StandardJumpCharsInclusive As String = $" {vbTab}{vbCrLf}"
        Public Property StandardJumpCharsExclusive As String

        Friend ReadOnly Property GetStandardJumpForwardPattern As String
            Get
                If StandardJumpCharsInclusive = "" AndAlso StandardJumpCharsExclusive = "" Then
                    Return ""
                Else
                    '\s
                    Dim Inclusive = (StandardJumpCharsInclusive & "").RegexEscape()
                    '\.
                    Dim Exclusive = (StandardJumpCharsExclusive & "").RegexEscape()
                    '[^\s\.]*?[\s]+
                    Dim ptI = If(Inclusive = "", Nothing, $"[^{Inclusive}{Exclusive}]*?[{Inclusive}]+")
                    '[^\s\.]*?(?=[\.])
                    Dim ptE = If(Exclusive = "", Nothing, $"[^{Inclusive}{Exclusive}]*?(?=[{Exclusive}])")
                    'if ptI + ptE:
                    '          ← ptI        → ← ptE           →
                    '   ^[\.]?([^\s\.]*?[\s]+|[^\s\.]*?(?=[\.]))$
                    'if ptI:
                    '   ^     ([^\s  ]*?[\s]+                  )$
                    'if ptE:
                    '   ^[\.]?(               [^  \.]*?(?=[\.]))$
                    Return $"^{If(Exclusive = "", "", $"[{Exclusive}]?")}({ptI}{If(ptI = "" OrElse ptE = "", "", "|")}{ptE})"
                End If
            End Get
        End Property

        Friend ReadOnly Property GetStandardJumpBackPattern As String
            Get
                If StandardJumpCharsInclusive = "" AndAlso StandardJumpCharsExclusive = "" Then
                    Return ""
                Else
                    '   \s
                    Dim Inclusive = (StandardJumpCharsInclusive & "").RegexEscape()
                    '   \.
                    Dim Exclusive = (StandardJumpCharsExclusive & "").RegexEscape()
                    '   [\s]*[^\s\.]*?
                    Dim ptI = If(Inclusive = "", Nothing, $"[{Inclusive}]*[^{Inclusive}{Exclusive}]*?")
                    '   (?<=[\.])[^\s\.]+?
                    Dim ptE = If(Exclusive = "", Nothing, $"(?<=[{Exclusive}])[^{Inclusive}{Exclusive}]+?")
                    'if ptI + ptE:
                    '    ← ptI        → ← ptE            →
                    '   ([\s]*[^\s\.]*?|(?<=[\.])[^\s\.]+?)[\.]?$
                    'if ptI:
                    '   ([\s]*[^\s  ]*?                   )     $
                    'if ptE:
                    '   (               (?<=[\.])[^  \.]+?)[\.]?$
                    Return $"({ptI}{If(ptI = "" OrElse ptE = "", "", "|")}{ptE}){If(Exclusive = "", "", $"[{Exclusive}]?")}$"
                End If
            End Get
        End Property

#End Region

#Region "Highlight"

        Public NotInheritable Class SyntaxHighlighterEventArgs
            Inherits EventArgs
            Public ReadOnly Property TextBefore As String
            Public ReadOnly Property TextAfter As String
            Public ReadOnly Property LineText As String
            Public ReadOnly Property Key As ConsoleKeyInfo
            Public ReadOnly Property EditAction As EditAction
            Friend Sub New(TextBefore As String, TextAfter As String, Key As ConsoleKeyInfo, EditAction As EditAction)
                Me.TextBefore = TextBefore
                Me.TextAfter = TextAfter
                Me.LineText = TextBefore & TextAfter
                Me.Key = Key
                Me.EditAction = EditAction
            End Sub
            Public Property Process As Boolean = True
            Public ReadOnly Property HighlightPoints As New HighlightPointList(Me)

            'Public Property AddPreset As Presets
            Public NotInheritable Class HighlightPointList
                Inherits List(Of Formatter.Point)

                Dim e As SyntaxHighlighterEventArgs
                Friend Sub New(e As SyntaxHighlighterEventArgs)
                    Me.e = e
                End Sub
                Public Overloads Sub Add(Index As Integer, ConsoleFormat As String)
                    MyBase.Add(New Formatter.Point(Index, ConsoleFormat))
                End Sub
                Public Overloads Sub Add(item As Formatter.Point)
                    MyBase.Add(item)
                End Sub

                Public Sub AddRegex(Matches As Text.RegularExpressions.MatchCollection, ConsoleFormat As String)
                    Me.AddRegex(Matches.OfType(Of Text.RegularExpressions.Match), ConsoleFormat)
                End Sub

                Public Sub AddRegex(Matches As IEnumerable(Of Text.RegularExpressions.Match), ConsoleFormat As String)
                    Dim f = Format()
                    Matches.ToList.
                    ForEach(Sub(x)
                                e.HighlightPoints.Add(New Formatter.Point(x.Index, ConsoleFormat))
                                e.HighlightPoints.Add(New Formatter.Point(x.Index + x.Length, Formatter.GetNegatorConsoleFormat(ConsoleFormat)))
                            End Sub)
                End Sub

                Public Sub AddBracket()
                    If e.EditAction <> EditAction.Finish Then
                        'current bracket highlighting
                        Dim NextChar = Left(e.TextAfter, 1)
                        Dim LastOpening = Text.RegularExpressions.Regex.Match(e.TextBefore & If(NextChar = "(", NextChar, ""), "\((?>\((?<c>)|[^()]+|\)(?<-c>))*$(?(c)(?!))")
                        If LastOpening.Success Then
                            'we in a bracket
                            Dim m = Text.RegularExpressions.Regex.Matches(e.LineText, "\(((?<=.{" & LastOpening.Index & "})(?>\((?<c>)|[^()]+|\)(?<-c>)))*(?(c)(?!))\)").
                                                          OfType(Of Text.RegularExpressions.Match).
                                                          FirstOrDefault(Function(x) x.Index = LastOpening.Index)

                            Dim f = Format()
                            Dim ExtraDimGray = f.Background.Custom(Drawing.BlendColor(Color.DimGray, Color.Black))
                            If m?.Success Then
                                e.HighlightPoints.Add(New Formatter.Point(m.Index, f.Background.Custom(Color.DimGray)))
                                e.HighlightPoints.Add(New Formatter.Point(m.Index + 1, ExtraDimGray))
                                e.HighlightPoints.Add(New Formatter.Point(m.Index + m.Length - 1, f.Background.Custom(Color.DimGray)))
                                e.HighlightPoints.Add(New Formatter.Point(m.Index + m.Length, f.Background.Default))
                            Else
                                'we are in a new bracket set
                                e.HighlightPoints.Add(New Formatter.Point(LastOpening.Index, f.Background.Custom(Color.Maroon)))
                                e.HighlightPoints.Add(New Formatter.Point(LastOpening.Index + 1, ExtraDimGray))
                                e.HighlightPoints.Add(New Formatter.Point(e.LineText.Length, f.Background.Default))
                            End If
                        End If
                    End If
                End Sub
            End Class
        End Class
        Public Delegate Sub SyntaxHighlighterDelegate(e As SyntaxHighlighterEventArgs)
        Public Property SyntaxHighlighter As SyntaxHighlighterDelegate

#End Region

        'TODO:
        'Intellisense
        '  - tab to cycle through
        '  - ctrl + up / down to step forward and back through the list
        'Jump
        '  - combine with highlights?
        '  - allow to specify points + a key used to jump (key + ctrl = jump)
        'Intercept
        '  - allow to take full control?
        '  - have built in options to:
        '    - insert string
        '    - change whole string and set cursor position
        'Help notification
        '  - can be set after text changed
        '  - combine with highlights?
        '  - have option to show intellisense options?
    End Class

    Public Shared Function ReadLine(Optional InitialText As String = "", Optional Options As ReadLineOptions = Nothing) As String
        Dim StartLeft = Console.CursorLeft
        Dim StartTop = Console.CursorTop
        Dim LineText As New Text.StringBuilder(InitialText)

        Options = If(Options, If(DefaultReadLineOptions, New ReadLineOptions))

        Dim Highlighter = If(Formatter.FormattingSupported = False OrElse Options.SyntaxHighlighter Is Nothing,
                            Nothing,
                            Sub(e As ReadLineOptions.SyntaxHighlighterEventArgs)
                                Options.SyntaxHighlighter.Invoke(e)
                                If e.Process Then
                                    Using New HoldCursor()
                                        Console.SetCursorPosition(StartLeft, StartTop)
                                        Dim SB As New Text.StringBuilder(LineText.ToString())

                                        'need to call this because, if we have 2x numbers at the same index such that: {1,Pink}{5,Red}{5,Default}
                                        'the items that are of the same index will be reversed if we don't!
                                        '    {1,Pink}{5,Default}{5,Red}
                                        'we want:
                                        '    {1,Pink}{5,Red}{5,Default}
                                        e.HighlightPoints.Reverse()

                                        For Each HighlightPoint In e.HighlightPoints.OrderByDescending(Function(x) x.Index)
                                            SB.Insert(HighlightPoint.Index, HighlightPoint.ConsoleFormat)
                                        Next

                                        Console.Write(SB.ToString())
                                    End Using
                                End If
                            End Sub)

        If InitialText <> "" Then
            Console.Write(InitialText)
        End If

        Dim UndoList = {New With {.Text = "", .Index = 0}}.Take(0).ToList
        Dim UndoListEndOffset = 1
        Dim EditAction = ReadLineOptions.EditAction.Initial
        Do
            Dim Left = Console.CursorLeft
            Dim Top = Console.CursorTop

            Dim PositionInString = If(Top = StartTop, Left - StartLeft, (Console.BufferWidth - StartLeft) + ((Top - StartTop - 1) * Console.BufferWidth) + (Left))
            Dim PreString = LineText.ToString(0, PositionInString)
            Dim PostString = LineText.ToString(PositionInString, (LineText.Length - PositionInString))

            Dim Key As ConsoleKeyInfo
            If Highlighter IsNot Nothing AndAlso EditAction <> ReadLineOptions.EditAction.None Then
                Highlighter(New ReadLineOptions.SyntaxHighlighterEventArgs(PreString, PostString, Key, EditAction))
            End If

            If EditAction = ReadLineOptions.EditAction.Finish Then
                'get bottom of the text and goto the next line
                '... we do this as Console.CursorTop +1 will not be correct if there are multiple lines and the cursor is not on the last one
                Dim Offset = GetCursorOffset(LineText.Length, StartLeft, StartTop)

                'Don't know why I have to add 2 to the buffer here ... but otherwise SetCursorPosition fails?!
                '...also - this single line is technically only required in Windows 11+ as it will crash if we try to set the cursor pos outside the buffer when scrolling the content
                Console.BufferHeight = Math.Max(Console.BufferHeight, Offset.Y + 2)

                Console.SetCursorPosition(0, Offset.Y + 1)
                Exit Do
            End If


            Select Case EditAction
                Case ReadLineOptions.EditAction.Add, ReadLineOptions.EditAction.Delete, ReadLineOptions.EditAction.Initial
                    If UndoListEndOffset > 1 Then
                        'we have undone and now made an edit ... so we need to ditch changes that are in the stack
                        Dim KillRange = UndoListEndOffset - 1
                        UndoList.RemoveRange(UndoList.Count - KillRange, KillRange)
                        UndoListEndOffset = 1
                    End If
                    UndoList.Add(New With {.Text = LineText.ToString, .Index = PositionInString})
            End Select
            EditAction = ReadLineOptions.EditAction.None

            Key = Console.ReadKey(True)

            If Left <> Console.CursorLeft OrElse Top <> Console.CursorTop Then
                'we know that the cursor won't have moved ... but this can happen when resizing the window...
                'we will need to reset StartLeft, and StartTop
                'This will be PreString.Length BEFORE its current location
                Dim StartLinePos = GetCursorOffset(-PreString.Length)
                StartLeft = StartLinePos.X
                StartTop = StartLinePos.Y
                Left = Console.CursorLeft
                Top = Console.CursorTop
            End If

            Select Case Key.Key
                Case ConsoleKey.Backspace, ConsoleKey.Delete
                    Dim isBackspace = Key.Key = ConsoleKey.Backspace
                    Dim DeleteCount = 0 '< neg for backspace!
                    Dim JumpString = If(isBackspace, PreString, PostString)
                    If Key.Modifiers = ConsoleModifiers.Control Then
                        'whole word
                        Dim JumpPattern = If(isBackspace, Options.GetStandardJumpBackPattern, Options.GetStandardJumpForwardPattern)
                        If JumpPattern = "" Then
                            'use normal 1 char
                        Else
                            Dim m = Text.RegularExpressions.Regex.Match(JumpString, JumpPattern)
                            DeleteCount = If(m.Success, m.Length, PreString.Length)
                        End If
                    End If
                    If DeleteCount = 0 AndAlso JumpString <> "" Then '< JumpString will be something if there is something to delete :)
                        DeleteCount = 1
                    End If
                    If DeleteCount <> 0 Then
                        EditAction = ReadLineOptions.EditAction.Delete
                        Dim NewPostString = PostString
                        If isBackspace Then
                            DeleteCount *= -1

                            Dim CursorLocation = GetCursorOffset(DeleteCount)
                            Console.SetCursorPosition(CursorLocation.X, CursorLocation.Y)
                        Else
                            NewPostString = PostString.Substring(DeleteCount)
                        End If
                        Using New HoldCursor()
                            Console.Write($"{NewPostString}")
                            ClearCharacters(DeleteCount)
                        End Using
                        LineText.Remove(PositionInString + If(isBackspace, DeleteCount, 0), Math.Abs(DeleteCount))
                    End If
                Case ConsoleKey.LeftArrow, ConsoleKey.RightArrow
                    Dim MoveLeft = Key.Key = ConsoleKey.LeftArrow

                    Dim MoveCount = 0
                    Dim JumpCheckString = If(MoveLeft, PreString, PostString)
                    If Key.Modifiers = ConsoleModifiers.Control Then
                        'move a whole word
                        Dim JumpPattern = If(MoveLeft, Options.GetStandardJumpBackPattern, Options.GetStandardJumpForwardPattern)
                        If JumpPattern = "" Then
                            'use normal move
                        Else
                            Dim m = Text.RegularExpressions.Regex.Match(JumpCheckString, JumpPattern)
                            MoveCount = If(m.Success, m.Length, JumpCheckString.Length) * If(MoveLeft, -1, 1)
                        End If
                    End If
                    If MoveCount = 0 Then
                        'standard move
                        MoveCount = If(MoveLeft, -1, 1)
                    End If

                    If MoveCount <> 0 Then
                        If Math.Abs(MoveCount) > JumpCheckString.Length Then
                            'attempted to move out of range
                        Else
                            EditAction = ReadLineOptions.EditAction.Move

                            Dim CursorLocation = GetCursorOffset(MoveCount)
                            Console.SetCursorPosition(CursorLocation.X, CursorLocation.Y)
                        End If
                    End If
                Case ConsoleKey.UpArrow
                    If Top > StartTop Then
                        If Top = StartTop + 1 AndAlso Left < StartLeft Then
                            'prevent up if we are on line 2 and have an indent on the line above
                        Else
                            EditAction = ReadLineOptions.EditAction.Move
                            Console.CursorTop -= 1
                        End If
                    End If
                Case ConsoleKey.DownArrow
                    Dim Offset = GetCursorOffset(PostString.Length)
                    If Offset.Y > Top AndAlso Left <= Offset.X Then
                        EditAction = ReadLineOptions.EditAction.Move
                        Console.CursorTop += 1
                    Else
                        'move past end of string
                    End If
                Case ConsoleKey.Tab
                        'TODO: intellisense
                Case ConsoleKey.Enter
                    EditAction = ReadLineOptions.EditAction.Finish
                Case ConsoleKey.Home, ConsoleKey.End
                    Dim GotoLeft = StartLeft
                    Dim GotoTop = StartTop
                    If Key.Key = ConsoleKey.End Then
                        Dim Offset = GetCursorOffset(LineText.Length, StartLeft, StartTop)
                        GotoLeft = Offset.X
                        GotoTop = Offset.Y
                    End If
                    If Left <> GotoLeft OrElse Top <> GotoTop Then
                        EditAction = ReadLineOptions.EditAction.Move
                        Console.SetCursorPosition(GotoLeft, GotoTop)
                    End If
                Case Else
                    If (Key.Key = ConsoleKey.Z OrElse Key.Key = ConsoleKey.Y) AndAlso Key.Modifiers = ConsoleModifiers.Control Then
                        'undo / redo
                        Dim IsUndo = Key.Key = ConsoleKey.Z
                        If If(IsUndo, UndoListEndOffset < UndoList.Count, UndoListEndOffset > 1) Then
                            EditAction = If(IsUndo, ReadLineOptions.EditAction.UnDo, ReadLineOptions.EditAction.ReDo)
                            UndoListEndOffset += If(IsUndo, 1, -1)
                            Console.SetCursorPosition(StartLeft, StartTop)
                            ClearCharacters(LineText.Length)

                            Dim UndoItem = UndoList(UndoList.Count - UndoListEndOffset)
                            Console.Write(UndoItem.Text)
                            Dim Offset = GetCursorOffset(UndoItem.Index, StartLeft, StartTop)
                            Console.SetCursorPosition(Offset.X, Offset.Y)
                            LineText.Clear()
                            LineText.Append(UndoItem.Text)
                        End If
                    ElseIf Key.KeyChar <> vbNullChar Then
                        'standard input
                        EditAction = ReadLineOptions.EditAction.Add
                        LineText.Insert(PositionInString, Key.KeyChar)
                        Console.Write(Key.KeyChar)
                        If PostString <> "" Then
                            Using New HoldCursor()
                                Console.Write($"{PostString}")
                            End Using
                        End If

                        If Left = Console.CursorLeft Then
                            'push to New line - this is to get around that the console won't go to the next line until the line is filled!
                            Console.SetCursorPosition(0, Top + 1)
                        End If
                    End If
            End Select

        Loop

        Return LineText.ToString()
    End Function

End Class
