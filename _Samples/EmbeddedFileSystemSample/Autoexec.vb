Public NotInheritable Class Autoexec

    Public Shared Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        Using fs = New IO.FileStream("Test.efs", IO.FileMode.OpenOrCreate)
            Dim Options = New i00.Streams.ChunkedStream.ChunkedStreamOptions() With {
                .CompressionMethod = i00.Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4
            }
            Using cs = i00.Streams.ChunkedStream.Open(fs, Options)
                'cs.Defragment(i00.Streams.ChunkedStream.DefragTypes.Rebuild,
                '              Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                '                  Dim ZZZ = ""
                '                  'Debug.Print($"{ProcessedUnits / TotalUnits:P0}")
                '              End Sub)
                Using efs = New i00.Streams.EmbeddedFileSystem(cs)
                    Using frmEfs = New EmbeddedFileSystemBrowserForm(efs, False)
                        frmEfs.ShowDialog()
                    End Using
                End Using
            End Using
        End Using
    End Sub

End Class
