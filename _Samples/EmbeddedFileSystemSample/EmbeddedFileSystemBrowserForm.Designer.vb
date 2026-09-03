Imports System.ComponentModel
Imports System.Diagnostics

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class EmbeddedFileSystemBrowserForm
    Inherits Form

    Private WithEvents SplitExplorer As Controls.SplitContainer
    Private WithEvents FolderContextMenu As ContextMenuStrip
    Private WithEvents FileContextMenu As ContextMenuStrip
    Friend WithEvents tsiOpen As ToolStripMenuItem
    Friend WithEvents tsiOpenContainingFolder As ToolStripMenuItem
    Friend WithEvents tsiFileSep1 As ToolStripSeparator
    Friend WithEvents tsiSaveAs As ToolStripMenuItem
    Friend WithEvents tsiRename As ToolStripMenuItem
    Friend WithEvents tsiDelete As ToolStripMenuItem
    Friend WithEvents tsiFileSep2 As ToolStripSeparator
    Friend WithEvents tsiUploadFiles As ToolStripMenuItem
    Friend WithEvents tsiUploadFolder As ToolStripMenuItem
    Friend WithEvents tsiNewFolder As ToolStripMenuItem
    Friend WithEvents tsiFileSep3 As ToolStripSeparator
    Friend WithEvents tsiView As ToolStripMenuItem
    Friend WithEvents tsiViewXlThumb As ToolStripMenuItem
    Friend WithEvents tsiViewLgThumb As ToolStripMenuItem
    Friend WithEvents tsiViewLgIcon As ToolStripMenuItem
    Friend WithEvents tsiViewSmIcon As ToolStripMenuItem
    Friend WithEvents tsiViewList As ToolStripMenuItem
    Friend WithEvents tsiViewDetails As ToolStripMenuItem
    Friend WithEvents tsiViewTiles As ToolStripMenuItem
    Friend WithEvents tsiRefresh As ToolStripMenuItem
    Friend WithEvents tsiFolderOpen As ToolStripMenuItem
    Friend WithEvents tsiFolderSep1 As ToolStripSeparator
    Friend WithEvents tsiFolderUploadFiles As ToolStripMenuItem
    Friend WithEvents tsiFolderUploadFolder As ToolStripMenuItem
    Friend WithEvents tsiFolderNewFolder As ToolStripMenuItem
    Friend WithEvents tsiFolderSep2 As ToolStripSeparator
    Friend WithEvents tsiSaveFolderAs As ToolStripMenuItem
    Friend WithEvents tsiFolderSep3 As ToolStripSeparator
    Friend WithEvents tsiFolderRename As ToolStripMenuItem
    Friend WithEvents tsiDeleteFolder As ToolStripMenuItem
    Friend WithEvents tsiFolderSep4 As ToolStripSeparator
    Friend WithEvents tsiFolderRefresh As ToolStripMenuItem
    Private WithEvents colName As ColumnHeader
    Private WithEvents colSize As ColumnHeader
    Private WithEvents colState As ColumnHeader
    Private WithEvents tvFolders As TreeView
    Private WithEvents lvFiles As ListView
    Friend WithEvents SmallSizer As ImageList
    Friend WithEvents Large32Sizer As ImageList
    Friend WithEvents Thumbnail128Sizer As ImageList
    Friend WithEvents Thumbnail256Sizer As ImageList

    <DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(EmbeddedFileSystemBrowserForm))
        Me.SplitExplorer = New EmbeddedFileSystemSample.Controls.SplitContainer()
        Me.tvFolders = New System.Windows.Forms.TreeView()
        Me.lvFiles = New System.Windows.Forms.ListView()
        Me.colName = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.colSize = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.colState = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.SmallSizer = New System.Windows.Forms.ImageList(Me.components)
        Me.FolderContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.tsiFolderOpen = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderSep1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiFolderUploadFiles = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderUploadFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderNewFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderSep2 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiSaveFolderAs = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderSep3 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiFolderRename = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDeleteFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFolderSep4 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiFolderRefresh = New System.Windows.Forms.ToolStripMenuItem()
        Me.FileContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.tsiOpen = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiOpenContainingFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiSaveAs = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiRename = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDelete = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep2 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiUploadFiles = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiUploadFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiNewFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep3 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiView = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewXlThumb = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewLgThumb = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewLgIcon = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewSmIcon = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewList = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewDetails = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiViewTiles = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiRefresh = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStrip1 = New System.Windows.Forms.ToolStrip()
        Me.tsiFragmentation = New System.Windows.Forms.ToolStripButton()
        Me.tsiCompression = New System.Windows.Forms.ToolStripLabel()
        Me.StatusLabel = New System.Windows.Forms.ToolStripLabel()
        Me.pnlExplorer = New System.Windows.Forms.Panel()
        Me.Large32Sizer = New System.Windows.Forms.ImageList(Me.components)
        Me.Thumbnail128Sizer = New System.Windows.Forms.ImageList(Me.components)
        Me.Thumbnail256Sizer = New System.Windows.Forms.ImageList(Me.components)
        Me.tsMain = New System.Windows.Forms.ToolStrip()
        Me.ToolStripDropDownButton1 = New System.Windows.Forms.ToolStripDropDownButton()
        Me.MenuTextSeparator2 = New EmbeddedFileSystemSample.Controls.MenuTextSeparator()
        Me.tsiScan = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDefrag = New System.Windows.Forms.ToolStripMenuItem()
        Me.MenuTextSeparator1 = New EmbeddedFileSystemSample.Controls.MenuTextSeparator()
        Me.EncryptToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiExit = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiBack = New System.Windows.Forms.ToolStripButton()
        Me.tsiForward = New System.Windows.Forms.ToolStripButton()
        Me.tsiSearch = New EmbeddedFileSystemSample.Controls.ToolStripSpringTextBox()
        CType(Me.SplitExplorer, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SplitExplorer.Panel1.SuspendLayout()
        Me.SplitExplorer.Panel2.SuspendLayout()
        Me.SplitExplorer.SuspendLayout()
        Me.FolderContextMenu.SuspendLayout()
        Me.FileContextMenu.SuspendLayout()
        Me.ToolStrip1.SuspendLayout()
        Me.pnlExplorer.SuspendLayout()
        Me.tsMain.SuspendLayout()
        Me.SuspendLayout()
        '
        'SplitExplorer
        '
        Me.SplitExplorer.Dock = System.Windows.Forms.DockStyle.Fill
        Me.SplitExplorer.GripSpacing = 1.5!
        Me.SplitExplorer.Location = New System.Drawing.Point(0, 0)
        Me.SplitExplorer.Name = "SplitExplorer"
        '
        'SplitExplorer.Panel1
        '
        Me.SplitExplorer.Panel1.Controls.Add(Me.tvFolders)
        Me.SplitExplorer.Panel1MinSize = 180
        '
        'SplitExplorer.Panel2
        '
        Me.SplitExplorer.Panel2.Controls.Add(Me.lvFiles)
        Me.SplitExplorer.Size = New System.Drawing.Size(1084, 611)
        Me.SplitExplorer.SplitterDistance = 320
        Me.SplitExplorer.SplitterWidth = 8
        Me.SplitExplorer.TabIndex = 0
        Me.SplitExplorer.TabStop = False
        '
        'tvFolders
        '
        Me.tvFolders.AllowDrop = True
        Me.tvFolders.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvFolders.HideSelection = False
        Me.tvFolders.Location = New System.Drawing.Point(0, 0)
        Me.tvFolders.Name = "tvFolders"
        Me.tvFolders.ShowNodeToolTips = True
        Me.tvFolders.Size = New System.Drawing.Size(320, 611)
        Me.tvFolders.TabIndex = 0
        '
        'lvFiles
        '
        Me.lvFiles.AllowDrop = True
        Me.lvFiles.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {Me.colName, Me.colSize, Me.colState})
        Me.lvFiles.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lvFiles.FullRowSelect = True
        Me.lvFiles.HideSelection = False
        Me.lvFiles.Location = New System.Drawing.Point(0, 0)
        Me.lvFiles.Name = "lvFiles"
        Me.lvFiles.OwnerDraw = True
        Me.lvFiles.Size = New System.Drawing.Size(756, 611)
        Me.lvFiles.SmallImageList = Me.SmallSizer
        Me.lvFiles.TabIndex = 0
        Me.lvFiles.TileSize = New System.Drawing.Size(260, 48)
        Me.lvFiles.UseCompatibleStateImageBehavior = False
        Me.lvFiles.View = System.Windows.Forms.View.Details
        '
        'colName
        '
        Me.colName.Text = "Name"
        Me.colName.Width = 420
        '
        'colSize
        '
        Me.colSize.Text = "Size"
        Me.colSize.TextAlign = System.Windows.Forms.HorizontalAlignment.Right
        Me.colSize.Width = 120
        '
        'colState
        '
        Me.colState.Text = "State"
        Me.colState.Width = 120
        '
        'SmallSizer
        '
        Me.SmallSizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.SmallSizer.ImageSize = New System.Drawing.Size(16, 16)
        Me.SmallSizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'FolderContextMenu
        '
        Me.FolderContextMenu.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.FolderContextMenu.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiFolderOpen, Me.tsiFolderSep1, Me.tsiFolderUploadFiles, Me.tsiFolderUploadFolder, Me.tsiFolderNewFolder, Me.tsiFolderSep2, Me.tsiSaveFolderAs, Me.tsiFolderSep3, Me.tsiFolderRename, Me.tsiDeleteFolder, Me.tsiFolderSep4, Me.tsiFolderRefresh})
        Me.FolderContextMenu.Name = "FolderContextMenu"
        Me.FolderContextMenu.Size = New System.Drawing.Size(160, 204)
        '
        'tsiFolderOpen
        '
        Me.tsiFolderOpen.Name = "tsiFolderOpen"
        Me.tsiFolderOpen.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderOpen.Text = "Open"
        '
        'tsiFolderSep1
        '
        Me.tsiFolderSep1.Name = "tsiFolderSep1"
        Me.tsiFolderSep1.Size = New System.Drawing.Size(156, 6)
        '
        'tsiFolderUploadFiles
        '
        Me.tsiFolderUploadFiles.Name = "tsiFolderUploadFiles"
        Me.tsiFolderUploadFiles.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderUploadFiles.Text = "Upload File(s)..."
        '
        'tsiFolderUploadFolder
        '
        Me.tsiFolderUploadFolder.Name = "tsiFolderUploadFolder"
        Me.tsiFolderUploadFolder.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderUploadFolder.Text = "Upload Folder..."
        '
        'tsiFolderNewFolder
        '
        Me.tsiFolderNewFolder.Name = "tsiFolderNewFolder"
        Me.tsiFolderNewFolder.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderNewFolder.Text = "New Folder"
        '
        'tsiFolderSep2
        '
        Me.tsiFolderSep2.Name = "tsiFolderSep2"
        Me.tsiFolderSep2.Size = New System.Drawing.Size(156, 6)
        '
        'tsiSaveFolderAs
        '
        Me.tsiSaveFolderAs.Name = "tsiSaveFolderAs"
        Me.tsiSaveFolderAs.Size = New System.Drawing.Size(159, 22)
        Me.tsiSaveFolderAs.Text = "Save Folder As..."
        '
        'tsiFolderSep3
        '
        Me.tsiFolderSep3.Name = "tsiFolderSep3"
        Me.tsiFolderSep3.Size = New System.Drawing.Size(156, 6)
        '
        'tsiFolderRename
        '
        Me.tsiFolderRename.Name = "tsiFolderRename"
        Me.tsiFolderRename.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderRename.Text = "Rename"
        '
        'tsiDeleteFolder
        '
        Me.tsiDeleteFolder.Name = "tsiDeleteFolder"
        Me.tsiDeleteFolder.Size = New System.Drawing.Size(159, 22)
        Me.tsiDeleteFolder.Text = "Delete Folder"
        '
        'tsiFolderSep4
        '
        Me.tsiFolderSep4.Name = "tsiFolderSep4"
        Me.tsiFolderSep4.Size = New System.Drawing.Size(156, 6)
        '
        'tsiFolderRefresh
        '
        Me.tsiFolderRefresh.Name = "tsiFolderRefresh"
        Me.tsiFolderRefresh.Size = New System.Drawing.Size(159, 22)
        Me.tsiFolderRefresh.Text = "Refresh"
        '
        'FileContextMenu
        '
        Me.FileContextMenu.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.FileContextMenu.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiOpen, Me.tsiOpenContainingFolder, Me.tsiFileSep1, Me.tsiSaveAs, Me.tsiRename, Me.tsiDelete, Me.tsiFileSep2, Me.tsiUploadFiles, Me.tsiUploadFolder, Me.tsiNewFolder, Me.tsiFileSep3, Me.tsiView, Me.tsiRefresh})
        Me.FileContextMenu.Name = "FileContextMenu"
        Me.FileContextMenu.Size = New System.Drawing.Size(202, 242)
        '
        'tsiOpen
        '
        Me.tsiOpen.Name = "tsiOpen"
        Me.tsiOpen.Size = New System.Drawing.Size(201, 22)
        Me.tsiOpen.Text = "Open"
        '
        'tsiOpenContainingFolder
        '
        Me.tsiOpenContainingFolder.Name = "tsiOpenContainingFolder"
        Me.tsiOpenContainingFolder.Size = New System.Drawing.Size(201, 22)
        Me.tsiOpenContainingFolder.Text = "Open Containing Folder"
        '
        'tsiFileSep1
        '
        Me.tsiFileSep1.Name = "tsiFileSep1"
        Me.tsiFileSep1.Size = New System.Drawing.Size(198, 6)
        '
        'tsiSaveAs
        '
        Me.tsiSaveAs.Name = "tsiSaveAs"
        Me.tsiSaveAs.Size = New System.Drawing.Size(201, 22)
        Me.tsiSaveAs.Text = "Save As..."
        '
        'tsiRename
        '
        Me.tsiRename.Name = "tsiRename"
        Me.tsiRename.Size = New System.Drawing.Size(201, 22)
        Me.tsiRename.Text = "Rename"
        '
        'tsiDelete
        '
        Me.tsiDelete.Name = "tsiDelete"
        Me.tsiDelete.Size = New System.Drawing.Size(201, 22)
        Me.tsiDelete.Text = "Delete"
        '
        'tsiFileSep2
        '
        Me.tsiFileSep2.Name = "tsiFileSep2"
        Me.tsiFileSep2.Size = New System.Drawing.Size(198, 6)
        '
        'tsiUploadFiles
        '
        Me.tsiUploadFiles.Name = "tsiUploadFiles"
        Me.tsiUploadFiles.Size = New System.Drawing.Size(201, 22)
        Me.tsiUploadFiles.Text = "Upload File(s)..."
        '
        'tsiUploadFolder
        '
        Me.tsiUploadFolder.Name = "tsiUploadFolder"
        Me.tsiUploadFolder.Size = New System.Drawing.Size(201, 22)
        Me.tsiUploadFolder.Text = "Upload Folder..."
        '
        'tsiNewFolder
        '
        Me.tsiNewFolder.Name = "tsiNewFolder"
        Me.tsiNewFolder.Size = New System.Drawing.Size(201, 22)
        Me.tsiNewFolder.Text = "New Folder"
        '
        'tsiFileSep3
        '
        Me.tsiFileSep3.Name = "tsiFileSep3"
        Me.tsiFileSep3.Size = New System.Drawing.Size(198, 6)
        '
        'tsiView
        '
        Me.tsiView.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiViewXlThumb, Me.tsiViewLgThumb, Me.tsiViewLgIcon, Me.tsiViewSmIcon, Me.tsiViewList, Me.tsiViewDetails, Me.tsiViewTiles})
        Me.tsiView.Name = "tsiView"
        Me.tsiView.Size = New System.Drawing.Size(201, 22)
        Me.tsiView.Text = "View"
        '
        'tsiViewXlThumb
        '
        Me.tsiViewXlThumb.Name = "tsiViewXlThumb"
        Me.tsiViewXlThumb.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewXlThumb.Text = "Extra Large Thumbnails"
        '
        'tsiViewLgThumb
        '
        Me.tsiViewLgThumb.Name = "tsiViewLgThumb"
        Me.tsiViewLgThumb.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewLgThumb.Text = "Large Thumbnails"
        '
        'tsiViewLgIcon
        '
        Me.tsiViewLgIcon.Name = "tsiViewLgIcon"
        Me.tsiViewLgIcon.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewLgIcon.Text = "Large Icons"
        '
        'tsiViewSmIcon
        '
        Me.tsiViewSmIcon.Name = "tsiViewSmIcon"
        Me.tsiViewSmIcon.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewSmIcon.Text = "Small Icons"
        '
        'tsiViewList
        '
        Me.tsiViewList.Name = "tsiViewList"
        Me.tsiViewList.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewList.Text = "List"
        '
        'tsiViewDetails
        '
        Me.tsiViewDetails.Name = "tsiViewDetails"
        Me.tsiViewDetails.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewDetails.Text = "Details"
        '
        'tsiViewTiles
        '
        Me.tsiViewTiles.Name = "tsiViewTiles"
        Me.tsiViewTiles.Size = New System.Drawing.Size(197, 22)
        Me.tsiViewTiles.Text = "Tiles"
        '
        'tsiRefresh
        '
        Me.tsiRefresh.Name = "tsiRefresh"
        Me.tsiRefresh.Size = New System.Drawing.Size(201, 22)
        Me.tsiRefresh.Text = "Refresh"
        '
        'ToolStrip1
        '
        Me.ToolStrip1.Dock = System.Windows.Forms.DockStyle.Bottom
        Me.ToolStrip1.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.ToolStrip1.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.ToolStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiFragmentation, Me.tsiCompression, Me.StatusLabel})
        Me.ToolStrip1.Location = New System.Drawing.Point(0, 636)
        Me.ToolStrip1.Name = "ToolStrip1"
        Me.ToolStrip1.Padding = New System.Windows.Forms.Padding(0, 0, 6, 0)
        Me.ToolStrip1.Size = New System.Drawing.Size(1084, 25)
        Me.ToolStrip1.TabIndex = 3
        Me.ToolStrip1.Text = "ToolStrip1"
        '
        'tsiFragmentation
        '
        Me.tsiFragmentation.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiFragmentation.AutoSize = False
        Me.tsiFragmentation.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiFragmentation.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiFragmentation.Name = "tsiFragmentation"
        Me.tsiFragmentation.Size = New System.Drawing.Size(100, 16)
        '
        'tsiCompression
        '
        Me.tsiCompression.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiCompression.Name = "tsiCompression"
        Me.tsiCompression.Size = New System.Drawing.Size(73, 22)
        Me.tsiCompression.Text = "Relative size:"
        '
        'StatusLabel
        '
        Me.StatusLabel.Name = "StatusLabel"
        Me.StatusLabel.Size = New System.Drawing.Size(39, 22)
        Me.StatusLabel.Text = "Status"
        '
        'pnlExplorer
        '
        Me.pnlExplorer.Controls.Add(Me.SplitExplorer)
        Me.pnlExplorer.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlExplorer.Location = New System.Drawing.Point(0, 25)
        Me.pnlExplorer.Name = "pnlExplorer"
        Me.pnlExplorer.Size = New System.Drawing.Size(1084, 611)
        Me.pnlExplorer.TabIndex = 1
        '
        'Large32Sizer
        '
        Me.Large32Sizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.Large32Sizer.ImageSize = New System.Drawing.Size(32, 32)
        Me.Large32Sizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'Thumbnail128Sizer
        '
        Me.Thumbnail128Sizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.Thumbnail128Sizer.ImageSize = New System.Drawing.Size(128, 128)
        Me.Thumbnail128Sizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'Thumbnail256Sizer
        '
        Me.Thumbnail256Sizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.Thumbnail256Sizer.ImageSize = New System.Drawing.Size(256, 256)
        Me.Thumbnail256Sizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'tsMain
        '
        Me.tsMain.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.tsMain.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.ToolStripDropDownButton1, Me.tsiBack, Me.tsiForward, Me.tsiSearch})
        Me.tsMain.Location = New System.Drawing.Point(0, 0)
        Me.tsMain.Name = "tsMain"
        Me.tsMain.Size = New System.Drawing.Size(1084, 25)
        Me.tsMain.TabIndex = 8
        Me.tsMain.Text = "ToolStrip2"
        '
        'ToolStripDropDownButton1
        '
        Me.ToolStripDropDownButton1.AutoToolTip = False
        Me.ToolStripDropDownButton1.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.MenuTextSeparator2, Me.tsiScan, Me.tsiDefrag, Me.MenuTextSeparator1, Me.EncryptToolStripMenuItem1, Me.ToolStripMenuItem1, Me.tsiExit})
        Me.ToolStripDropDownButton1.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.ToolStripDropDownButton1.Name = "ToolStripDropDownButton1"
        Me.ToolStripDropDownButton1.ShowDropDownArrow = False
        Me.ToolStripDropDownButton1.Size = New System.Drawing.Size(29, 22)
        Me.ToolStripDropDownButton1.Text = "&File"
        '
        'MenuTextSeparator2
        '
        Me.MenuTextSeparator2.AutoSize = False
        Me.MenuTextSeparator2.BackColor = System.Drawing.SystemColors.Control
        Me.MenuTextSeparator2.ForeColor = System.Drawing.SystemColors.ControlText
        Me.MenuTextSeparator2.Name = "MenuTextSeparator2"
        Me.MenuTextSeparator2.Size = New System.Drawing.Size(3840, 18)
        Me.MenuTextSeparator2.Text = "Tools"
        '
        'tsiScan
        '
        Me.tsiScan.Name = "tsiScan"
        Me.tsiScan.Size = New System.Drawing.Size(116, 22)
        Me.tsiScan.Text = "&Scan"
        '
        'tsiDefrag
        '
        Me.tsiDefrag.Name = "tsiDefrag"
        Me.tsiDefrag.Size = New System.Drawing.Size(116, 22)
        Me.tsiDefrag.Text = "&Defrag"
        '
        'MenuTextSeparator1
        '
        Me.MenuTextSeparator1.AutoSize = False
        Me.MenuTextSeparator1.BackColor = System.Drawing.SystemColors.Control
        Me.MenuTextSeparator1.ForeColor = System.Drawing.SystemColors.ControlText
        Me.MenuTextSeparator1.Name = "MenuTextSeparator1"
        Me.MenuTextSeparator1.Size = New System.Drawing.Size(3840, 18)
        Me.MenuTextSeparator1.Text = "Options"
        '
        'EncryptToolStripMenuItem1
        '
        Me.EncryptToolStripMenuItem1.Name = "EncryptToolStripMenuItem1"
        Me.EncryptToolStripMenuItem1.Size = New System.Drawing.Size(116, 22)
        Me.EncryptToolStripMenuItem1.Text = "&Encrypt"
        '
        'ToolStripMenuItem1
        '
        Me.ToolStripMenuItem1.Name = "ToolStripMenuItem1"
        Me.ToolStripMenuItem1.Size = New System.Drawing.Size(113, 6)
        '
        'tsiExit
        '
        Me.tsiExit.Name = "tsiExit"
        Me.tsiExit.Size = New System.Drawing.Size(116, 22)
        Me.tsiExit.Text = "E&xit"
        '
        'tsiBack
        '
        Me.tsiBack.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiBack.Image = CType(resources.GetObject("tsiBack.Image"), System.Drawing.Image)
        Me.tsiBack.Name = "tsiBack"
        Me.tsiBack.RightToLeftAutoMirrorImage = True
        Me.tsiBack.Size = New System.Drawing.Size(23, 22)
        Me.tsiBack.Text = "Move previous"
        '
        'tsiForward
        '
        Me.tsiForward.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiForward.Image = CType(resources.GetObject("tsiForward.Image"), System.Drawing.Image)
        Me.tsiForward.Name = "tsiForward"
        Me.tsiForward.RightToLeftAutoMirrorImage = True
        Me.tsiForward.Size = New System.Drawing.Size(23, 22)
        Me.tsiForward.Text = "Move next"
        '
        'tsiSearch
        '
        Me.tsiSearch.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiSearch.Name = "tsiSearch"
        Me.tsiSearch.Size = New System.Drawing.Size(100, 25)
        '
        'EmbeddedFileSystemBrowserForm
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(1084, 661)
        Me.Controls.Add(Me.pnlExplorer)
        Me.Controls.Add(Me.ToolStrip1)
        Me.Controls.Add(Me.tsMain)
        Me.KeyPreview = True
        Me.MinimumSize = New System.Drawing.Size(758, 474)
        Me.Name = "EmbeddedFileSystemBrowserForm"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent
        Me.Text = "Embedded File System"
        Me.SplitExplorer.Panel1.ResumeLayout(False)
        Me.SplitExplorer.Panel2.ResumeLayout(False)
        CType(Me.SplitExplorer, System.ComponentModel.ISupportInitialize).EndInit()
        Me.SplitExplorer.ResumeLayout(False)
        Me.FolderContextMenu.ResumeLayout(False)
        Me.FileContextMenu.ResumeLayout(False)
        Me.ToolStrip1.ResumeLayout(False)
        Me.ToolStrip1.PerformLayout()
        Me.pnlExplorer.ResumeLayout(False)
        Me.tsMain.ResumeLayout(False)
        Me.tsMain.PerformLayout()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents ToolStrip1 As ToolStrip
    Private components As IContainer
    Friend WithEvents StatusLabel As ToolStripLabel
    Friend WithEvents tsiCompression As ToolStripLabel
    Friend WithEvents pnlExplorer As Panel
    Friend WithEvents tsiFragmentation As ToolStripButton
    Friend WithEvents tsMain As ToolStrip
    Friend WithEvents ToolStripDropDownButton1 As ToolStripDropDownButton
    Friend WithEvents MenuTextSeparator2 As Controls.MenuTextSeparator
    Friend WithEvents tsiScan As ToolStripMenuItem
    Friend WithEvents tsiDefrag As ToolStripMenuItem
    Friend WithEvents MenuTextSeparator1 As Controls.MenuTextSeparator
    Friend WithEvents EncryptToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem1 As ToolStripSeparator
    Friend WithEvents tsiExit As ToolStripMenuItem
    Friend WithEvents tsiBack As ToolStripButton
    Friend WithEvents tsiForward As ToolStripButton
    Friend WithEvents tsiSearch As Controls.ToolStripSpringTextBox
End Class
