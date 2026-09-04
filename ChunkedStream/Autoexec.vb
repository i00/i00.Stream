Imports i00CodeLib
Imports System.IO
Imports i00.Streams
Imports System.Windows.Forms

Friend NotInheritable Class Autoexec

    Private Sub New()

    End Sub

    Public Shared Sub Main()
        Dim BigFile = FileIO.FileSystem.GetDirectoryInfo("C:\Windows\System32").
                             GetFiles.OrderByDescending(Function(x) x.Length).
                             First()
        Test(BigFile.FullName)
    End Sub

    Public Shared Sub Test(FileName As String, Optional RandomWrite As Boolean = False)

        Dim Options As New Streams.ChunkedStream.ChunkedStreamOptions() With
            {
                .CompressionRatioThreshold = 1,
                .CompressionMethod = Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                .NewIndexPageWriteLocationPolicy = Streams.ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                .NewChunkWriteLocationPolicy = Streams.ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFit,
                .EncryptionInfo = New Streams.ChunkedStream.EncryptionInfo("MySecretPassword")
            }

        Dim StartConsoleColor = Console.ForegroundColor

        Dim BufferSize = 1024 * 1024

        Dim Features As New List(Of String)
        If Options.CompressionMethod <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None Then
            Features.Add($"Compression ({Options.CompressionMethod})")
        End If
        If Options.EncryptionInfo IsNot Nothing Then
            Features.Add("Encrypting")
        End If
        If RandomWrite Then
            Features.Add("With random access")
        End If

        Features.Add($"Buffer Size: {BufferSize / 1024:F1} KB")

        Dim OriginalMD5 As String
        Console.WriteLine($"Testing {IO.Path.GetFileName(FileName)} ...")
        Console.WriteLine($"Features:")
        For Each Feature In Features
            Console.WriteLine($"  - {Feature}")
        Next
        Console.WriteLine()

        Using EncStorage = New MemoryStream()


            Dim OrigFileLength As Long
            Using InputFs As New FileStream(FileName,
                                            FileMode.Open,
                                            FileAccess.Read,
                                            FileShare.Read)

                OriginalMD5 = CalcMd5(InputFs)
                Console.Write($"Original MD5: ")
                Console.ForegroundColor = ConsoleColor.DarkGreen
                Console.WriteLine(OriginalMD5)
                Console.ForegroundColor = StartConsoleColor

                Dim StartTime = DateTime.UtcNow
                OrigFileLength = InputFs.Length

                Using Enc = Streams.ChunkedStream.Open(EncStorage, Options)

                    Dim InputMBps = 0.0

                    If RandomWrite Then

                        Dim Randomizer As New Random(12345)

                        Dim Segments As New List(Of Tuple(Of Long, Byte()))

                        While True

                            Dim Remaining = OrigFileLength - InputFs.Position

                            If Remaining <= 0 Then Exit While

                            'Dim BlockSize = Randomizer.Next(1024, 512 * 1024)
                            Dim BlockSize = BufferSize

                            BlockSize = CInt(Math.Min(BlockSize,
                                                        Remaining))

                            Dim Buffer(BlockSize - 1) As Byte

                            Dim BytesRead = InputFs.Read(Buffer,
                                                            0,
                                                            Buffer.Length)

                            If BytesRead = 0 Then Exit While

                            If BytesRead <> Buffer.Length Then
                                ReDim Preserve Buffer(BytesRead - 1)
                            End If

                            Segments.Add(
                                Tuple.Create(
                                    InputFs.Position - BytesRead,
                                    Buffer))

                        End While

                        Segments = Segments.
                            OrderBy(Function(x) Randomizer.Next()).
                            ToList()

                        Dim ProcessedBytes As Long = 0

                        StartTime = DateTime.UtcNow
                        For Each Segment In Segments

                            Enc.Write(Segment.Item1,
                                        Segment.Item2)

                            ProcessedBytes += Segment.Item2.Length

                            Dim Progress = CDbl(ProcessedBytes) / OrigFileLength

                            InputMBps =
                                (ProcessedBytes / 1024.0 / 1024.0) /
                                Math.Max(0.001,
                                            (DateTime.UtcNow - StartTime).TotalSeconds)

                        Next

                    Else

                        Dim Buffer(BufferSize - 1) As Byte
                        Dim Offset As Long = 0

                        'we read the entire file into memory to avoid disk IO slowing down the encryption speed test. This is not necessary for normal use, but it makes the test more accurate.
                        Using ms As New MemoryStream
                            InputFs.CopyTo(ms)
                            ms.Seek(0, SeekOrigin.Begin)

                            StartTime = DateTime.UtcNow
                            While True

                                Dim BytesRead = ms.Read(Buffer,
                                                        0,
                                                        Buffer.Length)

                                If BytesRead = 0 Then Exit While

                                Dim FinalBlock(BytesRead - 1) As Byte

                                System.Buffer.BlockCopy(Buffer,
                                                        0,
                                                        FinalBlock,
                                                        0,
                                                        BytesRead)

                                Enc.Write(Offset,
                                                FinalBlock)

                                Offset += BytesRead

                                Dim Progress = CDbl(Offset) / OrigFileLength

                                InputMBps =
                                    (Offset / 1024.0 / 1024.0) /
                                    Math.Max(0.001,
                                                (DateTime.UtcNow - StartTime).TotalSeconds)

                            End While
                        End Using

                    End If

                    Console.WriteLine($"Write speed: {InputMBps:F1} MB/s")

                End Using

            End Using

            EncStorage.Position = 0

            Using Enc = Streams.ChunkedStream.Open(EncStorage, Options)

                Dim Offset As Long = 0
                Dim Buffer(BufferSize - 1) As Byte

                Dim DecryptStartTime = DateTime.UtcNow
                Dim OutputMBps = 0.0

                While Offset < Enc.Length

                    Dim Remaining = Enc.Length - Offset

                    Dim ReadSize =
                        CInt(Math.Min(Buffer.Length,
                                        Remaining))

                    Dim ReadBuffer(ReadSize - 1) As Byte

                    Dim BytesRead =
                        Enc.Read(Offset,
                                    ReadBuffer)

                    If BytesRead = 0 Then Exit While

                    Offset += BytesRead

                    Dim Progress = CDbl(Offset) / Enc.Length

                    OutputMBps =
                        (Offset / 1024.0 / 1024.0) /
                        Math.Max(0.001,
                                    (DateTime.UtcNow - DecryptStartTime).TotalSeconds)

                End While

                Console.WriteLine($"Read speed: {OutputMBps:F1} MB/s")

                Dim DecryptedMD5 = CalcMd5(Enc)
                Console.Write($"{NameOf(ChunkedStream)} MD5: ")
                Console.ForegroundColor = If(DecryptedMD5 = OriginalMD5, ConsoleColor.DarkGreen, ConsoleColor.DarkRed)
                Console.WriteLine(DecryptedMD5)
                Console.ForegroundColor = StartConsoleColor

                Console.WriteLine($"Initial fragmentation: {Enc.GetFragmentation:P1}")

                Dim ReclaimedBytes = Enc.Defragment(Streams.ChunkedStream.DefragTypes.Move)
                Console.WriteLine($"Post defragmentation:  {Enc.GetFragmentation:P1}")

                '▌

                Dim DefragMD5 = CalcMd5(Enc)
                Console.Write($"{NameOf(ChunkedStream)} MD5 (post defrag): ")
                Console.ForegroundColor = If(DefragMD5 = OriginalMD5, ConsoleColor.DarkGreen, ConsoleColor.DarkRed)
                Console.WriteLine(DefragMD5)
                Console.ForegroundColor = StartConsoleColor

                Console.WriteLine($"Original size: {OrigFileLength / 1024 / 1024:F1} MB")
                Console.WriteLine($"{NameOf(ChunkedStream)} size: {EncStorage.Length / 1024 / 1024:F1} MB ({EncStorage.Length / OrigFileLength:P1})")

            End Using

        End Using

    End Sub


    Private Shared Function CalcMd5(Stream As Stream, Optional ProgressCallback As Action(Of Single) = Nothing) As String
        Dim OriginalPosition = Stream.Position
        Stream.Seek(0, SeekOrigin.Begin)
        Using Md5 = System.Security.Cryptography.MD5.Create()
            Dim Buffer(1024 * 1024 - 1) As Byte
            Dim Offset As Long = 0
            While Offset < Stream.Length
                Dim Remaining = Stream.Length - Offset
                Dim ReadSize = CInt(Math.Min(Buffer.Length, Remaining))
                Dim ReadBuffer(ReadSize - 1) As Byte
                Dim BytesRead = Stream.Read(ReadBuffer, 0, ReadBuffer.Length)
                If BytesRead = 0 Then Exit While
                Md5.TransformBlock(ReadBuffer, 0, BytesRead, Nothing, 0)
                Offset += BytesRead
                If ProgressCallback IsNot Nothing Then
                    ProgressCallback(CSng(Offset) / Stream.Length)
                End If
            End While
            Md5.TransformFinalBlock(New Byte() {}, 0, 0)
            Stream.Position = OriginalPosition
            Return BitConverter.ToString(Md5.Hash).Replace("-", "")
        End Using
    End Function
End Class

