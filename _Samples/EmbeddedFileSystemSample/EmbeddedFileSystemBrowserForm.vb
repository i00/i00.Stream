Imports i00.Streams
Imports System.ComponentModel
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Threading
Imports i00CodeLib
Imports System.Runtime.InteropServices

Partial Public NotInheritable Class EmbeddedFileSystemBrowserForm

    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SetWindowTheme(hWnd As IntPtr, pszSubAppName As String, pszSubIdList As String) As Integer
    End Function

    <DllImport("user32.dll")>
    Private Shared Function SendMessage(Handle As IntPtr, Message As Integer, WParam As IntPtr, LParam As IntPtr) As IntPtr
    End Function

    Private Const LvmSetExtendedListViewStyle As Integer = &H1000 + 54
    Private Const LvsExDoubleBuffer As Integer = &H10000

    ''' <summary>Extensions previewed as thumbnails in the thumbnail views (all GDI+-decodable).</summary>
    Private Shared ReadOnly ThumbnailImageExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico"
    }
    Private Const MaximumThumbnailSourceBytes As Long = 40L * 1024L * 1024L
    Private Const ThumbnailRenderSize As Integer = 256

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
    Private ReadOnly _ThumbnailCache As New Dictionary(Of Long, Bitmap)()
    Private ReadOnly _ThumbnailPending As New HashSet(Of Long)()
    Private ReadOnly _ThumbnailUnavailable As New HashSet(Of Long)()
    Private _CurrentDirectoryAnchorId As Long
    Private _ListDragStart As Point
    Private _TreeDragStart As Point
    Private _ListDragArmed As Boolean
    Private _TreeDragArmed As Boolean
    Private _Disposed As Boolean
    Private _ThumbnailGeneration As Integer
    Private _ThumbnailCacheAnchorId As Long
    Private _ThumbnailCellSize As Integer


    Public Sub New(FileSystem As EmbeddedFileSystem)
        If FileSystem Is Nothing Then Throw New ArgumentNullException(NameOf(FileSystem))

        _FileSystem = FileSystem
        _CurrentDirectoryAnchorId = _FileSystem.RootAnchorId

        InitializeComponent()

        tvFolders.ImageList = _IconProvider.TreeImages
        lvFiles.ListViewItemSorter = _ListSorter

        Try
            SetWindowTheme(tvFolders.Handle, "Explorer", Nothing)
            SetWindowTheme(lvFiles.Handle, "Explorer", Nothing)
            SendMessage(lvFiles.Handle, LvmSetExtendedListViewStyle, New IntPtr(LvsExDoubleBuffer), New IntPtr(LvsExDoubleBuffer))
        Catch ex As Exception

        End Try

        RefreshFileSystemView()
        ApplySavedFileListView()

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

        ' Thumbnails stay resident while the same folder is shown (including plain refreshes) and are
        ' released only when a different folder is opened.
        If _ThumbnailCacheAnchorId <> _CurrentDirectoryAnchorId Then
            ClearThumbnailCache()
            _ThumbnailCacheAnchorId = _CurrentDirectoryAnchorId
        End If

        Dim Entries = _FileSystem.GetDirectoryEntries(_CurrentDirectoryAnchorId).
                                  OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                                  ToList()

        lvFiles.BeginUpdate()
        Try
            lvFiles.ListViewItemSorter = Nothing
            lvFiles.Items.Clear()

            For Each entry In Entries
                Dim IsDirectory = entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
                Dim Item = New ListViewItem(entry.Name) With {.Tag = entry}
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
    End Sub

    ''' <summary>
    ''' Returns the icon bitmap for an entry at <paramref name="Size"/> px: the executable's own icon
    ''' when it has been loaded (queuing a background load if not), otherwise the shell icon for its
    ''' extension. Every result is a cached bitmap, so this is fine to call from the paint path.
    ''' </summary>
    Private Function GetEntryIcon(Entry As EmbeddedFileSystem.ContentListEntry, Size As Integer) As Bitmap
        If Entry Is Nothing Then Return Nothing

        If Entry.EntryType <> EmbeddedFileSystem.EntryTypes.Directory AndAlso
           Entry.LengthOfDataAtEntry > 0 AndAlso
           String.Equals(Path.GetExtension(Entry.Name), ".exe", StringComparison.OrdinalIgnoreCase) Then
            Dim ExecutableIcon = _IconProvider.TryGetExecutableIcon(Entry.ChildAnchorId, Size)
            If ExecutableIcon IsNot Nothing Then Return ExecutableIcon
            RequestExecutableIcon(Entry)
        End If

        Dim IsDirectory = Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
        Return _IconProvider.GetShellIcon(FileIconProvider.KeyForEntry(Entry.Name, IsDirectory), Size)
    End Function

    ''' <summary>
    ''' Queues a background read of an executable's own icon (rendered to bitmaps at every list size).
    ''' The read goes through <see cref="EmbeddedFileReadStream"/>, so the entry is not flipped to
    ''' PendingFile, and the result is cached by anchor ID for the session.
    ''' </summary>
    Private Sub RequestExecutableIcon(Entry As EmbeddedFileSystem.ContentListEntry)
        If _IconProvider.BeginExecutableLoad(Entry.ChildAnchorId) = False Then Return
        Dim RequestedEntry = Entry
        System.Threading.ThreadPool.QueueUserWorkItem(Sub() LoadExecutableIcon(RequestedEntry))
    End Sub

    Private Sub LoadExecutableIcon(Entry As EmbeddedFileSystem.ContentListEntry)
        Dim Result As Dictionary(Of Integer, Bitmap) = Nothing
        Try
            If _Disposed = False Then
                Using Source = EmbeddedFileReadStream.TryOpen(_FileSystem, Entry)
                    If Source IsNot Nothing Then
                        Result = FileIconProvider.ExtractExecutableIconBitmaps(
                            Source, Source.Length,
                            {FileIconProvider.SmallIconSize, FileIconProvider.LargeIconSize, FileIconProvider.JumboIconSize})
                    End If
                End Using
            End If
        Catch
            Result = Nothing
        End Try

        If _Disposed OrElse IsHandleCreated = False Then
            DisposeBitmaps(Result)
            Return
        End If

        Try
            BeginInvoke(
                Sub()
                    _IconProvider.CompleteExecutableLoad(Entry.ChildAnchorId, Result)
                    If _Disposed = False Then InvalidateEntry(Entry.ChildAnchorId)
                End Sub)
        Catch ex As InvalidOperationException
            ' The form closed between the guard above and the marshalled call.
            DisposeBitmaps(Result)
        End Try
    End Sub

    Private Sub InvalidateEntry(AnchorId As Long)
        For Each item As ListViewItem In lvFiles.Items
            Dim Entry = TryCast(item.Tag, EmbeddedFileSystem.ContentListEntry)
            If Entry IsNot Nothing AndAlso Entry.ChildAnchorId = AnchorId Then
                lvFiles.Invalidate(item.Bounds)
                Return
            End If
        Next
    End Sub

    Private Shared Sub DisposeBitmaps(Bitmaps As Dictionary(Of Integer, Bitmap))
        If Bitmaps Is Nothing Then Return
        For Each Bitmap In Bitmaps.Values
            Bitmap?.Dispose()
        Next
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
        AddViewOption(ViewMenu, "Extra Large Thumbnails", View.LargeIcon, 256)
        AddViewOption(ViewMenu, "Large Thumbnails", View.LargeIcon, 128)
        AddViewOption(ViewMenu, "Large Icons", View.LargeIcon, 0)
        AddViewOption(ViewMenu, "Small Icons", View.SmallIcon, 0)
        AddViewOption(ViewMenu, "List", View.List, 0)
        AddViewOption(ViewMenu, "Details", View.Details, 0)
        AddViewOption(ViewMenu, "Tiles", View.Tile, 0)
        Menu.Items.Add(ViewMenu)
    End Sub

    Private Sub AddViewOption(ViewMenu As ToolStripMenuItem, Text As String, TargetView As View, ThumbnailCellSize As Integer)
        Dim IsCurrent = _ThumbnailCellSize = ThumbnailCellSize AndAlso
                        (ThumbnailCellSize <> 0 OrElse lvFiles.View = TargetView)
        Dim Item = New ToolStripMenuItem(Text) With {.Checked = IsCurrent}
        AddHandler Item.Click, Sub() SelectFileListView(TargetView, ThumbnailCellSize)
        ViewMenu.DropDownItems.Add(Item)
    End Sub

    ''' <summary>Applies a file-list view and remembers it (view mode plus thumbnail cell size).</summary>
    Private Sub SelectFileListView(TargetView As View, ThumbnailCellSize As Integer)
        SetFileListView(TargetView, ThumbnailCellSize)
        Try
            My.Settings.FileListView = SerializeFileListView(TargetView, ThumbnailCellSize)
            My.Settings.Save()
        Catch
            ' A settings write failure must not break the view switch.
        End Try
    End Sub

    Private Sub ApplySavedFileListView()
        Dim Saved As String = Nothing
        Try
            Saved = My.Settings.FileListView
        Catch
        End Try

        Select Case Saved
            Case "Thumbnail256" : SetFileListView(View.LargeIcon, 256)
            Case "Thumbnail128" : SetFileListView(View.LargeIcon, 128)
            Case "LargeIcon" : SetFileListView(View.LargeIcon, 0)
            Case "SmallIcon" : SetFileListView(View.SmallIcon, 0)
            Case "List" : SetFileListView(View.List, 0)
            Case "Tile" : SetFileListView(View.Tile, 0)
            Case Else : SetFileListView(View.Details, 0)
        End Select
    End Sub

    Private Shared Function SerializeFileListView(TargetView As View, ThumbnailCellSize As Integer) As String
        If ThumbnailCellSize <> 0 Then Return $"Thumbnail{ThumbnailCellSize}"
        Return TargetView.ToString()
    End Function

    ''' <summary>
    ''' Switches the file list to <paramref name="TargetView"/>. A non-zero <paramref name="ThumbnailCellSize"/>
    ''' selects the image-preview thumbnail view (128 or 256 px cells) laid out as large icons. The list is
    ''' always owner-drawn; the empty image lists only fix the cell geometry per view.
    ''' </summary>
    Private Sub SetFileListView(TargetView As View, ThumbnailCellSize As Integer)
        _ThumbnailCellSize = ThumbnailCellSize
        lvFiles.BeginUpdate()
        Try
            If ThumbnailCellSize >= 256 Then
                lvFiles.LargeImageList = _Thumbnail256Sizer
            ElseIf ThumbnailCellSize > 0 Then
                lvFiles.LargeImageList = _Thumbnail128Sizer
            Else
                lvFiles.LargeImageList = _Large32Sizer
            End If
            lvFiles.View = If(ThumbnailCellSize > 0, View.LargeIcon, TargetView)
        Finally
            lvFiles.EndUpdate()
        End Try
        lvFiles.Invalidate()
    End Sub

    ''' <summary>The icon edge, in pixels, for the current view.</summary>
    Private Function CurrentIconSize() As Integer
        If _ThumbnailCellSize <> 0 Then Return FileIconProvider.JumboIconSize
        Select Case lvFiles.View
            Case View.LargeIcon, View.Tile
                Return FileIconProvider.LargeIconSize
            Case Else
                Return FileIconProvider.SmallIconSize
        End Select
    End Function

    Private Sub lvFiles_DrawColumnHeader(Sender As Object, EventArgs As DrawListViewColumnHeaderEventArgs) Handles lvFiles.DrawColumnHeader
        EventArgs.DrawDefault = True
    End Sub

    Private Sub lvFiles_DrawItem(Sender As Object, EventArgs As DrawListViewItemEventArgs) Handles lvFiles.DrawItem
        ' In Details view every column is painted by DrawSubItem instead.
        If lvFiles.View = View.Details Then Return

        If _ThumbnailCellSize <> 0 Then
            DrawThumbnailItem(EventArgs)
        Else
            DrawIconViewItem(EventArgs)
        End If
    End Sub

    Private Sub lvFiles_DrawSubItem(Sender As Object, EventArgs As DrawListViewSubItemEventArgs) Handles lvFiles.DrawSubItem
        If EventArgs.SubItem Is Nothing Then
            EventArgs.DrawDefault = True
            Return
        End If

        Dim Canvas = EventArgs.Graphics
        Dim Bounds = EventArgs.Bounds
        Dim Selected = EventArgs.Item.Selected

        Using Background As New SolidBrush(If(Selected, SystemColors.Highlight, lvFiles.BackColor))
            Canvas.FillRectangle(Background, Bounds)
        End Using

        Dim ForeColour = If(Selected, SystemColors.HighlightText, EventArgs.Item.ForeColor)
        Dim TextBounds = Bounds
        If EventArgs.ColumnIndex = 0 Then
            Dim Entry = TryCast(EventArgs.Item.Tag, EmbeddedFileSystem.ContentListEntry)
            Dim IconRectangle = New Rectangle(Bounds.X + 2, Bounds.Y + ((Bounds.Height - FileIconProvider.SmallIconSize) \ 2),
                                              FileIconProvider.SmallIconSize, FileIconProvider.SmallIconSize)
            DrawEntryIcon(Canvas, Entry, IconRectangle)
            TextBounds = Rectangle.FromLTRB(IconRectangle.Right + 4, Bounds.Top, Bounds.Right, Bounds.Bottom)
        End If

        Dim Flags = TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix
        If EventArgs.Header IsNot Nothing AndAlso EventArgs.Header.TextAlign = HorizontalAlignment.Right Then
            Flags = Flags Or TextFormatFlags.Right
        ElseIf EventArgs.Header IsNot Nothing AndAlso EventArgs.Header.TextAlign = HorizontalAlignment.Center Then
            Flags = Flags Or TextFormatFlags.HorizontalCenter
        End If
        TextRenderer.DrawText(Canvas, EventArgs.SubItem.Text, lvFiles.Font, TextBounds, ForeColour, Flags)
    End Sub

    Private Sub DrawIconViewItem(EventArgs As DrawListViewItemEventArgs)
        Dim Canvas = EventArgs.Graphics
        Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
        EventArgs.DrawBackground()

        Dim Bounds = EventArgs.Bounds
        Dim Entry = TryCast(EventArgs.Item.Tag, EmbeddedFileSystem.ContentListEntry)
        Dim IconSize = CurrentIconSize()

        If lvFiles.View = View.LargeIcon Then
            Dim IconRectangle = New Rectangle(Bounds.X + ((Bounds.Width - IconSize) \ 2), Bounds.Y + 2, IconSize, IconSize)
            DrawEntryIcon(Canvas, Entry, IconRectangle)
            Dim LabelArea = Rectangle.FromLTRB(Bounds.Left, IconRectangle.Bottom + 2, Bounds.Right, Bounds.Bottom)
            DrawIconViewLabel(Canvas, EventArgs.Item, LabelArea,
                              TextFormatFlags.HorizontalCenter Or TextFormatFlags.WordEllipsis Or TextFormatFlags.NoPrefix)
        Else
            Dim IconRectangle = New Rectangle(Bounds.X + 1, Bounds.Y + ((Bounds.Height - IconSize) \ 2), IconSize, IconSize)
            DrawEntryIcon(Canvas, Entry, IconRectangle)
            Dim LabelArea = Rectangle.FromLTRB(IconRectangle.Right + 3, Bounds.Top, Bounds.Right - 2, Bounds.Bottom)
            DrawIconViewLabel(Canvas, EventArgs.Item, LabelArea,
                              TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix)
        End If

        If EventArgs.Item.Focused Then EventArgs.DrawFocusRectangle()
    End Sub

    Private Sub DrawIconViewLabel(Canvas As Graphics, Item As ListViewItem, LabelArea As Rectangle, Flags As TextFormatFlags)
        Dim Selected = Item.Selected
        Dim ForeColour = If(Selected, SystemColors.HighlightText, Item.ForeColor)

        If Selected Then
            Dim TextSize = TextRenderer.MeasureText(Canvas, Item.Text, lvFiles.Font, LabelArea.Size, Flags)
            Dim HighlightWidth = Math.Min(TextSize.Width + 4, LabelArea.Width)
            Dim HighlightHeight = Math.Min(TextSize.Height + 1, LabelArea.Height)
            Dim HighlightLeft = If((Flags And TextFormatFlags.HorizontalCenter) <> 0,
                                   LabelArea.X + ((LabelArea.Width - HighlightWidth) \ 2), LabelArea.X)
            Using Fill As New SolidBrush(SystemColors.Highlight)
                Canvas.FillRectangle(Fill, HighlightLeft, LabelArea.Y, HighlightWidth, HighlightHeight)
            End Using
        End If

        TextRenderer.DrawText(Canvas, Item.Text, lvFiles.Font, LabelArea, ForeColour, Flags)
    End Sub

    Private Sub DrawEntryIcon(Canvas As Graphics, Entry As EmbeddedFileSystem.ContentListEntry, Destination As Rectangle)
        If Entry Is Nothing Then Return
        Dim Icon = GetEntryIcon(Entry, Destination.Width)
        If Icon Is Nothing Then Return
        Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
        Canvas.DrawImage(Icon, Destination)
    End Sub

    Private Sub DrawThumbnailItem(EventArgs As DrawListViewItemEventArgs)
        Dim Canvas = EventArgs.Graphics
        Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
        Canvas.PixelOffsetMode = PixelOffsetMode.HighQuality
        EventArgs.DrawBackground()

        Dim Bounds = EventArgs.Bounds
        If EventArgs.Item.Selected Then
            Using Fill As New SolidBrush(Color.FromArgb(48, SystemColors.Highlight))
                Canvas.FillRectangle(Fill, Bounds)
            End Using
            Using Border As New Pen(SystemColors.Highlight)
                Canvas.DrawRectangle(Border, Bounds.X, Bounds.Y, Bounds.Width - 1, Bounds.Height - 1)
            End Using
        End If

        Dim CellSize = _ThumbnailCellSize
        Dim IconArea = New Rectangle(Bounds.X, Bounds.Y + 3, Bounds.Width, CellSize)
        Dim Entry = TryCast(EventArgs.Item.Tag, EmbeddedFileSystem.ContentListEntry)
        Dim Thumbnail = If(Entry IsNot Nothing AndAlso IsThumbnailableEntry(Entry), GetOrRequestThumbnail(Entry), Nothing)

        If Thumbnail IsNot Nothing Then
            Dim Target = FitCentered(Thumbnail.Size, IconArea, CellSize)
            Canvas.DrawImage(Thumbnail, Target)
            Using Border As New Pen(Color.FromArgb(128, Color.Gray))
                Canvas.DrawRectangle(Border, Target.X - 1, Target.Y - 1, Target.Width + 1, Target.Height + 1)
            End Using
            DrawTypeBadge(Canvas, Entry, Target)
        Else
            Dim TypeIcon = GetEntryIcon(Entry, FileIconProvider.JumboIconSize)
            If TypeIcon IsNot Nothing Then Canvas.DrawImage(TypeIcon, FitCentered(TypeIcon.Size, IconArea, CInt(CellSize * 0.82)))
        End If

        Dim LabelArea = New Rectangle(Bounds.X + 2, IconArea.Bottom + 2, Bounds.Width - 4, Bounds.Bottom - IconArea.Bottom - 4)
        TextRenderer.DrawText(Canvas, EventArgs.Item.Text, lvFiles.Font, LabelArea, lvFiles.ForeColor,
                              TextFormatFlags.HorizontalCenter Or TextFormatFlags.WordEllipsis Or TextFormatFlags.NoPrefix)

        If EventArgs.Item.Focused Then EventArgs.DrawFocusRectangle()
    End Sub

    Private Shared Function FitCentered(ImageSize As Size, Area As Rectangle, MaximumEdge As Integer) As Rectangle
        Dim Scale = Math.Min(MaximumEdge / CDbl(ImageSize.Width), MaximumEdge / CDbl(ImageSize.Height))
        If Scale > 1 Then Scale = 1
        Dim Width = Math.Max(1, CInt(Math.Round(ImageSize.Width * Scale)))
        Dim Height = Math.Max(1, CInt(Math.Round(ImageSize.Height * Scale)))
        Return New Rectangle(Area.X + ((Area.Width - Width) \ 2), Area.Y + ((Area.Height - Height) \ 2), Width, Height)
    End Function

    Private Sub DrawTypeBadge(Canvas As Graphics, Entry As EmbeddedFileSystem.ContentListEntry, ThumbnailRectangle As Rectangle)
        Dim Badge = GetEntryIcon(Entry, FileIconProvider.SmallIconSize)
        If Badge Is Nothing Then Return

        Dim X = ThumbnailRectangle.Right - Badge.Width
        Dim Y = ThumbnailRectangle.Bottom - Badge.Height
        Using Backing As New SolidBrush(Color.FromArgb(210, Color.White))
            Canvas.FillRectangle(Backing, X - 1, Y - 1, Badge.Width + 2, Badge.Height + 2)
        End Using
        Canvas.DrawImage(Badge, X, Y, Badge.Width, Badge.Height)
    End Sub

    Private Shared Function IsThumbnailableEntry(Entry As EmbeddedFileSystem.ContentListEntry) As Boolean
        Return Entry.EntryType <> EmbeddedFileSystem.EntryTypes.Directory AndAlso
               Entry.LengthOfDataAtEntry > 0 AndAlso
               Entry.LengthOfDataAtEntry <= MaximumThumbnailSourceBytes AndAlso
               ThumbnailImageExtensions.Contains(Path.GetExtension(Entry.Name))
    End Function

    ''' <summary>
    ''' Returns the cached thumbnail for <paramref name="Entry"/>, or Nothing while one is generated on a
    ''' background thread. Called from the paint path, so the item is generated the first time it scrolls
    ''' into view and stays cached until the folder changes.
    ''' </summary>
    Private Function GetOrRequestThumbnail(Entry As EmbeddedFileSystem.ContentListEntry) As Bitmap
        Dim AnchorId = Entry.ChildAnchorId
        Dim Cached As Bitmap = Nothing
        If _ThumbnailCache.TryGetValue(AnchorId, Cached) Then Return Cached
        If _ThumbnailUnavailable.Contains(AnchorId) OrElse _ThumbnailPending.Contains(AnchorId) Then Return Nothing

        _ThumbnailPending.Add(AnchorId)
        Dim Generation = _ThumbnailGeneration
        Dim RequestedEntry = Entry
        System.Threading.ThreadPool.QueueUserWorkItem(Sub() GenerateThumbnail(Generation, RequestedEntry))
        Return Nothing
    End Function

    Private Sub GenerateThumbnail(Generation As Integer, Entry As EmbeddedFileSystem.ContentListEntry)
        Dim Result As Bitmap = Nothing
        Try
            If Generation = _ThumbnailGeneration AndAlso _Disposed = False Then
                Dim Bytes = ReadEntryBytes(Entry)
                If Bytes IsNot Nothing Then
                    Using SourceStream As New MemoryStream(Bytes, False)
                        Using Original = Image.FromStream(SourceStream)
                            Result = ScaleImageToBox(Original, ThumbnailRenderSize)
                        End Using
                    End Using
                End If
            End If
        Catch
            Result?.Dispose()
            Result = Nothing
        End Try

        If _Disposed OrElse IsHandleCreated = False Then
            Result?.Dispose()
            Return
        End If

        Try
            BeginInvoke(Sub() CompleteThumbnail(Generation, Entry.ChildAnchorId, Result))
        Catch ex As InvalidOperationException
            Result?.Dispose()
        End Try
    End Sub

    Private Function ReadEntryBytes(Entry As EmbeddedFileSystem.ContentListEntry) As Byte()
        Using Source = EmbeddedFileReadStream.TryOpen(_FileSystem, Entry)
            If Source Is Nothing OrElse Source.Length <= 0 OrElse Source.Length > MaximumThumbnailSourceBytes Then Return Nothing

            Dim Bytes = New Byte(CInt(Source.Length) - 1) {}
            Dim Total = 0
            While Total < Bytes.Length
                Dim ThisRead = Source.Read(Bytes, Total, Bytes.Length - Total)
                If ThisRead = 0 Then Return Nothing
                Total += ThisRead
            End While
            Return Bytes
        End Using
    End Function

    Private Sub CompleteThumbnail(Generation As Integer, AnchorId As Long, Result As Bitmap)
        _ThumbnailPending.Remove(AnchorId)
        If Generation <> _ThumbnailGeneration OrElse _Disposed Then
            Result?.Dispose()
            Return
        End If

        If Result Is Nothing Then
            _ThumbnailUnavailable.Add(AnchorId)
        Else
            _ThumbnailCache(AnchorId) = Result
        End If
        InvalidateThumbnailItem(AnchorId)
    End Sub

    Private Sub InvalidateThumbnailItem(AnchorId As Long)
        If _ThumbnailCellSize = 0 Then Return
        For Each item As ListViewItem In lvFiles.Items
            Dim Entry = TryCast(item.Tag, EmbeddedFileSystem.ContentListEntry)
            If Entry IsNot Nothing AndAlso Entry.ChildAnchorId = AnchorId Then
                lvFiles.Invalidate(item.Bounds)
                Return
            End If
        Next
    End Sub

    Private Shared Function ScaleImageToBox(Source As Image, MaximumEdge As Integer) As Bitmap
        Dim Scale = Math.Min(MaximumEdge / CDbl(Source.Width), MaximumEdge / CDbl(Source.Height))
        If Scale > 1 Then Scale = 1
        Dim Width = Math.Max(1, CInt(Math.Round(Source.Width * Scale)))
        Dim Height = Math.Max(1, CInt(Math.Round(Source.Height * Scale)))

        Dim Result = New Bitmap(Width, Height, Imaging.PixelFormat.Format32bppPArgb)
        Using Canvas = Graphics.FromImage(Result)
            Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
            Canvas.PixelOffsetMode = PixelOffsetMode.HighQuality
            Canvas.CompositingQuality = CompositingQuality.HighQuality
            Canvas.DrawImage(Source, New Rectangle(0, 0, Width, Height))
        End Using
        Return Result
    End Function

    Private Sub ClearThumbnailCache()
        _ThumbnailGeneration += 1
        For Each thumbnail In _ThumbnailCache.Values
            thumbnail.Dispose()
        Next
        _ThumbnailCache.Clear()
        _ThumbnailPending.Clear()
        _ThumbnailUnavailable.Clear()
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

        ' Signals any executable-icon or thumbnail worker still in flight to drop its result.
        _Disposed = True
        _ThumbnailGeneration += 1

        If Disposing Then
            If components IsNot Nothing Then components.Dispose()
            _IconProvider.Dispose()
            For Each thumbnail In _ThumbnailCache.Values
                thumbnail.Dispose()
            Next
            _ThumbnailCache.Clear()
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
