Imports i00.Streams
Imports System.ComponentModel
Imports System.IO

Partial Public NotInheritable Class EmbeddedFileSystemBrowserForm
    Inherits Form

    Private NotInheritable Class DirectoryNodeInfo
        Public Sub New(AnchorId As Long, Name As String)
            Me.AnchorId = AnchorId
            Me.Name = Name
        End Sub

        Public ReadOnly Property AnchorId As Long
        Public ReadOnly Property Name As String
    End Class

    Private NotInheritable Class DragExport
        Implements IDisposable

        Public Sub New(TemporaryDirectory As String, Paths As String())
            Me.TemporaryDirectory = TemporaryDirectory
            Me.Paths = Paths
        End Sub

        Public ReadOnly Property TemporaryDirectory As String
        Public ReadOnly Property Paths As String()

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                If Directory.Exists(TemporaryDirectory) Then Directory.Delete(TemporaryDirectory, True)
            Catch
                ' Explorer may still have a transient handle. Temporary files can be removed later.
            End Try
        End Sub
    End Class

    Private ReadOnly _FileSystem As EmbeddedFileSystem
    Private ReadOnly _OwnsFileSystem As Boolean
    Private _CurrentDirectoryAnchorId As Long
    Private _ListDragStart As Point
    Private _TreeDragStart As Point
    Private _ListDragArmed As Boolean
    Private _TreeDragArmed As Boolean
    Private _Disposed As Boolean


    Public Sub New(FileSystem As EmbeddedFileSystem, Optional OwnsFileSystem As Boolean = False)
        If FileSystem Is Nothing Then Throw New ArgumentNullException(NameOf(FileSystem))

        _FileSystem = FileSystem
        _OwnsFileSystem = OwnsFileSystem
        _CurrentDirectoryAnchorId = _FileSystem.RootAnchorId

        InitializeComponent()
        RefreshFileSystemView()
    End Sub

    Public ReadOnly Property FileSystem As EmbeddedFileSystem
        Get
            Return _FileSystem
        End Get
    End Property

    Public Sub RefreshFileSystemView()
        Dim SelectedAnchorId = GetSelectedDirectoryAnchorId()
        If SelectedAnchorId <= 0 Then SelectedAnchorId = _CurrentDirectoryAnchorId
        If SelectedAnchorId <= 0 Then SelectedAnchorId = _FileSystem.RootAnchorId

        tvFolders.BeginUpdate()
        Try
            tvFolders.Nodes.Clear()
            Dim RootNode = New TreeNode("Root") With {
                .Name = _FileSystem.RootAnchorId.ToString(),
                .Tag = New DirectoryNodeInfo(_FileSystem.RootAnchorId, "Root"),
                .ToolTipText = $"Anchor {_FileSystem.RootAnchorId}"
            }
            tvFolders.Nodes.Add(RootNode)
            PopulateDirectoryNodes(RootNode, _FileSystem.RootAnchorId, New HashSet(Of Long)())
            RootNode.Expand()

            Dim NodeToSelect = FindDirectoryNode(SelectedAnchorId)
            If NodeToSelect Is Nothing Then NodeToSelect = RootNode
            tvFolders.SelectedNode = NodeToSelect
            NodeToSelect.EnsureVisible()
        Finally
            tvFolders.EndUpdate()
        End Try

        RefreshCurrentDirectory()
    End Sub

    Private Sub PopulateDirectoryNodes(ParentNode As TreeNode,
                                       DirectoryAnchorId As Long,
                                       VisitedDirectories As HashSet(Of Long))
        If VisitedDirectories.Add(DirectoryAnchorId) = False Then
            ParentNode.Nodes.Add(New TreeNode("[Directory cycle]") With {.ForeColor = Color.Red})
            Return
        End If

        Dim Entries = _FileSystem.GetDirectoryEntries(DirectoryAnchorId).
                                  Where(Function(x) x.EntryType = EmbeddedFileSystem.EntryTypes.Directory).
                                  OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                                  ToList()

        For Each entry In Entries
            Dim ChildNode = New TreeNode(entry.Name) With {
                .Name = entry.ChildAnchorId.ToString(),
                .Tag = New DirectoryNodeInfo(entry.ChildAnchorId, entry.Name),
                .ToolTipText = $"Anchor {entry.ChildAnchorId}, {FormatByteLength(entry.LengthOfDataAtEntry)} stored"
            }
            ParentNode.Nodes.Add(ChildNode)
            PopulateDirectoryNodes(ChildNode, entry.ChildAnchorId, VisitedDirectories)
        Next

        VisitedDirectories.Remove(DirectoryAnchorId)
    End Sub

    Private Sub RefreshCurrentDirectory()
        If _CurrentDirectoryAnchorId <= 0 Then Return

        lvFiles.BeginUpdate()
        Try
            lvFiles.Items.Clear()
            Dim Entries = _FileSystem.GetDirectoryEntries(_CurrentDirectoryAnchorId).
                                      Where(Function(x) x.EntryType <> EmbeddedFileSystem.EntryTypes.Directory).
                                      OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                                      ToList()

            For Each entry In Entries
                Dim Item = New ListViewItem(entry.Name) With {.Tag = entry}
                Item.SubItems.Add(FormatByteLength(entry.LengthOfDataAtEntry))
                Item.SubItems.Add(GetEntryStateText(entry.EntryType))
                If entry.EntryType = EmbeddedFileSystem.EntryTypes.PendingFile Then Item.ForeColor = Color.DarkOrange
                lvFiles.Items.Add(Item)
            Next
        Finally
            lvFiles.EndUpdate()
        End Try

        UpdateStatus()
    End Sub

    Private Sub UpdateStatus()
        Dim DirectoryName = If(tvFolders.SelectedNode Is Nothing, "Root", tvFolders.SelectedNode.Text)
        Dim SelectedCount = lvFiles.SelectedItems.Count
        Dim SelectionText = If(SelectedCount = 0, String.Empty, $", {SelectedCount:N0} selected")
        _StatusLabel.Text = $"{DirectoryName}: {lvFiles.Items.Count:N0} files{SelectionText}"
    End Sub

    Private Shared Function GetEntryStateText(EntryType As EmbeddedFileSystem.EntryTypes) As String
        Select Case EntryType
            Case EmbeddedFileSystem.EntryTypes.File
                Return "File"
            Case EmbeddedFileSystem.EntryTypes.PendingFile
                Return "Pending"
            Case EmbeddedFileSystem.EntryTypes.Directory
                Return "Directory"
            Case Else
                Return EntryType.ToString()
        End Select
    End Function

    Private Shared Function FormatByteLength(Length As Long) As String
        If Length < 1024 Then Return $"{Length:N0} B"
        If Length < 1024L * 1024L Then Return $"{Length / 1024.0R:N1} KB"
        If Length < 1024L * 1024L * 1024L Then Return $"{Length / (1024.0R * 1024.0R):N1} MB"
        Return $"{Length / (1024.0R * 1024.0R * 1024.0R):N2} GB"
    End Function

    Private Function GetSelectedDirectoryAnchorId() As Long
        If tvFolders.SelectedNode Is Nothing Then Return 0
        Dim Info = TryCast(tvFolders.SelectedNode.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return 0
        Return Info.AnchorId
    End Function

    Private Function FindDirectoryNode(AnchorId As Long) As TreeNode
        If AnchorId <= 0 Then Return Nothing
        Dim Matches = tvFolders.Nodes.Find(AnchorId.ToString(), True)
        If Matches.Length = 0 Then Return Nothing
        Return Matches(0)
    End Function

    Private Function GetSelectedFileEntries() As List(Of EmbeddedFileSystem.ContentListEntry)
        Return lvFiles.SelectedItems.
                       Cast(Of ListViewItem)().
                       Select(Function(x) TryCast(x.Tag, EmbeddedFileSystem.ContentListEntry)).
                       Where(Function(x) x IsNot Nothing).
                       ToList()
    End Function

    Private Sub tvFolders_AfterSelect(Sender As Object, EventArgs As TreeViewEventArgs) Handles tvFolders.AfterSelect
        Dim Info = TryCast(EventArgs.Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return
        _CurrentDirectoryAnchorId = Info.AnchorId
        RefreshCurrentDirectory()
    End Sub

    Private Sub tvFolders_NodeMouseClick(Sender As Object, EventArgs As TreeNodeMouseClickEventArgs) Handles tvFolders.NodeMouseClick
        If EventArgs.Button = MouseButtons.Right Then tvFolders.SelectedNode = EventArgs.Node
    End Sub

    Private Sub lvFiles_SelectedIndexChanged(Sender As Object, EventArgs As EventArgs) Handles lvFiles.SelectedIndexChanged
        lvFiles.ContextMenuStrip = If(lvFiles.SelectedItems.Count > 0, _FileContextMenu, _EmptyFileContextMenu)
        UpdateStatus()
    End Sub

    Private Sub lvFiles_MouseDoubleClick(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseDoubleClick
        If EventArgs.Button <> MouseButtons.Left OrElse lvFiles.SelectedItems.Count <> 1 Then Return
        SaveSelectedFiles(Me, EventArgs)
    End Sub

    Private Sub FolderContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles _FolderContextMenu.Opening
        _FolderContextMenu.Items.Clear()
        If tvFolders.SelectedNode Is Nothing Then
            EventArgs.Cancel = True
            Return
        End If

        AddMenuItem(_FolderContextMenu, "Open", AddressOf OpenSelectedDirectory)
        _FolderContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FolderContextMenu, "Upload File(s)...", AddressOf UploadFilesFromDialog)
        AddMenuItem(_FolderContextMenu, "Upload Folder...", AddressOf UploadFolderFromDialog)
        AddMenuItem(_FolderContextMenu, "New Folder...", AddressOf CreateFolderFromPrompt)
        _FolderContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FolderContextMenu, "Save Folder As...", AddressOf SaveSelectedFolder)

        Dim IsRoot = GetSelectedDirectoryAnchorId() = _FileSystem.RootAnchorId
        If IsRoot = False Then
            _FolderContextMenu.Items.Add(New ToolStripSeparator())
            AddMenuItem(_FolderContextMenu, "Delete Folder", AddressOf DeleteSelectedFolder)
        End If

        _FolderContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FolderContextMenu, "Refresh", AddressOf RefreshMenuItem_Click)
    End Sub

    Private Sub FileContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles _FileContextMenu.Opening
        _FileContextMenu.Items.Clear()
        Dim Entries = GetSelectedFileEntries()
        If Entries.Count = 0 Then
            EventArgs.Cancel = True
            Return
        End If

        If Entries.Count = 1 Then AddMenuItem(_FileContextMenu, "Save As...", AddressOf SaveSelectedFiles)
        If Entries.Count > 1 Then AddMenuItem(_FileContextMenu, "Save Selected To Folder...", AddressOf SaveSelectedFiles)
        AddMenuItem(_FileContextMenu, "Delete", AddressOf DeleteSelectedFiles)
        _FileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FileContextMenu, "Upload File(s)...", AddressOf UploadFilesFromDialog)
        AddMenuItem(_FileContextMenu, "New Folder...", AddressOf CreateFolderFromPrompt)
        _FileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FileContextMenu, "Refresh", AddressOf RefreshMenuItem_Click)
    End Sub

    Private Sub EmptyFileContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles _EmptyFileContextMenu.Opening
        _EmptyFileContextMenu.Items.Clear()
        AddMenuItem(_EmptyFileContextMenu, "Upload File(s)...", AddressOf UploadFilesFromDialog)
        AddMenuItem(_EmptyFileContextMenu, "Upload Folder...", AddressOf UploadFolderFromDialog)
        AddMenuItem(_EmptyFileContextMenu, "New Folder...", AddressOf CreateFolderFromPrompt)
        _EmptyFileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_EmptyFileContextMenu, "Refresh", AddressOf RefreshMenuItem_Click)
    End Sub

    Private Shared Sub AddMenuItem(Menu As ContextMenuStrip, Text As String, ClickHandler As EventHandler)
        Dim Item = New ToolStripMenuItem(Text)
        AddHandler Item.Click, ClickHandler
        Menu.Items.Add(Item)
    End Sub

    Private Sub OpenSelectedDirectory(Sender As Object, EventArgs As EventArgs)
        If tvFolders.SelectedNode Is Nothing Then Return
        tvFolders.SelectedNode.Expand()
        _CurrentDirectoryAnchorId = GetSelectedDirectoryAnchorId()
        RefreshCurrentDirectory()
    End Sub

    Private Sub CreateFolderFromPrompt(Sender As Object, EventArgs As EventArgs)
        Dim FolderName = PromptForText("New Folder", "Folder name:")
        If FolderName Is Nothing Then Return

        ExecuteUiOperation(
            Sub()
                _FileSystem.CreateDirectory(_CurrentDirectoryAnchorId, FolderName)
                RefreshFileSystemView()
            End Sub,
            "The folder could not be created.")
    End Sub

    Private Sub UploadFilesFromDialog(Sender As Object, EventArgs As EventArgs)
        Using Dialog As New OpenFileDialog With {
            .Title = "Upload files",
            .Filter = "All files (*.*)|*.*",
            .Multiselect = True,
            .CheckFileExists = True
        }
            If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            UploadPaths(Dialog.FileNames, _CurrentDirectoryAnchorId)
        End Using
    End Sub

    Private Sub UploadFolderFromDialog(Sender As Object, EventArgs As EventArgs)
        Using Dialog As New FolderBrowserDialog With {.Description = "Select a folder to upload"}
            If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            UploadPaths(New String() {Dialog.SelectedPath}, _CurrentDirectoryAnchorId)
        End Using
    End Sub

    Private Sub UploadPaths(Paths As IEnumerable(Of String), TargetDirectoryAnchorId As Long)
        Dim PathList = Paths.Where(Function(x) String.IsNullOrWhiteSpace(x) = False).ToList()
        If PathList.Count = 0 Then Return

        ExecuteUiOperation(
            Sub()
                UseWaitCursor = True
                For Each sourcePath In PathList
                    If File.Exists(sourcePath) Then
                        UploadFile(sourcePath, TargetDirectoryAnchorId)
                    ElseIf Directory.Exists(sourcePath) Then
                        UploadDirectory(sourcePath, TargetDirectoryAnchorId)
                    End If
                Next
                RefreshFileSystemView()
            End Sub,
            "One or more items could not be uploaded.")
    End Sub

    Private Sub UploadFile(SourceFilePath As String, ParentDirectoryAnchorId As Long)
        Dim FileName = Path.GetFileName(SourceFilePath)
        EnsureDestinationNameDoesNotExist(ParentDirectoryAnchorId, FileName)
        Dim FileAnchorId = _FileSystem.CreateFile(ParentDirectoryAnchorId, FileName)

        Try
            Using SourceStream = New FileStream(SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                Using DestinationStream = _FileSystem.OpenFile(FileAnchorId)
                    SourceStream.CopyTo(DestinationStream, 1024 * 1024)
                    DestinationStream.Flush()
                End Using
            End Using
        Catch
            Try
                _FileSystem.DeleteEntry(ParentDirectoryAnchorId, FileName)
            Catch
            End Try
            Throw
        End Try
    End Sub

    Private Sub UploadDirectory(SourceDirectoryPath As String, ParentDirectoryAnchorId As Long)
        Dim DirectoryName = New DirectoryInfo(SourceDirectoryPath).Name
        EnsureDestinationNameDoesNotExist(ParentDirectoryAnchorId, DirectoryName)
        Dim DirectoryAnchorId = _FileSystem.CreateDirectory(ParentDirectoryAnchorId, DirectoryName)

        Try
            For Each sourceFilePath In Directory.EnumerateFiles(SourceDirectoryPath)
                UploadFile(sourceFilePath, DirectoryAnchorId)
            Next
            For Each childDirectoryPath In Directory.EnumerateDirectories(SourceDirectoryPath)
                UploadDirectory(childDirectoryPath, DirectoryAnchorId)
            Next
        Catch
            Try
                _FileSystem.DeleteEntry(ParentDirectoryAnchorId, DirectoryName)
            Catch
            End Try
            Throw
        End Try
    End Sub

    Private Sub EnsureDestinationNameDoesNotExist(ParentDirectoryAnchorId As Long, Name As String)
        If _FileSystem.GetDirectoryEntries(ParentDirectoryAnchorId).
                       Any(Function(x) String.Equals(x.Name, Name, StringComparison.OrdinalIgnoreCase)) Then
            Throw New IOException($"An item named '{Name}' already exists in the destination folder.")
        End If
    End Sub

    Private Sub SaveSelectedFiles(Sender As Object, EventArgs As EventArgs)
        Dim Entries = GetSelectedFileEntries()
        If Entries.Count = 0 Then Return

        If Entries.Count = 1 Then
            Using Dialog As New SaveFileDialog With {
                .Title = "Save file",
                .FileName = Entries(0).Name,
                .Filter = "All files (*.*)|*.*",
                .OverwritePrompt = True
            }
                If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                ExportFile(Entries(0), Dialog.FileName)
            End Using
            Return
        End If

        Using Dialog As New FolderBrowserDialog With {.Description = "Select a destination for the selected files"}
            If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            ExecuteUiOperation(
                Sub()
                    UseWaitCursor = True
                    For Each entry In Entries
                        ExportFile(entry, Path.Combine(Dialog.SelectedPath, MakeSafeFileName(entry.Name)))
                    Next
                End Sub,
                "One or more files could not be saved.")
        End Using
    End Sub

    Private Sub SaveSelectedFolder(Sender As Object, EventArgs As EventArgs)
        Dim SelectedNode = tvFolders.SelectedNode
        If SelectedNode Is Nothing Then Return
        Dim Info = TryCast(SelectedNode.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return

        Using Dialog As New FolderBrowserDialog With {.Description = "Select the destination folder"}
            If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim OutputPath = Path.Combine(Dialog.SelectedPath, MakeSafeFileName(Info.Name))
            ExecuteUiOperation(
                Sub()
                    UseWaitCursor = True
                    ExportDirectory(Info.AnchorId, OutputPath)
                End Sub,
                "The folder could not be saved.")
        End Using
    End Sub

    Private Sub ExportFile(Entry As EmbeddedFileSystem.ContentListEntry, DestinationFilePath As String)
        Dim DestinationDirectory = Path.GetDirectoryName(DestinationFilePath)
        If String.IsNullOrEmpty(DestinationDirectory) = False Then Directory.CreateDirectory(DestinationDirectory)

        Using SourceStream = _FileSystem.OpenFile(Entry.ChildAnchorId)
            Using DestinationStream = New FileStream(DestinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None)
                SourceStream.CopyTo(DestinationStream, 1024 * 1024)
            End Using
        End Using
    End Sub

    Private Sub ExportDirectory(DirectoryAnchorId As Long, DestinationDirectoryPath As String)
        Directory.CreateDirectory(DestinationDirectoryPath)
        Dim Entries = _FileSystem.GetDirectoryEntries(DirectoryAnchorId)

        For Each entry In Entries
            Dim OutputName = MakeSafeFileName(entry.Name)
            Dim OutputPath = Path.Combine(DestinationDirectoryPath, OutputName)
            If entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory Then
                ExportDirectory(entry.ChildAnchorId, OutputPath)
            Else
                ExportFile(entry, OutputPath)
            End If
        Next
    End Sub

    Private Shared Function MakeSafeFileName(Name As String) As String
        Dim InvalidCharacters = Path.GetInvalidFileNameChars()
        Dim Characters = Name.ToCharArray()
        For Index = 0 To Characters.Length - 1
            If InvalidCharacters.Contains(Characters(Index)) Then Characters(Index) = "_"c
        Next
        Dim Result = New String(Characters).Trim()
        If Result.Length = 0 Then Return "unnamed"
        Return Result
    End Function

    Private Sub DeleteSelectedFiles(Sender As Object, EventArgs As EventArgs)
        Dim Entries = GetSelectedFileEntries()
        If Entries.Count = 0 Then Return

        Dim Prompt = If(Entries.Count = 1,
                        $"Delete '{Entries(0).Name}'?",
                        $"Delete the {Entries.Count:N0} selected files?")
        If MessageBox.Show(Me, Prompt, "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return

        ExecuteUiOperation(
            Sub()
                For Each entry In Entries
                    _FileSystem.DeleteEntry(_CurrentDirectoryAnchorId, entry.Name)
                Next
                RefreshFileSystemView()
            End Sub,
            "One or more files could not be deleted.")
    End Sub

    Private Sub DeleteSelectedFolder(Sender As Object, EventArgs As EventArgs)
        Dim SelectedNode = tvFolders.SelectedNode
        If SelectedNode Is Nothing OrElse SelectedNode.Parent Is Nothing Then Return

        Dim Info = TryCast(SelectedNode.Tag, DirectoryNodeInfo)
        Dim ParentInfo = TryCast(SelectedNode.Parent.Tag, DirectoryNodeInfo)
        If Info Is Nothing OrElse ParentInfo Is Nothing Then Return

        If MessageBox.Show(Me,
                           $"Delete '{Info.Name}' and all of its contents?",
                           "Delete Folder",
                           MessageBoxButtons.YesNo,
                           MessageBoxIcon.Warning) <> DialogResult.Yes Then Return

        ExecuteUiOperation(
            Sub()
                _FileSystem.DeleteEntry(ParentInfo.AnchorId, Info.Name)
                _CurrentDirectoryAnchorId = ParentInfo.AnchorId
                RefreshFileSystemView()
            End Sub,
            "The folder could not be deleted.")
    End Sub

    Private Sub RefreshMenuItem_Click(Sender As Object, EventArgs As EventArgs)
        ExecuteUiOperation(AddressOf RefreshFileSystemView, "The file-system view could not be refreshed.")
    End Sub

    Private Sub FileSystemControl_DragEnter(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragEnter, lvFiles.DragEnter
        EventArgs.Effect = GetExternalDropEffect(EventArgs.Data, EventArgs.AllowedEffect)
    End Sub

    Private Sub lvFiles_DragOver(Sender As Object, EventArgs As DragEventArgs) Handles lvFiles.DragOver
        EventArgs.Effect = GetExternalDropEffect(EventArgs.Data, EventArgs.AllowedEffect)
    End Sub

    Private Sub tvFolders_DragOver(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragOver
        EventArgs.Effect = GetExternalDropEffect(EventArgs.Data, EventArgs.AllowedEffect)
        If EventArgs.Effect = DragDropEffects.None Then Return

        Dim ClientPoint = tvFolders.PointToClient(New Point(EventArgs.X, EventArgs.Y))
        Dim TargetNode = tvFolders.GetNodeAt(ClientPoint)
        If TargetNode IsNot Nothing Then tvFolders.SelectedNode = TargetNode
    End Sub

    Private Shared Function GetExternalDropEffect(Data As IDataObject, AllowedEffect As DragDropEffects) As DragDropEffects
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return DragDropEffects.None
        If AllowedEffect.HasFlag(DragDropEffects.Copy) Then Return DragDropEffects.Copy
        Return DragDropEffects.None
    End Function

    Private Sub lvFiles_DragDrop(Sender As Object, EventArgs As DragEventArgs) Handles lvFiles.DragDrop
        UploadDroppedPaths(EventArgs.Data, _CurrentDirectoryAnchorId)
    End Sub

    Private Sub tvFolders_DragDrop(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragDrop
        Dim ClientPoint = tvFolders.PointToClient(New Point(EventArgs.X, EventArgs.Y))
        Dim TargetNode = tvFolders.GetNodeAt(ClientPoint)
        If TargetNode Is Nothing Then Return
        Dim Info = TryCast(TargetNode.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return
        UploadDroppedPaths(EventArgs.Data, Info.AnchorId)
    End Sub

    Private Sub UploadDroppedPaths(Data As IDataObject, TargetDirectoryAnchorId As Long)
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return
        Dim Paths = TryCast(Data.GetData(DataFormats.FileDrop), String())
        If Paths Is Nothing OrElse Paths.Length = 0 Then Return
        UploadPaths(Paths, TargetDirectoryAnchorId)
    End Sub

    Private Sub lvFiles_MouseDown(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseDown
        _ListDragStart = EventArgs.Location
        _ListDragArmed = EventArgs.Button = MouseButtons.Left AndAlso lvFiles.SelectedItems.Count > 0
    End Sub

    Private Sub lvFiles_MouseMove(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseMove
        If _ListDragArmed = False OrElse EventArgs.Button <> MouseButtons.Left Then Return
        If IsDragThresholdExceeded(_ListDragStart, EventArgs.Location) = False Then Return
        _ListDragArmed = False

        Dim Entries = GetSelectedFileEntries()
        If Entries.Count = 0 Then Return
        BeginExternalFileDrag(Entries)
    End Sub

    Private Sub lvFiles_MouseUp(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseUp
        _ListDragArmed = False
    End Sub

    Private Sub tvFolders_MouseDown(Sender As Object, EventArgs As MouseEventArgs) Handles tvFolders.MouseDown
        _TreeDragStart = EventArgs.Location
        _TreeDragArmed = EventArgs.Button = MouseButtons.Left AndAlso tvFolders.GetNodeAt(EventArgs.Location) IsNot Nothing
    End Sub

    Private Sub tvFolders_MouseMove(Sender As Object, EventArgs As MouseEventArgs) Handles tvFolders.MouseMove
        If _TreeDragArmed = False OrElse EventArgs.Button <> MouseButtons.Left Then Return
        If IsDragThresholdExceeded(_TreeDragStart, EventArgs.Location) = False Then Return
        _TreeDragArmed = False

        Dim Node = tvFolders.GetNodeAt(_TreeDragStart)
        If Node Is Nothing Then Return
        tvFolders.SelectedNode = Node
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return
        BeginExternalDirectoryDrag(Info)
    End Sub

    Private Sub tvFolders_MouseUp(Sender As Object, EventArgs As MouseEventArgs) Handles tvFolders.MouseUp
        _TreeDragArmed = False
    End Sub

    Private Shared Function IsDragThresholdExceeded(StartPoint As Point, CurrentPoint As Point) As Boolean
        Dim DragSize = SystemInformation.DragSize
        Dim DragRectangle = New Rectangle(StartPoint.X - (DragSize.Width \ 2),
                                          StartPoint.Y - (DragSize.Height \ 2),
                                          DragSize.Width,
                                          DragSize.Height)
        Return DragRectangle.Contains(CurrentPoint) = False
    End Function

    Private Sub BeginExternalFileDrag(Entries As IList(Of EmbeddedFileSystem.ContentListEntry))
        ExecuteUiOperation(
            Sub()
                UseWaitCursor = True
                Using Export = CreateTemporaryFileExport(Entries)
                    UseWaitCursor = False
                    Dim Data = New DataObject(DataFormats.FileDrop, Export.Paths)
                    lvFiles.DoDragDrop(Data, DragDropEffects.Copy)
                End Using
            End Sub,
            "The selected files could not be prepared for drag-and-drop.")
    End Sub

    Private Sub BeginExternalDirectoryDrag(Info As DirectoryNodeInfo)
        ExecuteUiOperation(
            Sub()
                UseWaitCursor = True
                Using Export = CreateTemporaryDirectoryExport(Info)
                    UseWaitCursor = False
                    Dim Data = New DataObject(DataFormats.FileDrop, Export.Paths)
                    tvFolders.DoDragDrop(Data, DragDropEffects.Copy)
                End Using
            End Sub,
            "The selected folder could not be prepared for drag-and-drop.")
    End Sub

    Private Function CreateTemporaryFileExport(Entries As IList(Of EmbeddedFileSystem.ContentListEntry)) As DragExport
        Dim TemporaryDirectory = CreateTemporaryExportDirectory()
        Try
            Dim OutputPaths As New List(Of String)(Entries.Count)
            For Each entry In Entries
                Dim OutputPath = GetUniquePath(TemporaryDirectory, MakeSafeFileName(entry.Name), False)
                ExportFile(entry, OutputPath)
                OutputPaths.Add(OutputPath)
            Next
            Return New DragExport(TemporaryDirectory, OutputPaths.ToArray())
        Catch
            Try
                Directory.Delete(TemporaryDirectory, True)
            Catch
            End Try
            Throw
        End Try
    End Function

    Private Function CreateTemporaryDirectoryExport(Info As DirectoryNodeInfo) As DragExport
        Dim TemporaryDirectory = CreateTemporaryExportDirectory()
        Try
            Dim OutputPath = GetUniquePath(TemporaryDirectory, MakeSafeFileName(Info.Name), True)
            ExportDirectory(Info.AnchorId, OutputPath)
            Return New DragExport(TemporaryDirectory, New String() {OutputPath})
        Catch
            Try
                Directory.Delete(TemporaryDirectory, True)
            Catch
            End Try
            Throw
        End Try
    End Function

    Private Shared Function CreateTemporaryExportDirectory() As String
        Dim TemporaryDirectory = Path.Combine(Path.GetTempPath(),
                                              "EmbeddedFileSystemDrag",
                                              Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(TemporaryDirectory)
        Return TemporaryDirectory
    End Function

    Private Shared Function GetUniquePath(DirectoryPath As String, Name As String, IsDirectory As Boolean) As String
        Dim Candidate = Path.Combine(DirectoryPath, Name)
        If File.Exists(Candidate) = False AndAlso Directory.Exists(Candidate) = False Then Return Candidate

        Dim BaseName = If(IsDirectory, Name, Path.GetFileNameWithoutExtension(Name))
        Dim Extension = If(IsDirectory, String.Empty, Path.GetExtension(Name))
        Dim Number = 2
        Do
            Candidate = Path.Combine(DirectoryPath, $"{BaseName} ({Number}){Extension}")
            Number += 1
        Loop While File.Exists(Candidate) OrElse Directory.Exists(Candidate)
        Return Candidate
    End Function

    Private Sub BrowserForm_KeyDown(Sender As Object, EventArgs As KeyEventArgs) Handles Me.KeyDown
        If EventArgs.KeyCode = Keys.F5 Then
            RefreshMenuItem_Click(Me, EventArgs)
            EventArgs.Handled = True
            Return
        End If

        If EventArgs.KeyCode = Keys.Delete AndAlso lvFiles.Focused AndAlso lvFiles.SelectedItems.Count > 0 Then
            DeleteSelectedFiles(Me, EventArgs)
            EventArgs.Handled = True
        End If
    End Sub

    Private Sub ExecuteUiOperation(Operation As Action, ErrorMessage As String)
        Try
            Operation()
        Catch OperationException As Exception
            MessageBox.Show(Me,
                            $"{ErrorMessage}{Environment.NewLine}{Environment.NewLine}{OperationException.Message}",
                            Text,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error)
        Finally
            UseWaitCursor = False
        End Try
    End Sub

    Private Shared Function PromptForText(Title As String, Prompt As String) As String
        Using Dialog As New Form()
            Dialog.Text = Title
            Dialog.FormBorderStyle = FormBorderStyle.FixedDialog
            Dialog.StartPosition = FormStartPosition.CenterParent
            Dialog.MinimizeBox = False
            Dialog.MaximizeBox = False
            Dialog.ShowInTaskbar = False
            Dialog.ClientSize = New Size(430, 118)

            Dim PromptLabel = New Label With {
                .AutoSize = True,
                .Left = 12,
                .Top = 14,
                .Text = Prompt
            }
            Dim InputTextBox = New TextBox With {
                .Left = 12,
                .Top = 38,
                .Width = 406,
                .MaxLength = 256
            }
            Dim OkButton = New Button With {
                .Text = "OK",
                .DialogResult = DialogResult.OK,
                .Left = 262,
                .Top = 76,
                .Width = 75
            }
            Dim CancelButton = New Button With {
                .Text = "Cancel",
                .DialogResult = DialogResult.Cancel,
                .Left = 343,
                .Top = 76,
                .Width = 75
            }

            Dialog.Controls.Add(PromptLabel)
            Dialog.Controls.Add(InputTextBox)
            Dialog.Controls.Add(OkButton)
            Dialog.Controls.Add(CancelButton)
            Dialog.AcceptButton = OkButton
            Dialog.CancelButton = CancelButton

            If Dialog.ShowDialog() <> DialogResult.OK Then Return Nothing
            Dim Result = InputTextBox.Text.Trim()
            If Result.Length = 0 Then Return Nothing
            Return Result
        End Using
    End Function

    Protected Overrides Sub Dispose(Disposing As Boolean)
        If _Disposed Then Return

        If Disposing Then
            If Components IsNot Nothing Then Components.Dispose()
            If _OwnsFileSystem Then _FileSystem.Dispose()
        End If

        _Disposed = True
        MyBase.Dispose(Disposing)
    End Sub
End Class
