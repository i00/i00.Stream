Imports System.IO
Imports StreamEncryption.Streams

Public Class Autoexec

    Public Shared Function Main() As Integer

        'because we are not testing PBKDF2 key generation speed :P:
        StreamEncryption.Streams.ChunkedStream.EncryptionInfo.DefaultPBKDF2Iterations = 1
        UnitTester.SimpleTest.TestTypesToRun = UnitTester.SimpleTest.TestTypes.Test

        'Test()

        Return UnitTester.Autoexec.Main()
    End Function

    Public Shared Sub Test()

        Using Ms As New MemoryStream()
            Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                .ChunkSize = 1024
            }
            Using Cs = ChunkedStream.Open(Ms, Options)
                Dim Data =
                   Tests.Helpers.GenerateRandomData(
                         Options.ChunkSize * 100,
                         8).ToArray()
                Cs.Write(0, Data)
                Cs.Clone(Options.ChunkSize \ 2, Options.ChunkSize * 10, Options.ChunkSize * 20)



                'Cs.SetLength(Options.ChunkSize)
                'Cs.Write(0, {0})

                Dim Write = Sub(Text As String)
                                Cs.Write(Cs.Length, System.Text.Encoding.UTF8.GetBytes(Text))
                            End Sub
                Dim Print = Sub(Text As String)
                                Debug.Print($"{Text}: {System.Text.Encoding.UTF8.GetString(Cs.ToArray())}")
                            End Sub

                Write("Hello World!")
                Using cp1 = Cs.CreateCheckpoint()
                    Write("1")
                    cp1.Commit()
                    Write("2")
                    cp1.Rollback()
                    Print("check1")
                    Using cp2 = Cs.CreateCheckpoint()
                        Write("3")
                        cp2.Rollback()
                        Print("check2")

                    End Using
                    Print("check2")

                End Using

                'For i = 0 To 10
                '    Cs.Write(i, {CByte(i + 1)})
                '    Debug.Print($"{i}: {Ms.Length}")
                'Next
                Dim Struct = Cs.GetStructure()
                Dim zz = ""
            End Using
            Debug.Print($"fin: {Ms.Length}")
        End Using
    End Sub

End Class
