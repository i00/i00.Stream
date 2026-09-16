Imports System.ComponentModel
Imports System.Diagnostics

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class EmbeddedFileSystemBrowserForm
    Inherits Form

    Private WithEvents SplitExplorer As Controls.SplitContainer
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
    Friend WithEvents tsiRefresh As ToolStripMenuItem
    Private WithEvents colName As ColumnHeader
    Private WithEvents colSize As ColumnHeader
    Private WithEvents colState As ColumnHeader
    Private WithEvents tvFolders As TreeView
    Private WithEvents lvFiles As ListView
    Friend WithEvents SmallSizer As ImageList
    Friend WithEvents ThumbnailSizer As ImageList

    <DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(EmbeddedFileSystemBrowserForm))
        Me.SplitExplorer = New i00.EmbeddedFileSystemSample.Controls.SplitContainer()
        Me.tvFolders = New System.Windows.Forms.TreeView()
        Me.SmallSizer = New System.Windows.Forms.ImageList(Me.components)
        Me.lvFiles = New System.Windows.Forms.ListView()
        Me.colName = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.colSize = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.colState = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me.FileContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.tsiOpen = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiOpenContainingFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiCut = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiCopy = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiPaste = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiSaveAs = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem3 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiDelete = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiRename = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep2 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiUploadFiles = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiUploadFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiFileSep3 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiView = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiSortBy = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiRefresh = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem5 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiNewFolder = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem2 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiProperties = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStrip1 = New System.Windows.Forms.ToolStrip()
        Me.tsiFragmentation = New System.Windows.Forms.ToolStripButton()
        Me.tsiCompression = New System.Windows.Forms.ToolStripLabel()
        Me.StatusLabel = New System.Windows.Forms.ToolStripLabel()
        Me.pnlExplorer = New System.Windows.Forms.Panel()
        Me.ThumbnailSizer = New System.Windows.Forms.ImageList(Me.components)
        Me.tsMain = New System.Windows.Forms.ToolStrip()
        Me.tsiFile = New System.Windows.Forms.ToolStripDropDownButton()
        Me.OpenToolStripMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiExit = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiTools = New System.Windows.Forms.ToolStripDropDownButton()
        Me.MenuTextSeparator2 = New i00.EmbeddedFileSystemSample.Controls.MenuTextSeparator()
        Me.tsiScan = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiScanQuick = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiScanExtended = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDefrag = New System.Windows.Forms.ToolStripMenuItem()
        Me.MenuTextSeparator1 = New i00.EmbeddedFileSystemSample.Controls.MenuTextSeparator()
        Me.tsiEncrypt = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiBack = New System.Windows.Forms.ToolStripButton()
        Me.tsiForward = New System.Windows.Forms.ToolStripButton()
        Me.tsiSearch = New i00.EmbeddedFileSystemSample.Controls.ToolStripSpringTextBox()
        Me.DropMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.tsiDropCopy = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDropMove = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem4 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiDropExtract = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDropExtractEachToOwnFolder = New System.Windows.Forms.ToolStripMenuItem()
        CType(Me.SplitExplorer, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SplitExplorer.Panel1.SuspendLayout()
        Me.SplitExplorer.Panel2.SuspendLayout()
        Me.SplitExplorer.SuspendLayout()
        Me.FileContextMenu.SuspendLayout()
        Me.ToolStrip1.SuspendLayout()
        Me.pnlExplorer.SuspendLayout()
        Me.tsMain.SuspendLayout()
        Me.DropMenu.SuspendLayout()
        Me.SuspendLayout()
        '
        'SplitExplorer
        '
        Me.SplitExplorer.Dock = System.Windows.Forms.DockStyle.Fill
        Me.SplitExplorer.DrawSplitBorder1 = True
        Me.SplitExplorer.DrawSplitBorder2 = True
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
        Me.tvFolders.BorderStyle = System.Windows.Forms.BorderStyle.None
        Me.tvFolders.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvFolders.DrawMode = System.Windows.Forms.TreeViewDrawMode.OwnerDrawAll
        Me.tvFolders.HideSelection = False
        Me.tvFolders.ImageIndex = 0
        Me.tvFolders.ImageList = Me.SmallSizer
        Me.tvFolders.Location = New System.Drawing.Point(0, 0)
        Me.tvFolders.Name = "tvFolders"
        Me.tvFolders.SelectedImageIndex = 0
        Me.tvFolders.ShowNodeToolTips = True
        Me.tvFolders.Size = New System.Drawing.Size(320, 611)
        Me.tvFolders.TabIndex = 0
        '
        'SmallSizer
        '
        Me.SmallSizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.SmallSizer.ImageSize = New System.Drawing.Size(16, 16)
        Me.SmallSizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'lvFiles
        '
        Me.lvFiles.AllowDrop = True
        Me.lvFiles.BorderStyle = System.Windows.Forms.BorderStyle.None
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
        'FileContextMenu
        '
        Me.FileContextMenu.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiOpen, Me.tsiOpenContainingFolder, Me.tsiFileSep1, Me.tsiCut, Me.tsiCopy, Me.tsiPaste, Me.tsiSaveAs, Me.ToolStripMenuItem3, Me.tsiDelete, Me.tsiRename, Me.tsiFileSep2, Me.tsiUploadFiles, Me.tsiUploadFolder, Me.tsiFileSep3, Me.tsiView, Me.tsiSortBy, Me.tsiRefresh, Me.ToolStripMenuItem5, Me.tsiNewFolder, Me.ToolStripMenuItem2, Me.tsiProperties})
        Me.FileContextMenu.Name = "FileContextMenu"
        Me.FileContextMenu.Size = New System.Drawing.Size(202, 370)
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
        'tsiCut
        '
        Me.tsiCut.Name = "tsiCut"
        Me.tsiCut.Size = New System.Drawing.Size(201, 22)
        Me.tsiCut.Text = "Cut"
        '
        'tsiCopy
        '
        Me.tsiCopy.Name = "tsiCopy"
        Me.tsiCopy.Size = New System.Drawing.Size(201, 22)
        Me.tsiCopy.Text = "Copy"
        '
        'tsiPaste
        '
        Me.tsiPaste.Name = "tsiPaste"
        Me.tsiPaste.Size = New System.Drawing.Size(201, 22)
        Me.tsiPaste.Text = "Paste"
        '
        'tsiSaveAs
        '
        Me.tsiSaveAs.BackColor = System.Drawing.Color.LightPink
        Me.tsiSaveAs.Name = "tsiSaveAs"
        Me.tsiSaveAs.Size = New System.Drawing.Size(201, 22)
        Me.tsiSaveAs.Text = "Save As..."
        '
        'ToolStripMenuItem3
        '
        Me.ToolStripMenuItem3.Name = "ToolStripMenuItem3"
        Me.ToolStripMenuItem3.Size = New System.Drawing.Size(198, 6)
        '
        'tsiDelete
        '
        Me.tsiDelete.Name = "tsiDelete"
        Me.tsiDelete.Size = New System.Drawing.Size(201, 22)
        Me.tsiDelete.Text = "Delete"
        '
        'tsiRename
        '
        Me.tsiRename.Name = "tsiRename"
        Me.tsiRename.Size = New System.Drawing.Size(201, 22)
        Me.tsiRename.Text = "Rename"
        '
        'tsiFileSep2
        '
        Me.tsiFileSep2.Name = "tsiFileSep2"
        Me.tsiFileSep2.Size = New System.Drawing.Size(198, 6)
        '
        'tsiUploadFiles
        '
        Me.tsiUploadFiles.BackColor = System.Drawing.Color.LightPink
        Me.tsiUploadFiles.Name = "tsiUploadFiles"
        Me.tsiUploadFiles.Size = New System.Drawing.Size(201, 22)
        Me.tsiUploadFiles.Text = "Upload File(s)..."
        '
        'tsiUploadFolder
        '
        Me.tsiUploadFolder.BackColor = System.Drawing.Color.LightPink
        Me.tsiUploadFolder.Name = "tsiUploadFolder"
        Me.tsiUploadFolder.Size = New System.Drawing.Size(201, 22)
        Me.tsiUploadFolder.Text = "Upload Folder..."
        '
        'tsiFileSep3
        '
        Me.tsiFileSep3.Name = "tsiFileSep3"
        Me.tsiFileSep3.Size = New System.Drawing.Size(198, 6)
        '
        'tsiView
        '
        Me.tsiView.Name = "tsiView"
        Me.tsiView.Size = New System.Drawing.Size(201, 22)
        Me.tsiView.Text = "View"
        '
        'tsiSortBy
        '
        Me.tsiSortBy.Name = "tsiSortBy"
        Me.tsiSortBy.Size = New System.Drawing.Size(201, 22)
        Me.tsiSortBy.Text = "Sort By"
        '
        'tsiRefresh
        '
        Me.tsiRefresh.Name = "tsiRefresh"
        Me.tsiRefresh.Size = New System.Drawing.Size(201, 22)
        Me.tsiRefresh.Text = "Refresh"
        '
        'ToolStripMenuItem5
        '
        Me.ToolStripMenuItem5.Name = "ToolStripMenuItem5"
        Me.ToolStripMenuItem5.Size = New System.Drawing.Size(198, 6)
        '
        'tsiNewFolder
        '
        Me.tsiNewFolder.Name = "tsiNewFolder"
        Me.tsiNewFolder.Size = New System.Drawing.Size(201, 22)
        Me.tsiNewFolder.Text = "New Folder"
        '
        'ToolStripMenuItem2
        '
        Me.ToolStripMenuItem2.Name = "ToolStripMenuItem2"
        Me.ToolStripMenuItem2.Size = New System.Drawing.Size(198, 6)
        '
        'tsiProperties
        '
        Me.tsiProperties.BackColor = System.Drawing.Color.LightYellow
        Me.tsiProperties.Name = "tsiProperties"
        Me.tsiProperties.Size = New System.Drawing.Size(201, 22)
        Me.tsiProperties.Text = "Properties"
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
        Me.tsiFragmentation.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiFragmentation.Name = "tsiFragmentation"
        Me.tsiFragmentation.Size = New System.Drawing.Size(92, 22)
        Me.tsiFragmentation.Text = "Fragmentation:"
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
        'ThumbnailSizer
        '
        Me.ThumbnailSizer.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        Me.ThumbnailSizer.ImageSize = New System.Drawing.Size(256, 256)
        Me.ThumbnailSizer.TransparentColor = System.Drawing.Color.Transparent
        '
        'tsMain
        '
        Me.tsMain.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.tsMain.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiFile, Me.tsiTools, Me.tsiBack, Me.tsiForward, Me.tsiSearch})
        Me.tsMain.Location = New System.Drawing.Point(0, 0)
        Me.tsMain.Name = "tsMain"
        Me.tsMain.Size = New System.Drawing.Size(1084, 25)
        Me.tsMain.TabIndex = 8
        Me.tsMain.Text = "ToolStrip2"
        '
        'tsiFile
        '
        Me.tsiFile.AutoToolTip = False
        Me.tsiFile.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.OpenToolStripMenuItem, Me.ToolStripMenuItem1, Me.tsiExit})
        Me.tsiFile.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiFile.Name = "tsiFile"
        Me.tsiFile.ShowDropDownArrow = False
        Me.tsiFile.Size = New System.Drawing.Size(29, 22)
        Me.tsiFile.Text = "&File"
        '
        'OpenToolStripMenuItem
        '
        Me.OpenToolStripMenuItem.Name = "OpenToolStripMenuItem"
        Me.OpenToolStripMenuItem.Size = New System.Drawing.Size(103, 22)
        Me.OpenToolStripMenuItem.Text = "&Open"
        '
        'ToolStripMenuItem1
        '
        Me.ToolStripMenuItem1.Name = "ToolStripMenuItem1"
        Me.ToolStripMenuItem1.Size = New System.Drawing.Size(100, 6)
        '
        'tsiExit
        '
        Me.tsiExit.Name = "tsiExit"
        Me.tsiExit.Size = New System.Drawing.Size(103, 22)
        Me.tsiExit.Text = "E&xit"
        '
        'tsiTools
        '
        Me.tsiTools.AutoToolTip = False
        Me.tsiTools.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.MenuTextSeparator2, Me.tsiScan, Me.tsiDefrag, Me.MenuTextSeparator1, Me.tsiEncrypt})
        Me.tsiTools.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiTools.Name = "tsiTools"
        Me.tsiTools.ShowDropDownArrow = False
        Me.tsiTools.Size = New System.Drawing.Size(39, 22)
        Me.tsiTools.Text = "&Tools"
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
        Me.tsiScan.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiScanQuick, Me.tsiScanExtended})
        Me.tsiScan.Name = "tsiScan"
        Me.tsiScan.Size = New System.Drawing.Size(116, 22)
        Me.tsiScan.Text = "&Scan"
        '
        'tsiScanQuick
        '
        Me.tsiScanQuick.Name = "tsiScanQuick"
        Me.tsiScanQuick.Size = New System.Drawing.Size(122, 22)
        Me.tsiScanQuick.Text = "&Quick"
        '
        'tsiScanExtended
        '
        Me.tsiScanExtended.Name = "tsiScanExtended"
        Me.tsiScanExtended.Size = New System.Drawing.Size(122, 22)
        Me.tsiScanExtended.Text = "&Extended"
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
        'tsiEncrypt
        '
        Me.tsiEncrypt.Name = "tsiEncrypt"
        Me.tsiEncrypt.Size = New System.Drawing.Size(116, 22)
        Me.tsiEncrypt.Text = "&Encrypt"
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
        Me.tsiSearch.Font = New System.Drawing.Font("Segoe UI", 9.0!)
        Me.tsiSearch.Name = "tsiSearch"
        Me.tsiSearch.Size = New System.Drawing.Size(100, 25)
        '
        'DropMenu
        '
        Me.DropMenu.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiDropCopy, Me.tsiDropMove, Me.ToolStripMenuItem4, Me.tsiDropExtract, Me.tsiDropExtractEachToOwnFolder})
        Me.DropMenu.Name = "FileDragMenu"
        Me.DropMenu.Size = New System.Drawing.Size(268, 98)
        '
        'tsiDropCopy
        '
        Me.tsiDropCopy.Name = "tsiDropCopy"
        Me.tsiDropCopy.Size = New System.Drawing.Size(267, 22)
        Me.tsiDropCopy.Text = "&Copy here"
        '
        'tsiDropMove
        '
        Me.tsiDropMove.BackColor = System.Drawing.Color.LightYellow
        Me.tsiDropMove.Name = "tsiDropMove"
        Me.tsiDropMove.Size = New System.Drawing.Size(267, 22)
        Me.tsiDropMove.Text = "&Move here"
        '
        'ToolStripMenuItem4
        '
        Me.ToolStripMenuItem4.Name = "ToolStripMenuItem4"
        Me.ToolStripMenuItem4.Size = New System.Drawing.Size(264, 6)
        '
        'tsiDropExtract
        '
        Me.tsiDropExtract.Name = "tsiDropExtract"
        Me.tsiDropExtract.Size = New System.Drawing.Size(267, 22)
        Me.tsiDropExtract.Text = "&Extract here"
        '
        'tsiDropExtractEachToOwnFolder
        '
        Me.tsiDropExtractEachToOwnFolder.Name = "tsiDropExtractEachToOwnFolder"
        Me.tsiDropExtractEachToOwnFolder.Size = New System.Drawing.Size(267, 22)
        Me.tsiDropExtractEachToOwnFolder.Text = "Extract each archive to its own &folder"
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
        Me.FileContextMenu.ResumeLayout(False)
        Me.ToolStrip1.ResumeLayout(False)
        Me.ToolStrip1.PerformLayout()
        Me.pnlExplorer.ResumeLayout(False)
        Me.tsMain.ResumeLayout(False)
        Me.tsMain.PerformLayout()
        Me.DropMenu.ResumeLayout(False)
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
    Friend WithEvents tsiFile As ToolStripDropDownButton
    Friend WithEvents MenuTextSeparator2 As Controls.MenuTextSeparator
    Friend WithEvents tsiScan As ToolStripMenuItem
    Friend WithEvents tsiDefrag As ToolStripMenuItem
    Friend WithEvents MenuTextSeparator1 As Controls.MenuTextSeparator
    Friend WithEvents tsiEncrypt As ToolStripMenuItem
    Friend WithEvents tsiExit As ToolStripMenuItem
    Friend WithEvents tsiBack As ToolStripButton
    Friend WithEvents tsiForward As ToolStripButton
    Friend WithEvents tsiSearch As Controls.ToolStripSpringTextBox
    Friend WithEvents tsiCut As ToolStripMenuItem
    Friend WithEvents tsiCopy As ToolStripMenuItem
    Friend WithEvents tsiPaste As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem3 As ToolStripSeparator
    Friend WithEvents tsiSortBy As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem5 As ToolStripSeparator
    Friend WithEvents ToolStripMenuItem2 As ToolStripSeparator
    Friend WithEvents tsiProperties As ToolStripMenuItem
    Friend WithEvents tsiTools As ToolStripDropDownButton
    Friend WithEvents OpenToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem1 As ToolStripSeparator
    Friend WithEvents DropMenu As ContextMenuStrip
    Friend WithEvents tsiDropCopy As ToolStripMenuItem
    Friend WithEvents tsiDropMove As ToolStripMenuItem
    Friend WithEvents tsiDropExtract As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem4 As ToolStripSeparator
    Friend WithEvents tsiDropExtractEachToOwnFolder As ToolStripMenuItem
    Friend WithEvents tsiScanQuick As ToolStripMenuItem
    Friend WithEvents tsiScanExtended As ToolStripMenuItem
End Class
