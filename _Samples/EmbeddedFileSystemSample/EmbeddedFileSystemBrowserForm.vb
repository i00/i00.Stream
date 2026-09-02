Imports i00.Streams
Imports System.ComponentModel
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Threading
Imports System.Runtime.InteropServices
Imports System.Text.RegularExpressions
Imports EmbeddedFileSystemSample.VirtualDragCopyFiles

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

    ''' <summary>The anchor id and name of one child directory - all the tree needs to know per child.</summary>
    Private NotInheritable Class ChildDirectory
        Public Sub New(AnchorId As Long, Name As String)
            Me.AnchorId = AnchorId
            Me.Name = Name
        End Sub

        Public ReadOnly Property AnchorId As Long
        Public Property Name As String
    End Class

    ''' <summary>
    ''' Backs a folder node in the tree. The tree is filled lazily: <see cref="ChildDirectories"/> is the
    ''' one-time fetch of a folder's child directories (used to decide whether to show an expander and,
    ''' later, to build the child nodes), and <see cref="ChildrenMaterialised"/> tracks whether those
    ''' child nodes have actually been created yet.
    ''' </summary>
    Private NotInheritable Class DirectoryNodeInfo
        Public Sub New(AnchorId As Long, Name As String)
            Me.AnchorId = AnchorId
            Me.Name = Name
        End Sub

        Public Property AnchorId As Long
        Public Property Name As String
        Public Property ChildDirectories As List(Of ChildDirectory)
        Public Property ChildrenMaterialised As Boolean

        ''' <summary>True while this is a not-yet-created folder whose label is being typed.</summary>
        Public Property IsUncommitted As Boolean
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

    ''' <summary>
    ''' Sorts the file list with directories always ahead of files, and names ordered naturally
    ''' ("File2" before "File10") via <see cref="AlphaNumericSorter"/>.
    ''' </summary>
    Private NotInheritable Class EntryListViewComparer
        Implements IComparer

        Private Shared ReadOnly NameComparer As New AlphaNumericSorter()

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
            ElseIf Column >= 2 AndAlso Left.SubItems.Count > Column AndAlso Right.SubItems.Count > Column Then
                Result = NameComparer.Compare(Left.SubItems(Column).Text, Right.SubItems(Column).Text)
            Else
                Result = NameComparer.Compare(Left.Text, Right.Text)
            End If

            If Result = 0 Then Result = NameComparer.Compare(Left.Text, Right.Text)
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

    ''' <summary>
    ''' One '|'-separated piece of the address/search box: an optional folder path (the part up to the
    ''' last backslash) plus a name pattern (the rest). <see cref="HasPath"/> records whether the piece
    ''' actually contained a backslash.
    ''' </summary>
    Private NotInheritable Class QueryTerm
        Public Sub New(PathSegments As String(), Pattern As String, HasPath As Boolean)
            Me.PathSegments = PathSegments
            Me.Pattern = Pattern
            Me.HasPath = HasPath
        End Sub

        Public ReadOnly Property PathSegments As String()
        Public ReadOnly Property Pattern As String
        Public ReadOnly Property HasPath As Boolean

        ''' <summary>An empty or bare "*" pattern lists a folder's direct contents; anything else searches.</summary>
        Public ReadOnly Property ListsDirectContents As Boolean
            Get
                Return Pattern.Length = 0 OrElse Pattern = "*"
            End Get
        End Property
    End Class

    ''' <summary>One search match, with the anchor and display path of its containing folder.</summary>
    Private NotInheritable Class SearchHit
        Public Sub New(Entry As EmbeddedFileSystem.ContentListEntry, ParentAnchorId As Long, ParentPath As String)
            Me.Entry = Entry
            Me.ParentAnchorId = ParentAnchorId
            Me.ParentPath = ParentPath
        End Sub

        Public ReadOnly Property Entry As EmbeddedFileSystem.ContentListEntry
        Public ReadOnly Property ParentAnchorId As Long
        Public ReadOnly Property ParentPath As String
    End Class

    Private ReadOnly _FileSystem As EmbeddedFileSystem
    Private ReadOnly _IconProvider As New FileIconProvider()
    Private ReadOnly _ListSorter As New EntryListViewComparer()
    Private ReadOnly _NameSorter As New AlphaNumericSorter()
    Private ReadOnly _ThumbnailCache As New Dictionary(Of Long, Bitmap)()
    Private ReadOnly _ThumbnailPending As New HashSet(Of Long)()
    Private ReadOnly _ThumbnailUnavailable As New HashSet(Of Long)()
    Private ReadOnly _History As New List(Of String)()
    Private ReadOnly _SearchResults As New List(Of SearchHit)()
    Private ReadOnly _SearchItemInfo As New Dictionary(Of ListViewItem, SearchHit)()
    Private ReadOnly _PathColumn As New ColumnHeader() With {.Text = "Path", .Width = 260}
    Private WithEvents _SearchTimer As New System.Windows.Forms.Timer() With {.Interval = 400}
    Private _HistoryIndex As Integer = -1
    Private _ApplyingLocation As Boolean
    Private _SearchActive As Boolean
    Private _CompletedSearchGeneration As Integer = -1
    Private _CurrentQueryText As String = String.Empty
    Private _LastSearchedText As String
    Private _LastPushWasTyped As Boolean
    Private _SearchGeneration As Integer
    Private _SuppressSearchText As Boolean
    Private _CurrentDirectoryAnchorId As Long
    Private _Disposed As Boolean
    Private _ThumbnailGeneration As Integer
    Private _ThumbnailCacheAnchorId As Long
    Private _ThumbnailCellSize As Integer
    Private _EditingListItemIndex As Integer = -1

    ''' <summary>
    ''' Set immediately before a deliberate <c>BeginEdit</c> (F2, the Rename item, or a new folder). Any
    ''' label edit the tree or list tries to start on its own - the slow second click on a selected item -
    ''' is refused so it can never swallow a drag or a right-click.
    ''' </summary>
    Private _LabelEditRequested As Boolean


    Public Sub New(FileSystem As EmbeddedFileSystem)
        If FileSystem Is Nothing Then Throw New ArgumentNullException(NameOf(FileSystem))

        _FileSystem = FileSystem
        _CurrentDirectoryAnchorId = _FileSystem.RootAnchorId

        InitializeComponent()

        tvFolders.ImageList = _IconProvider.TreeImages
        tvFolders.LabelEdit = True
        lvFiles.ListViewItemSorter = _ListSorter
        lvFiles.LabelEdit = True

        ' The menus (defined in the designer) are shown explicitly from the MouseDown handlers.
        lvFiles.ContextMenuStrip = Nothing
        tvFolders.ContextMenuStrip = Nothing

        Try
            SetWindowTheme(tvFolders.Handle, "Explorer", Nothing)
            SetWindowTheme(lvFiles.Handle, "Explorer", Nothing)
            SendMessage(lvFiles.Handle, LvmSetExtendedListViewStyle, New IntPtr(LvsExDoubleBuffer), New IntPtr(LvsExDoubleBuffer))
        Catch ex As Exception

        End Try

        ApplySavedFileListView()
        RefreshFileSystemView()

        SetAddressText(String.Empty)

    End Sub

    Public ReadOnly Property FileSystem As EmbeddedFileSystem
        Get
            Return _FileSystem
        End Get
    End Property

    Public Sub RefreshFileSystemView()

        Dim ExpandedAnchors = CollectExpandedAnchors()

        tvFolders.BeginUpdate()
        Try
            tvFolders.Nodes.Clear()
            Dim RootNode = CreateDirectoryNode(_FileSystem.RootAnchorId, "Root")
            tvFolders.Nodes.Add(RootNode)
            MaterialiseChildNodes(RootNode)
            RootNode.Expand()
            ReExpandAnchors(RootNode, ExpandedAnchors)
        Finally
            tvFolders.EndUpdate()
        End Try

        ' Re-apply the address so the tree selection and the list follow the box (no new history entry).
        If tsiSearch IsNot Nothing Then
            _LastSearchedText = Nothing
            ApplyAddress(_CurrentQueryText)
        End If

        'Dim Fragmentation = _FileSystem.ChunkedStream.GetFragmentation
        Dim Struct = FileSystem.ChunkedStream.GetStructure()

        tsiFragmentation.Text = $"Fragmentation: {Struct.FragmentationRatio:P0}{vbCrLf}Click to defrag"
        tsiCompression.Text = $"File size: {Struct.PhysicalLength.FormatFileSizeFromBytes()} " &
                              $"Data size: {Struct.LogicalLength.FormatFileSizeFromBytes()} " &
                              $"Fragmented waste: {Struct.FragmentedBytes.FormatFileSizeFromBytes()} "


        'If Fragmentation >= 0.1 Then
        '    tsiCompression.Visible = True
        '    tsiCompressionBar.Visible = False
        '    tsiFragmentation.Visible = True


        'ElseIf _FileSystem.GetDirectoryEntries(_FileSystem.RootAnchorId).Any() = False Then
        '    tsiCompression.Visible = True
        '    tsiCompressionBar.Visible = False
        '    tsiFragmentation.Visible = False

        '    tsiCompression.Text = "No Data"
        'Else
        '    tsiCompression.Visible = True
        '    tsiCompressionBar.Visible = True
        '    tsiFragmentation.Visible = False

        '    Dim Ratio = _FileSystem.ChunkedStream.Length / _FileSystem.ChunkedStream.BaseStream.Length

        '    Dim CompressionPercent = Ratio * 100
        '    tsiCompression.Text = $"Relative size: {Ratio:P0}"
        '    tsiCompressionBar.MaxValue = Math.Max(CompressionPercent, 100)
        '    tsiCompressionBar.Value = CompressionPercent
        'End If
    End Sub

    ' ===================================================================================================
    ' Lazily populated folder tree
    ' ===================================================================================================

    ''' <summary>
    ''' Creates a folder node whose children are not built until it is expanded. A single empty
    ''' placeholder child is added so the expander shows when (and only when) the folder actually
    ''' contains sub-directories.
    ''' </summary>
    Private Function CreateDirectoryNode(AnchorId As Long, Name As String) As TreeNode
        Dim Info As New DirectoryNodeInfo(AnchorId, Name)
        Dim Node As New TreeNode(Name) With {
            .Name = AnchorId.ToString(),
            .Tag = Info,
            .ToolTipText = $"Anchor {AnchorId}",
            .ImageKey = FileIconProvider.FolderKey,
            .SelectedImageKey = FileIconProvider.FolderKey
        }
        If GetChildDirectories(Info).Count > 0 Then Node.Nodes.Add(New TreeNode(String.Empty))
        Return Node
    End Function

    ''' <summary>Creates a folder node already known to be empty, without touching the file system.</summary>
    Private Shared Function CreateEmptyDirectoryNode(AnchorId As Long, Name As String) As TreeNode
        Dim Info As New DirectoryNodeInfo(AnchorId, Name) With {
            .ChildDirectories = New List(Of ChildDirectory)(),
            .ChildrenMaterialised = True
        }
        Return New TreeNode(Name) With {
            .Name = AnchorId.ToString(),
            .Tag = Info,
            .ToolTipText = $"Anchor {AnchorId}",
            .ImageKey = FileIconProvider.FolderKey,
            .SelectedImageKey = FileIconProvider.FolderKey
        }
    End Function

    ''' <summary>A folder node's child directories, fetched from the file system once and then cached.</summary>
    Private Function GetChildDirectories(Info As DirectoryNodeInfo) As List(Of ChildDirectory)
        If Info.ChildDirectories Is Nothing Then
            Dim Directories As New List(Of ChildDirectory)()
            Try
                For Each entry In _FileSystem.GetDirectoryEntries(Info.AnchorId)
                    If entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory Then
                        Directories.Add(New ChildDirectory(entry.ChildAnchorId, entry.Name))
                    End If
                Next
            Catch
                ' A folder removed under us simply has no children.
            End Try
            Info.ChildDirectories = Directories
        End If
        Return Info.ChildDirectories
    End Function

    ''' <summary>Builds a folder node's real child nodes from its cached directory list, once.</summary>
    Private Sub MaterialiseChildNodes(Node As TreeNode)
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing OrElse Info.ChildrenMaterialised Then Return
        Info.ChildrenMaterialised = True

        tvFolders.BeginUpdate()
        Try
            Node.Nodes.Clear()
            For Each child In GetChildDirectories(Info).OrderBy(Function(c) c.Name, _NameSorter)
                If IsAncestorAnchor(Node, child.AnchorId) Then
                    Node.Nodes.Add(New TreeNode("[directory cycle]") With {.ForeColor = Drawing.BlendColor(tvFolders.ForeColor, Color.Red)})
                Else
                    Node.Nodes.Add(CreateDirectoryNode(child.AnchorId, child.Name))
                End If
            Next
        Finally
            tvFolders.EndUpdate()
        End Try
    End Sub

    Private Shared Function IsAncestorAnchor(Node As TreeNode, AnchorId As Long) As Boolean
        Dim Current = Node
        While Current IsNot Nothing
            Dim Info = TryCast(Current.Tag, DirectoryNodeInfo)
            If Info IsNot Nothing AndAlso Info.AnchorId = AnchorId Then Return True
            Current = Current.Parent
        End While
        Return False
    End Function

    Private Sub tvFolders_BeforeExpand(Sender As Object, EventArgs As TreeViewCancelEventArgs) Handles tvFolders.BeforeExpand
        MaterialiseChildNodes(EventArgs.Node)
    End Sub

    ''' <summary>
    ''' Returns the tree node for <paramref name="AnchorId"/>, materialising every folder on the path
    ''' from the root down so a target that has never been expanded still resolves.
    ''' </summary>
    Private Function EnsureDirectoryNode(AnchorId As Long) As TreeNode
        If AnchorId <= 0 OrElse tvFolders.Nodes.Count = 0 Then Return Nothing
        Dim RootNode = tvFolders.Nodes(0)
        If AnchorId = _FileSystem.RootAnchorId Then Return RootNode

        Dim Chain As New List(Of Long)()
        Dim Current = AnchorId
        Dim Guard = 0
        While Current > 0 AndAlso Current <> _FileSystem.RootAnchorId
            Chain.Add(Current)
            Guard += 1
            If Guard > 8192 Then Return Nothing
            Try
                Current = _FileSystem.GetParentAnchorId(Current)
            Catch
                Return Nothing
            End Try
        End While
        If Current <> _FileSystem.RootAnchorId Then Return Nothing
        Chain.Reverse()

        Dim CursorNode = RootNode
        For Each StepAnchor In Chain
            MaterialiseChildNodes(CursorNode)
            Dim NextNode As TreeNode = Nothing
            For Each ChildNode As TreeNode In CursorNode.Nodes
                Dim ChildInfo = TryCast(ChildNode.Tag, DirectoryNodeInfo)
                If ChildInfo IsNot Nothing AndAlso ChildInfo.AnchorId = StepAnchor Then
                    NextNode = ChildNode
                    Exit For
                End If
            Next
            If NextNode Is Nothing Then Return Nothing
            CursorNode = NextNode
        Next
        Return CursorNode
    End Function

    Private Function CollectExpandedAnchors() As HashSet(Of Long)
        Dim Result As New HashSet(Of Long)()
        If tvFolders.Nodes.Count > 0 Then CollectExpandedAnchors(tvFolders.Nodes(0), Result)
        Return Result
    End Function

    Private Shared Sub CollectExpandedAnchors(Node As TreeNode, Into As HashSet(Of Long))
        If Node.IsExpanded = False Then Return
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info IsNot Nothing Then Into.Add(Info.AnchorId)
        For Each ChildNode As TreeNode In Node.Nodes
            CollectExpandedAnchors(ChildNode, Into)
        Next
    End Sub

    Private Sub ReExpandAnchors(Node As TreeNode, Anchors As HashSet(Of Long))
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing OrElse Anchors.Contains(Info.AnchorId) = False Then Return
        MaterialiseChildNodes(Node)
        Node.Expand()
        For Each ChildNode As TreeNode In Node.Nodes
            ReExpandAnchors(ChildNode, Anchors)
        Next
    End Sub

    ''' <summary>Re-reads one folder's children after its contents changed, rebuilding just that node.</summary>
    Private Sub RefreshFolderNode(AnchorId As Long)
        Dim Node = FindDirectoryNode(AnchorId)
        If Node Is Nothing Then Return
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return

        Dim WasExpanded = Node.IsExpanded
        Info.ChildDirectories = Nothing
        Info.ChildrenMaterialised = False
        tvFolders.BeginUpdate()
        Try
            Node.Nodes.Clear()
            If GetChildDirectories(Info).Count > 0 Then Node.Nodes.Add(New TreeNode(String.Empty))
            If WasExpanded Then
                MaterialiseChildNodes(Node)
                Node.Expand()
            End If
        Finally
            tvFolders.EndUpdate()
        End Try
    End Sub

    ''' <summary>Re-orders one tree node's immediate children by name without a file-system read.</summary>
    Private Sub ResortChildNodes(ParentNode As TreeNode)
        If ParentNode Is Nothing OrElse ParentNode.Nodes.Count < 2 Then Return
        Dim Ordered = ParentNode.Nodes.Cast(Of TreeNode)().OrderBy(Function(n) n.Text, _NameSorter).ToArray()
        Dim Selected = tvFolders.SelectedNode
        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        tvFolders.BeginUpdate()
        Try
            ParentNode.Nodes.Clear()
            ParentNode.Nodes.AddRange(Ordered)
            If Selected IsNot Nothing Then tvFolders.SelectedNode = Selected
        Finally
            tvFolders.EndUpdate()
            _ApplyingLocation = WasApplying
        End Try
    End Sub

    ''' <summary>Adds a node for a just-created directory to its parent node, without a file-system read.</summary>
    Private Sub AddDirectoryToTree(ParentAnchorId As Long, NewAnchorId As Long, Name As String)
        Dim ParentNode = FindDirectoryNode(ParentAnchorId)
        If ParentNode Is Nothing Then Return
        Dim ParentInfo = TryCast(ParentNode.Tag, DirectoryNodeInfo)
        If ParentInfo Is Nothing Then Return

        If ParentInfo.ChildDirectories IsNot Nothing AndAlso
           ParentInfo.ChildDirectories.Any(Function(c) c.AnchorId = NewAnchorId) = False Then
            ParentInfo.ChildDirectories.Add(New ChildDirectory(NewAnchorId, Name))
        End If

        If ParentInfo.ChildrenMaterialised Then
            ' The [+] placeholder (if any) can go now that there is a real child.
            Dim Placeholder = ParentNode.Nodes.Cast(Of TreeNode)().FirstOrDefault(Function(n) n.Tag Is Nothing)
            If Placeholder IsNot Nothing Then ParentNode.Nodes.Remove(Placeholder)
            If ParentNode.Nodes.Cast(Of TreeNode)().Any(Function(n) NodeAnchorId(n) = NewAnchorId) = False Then
                ParentNode.Nodes.Add(CreateEmptyDirectoryNode(NewAnchorId, Name))
                ResortChildNodes(ParentNode)
            End If
        ElseIf ParentNode.Nodes.Count = 0 Then
            ParentNode.Nodes.Add(New TreeNode(String.Empty))
        End If
    End Sub

    ''' <summary>Removes a directory's node from the tree and its parent's cached child list.</summary>
    Private Sub RemoveDirectoryFromTree(ParentAnchorId As Long, ChildAnchorId As Long)
        Dim ParentNode = FindDirectoryNode(ParentAnchorId)
        If ParentNode Is Nothing Then Return
        Dim ParentInfo = TryCast(ParentNode.Tag, DirectoryNodeInfo)
        If ParentInfo?.ChildDirectories IsNot Nothing Then
            ParentInfo.ChildDirectories.RemoveAll(Function(c) c.AnchorId = ChildAnchorId)
        End If
        Dim ChildNode = ParentNode.Nodes.Cast(Of TreeNode)().FirstOrDefault(Function(n) NodeAnchorId(n) = ChildAnchorId)
        If ChildNode IsNot Nothing Then ParentNode.Nodes.Remove(ChildNode)
    End Sub

    Private Shared Function NodeAnchorId(Node As TreeNode) As Long
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        Return If(Info Is Nothing, 0L, Info.AnchorId)
    End Function

    ' ===================================================================================================
    ' In-place folder creation and label editing (rename)
    ' ===================================================================================================

    ''' <summary>
    ''' Starts a new folder in the current directory as an uncommitted tree node whose name is typed in
    ''' place. Pressing Escape (or leaving it blank) drops the node; a name creates the folder for real.
    ''' </summary>
    Private Sub BeginNewFolderInline(Sender As Object, EventArgs As EventArgs) Handles tsiNewFolder.Click, tsiFolderNewFolder.Click
        If _SearchActive Then Return
        Dim ParentAnchorId = _CurrentDirectoryAnchorId
        If ParentAnchorId <= 0 Then Return

        Dim ParentNode = EnsureDirectoryNode(ParentAnchorId)
        If ParentNode Is Nothing Then Return
        MaterialiseChildNodes(ParentNode)
        ParentNode.Expand()

        Dim Info As New DirectoryNodeInfo(0, "New folder") With {
            .ChildDirectories = New List(Of ChildDirectory)(),
            .ChildrenMaterialised = True,
            .IsUncommitted = True
        }
        Dim NewNode As New TreeNode("New folder") With {
            .Tag = Info,
            .ImageKey = FileIconProvider.FolderKey,
            .SelectedImageKey = FileIconProvider.FolderKey
        }
        ParentNode.Nodes.Add(NewNode)

        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        Try
            tvFolders.SelectedNode = NewNode
        Finally
            _ApplyingLocation = WasApplying
        End Try
        NewNode.EnsureVisible()
        tvFolders.Focus()
        NewNode.BeginEdit()
    End Sub

    Private Sub RenameSelectedFolder(Sender As Object, EventArgs As EventArgs) Handles tsiFolderRename.Click
        If tvFolders.SelectedNode Is Nothing Then Return
        tvFolders.Focus()
        _LabelEditRequested = True
        tvFolders.SelectedNode.BeginEdit()
    End Sub

    Private Sub tvFolders_BeforeLabelEdit(Sender As Object, EventArgs As NodeLabelEditEventArgs) Handles tvFolders.BeforeLabelEdit
        Dim WasRequested = _LabelEditRequested
        _LabelEditRequested = False

        Dim Info = TryCast(EventArgs.Node.Tag, DirectoryNodeInfo)
        If WasRequested = False OrElse Info Is Nothing OrElse
           (Info.IsUncommitted = False AndAlso Info.AnchorId = _FileSystem.RootAnchorId) Then
            EventArgs.CancelEdit = True
        End If
    End Sub

    Private Sub tvFolders_AfterLabelEdit(Sender As Object, EventArgs As NodeLabelEditEventArgs) Handles tvFolders.AfterLabelEdit
        Dim Node = EventArgs.Node
        Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return

        ' We always apply the final text ourselves so the siblings can be re-sorted afterwards.
        EventArgs.CancelEdit = True
        Dim NewName = If(EventArgs.Label, String.Empty).Trim()

        If Info.IsUncommitted Then
            If EventArgs.Label Is Nothing OrElse NewName.Length = 0 Then
                RemoveUncommittedNode(Node)
                Return
            End If
            CommitNewFolder(Node, Info, NewName)
            Return
        End If

        If EventArgs.Label Is Nothing OrElse NewName.Length = 0 Then Return
        If String.Equals(NewName, Node.Text, StringComparison.Ordinal) Then Return

        Dim ParentNode = Node.Parent
        Dim ParentInfo = TryCast(ParentNode?.Tag, DirectoryNodeInfo)
        If ParentInfo Is Nothing Then Return

        Try
            _FileSystem.RenameEntry(ParentInfo.AnchorId, Node.Text, NewName)
        Catch ex As Exception
            MsgBox(Me, $"Could not rename '{Node.Text}'.{Environment.NewLine}{ex.Message}", MsgBoxStyle.Critical)
            Return
        End Try

        Info.Name = NewName
        Node.Text = NewName
        UpdateCachedChildName(ParentInfo, Info.AnchorId, NewName)
        UpdateListItemName(Info.AnchorId, NewName)
        ResortChildNodes(ParentNode)
    End Sub

    Private Sub RemoveUncommittedNode(Node As TreeNode)
        Dim Parent = Node.Parent
        Node.Remove()
        If Parent IsNot Nothing Then
            Dim WasApplying = _ApplyingLocation
            _ApplyingLocation = True
            Try
                tvFolders.SelectedNode = Parent
            Finally
                _ApplyingLocation = WasApplying
            End Try
        End If
    End Sub

    Private Sub CommitNewFolder(Node As TreeNode, Info As DirectoryNodeInfo, Name As String)
        Dim ParentNode = Node.Parent
        Dim ParentInfo = TryCast(ParentNode?.Tag, DirectoryNodeInfo)
        If ParentInfo Is Nothing Then
            RemoveUncommittedNode(Node)
            Return
        End If

        Dim NewId As Long
        Try
            NewId = _FileSystem.CreateDirectory(ParentInfo.AnchorId, Name)
        Catch ex As Exception
            MsgBox(Me, $"Could not create the folder.{Environment.NewLine}{ex.Message}", MsgBoxStyle.Critical)
            RemoveUncommittedNode(Node)
            Return
        End Try

        Info.AnchorId = NewId
        Info.Name = Name
        Info.IsUncommitted = False
        Node.Text = Name
        Node.Name = NewId.ToString()
        Node.ToolTipText = $"Anchor {NewId}"

        If ParentInfo.ChildDirectories IsNot Nothing Then ParentInfo.ChildDirectories.Add(New ChildDirectory(NewId, Name))
        ResortChildNodes(ParentNode)

        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        Try
            tvFolders.SelectedNode = Node
        Finally
            _ApplyingLocation = WasApplying
        End Try

        If _CurrentDirectoryAnchorId = ParentInfo.AnchorId AndAlso _SearchActive = False Then RefreshCurrentDirectory()
    End Sub

    Private Shared Sub UpdateCachedChildName(ParentInfo As DirectoryNodeInfo, ChildAnchorId As Long, NewName As String)
        If ParentInfo.ChildDirectories Is Nothing Then Return
        Dim Cached = ParentInfo.ChildDirectories.FirstOrDefault(Function(c) c.AnchorId = ChildAnchorId)
        If Cached IsNot Nothing Then Cached.Name = NewName
    End Sub

    ''' <summary>Updates the file-list row for an entry after a rename, if that folder's contents are shown.</summary>
    Private Sub UpdateListItemName(ChildAnchorId As Long, NewName As String)
        For Each Item As ListViewItem In lvFiles.Items
            Dim Entry = TryCast(Item.Tag, EmbeddedFileSystem.ContentListEntry)
            If Entry IsNot Nothing AndAlso Entry.ChildAnchorId = ChildAnchorId Then
                Item.Text = NewName
                lvFiles.Sort()
                Return
            End If
        Next
    End Sub

    ' ---- File list label editing -------------------------------------------------------------------

    Private Sub RenameSelectedListEntry(Sender As Object, EventArgs As EventArgs) Handles tsiRename.Click
        If lvFiles.SelectedItems.Count <> 1 Then Return
        lvFiles.Select()
        lvFiles.SelectedItems(0).BeginEdit()
    End Sub

    Private Sub lvFiles_BeforeLabelEdit(Sender As Object, EventArgs As LabelEditEventArgs) Handles lvFiles.BeforeLabelEdit
        _EditingListItemIndex = EventArgs.Item
    End Sub

    Private Sub lvFiles_AfterLabelEdit(Sender As Object, EventArgs As LabelEditEventArgs) Handles lvFiles.AfterLabelEdit
        _EditingListItemIndex = -1
        Dim Item = lvFiles.Items(EventArgs.Item)
        Dim Entry = TryCast(Item.Tag, EmbeddedFileSystem.ContentListEntry)
        If Entry Is Nothing Then
            EventArgs.CancelEdit = True
            Return
        End If

        Dim NewName = If(EventArgs.Label, String.Empty).Trim()
        If EventArgs.Label Is Nothing OrElse NewName.Length = 0 OrElse String.Equals(NewName, Item.Text, StringComparison.Ordinal) Then
            EventArgs.CancelEdit = True
            Return
        End If

        Dim Hit As SearchHit = Nothing
        Dim ParentAnchorId = If(_SearchItemInfo.TryGetValue(Item, Hit), Hit.ParentAnchorId, _CurrentDirectoryAnchorId)

        Try
            _FileSystem.RenameEntry(ParentAnchorId, Item.Text, NewName)
        Catch ex As Exception
            EventArgs.CancelEdit = True
            MsgBox(Me, $"Could not rename '{Item.Text}'.{Environment.NewLine}{ex.Message}", MsgBoxStyle.Critical)
            Return
        End Try

        If IsDirectory(Entry) Then
            Dim Node = FindDirectoryNode(Entry.ChildAnchorId)
            If Node IsNot Nothing Then
                Node.Text = NewName
                Dim Info = TryCast(Node.Tag, DirectoryNodeInfo)
                If Info IsNot Nothing Then Info.Name = NewName
                Dim ParentInfo = TryCast(Node.Parent?.Tag, DirectoryNodeInfo)
                If ParentInfo IsNot Nothing Then UpdateCachedChildName(ParentInfo, Entry.ChildAnchorId, NewName)
                ResortChildNodes(Node.Parent)
            End If
        End If

        ' The list keeps the accepted label; just re-sort so it lands in the right place.
        lvFiles.BeginInvoke(Sub()
                                Item.Text = NewName
                                lvFiles.Sort()
                            End Sub)
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
            _SearchItemInfo.Clear()

            For Each entry In Entries
                Dim IsDirectory = entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
                Dim Item = New ListViewItem(entry.Name) With {.Tag = entry}
                Item.SubItems.Add(If(IsDirectory, String.Empty, FormatByteLength(entry.LengthOfDataAtEntry)))
                Item.SubItems.Add(GetEntryStateText(entry.EntryType))
                If entry.EntryType = EmbeddedFileSystem.EntryTypes.PendingFile Then Item.ForeColor = Drawing.BlendColor(lvFiles.ForeColor, Color.Red)
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
        ' Don't start a load the form can't finish. The read is fast, so a load queued during
        ' construction / early Load can complete before the window handle exists; LoadExecutableIcon
        ' then discards the result (IsHandleCreated = False) but the anchor stays marked "pending"
        ' forever in BeginExecutableLoad, so it is never retried. A later paint - once the handle
        ' exists - requests it normally.
        If IsHandleCreated = False Then Return
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
        Dim SelectedCount = lvFiles.SelectedItems.Count
        Dim SelectionText = If(SelectedCount = 0, String.Empty, $", {SelectedCount:N0} selected")

        If _SearchActive Then
            Dim ProgressText = If(_SearchGeneration = _CompletedSearchGeneration, String.Empty, " (searching...)")
            StatusLabel.Text = $"'{_CurrentQueryText}': {lvFiles.Items.Count:N0} result{If(lvFiles.Items.Count = 1, "", "s")}{ProgressText}{SelectionText}"
            ' Incremental result batches update this caption faster than the ToolStrip repaints
            ' itself, so force it while a search is on screen.
            StatusLabel.Owner?.Refresh()
        Else
            Dim DirectoryName = If(tvFolders.SelectedNode Is Nothing, "Root", tvFolders.SelectedNode.Text)
            StatusLabel.Text = $"{DirectoryName}: {lvFiles.Items.Count:N0} items{SelectionText}"
        End If
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
        SetAddressText(BuildPathForAnchor(DirectoryAnchorId))
    End Sub

    Private Sub tvFolders_AfterSelect(Sender As Object, EventArgs As TreeViewEventArgs) Handles tvFolders.AfterSelect
        If _ApplyingLocation OrElse EventArgs.Node Is Nothing Then Return
        If TryCast(EventArgs.Node.Tag, DirectoryNodeInfo) Is Nothing Then Return
        SetAddressText(BuildNodePath(EventArgs.Node))
    End Sub

    ' ===================================================================================================
    ' Address / search box - the single input that drives the tree selection and the file list
    ' ===================================================================================================
    '
    ' The box text is the source of truth. It is one or more '|'-separated pieces; each piece is an
    ' optional folder path (up to the last '\') plus a name pattern:
    '   \A\B\                a plain folder path -> navigate, show that folder's contents
    '   exe                  a bare pattern -> recursive "contains" search from the root
    '   *.exe                wildcards (* = one or more chars, ? = one char) -> recursive glob from root
    '   \A\B\*.exe           a pattern under a path -> recursive glob within \A\B
    '   \A\*  or  \A\        list \A's direct contents
    '   \A\*|\B\*            union of two folders' direct contents
    '
    ' Programmatic changes (tree click, Back/Forward, "Open Folder", Ctrl+F) go through SetAddressText
    ' and apply immediately; only user typing is debounced.

    ''' <summary>Sets the box text from code and applies it at once, pushing a history entry.</summary>
    Private Sub SetAddressText(Text As String)
        _SearchTimer.Stop()
        _LastPushWasTyped = False
        _SuppressSearchText = True
        Try
            tsiSearch.Text = Text
        Finally
            _SuppressSearchText = False
        End Try
        PushAddressHistory(Text)
        ApplyAddress(Text)
        UpdateNavigationButtons()
    End Sub

    Private Sub PushAddressHistory(Text As String)
        If _HistoryIndex >= 0 AndAlso String.Equals(_History(_HistoryIndex), Text, StringComparison.OrdinalIgnoreCase) Then Return
        If _HistoryIndex < _History.Count - 1 Then _History.RemoveRange(_HistoryIndex + 1, _History.Count - _HistoryIndex - 1)
        _History.Add(Text)
        _HistoryIndex = _History.Count - 1
    End Sub

    Private Sub GoBack()
        If _HistoryIndex <= 0 Then Return
        _HistoryIndex -= 1
        ApplyHistoryEntry()
    End Sub

    Private Sub GoForward()
        If _HistoryIndex >= _History.Count - 1 Then Return
        _HistoryIndex += 1
        ApplyHistoryEntry()
    End Sub

    Private Sub ApplyHistoryEntry()
        _SearchTimer.Stop()
        _LastPushWasTyped = False
        Dim Text = _History(_HistoryIndex)
        _SuppressSearchText = True
        Try
            tsiSearch.Text = Text
        Finally
            _SuppressSearchText = False
        End Try
        ApplyAddress(Text)
        UpdateNavigationButtons()
    End Sub

    Private Sub UpdateNavigationButtons()
        tsiBack.Enabled = _HistoryIndex > 0
        tsiForward.Enabled = _HistoryIndex < _History.Count - 1
    End Sub

    Private Sub tsiBack_Click(Sender As Object, EventArgs As EventArgs) Handles tsiBack.Click
        GoBack()
    End Sub

    Private Sub tsiForward_Click(Sender As Object, EventArgs As EventArgs) Handles tsiForward.Click
        GoForward()
    End Sub

    Private Sub tsiSearch_TextChanged(Sender As Object, EventArgs As EventArgs) Handles tsiSearch.TextChanged
        If _SuppressSearchText Then Return
        _SearchTimer.Stop()
        _SearchTimer.Start()
    End Sub

    Private Sub SearchTimer_Tick(Sender As Object, EventArgs As EventArgs) Handles _SearchTimer.Tick
        _SearchTimer.Stop()
        CommitTypedAddress()
    End Sub

    ''' <summary>Enter applies the typed text at once instead of waiting out the debounce.</summary>
    Private Sub tsiSearch_KeyDown(Sender As Object, EventArgs As KeyEventArgs) Handles tsiSearch.KeyDown
        If EventArgs.KeyCode <> Keys.Enter AndAlso EventArgs.KeyCode <> Keys.Return Then Return
        _SearchTimer.Stop()
        CommitTypedAddress()
        EventArgs.Handled = True
        EventArgs.SuppressKeyPress = True
    End Sub

    ''' <summary>Applies text the user typed. Consecutive typed edits collapse into one history entry.</summary>
    Private Sub CommitTypedAddress()
        _SearchTimer.Stop()
        Dim Text = tsiSearch.Text
        If _LastPushWasTyped AndAlso _HistoryIndex >= 0 Then
            _History(_HistoryIndex) = Text
        Else
            PushAddressHistory(Text)
        End If
        _LastPushWasTyped = True
        ApplyAddress(Text)
        UpdateNavigationButtons()
    End Sub

    ''' <summary>Ctrl+F: focus the box, prefilled with the current folder path and the cursor at the end.</summary>
    Private Sub FocusSearchBox()
        tsiSearch.Focus()
        If _SearchActive Then
            tsiSearch.SelectAll()
            Return
        End If
        Dim Path = CurrentFolderPath()
        If String.Equals(tsiSearch.Text, Path, StringComparison.Ordinal) = False Then
            _SuppressSearchText = True
            Try
                tsiSearch.Text = Path
            Finally
                _SuppressSearchText = False
            End Try
        End If
        _SearchTimer.Stop()
        tsiSearch.SelectionStart = tsiSearch.Text.Length
        tsiSearch.SelectionLength = 0
    End Sub

    ' ---- Parsing and dispatch ---------------------------------------------------------------------------

    Private Sub ApplyAddress(Text As String)
        _CurrentQueryText = If(Text, String.Empty)
        Dim Terms = ParseAddress(_CurrentQueryText)

        If Terms.Count = 0 OrElse (Terms.Count = 1 AndAlso Terms(0).Pattern.Length = 0) Then
            Dim Segments = If(Terms.Count = 0, Array.Empty(Of String)(), Terms(0).PathSegments)
            ShowFolderContents(Segments)
        Else
            ShowSearchResults(Terms)
        End If
    End Sub

    Private Shared Function ParseAddress(Text As String) As List(Of QueryTerm)
        Dim Result As New List(Of QueryTerm)()
        For Each Raw In Text.Split("|"c)
            Dim Piece = Raw.Trim()
            Dim BackslashIndex = Piece.LastIndexOf("\"c)
            If BackslashIndex < 0 Then
                If Piece.Length > 0 Then Result.Add(New QueryTerm(Array.Empty(Of String)(), Piece, False))
            Else
                Dim Segments = Piece.Substring(0, BackslashIndex).Split("\"c).Where(Function(s) s.Length > 0).ToArray()
                Result.Add(New QueryTerm(Segments, Piece.Substring(BackslashIndex + 1), True))
            End If
        Next
        Return Result
    End Function

    ' ---- Folder navigation ---------------------------------------------------------------------------

    Private Sub ShowFolderContents(Segments As String())
        Dim WasSearching = _SearchActive
        _SearchActive = False
        RemovePathColumn()

        Dim Node = EnsureNodeForPath(Segments)
        If Node Is Nothing Then
            _CurrentDirectoryAnchorId = 0
            lvFiles.BeginUpdate()
            Try
                lvFiles.Items.Clear()
                _SearchItemInfo.Clear()
            Finally
                lvFiles.EndUpdate()
            End Try
            If WasSearching Then ApplySavedFileListView()
            StatusLabel.Text = $"Path not found: \{String.Join("\", Segments)}\"
            StatusLabel.Owner?.Refresh()
            Return
        End If

        _CurrentDirectoryAnchorId = NodeAnchorId(Node)

        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        Try
            Node.EnsureVisible()
            If tvFolders.SelectedNode IsNot Node Then tvFolders.SelectedNode = Node
        Finally
            _ApplyingLocation = WasApplying
        End Try

        ' Snap the box to the folder's real path + casing, but not mid-edit (only a case/format tidy-up).
        Dim Canonical = BuildNodePath(Node)
        If tsiSearch.Focused = False AndAlso String.Equals(tsiSearch.Text, Canonical, StringComparison.Ordinal) = False Then
            _SuppressSearchText = True
            Try
                tsiSearch.Text = Canonical
            Finally
                _SuppressSearchText = False
            End Try
        End If

        RefreshCurrentDirectory()
        If WasSearching Then ApplySavedFileListView()
    End Sub

    ''' <summary>Resolves a folder path against the loaded tree (materialising as needed), or Nothing.</summary>
    Private Function EnsureNodeForPath(Segments As String()) As TreeNode
        If tvFolders.Nodes.Count = 0 Then Return Nothing
        Dim Node = tvFolders.Nodes(0)
        For Each Segment In Segments
            MaterialiseChildNodes(Node)
            Dim NextNode As TreeNode = Nothing
            For Each Child As TreeNode In Node.Nodes
                If String.Equals(Child.Text, Segment, StringComparison.OrdinalIgnoreCase) Then
                    NextNode = Child
                    Exit For
                End If
            Next
            If NextNode Is Nothing Then Return Nothing
            Node = NextNode
        Next
        Return Node
    End Function

    ''' <summary>The '\A\B\' path of a folder node. The root is the empty string.</summary>
    Private Shared Function BuildNodePath(Node As TreeNode) As String
        If Node Is Nothing Then Return String.Empty
        Dim Parts As New List(Of String)()
        Dim Current = Node
        While Current IsNot Nothing AndAlso Current.Parent IsNot Nothing
            Parts.Add(Current.Text)
            Current = Current.Parent
        End While
        If Parts.Count = 0 Then Return String.Empty
        Parts.Reverse()
        Return "\" & String.Join("\", Parts) & "\"
    End Function

    Private Function BuildPathForAnchor(AnchorId As Long) As String
        If AnchorId = _FileSystem.RootAnchorId Then Return String.Empty
        Return BuildNodePath(EnsureDirectoryNode(AnchorId))
    End Function

    Private Function CurrentFolderPath() As String
        If _CurrentDirectoryAnchorId > 0 Then Return BuildPathForAnchor(_CurrentDirectoryAnchorId)
        Return BuildNodePath(tvFolders.SelectedNode)
    End Function

    ' ---- Search ------------------------------------------------------------------------------------

    Private NotInheritable Class SearchScope
        Public Sub New(AnchorId As Long, Path As String, Matcher As Func(Of String, Boolean), Recursive As Boolean)
            Me.AnchorId = AnchorId
            Me.Path = Path
            Me.Matcher = Matcher
            Me.Recursive = Recursive
        End Sub

        Public ReadOnly Property AnchorId As Long
        Public ReadOnly Property Path As String
        Public ReadOnly Property Matcher As Func(Of String, Boolean)
        Public ReadOnly Property Recursive As Boolean
    End Class

    Private Sub ShowSearchResults(Terms As List(Of QueryTerm))
        Dim WasSearching = _SearchActive
        _SearchActive = True

        If WasSearching = False Then ApplySavedSearchListView()
        EnsurePathColumn()

        Dim ScopeSegments = SharedScopeSegments(Terms)
        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        Try
            If ScopeSegments Is Nothing Then
                If tvFolders.SelectedNode IsNot Nothing Then tvFolders.SelectedNode = Nothing
            Else
                Dim ScopeNode = EnsureNodeForPath(ScopeSegments)
                tvFolders.SelectedNode = ScopeNode
                ScopeNode?.EnsureVisible()
            End If
        Finally
            _ApplyingLocation = WasApplying
        End Try

        If String.Equals(_LastSearchedText, _CurrentQueryText, StringComparison.OrdinalIgnoreCase) = False Then
            StartSearch(Terms)
        End If
        PopulateSearchList()
        UpdateStatus()
    End Sub

    ''' <summary>The path shared by every term, if they are all scoped to the same one folder; else Nothing.</summary>
    Private Shared Function SharedScopeSegments(Terms As List(Of QueryTerm)) As String()
        Dim WithPath = Terms.Where(Function(t) t.HasPath).ToList()
        If WithPath.Count = 0 OrElse WithPath.Count <> Terms.Count Then Return Nothing
        Dim First = WithPath(0).PathSegments
        For Each Term In WithPath
            If Term.PathSegments.SequenceEqual(First, StringComparer.OrdinalIgnoreCase) = False Then Return Nothing
        Next
        Return If(First.Length = 0, Nothing, First)
    End Function

    Private Sub StartSearch(Terms As List(Of QueryTerm))
        _SearchGeneration += 1
        _SearchResults.Clear()
        _LastSearchedText = _CurrentQueryText
        Dim Generation = _SearchGeneration

        Dim Scopes As New List(Of SearchScope)()
        For Each Term In Terms
            Dim Node = EnsureNodeForPath(Term.PathSegments)
            If Node Is Nothing Then Continue For
            Dim ScopeAnchor = NodeAnchorId(Node)
            If ScopeAnchor <= 0 Then ScopeAnchor = _FileSystem.RootAnchorId
            Dim Recursive = (Term.HasPath = False) OrElse (Term.ListsDirectContents = False)
            Scopes.Add(New SearchScope(ScopeAnchor, BuildNodePath(Node).TrimEnd("\"c), BuildNameMatcher(Term.Pattern), Recursive))
        Next

        If Scopes.Count = 0 Then
            _CompletedSearchGeneration = Generation
            Return
        End If
        System.Threading.ThreadPool.QueueUserWorkItem(Sub() RunSearch(Generation, Scopes))
    End Sub

    Private Sub RerunSearch()
        _LastSearchedText = Nothing
        ApplyAddress(_CurrentQueryText)
    End Sub

    ''' <summary>Compiles a name pattern: empty/"*" matches all, plain text is "contains", else an anchored glob.</summary>
    Private Shared Function BuildNameMatcher(Pattern As String) As Func(Of String, Boolean)
        If Pattern.Length = 0 OrElse Pattern = "*" Then Return Function(Name) True
        If Pattern.IndexOfAny({"*"c, "?"c}) < 0 Then
            Dim Needle = Pattern
            Return Function(Name) Name.IndexOf(Needle, StringComparison.OrdinalIgnoreCase) >= 0
        End If
        Dim RegexText = "^" & Regex.Escape(Pattern).Replace("\*", ".+").Replace("\?", ".") & "$"
        Dim Compiled As New Regex(RegexText, RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        Return Function(Name) Compiled.IsMatch(Name)
    End Function

    Private Const SearchResultFlushIntervalMs As Integer = 80

    Private Sub RunSearch(Generation As Integer, Scopes As List(Of SearchScope))
        Dim Seen As New HashSet(Of Long)()
        Dim Accumulated As New List(Of SearchHit)()
        Dim LastFlush = Environment.TickCount

        For Each Scope In Scopes
            Dim Pending As New Stack(Of KeyValuePair(Of Long, String))()
            Pending.Push(New KeyValuePair(Of Long, String)(Scope.AnchorId, Scope.Path))

            While Pending.Count > 0
                If Generation <> _SearchGeneration OrElse _Disposed Then Return
                Dim Current = Pending.Pop()

                Dim Entries As IReadOnlyList(Of EmbeddedFileSystem.ContentListEntry)
                Try
                    Entries = _FileSystem.GetDirectoryEntries(Current.Key)
                Catch
                    Continue While
                End Try

                Dim ContainingPath = If(Current.Value.Length = 0, "\", Current.Value)
                For Each Entry In Entries
                    If Scope.Matcher(Entry.Name) AndAlso Seen.Add(Entry.ChildAnchorId) Then
                        Accumulated.Add(New SearchHit(Entry, Current.Key, ContainingPath))
                    End If
                    If Scope.Recursive AndAlso Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory Then
                        Pending.Push(New KeyValuePair(Of Long, String)(Entry.ChildAnchorId, Current.Value & "\" & Entry.Name))
                    End If
                Next

                If Accumulated.Count > 0 AndAlso Environment.TickCount - LastFlush >= SearchResultFlushIntervalMs Then
                    If FlushSearchBatch(Generation, Accumulated) = False Then Return
                    Accumulated = New List(Of SearchHit)()
                    LastFlush = Environment.TickCount
                End If
            End While
        Next

        If Accumulated.Count > 0 Then FlushSearchBatch(Generation, Accumulated)

        Try
            BeginInvoke(Sub() OnSearchComplete(Generation))
        Catch ex As InvalidOperationException
        End Try
    End Sub

    ''' <summary>Marshals a batch of hits to the UI thread. Returns False if the form is gone.</summary>
    Private Function FlushSearchBatch(Generation As Integer, Batch As List(Of SearchHit)) As Boolean
        Try
            BeginInvoke(Sub() AddSearchBatch(Generation, Batch))
            Return True
        Catch ex As InvalidOperationException
            Return False
        End Try
    End Function

    Private Sub AddSearchBatch(Generation As Integer, Batch As List(Of SearchHit))
        If Generation <> _SearchGeneration Then Return
        _SearchResults.AddRange(Batch)
        If _SearchActive = False Then Return

        lvFiles.BeginUpdate()
        Try
            lvFiles.ListViewItemSorter = Nothing
            For Each Hit In Batch
                lvFiles.Items.Add(CreateSearchListItem(Hit))
            Next
            lvFiles.ListViewItemSorter = _ListSorter
            lvFiles.Sort()
        Finally
            lvFiles.EndUpdate()
        End Try
        UpdateStatus()
    End Sub

    Private Sub OnSearchComplete(Generation As Integer)
        If Generation <> _SearchGeneration Then Return
        _CompletedSearchGeneration = Generation
        UpdateStatus()
    End Sub

    Private Sub PopulateSearchList()
        lvFiles.BeginUpdate()
        Try
            lvFiles.ListViewItemSorter = Nothing
            lvFiles.Items.Clear()
            _SearchItemInfo.Clear()
            For Each Hit In _SearchResults
                lvFiles.Items.Add(CreateSearchListItem(Hit))
            Next
            lvFiles.ListViewItemSorter = _ListSorter
            lvFiles.Sort()
        Finally
            lvFiles.EndUpdate()
        End Try
    End Sub

    Private Function CreateSearchListItem(Hit As SearchHit) As ListViewItem
        Dim Entry = Hit.Entry
        Dim IsDirectory = Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory
        Dim Item = New ListViewItem(Entry.Name) With {.Tag = Entry}
        Item.SubItems.Add(If(IsDirectory, String.Empty, FormatByteLength(Entry.LengthOfDataAtEntry)))
        Item.SubItems.Add(GetEntryStateText(Entry.EntryType))
        Item.SubItems.Add(Hit.ParentPath)
        If Entry.EntryType = EmbeddedFileSystem.EntryTypes.PendingFile Then Item.ForeColor = Drawing.BlendColor(lvFiles.ForeColor, Color.Red)
        _SearchItemInfo(Item) = Hit
        Return Item
    End Function

    Private Sub EnsurePathColumn()
        If lvFiles.Columns.Contains(_PathColumn) = False Then lvFiles.Columns.Add(_PathColumn)
    End Sub

    Private Sub RemovePathColumn()
        If lvFiles.Columns.Contains(_PathColumn) = False Then Return
        lvFiles.Columns.Remove(_PathColumn)
        If _ListSorter.Column >= lvFiles.Columns.Count Then
            _ListSorter.Column = 0
            _ListSorter.Order = SortOrder.Ascending
        End If
    End Sub

    Private Sub OpenContainingFolder(Sender As Object, EventArgs As EventArgs) Handles tsiOpenContainingFolder.Click
        If lvFiles.SelectedItems.Count <> 1 Then Return
        Dim Hit As SearchHit = Nothing
        If _SearchItemInfo.TryGetValue(lvFiles.SelectedItems(0), Hit) = False Then Return

        Dim TargetName = Hit.Entry.Name
        SetAddressText(BuildPathForAnchor(Hit.ParentAnchorId))
        SelectListItemByName(TargetName)
    End Sub

    Private Sub SelectListItemByName(Name As String)
        For Each Item As ListViewItem In lvFiles.Items
            If String.Equals(Item.Text, Name, StringComparison.Ordinal) Then
                Item.Selected = True
                Item.Focused = True
                Item.EnsureVisible()
                lvFiles.Select()
                Return
            End If
        Next
    End Sub

    Private Sub lvFiles_SelectedIndexChanged(Sender As Object, EventArgs As EventArgs) Handles lvFiles.SelectedIndexChanged
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

    ' ===================================================================================================
    ' Context menus - built once so a menu is never momentarily empty (an empty ContextMenuStrip refuses
    ' to open at all, even for the gesture that emptied it), then shown with only the relevant items.
    ' ===================================================================================================

    Private Sub FileMenu_Open(Sender As Object, EventArgs As EventArgs) Handles tsiOpen.Click
        Dim Entries = GetSelectedEntries()
        If Entries.Count = 1 AndAlso IsDirectory(Entries(0)) Then NavigateToDirectory(Entries(0).ChildAnchorId)
    End Sub

    Private Sub MiViewXlThumb_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewXlThumb.Click
        SelectFileListView(View.LargeIcon, 256)
    End Sub

    Private Sub MiViewLgThumb_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewLgThumb.Click
        SelectFileListView(View.LargeIcon, 128)
    End Sub

    Private Sub MiViewLgIcon_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewLgIcon.Click
        SelectFileListView(View.LargeIcon, 0)
    End Sub

    Private Sub MiViewSmIcon_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewSmIcon.Click
        SelectFileListView(View.SmallIcon, 0)
    End Sub

    Private Sub MiViewList_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewList.Click
        SelectFileListView(View.List, 0)
    End Sub

    Private Sub MiViewDetails_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewDetails.Click
        SelectFileListView(View.Details, 0)
    End Sub

    Private Sub MiViewTiles_Click(Sender As Object, EventArgs As EventArgs) Handles tsiViewTiles.Click
        SelectFileListView(View.Tile, 0)
    End Sub

    Private Sub FileContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles FileContextMenu.Opening
        Dim Entries = GetSelectedEntries()
        Dim One = Entries.Count = 1
        Dim OneDir = One AndAlso IsDirectory(Entries(0))
        Dim Any = Entries.Count > 0

        tsiOpen.Available = OneDir
        tsiOpenContainingFolder.Available = _SearchActive AndAlso One
        tsiSaveAs.Available = Any
        tsiSaveAs.Text = If(OneDir, "Save Folder As...", If(One, "Save As...", "Save Selected To Folder..."))
        tsiRename.Available = One AndAlso _SearchActive = False
        tsiDelete.Available = Any
        tsiUploadFiles.Available = _SearchActive = False
        tsiUploadFolder.Available = _SearchActive = False
        tsiNewFolder.Available = _SearchActive = False
        tsiRefresh.Available = True
        tsiView.Available = True
        UpdateViewMenuChecks()

        TidySeparators(FileContextMenu)
    End Sub

    Private Sub FolderContextMenu_Opening(Sender As Object, EventArgs As CancelEventArgs) Handles FolderContextMenu.Opening
        If tvFolders.SelectedNode Is Nothing Then
            EventArgs.Cancel = True
            Return
        End If
        Dim IsRoot = GetSelectedDirectoryAnchorId() = _FileSystem.RootAnchorId

        tsiFolderOpen.Available = True
        tsiFolderUploadFiles.Available = True
        tsiFolderUploadFolder.Available = True
        tsiFolderNewFolder.Available = True
        tsiSaveFolderAs.Available = True
        tsiFolderRename.Available = IsRoot = False
        tsiDeleteFolder.Available = IsRoot = False
        tsiFolderRefresh.Available = True

        TidySeparators(FolderContextMenu)
    End Sub

    Private Sub ContextMenu_Closed(Sender As Object, EventArgs As ToolStripDropDownClosedEventArgs) _
            Handles FileContextMenu.Closed, FolderContextMenu.Closed
        Dim Menu = TryCast(Sender, ContextMenuStrip)
        If Menu IsNot Nothing Then MakeAllItemsAvailable(Menu.Items)
    End Sub

    ''' <summary>Restores every item so the menu is never queried while empty on the next gesture.</summary>
    Private Shared Sub MakeAllItemsAvailable(Items As ToolStripItemCollection)
        For Each Item As ToolStripItem In Items
            Item.Available = True
            Dim Parent = TryCast(Item, ToolStripMenuItem)
            If Parent IsNot Nothing AndAlso Parent.HasDropDownItems Then MakeAllItemsAvailable(Parent.DropDownItems)
        Next
    End Sub

    ''' <summary>Hides leading, trailing and doubled-up separators for the currently visible items.</summary>
    Private Shared Sub TidySeparators(Menu As ContextMenuStrip)
        Dim PreviousWasContent = False
        Dim LastSeparator As ToolStripSeparator = Nothing
        For Each Item As ToolStripItem In Menu.Items
            Dim Separator = TryCast(Item, ToolStripSeparator)
            If Separator IsNot Nothing Then
                Separator.Available = PreviousWasContent
                If Separator.Available Then
                    LastSeparator = Separator
                    PreviousWasContent = False
                End If
            ElseIf Item.Available Then
                PreviousWasContent = True
                LastSeparator = Nothing
            End If
        Next
        If LastSeparator IsNot Nothing Then LastSeparator.Available = False
    End Sub

    Private Sub UpdateViewMenuChecks()
        tsiViewXlThumb.Checked = _ThumbnailCellSize = 256
        tsiViewLgThumb.Checked = _ThumbnailCellSize = 128
        tsiViewLgIcon.Checked = _ThumbnailCellSize = 0 AndAlso lvFiles.View = View.LargeIcon
        tsiViewSmIcon.Checked = _ThumbnailCellSize = 0 AndAlso lvFiles.View = View.SmallIcon
        tsiViewList.Checked = _ThumbnailCellSize = 0 AndAlso lvFiles.View = View.List
        tsiViewDetails.Checked = _ThumbnailCellSize = 0 AndAlso lvFiles.View = View.Details
        tsiViewTiles.Checked = _ThumbnailCellSize = 0 AndAlso lvFiles.View = View.Tile
    End Sub

    ''' <summary>
    ''' Applies a file-list view and remembers it (view mode plus thumbnail cell size). A search has its
    ''' own persisted view state (<see cref="My.MySettings.SearchListView"/>) separate from a folder's.
    ''' </summary>
    Private Sub SelectFileListView(TargetView As View, ThumbnailCellSize As Integer)
        SetFileListView(TargetView, ThumbnailCellSize)
        Try
            If _SearchActive Then
                My.Settings.SearchListView = SerializeFileListView(TargetView, ThumbnailCellSize)
            Else
                My.Settings.FileListView = SerializeFileListView(TargetView, ThumbnailCellSize)
            End If
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
        ApplySavedListView(Saved, View.Details)
    End Sub

    ''' <summary>Applies the search result view, which defaults to a plain list.</summary>
    Private Sub ApplySavedSearchListView()
        Dim Saved As String = Nothing
        Try
            Saved = My.Settings.SearchListView
        Catch
        End Try
        ApplySavedListView(Saved, View.List)
    End Sub

    Private Sub ApplySavedListView(Saved As String, Fallback As View)
        Select Case Saved
            Case "Thumbnail256" : SetFileListView(View.LargeIcon, 256)
            Case "Thumbnail128" : SetFileListView(View.LargeIcon, 128)
            Case "LargeIcon" : SetFileListView(View.LargeIcon, 0)
            Case "SmallIcon" : SetFileListView(View.SmallIcon, 0)
            Case "List" : SetFileListView(View.List, 0)
            Case "Tile" : SetFileListView(View.Tile, 0)
            Case "Details" : SetFileListView(View.Details, 0)
            Case Else : SetFileListView(Fallback, 0)
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
                lvFiles.LargeImageList = Thumbnail256Sizer
            ElseIf ThumbnailCellSize > 0 Then
                lvFiles.LargeImageList = Thumbnail128Sizer
            Else
                lvFiles.LargeImageList = Large32Sizer
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

        If EventArgs.ColumnIndex = 0 AndAlso IsListItemBeingEdited(EventArgs.Item) Then Return

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

    ''' <summary>True while the native rename box is open over this row, so owner-draw leaves its label alone.</summary>
    Private Function IsListItemBeingEdited(Item As ListViewItem) As Boolean
        Return _EditingListItemIndex >= 0 AndAlso Item IsNot Nothing AndAlso Item.Index = _EditingListItemIndex
    End Function

    Private Sub DrawIconViewLabel(Canvas As Graphics, Item As ListViewItem, LabelArea As Rectangle, Flags As TextFormatFlags)
        If IsListItemBeingEdited(Item) Then Return
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
                Canvas.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
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
        If IsListItemBeingEdited(EventArgs.Item) = False Then
            TextRenderer.DrawText(Canvas, EventArgs.Item.Text, lvFiles.Font, LabelArea, lvFiles.ForeColor,
                                  TextFormatFlags.HorizontalCenter Or TextFormatFlags.WordEllipsis Or TextFormatFlags.NoPrefix)
        End If

        If EventArgs.Item.Focused Then
            'EventArgs.DrawFocusRectangle()
            Using Border As New Pen(SystemColors.Highlight)
                Canvas.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
            End Using
        End If
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

        Dim X = ThumbnailRectangle.Right - Badge.Width - 2
        Dim Y = ThumbnailRectangle.Bottom - Badge.Height - 2
        'Using Backing As New SolidBrush(Color.FromArgb(210, Color.White))
        '    Canvas.FillRectangle(Backing, X - 1, Y - 1, Badge.Width + 2, Badge.Height + 2)
        'End Using
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

        ' See RequestExecutableIcon: GenerateThumbnail discards its result and leaves the anchor
        ' pending if the handle is not up yet, so don't queue it before then.
        If IsHandleCreated = False Then Return Nothing

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

    Private Sub OpenSelectedDirectory(Sender As Object, EventArgs As EventArgs) Handles tsiFolderOpen.Click
        If tvFolders.SelectedNode Is Nothing Then Return
        tvFolders.SelectedNode.Expand()
        SetAddressText(BuildNodePath(tvFolders.SelectedNode))
    End Sub

    Private Sub UploadFilesFromDialog(Sender As Object, EventArgs As EventArgs) Handles tsiUploadFiles.Click, tsiFolderUploadFiles.Click
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

    Private Sub UploadFolderFromDialog(Sender As Object, EventArgs As EventArgs) Handles tsiUploadFolder.Click, tsiFolderUploadFolder.Click
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
                                   TotalSize As TotalSizeBox, Report As frmProgress.ProgressReport)
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
    Private Shared Sub ReportUploadProgress(Report As frmProgress.ProgressReport, TotalSize As TotalSizeBox,
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

    Private Shared Function PromptForConflictChoice(Report As frmProgress.ProgressReport, Name As String) As ConflictChoice
        Dim Choice As ConflictChoice = ConflictChoice.Cancel
        Dim Buttons As New List(Of MessageBox.MsgBoxButton) From {
            New MessageBox.MsgBoxButton("Skip", Sub() Choice = ConflictChoice.Skip),
            New MessageBox.MsgBoxButton("Skip All", Sub() Choice = ConflictChoice.SkipAll),
            New MessageBox.MsgBoxButton("Replace", Sub() Choice = ConflictChoice.Replace),
            New MessageBox.MsgBoxButton("Replace All", Sub() Choice = ConflictChoice.ReplaceAll),
            New MessageBox.MsgBoxButton("Cancel", Sub() Choice = ConflictChoice.Cancel)
        }

        Report.ShowMessageBox($"'{Name}' already exists in the destination folder.{Environment.NewLine}What would you like to do?",
                              MsgBoxStyle.Exclamation, "Replace File", Buttons)
        Return Choice
    End Function

    Private Sub SaveSelectedEntries(Sender As Object, EventArgs As EventArgs) Handles tsiSaveAs.Click
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

    Private Sub SaveSelectedFolder(Sender As Object, EventArgs As EventArgs) Handles tsiSaveFolderAs.Click
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

    Private Sub DeleteSelectedEntries(Sender As Object, EventArgs As EventArgs) Handles tsiDelete.Click
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
        If MsgBox(Me, Prompt, MsgBoxStyle.YesNo Or MsgBoxStyle.Exclamation) <> MsgBoxResult.Yes Then Return

        ' Resolve each item's parent on the UI thread (a search result lives outside the current folder).
        Dim Deletions = lvFiles.SelectedItems.
                                Cast(Of ListViewItem)().
                                Select(Function(item)
                                           Dim entry = TryCast(item.Tag, EmbeddedFileSystem.ContentListEntry)
                                           Dim hit As SearchHit = Nothing
                                           Dim parentId = If(_SearchItemInfo.TryGetValue(item, hit), hit.ParentAnchorId, _CurrentDirectoryAnchorId)
                                           Return New KeyValuePair(Of Long, String)(parentId, If(entry Is Nothing, Nothing, entry.Name))
                                       End Function).
                                Where(Function(x) x.Value IsNot Nothing).
                                ToList()

        ExecuteLongBlockingActionOnThread(
            Sub()
                For Each deletion In Deletions
                    _FileSystem.DeleteEntry(deletion.Key, deletion.Value)
                Next
            End Sub,
            "One or more items could not be deleted.")

        If _SearchActive Then RerunSearch() Else RefreshFileSystemView()
    End Sub

    Private Sub DeleteSelectedFolder(Sender As Object, EventArgs As EventArgs) Handles tsiDeleteFolder.Click
        Dim SelectedNode = tvFolders.SelectedNode
        If SelectedNode Is Nothing OrElse SelectedNode.Parent Is Nothing Then Return

        Dim Info = TryCast(SelectedNode.Tag, DirectoryNodeInfo)
        Dim ParentInfo = TryCast(SelectedNode.Parent.Tag, DirectoryNodeInfo)
        If Info Is Nothing OrElse ParentInfo Is Nothing Then Return

        If MsgBox(Me,
                  $"Delete '{Info.Name}' and all of its contents?",
                  MsgBoxStyle.YesNo Or MsgBoxStyle.Exclamation
                  ) <> MsgBoxResult.Yes Then Return

        Dim DeletedAnchorId = Info.AnchorId
        Dim ParentAnchorId = ParentInfo.AnchorId

        ExecuteLongBlockingActionOnThread(
            Sub() _FileSystem.DeleteEntry(ParentAnchorId, Info.Name),
            "The folder could not be deleted.")

        RemoveDirectoryFromTree(ParentAnchorId, DeletedAnchorId)
        SetAddressText(BuildPathForAnchor(ParentAnchorId))
    End Sub

    Private Sub RefreshMenuItem_Click(Sender As Object, EventArgs As EventArgs) Handles tsiRefresh.Click, tsiFolderRefresh.Click
        If _SearchActive Then RerunSearch() Else RefreshFileSystemView()
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

    ' On right-click the menu is shown explicitly (a ContextMenuStrip that opens itself can be beaten by a
    ' still-empty menu); dragging goes through the ItemDrag event, which fires after the control has done
    ' its own selection so "nothing selected yet" is no longer a problem.

    Private Sub lvFiles_MouseDown(Sender As Object, EventArgs As MouseEventArgs) Handles lvFiles.MouseDown
        If EventArgs.Button <> MouseButtons.Right Then Return

        Dim HitItem = lvFiles.GetItemAt(EventArgs.X, EventArgs.Y)
        If HitItem IsNot Nothing Then
            If HitItem.Selected = False Then
                lvFiles.SelectedItems.Clear()
                HitItem.Selected = True
                HitItem.Focused = True
            End If
        Else
            lvFiles.SelectedItems.Clear()
        End If
        FileContextMenu.Show(lvFiles, EventArgs.Location)
    End Sub

    Private Sub lvFiles_ItemDrag(Sender As Object, EventArgs As ItemDragEventArgs) Handles lvFiles.ItemDrag
        If EventArgs.Button <> MouseButtons.Left Then Return
        Dim Item = TryCast(EventArgs.Item, ListViewItem)
        If Item IsNot Nothing AndAlso Item.Selected = False Then
            lvFiles.SelectedItems.Clear()
            Item.Selected = True
        End If
        Dim Entries = GetSelectedEntries()
        If Entries.Count = 0 Then Return
        BeginExternalFileDrag(Entries)
    End Sub

    Private Sub tvFolders_MouseDown(Sender As Object, EventArgs As MouseEventArgs) Handles tvFolders.MouseDown
        If EventArgs.Button <> MouseButtons.Right Then Return
        Dim Node = tvFolders.GetNodeAt(EventArgs.Location)
        If Node Is Nothing Then Return
        tvFolders.SelectedNode = Node
        FolderContextMenu.Show(tvFolders, EventArgs.Location)
    End Sub

    Private Sub tvFolders_ItemDrag(Sender As Object, EventArgs As ItemDragEventArgs) Handles tvFolders.ItemDrag
        If EventArgs.Button <> MouseButtons.Left Then Return
        Dim Node = TryCast(EventArgs.Item, TreeNode)
        Dim Info = TryCast(Node?.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return
        tvFolders.SelectedNode = Node
        BeginExternalDirectoryDrag(Info)
    End Sub

    ''' <summary>One file or folder being dragged out, plus what a Move needs in order to delete it.</summary>
    Private NotInheritable Class DraggedEntry
        Public Sub New(Name As String, IsDirectory As Boolean, ContentAnchorId As Long, Length As Long, ParentAnchorId As Long)
            Me.Name = Name
            Me.IsDirectory = IsDirectory
            Me.ContentAnchorId = ContentAnchorId
            Me.Length = Length
            Me.ParentAnchorId = ParentAnchorId
        End Sub

        Public ReadOnly Property Name As String
        Public ReadOnly Property IsDirectory As Boolean
        Public ReadOnly Property ContentAnchorId As Long
        Public ReadOnly Property Length As Long
        Public ReadOnly Property ParentAnchorId As Long
    End Class

    Private Sub BeginExternalFileDrag(Entries As IList(Of EmbeddedFileSystem.ContentListEntry))
        If Entries.Count = 0 Then Return
        Dim Dragged = Entries.Select(Function(e) New DraggedEntry(e.Name, IsDirectory(e), e.ChildAnchorId,
                                                                 e.LengthOfDataAtEntry, ResolveParentAnchor(e))).ToList()
        StartStreamedDrag(Dragged)
    End Sub

    Private Sub BeginExternalDirectoryDrag(Info As DirectoryNodeInfo)
        Dim Node = FindDirectoryNode(Info.AnchorId)
        Dim ParentInfo = TryCast(Node?.Parent?.Tag, DirectoryNodeInfo)
        Dim Dragged As New List(Of DraggedEntry) From {
            New DraggedEntry(Info.Name, True, Info.AnchorId, 0, If(ParentInfo IsNot Nothing, ParentInfo.AnchorId, 0L))
        }
        StartStreamedDrag(Dragged)
    End Sub

    ''' <summary>The parent directory anchor for a listed entry - the containing folder during a search.</summary>
    Private Function ResolveParentAnchor(Entry As EmbeddedFileSystem.ContentListEntry) As Long
        If _SearchActive Then
            Dim Hit = _SearchResults.FirstOrDefault(Function(h) h.Entry.ChildAnchorId = Entry.ChildAnchorId)
            If Hit IsNot Nothing Then Return Hit.ParentAnchorId
        End If
        Return _CurrentDirectoryAnchorId
    End Function

    ''' <summary>
    ''' Drags <paramref name="Dragged"/> out to the drop target, streaming each file's bytes straight
    ''' from the embedded stream on demand - no temp files, no whole-file buffering. Dropping copies;
    ''' dropping with Shift held moves (the entries are removed once the transfer finishes).
    ''' </summary>
    ''' <remarks>
    ''' The shell pulls the file contents on its own thread after the drop; if you set a breakpoint inside
    ''' <see cref="StreamEmbeddedContent"/> or the completion callbacks while debugging, the transfer can
    ''' stall. Otherwise it works fine with the debugger attached.
    ''' </remarks>
    Private Sub StartStreamedDrag(Dragged As List(Of DraggedEntry))
        If Dragged.Count = 0 Then Return

        Dim Descriptors As New List(Of VirtualFileDataObject.FileDescriptor)()
        For Each Item In Dragged
            If Item.IsDirectory Then
                AppendDirectoryDescriptors(Item.ContentAnchorId, Item.Name, Descriptors)
            Else
                Descriptors.Add(BuildFileDescriptor(Item.Name, Item.ContentAnchorId, Item.Length))
            End If
        Next
        If Descriptors.Count = 0 Then Return

        Dim CapturedSources = Dragged
        Dim Data As New VirtualFileDataObject(Sub(o)
                                              End Sub,
                                              Sub(o) OnStreamedDragFinished(o, CapturedSources))
        Data.SetData(Descriptors)

        Try
            VirtualFileDataObject.DoDragDrop(Data, DragDropEffects.Copy Or DragDropEffects.Move)
        Catch ex As Exception
            MsgBox(Me, $"The drag could not be started.{Environment.NewLine}{ex.Message}", MsgBoxStyle.Critical)
        End Try
    End Sub

    Private Function BuildFileDescriptor(RelativeName As String, ContentAnchorId As Long, Length As Long) As VirtualFileDataObject.FileDescriptor
        Return New VirtualFileDataObject.FileDescriptor With {
            .Name = RelativeName,
            .Length = Length,
            .StreamContents = Sub(Output) StreamEmbeddedContent(ContentAnchorId, Length, Output)
        }
    End Function

    ''' <summary>Recursively emits a descriptor per contained file, keyed by its path within the folder.</summary>
    Private Sub AppendDirectoryDescriptors(DirectoryAnchorId As Long, RelativePath As String,
                                           Descriptors As List(Of VirtualFileDataObject.FileDescriptor))
        Dim Entries As IReadOnlyList(Of EmbeddedFileSystem.ContentListEntry)
        Try
            Entries = _FileSystem.GetDirectoryEntries(DirectoryAnchorId)
        Catch
            Return
        End Try

        For Each Entry In Entries
            Dim ChildPath = $"{RelativePath}\{Entry.Name}"
            If Entry.EntryType = EmbeddedFileSystem.EntryTypes.Directory Then
                AppendDirectoryDescriptors(Entry.ChildAnchorId, ChildPath, Descriptors)
            ElseIf Entry.EntryType = EmbeddedFileSystem.EntryTypes.File Then
                Descriptors.Add(BuildFileDescriptor(ChildPath, Entry.ChildAnchorId, Entry.LengthOfDataAtEntry))
            End If
        Next
    End Sub

    ''' <summary>Feeds one embedded file's bytes into the drop target's stream. Runs on the shell's thread.</summary>
    Private Sub StreamEmbeddedContent(ContentAnchorId As Long, Length As Long, Output As Stream)
        Using Source = EmbeddedFileReadStream.TryOpen(_FileSystem, ContentAnchorId, Length)
            If Source Is Nothing Then Return
            Source.CopyTo(Output, 1024 * 1024)
        End Using
    End Sub

    Private Sub OnStreamedDragFinished(Data As VirtualFileDataObject, Dragged As List(Of DraggedEntry))
        If Data.PerformedDropEffect.GetValueOrDefault() <> DragDropEffects.Move Then Return
        Try
            BeginInvoke(Sub() CompleteStreamedDragMove(Dragged))
        Catch
            ' The form is gone; nothing to remove.
        End Try
    End Sub

    Private Sub CompleteStreamedDragMove(Dragged As List(Of DraggedEntry))
        ExecuteLongBlockingActionOnThread(
            Sub()
                For Each Item In Dragged
                    Try
                        _FileSystem.DeleteEntry(Item.ParentAnchorId, Item.Name)
                    Catch
                        ' Already gone, or its folder changed since the drag - leave it.
                    End Try
                Next
            End Sub,
            "The moved items could not all be removed from the embedded file system.")

        If _SearchActive Then RerunSearch() Else RefreshFileSystemView()
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

    ''' <summary>
    ''' Form-wide navigation shortcuts. Handled here rather than in <see cref="BrowserForm_KeyDown"/> so
    ''' they still fire when focus is inside the tree, the list, or a toolbar control (which otherwise
    ''' swallow Alt+Arrow and the arrow keys before <c>KeyDown</c> bubbles to the form).
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef Msg As Message, KeyData As Keys) As Boolean
        Select Case KeyData
            Case Keys.Alt Or Keys.Left
                GoBack()
                Return True
            Case Keys.Alt Or Keys.Right
                GoForward()
                Return True
            Case Keys.Control Or Keys.F
                FocusSearchBox()
                Return True
        End Select
        Return MyBase.ProcessCmdKey(Msg, KeyData)
    End Function

    Private Sub BrowserForm_KeyDown(Sender As Object, EventArgs As KeyEventArgs) Handles Me.KeyDown
        If EventArgs.KeyCode = Keys.F5 Then
            RefreshMenuItem_Click(Me, EventArgs)
            EventArgs.Handled = True
            Return
        End If

        If EventArgs.KeyCode = Keys.F2 Then
            If tvFolders.Focused AndAlso tvFolders.SelectedNode IsNot Nothing Then
                RenameSelectedFolder(Me, EventArgs)
                EventArgs.Handled = True
            ElseIf lvFiles.Focused AndAlso lvFiles.SelectedItems.Count = 1 AndAlso _SearchActive = False Then
                RenameSelectedListEntry(Me, EventArgs)
                EventArgs.Handled = True
            End If
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

    Private Sub ExecuteLongBlockingActionOnThread(Operation As Action(Of frmProgress.ProgressReport), ErrorMessage As String)
        Using ProgressForm As New frmProgress(
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

        ' Signals any executable-icon, thumbnail, or search worker still in flight to drop its result.
        _Disposed = True
        _ThumbnailGeneration += 1
        _SearchGeneration += 1

        If Disposing Then
            If components IsNot Nothing Then components.Dispose()
            _SearchTimer.Dispose()
            _PathColumn.Dispose()
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
        Using frmProgress As New frmProgress(
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
                    MsgBox(ProgressReport.frmProgress, $"Partial Files Removed: {Removed}{Environment.NewLine}Validation: {If(ValidationException Is Nothing, "Pass", $"{ValidationException.GetType.Name}: {ValidationException.Message}")}", Icon)
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
        Using frmProgress As New frmProgress(
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
                            If CurrentTime.Subtract(LastUpdate).TotalSeconds >= 2.5 OrElse Done Then
                                LastUpdate = CurrentTime
                                Dim S = FileSystem.ChunkedStream.GetStructure()
                                pnlDefrag.BackgroundImage = S.GenerateFragmentationBitmap(pnlDefrag.ClientSize.Width, 1)
                            End If
                        End Sub)

                    'Dim Struct = FileSystem.ChunkedStream.GetStructure()
                    'For Each rr In Struct.Regions
                    '    Debug.Print($"{rr}")
                    'Next
                    'Dim Hc = Struct.Regions.Where(Function(x) x.RegionType = ChunkedStreamStructure.RegionTypes.Hole).Count

                    MsgBox(ProgressReport.frmProgress, $"Saved: {Saved.FormatFileSizeFromBytes}{Environment.NewLine}Fragmentation: {OldFragmentation:P0} > {FileSystem.ChunkedStream.GetFragmentation():P0}", MsgBoxStyle.Information)
                End Sub, Nothing)

            frmProgress.ShowInTaskbar = True
            frmProgress.ShowDialog(Me)
            frmProgress.Text = "Defragmenting"
        End Using

        RefreshFileSystemView()
    End Sub

    Private Sub EmbeddedFileSystemBrowserForm_Load(sender As Object, e As EventArgs) Handles Me.Load
        Dim tsam As New ToolStripAsMenu(tsMain)
    End Sub

    Private Sub tsiSearch_Click(sender As Object, e As EventArgs) Handles tsiSearch.Click

    End Sub
End Class
