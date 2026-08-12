Imports System.IO
Imports StreamEncryption.Streams

Public Class Autoexec

    Public Shared Function Main() As Integer
        'Test()
        'Tests.StreamChunked.Checkpoints.CheckpointLifoEnforced()
        UnitTester.SimpleTest.TestTypesToRun = UnitTester.SimpleTest.TestTypes.Benchmark

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
                Cs.Write(0, {CByte(1)})
                'make a payload that will be able to be compressable to about 75%
                Dim Data = Tests.Helpers.GenerateCompressableData(0.25, ChunkedStream.DefaultChunkSize, 8)

                Cs.Write(0, Data)
                Dim c = Cs.GetStructure().Chunks
                Dim qwe = ""

            End Using
        End Using
    End Sub

End Class
