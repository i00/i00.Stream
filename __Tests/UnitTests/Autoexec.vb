Imports System.IO
Imports StreamEncryption.Streams

Public Class Autoexec

    Public Shared Function Main() As Integer
        Test()
        'Tests.StreamChunked.Checkpoints.CheckpointLifoEnforced()
        UnitTester.SimpleTest.TestTypesToRun = UnitTester.SimpleTest.TestTypes.Test

        Return UnitTester.Autoexec.Main()
    End Function

    Public Shared Sub Test()

        Using Ms As New MemoryStream()
            Dim Options = New ChunkedStream.ChunkedStreamOptions With {
                .EncryptionInfo = New ChunkedStream.EncryptionInfo("Hello"),
                .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                .CompressionRatioThreshold = 50
            }
            Using Cs = ChunkedStream.Open(Ms, Options)
                For i = 0 To 10
                    Cs.Write(i, {CByte(i + 1)})
                    Debug.Print($"{i}: {Ms.Length}")
                Next
                Dim Struct = Cs.GetStructure()
                Dim zz = ""
            End Using
            Debug.Print($"fin: {Ms.Length}")
        End Using
    End Sub

End Class
