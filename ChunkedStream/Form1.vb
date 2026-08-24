Imports System.Text
Imports System.IO
Imports System.Security.Cryptography
Imports i00CodeLib
Imports i00.Streams


Public Class Form1
    'Dim Key As Byte() = Streams.ChunkedStream.EncryptionInfo.DeriveMasterKey(
    '        "MySecretPassword",
    '        Encoding.UTF8.GetBytes("Test"),
    '        10000)

    Dim Options As New Streams.ChunkedStream.ChunkedStreamOptions() With {.CompressionMethod = Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4, .EncryptionInfo = New Streams.ChunkedStream.EncryptionInfo("MySecretPassword")}


    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        'Dim Key = Encryption.General.DeriveMasterKey(masterkey, Encoding.UTF8.GetBytes("DokanMirror.FixedSalt.v1"), 300000)

        'Using ms As New IO.MemoryStream(My.Computer.FileSystem.ReadAllBytes("C:\Users\Kris\Pictures\00632-2246855333-castle, tone mapped, shiny, intricate, cinematic lighting, highly detailed, digital painting, artstation, concept art, smooth, s.png"))
        '    Using fs As New IO.FileStream("C:\Users\Kris\Pictures\enc.png", IO.FileMode.OpenOrCreate)
        '        Using es = Encryption.EncryptedStream.Open(fs, Key, True)

        '        End Using
        '    End Using
        'End Using

        'Dim Key = Encryption.General.DeriveMasterKey(
        '    "MySecretPassword",
        '    Encoding.UTF8.GetBytes("Testinghere"),
        '    300000)

        'Dim PlainText = "Hello World!"
        'Dim Data = Encoding.UTF8.GetBytes(PlainText)

        'Using Fs As New FileStream("c:\temp\test.enc",
        '                           FileMode.Create,
        '                           FileAccess.ReadWrite)

        '    Using EncFs = Encryption.EncryptedStream.Open(Fs, Key, True)

        '        EncFs.Write(0, Data)

        '        Dim Output(Data.Length - 1) As Byte

        '        EncFs.Read(0, Output)

        '        MessageBox.Show(Encoding.UTF8.GetString(Output))

        '    End Using

        'End Using

        '    Dim Original = File.ReadAllBytes("C:\Games\mrt.exe")

        '    Dim Compressed = Encryption.Lz4Block.Compress(Original)

        '    Dim Decompressed = Encryption.Lz4Block.Decompress(
        'Compressed,
        'Original.Length)

        '    Dim Md51 = i00CodeLib.Hash.ComputeHash(Original)
        '    Dim Md52 = i00CodeLib.Hash.ComputeHash(Decompressed)

        'Debug.Assert(Md51 = Md52)


        'Using ms As New MemoryStream()
        '    Using cs = ChunkedStream.Open(ms)
        '        Using efs = New EmbeddedFileSystem(cs)
        '            Dim FileID = efs.CreateFile(efs.RootAnchorId, "Test.png")
        '            Using fs = efs.OpenFile(FileID)
        '                Using b As New Bitmap("C:\Users\Kris\Desktop\_\1-4000\1f4ab 2650.png")
        '                    b.Save(fs, Imaging.ImageFormat.Png)
        '                End Using
        '            End Using
        '        End Using
        '    End Using
        'End Using

        'Test("C:\Games\Vampire The Masquerade - Bloodlines.zip")
        'Test("C:\Windows\System32\mrt.exe")
        'Test("C:\Games\mrt.zip", True)
        Test("C:\Games\mrt.exe", True)
        'Test("C:\Games\Vampire The Masquerade - Bloodlines.zip", False)
        'Test("C:\Windows\explorer.exe", True)


        Encrypt()
        Decrypt()
        Me.BackgroundImage = New System.Drawing.Bitmap("Out.png")
        Me.Text = HelloWorld()

    End Sub

    Public Sub Test(FileName As String, Optional RandomWrite As Boolean = False)
        Static LastTime As Date = Now()

        Dim Options As New Streams.ChunkedStream.ChunkedStreamOptions() With
        {
            .CompressionRatioThreshold = 1,
            .CompressionMethod = Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
            .NewIndexPageWriteLocationPolicy = Streams.ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles,
            .NewChunkWriteLocationPolicy = Streams.ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles,
            .EncryptionInfo = New Streams.ChunkedStream.EncryptionInfo("MySecretPassword")
        }
        '.EncryptionInfo = New Streams.ChunkedStream.EncryptionInfo("MySecretPassword"),


        Using frmProgress As New i00CodeLib.frmProgress(
        Sub(Parameter, ProgressReport)

            Dim OriginalMD5 As String

            ' -----------------------------------------------------------------
            ' MD5 original file
            ' -----------------------------------------------------------------

            Using Fs As New FileStream(FileName,
                                       FileMode.Open,
                                       FileAccess.Read,
                                       FileShare.Read)

                Using Md5 = System.Security.Cryptography.MD5.Create()
                    ProgressReport.SetText("Generating file MD5...")
                    OriginalMD5 = BitConverter.ToString(Md5.ComputeHash(Fs)).Replace("-", "")
                End Using

            End Using

            ProgressReport.SetText("Encrypting file...")

            Dim DecryptedMD5 As String

            Using EncStorage = New MemoryStream() 'New FileStream("asd.ziped", FileMode.Create) ' New MemoryStream()

                Dim InputMBps = 0.0
                Dim OutputMBps = 0.0

                Using InputFs As New FileStream(FileName,
                                               FileMode.Open,
                                               FileAccess.Read,
                                               FileShare.Read)

                    Dim StartTime = DateTime.UtcNow
                    Dim FileLength = InputFs.Length

                    Using Enc = Streams.ChunkedStream.Open(EncStorage, Options)

                        If RandomWrite Then

                            ProgressReport.SetText("Building random write segments...")

                            Dim Randomizer As New Random(12345)

                            Dim Segments As New List(Of Tuple(Of Long, Byte()))

                            While True

                                Dim Remaining = FileLength - InputFs.Position

                                If Remaining <= 0 Then Exit While

                                Dim BlockSize = Randomizer.Next(1024,
                                                                512 * 1024)

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

                            For Each Segment In Segments

                                Enc.Write(Segment.Item1,
                                          Segment.Item2)

                                ProcessedBytes += Segment.Item2.Length

                                Dim Progress = CDbl(ProcessedBytes) / FileLength

                                InputMBps =
                                    (ProcessedBytes / 1024.0 / 1024.0) /
                                    Math.Max(0.001,
                                             (DateTime.UtcNow - StartTime).TotalSeconds)

                                ProgressReport.SetText(
                                    $"Random writing ({InputMBps:0} MB/s)...")

                                ProgressReport.SetProgress(
                                    CLng(Progress * 100),
                                    100)

                            Next

                        Else

                            Dim Buffer(1024 * 1024 - 1) As Byte
                            Dim Offset As Long = 0

                            LastTime = Now()
                            While True

                                Dim BytesRead = InputFs.Read(Buffer,
                                                             0,
                                                             Buffer.Length)

                                If BytesRead = 0 Then Exit While

                                If BytesRead = Buffer.Length Then
                                    'Enc.DebugTimers.Clear()

                                    'Enc.swCryptPayload.Reset()
                                    ''Enc.swCompressPayload.Reset()
                                    ''Enc.swComputeHash.Reset()
                                    ''Enc.swWritePhysicalRecordWithPolicy.Reset()
                                    'Enc.swGetNextPhysicalRecordWriteOffset.Reset()
                                    'Enc.swPersistPagedMetadata.Reset()
                                    ''Enc.swMarkPhysicalRecordDirty.Reset()

                                    'Enc.swWriteExtentPage.Reset()
                                    'Enc.swWritePhysicalRecordPage.Reset()
                                    'Enc.swWriteDirectoryPages.Reset()
                                    'Enc.swWriteHoleDirectoryPages.Reset()
                                    'Enc.DebugWriteExtentPageCount = 0
                                    'Enc.DebugWritePhysicalRecordPageCount = 0

                                    Dim sw As New Stopwatch()
                                    sw.Start()
                                    Enc.Write(Offset,
                                              Buffer)
                                    sw.Stop()

                                    Dim ThisTime = Now()
                                    If Math.Abs(ThisTime.Subtract(LastTime).TotalSeconds) > 1 Then
                                        LastTime = ThisTime

                                        Debug.Print($"{Now}: Cs.Length: {Enc.Length}")
                                        Debug.Print($"    Total Write: {sw.ElapsedMilliseconds}")
                                        'Debug.Print($"    WriteExtentPage: {Enc.swWriteExtentPage.ElapsedMilliseconds}")
                                        ''Debug.Print($"    MarkPhysicalRecordDirty: {Enc.swMarkPhysicalRecordDirty.ElapsedMilliseconds}")
                                        'Debug.Print($"    WritePhysicalRecordPage: {Enc.swWritePhysicalRecordPage.ElapsedMilliseconds}")
                                        'Debug.Print($"    WriteDirectoryPages: {Enc.swWriteDirectoryPages.ElapsedMilliseconds}")
                                        'Debug.Print($"    WriteHoleDirectoryPages: {Enc.swWriteHoleDirectoryPages.ElapsedMilliseconds}")
                                        ''Debug.Print($"    WritePhysicalRecordWithPolicy: {Enc.swWritePhysicalRecordWithPolicy.ElapsedMilliseconds}")
                                        'Debug.Print($"    GetNextPhysicalRecordWriteOffset: {Enc.swGetNextPhysicalRecordWriteOffset.ElapsedMilliseconds}")
                                        'Debug.Print($"    PersistPagedMetadata: {Enc.swPersistPagedMetadata.ElapsedMilliseconds}")
                                        'Debug.Print($"    CryptPayload: {Enc.swCryptPayload.ElapsedMilliseconds}")
                                        ''Debug.Print($"    CompressPayload: {Enc.swCompressPayload.ElapsedMilliseconds}")
                                        ''Debug.Print($"    ComputeHash: {Enc.swComputeHash.ElapsedMilliseconds}")
                                        'Debug.Print($"    DirtyExtentPages: {Enc._DirtyExtentPages.Count}")
                                        'Debug.Print($"    DirtyPhysicalPages: {Enc._DirtyPhysicalRecordPages.Count}")
                                        'Debug.Print($"    DebugWriteExtentPageCount: {Enc.DebugWriteExtentPageCount}")
                                        'Debug.Print($"    DebugWritePhysicalRecordPageCount: {Enc.DebugWritePhysicalRecordPageCount}")
                                        'For Each entry In Enc.DebugTimers
                                        '    Debug.Print($"    {entry.Key}: {entry.Value.ElapsedMilliseconds}")
                                        'Next

                                    End If

                                Else

                                    Dim FinalBlock(BytesRead - 1) As Byte

                                    System.Buffer.BlockCopy(Buffer,
                                                     0,
                                                     FinalBlock,
                                                     0,
                                                     BytesRead)

                                    Enc.Write(Offset,
                                              FinalBlock)

                                End If

                                Offset += BytesRead

                                Dim Progress = CDbl(Offset) / FileLength

                                InputMBps =
                                    (Offset / 1024.0 / 1024.0) /
                                    Math.Max(0.001,
                                             (DateTime.UtcNow - StartTime).TotalSeconds)

                                ProgressReport.SetText(
                                    $"Encrypting file ({InputMBps:0} MB/s)...")

                                ProgressReport.SetProgress(
                                    CLng(Progress * 100),
                                    100)

                            End While

                        End If

                    End Using

                End Using

                EncStorage.Position = 0

                ' -----------------------------------------------------------------
                ' Read encrypted stream back and MD5 decrypted stream
                ' -----------------------------------------------------------------

                ProgressReport.SetText("Decrypting stream...")

                Dim DecryptStartTime = DateTime.UtcNow

                Using Enc = Streams.ChunkedStream.Open(EncStorage, Options)

                    Using Md5 = System.Security.Cryptography.MD5.Create()

                        Dim Offset As Long = 0
                        Dim Buffer(1024 * 1024 - 1) As Byte

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

                            Md5.TransformBlock(ReadBuffer,
                                               0,
                                               BytesRead,
                                               Nothing,
                                               0)

                            Offset += BytesRead

                            Dim Progress = CDbl(Offset) / Enc.Length

                            OutputMBps =
                                (Offset / 1024.0 / 1024.0) /
                                Math.Max(0.001,
                                         (DateTime.UtcNow - DecryptStartTime).TotalSeconds)

                            ProgressReport.SetText(
                                $"Decrypting stream ({OutputMBps:0} MB/s)...")

                            ProgressReport.SetProgress(
                                CLng(Progress * 100),
                                100)

                        End While

                        Md5.TransformFinalBlock(New Byte() {},
                                                0,
                                                0)

                        DecryptedMD5 =
                            BitConverter.ToString(Md5.Hash).
                            Replace("-", "")

                    End Using


                    'Dim Ss = Enc.GetStructure()

                    'Debug.Print($"Chunks={Ss.ChunkCount:N0}")
                    'Debug.Print($"Allocated={Ss.AllocatedChunkCount:N0}")
                    'Debug.Print($"Sparse={Ss.SparseChunkCount:N0}")

                    'Debug.Print($"PhysicalPayload={Ss.PhysicalPayloadBytes:N0}")
                    'Debug.Print($"PhysicalRecords={Ss.PhysicalChunkRecordBytes:N0}")
                    'Debug.Print($"Overhead={Ss.PhysicalChunkOverheadBytes:N0}")

                    'Debug.Print($"Metadata={Ss.PhysicalMetadataBytes:N0}")

                    'Debug.Print($"Fragmented={Ss.FragmentedBytes:N0}")
                    'Debug.Print($"Fragmentation={Ss.FragmentationRatio:P2}")

                    ' -----------------------------------------------------------------
                    ' Defrag
                    ' -----------------------------------------------------------------

                    ProgressReport.SetText("Defragmenting...")

                    Dim EncryptedFragmentation = Enc.GetFragmentation

                    Dim FragmentationDrawOptions = New FragmentationDrawOptions 'With
                    '    {
                    '        .BackgroundColor = Color.Red,
                    '        .FragmentedChunkColor = Color.Transparent,
                    '        .UnusedColor = Color.Transparent,
                    '        .HoleColor = Color.Transparent
                    '    }
                    Panel1.BackgroundImageLayout = ImageLayout.Stretch
                    Panel1.BackgroundImage = Enc.GetStructure().GenerateFragmentationBitmap(Panel1.ClientSize.Width, 1)
                    'Panel1.BackgroundImage = Enc.GetStructure().GenerateFragmentationBitmap(Panel1.ClientSize, FragmentationDrawOptions)

                    Dim pnlDefrag As Panel = Nothing
                    ProgressReport.frmProgress.Invoke(
                        Sub()
                            pnlDefrag = New Panel
                            pnlDefrag.Bounds = New Rectangle(ProgressReport.frmProgress.nbProgress.Left,
                                                       ProgressReport.frmProgress.nbProgress.Bottom,
                                                       ProgressReport.frmProgress.nbProgress.Width,
                                                       ProgressReport.frmProgress.nbProgress.Height)
                            ProgressReport.frmProgress.Controls.Add(pnlDefrag)
                        End Sub)

                    Dim LastUpdate As Date

                    'Dim Before = Enc.GetStructure
                    'Enc.Options.EncryptionInfo = Nothing
                    'Enc.Options.CompressionMethod = Streams.ChunkedStream.ChunkedStreamOptions.CompressionMethods.None
                    'Enc.Defragment(Streams.ChunkedStream.DefragTypes.Move)
                    'Dim After = Enc.GetStructure
                    'Enc.Defragment(Streams.ChunkedStream.DefragTypes.Move)

                    Dim ReclaimedBytes = Enc.Defragment(Streams.ChunkedStream.DefragTypes.Move,
                                                        Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                                                            Dim Progress = If(TotalUnits = 0, 1.0R, ProcessedUnits / CDbl(TotalUnits))
                                                            ProgressReport.SetText($"Defragmenting ({Progress:P0})...")
                                                            ProgressReport.SetProgress(CLng(Progress * 100), 100)

                                                            Dim CurrentTime = Now()
                                                            Dim Done = ProcessedUnits = TotalUnits
                                                            If CurrentTime.Subtract(LastUpdate).TotalMilliseconds >= 250 OrElse Done Then
                                                                Dim S = Enc.GetStructure()
                                                                Debug.Print(
                                                                    $"cs.Frag={Enc.GetFragmentation():P4}," &
                                                                    $"S.Frag={S.FragmentationRatio:P4}, " &
                                                                    $"S.FragBytes={S.FragmentedBytes:N0}, " &
                                                                    $"S.LiveEnd={S.LiveDataEndOffset:N0}, " &
                                                                    $"S.Physical={S.PhysicalLength:N0}")
                                                                LastUpdate = CurrentTime
                                                                pnlDefrag.BackgroundImage = Enc.GetStructure().GenerateFragmentationBitmap(pnlDefrag.ClientSize.Width, 1)
                                                                'pnlDefrag.BackgroundImage = Enc.GenerateFragmentationBitmap(pnlDefrag.ClientSize.Width, 1)
                                                            End If
                                                            'pnlDefrag.BackgroundImage = Enc.GenerateFragmentationBitmap(pnlDefrag.ClientSize.Width, 1)
                                                            'System.Threading.Thread.Sleep(10)
                                                            'ProgressReport.frmProgress.
                                                        End Sub)
                    Dim S22 = Enc.GetStructure()
                    Debug.Print(
                        $"cs.Frag={Enc.GetFragmentation():P4}," &
                        $"S.Frag={S22.FragmentationRatio:P4}, " &
                        $"S.FragBytes={S22.FragmentedBytes:N0}, " &
                        $"S.LiveEnd={S22.LiveDataEndOffset:N0}, " &
                        $"S.Physical={S22.PhysicalLength:N0}")

                    Panel2.BackgroundImageLayout = ImageLayout.Stretch
                    'Panel2.BackgroundImage = Enc.GetStructure().GenerateFragmentationBitmap(Panel2.ClientSize, FragmentationDrawOptions)
                    Panel2.BackgroundImage = Enc.GetStructure().GenerateFragmentationBitmap(Panel2.ClientSize.Width, 1)

                    Dim DefragMD5 As String

                    Using Md5 = System.Security.Cryptography.MD5.Create()

                        ProgressReport.SetText("Generating post defrag decrypted MD5...")

                        Dim Offset As Long = 0
                        Dim Buffer(1024 * 1024 - 1) As Byte

                        While Offset < Enc.Length

                            Dim Remaining = Enc.Length - Offset
                            Dim ReadSize = CInt(Math.Min(Buffer.Length, Remaining))

                            Dim ReadBuffer(ReadSize - 1) As Byte

                            Dim BytesRead = Enc.Read(Offset, ReadBuffer)

                            If BytesRead = 0 Then Exit While

                            Md5.TransformBlock(ReadBuffer,
                           0,
                           BytesRead,
                           Nothing,
                           0)

                            Offset += BytesRead

                            Dim Progress = If(Enc.Length = 0, 1.0R, CDbl(Offset) / Enc.Length)

                            ProgressReport.SetText($"Generating post defrag decrypted MD5 ({Progress:P0})...")
                            ProgressReport.SetProgress(CLng(Progress * 100), 100)

                        End While

                        Md5.TransformFinalBlock(New Byte() {}, 0, 0)

                        DefragMD5 = BitConverter.ToString(Md5.Hash).Replace("-", "")

                    End Using

                    Dim DefraggedFragmentation = Enc.GetFragmentation


                    Dim Dictionary As New Dictionary(Of String, String) From {
                          {"Mode", If(RandomWrite,
                                      "Random Fragmented Write",
                                      "Sequential Write")},
                          {"Original MD5", OriginalMD5},
                          {"Decrypted MD5", DecryptedMD5},
                          {"Defragged MD5", DefragMD5},
                          {"Match", If(OriginalMD5 = DecryptedMD5 AndAlso DecryptedMD5 = DefragMD5,
                                       "Yes",
                                       "NO")},
                          {"File Size", FileIO.FileSystem.GetFileInfo(FileName).Length.FormatFileSizeFromBytes},
                          {"Encrypted Size", EncStorage.Length.FormatFileSizeFromBytes},
                          {"Encryption Speed", $"{InputMBps:0.00} MB/s"},
                          {"Decryption Speed", $"{OutputMBps:0.00} MB/s"},
                          {"Encrypted Fragmentation", $"{EncryptedFragmentation:P0}"},
                          {"Defragged Fragmentation", $"{DefraggedFragmentation:P0}"},
                          {"Defragged Saved", $"{ReclaimedBytes.FormatFileSizeFromBytes}"},
                          {"Compression Method", $"{Options.CompressionMethod}"}
                      }

                    i00CodeLib.MsgBox(
                                ProgressReport.frmProgress,
                                Join(Dictionary.
                                     Select(Function(x) $"{x.Key}: {x.Value}").
                                     ToArray(),
                                     vbCrLf))

                End Using


            End Using

        End Sub,
        Nothing)

            frmProgress.ShowInTaskbar = True
            frmProgress.Text = If(RandomWrite,
                              "Testing (Random Write)",
                              "Testing (Sequential Write)")

            frmProgress.ShowDialog(Nothing)

        End Using

    End Sub

    Public Function HelloWorld() As String

        Dim PlainText = "Hello World!"
        Dim Data = Encoding.UTF8.GetBytes(PlainText)

        Using Ms As New MemoryStream()

            ' Encrypt
            Using EncFs = Streams.ChunkedStream.Open(Ms, Options)

                EncFs.Write(0, Data)

                Dim Output(Data.Length - 1) As Byte

                EncFs.Read(0, Output)

                'Console.WriteLine(Encoding.UTF8.GetString(Output))
                Return Encoding.UTF8.GetString(Output)
            End Using

        End Using
    End Function

    Public Sub Encrypt()
        Dim SourceFile = "Test.png"
        Dim EncryptedFile = "Test.enc"

        Using InputFs As New FileStream(SourceFile,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read)

            Using OutputFs As New FileStream(EncryptedFile,
                                             FileMode.Create,
                                             FileAccess.ReadWrite,
                                             FileShare.None)

                Using EncFs = Streams.ChunkedStream.Open(OutputFs, Options)

                    Dim Buffer(65535) As Byte

                    Dim Offset As Long = 0
                    Dim BytesRead As Integer

                    Do

                        BytesRead = InputFs.Read(Buffer, 0, Buffer.Length)

                        If BytesRead > 0 Then

                            If BytesRead = Buffer.Length Then

                                EncFs.Write(Offset, Buffer)

                            Else

                                Dim LastBlock(BytesRead - 1) As Byte
                                System.Buffer.BlockCopy(Buffer, 0, LastBlock, 0, BytesRead)

                                EncFs.Write(Offset, LastBlock)

                            End If

                            Offset += BytesRead

                        End If

                    Loop While BytesRead > 0

                End Using

            End Using

        End Using
    End Sub

    Public Sub Decrypt()
        Dim EncryptedFile = "Test.enc"
        Dim DecryptedFile = "Out.png"

        Using InputFs As New FileStream(EncryptedFile,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read)

            Using EncFs = Streams.ChunkedStream.Open(InputFs, Options)

                Using OutputFs As New FileStream(DecryptedFile,
                                                 FileMode.Create,
                                                 FileAccess.Write,
                                                 FileShare.None)

                    Dim Offset As Long = 0
                    Dim Remaining = EncFs.Length

                    While Remaining > 0

                        Dim BlockSize = CInt(Math.Min(65536, Remaining))

                        Dim Buffer(BlockSize - 1) As Byte

                        Dim ReadBytes = EncFs.Read(Offset, Buffer)

                        If ReadBytes <= 0 Then Exit While

                        OutputFs.Write(Buffer, 0, ReadBytes)

                        Offset += ReadBytes
                        Remaining -= ReadBytes

                    End While

                End Using

            End Using

        End Using
    End Sub

End Class
