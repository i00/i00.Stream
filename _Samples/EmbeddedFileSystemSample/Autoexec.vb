Imports i00.Streams

Public NotInheritable Class Autoexec

    Private Sub New()

    End Sub

    Public Shared Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        Using fs = New IO.FileStream("Test.efs", IO.FileMode.OpenOrCreate, IO.FileAccess.ReadWrite, IO.FileShare.Read, bufferSize:=1)
            Dim EncryptionInfo As ChunkedStream.EncryptionInfo = Nothing
            If ChunkedStream.IsEncrypted(fs) Then
                Dim Password = EmbeddedFileSystemBrowserForm.PromptForText(Nothing, "Password", "Please enter the password:", True)
                If Password = "" Then Return
                EncryptionInfo = New ChunkedStream.EncryptionInfo(Password, System.Text.Encoding.UTF8.GetBytes(Password))
            End If
            Dim Options = New i00.Streams.ChunkedStream.ChunkedStreamOptions() With {
                .CompressionMethod = i00.Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                .AutoRecoverOnFault = True,
                .EncryptionInfo = EncryptionInfo
            }
            Using cs = i00.Streams.ChunkedStream.Open(fs, Options)
                'cs.Defragment(i00.Streams.ChunkedStream.DefragTypes.Rebuild,
                '              Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                '                  Dim ZZZ = ""
                '                  'Debug.Print($"{ProcessedUnits / TotalUnits:P0}")
                '              End Sub)
                Using efs = New i00.Streams.EmbeddedFileSystem(cs)
                    Using frmEfs = New EmbeddedFileSystemBrowserForm(efs)
                        frmEfs.ShowDialog()
                    End Using
                End Using
            End Using
        End Using
    End Sub

End Class
