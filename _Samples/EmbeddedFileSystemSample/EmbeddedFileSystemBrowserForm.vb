Imports i00.Streams
Imports System.ComponentModel
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Runtime.InteropServices
Imports System.Text.RegularExpressions
Imports EmbeddedFileSystemSample.VirtualDragCopyFiles

Partial Public NotInheritable Class EmbeddedFileSystemBrowserForm

    Public Event MutatedFileSystem As EventHandler
    Protected Sub OnMutatedFileSystem()
        RaiseEvent MutatedFileSystem(Me, EventArgs.Empty)
    End Sub

    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SetWindowTheme(hWnd As IntPtr, pszSubAppName As String, pszSubIdList As String) As Integer
    End Function

    <DllImport("user32.dll")>
    Private Shared Function SendMessage(Handle As IntPtr, Message As Integer, WParam As IntPtr, LParam As IntPtr) As IntPtr
    End Function

    Private Const LvmSetExtendedListViewStyle As Integer = &H1000 + 54
    Private Const LvsExDoubleBuffer As Integer = &H10000

    '''' <summary>Extensions previewed as thumbnails in the thumbnail views (all GDI+-decodable).</summary>
    'Private Shared ReadOnly ThumbnailImageExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
    '    ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico"
    '}
    Private Shared ReadOnly Property ThumbnailImageExtensions As HashSet(Of String)
        Get
            Static _Returner As HashSet(Of String)
            If _Returner Is Nothing Then
                _Returner = New HashSet(Of String)(
                                Imaging.ImageCodecInfo.GetImageDecoders().
                                        SelectMany(Function(x) x.FilenameExtension.Split(";"c)).
                                        Select(Function(x) IO.Path.GetExtension(x)).
                                        Distinct(), StringComparer.OrdinalIgnoreCase)
            End If
            Return _Returner
        End Get
    End Property


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

    Private Class FileUploadWorkItem
        Inherits UploadWorkItem

        Private ReadOnly SourcePath As String
        Public Sub New(SourcePath As String, RelativeParent As String)
            MyBase.New(RelativeParent,
                       IO.Path.GetFileName(SourcePath),
                       File.GetAttributes(SourcePath).HasFlag(FileAttributes.Directory))
            Me.SourcePath = SourcePath
        End Sub

        Public Overrides ReadOnly Property LogicalSize As Long
            Get
                Static _LogicalSize As Long = SafeFileLength(SourcePath)
                Return _LogicalSize
            End Get
        End Property

        Private Shared Function SafeFileLength(FilePath As String) As Long
            Try
                Return New FileInfo(FilePath).Length
            Catch
                Return 0
            End Try
        End Function

        Public Overrides Function CreateStream() As Stream
            Return New FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)
        End Function
    End Class

    ''' <summary>One resolved copy step produced by walking the dropped or picked paths.</summary>
    Private MustInherit Class UploadWorkItem
        Public Sub New(RelativeParent As String, Name As String, IsDirectory As Boolean)
            Me.RelativeParent = RelativeParent
            Me.Name = Name
            Me.IsDirectory = IsDirectory
        End Sub

        Public MustOverride ReadOnly Property LogicalSize As Long
        Public MustOverride Function CreateStream() As Stream
        ''' <summary>'\'-joined parent directories relative to the upload target, or "" for the target itself.</summary>
        Public ReadOnly Property RelativeParent As String
        Public ReadOnly Property Name As String
        Public ReadOnly Property IsDirectory As Boolean
    End Class

    ''' <summary>One entry snapshotted from a zip's central directory by <see cref="ReadZipDirectory"/>.</summary>
    Private NotInheritable Class ZipDirectoryEntry
        Public Sub New(FullName As String, Length As Long, IsDirectory As Boolean)
            Me.FullName = FullName
            Me.Length = Length
            Me.IsDirectory = IsDirectory
        End Sub

        ''' <summary>The entry's path within the archive, exactly as stored ('/'-separated, may end in '/').</summary>
        Public ReadOnly Property FullName As String
        ''' <summary>Uncompressed size in bytes; 0 for a directory entry.</summary>
        Public ReadOnly Property Length As Long
        Public ReadOnly Property IsDirectory As Boolean
    End Class

    ''' <summary>
    ''' One upload step sourced from a zip entry. A directory step just carries its target name; a file
    ''' step remembers its archive path and entry name and reopens the archive in <see cref="CreateStream"/>
    ''' every time its bytes are needed, so the copy pass and the background size scan never contend
    ''' over a single archive handle.
    ''' </summary>
    Private NotInheritable Class ZipUploadWorkItem
        Inherits UploadWorkItem

        Private ReadOnly ArchivePath As String
        Private ReadOnly EntryFullName As String
        Private ReadOnly _LogicalSize As Long

        ''' <summary>A directory step: create <paramref name="Name"/> under <paramref name="RelativeParent"/>.</summary>
        Public Sub New(RelativeParent As String, Name As String)
            MyBase.New(RelativeParent, Name, True)
        End Sub

        ''' <summary>A file step: copy the entry named <paramref name="EntryFullName"/> out of <paramref name="ArchivePath"/>.</summary>
        Public Sub New(ArchivePath As String, EntryFullName As String, RelativeParent As String, Name As String, Length As Long)
            MyBase.New(RelativeParent, Name, False)
            Me.ArchivePath = ArchivePath
            Me.EntryFullName = EntryFullName
            Me._LogicalSize = Length
        End Sub

        Public Overrides ReadOnly Property LogicalSize As Long
            Get
                Return _LogicalSize
            End Get
        End Property

        Public Overrides Function CreateStream() As Stream
            Dim Archive = ZipFile.OpenRead(ArchivePath)
            Try
                Dim Entry = Archive.GetEntry(EntryFullName)
                If Entry Is Nothing Then
                    Throw New FileNotFoundException($"'{EntryFullName}' is no longer present in '{Path.GetFileName(ArchivePath)}'.")
                End If
                Return New ZipEntryReadStream(Archive, Entry.Open(), Entry.Length)
            Catch
                Archive.Dispose()
                Throw
            End Try
        End Function
    End Class

    ''' <summary>
    ''' Wraps the forward-only, unknown-length stream that <see cref="ZipArchiveEntry.Open"/> returns
    ''' for a compressed entry so it reports the entry's uncompressed <see cref="Length"/> - which the
    ''' copy loop reads to size its write buffer - and disposes the owning <see cref="ZipArchive"/> once
    ''' the copy closes it.
    ''' </summary>
    Private NotInheritable Class ZipEntryReadStream
        Inherits Stream

        Private ReadOnly _Archive As ZipArchive
        Private ReadOnly _Inner As Stream
        Private ReadOnly _Length As Long
        Private _Position As Long

        Public Sub New(Archive As ZipArchive, Inner As Stream, Length As Long)
            _Archive = Archive
            _Inner = Inner
            _Length = Length
        End Sub

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return _Length
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return _Position
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property

        Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
            Dim BytesRead = _Inner.Read(Buffer, Offset, Count)
            _Position += BytesRead
            Return BytesRead
        End Function

        Public Overrides Sub Flush()
        End Sub

        Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function

        Public Overrides Sub SetLength(Value As Long)
            Throw New NotSupportedException()
        End Sub

        Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
            Throw New NotSupportedException()
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            Try
                If disposing Then
                    _Inner.Dispose()
                    _Archive.Dispose()
                End If
            Finally
                MyBase.Dispose(disposing)
            End Try
        End Sub
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
    Private ReadOnly _History As New List(Of AddressHistoryEntry)()
    Private ReadOnly _SearchResults As New List(Of SearchHit)()
    Private ReadOnly _SearchItemInfo As New Dictionary(Of ListViewItem, SearchHit)()
    Private ReadOnly _PathColumn As New ColumnHeader() With {.Text = "Path", .Width = 260}
    'Private WithEvents _SearchTimer As New System.Windows.Forms.Timer() With {.Interval = 400}
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
        OnMutatedFileSystem()

        _CurrentDirectoryAnchorId = _FileSystem.RootAnchorId

        InitializeComponent()

        tvFolders.ImageList = _IconProvider.TreeImages
        tvFolders.LabelEdit = True
        lvFiles.ListViewItemSorter = _ListSorter
        lvFiles.LabelEdit = True

        ' The menus (defined in the designer) are shown explicitly from the MouseDown handlers.
        lvFiles.ContextMenuStrip = Nothing
        tvFolders.ContextMenuStrip = Nothing

        Dim MouseNavigation = New MouseNavigationButtonsMessageFilter(Me,
            Sub() GoBack(),
            Sub() GoForward())

        Try
            'SetWindowTheme(tvFolders.Handle, "Explorer", Nothing)
            'SetWindowTheme(lvFiles.Handle, "Explorer", Nothing)
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
        'Dim Struct = FileSystem.ChunkedStream.GetStructure()

        'tsiFragmentation.Text = $"Fragmentation: {Struct.FragmentationRatio:P0}{vbCrLf}Click to defrag"
        'tsiCompression.Text = $"File size: {Struct.PhysicalLength.FormatFileSizeFromBytes()} " &
        '                      $"Data size: {Struct.LogicalLength.FormatFileSizeFromBytes()} " &
        '                      $"Fragmented waste: {Struct.FragmentedBytes.FormatFileSizeFromBytes()} "
        tsiFragmentation.Text = $"Fragmentation: {FileSystem.ChunkedStream.GetFragmentation():P0}"
        tsiFragmentation.ToolTipText = $"Click to defrag"
        tsiCompression.Text = $"File size: {FileSystem.ChunkedStream.Length.FormatFileSizeFromBytes()} " &
                              $"Data size: {FileSystem.ChunkedStream.BaseStream.Length.FormatFileSizeFromBytes()}"

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
        _LabelEditRequested = True
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
        Dim WasApplying = _ApplyingLocation
        _ApplyingLocation = True
        Try
            ' Removing the selected node can itself make the TreeView jump its selection to
            ' whatever comes next, firing a real AfterSelect before we get to reselect Parent
            ' below - guard the removal too, not just the reselection.
            Node.Remove()
            If Parent IsNot Nothing Then tvFolders.SelectedNode = Parent
        Finally
            _ApplyingLocation = WasApplying
        End Try
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

        ' Navigate into the new folder, same as if the user had clicked it in the tree.
        NavigateToDirectory(NewId)
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
            StatusLabel.Text = $"Search for '{_CurrentQueryText}': {lvFiles.Items.Count:N0} result{If(lvFiles.Items.Count = 1, "", "s")}{ProgressText}{SelectionText}"
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

    ''' <summary>A visited address plus the file-list selection/scroll position it last had, so
    ''' Back/Forward can restore the view exactly as it was left rather than always resetting it.
    ''' Keyed by anchor ID rather than name - a search result list can show several entries that
    ''' share the same name from different folders.</summary>
    Private NotInheritable Class AddressHistoryEntry
        Public Sub New(Text As String)
            Me.Text = Text
        End Sub

        Public Property Text As String
        Public Property SelectedAnchors As List(Of Long)
        Public Property FocusedAnchor As Long

        ' I removed this as setting the top item does not work in certain views
        'Public Property TopItemAnchor As Long
        Public Property ScrollPosition As Point
    End Class

    ''' <summary>Sets the box text from code and applies it at once, pushing a history entry.</summary>
    Private Sub SetAddressText(Text As String)
        '_SearchTimer.Stop()
        _LastPushWasTyped = False
        _SuppressSearchText = True
        Try
            tsiSearch.Text = Text
        Finally
            _SuppressSearchText = False
        End Try
        CaptureCurrentViewState()
        PushAddressHistory(Text)
        ApplyAddress(Text)
        UpdateNavigationButtons()
    End Sub

    Private Sub PushAddressHistory(Text As String)
        If _HistoryIndex >= 0 AndAlso String.Equals(_History(_HistoryIndex).Text, Text, StringComparison.OrdinalIgnoreCase) Then Return
        If _HistoryIndex < _History.Count - 1 Then _History.RemoveRange(_HistoryIndex + 1, _History.Count - _HistoryIndex - 1)
        _History.Add(New AddressHistoryEntry(Text))
        _HistoryIndex = _History.Count - 1
    End Sub

    Private Sub GoBack()
        If _HistoryIndex <= 0 Then Return
        CaptureCurrentViewState()
        _HistoryIndex -= 1
        ApplyHistoryEntry()
    End Sub

    Private Sub GoForward()
        If _HistoryIndex >= _History.Count - 1 Then Return
        CaptureCurrentViewState()
        _HistoryIndex += 1
        ApplyHistoryEntry()
    End Sub

    Private Sub ApplyHistoryEntry()
        '_SearchTimer.Stop()

        _LastPushWasTyped = False
        Dim Entry = _History(_HistoryIndex)
        _SuppressSearchText = True
        Try
            tsiSearch.Text = Entry.Text
        Finally
            _SuppressSearchText = False
        End Try

        ApplyAddress(Entry.Text)
        RestoreViewState(Entry)
        UpdateNavigationButtons()

    End Sub

    ''' <summary>The anchor ID of the entry behind a list item, or 0 if it has none (or Item is Nothing).</summary>
    Private Shared Function AnchorOf(Item As ListViewItem) As Long
        Dim Entry = TryCast(Item?.Tag, EmbeddedFileSystem.ContentListEntry)
        Return If(Entry Is Nothing, 0, Entry.ChildAnchorId)
    End Function

    ''' <summary>Snapshots the file list's current selection/focus/scroll into the history entry
    ''' being left, so a later Back/Forward to it can put the view back the way it was.</summary>
    Private Sub CaptureCurrentViewState()
        If _HistoryIndex < 0 OrElse _HistoryIndex >= _History.Count Then Return
        Dim Entry = _History(_HistoryIndex)
        Entry.SelectedAnchors = lvFiles.SelectedItems.
                                        Cast(Of ListViewItem)().
                                        Select(AddressOf AnchorOf).
                                        Where(Function(anchor) anchor <> 0).
                                        ToList()
        Entry.FocusedAnchor = AnchorOf(lvFiles.FocusedItem)

        Entry.ScrollPosition = GetScrollPosition(lvFiles)
        'Dim TopVisibleItem = lvFiles.Items.
        '    Cast(Of ListViewItem)().
        '    FirstOrDefault(Function(Item) lvFiles.ClientRectangle.IntersectsWith(Item.Bounds))
        'Entry.TopItemAnchor = AnchorOf(TopVisibleItem)

    End Sub

    ''' <summary>Re-applies a history entry's saved selection/focus/scroll after its address has
    ''' repopulated the file list.</summary>
    Private Sub RestoreViewState(Entry As AddressHistoryEntry)
        If Entry.SelectedAnchors IsNot Nothing Then
            For Each Item As ListViewItem In lvFiles.Items
                Dim Anchor = AnchorOf(Item)
                If Anchor = 0 Then Continue For
                If Entry.SelectedAnchors.Contains(Anchor) Then Item.Selected = True
                If Anchor = Entry.FocusedAnchor Then Item.Focused = True
            Next
        End If

        SetScrollPosition(lvFiles, Entry.ScrollPosition)
        'If Entry.TopItemAnchor = 0 Then Return
        'For Each Item As ListViewItem In lvFiles.Items
        '    If AnchorOf(Item) <> Entry.TopItemAnchor Then Continue For
        '    If lvFiles.View = View.Details OrElse lvFiles.View = View.List Then
        '        lvFiles.TopItem = Item
        '    Else
        '        Item.EnsureVisible()
        '    End If
        '    Exit For
        'Next
    End Sub

    Private Const LVM_FIRST As Integer = &H1000
    Private Const LVM_GETORIGIN As Integer = LVM_FIRST + 41
    Private Const LVM_SCROLL As Integer = LVM_FIRST + 20


    <StructLayout(LayoutKind.Sequential)>
    Private Structure NativePoint
        Public X As Integer
        Public Y As Integer
    End Structure

    <DllImport("user32.dll", CharSet:=CharSet.Auto)>
    Private Shared Function SendMessage(
    HWnd As IntPtr,
    Msg As Integer,
    WParam As IntPtr,
    ByRef LParam As NativePoint) As IntPtr
    End Function

    Private Function GetScrollPosition(ListView As ListView) As Point

        Dim Origin As NativePoint

        SendMessage(ListView.Handle,
                LVM_GETORIGIN,
                IntPtr.Zero,
                Origin)

        Return New Point(Origin.X, Origin.Y)

    End Function

    Private Sub SetScrollPosition(ListView As ListView, TargetPosition As Point)

        Dim CurrentPosition = GetScrollPosition(ListView)

        SendMessage(ListView.Handle,
                LVM_SCROLL,
                CType(TargetPosition.X - CurrentPosition.X, IntPtr),
                CType(TargetPosition.Y - CurrentPosition.Y, IntPtr))

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
        'If _SuppressSearchText Then Return
        '_SearchTimer.Stop()
        '_SearchTimer.Start()
    End Sub

    'Private Sub SearchTimer_Tick(Sender As Object, EventArgs As EventArgs) Handles _SearchTimer.Tick
    '    _SearchTimer.Stop()
    '    CommitTypedAddress()
    'End Sub

    ''' <summary>Enter applies the typed text at once instead of waiting out the debounce.</summary>
    Private Sub tsiSearch_KeyDown(Sender As Object, EventArgs As KeyEventArgs) Handles tsiSearch.KeyDown
        If EventArgs.KeyCode <> Keys.Enter AndAlso EventArgs.KeyCode <> Keys.Return Then Return
        '_SearchTimer.Stop()
        CommitTypedAddress()
        EventArgs.Handled = True
        EventArgs.SuppressKeyPress = True
        lvFiles.Focus()
    End Sub

    ''' <summary>Applies text the user typed. Consecutive typed edits collapse into one history entry.</summary>
    Private Sub CommitTypedAddress()
        '_SearchTimer.Stop()
        Dim Text = tsiSearch.Text
        If _LastPushWasTyped AndAlso _HistoryIndex >= 0 Then
            Dim ExistingEntry = _History(_HistoryIndex)
            ExistingEntry.Text = Text
            ExistingEntry.SelectedAnchors = Nothing
            ExistingEntry.FocusedAnchor = 0
            ExistingEntry.ScrollPosition = New Point()
        Else
            CaptureCurrentViewState()
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
        '_SearchTimer.Stop()
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

    Private Const SearchResultFlushIntervalMs As Integer = 250

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
            'lvFiles.ListViewItemSorter = _ListSorter
            'lvFiles.Sort()
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
            lvFiles.Items.AddRange(_SearchResults.Select(Function(x) CreateSearchListItem(x)).ToArray())
            'lvFiles.ListViewItemSorter = _ListSorter
            'lvFiles.Sort()
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

    <DllImport("user32.dll")>
    Private Shared Function InvalidateRect(
        hWnd As IntPtr,
        lpRect As IntPtr,
        bErase As Boolean) As Boolean
    End Function

    Private Const LVM_GETHEADER As Integer = &H101F

    Private Sub InvalidateHeader(lv As ListView)

        Dim hHeader = SendMessage(
            lv.Handle,
            LVM_GETHEADER,
            IntPtr.Zero,
            IntPtr.Zero)

        If hHeader <> IntPtr.Zero Then
            InvalidateRect(hHeader, IntPtr.Zero, True)
        End If

    End Sub

    Private Sub SortByColumn(ColumnIndex As Integer)
        If lvFiles.ListViewItemSorter Is Nothing OrElse ColumnIndex <> _ListSorter.Column Then
            lvFiles.ListViewItemSorter = _ListSorter
            _ListSorter.Column = ColumnIndex
            _ListSorter.Order = SortOrder.Ascending
        Else
            _ListSorter.Order = If(_ListSorter.Order = SortOrder.Ascending, SortOrder.Descending, SortOrder.Ascending)
        End If

        lvFiles.Invalidate(New Rectangle(0, 0, 100, 100))

        InvalidateHeader(lvFiles)
        lvFiles.Sort()
    End Sub

    Private Sub lvFiles_ColumnClick(Sender As Object, e As ColumnClickEventArgs) Handles lvFiles.ColumnClick
        SortByColumn(e.Column)
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
        tsiNewFolder.Available = _SearchActive = False AndAlso Any = False
        tsiRefresh.Available = True
        tsiView.Available = True
        ' We did use DrawIconViewItem to custom render these - but we are in the process of unifying it all into DrawThumbnailItem...
        ' so in the meantime these are not avaliable:
        tsiViewSmIcon.Available = False
        tsiViewList.Available = False
        tsiViewTiles.Available = False

        tsiSortBy.Available = True
        Static SortByMenuItems As Dictionary(Of Integer, ToolStripMenuItem) =
            lvFiles.Columns().OfType(Of ColumnHeader).
                              Select(Function(x)
                                         Dim colIndex = lvFiles.Columns.IndexOf(x)
                                         Dim tsi As New ToolStripMenuItem(x.Text, Nothing,
                                             Sub(ss, ee)
                                                 SortByColumn(colIndex)
                                             End Sub)
                                         tsiSortBy.DropDownItems.Add(tsi)
                                         Return New With {.Column = colIndex, .tsi = tsi}
                                     End Function).
                              ToDictionary(Function(x) x.Column, Function(x) x.tsi)
        For Each SortByItem In SortByMenuItems
            SortByItem.Value.Checked = lvFiles.ListViewItemSorter IsNot Nothing AndAlso _ListSorter.Column = SortByItem.Key
        Next

        tsiCut.Available = Any
        tsiCopy.Available = Any
        tsiPaste.Available = True 'TODO: <

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

    Private Sub lvFiles_DrawColumnHeader(Sender As Object, e As DrawListViewColumnHeaderEventArgs) Handles lvFiles.DrawColumnHeader
        e.DrawBackground()

        Dim Bounds = e.Bounds

        Dim Flags = TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix
        If e.Header IsNot Nothing AndAlso e.Header.TextAlign = HorizontalAlignment.Right Then
            Flags = Flags Or TextFormatFlags.Right
        ElseIf e.Header IsNot Nothing AndAlso e.Header.TextAlign = HorizontalAlignment.Center Then
            Flags = Flags Or TextFormatFlags.HorizontalCenter
        End If
        TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, Bounds, e.ForeColor, Flags)

        If e.ColumnIndex = _ListSorter.Column AndAlso lvFiles.ListViewItemSorter IsNot Nothing Then
            DrawSortIndicator(e.Graphics, Bounds, _ListSorter.Order)
        End If

    End Sub

    Shared Function DrawSortIndicator(g As Graphics, r As Rectangle, order As SortOrder) As Rectangle
        If order = SortOrder.None Then Return r

        If VisualStyles.VisualStyleRenderer.IsSupported Then
            Dim Element = If(order = SortOrder.Ascending, VisualStyles.VisualStyleElement.Header.SortArrow.SortedUp, VisualStyles.VisualStyleElement.Header.SortArrow.SortedDown)
            If VisualStyles.VisualStyleRenderer.IsElementDefined(Element) Then
                'yay - rendering supported

                Dim renderer2 = New VisualStyles.VisualStyleRenderer(Element)
                If renderer2 IsNot Nothing Then
                    Dim sz As Size = renderer2.GetPartSize(g, VisualStyles.ThemeSizeType.True)
                    Dim pt As Point = renderer2.GetPoint(VisualStyles.PointProperty.Offset)
                    ' GetPoint() should work, but if it doesn't, put the arrow in the top middle
                    If pt.IsEmpty Then
                        pt = New Point(CInt(r.X + (r.Width / 2) - (sz.Width / 2)), r.Y)
                    End If
                    renderer2.DrawBackground(g, New Rectangle(pt, sz))

                    Return r
                End If
            End If
        End If

        ' No theme support for sort indicators. So, we draw a triangle at the right edge
        ' of the column header.
        Const triangleHeight As Integer = 16
        Const triangleWidth As Integer = 16
        Const midX As Integer = triangleWidth \ 2
        Const midY As Integer = (triangleHeight \ 2) - 1
        Const deltaX As Integer = midX - 2
        Const deltaY As Integer = deltaX \ 2

        Dim triangleLocation As New Point(r.Right - triangleWidth - 2, r.Top + (r.Height - triangleHeight) \ 2)
        Dim pts As Point() = New Point() {triangleLocation, triangleLocation, triangleLocation}

        If order = SortOrder.Ascending Then
            pts(0).Offset(midX - deltaX, midY + deltaY)
            pts(1).Offset(midX, midY - deltaY - 1)
            pts(2).Offset(midX + deltaX, midY + deltaY)
        Else
            pts(0).Offset(midX - deltaX, midY - deltaY)
            pts(1).Offset(midX, midY + deltaY)
            pts(2).Offset(midX + deltaX, midY - deltaY)
        End If

        g.FillPolygon(SystemBrushes.ControlDark, pts)
        r.Width = r.Width - triangleWidth
        Return r

    End Function

    Public Shared Sub DrawBackground(g As Graphics, rect As Rectangle, DrawElement As VisualStyles.VisualStyleElement)
        If VisualStyles.VisualStyleRenderer.IsSupported AndAlso VisualStyles.VisualStyleRenderer.IsElementDefined(DrawElement) Then
            Dim vsr = New System.Windows.Forms.VisualStyles.VisualStyleRenderer(DrawElement)
            vsr.DrawBackground(g, rect)
        Else
            ControlPaint.DrawBorder3D(g, rect, Border3DStyle.RaisedInner)
            If DrawElement Is VisualStyles.VisualStyleElement.Header.Item.Pressed Then
                ControlPaint.DrawBorder3D(g, rect, Border3DStyle.SunkenInner)
            Else
                ControlPaint.DrawBorder3D(g, rect, Border3DStyle.RaisedInner)
            End If
        End If
    End Sub

    Private Sub lvFiles_DrawItem(Sender As Object, e As DrawListViewItemEventArgs) Handles lvFiles.DrawItem
        ' In Details view every column is painted by DrawSubItem instead.

        If lvFiles.View = View.Details Then
            Return
        End If

        ' We need this check because the ListView seems to request items to be drawn even if they are not in the visible area.
        If lvFiles.ClientRectangle.IntersectsWith(e.Bounds) = False Then
            Return
        End If

        DrawThumbnailItem(e)
        'If _ThumbnailCellSize <> 0 Then
        '    DrawThumbnailItem(e)
        'Else
        '    DrawIconViewItem(e)
        'End If
    End Sub

    Private Sub lvFiles_DrawSubItem(Sender As Object, e As DrawListViewSubItemEventArgs) Handles lvFiles.DrawSubItem
        If e.SubItem Is Nothing Then
            e.DrawDefault = True
            Return
        End If

        'stop the built in control from rendering the background
        Using sb As New SolidBrush(lvFiles.BackColor)
            e.Graphics.FillRectangle(sb, e.Bounds)
        End Using

        Dim Canvas = e.Graphics
        Dim Bounds = e.Bounds
        Dim Selected = e.Item.Selected

        'Using Background As New SolidBrush(If(Selected, SystemColors.Highlight, lvFiles.BackColor))
        '    Canvas.FillRectangle(Background, Bounds)
        'End Using
        If Selected Then
            Using Fill As New SolidBrush(Color.FromArgb(48, SystemColors.Highlight))
                Canvas.FillRectangle(Fill, Bounds)
            End Using
            'Using Border As New Pen(SystemColors.Highlight)
            '    Canvas.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
            'End Using
        End If

        Dim TextBounds = Bounds
        If e.ColumnIndex = 0 Then
            Dim Entry = TryCast(e.Item.Tag, EmbeddedFileSystem.ContentListEntry)
            Dim IconRectangle = New Rectangle(Bounds.X + 2, Bounds.Y + ((Bounds.Height - FileIconProvider.SmallIconSize) \ 2),
                                              FileIconProvider.SmallIconSize, FileIconProvider.SmallIconSize)
            DrawEntryIcon(Canvas, Entry, IconRectangle)
            TextBounds = Rectangle.FromLTRB(IconRectangle.Right + 4, Bounds.Top, Bounds.Right, Bounds.Bottom)
        End If

        If e.ColumnIndex = 0 AndAlso IsListItemBeingEdited(e.Item) Then Return

        Dim Flags = TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix
        If e.Header IsNot Nothing AndAlso e.Header.TextAlign = HorizontalAlignment.Right Then
            Flags = Flags Or TextFormatFlags.Right
        ElseIf e.Header IsNot Nothing AndAlso e.Header.TextAlign = HorizontalAlignment.Center Then
            Flags = Flags Or TextFormatFlags.HorizontalCenter
        End If
        Dim ForeColour = e.SubItem.ForeColor 'If(Selected, SystemColors.HighlightText, EventArgs.Item.ForeColor)
        TextRenderer.DrawText(Canvas, e.SubItem.Text, e.SubItem.Font, TextBounds, ForeColour, Flags)

        If e.Item.Focused AndAlso lvFiles.Focused Then
            'EventArgs.DrawFocusRectangle()
            Dim FirstCol = e.ColumnIndex = 0
            Dim LastCol = e.ColumnIndex = lvFiles.Columns.Count - 1
            Using Border As New Pen(SystemColors.Highlight)
                'Canvas.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
                Dim RectBounds = New Rectangle(Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
                'top
                Canvas.DrawLine(Border, RectBounds.X, RectBounds.Y, RectBounds.Right, RectBounds.Y)
                'bottom
                Canvas.DrawLine(Border, RectBounds.X, RectBounds.Bottom, RectBounds.Right, RectBounds.Bottom)
                If FirstCol Then
                    'left
                    Canvas.DrawLine(Border, RectBounds.X, RectBounds.Y, RectBounds.X, RectBounds.Bottom)
                End If
                If LastCol Then
                    'right
                    Canvas.DrawLine(Border, RectBounds.Right, RectBounds.Y, RectBounds.Right, RectBounds.Bottom)
                End If
            End Using
        End If
    End Sub

    Private Sub tvFolders_DrawNode(sender As Object, e As DrawTreeNodeEventArgs) Handles tvFolders.DrawNode

        'stop the built in control from rendering the background
        Using sb As New SolidBrush(lvFiles.BackColor)
            e.Graphics.FillRectangle(sb, e.Bounds)
        End Using

        Dim Bounds = e.Bounds

        If e.State.HasFlag(TreeNodeStates.Selected) Then
            Using Fill As New SolidBrush(Color.FromArgb(48, SystemColors.Highlight))
                e.Graphics.FillRectangle(Fill, Bounds)
            End Using
        End If

        Dim Flags = TextFormatFlags.VerticalCenter Or TextFormatFlags.NoPrefix
        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont, e.Bounds, e.Node.ForeColor, Flags)

        If e.State.HasFlag(TreeNodeStates.Focused) Then
            'EventArgs.DrawFocusRectangle()
            Using Border As New Pen(SystemColors.Highlight)
                e.Graphics.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
            End Using
        End If
    End Sub

    'Private Sub DrawIconViewItem(e As DrawListViewItemEventArgs)
    '    Dim Canvas = e.Graphics
    '    Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
    '    e.DrawBackground()

    '    Dim Bounds = e.Bounds
    '    Dim Entry = TryCast(e.Item.Tag, EmbeddedFileSystem.ContentListEntry)
    '    Dim IconSize = CurrentIconSize()

    '    If lvFiles.View = View.LargeIcon Then
    '        Dim IconRectangle = New Rectangle(Bounds.X + ((Bounds.Width - IconSize) \ 2), Bounds.Y + 2, IconSize, IconSize)
    '        DrawEntryIcon(Canvas, Entry, IconRectangle)
    '        Dim LabelArea = Rectangle.FromLTRB(Bounds.Left, IconRectangle.Bottom + 2, Bounds.Right, Bounds.Bottom)
    '        DrawIconViewLabel(Canvas, e.Item, LabelArea,
    '                          TextFormatFlags.HorizontalCenter Or TextFormatFlags.WordEllipsis Or TextFormatFlags.NoPrefix)
    '    Else
    '        Dim IconRectangle = New Rectangle(Bounds.X + 1, Bounds.Y + ((Bounds.Height - IconSize) \ 2), IconSize, IconSize)
    '        DrawEntryIcon(Canvas, Entry, IconRectangle)
    '        Dim LabelArea = Rectangle.FromLTRB(IconRectangle.Right + 3, Bounds.Top, Bounds.Right - 2, Bounds.Bottom)
    '        DrawIconViewLabel(Canvas, e.Item, LabelArea,
    '                          TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix)
    '    End If

    '    If e.Item.Focused Then e.DrawFocusRectangle()
    'End Sub

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

    Private Sub DrawThumbnailItem(e As DrawListViewItemEventArgs)
        Dim Canvas = e.Graphics
        Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
        Canvas.PixelOffsetMode = PixelOffsetMode.HighQuality
        e.DrawBackground()

        Dim Bounds = e.Bounds
        If e.Item.Selected Then
            Using Fill As New SolidBrush(Color.FromArgb(48, SystemColors.Highlight))
                Canvas.FillRectangle(Fill, Bounds)
            End Using
            'Using Border As New Pen(SystemColors.Highlight)
            '    Canvas.DrawRectangle(Border, Bounds.X + 1, Bounds.Y + 1, Bounds.Width - 2, Bounds.Height - 2)
            'End Using
        End If

        Dim IconView = _ThumbnailCellSize = 0
        Dim IconSize = FileIconProvider.JumboIconSize

        Dim CellSize = _ThumbnailCellSize
        Dim IconToCellSizeRatio = 0.82
        If IconView Then
            If lvFiles.View = View.LargeIcon Then
                CellSize = lvFiles.LargeImageList.ImageSize.Height
                IconSize = FileIconProvider.LargeIconSize
            Else
                CellSize = lvFiles.SmallImageList.ImageSize.Height
                IconSize = FileIconProvider.SmallIconSize
            End If
            IconToCellSizeRatio = 1
        End If

        Dim IconArea = New Rectangle(Bounds.X, Bounds.Y + 3, Bounds.Width, CellSize)
        Dim Entry = TryCast(e.Item.Tag, EmbeddedFileSystem.ContentListEntry)
        Debug.Print(Entry.Name)
        Dim Thumbnail = If(IconView, Nothing, If(Entry IsNot Nothing AndAlso IsThumbnailableEntry(Entry), GetOrRequestThumbnail(Entry), Nothing))

        If Thumbnail IsNot Nothing Then
            Dim Target = FitCentered(Thumbnail.Size, IconArea, CellSize)

            Dim mfWidth = 32
            Dim mfHeight = 32
            Using mf = Drawing.CreateMetafile(New Size(mfWidth, mfHeight))
                Using g = Graphics.FromImage(mf)
                    Using sb As New SolidBrush(Drawing.AlphaColor(SystemColors.WindowText, 15))
                        g.FillRectangle(sb, New RectangleF(0, 0, CSng(mfWidth / 2), CSng(mfHeight / 2)))
                        g.FillRectangle(sb, New RectangleF(CSng(mfWidth / 2), CSng(mfHeight / 2), CSng(mfWidth / 2), CSng(mfHeight / 2)))
                    End Using
                End Using
                Using tb As New TextureBrush(mf, Drawing2D.WrapMode.Tile)
                    Canvas.FillRectangle(tb, Target)
                End Using
            End Using

            Canvas.DrawImage(Thumbnail, Target)

            'Using Border As New Pen(Drawing.AlphaColor(SystemColors.WindowText, 63))
            '    Canvas.DrawRectangle(Border, Target.X - 1, Target.Y - 1, Target.Width + 1, Target.Height + 1)
            DrawTypeBadge(Canvas, Entry, IconArea)
            '    Canvas.DrawRectangle(Border, IconArea.X - 1, IconArea.Y - 1, IconArea.Width + 1, IconArea.Height + 1)
            'End Using

        Else
            Dim TypeIcon = GetEntryIcon(Entry, IconSize)
            If TypeIcon IsNot Nothing Then Canvas.DrawImage(TypeIcon, FitCentered(TypeIcon.Size, IconArea, CInt(CellSize * IconToCellSizeRatio)))
        End If

        Dim LabelArea = New Rectangle(Bounds.X + 2, IconArea.Bottom + 2, Bounds.Width - 4, Bounds.Bottom - IconArea.Bottom - 4)
        If IsListItemBeingEdited(e.Item) = False Then
            TextRenderer.DrawText(Canvas, e.Item.Text, lvFiles.Font, LabelArea, lvFiles.ForeColor,
                                  TextFormatFlags.HorizontalCenter Or TextFormatFlags.WordEllipsis Or TextFormatFlags.NoPrefix)
        End If

        If e.Item.Focused AndAlso lvFiles.Focused Then
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

    Private Sub UploadPaths(Paths As IEnumerable(Of String), TargetDirectoryAnchorId As Long, Optional CopyType As CopyTypes = CopyTypes.Copy)
        Dim RootPaths = Paths.Where(Function(x) String.IsNullOrWhiteSpace(x) = False).ToList()
        Dim PostSelectionItems As New List(Of String)
        If RootPaths.Count = 0 Then Return

        ExecuteLongBlockingActionOnThread(
            Sub(Report)
                Report.SetText("Uploading...")

                ' The disk walk (EnumerateUploadEntries) only ever runs once no matter how many
                ' independent passes are made over WorkItems below - the copy and the size scan each
                ' get their own cursor, but only whichever one is further ahead actually touches disk.

                Dim ieWorkItems As IEnumerable(Of UploadWorkItem)
                Select Case CopyType
                    Case CopyTypes.ExtractEachToOwnFolder, CopyTypes.Extract
                        ' Non-zip paths are reported by ReadZipDirectory as they are reached rather than
                        ' filtered out silently, so a mixed selection does not extract half and drop the rest.
                        ieWorkItems = EnumerateZipUploadEntries(RootPaths, CopyType = CopyTypes.ExtractEachToOwnFolder)
                    Case Else
                        ieWorkItems = EnumerateUploadEntries(RootPaths)
                End Select
                Dim WorkItems = MemoizedEnumerable.Create(ieWorkItems)
                If WorkItems.Any() = False Then Return

                ' The copy starts as soon as the first item is available; the total byte count - the
                ' part that is slow over a network share or a deep tree - is summed on a second thread
                ' and published when ready. The copy reports byte progress only once the total is known.
                Dim TotalSize As New TotalSizeBox()
                Using Cancellation As New CancellationTokenSource()
                    Dim Sizer = New Thread(
                        Sub()
                            Try
                                Dim Sum As Long = 0
                                For Each workItem In WorkItems
                                    Cancellation.Token.ThrowIfCancellationRequested()
                                    If workItem.IsDirectory = False Then Sum += workItem.LogicalSize
                                Next
                                TotalSize.Publish(Sum)
                            Catch
                                ' Cancelled by a copy failure, or a source file vanished mid-scan;
                                ' the progress bar simply stays indeterminate.
                            End Try
                        End Sub) With {.IsBackground = True, .Name = "Upload size scan"}
                    Sizer.Start()

                    Try
                        Dim sw As New Stopwatch
                        sw.Start()
                        CopyUploadWorkList(WorkItems, TargetDirectoryAnchorId, TotalSize, Report, PostSelectionItems, CopyType)
                        sw.Stop()
                        Debug.Print($"Upload time: {sw.Elapsed}")
                    Finally
                        Cancellation.Cancel()
                        Sizer.Join()
                    End Try
                End Using
            End Sub,
            "One or more items could not be uploaded.")

        RefreshFileSystemView()

        OnMutatedFileSystem()

        'select items just copied:
        Dim ItemsInFolder = lvFiles.Items.OfType(Of ListViewItem).
                                          Select(Function(x) New With {.ListViewItem = x,
                                                                       .ContentListEntry = TryCast(x.Tag, EmbeddedFileSystem.ContentListEntry)}).
                                          Where(Function(x) x.ContentListEntry IsNot Nothing)
        Dim ItemsToSelect = ItemsInFolder.Join(PostSelectionItems,
                                               Function(x) x.ContentListEntry.Name,
                                               Function(y) IO.Path.GetFileName(y),
                                               Function(x, y) x.ListViewItem, StringComparer.OrdinalIgnoreCase).
                                          ToArray()
        For i = 0 To ItemsToSelect.Count - 1
            Dim Item = ItemsToSelect(i)
            Item.Selected = True
            If i = 0 Then Item.EnsureVisible()
        Next
    End Sub

    ''' <summary>
    ''' Lazily walks each zip in <paramref name="RootPaths"/>, yielding a directory-create or file-copy
    ''' step for every entry so <see cref="CopyUploadWorkList"/> can extract the archive straight into
    ''' the browser. When <paramref name="EachToOwnFolder"/> is set an archive's entries land under a
    ''' folder named after the archive; otherwise they merge into the target directory. Each archive's
    ''' central directory is read once, as the walk reaches it; file bytes are pulled per entry, on
    ''' demand, so nothing is held open between steps. Entries that try to climb out of the target with
    ''' a ".." segment are skipped.
    ''' </summary>
    Private Shared Iterator Function EnumerateZipUploadEntries(RootPaths As IEnumerable(Of String), EachToOwnFolder As Boolean) As IEnumerable(Of UploadWorkItem)
        For Each RootPath In RootPaths
            If File.Exists(RootPath) = False Then Continue For

            Dim ArchiveRoot = String.Empty
            If EachToOwnFolder Then
                ArchiveRoot = MakeSafeFileName(Path.GetFileNameWithoutExtension(RootPath))
                If ArchiveRoot = "." OrElse ArchiveRoot = ".." Then ArchiveRoot = "unnamed"
            End If

            ' Create the per-archive folder up front so an empty archive still leaves something visible.
            If ArchiveRoot.Length > 0 Then Yield New ZipUploadWorkItem(String.Empty, ArchiveRoot)

            For Each Entry In ReadZipDirectory(RootPath)
                ' "a/b/c.txt" or "a\b\c.txt" -> ["a", "b", "c.txt"]; a bare "." segment is just noise.
                Dim Segments = Entry.FullName.Split({"/"c, "\"c}, StringSplitOptions.RemoveEmptyEntries).
                                    Where(Function(s) s <> ".").ToArray()
                If Segments.Length = 0 OrElse Segments.Any(Function(s) s = "..") Then Continue For

                Dim RelativeParent = String.Join("\", Segments.Take(Segments.Length - 1))
                If ArchiveRoot.Length > 0 Then
                    RelativeParent = If(RelativeParent.Length = 0, ArchiveRoot, $"{ArchiveRoot}\{RelativeParent}")
                End If
                Dim Name = Segments(Segments.Length - 1)

                If Entry.IsDirectory Then
                    Yield New ZipUploadWorkItem(RelativeParent, Name)
                Else
                    Yield New ZipUploadWorkItem(RootPath, Entry.FullName, RelativeParent, Name, Entry.Length)
                End If
            Next
        Next
    End Function

    ''' <summary>
    ''' Snapshots an archive's entries with the archive open only for the read, so
    ''' <see cref="EnumerateZipUploadEntries"/> can yield without holding a file handle across steps.
    ''' A path that is not a readable zip is surfaced as an <see cref="IOException"/> naming the file.
    ''' </summary>
    Private Shared Function ReadZipDirectory(ArchivePath As String) As List(Of ZipDirectoryEntry)
        Try
            Using Archive = ZipFile.OpenRead(ArchivePath)
                Return Archive.Entries.
                               Select(Function(e) New ZipDirectoryEntry(e.FullName, e.Length,
                                                                        e.FullName.EndsWith("/") OrElse e.FullName.EndsWith("\"))).
                               ToList()
            End Using
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is NotSupportedException
            Throw New IOException($"'{Path.GetFileName(ArchivePath)}' could not be read as a zip archive.", ex)
        End Try
    End Function

    ''' <summary>
    ''' Lazily walks <paramref name="RootPaths"/>, yielding every directory-create and file-copy step
    ''' one at a time, ordered so a directory always precedes its contents. Nothing is read from disk
    ''' until a consumer actually asks for the next item, so a caller can start acting on the first
    ''' result instead of waiting for the whole tree to be walked first.
    ''' </summary>
    Private Shared Iterator Function EnumerateUploadEntries(RootPaths As IEnumerable(Of String)) As IEnumerable(Of UploadWorkItem)
        For Each RootPath In RootPaths
            If File.Exists(RootPath) Then
                Yield New FileUploadWorkItem(RootPath, String.Empty)
            ElseIf Directory.Exists(RootPath) Then
                For Each WorkItem In EnumerateDirectoryEntries(RootPath, String.Empty, New DirectoryInfo(RootPath).Name)
                    Yield WorkItem
                Next
            End If
        Next
    End Function

    Private Shared Iterator Function EnumerateDirectoryEntries(DiskPath As String, RelativeParent As String, Name As String) As IEnumerable(Of UploadWorkItem)
        Yield New FileUploadWorkItem(DiskPath, RelativeParent)
        Dim ChildRelativeParent = If(RelativeParent.Length = 0, Name, $"{RelativeParent}\{Name}")

        For Each FilePath In Directory.EnumerateFiles(DiskPath)
            Yield New FileUploadWorkItem(FilePath, ChildRelativeParent)
        Next
        For Each ChildPath In Directory.EnumerateDirectories(DiskPath)
            For Each WorkItem In EnumerateDirectoryEntries(ChildPath, ChildRelativeParent, New DirectoryInfo(ChildPath).Name)
                Yield WorkItem
            Next
        Next
    End Function

    Private Sub CopyUploadWorkList(WorkItems As IEnumerable(Of UploadWorkItem), TargetDirectoryAnchorId As Long,
                                   TotalSize As TotalSizeBox, Report As frmProgress.ProgressReport, PostSelectionItems As List(Of String), Optional CopyType As CopyTypes = CopyTypes.Copy)
        Dim FolderAnchors As New Dictionary(Of String, Long)() From {{String.Empty, TargetDirectoryAnchorId}}
        Dim Resolution As ConflictChoice = ConflictChoice.Cancel
        Dim Resolved As Boolean = False
        Dim CopiedBytes As Long = 0
        Dim LastReport As Date = Date.MinValue

        Dim DirectoryEntryCache As New Dictionary(Of Long, IReadOnlyList(Of EmbeddedFileSystem.ContentListEntry))
        Dim GetDirectoryEntries =
            Function(AnchorId As Long)
                If Not DirectoryEntryCache.ContainsKey(AnchorId) Then
                    DirectoryEntryCache(AnchorId) = _FileSystem.GetDirectoryEntries(AnchorId)
                End If
                Return DirectoryEntryCache(AnchorId)
            End Function

        ' Every item that is actually written contributes the top-level entry its path starts under -
        ' the first '\'-separated segment of RelativeParent, or the item itself when it lands directly
        ' in the target folder - so UploadPaths can reselect exactly what this run produced. A folder
        ' is recorded even when its own contents are skipped, because other entries beneath it may not
        ' be; a skipped file records nothing, since it was not copied.
        Dim SeenRoots As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim RecordRootSelection =
            Sub(Source As UploadWorkItem)
                Dim SeparatorIndex = Source.RelativeParent.IndexOf("\"c)
                Dim RootName = If(Source.RelativeParent.Length = 0, Source.Name,
                                  If(SeparatorIndex < 0, Source.RelativeParent, Source.RelativeParent.Substring(0, SeparatorIndex)))
                If SeenRoots.Add(RootName) Then PostSelectionItems.Add(RootName)
            End Sub

        For Each workItem In WorkItems
            If workItem.IsDirectory Then
                EnsureUploadFolder(workItem.RelativeParent, workItem.Name, TargetDirectoryAnchorId, FolderAnchors)
                RecordRootSelection(workItem)
                Continue For
            End If

            Dim ParentAnchor = EnsureUploadFolderPath(workItem.RelativeParent, TargetDirectoryAnchorId, FolderAnchors)
            Dim Existing = GetDirectoryEntries(ParentAnchor).
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
                        CopiedBytes += workItem.LogicalSize
                        ReportUploadProgress(Report, TotalSize, CopiedBytes, workItem.Name, LastReport, False)
                        Continue For
                    Case Else
                        Replace = True
                End Select
            End If

            If Replace Then _FileSystem.DeleteEntry(ParentAnchor, Existing.Name)
            Dim FileAnchorId = _FileSystem.CreateFile(ParentAnchor, workItem.Name)
            Using SourceStream = workItem.CreateStream()
                ' A bigger write-buffer threshold means far fewer durable (fsync) publishes for a
                ' large sequential upload - see the comment on FileStreamView.BufferedEndPosition.
                ' It must also stay at least ChunkSize * ParallelChunkCryptoMinChunks, or a drain
                ' never contains enough chunks to reach the parallel crypto path (Options.
                ' MaxCryptoParallelism) and the whole upload silently falls back to serial - this
                ' bit twice: a 4MB threshold paired with a 4MB ChunkSize gives exactly 1 chunk per
                ' drain, when 8 are needed. Scales with file size, capped well short of the memory
                ' a huge file could demand, but never below the parallel-crypto floor.
                Dim MinBufferFlushThreshold = Math.Max(CLng(EmbeddedFileSystem.FileStreamView.DefaultWriteBufferFlushThreshold),
                                                            CLng(_FileSystem.ChunkedStream.ChunkSize) * ChunkedStream.ParallelChunkCryptoMinChunks)
                Dim MaxBufferFlushThreshold = Math.Max(MinBufferFlushThreshold, 256L * 1024 * 1024)
                Dim WriteBufferFlushThreshold = CInt(Math.Min(MaxBufferFlushThreshold,
                                                                    Math.Max(MinBufferFlushThreshold, SourceStream.Length \ 64)))
                Using DestinationStream = _FileSystem.OpenFile(FileAnchorId, PendingOnClose:=True, WriteBufferFlushThreshold:=WriteBufferFlushThreshold)
                    Dim Buffer(1024 * 1024 - 1) As Byte
                    While True
                        Dim BytesRead = SourceStream.Read(Buffer, 0, Buffer.Length)
                        If BytesRead = 0 Then Exit While
                        DestinationStream.Write(Buffer, 0, BytesRead)
                        CopiedBytes += BytesRead
                        ReportUploadProgress(Report, TotalSize, CopiedBytes, workItem.Name, LastReport, False)
                    End While
                    DestinationStream.Flush()

                    ' Reached only when the copy completed: commit the entry as the stream closes.
                    ' Any earlier throw leaves it PendingFile for the next run or Check integrity.
                    DestinationStream.PendingOnClose = False
                End Using
            End Using

            RecordRootSelection(workItem)
            ReportUploadProgress(Report, TotalSize, CopiedBytes, workItem.Name, LastReport, False)
        Next
    End Sub

    ''' <summary>
    ''' Pushes the current byte progress to the progress dialog. Throttled to ~4 updates a second
    ''' unless <paramref name="Force"/> is set (file finished, or an item was skipped).
    ''' </summary>
    Private Shared Sub ReportUploadProgress(Report As frmProgress.ProgressReport, TotalSize As TotalSizeBox,
                                            CopiedBytes As Long, Name As String,
                                            ByRef LastReport As Date, Force As Boolean)
        Dim Timestamp = Date.UtcNow
        If Force = False AndAlso Timestamp.Subtract(LastReport).TotalMilliseconds < 250 Then Return
        LastReport = Timestamp

        Dim Total = TotalSize.Value
        If Total.HasValue Then
            Report.SetText($"Uploading {Name} ({FormatByteLength(CopiedBytes)} of {FormatByteLength(Total.Value)})...")
            Report.SetProgress(CopiedBytes, Math.Max(Total.Value, CopiedBytes))
        Else
            Report.SetText($"Uploading {Name} ({FormatByteLength(CopiedBytes)} copied)...")
        End If
    End Sub

    Private Function EnsureUploadFolderPath(RelativeParent As String, TargetDirectoryAnchorId As Long,
                                            FolderAnchors As Dictionary(Of String, Long)) As Long
        If RelativeParent.Length = 0 Then Return TargetDirectoryAnchorId

        Dim CachedAnchor As Long
        If FolderAnchors.TryGetValue(RelativeParent, CachedAnchor) Then Return CachedAnchor

        Dim SeparatorIndex = RelativeParent.LastIndexOf("\"c)
        Dim GrandParent = If(SeparatorIndex < 0, String.Empty, RelativeParent.Substring(0, SeparatorIndex))
        Dim Name = If(SeparatorIndex < 0, RelativeParent, RelativeParent.Substring(SeparatorIndex + 1))
        Return EnsureUploadFolder(GrandParent, Name, TargetDirectoryAnchorId, FolderAnchors)
    End Function

    Private Function EnsureUploadFolder(RelativeParent As String, Name As String, TargetDirectoryAnchorId As Long,
                                        FolderAnchors As Dictionary(Of String, Long)) As Long
        Dim ParentAnchor = EnsureUploadFolderPath(RelativeParent, TargetDirectoryAnchorId, FolderAnchors)
        Dim Key = If(RelativeParent.Length = 0, Name, $"{RelativeParent}\{Name}")

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
        OnMutatedFileSystem()
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

    Dim DroppedMouseButtons As MouseButtons
    Private Sub FileSystemControl_DragEnter(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragEnter, lvFiles.DragEnter
        DroppedMouseButtons = Control.MouseButtons
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
        'TODO: allow move as well as copy?
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return DragDropEffects.None
        If AllowedEffect.HasFlag(DragDropEffects.Copy) Then Return DragDropEffects.Copy
        Return DragDropEffects.None
    End Function

    Private Sub lvFiles_DragDrop(Sender As Object, EventArgs As DragEventArgs) Handles lvFiles.DragDrop
        If DroppedMouseButtons = MouseButtons.Right Then
            ShowDropMenu(DirectCast(Sender, Control), EventArgs, _CurrentDirectoryAnchorId)
        Else
            QueueDroppedPathUpload(EventArgs.Data, _CurrentDirectoryAnchorId)
        End If
    End Sub

    Private Sub tvFolders_DragDrop(Sender As Object, EventArgs As DragEventArgs) Handles tvFolders.DragDrop
        Dim ClientPoint = tvFolders.PointToClient(New Point(EventArgs.X, EventArgs.Y))
        Dim TargetNode = tvFolders.GetNodeAt(ClientPoint)
        If TargetNode Is Nothing Then Return
        Dim Info = TryCast(TargetNode.Tag, DirectoryNodeInfo)
        If Info Is Nothing Then Return

        If DroppedMouseButtons = MouseButtons.Right Then
            ShowDropMenu(DirectCast(Sender, Control), EventArgs, _CurrentDirectoryAnchorId)
        Else
            QueueDroppedPathUpload(EventArgs.Data, Info.AnchorId)
        End If

    End Sub

    Dim DroppedData As IDataObject
    Dim DroppedAnchorId As Long
    Private Sub ShowDropMenu(Control As Control, DragEventArgs As DragEventArgs, TargetDirectoryAnchorId As Long)
        Dim Data = DragEventArgs.Data
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return
        Dim Paths = TryCast(Data.GetData(DataFormats.FileDrop), String())
        If Paths Is Nothing OrElse Paths.Length = 0 Then Return

        DroppedData = Data
        DroppedAnchorId = TargetDirectoryAnchorId

        If Paths.All(Function(x) String.Equals(IO.Path.GetExtension(x), ".zip", StringComparison.OrdinalIgnoreCase)) Then
            tsiDropExtract.Available = True
            tsiDropExtractEachToOwnFolder.Available = True
        Else
            tsiDropExtract.Available = False
            tsiDropExtractEachToOwnFolder.Available = False
        End If

        'TODO: make the menu item bold based on the DropEffect?
        'show the context menu

        Me.Activate()
        Dim Point = Control.PointToClient(New Point(DragEventArgs.X, DragEventArgs.Y))
        DropMenu.Show(Control, Point)
    End Sub

    Private Sub tsiDropCopy_Click(sender As Object, e As EventArgs) Handles tsiDropCopy.Click
        QueueDroppedPathUpload(DroppedData, DroppedAnchorId)
    End Sub

    Private Sub tsiDropExtract_Click(sender As Object, e As EventArgs) Handles tsiDropExtract.Click
        QueueDroppedPathUpload(DroppedData, DroppedAnchorId, CopyTypes.Extract)
    End Sub

    Private Sub tsiDropExtractEachToOwnFolder_Click(sender As Object, e As EventArgs) Handles tsiDropExtractEachToOwnFolder.Click
        QueueDroppedPathUpload(DroppedData, DroppedAnchorId, CopyTypes.ExtractEachToOwnFolder)
    End Sub

    Private Enum CopyTypes
        Copy
        Move
        Extract
        ExtractEachToOwnFolder
    End Enum

    ''' <summary>
    ''' Reads the dropped paths and schedules the upload to run once this handler has returned, so the
    ''' drop finishes immediately and the source window (Explorer) is never held while files copy.
    ''' </summary>
    Private Sub QueueDroppedPathUpload(Data As IDataObject, TargetDirectoryAnchorId As Long, Optional CopyType As CopyTypes = CopyTypes.Copy)
        If Data Is Nothing OrElse Data.GetDataPresent(DataFormats.FileDrop) = False Then Return
        Dim Paths = TryCast(Data.GetData(DataFormats.FileDrop), String())

        BeginInvoke(Sub() UploadPaths(Paths, TargetDirectoryAnchorId, CopyType))
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
        OnMutatedFileSystem()
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

    Private Const PromptDialogFieldWidth As Integer = 360
    Private Const PromptDialogWrapWidth As Integer = 360

    ''' <summary>
    ''' Creates the small fixed-dialog shell (padding, font, AutoSize, an empty single-column
    ''' content layout, and an unattached OK/Cancel button row) shared by the prompt dialogs
    ''' below. The caller adds its own rows to Layout, in order, then adds ButtonRow last.
    ''' OkButton's DialogResult is left None so a caller that needs to validate input before
    ''' closing can handle its Click itself; a caller with nothing to validate can just set
    ''' OkButton.DialogResult = DialogResult.OK.
    ''' </summary>
    Private Shared Sub CreatePromptDialogShell(Title As String,
                                               ByRef Dialog As Form,
                                               ByRef Layout As TableLayoutPanel,
                                               ByRef OkButton As Button,
                                               ByRef CancelButton As Button,
                                               ByRef ButtonRow As Control)

        Dialog = New Form With {
            .Text = Title,
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .StartPosition = FormStartPosition.CenterParent,
            .MinimizeBox = False,
            .MaximizeBox = False,
            .ShowInTaskbar = False,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .Padding = New Padding(12),
            .Font = SystemFonts.MessageBoxFont
        }

        OkButton = New Button With {
            .Text = "OK",
            .AutoSize = True,
            .Margin = New Padding(0, 0, 6, 0)
        }

        CancelButton = New Button With {
            .Text = "Cancel",
            .DialogResult = DialogResult.Cancel,
            .Margin = New Padding(0, 0, 0, 0),
            .AutoSize = True
        }

        Dim Row = New FlowLayoutPanel With {
            .FlowDirection = FlowDirection.RightToLeft,
            .AutoSize = True,
            .Dock = DockStyle.Top,
            .Margin = New Padding(0, 4, 0, 0)
        }
        Row.Controls.Add(CancelButton)
        Row.Controls.Add(OkButton)
        ButtonRow = Row

        Layout = New TableLayoutPanel With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .Dock = DockStyle.Fill
        }

        Dialog.Controls.Add(Layout)
        Dialog.AcceptButton = OkButton
        Dialog.CancelButton = CancelButton

    End Sub

    Private Shared Function CreatePromptLabel(Text As String) As Label
        Return New Label With {
            .AutoSize = True,
            .MaximumSize = New Size(PromptDialogWrapWidth, 0),
            .Text = Text,
            .Margin = New Padding(0, 0, 0, 10)
        }
    End Function

    Public Shared Function PromptForText(Owner As IWin32Window, Title As String, Prompt As String, PasswordBox As Boolean) As String

        Dim Dialog As Form = Nothing
        Dim Layout As TableLayoutPanel = Nothing
        Dim OkButton As Button = Nothing
        Dim CancelButton As Button = Nothing
        Dim ButtonRow As Control = Nothing
        CreatePromptDialogShell(Title, Dialog, Layout, OkButton, CancelButton, ButtonRow)

        Using Dialog

            Dim InputTextBox = New TextBox With {
                .Width = PromptDialogFieldWidth,
                .MaxLength = 256,
                .UseSystemPasswordChar = PasswordBox,
                .Margin = New Padding(0, 0, 0, 10)
            }

            Layout.Controls.Add(CreatePromptLabel(Prompt))
            Layout.Controls.Add(InputTextBox)
            Layout.Controls.Add(ButtonRow)

            OkButton.DialogResult = DialogResult.OK

            If Owner Is Nothing Then
                Dialog.StartPosition = FormStartPosition.CenterScreen
                Dialog.ShowInTaskbar = True
            Else
                Dialog.StartPosition = FormStartPosition.CenterParent
            End If
            If Dialog.ShowDialog(Owner) <> DialogResult.OK Then Return Nothing
            Return InputTextBox.Text.Trim()

        End Using

    End Function

    ''' <summary>
    ''' Prompts for a new password with a confirmation box. The existing password, if any, is
    ''' not requested or validated - the caller is assumed to already have decrypted access.
    ''' </summary>
    ''' <returns>
    ''' Nothing if the user cancels. An empty string if both boxes are left blank, meaning
    ''' password protection should be removed. Otherwise, the confirmed new password.
    ''' </returns>
    Public Shared Function PromptForNewPassword(Title As String, Prompt As String) As String

        Dim Dialog As Form = Nothing
        Dim Layout As TableLayoutPanel = Nothing
        Dim OkButton As Button = Nothing
        Dim CancelButton As Button = Nothing
        Dim ButtonRow As Control = Nothing
        CreatePromptDialogShell(Title, Dialog, Layout, OkButton, CancelButton, ButtonRow)

        Using Dialog

            Dim NewPasswordBox = New TextBox With {.Width = PromptDialogFieldWidth, .MaxLength = 256, .UseSystemPasswordChar = True, .Margin = New Padding(0, 0, 0, 8)}
            Dim ConfirmPasswordBox = New TextBox With {.Width = PromptDialogFieldWidth, .MaxLength = 256, .UseSystemPasswordChar = True, .Margin = New Padding(0, 0, 0, 10)}

            Layout.Controls.Add(CreatePromptLabel(Prompt))
            Layout.Controls.Add(New Label With {.AutoSize = True, .Text = "New password:", .Margin = New Padding(0, 0, 0, 2)})
            Layout.Controls.Add(NewPasswordBox)
            Layout.Controls.Add(New Label With {.AutoSize = True, .Text = "Confirm password:", .Margin = New Padding(0, 0, 0, 2)})
            Layout.Controls.Add(ConfirmPasswordBox)
            Layout.Controls.Add(ButtonRow)

            AddHandler OkButton.Click,
                Sub()
                    If NewPasswordBox.Text <> ConfirmPasswordBox.Text Then
                        MsgBox(Dialog, "The passwords do not match.", MsgBoxStyle.Exclamation)
                        ConfirmPasswordBox.Clear()
                        ConfirmPasswordBox.Focus()
                        Return
                    End If

                    Dialog.DialogResult = DialogResult.OK
                End Sub

            If Dialog.ShowDialog() <> DialogResult.OK Then Return Nothing
            Return NewPasswordBox.Text

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
            '_SearchTimer.Dispose()
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
        'Struct.DrawFragmentation(e.Graphics, tsiFragmentation.ContentRectangle,
        '                         New FragmentationDrawOptions() With {
        '                            .MaxXBlockCount = 100,
        '                            .MaxYBlockCount = 1
        '                         })
        'Struct.DrawFragmentation(e.Graphics, New Rectangle(0, 0, tsiFragmentation.ContentRectangle.Width, 1))
    End Sub

    Private Sub tsiExit_Click(sender As Object, e As EventArgs) Handles tsiExit.Click
        Me.Close()
    End Sub

    Private Sub tsiDefrag_Click(sender As Object, e As EventArgs) Handles tsiDefrag.Click
        Defrag()
    End Sub

    Private Sub tsiEncrypt_Click(sender As Object, e As EventArgs) Handles tsiEncrypt.Click
        SetPassword()
    End Sub

    Private Sub tsiScanQuick_Click(sender As Object, e As EventArgs) Handles tsiScanQuick.Click
        Scan(True)
    End Sub

    Private Sub tsiScanFull_Click(sender As Object, e As EventArgs) Handles tsiScanExtended.Click
        Scan()
    End Sub

    Private Sub Scan(Optional Quick As Boolean = False)
        Dim Mutated = True '< because canceling the thread could mutate the file system
        Using frmProgress As New frmProgress(
                Sub(Parameter, ProgressReport)
                    Dim Critical = False
                    Dim ErrorStates As New Dictionary(Of String, List(Of String))
                    Dim AddErrorStates = Sub(Description As String, State As String)
                                             If ErrorStates.ContainsKey(Description) = False Then ErrorStates(Description) = New List(Of String)
                                             ErrorStates(Description).Add(State)
                                         End Sub
                    Try

                        If Quick = False Then
                            ProgressReport.SetText("Validating...")

                            Dim Report As ChunkedStream.ValidationReport
                            Try
                                Report = FileSystem.ChunkedStream.Validate(
                                    Sub(ProcessedUnits, TotalUnits, UnitType, CancellationToken)
                                        Dim Progress = If(TotalUnits = 0, 1.0R, ProcessedUnits / CDbl(TotalUnits))
                                        ProgressReport.SetText($"Validating ({Progress:P0})...")
                                    End Sub)
                            Catch ex As Exception When ex.getThreadAbortException IsNot Nothing
                                Return
                            Catch ex As Exception
                                MsgBox(ProgressReport.frmProgress, $"Validation could not run.{Environment.NewLine}{ex.GetType.Name}: {ex.Message}", MsgBoxStyle.Critical)
                                Return
                            End Try

                            If Report.HasErrors Then
                                Critical = Report.Problems.Any(Function(x) x.RepairIsLossy)

                                ProgressReport.SetText("Marking affected files...")
                                FileSystem.Mark(Report)

                                For Each problem In Report.Problems
                                    AddErrorStates("File validation failed", problem.Message)
                                Next

                                ProgressReport.SetText("Repairing...")
                                Dim Outcome = Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)

                                AddErrorStates("Initial repair outcome", $"Repaired: {Outcome.Repaired.Count}")
                                AddErrorStates("Initial repair outcome", $"Skipped: {Outcome.Skipped.Count}")
                                AddErrorStates("Initial repair outcome", $"Corrupted data zeroed: {Outcome.BytesZeroed.FormatFileSizeFromBytes()}")

                                If FileSystem.ChunkedStream.Validate().HasErrors Then
                                    AddErrorStates("Initial repair outcome", "Residual errors found, a second check may be needed")
                                End If
                            End If
                        End If

                        ProgressReport.SetText("Recovering unreferenced records...")
                        FileSystem.RecoverPendingFiles(Selector:=New Func(Of EmbeddedFileSystem.PendingFileRecoveryCandidate, EmbeddedFileSystem.PendingFileRecoveryActions)(
                                                       Function(x)
                                                           Dim IsFile = {EmbeddedFileSystem.EntryTypes.File, EmbeddedFileSystem.EntryTypes.CorruptFile, EmbeddedFileSystem.EntryTypes.PendingFile}.Contains(x.State)
                                                           If x.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.CorruptData) Then
                                                               If IsFile Then
                                                                   Critical = True
                                                                   AddErrorStates($"Corrupt files recovered", x.Path)
                                                                   Return EmbeddedFileSystem.PendingFileRecoveryActions.Finalize
                                                               Else
                                                                   Critical = True
                                                                   AddErrorStates("Corrupt directories removed", x.Path)
                                                                   Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                                               End If
                                                           ElseIf x.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.Pending) Then
                                                               Critical = True
                                                               AddErrorStates("Half copied files removed", x.Path)
                                                               Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                                           ElseIf x.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.Orphaned) Then
                                                               Critical = True
                                                               AddErrorStates($"Orphaned {If(IsFile, "files", "directories")} recovered", x.Path)
                                                               Return EmbeddedFileSystem.PendingFileRecoveryActions.Finalize
                                                           End If
                                                           Return EmbeddedFileSystem.PendingFileRecoveryActions.None
                                                       End Function))
                    Catch ex As Exception When ex.getThreadAbortException IsNot Nothing
                        Return
                    Catch ex As Exception
                        MsgBox(ProgressReport.frmProgress,
                               $"The scan could not finish.{Environment.NewLine}{ex.GetType.Name}: {ex.Message}",
                               MsgBoxStyle.Critical)
                        Return
                    End Try
                    If ErrorStates.Any = False Then
                        Mutated = False
                        MsgBox(ProgressReport.frmProgress, "No issues found.", MsgBoxStyle.Information)
                    Else
                        MsgBox(ProgressReport.frmProgress,
                            $"Issues were found while scanning:{Environment.NewLine}{String.Join(Environment.NewLine, ErrorStates.Select(Function(x) $"{x.Key}{Environment.NewLine}{String.Join(Environment.NewLine, x.Value.Select(Function(y) $"    • {y}"))}"))}",
                            If(Critical, MsgBoxStyle.Critical, MsgBoxStyle.Exclamation))
                    End If

                End Sub, Nothing)

            frmProgress.Text = $"{If(Quick, "Quick", "Extended")} Scan"
            frmProgress.ShowInTaskbar = True
            frmProgress.ShowDialog(Me)
            frmProgress.Text = "Scanning"
        End Using

        RefreshFileSystemView()
        If Mutated Then
            OnMutatedFileSystem()
        End If
    End Sub

    Private Sub tsiFragmentation_Click(sender As Object, e As EventArgs) Handles tsiFragmentation.Click
        Defrag()
    End Sub

    ''' <summary>
    ''' Sets, changes, or clears the file's password. The existing password, if any, is not
    ''' requested - the file is already open and decrypted, so no prior authorisation check is
    ''' needed here.
    ''' </summary>
    Private Sub SetPassword()
        Dim NewPassword = PromptForNewPassword(
            "Set Password",
            "Enter a new password to protect this file, or leave both boxes blank to remove password protection.")

        If NewPassword Is Nothing Then Return

        Dim NewEncryptionInfo As ChunkedStream.EncryptionInfo = Nothing
        If NewPassword <> "" Then
            NewEncryptionInfo = New ChunkedStream.EncryptionInfo(NewPassword, System.Text.Encoding.UTF8.GetBytes(NewPassword))
        End If

        FileSystem.ChunkedStream.Options.EncryptionInfo = NewEncryptionInfo

        Dim ApplyException As Exception = Nothing
        ExecuteLongBlockingActionOnThread(
            Sub()
                Try
                    'TODO: this should use a progress form
                    FileSystem.ChunkedStream.ApplyOptions(ChunkedStream.ApplyOptionTypes.Encryption)
                Catch ex As Exception
                    ApplyException = ex
                End Try
            End Sub,
            "The password could not be changed.")

        OnMutatedFileSystem()

        Dim ApplyAborted = ApplyException?.getThreadAbortException() IsNot Nothing

        MsgBox(Me, $"Password {If(NewEncryptionInfo Is Nothing, "removed", "updated")}.{If(ApplyException Is Nothing, "", $"{Environment.NewLine}{Environment.NewLine}However, the {If(NewEncryptionInfo Is Nothing, "decryption", "encryption")} of existing files {If(ApplyAborted, "was aborted", $"failed:{Environment.NewLine}{ApplyException.GetType.Name}: {ApplyException.Message}")}{If(NewEncryptionInfo IsNot Nothing, "", $"{Environment.NewLine}Files can still be accessed as the encryption key has been publicly wrapped.")}")}", If(ApplyException Is Nothing, MsgBoxStyle.Information, MsgBoxStyle.Exclamation))
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

                    Dim Struct = FileSystem.ChunkedStream.GetStructure()
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
        OnMutatedFileSystem()
    End Sub

    Private Sub EmbeddedFileSystemBrowserForm_Load(sender As Object, e As EventArgs) Handles Me.Load
        Dim tsam As New ToolStripAsMenu(tsMain)
    End Sub

    'Dim Struct As ChunkedStreamStructure
    Private Sub EmbeddedFileSystemBrowserForm_MutatedFileSystem(sender As Object, e As EventArgs) Handles Me.MutatedFileSystem
        'Struct = FileSystem.ChunkedStream.GetStructure()
    End Sub

End Class
