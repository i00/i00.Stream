Imports i00.Streams
Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports i00CodeLib
Imports System.Runtime.InteropServices

Partial Public NotInheritable Class EmbeddedFileSystemBrowserForm

    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SetWindowTheme(hWnd As IntPtr, pszSubAppName As String, pszSubIdList As String) As Integer
    End Function

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

    ''' <summary>How a name collision during an upload is resolved.</summary>
    Private Enum ConflictChoice
        Skip
        SkipAll
        Replace
        ReplaceAll
        Cancel
    End Enum

    ''' <summary>One resolved copy step produced by walking the dropped or picked paths.</summary>
    Private NotInheritable Class UploadWorkItem
        Public Sub New(SourcePath As String, RelativeParent As String, Name As String, IsDirectory As Boolean)
            Me.SourcePath = SourcePath
            Me.RelativeParent = RelativeParent
            Me.Name = Name
            Me.IsDirectory = IsDirectory
        End Sub

        ''' <summary>The on-disk file to copy, or Nothing for a directory-create step.</summary>
        Public ReadOnly Property SourcePath As String
        ''' <summary>'/'-joined parent directories relative to the upload target, or "" for the target itself.</summary>
        Public ReadOnly Property RelativeParent As String
        Public ReadOnly Property Name As String
        Public ReadOnly Property IsDirectory As Boolean
    End Class

    ''' <summary>
    ''' Publishes a total byte count computed on a background thread. Reads and writes are
    ''' interlocked so the copy thread never observes a torn value.
    ''' </summary>
    Private NotInheritable Class TotalSizeBox
        Private _Value As Long
        Private _HasValue As Integer

        Public Sub Publish(Value As Long)
            Interlocked.Exchange(_Value, Value)
            Interlocked.Exchange(_HasValue, 1)
        End Sub

        Public ReadOnly Property Value As Long?
            Get
                If Interlocked.CompareExchange(_HasValue, 0, 0) = 0 Then Return Nothing
                Return Interlocked.Read(_Value)
            End Get
        End Property
    End Class

    ''' <summary>Sorts the file list with directories always ahead of files.</summary>
    Private NotInheritable Class EntryListViewComparer
        Implements IComparer

        Public Property Column As Integer
        Public Property Order As SortOrder = SortOrder.Ascending

        Public Function Compare(X As Object, Y As Object) As Integer Implements IComparer.Compare
            Dim Left = DirectCast(X, ListViewItem)
            Dim Right = DirectCast(Y, ListViewItem)
            Dim LeftEntry = TryCast(Left.Tag, EmbeddedFileSystem.ContentListEntry)
            Dim RightEntry = TryCast(Right.Tag, EmbeddedFileSystem.ContentListEntry)

            Dim LeftRank = If(IsDirectoryEntry(LeftEntry), 0, 1)
            Dim RightRank = If(IsDirectoryEntry(RightEntry), 0, 1)
            If LeftRank <> RightRank Then Return LeftRank - RightRank

            Dim Result As Integer
            If Column = 1 Then
                Result = EntryLength(LeftEntry).CompareTo(EntryLength(RightEntry))
            ElseIf Column = 2 Then
                Result = String.Compare(Left.SubItems(2).Text, Right.SubItems(2).Text, StringComparison.OrdinalIgnoreCase)
            Else
                Result = String.Compare(Left.Text, Right.Text, StringComparison.OrdinalIgnoreCase)
            End If

            If Result = 0 Then Result = String.Compare(Left.Text, Right.Text, StringComparison.OrdinalIgnoreCase)
            If Order = SortOrder.Descending Then Result = -Result
            Return Result
        End Function

        Private Shared Function IsDirectoryEntry(Entry As EmbeddedFileSystem.ContentListEntry) As Boolean
            Return Entry IsNot Nothing AndAlso Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
        End Function

        Private Shared Function EntryLength(Entry As EmbeddedFileSystem.ContentListEntry) As Long
            Return If(Entry Is Nothing, 0L, Entry.LengthOfDataAtEntry)
        End Function
    End Class

    Private ReadOnly _FileSystem As EmbeddedFileSystem
    Private ReadOnly _IconProvider As New FileIconProvider()
    Private ReadOnly _ListSorter As New EntryListViewComparer()
    Private _CurrentDirectoryAnchorId As Long
    Private _ListDragStart As Point
    Private _TreeDragStart As Point
    Private _ListDragArmed As Boolean
    Private _TreeDragArmed As Boolean
    Private _Disposed As Boolean
    Private _IconGeneration As Integer
    Private _ExecutableIconThread As Thread


    Public Sub New(FileSystem As EmbeddedFileSystem)
        If FileSystem Is Nothing Then Throw New ArgumentNullException(NameOf(FileSystem))

        _FileSystem = FileSystem
        _CurrentDirectoryAnchorId = _FileSystem.RootAnchorId

        InitializeComponent()

        tvFolders.ImageList = _IconProvider.SmallImages
        lvFiles.SmallImageList = _IconProvider.SmallImages
        lvFiles.LargeImageList = _IconProvider.LargeImages
        lvFiles.TileSize = New Size(260, (FileIconProvider.LargeIconSize * 3) \ 2)
        lvFiles.ListViewItemSorter = _ListSorter

        Try
            SetWindowTheme(tvFolders.Handle, "Explorer", Nothing)
            SetWindowTheme(lvFiles.Handle, "Explorer", Nothing)
        Catch ex As Exception

        End Try

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
                .ToolTipText = $"Anchor {_FileSystem.RootAnchorId}",
                .ImageKey = FileIconProvider.FolderKey,
                .SelectedImageKey = FileIconProvider.FolderKey
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


        Dim Fragmentation = _FileSystem.ChunkedStream.GetFragmentation

        If Fragmentation >= 0.1 Then
            tsiCompression.Visible = True
            tsiCompressionBar.Visible = False
            tsiFragmentation.Visible = True

            tsiCompression.Text = $"Fragmentation: {Fragmentation:P0}"

        ElseIf _FileSystem.GetDirectoryEntries(_FileSystem.RootAnchorId).Any() = False Then
            tsiCompression.Visible = True
            tsiCompressionBar.Visible = False
            tsiFragmentation.Visible = False

            tsiCompression.Text = "No Data"
        Else
            tsiCompression.Visible = True
            tsiCompressionBar.Visible = True
            tsiFragmentation.Visible = False

            Dim Ratio = _FileSystem.ChunkedStream.Length / _FileSystem.ChunkedStream.BaseStream.Length

            Dim CompressionPercent = Ratio * 100
            tsiCompression.Text = $"Relative size: {Ratio:P0}"
            tsiCompressionBar.MaxValue = Math.Max(CompressionPercent, 100)
            tsiCompressionBar.Value = CompressionPercent
        End If
    End Sub

    Private Sub PopulateDirectoryNodes(ParentNode As TreeNode,
                                       DirectoryAnchorId As Long,
                                       VisitedDirectories As HashSet(Of Long))
        If VisitedDirectories.Add(DirectoryAnchorId) = False Then
            ParentNode.Nodes.Add(New TreeNode("[Directory cycle]") With {.ForeColor = i00CodeLib.Drawing.BlendColor(tvFolders.ForeColor, Color.Red)})
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
                .ToolTipText = $"Anchor {entry.ChildAnchorId}, {FormatByteLength(entry.LengthOfDataAtEntry)} stored",
                .ImageKey = FileIconProvider.FolderKey,
                .SelectedImageKey = FileIconProvider.FolderKey
            }
            ParentNode.Nodes.Add(ChildNode)
            PopulateDirectoryNodes(ChildNode, entry.ChildAnchorId, VisitedDirectories)
        Next

        VisitedDirectories.Remove(DirectoryAnchorId)
    End Sub

    Private Sub RefreshCurrentDirectory()
        If _CurrentDirectoryAnchorId <= 0 Then Return

        ' A new listing invalidates any executable-icon pass still running for the previous one.
        _IconGeneration += 1
        Dim Generation = _IconGeneration

        Dim Entries = _FileSystem.GetDirectoryEntries(_CurrentDirectoryAnchorId).
                                  OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                                  ToList()

        lvFiles.BeginUpdate()
        Try
            lvFiles.ListViewItemSorter = Nothing
            lvFiles.Items.Clear()

            For Each entry In Entries
                Dim IsDirectory = entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
                Dim Item = New ListViewItem(entry.Name) With {
                    .Tag = entry,
                    .ImageKey = ResolveEntryImageKey(entry, IsDirectory)
                }
                Item.SubItems.Add(If(IsDirectory, String.Empty, FormatByteLength(entry.LengthOfDataAtEntry)))
                Item.SubItems.Add(GetEntryStateText(entry.EntryType))
                If entry.EntryType = EmbeddedFileSystem.EntryTypes.PendingFile Then Item.ForeColor = i00CodeLib.Drawing.BlendColor(lvFiles.ForeColor, Color.Red)
                lvFiles.Items.Add(Item)
            Next

            lvFiles.ListViewItemSorter = _ListSorter
            lvFiles.Sort()
        Finally
            lvFiles.EndUpdate()
        End Try

        UpdateStatus()

        ' Before the window is shown the icon pass cannot marshal its results back; BrowserForm_Shown
        ' runs it for the first listing once the handle exists.
        If IsHandleCreated Then BeginExecutableIconLoad(Generation, Entries)
    End Sub

    Private Sub BrowserForm_Shown(Sender As Object, EventArgs As EventArgs) Handles Me.Shown
        _IconGeneration += 1
        BeginExecutableIconLoad(_IconGeneration, _FileSystem.GetDirectoryEntries(_CurrentDirectoryAnchorId).ToList())
    End Sub

    Private Function ResolveEntryImageKey(Entry As EmbeddedFileSystem.ContentListEntry, IsDirectory As Boolean) As String
        If IsDirectory Then Return FileIconProvider.FolderKey

        Dim ExecutableKey = $"exe:{Entry.ChildAnchorId}"
        If _IconProvider.ContainsKey(ExecutableKey) Then Return ExecutableKey
        Return _IconProvider.EnsureExtensionIcon(Entry.Name)
    End Function

    ''' <summary>
    ''' Loads each executable's own icon on a background thread and swaps it into the list when ready.
    ''' Until then the shared <c>.exe</c> icon is shown; if extraction fails it simply stays. Results
    ''' are keyed by anchor ID so an executable is only read out of the store once per session.
    ''' </summary>
    Private Sub BeginExecutableIconLoad(Generation As Integer, Entries As List(Of EmbeddedFileSystem.ContentListEntry))
        Dim Executables = Entries.Where(Function(x) x.EntryType <> EmbeddedFileSystem.EntryTypes.Directory AndAlso
                                                    String.Equals(Path.GetExtension(x.Name), ".exe", StringComparison.OrdinalIgnoreCase) AndAlso
                                                    x.LengthOfDataAtEntry > 0 AndAlso
                                                    _IconProvider.ContainsKey($"exe:{x.ChildAnchorId}") = False).
                                  ToList()
        If Executables.Count = 0 Then Return

        Dim Worker = New Thread(
            Sub()
                For Each entry In Executables
                    If Generation <> _IconGeneration OrElse _Disposed Then Return

                    Dim ExtractedIcons As FileIconProvider.ExecutableIcons = Nothing
                    Try
                        Using Source = _FileSystem.OpenFile(entry.ChildAnchorId)
                            ExtractedIcons = FileIconProvider.ExtractExecutableIcons(Source, Source.Length)
                        End Using
                    Catch
                        ExtractedIcons = Nothing
                    End Try

                    If ExtractedIcons IsNot Nothing Then ApplyExecutableIcon(Generation, entry.ChildAnchorId, ExtractedIcons)
                Next
            End Sub) With {.IsBackground = True, .Name = "EFS executable icons"}
        _ExecutableIconThread = Worker
        Worker.Start()
    End Sub

    Private Sub ApplyExecutableIcon(Generation As Integer, AnchorId As Long, ExtractedIcons As FileIconProvider.ExecutableIcons)
        If _Disposed OrElse IsHandleCreated = False Then
            ExtractedIcons.Dispose()
            Return
        End If

        Dim Key = $"exe:{AnchorId}"
        Try
            BeginInvoke(
                Sub()
                    If Generation <> _IconGeneration OrElse _Disposed Then
                        ExtractedIcons.Dispose()
                        Return
                    End If

                    ' The provider takes ownership of the icons here; they must not be disposed elsewhere.
                    _IconProvider.AddExecutableIcon(Key, ExtractedIcons)

                    For Each item As ListViewItem In lvFiles.Items
                        Dim ItemEntry = TryCast(item.Tag, EmbeddedFileSystem.ContentListEntry)
                        If ItemEntry IsNot Nothing AndAlso ItemEntry.ChildAnchorId = AnchorId Then
                            item.ImageKey = Key
                            Exit For
                        End If
                    Next
                End Sub)
        Catch ex As InvalidOperationException
            ' The form closed between the guard above and the marshalled call.
            ExtractedIcons.Dispose()
        End Try
    End Sub

    Private Sub UpdateStatus()
        Dim DirectoryName = If(tvFolders.SelectedNode Is Nothing, "Root", tvFolders.SelectedNode.Text)
        Dim SelectedCount = lvFiles.SelectedItems.Count
        Dim SelectionText = If(SelectedCount = 0, String.Empty, $", {SelectedCount:N0} selected")
        StatusLabel.Text = $"{DirectoryName}: {lvFiles.Items.Count:N0} items{SelectionText}"
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
        'Return Me.InvokeIfRequired(
        '    Function()
        '    End Function)
    End Function

    Private Function FindDirectoryNode(AnchorId As Long) As TreeNode
        If AnchorId <= 0 Then Return Nothing
        Dim Matches = tvFolders.Nodes.Find(AnchorId.ToString(), True)
        If Matches.Length = 0 Then Return Nothing
        Return Matches(0)
    End Function

    Private Function GetSelectedEntries() As List(Of EmbeddedFileSystem.ContentListEntry)
        Return lvFiles.SelectedItems.
                       Cast(Of ListViewItem)().
                       Select(Function(x) TryCast(x.Tag, EmbeddedFileSystem.ContentListEntry)).
                       Where(Function(x) x IsNot Nothing).
                       ToList()
    End Function

    Private Shared Function IsDirectory(Entry As EmbeddedFileSystem.ContentListEntry) As Boolean
        Return Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
    End Function

    Private Sub NavigateToDirectory(DirectoryAnchorId As Long)
        Dim Node = FindDirectoryNode(DirectoryAnchorId)
        If Node Is Nothing Then Return
        Node.EnsureVisible()
        tvFolders.SelectedNode = Node
    End Sub

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

        Dim Entry = TryCast(lvFiles.SelectedItems(0).Tag, EmbeddedFileSystem.ContentListEntry)
        If Entry IsNot Nothing AndAlso IsDirectory(Entry) Then
            NavigateToDirectory(Entry.ChildAnchorId)
            Return
        End If

        SaveSelectedEntries(Me, EventArgs)
    End Sub

    Private Sub lvFiles_ColumnClick(Sender As Object, EventArgs As ColumnClickEventArgs) Handles lvFiles.ColumnClick
        If EventArgs.Column = _ListSorter.Column Then
            _ListSorter.Order = If(_ListSorter.Order = SortOrder.Ascending, SortOrder.Descending, SortOrder.Ascending)
        Else
            _ListSorter.Column = EventArgs.Column
            _ListSorter.Order = SortOrder.Ascending
        End If
        lvFiles.Sort()
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
        Dim Entries = GetSelectedEntries()
        If Entries.Count = 0 Then
            EventArgs.Cancel = True
            Return
        End If

        If Entries.Count = 1 AndAlso IsDirectory(Entries(0)) Then
            AddMenuItem(_FileContextMenu, "Open", Sub() NavigateToDirectory(Entries(0).ChildAnchorId))
            AddMenuItem(_FileContextMenu, "Save Folder As...", AddressOf SaveSelectedEntries)
        ElseIf Entries.Count = 1 Then
            AddMenuItem(_FileContextMenu, "Save As...", AddressOf SaveSelectedEntries)
        Else
            AddMenuItem(_FileContextMenu, "Save Selected To Folder...", AddressOf SaveSelectedEntries)
        End If
        AddMenuItem(_FileContextMenu, "Delete", AddressOf DeleteSelectedEntries)
        _FileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_FileContextMenu, "Upload File(s)...", AddressOf UploadFilesFromDialog)
        AddMenuItem(_FileContextMenu, "New Folder...", AddressOf CreateFolderFromPrompt)
        _FileContextMenu.Items.Add(New ToolStripSeparator())
        AddViewMenu(_FileContextMenu)
        AddMenuItem(_FileContextMenu, "Refresh", AddressOf RefreshMenuItem_Click)
    End Sub

    Private Sub EmptyFileContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles _EmptyFileContextMenu.Opening
        _EmptyFileContextMenu.Items.Clear()
        AddViewMenu(_EmptyFileContextMenu)
        _EmptyFileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_EmptyFileContextMenu, "Upload File(s)...", AddressOf UploadFilesFromDialog)
        AddMenuItem(_EmptyFileContextMenu, "Upload Folder...", AddressOf UploadFolderFromDialog)
        AddMenuItem(_EmptyFileContextMenu, "New Folder...", AddressOf CreateFolderFromPrompt)
        _EmptyFileContextMenu.Items.Add(New ToolStripSeparator())
        AddMenuItem(_EmptyFileContextMenu, "Refresh", AddressOf RefreshMenuItem_Click)
    End Sub

    Private Sub AddViewMenu(Menu As ContextMenuStrip)
        Dim ViewMenu = New ToolStripMenuItem("View")
        AddViewOption(ViewMenu, "Large Icons", View.LargeIcon)
        AddViewOption(ViewMenu, "Small Icons", View.SmallIcon)
        AddViewOption(ViewMenu, "List", View.List)
        AddViewOption(ViewMenu, "Details", View.Details)
        AddViewOption(ViewMenu, "Tiles", View.Tile)
        Menu.Items.Add(ViewMenu)
    End Sub

    Private Sub AddViewOption(ViewMenu As ToolStripMenuItem, Text As String, TargetView As View)
        Dim Item = New ToolStripMenuItem(Text) With {.Checked = lvFiles.View = TargetView}
        AddHandler Item.Click, Sub() lvFiles.View = TargetView
        ViewMenu.DropDownItems.Add(Item)
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

        _FileSystem.CreateDirectory(_CurrentDirectoryAnchorId, FolderName)

        RefreshFileSystemView()
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
        Dim RootPaths = Paths.Where(Function(x) String.IsNullOrWhiteSpace(x) = False).ToList()
        If RootPaths.Count = 0 Then Return

        ExecuteLongBlockingActionOnThread(
            Sub(Report)
                Report.SetText("Preparing upload...")
                Dim WorkItems = BuildUploadWorkList(RootPaths)
                If WorkItems.Count = 0 Then Return

                ' The copy starts as soon as the paths are walked; the total byte count - the part
                ' that is slow over a network share or a deep tree - is summed on a second thread and
                ' published when ready. The copy reports byte progress only once the total is known.
                Dim TotalSize As New TotalSizeBox()
                Using Cancellation As New CancellationTokenSource()
                    Dim Sizer = New Thread(
                        Sub()
                            Try
                                Dim Sum As Long = 0
                                For Each workItem In WorkItems
                                    Cancellation.Token.ThrowIfCancellationRequested()
                                    If workItem.IsDirectory = False Then Sum += New FileInfo(workItem.SourcePath).Length
                                Next
                                TotalSize.Publish(Sum)
                            Catch
                                ' Cancelled by a copy failure, or a source file vanished mid-scan;
                                ' the progress bar simply stays indeterminate.
                            End Try
                        End Sub) With {.IsBackground = True, .Name = "Upload size scan"}
                    Sizer.Start()

                    Try
                        CopyUploadWorkList(WorkItems, TargetDirectoryAnchorId, TotalSize, Report)
                    Finally
                        Cancellation.Cancel()
                        Sizer.Join()
                    End Try
                End Using
            End Sub,
            "One or more items could not be uploaded.")

        RefreshFileSystemView()
    End Sub

    ''' <summary>
    ''' Walks <paramref name="RootPaths"/> and returns every directory-create and file-copy step,
    ''' ordered so a directory always precedes its contents.
    ''' </summary>
    Private Shared Function BuildUploadWorkList(RootPaths As IEnumerable(Of String)) As List(Of UploadWorkItem)
        Dim Result As New List(Of UploadWorkItem)()
        For Each rootPath In RootPaths
            If File.Exists(rootPath) Then
                Result.Add(New UploadWorkItem(rootPath, String.Empty, Path.GetFileName(rootPath), False))
            ElseIf Directory.Exists(rootPath) Then
                AddDirectoryToWorkList(rootPath, String.Empty, New DirectoryInfo(rootPath).Name, Result)
            End If
        Next
        Return Result
    End Function

    Private Shared Sub AddDirectoryToWorkList(DiskPath As String, RelativeParent As String, Name As String,
                                              Result As List(Of UploadWorkItem))
        Result.Add(New UploadWorkItem(Nothing, RelativeParent, Name, True))
        Dim ChildRelativeParent = If(RelativeParent.Length = 0, Name, $"{RelativeParent}/{Name}")

        For Each filePath In Directory.EnumerateFiles(DiskPath)
            Result.Add(New UploadWorkItem(filePath, ChildRelativeParent, Path.GetFileName(filePath), False))
        Next
        For Each childPath In Directory.EnumerateDirectories(DiskPath)
            AddDirectoryToWorkList(childPath, ChildRelativeParent, New DirectoryInfo(childPath).Name, Result)
        Next
    End Sub

    Private Sub CopyUploadWorkList(WorkItems As List(Of UploadWorkItem), TargetDirectoryAnchorId As Long,
                                   TotalSize As TotalSizeBox, Report As i00CodeLib.frmProgress.ProgressReport)
        Dim FolderAnchors As New Dictionary(Of String, Long)() From {{String.Empty, TargetDirectoryAnchorId}}
        Dim Resolution As ConflictChoice = ConflictChoice.Cancel
        Dim Resolved As Boolean = False
        Dim CopiedBytes As Long = 0
        Dim FileCount = WorkItems.Where(Function(x) x.IsDirectory = False).Count()
        Dim CopiedFiles = 0
        Dim LastReport As Date = Date.MinValue

        For Each workItem In WorkItems
            If workItem.IsDirectory Then
                EnsureUploadFolder(workItem.RelativeParent, workItem.Name, TargetDirectoryAnchorId, FolderAnchors)
                Continue For
            End If

            Dim ParentAnchor = EnsureUploadFolderPath(workItem.RelativeParent, TargetDirectoryAnchorId, FolderAnchors)
            Dim Existing = _FileSystem.GetDirectoryEntries(ParentAnchor).
                                       FirstOrDefault(Function(x) String.Equals(x.Name, workItem.Name, StringComparison.OrdinalIgnoreCase))

            Dim Replace As Boolean
            If Existing Is Nothing Then
                Replace = False
            ElseIf IsDirectory(Existing) Then
                Throw New IOException($"A folder named '{workItem.Name}' already exists where a file is being uploaded.")
            ElseIf Existing.EntryType = EmbeddedFileSystem.EntryTypes.PendingFile Then
                ' A half-written file from an interrupted run is always overwritten.
                Replace = True
            Else
                Dim Choice As ConflictChoice
                If Resolved Then
                    Choice = Resolution
                Else
                    Choice = PromptForConflictChoice(Report, workItem.Name)
                    If Choice = ConflictChoice.SkipAll OrElse Choice = ConflictChoice.ReplaceAll Then
                        Resolution = Choice
                        Resolved = True
                    End If
                End If

                Select Case Choice
                    Case ConflictChoice.Cancel
                        Throw New OperationCanceledException()
                    Case ConflictChoice.Skip, ConflictChoice.SkipAll
                        CopiedFiles += 1
                        CopiedBytes += SafeFileLength(workItem.SourcePath)
                        ReportUploadProgress(Report, TotalSize, CopiedBytes, CopiedFiles, FileCount, workItem.Name, LastReport, True)
                        Continue For
                    Case Else
                        Replace = True
                End Select
            End If

            If Replace Then _FileSystem.DeleteEntry(ParentAnchor, Existing.Name)

            Dim FileAnchorId = _FileSystem.CreateFile(ParentAnchor, workItem.Name)
            Try
                Using SourceStream = New FileStream(workItem.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Using DestinationStream = _FileSystem.OpenFile(FileAnchorId)
                        Dim Buffer(1024 * 1024 - 1) As Byte
                        While True
                            Dim BytesRead = SourceStream.Read(Buffer, 0, Buffer.Length)
                            If BytesRead = 0 Then Exit While
                            DestinationStream.Write(Buffer, 0, BytesRead)
                            CopiedBytes += BytesRead
                            ReportUploadProgress(Report, TotalSize, CopiedBytes, CopiedFiles, FileCount, workItem.Name, LastReport, False)
                        End While
                        DestinationStream.Flush()
                    End Using
                End Using
            Catch
                Try
                    _FileSystem.DeleteEntry(ParentAnchor, workItem.Name)
                Catch
                End Try
                Throw
            End Try

            CopiedFiles += 1
            ReportUploadProgress(Report, TotalSize, CopiedBytes, CopiedFiles, FileCount, workItem.Name, LastReport, True)
        Next
    End Sub

    ''' <summary>
    ''' Pushes the current byte/file counts to the progress dialog. Throttled to ~10 updates a second
    ''' unless <paramref name="Force"/> is set (file finished, or an item was skipped).
    ''' </summary>
    Private Shared Sub ReportUploadProgress(Report As i00CodeLib.frmProgress.ProgressReport, TotalSize As TotalSizeBox,
                                            CopiedBytes As Long, CopiedFiles As Integer, FileCount As Integer, Name As String,
                                            ByRef LastReport As Date, Force As Boolean)
        Dim Timestamp = Date.UtcNow
        If Force = False AndAlso Timestamp.Subtract(LastReport).TotalMilliseconds < 100 Then Return
        LastReport = Timestamp

        Dim Total = TotalSize.Value
        If Total.HasValue Then
            Report.SetText($"Uploading {Name} ({CopiedFiles:N0} of {FileCount:N0})...")
            Report.SetProgress(CopiedBytes, Math.Max(Total.Value, CopiedBytes))
        Else
            Report.SetText($"Uploading {Name} ({FormatByteLength(CopiedBytes)} copied)...")
        End If
    End Sub

    Private Shared Function SafeFileLength(FilePath As String) As Long
        Try
            Return New FileInfo(FilePath).Length
        Catch
            Return 0
        End Try
    End Function

    Private Function EnsureUploadFolderPath(RelativeParent As String, TargetDirectoryAnchorId As Long,
                                            FolderAnchors As Dictionary(Of String, Long)) As Long
        If RelativeParent.Length = 0 Then Return TargetDirectoryAnchorId

        Dim CachedAnchor As Long
        If FolderAnchors.TryGetValue(RelativeParent, CachedAnchor) Then Return CachedAnchor

        Dim SeparatorIndex = RelativeParent.LastIndexOf("/"c)
        Dim GrandParent = If(SeparatorIndex < 0, String.Empty, RelativeParent.Substring(0, SeparatorIndex))
        Dim Name = If(SeparatorIndex < 0, RelativeParent, RelativeParent.Substring(SeparatorIndex + 1))
        Return EnsureUploadFolder(GrandParent, Name, TargetDirectoryAnchorId, FolderAnchors)
    End Function

    Private Function EnsureUploadFolder(RelativeParent As String, Name As String, TargetDirectoryAnchorId As Long,
                                        FolderAnchors As Dictionary(Of String, Long)) As Long
        Dim ParentAnchor = EnsureUploadFolderPath(RelativeParent, TargetDirectoryAnchorId, FolderAnchors)
        Dim Key = If(RelativeParent.Length = 0, Name, $"{RelativeParent}/{Name}")

        Dim CachedAnchor As Long
        If FolderAnchors.TryGetValue(Key, CachedAnchor) Then Return CachedAnchor

        Dim Existing = _FileSystem.GetDirectoryEntries(ParentAnchor).
                                   FirstOrDefault(Function(x) String.Equals(x.Name, Name, StringComparison.OrdinalIgnoreCase))
        Dim Anchor As Long
        If Existing Is Nothing Then
            Anchor = _FileSystem.CreateDirectory(ParentAnchor, Name)
        ElseIf IsDirectory(Existing) Then
            ' An upload merges into a folder that already exists.
            Anchor = Existing.ChildAnchorId
        Else
            Throw New IOException($"A file named '{Name}' already exists where a folder is being uploaded.")
        End If

        FolderAnchors(Key) = Anchor
        Return Anchor
    End Function

    Private Shared Function PromptForConflictChoice(Report As i00CodeLib.frmProgress.ProgressReport, Name As String) As ConflictChoice
        Dim Choice As ConflictChoice = ConflictChoice.Cancel
        Dim Buttons As New List(Of i00CodeLib.MessageBox.MsgBoxButton) From {
            New i00CodeLib.MessageBox.MsgBoxButton("Skip", Sub() Choice = ConflictChoice.Skip),
            New i00CodeLib.MessageBox.MsgBoxButton("Skip All", Sub() Choice = ConflictChoice.SkipAll),
            New i00CodeLib.MessageBox.MsgBoxButton("Replace", Sub() Choice = ConflictChoice.Replace),
            New i00CodeLib.MessageBox.MsgBoxButton("Replace All", Sub() Choice = ConflictChoice.ReplaceAll),
            New i00CodeLib.MessageBox.MsgBoxButton("Cancel", Sub() Choice = ConflictChoice.Cancel)
        }

        Report.ShowMessageBox($"'{Name}' already exists in the destination folder.{Environment.NewLine}What would you like to do?",
                              MsgBoxStyle.Exclamation, "Replace File", Buttons)
        Return Choice
    End Function

    Private Sub SaveSelectedEntries(Sender As Object, EventArgs As EventArgs)
        Dim Entries = GetSelectedEntries()
        If Entries.Count = 0 Then Return

        If Entries.Count = 1 AndAlso IsDirectory(Entries(0)) = False Then
            Using Dialog As New SaveFileDialog With {
                .Title = "Save file",
                .FileName = Entries(0).Name,
                .Filter = "All files (*.*)|*.*",
                .OverwritePrompt = True
            }
                If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                ExecuteLongBlockingActionOnThread(Sub() ExportFile(Entries(0), Dialog.FileName),
                                                  "The file could not be saved.")
            End Using
            Return
        End If

        Dim Description = If(Entries.Count = 1, "Select where to save the folder", "Select a destination for the selected items")
        Using Dialog As New FolderBrowserDialog With {.Description = Description}
            If Dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim DestinationRoot = Dialog.SelectedPath
            ExecuteLongBlockingActionOnThread(
                Sub()
                    For Each entry In Entries
                        Dim OutputPath = Path.Combine(DestinationRoot, MakeSafeFileName(entry.Name))
                        If IsDirectory(entry) Then
                            ExportDirectory(entry.ChildAnchorId, OutputPath)
                        Else
                            ExportFile(entry, OutputPath)
                        End If
                    Next
                End Sub,
                "One or more items could not be saved.")
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
            ExecuteLongBlockingActionOnThread(
                Sub()
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

    Private Sub DeleteSelectedEntries(Sender As Object, EventArgs As EventArgs)
        Dim Entries = GetSelectedEntries()
        If Entries.Count = 0 Then Return

        Dim ContainsFolder = Entries.Any(AddressOf IsDirectory)
        Dim Prompt As String
        If Entries.Count = 1 Then
            Prompt = If(IsDirectory(Entries(0)),
                        $"Delete '{Entries(0).Name}' and all of its contents?",
                        $"Delete '{Entries(0).Name}'?")
        Else
            Prompt = If(ContainsFolder,
                        $"Delete the {Entries.Count:N0} selected items, including all folder contents?",
                        $"Delete the {Entries.Count:N0} selected files?")
        End If
        If i00CodeLib.MsgBox(Me, Prompt, MsgBoxStyle.YesNo Or MsgBoxStyle.Exclamation) <> MsgBoxResult.Yes Then Return

        Dim DirectoryAnchorId = _CurrentDirectoryAnchorId
        ExecuteLongBlockingActionOnThread(
            Sub()
                For Each entry In Entries
                    _FileSystem.DeleteEntry(DirectoryAnchorId, entry.Name)
                Next
            End Sub,
            "One or more items could not be deleted.")

        RefreshFileSystemView()
    End Sub

    Private Sub DeleteSelectedFolder(Sender As Object, EventArgs As EventArgs)
        Dim SelectedNode = tvFolders.SelectedNode
        If SelectedNode Is Nothing OrElse SelectedNode.Parent Is Nothing Then Return

        Dim Info = TryCast(SelectedNode.Tag, DirectoryNodeInfo)
        Dim ParentInfo = TryCast(SelectedNode.Parent.Tag, DirectoryNodeInfo)
        If Info Is Nothing OrElse ParentInfo Is Nothing Then Return

        If MsgBox(Me,
                  $"Delete '{Info.Name}' and all of its contents?",
                  MsgBoxStyle.YesNo Or MsgBoxStyle.Exclamation
                  ) <> MsgBoxResult.Yes Then Return

        ExecuteLongBlockingActionOnThread(
            Sub()
                _FileSystem.DeleteEntry(ParentInfo.AnchorId, Info.Name)
                _CurrentDirectoryAnchorId = ParentInfo.AnchorId
            End Sub,
            "The folder could not be deleted.")

        RefreshFileSystemView()
    End Sub

    Private Sub RefreshMenuItem_Click(Sender As Object, EventArgs As EventArgs)
        RefreshFileSystemView()
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
        QueueDroppedPathUpload(EventArgs.Data, _CurrentDirectoryAnchorId)
    End Sub

    Private Sub tvFolders_DragDrop(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragDrop
        Dim ClientPoint = tvFolders.PointToClient(New Point(EventArgs.X, EventArgs.Y))
        Dim TargetNode = tvFolders.GetNodeAt(ClientPoint)
        If TargetNode Is Nothing Then Return
        Dim Info = TryCast(TargetNode.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return
        QueueDroppedPathUpload(EventArgs.Data, Info.AnchorId)
    End Sub

    ''' <summary>
    ''' Reads the dropped paths and schedules the upload to run once this handler has returned, so the
    ''' drop finishes immediately and the source window (Explorer) is never held while files copy.
    ''' </summary>
    Private Sub QueueDroppedPathUpload(Data As IDataObject, TargetDirectoryAnchorId As Long)
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return
        Dim Paths = TryCast(Data.GetData(DataFormats.FileDrop), String())
        If Paths Is Nothing OrElse Paths.Length = 0 Then Return

        Dim DroppedPaths = DirectCast(Paths.Clone(), String())
        BeginInvoke(Sub() UploadPaths(DroppedPaths, TargetDirectoryAnchorId))
    End Sub

    Private Sub lvFiles_MouseDown(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseDown
        _ListDragStart = EventArgs.Location
        _ListDragArmed = EventArgs.Button = MouseButtons.Left AndAlso lvFiles.SelectedItems.Count > 0
    End Sub

    Private Sub lvFiles_MouseMove(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseMove
        If _ListDragArmed = False OrElse EventArgs.Button <> MouseButtons.Left Then Return
        If IsDragThresholdExceeded(_ListDragStart, EventArgs.Location) = False Then Return
        _ListDragArmed = False

        Dim Entries = GetSelectedEntries()
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
        ExecuteLongBlockingActionOnThread(
            Sub()
                Using Export = CreateTemporaryFileExport(Entries)
                    Dim Data = New DataObject(DataFormats.FileDrop, Export.Paths)
                    lvFiles.DoDragDrop(Data, DragDropEffects.Copy)
                End Using
            End Sub,
            "The selected files could not be prepared for drag-and-drop.")
    End Sub

    Private Sub BeginExternalDirectoryDrag(Info As DirectoryNodeInfo)
        ExecuteLongBlockingActionOnThread(
            Sub()
                Using Export = CreateTemporaryDirectoryExport(Info)
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
                Dim OutputPath = GetUniquePath(TemporaryDirectory, MakeSafeFileName(entry.Name), IsDirectory(entry))
                If IsDirectory(entry) Then
                    ExportDirectory(entry.ChildAnchorId, OutputPath)
                Else
                    ExportFile(entry, OutputPath)
                End If
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

        If EventArgs.KeyCode = Keys.Delete Then
            If lvFiles.Focused AndAlso lvFiles.SelectedItems.Count > 0 Then
                DeleteSelectedEntries(Me, EventArgs)
                EventArgs.Handled = True
            ElseIf tvFolders.Focused AndAlso tvFolders.SelectedNode IsNot Nothing AndAlso tvFolders.SelectedNode.Parent IsNot Nothing Then
                DeleteSelectedFolder(Me, EventArgs)
                EventArgs.Handled = True
            End If
        End If
    End Sub

    Private Sub ExecuteLongBlockingActionOnThread(Operation As Action, ErrorMessage As String)
        ExecuteLongBlockingActionOnThread(Sub(Report) Operation(), ErrorMessage)
    End Sub

    Private Sub ExecuteLongBlockingActionOnThread(Operation As Action(Of i00CodeLib.frmProgress.ProgressReport), ErrorMessage As String)
        Using ProgressForm As New i00CodeLib.frmProgress(
                Sub(Parameter, ProgressReport)
                    Try
                        Operation(ProgressReport)
                    Catch ex As OperationCanceledException
                        ' The user cancelled at a prompt; nothing to report.
                    Catch ex As Exception When ex.getThreadAbortException Is Nothing
                        ProgressReport.ShowMessageBox(
                            $"{ErrorMessage}{Environment.NewLine}{ex.GetType.Name}:{Environment.NewLine}{ex.Message}",
                            MsgBoxStyle.Critical)
                    End Try
                End Sub, Nothing)
            ProgressForm.ShowDialog(Me)
        End Using
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

        ' Stops any executable-icon pass and waits for it to release the file it may be reading,
        ' so the caller can dispose the file system without hitting an open-stream guard.
        _Disposed = True
        _IconGeneration += 1
        _ExecutableIconThread?.Join(TimeSpan.FromSeconds(5))

        If Disposing Then
            If components IsNot Nothing Then components.Dispose()
            _IconProvider.Dispose()
        End If

        MyBase.Dispose(Disposing)
    End Sub

    Private Sub tsiFragmentation_Paint(sender As Object, e As PaintEventArgs) Handles tsiFragmentation.Paint
        Dim Struct = FileSystem.ChunkedStream.GetStructure()
        Struct.DrawFragmentation(e.Graphics, tsiFragmentation.ContentRectangle,
                                 New FragmentationDrawOptions() With {
                                    .MaxXBlockCount = 100,
                                    .MaxYBlockCount = 1
                                 })
        'Struct.DrawFragmentation(e.Graphics, New Rectangle(0, 0, tsiFragmentation.ContentRectangle.Width, 1))
    End Sub

    Private Sub tsiExit_Click(sender As Object, e As EventArgs) Handles tsiExit.Click
        Me.Close()
    End Sub

    Private Sub tsiDefrag_Click(sender As Object, e As EventArgs) Handles tsiDefrag.Click
        Defrag()
    End Sub

    Private Sub tsiScan_Click(sender As Object, e As EventArgs) Handles tsiScan.Click
        Using frmProgress As New i00CodeLib.frmProgress(
                Sub(Parameter, ProgressReport)
                    ProgressReport.SetText("Scanning Files...")

                    Dim Removed = FileSystem.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Remove)

                    ProgressReport.SetText("Validating...")

                    Dim ValidationException As Exception = Nothing
                    Try
                        FileSystem.ChunkedStream.Validate(
                            Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                                Dim Progress = If(TotalUnits = 0, 1.0R, ProcessedUnits / CDbl(TotalUnits))
                                ProgressReport.SetText($"Validating ({Progress:P0})...")
                            End Sub)
                    Catch ex As Exception
                        ValidationException = ex
                    End Try
                    Dim Icon = If(ValidationException Is Nothing, MsgBoxStyle.Information, MsgBoxStyle.Critical)
                    i00CodeLib.MsgBox(ProgressReport.frmProgress, $"Partial Files Removed: {Removed}{Environment.NewLine}Validation: {If(ValidationException Is Nothing, "Pass", $"{ValidationException.GetType.Name}: {ValidationException.Message}")}", Icon)
                End Sub, Nothing)

            frmProgress.ShowInTaskbar = True
            frmProgress.ShowDialog(Me)
            frmProgress.Text = "Scanning"
        End Using

        RefreshFileSystemView()

    End Sub

    Private Sub tsiFragmentation_Click(sender As Object, e As EventArgs) Handles tsiFragmentation.Click
        Defrag()
    End Sub

    Private Sub Defrag()
        Using frmProgress As New i00CodeLib.frmProgress(
                Sub(Parameter, ProgressReport)
                    ProgressReport.SetText("Defragmenting...")

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

                    Dim OldFragmentation = FileSystem.ChunkedStream.GetFragmentation()

                    Dim LastUpdate As Date
                    Dim Saved = FileSystem.ChunkedStream.Defragment(ChunkedStream.DefragTypes.Move,
                        Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                            Dim Progress = If(TotalUnits = 0, 1.0R, ProcessedUnits / CDbl(TotalUnits))
                            ProgressReport.SetText($"Defragmenting ({Progress:P0})...")
                            ProgressReport.SetProgress(CLng(Progress * 100), 100)

                            Dim CurrentTime = Now()
                            Dim Done = ProcessedUnits = TotalUnits
                            If CurrentTime.Subtract(LastUpdate).TotalMilliseconds >= 250 OrElse Done Then
                                Dim S = FileSystem.ChunkedStream.GetStructure()
                                LastUpdate = CurrentTime
                                pnlDefrag.BackgroundImage = S.GenerateFragmentationBitmap(pnlDefrag.ClientSize.Width, 1)
                            End If
                        End Sub)

                    'Dim Struct = FileSystem.ChunkedStream.GetStructure()
                    'For Each rr In Struct.Regions
                    '    Debug.Print($"{rr}")
                    'Next
                    'Dim Hc = Struct.Regions.Where(Function(x) x.RegionType = ChunkedStreamStructure.RegionTypes.Hole).Count

                    i00CodeLib.MsgBox(ProgressReport.frmProgress, $"Saved: {Saved.FormatFileSizeFromBytes}{Environment.NewLine}Fragmentation: {OldFragmentation:P0} > {FileSystem.ChunkedStream.GetFragmentation():P0}", MsgBoxStyle.Information)
                End Sub, Nothing)

            frmProgress.ShowInTaskbar = True
            frmProgress.ShowDialog(Me)
            frmProgress.Text = "Defragmenting"
        End Using

        RefreshFileSystemView()
    End Sub
End Class
