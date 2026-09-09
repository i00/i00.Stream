Imports i00.Streams

Public NotInheritable Class Autoexec

    Private Sub New()

    End Sub

    Public Shared Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        Dim cmd = New CommandLineParser()

        Dim File = cmd.GetValue()
        If File = "" Then
            Using ofd As New SaveFileDialog()
                ofd.Title = "Open"
                ofd.OverwritePrompt = False
                ofd.Filter = "Embedded File System|*.efs"
                If ofd.ShowDialog() = DialogResult.OK Then
                    File = ofd.FileName
                Else
                    Return
                End If
            End Using
        End If

        Using fs = New IO.FileStream(File, IO.FileMode.OpenOrCreate, IO.FileAccess.ReadWrite, IO.FileShare.Read, bufferSize:=1)
            Dim EncryptionInfo As ChunkedStream.EncryptionInfo = Nothing
            If ChunkedStream.IsEncrypted(fs) Then
                Dim Password As String = Nothing
                Password = cmd.GetValue("password")
                If Password = "" Then
                    Password = EmbeddedFileSystemBrowserForm.PromptForText(Nothing, "Password", "Please enter the password:", True)
                End If
                If Password = "" Then Return
                EncryptionInfo = New ChunkedStream.EncryptionInfo(Password, System.Text.Encoding.UTF8.GetBytes(Password))
            End If
            ' ChunkSize only takes effect when creating a new Test.efs - an existing file keeps
            ' whatever chunk size it was created with (Open reads it back into Options). 64KB (the
            ' library default) means tens to hundreds of millions of physical-record entries for a
            ' multi-TB file; 4MB keeps that in the low millions while still capping the cost of a
            ' random seek at a few milliseconds of wasted decrypt/MAC work.
            Dim Options = New i00.Streams.ChunkedStream.ChunkedStreamOptions() With {
                .CompressionMethod = i00.Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                .AutoRecoverOnFault = True,
                .EncryptionInfo = EncryptionInfo,
                .ChunkSize = 4 * 1024 * 1024,
                .MaxCryptoParallelism = 8,
                .MaxPhysicalReadParallelism = 8,
                .MaxPhysicalWriteParallelism = 8,
                .MaxSubBlockCryptoParallelism = 8
            }
            Try
                Using cs = i00.Streams.ChunkedStream.Open(fs, Options)
                    Using efs = New i00.Streams.EmbeddedFileSystem(cs)
                        Using frmEfs = New EmbeddedFileSystemBrowserForm(efs)
                            frmEfs.ShowDialog()
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                MsgBox(Nothing, "Error opening embedded file system: " & ex.Message, MsgBoxStyle.Critical Or MsgBoxStyle.OkOnly,,,, Function(x) x.ShowInTaskbar = True)
            End Try
        End Using
    End Sub

End Class
