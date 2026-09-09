Imports System.Text

Public Class CommandLineParser
    Inherits List(Of Command)

    <DebuggerDisplay("{" & NameOf(Command.ToString) & "}")>
    Public Class Command
        Public ReadOnly Property Command As String
        Public ReadOnly Property Value As String

        Public Sub New(Command As String, Value As String)
            Me.Command = Command
            Me.Value = Value
        End Sub

        Public Overrides Function ToString() As String
            Return $"{Command}: {Value}"
        End Function
    End Class

    Public Iterator Function GetValues(Optional Command As String = "") As IEnumerable(Of String)
        For Each Value In Me.Where(Function(x) String.Equals(x.Command, Command, StringComparison.OrdinalIgnoreCase)).
                             Select(Function(x) x.Value)
            Yield Value
        Next
    End Function

    Public Function GetValue(Optional Command As String = "") As String
        Return GetValues(Command).FirstOrDefault()
    End Function

    Public Sub New(Optional CommandLine As String = Nothing)
        If CommandLine = "" Then
            CommandLine = Interaction.Command()
        End If

        If CommandLine = "" Then Return

        Dim GetChar =
            Function(Check As Integer)
                Dim AscW = Check
                If AscW = -1 Then
                    Return New Char?
                Else
                    Return ChrW(AscW)
                End If
            End Function

        Dim sbCommand As New StringBuilder
        Dim sbValue As New StringBuilder

        Dim LockOff =
            Sub()
                Me.Add(New Command(sbCommand.ToString(), sbValue.ToString()))
                sbCommand.Clear()
                sbValue.Clear()
            End Sub

        Dim InCommand = False
        Dim InQuotes = False

        Dim FirstChar = True
        Using sr As New IO.StringReader(CommandLine)
            Do
                Try
                    Dim Chr = GetChar(sr.Read())

                    If Chr.HasValue = False Then
                        'we hit the end of the command
                        LockOff()
                        Return
                    End If

                    Dim Append = False
                    Select Case Chr
                        Case """"c
                            Dim NextChar = GetChar(sr.Peek())
                            If NextChar = """"c Then
                                'the next value is also a "
                                'so we want to treat this a literal and consume the next char too so we don't read the next quote again
                                sr.Read()
                                Append = True
                            Else
                                InQuotes = Not InQuotes
                            End If
                        Case " "c
                            If InQuotes Then
                                'since we are in quotes we can just add this to the existing value
                                Append = True
                            Else
                                'we need to check that the next thing is not:
                                '   - start of a new command
                                '   - the end of a command and start of a value
                                If GetChar(sr.Peek()) = "/"c Then
                                    'start of a command
                                    LockOff()
                                    InCommand = True
                                    'consume the /
                                    sr.Read()
                                ElseIf InCommand Then
                                    'end of the command bit and start of a value
                                    InCommand = False
                                Else
                                    'we can just consume this
                                    Append = True
                                End If
                            End If
                        Case Else
                            'special case here where the first char is /
                            '... then we need to start a command without a space..
                            If Chr = "/"c AndAlso FirstChar Then
                                InCommand = True
                            Else
                                Append = True
                            End If
                    End Select

                    If Append Then
                        If InCommand Then
                            sbCommand.Append(Chr)
                        Else
                            sbValue.Append(Chr)
                        End If
                    End If
                Finally
                    FirstChar = False
                End Try
            Loop
        End Using
    End Sub


End Class
