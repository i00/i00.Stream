Imports System.ComponentModel
Imports System.Diagnostics

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class EmbeddedFileSystemBrowserForm
    Inherits Form

    Private WithEvents _SplitContainer As i00CodeLib.Controls.SplitContainer
    Private WithEvents _FolderContextMenu As ContextMenuStrip
    Private WithEvents _FileContextMenu As ContextMenuStrip
    Private WithEvents _EmptyFileContextMenu As ContextMenuStrip
    Private WithEvents _NameColumnHeader As ColumnHeader
    Private WithEvents _SizeColumnHeader As ColumnHeader
    Private WithEvents _StateColumnHeader As ColumnHeader
    Private WithEvents tvFolders As TreeView
    Private WithEvents lvFiles As ListView

    <DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Me._SplitContainer = New i00CodeLib.Controls.SplitContainer()
        Me.tvFolders = New System.Windows.Forms.TreeView()
        Me._FolderContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.lvFiles = New System.Windows.Forms.ListView()
        Me._NameColumnHeader = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me._SizeColumnHeader = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me._StateColumnHeader = CType(New System.Windows.Forms.ColumnHeader(), System.Windows.Forms.ColumnHeader)
        Me._EmptyFileContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me._FileContextMenu = New System.Windows.Forms.ContextMenuStrip(Me.components)
        Me.ToolStrip1 = New System.Windows.Forms.ToolStrip()
        Me.tsiFragmentation = New System.Windows.Forms.ToolStripButton()
        Me.tsiCompressionBar = New i00CodeLib.Controls.NeroBarToolStripItem()
        Me.tsiCompression = New System.Windows.Forms.ToolStripLabel()
        Me.StatusLabel = New System.Windows.Forms.ToolStripLabel()
        Me.pnlExplorer = New System.Windows.Forms.Panel()
        Me.ToolStrip2 = New System.Windows.Forms.ToolStrip()
        Me.tsiSearch = New System.Windows.Forms.ToolStripTextBox()
        Me.ToolStripLabel1 = New System.Windows.Forms.ToolStripLabel()
        Me.ToolStripDropDownButton1 = New System.Windows.Forms.ToolStripDropDownButton()
        Me.MenuTextSeparator2 = New i00CodeLib.Controls.MenuTextSeparator()
        Me.tsiScan = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiDefrag = New System.Windows.Forms.ToolStripMenuItem()
        Me.MenuTextSeparator1 = New i00CodeLib.Controls.MenuTextSeparator()
        Me.EncryptToolStripMenuItem1 = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiExit = New System.Windows.Forms.ToolStripMenuItem()
        CType(Me._SplitContainer, System.ComponentModel.ISupportInitialize).BeginInit()
        Me._SplitContainer.Panel1.SuspendLayout()
        Me._SplitContainer.Panel2.SuspendLayout()
        Me._SplitContainer.SuspendLayout()
        Me.ToolStrip1.SuspendLayout()
        Me.pnlExplorer.SuspendLayout()
        Me.ToolStrip2.SuspendLayout()
        Me.SuspendLayout()
        '
        '_SplitContainer
        '
        Me._SplitContainer.Dock = System.Windows.Forms.DockStyle.Fill
        Me._SplitContainer.GripSpacing = 1.5!
        Me._SplitContainer.Location = New System.Drawing.Point(0, 0)
        Me._SplitContainer.Name = "_SplitContainer"
        '
        '_SplitContainer.Panel1
        '
        Me._SplitContainer.Panel1.Controls.Add(Me.tvFolders)
        Me._SplitContainer.Panel1MinSize = 180
        '
        '_SplitContainer.Panel2
        '
        Me._SplitContainer.Panel2.Controls.Add(Me.lvFiles)
        Me._SplitContainer.Size = New System.Drawing.Size(1084, 611)
        Me._SplitContainer.SplitterDistance = 320
        Me._SplitContainer.SplitterWidth = 8
        Me._SplitContainer.TabIndex = 0
        '
        'tvFolders
        '
        Me.tvFolders.AllowDrop = True
        Me.tvFolders.ContextMenuStrip = Me._FolderContextMenu
        Me.tvFolders.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvFolders.HideSelection = False
        Me.tvFolders.Location = New System.Drawing.Point(0, 0)
        Me.tvFolders.Name = "tvFolders"
        Me.tvFolders.ShowNodeToolTips = True
        Me.tvFolders.Size = New System.Drawing.Size(320, 611)
        Me.tvFolders.TabIndex = 0
        '
        '_FolderContextMenu
        '
        Me._FolderContextMenu.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me._FolderContextMenu.Name = "_FolderContextMenu"
        Me._FolderContextMenu.Size = New System.Drawing.Size(61, 4)
        '
        'lvFiles
        '
        Me.lvFiles.AllowDrop = True
        Me.lvFiles.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {Me._NameColumnHeader, Me._SizeColumnHeader, Me._StateColumnHeader})
        Me.lvFiles.ContextMenuStrip = Me._EmptyFileContextMenu
        Me.lvFiles.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lvFiles.FullRowSelect = True
        Me.lvFiles.HideSelection = False
        Me.lvFiles.Location = New System.Drawing.Point(0, 0)
        Me.lvFiles.Name = "lvFiles"
        Me.lvFiles.Size = New System.Drawing.Size(756, 611)
        Me.lvFiles.TabIndex = 0
        Me.lvFiles.UseCompatibleStateImageBehavior = False
        Me.lvFiles.View = System.Windows.Forms.View.Details
        '
        '_NameColumnHeader
        '
        Me._NameColumnHeader.Text = "Name"
        Me._NameColumnHeader.Width = 420
        '
        '_SizeColumnHeader
        '
        Me._SizeColumnHeader.Text = "Size"
        Me._SizeColumnHeader.TextAlign = System.Windows.Forms.HorizontalAlignment.Right
        Me._SizeColumnHeader.Width = 120
        '
        '_StateColumnHeader
        '
        Me._StateColumnHeader.Text = "State"
        Me._StateColumnHeader.Width = 120
        '
        '_EmptyFileContextMenu
        '
        Me._EmptyFileContextMenu.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me._EmptyFileContextMenu.Name = "_EmptyFileContextMenu"
        Me._EmptyFileContextMenu.Size = New System.Drawing.Size(61, 4)
        '
        '_FileContextMenu
        '
        Me._FileContextMenu.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me._FileContextMenu.Name = "_FileContextMenu"
        Me._FileContextMenu.Size = New System.Drawing.Size(61, 4)
        '
        'ToolStrip1
        '
        Me.ToolStrip1.Dock = System.Windows.Forms.DockStyle.Bottom
        Me.ToolStrip1.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.ToolStrip1.ImageScalingSize = New System.Drawing.Size(24, 24)
        Me.ToolStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiFragmentation, Me.tsiCompressionBar, Me.tsiCompression, Me.StatusLabel})
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
        Me.tsiFragmentation.Text = "ToolStripButton1"
        '
        'tsiCompressionBar
        '
        Me.tsiCompressionBar.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiCompressionBar.AutoSize = False
        Me.tsiCompressionBar.BackColor = System.Drawing.Color.Transparent
        Me.tsiCompressionBar.GlowPause = 1
        Me.tsiCompressionBar.GlowSpeed = 1
        Me.tsiCompressionBar.Name = "tsiCompressionBar"
        Me.tsiCompressionBar.Padding = New System.Windows.Forms.Padding(0, 1, 0, 2)
        Me.tsiCompressionBar.PercentageBasedOn = i00CodeLib.Controls.NeroBar.NeroBarPercentageCalculationModes.WholeControl
        Me.tsiCompressionBar.SegmentCount = i00CodeLib.Controls.NeroBar.NeroBarSegments.One
        Me.tsiCompressionBar.Size = New System.Drawing.Size(100, 15)
        Me.tsiCompressionBar.Text = "NeroBarToolStripItem1"
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
        Me.pnlExplorer.Controls.Add(Me._SplitContainer)
        Me.pnlExplorer.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlExplorer.Location = New System.Drawing.Point(0, 25)
        Me.pnlExplorer.Name = "pnlExplorer"
        Me.pnlExplorer.Size = New System.Drawing.Size(1084, 611)
        Me.pnlExplorer.TabIndex = 1
        '
        'ToolStrip2
        '
        Me.ToolStrip2.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.ToolStrip2.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiSearch, Me.ToolStripLabel1, Me.ToolStripDropDownButton1})
        Me.ToolStrip2.Location = New System.Drawing.Point(0, 0)
        Me.ToolStrip2.Name = "ToolStrip2"
        Me.ToolStrip2.Size = New System.Drawing.Size(1084, 25)
        Me.ToolStrip2.TabIndex = 4
        Me.ToolStrip2.Text = "ToolStrip2"
        '
        'tsiSearch
        '
        Me.tsiSearch.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiSearch.Font = New System.Drawing.Font("Segoe UI", 9.0!)
        Me.tsiSearch.Name = "tsiSearch"
        Me.tsiSearch.Size = New System.Drawing.Size(100, 25)
        '
        'ToolStripLabel1
        '
        Me.ToolStripLabel1.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.ToolStripLabel1.Name = "ToolStripLabel1"
        Me.ToolStripLabel1.Size = New System.Drawing.Size(42, 22)
        Me.ToolStripLabel1.Text = "Search"
        '
        'ToolStripDropDownButton1
        '
        Me.ToolStripDropDownButton1.AutoToolTip = False
        Me.ToolStripDropDownButton1.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.MenuTextSeparator2, Me.tsiScan, Me.tsiDefrag, Me.MenuTextSeparator1, Me.EncryptToolStripMenuItem1, Me.ToolStripMenuItem1, Me.tsiExit})
        Me.ToolStripDropDownButton1.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.ToolStripDropDownButton1.Name = "ToolStripDropDownButton1"
        Me.ToolStripDropDownButton1.Size = New System.Drawing.Size(38, 22)
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
        Me.tsiScan.Size = New System.Drawing.Size(180, 22)
        Me.tsiScan.Text = "&Scan"
        '
        'tsiDefrag
        '
        Me.tsiDefrag.Name = "tsiDefrag"
        Me.tsiDefrag.Size = New System.Drawing.Size(180, 22)
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
        Me.EncryptToolStripMenuItem1.Size = New System.Drawing.Size(180, 22)
        Me.EncryptToolStripMenuItem1.Text = "&Encrypt"
        '
        'ToolStripMenuItem1
        '
        Me.ToolStripMenuItem1.Name = "ToolStripMenuItem1"
        Me.ToolStripMenuItem1.Size = New System.Drawing.Size(177, 6)
        '
        'tsiExit
        '
        Me.tsiExit.Name = "tsiExit"
        Me.tsiExit.Size = New System.Drawing.Size(180, 22)
        Me.tsiExit.Text = "E&xit"
        '
        'EmbeddedFileSystemBrowserForm
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(1084, 661)
        Me.Controls.Add(Me.pnlExplorer)
        Me.Controls.Add(Me.ToolStrip1)
        Me.Controls.Add(Me.ToolStrip2)
        Me.KeyPreview = True
        Me.MinimumSize = New System.Drawing.Size(760, 480)
        Me.Name = "EmbeddedFileSystemBrowserForm"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent
        Me.Text = "Embedded File System"
        Me._SplitContainer.Panel1.ResumeLayout(False)
        Me._SplitContainer.Panel2.ResumeLayout(False)
        CType(Me._SplitContainer, System.ComponentModel.ISupportInitialize).EndInit()
        Me._SplitContainer.ResumeLayout(False)
        Me.ToolStrip1.ResumeLayout(False)
        Me.ToolStrip1.PerformLayout()
        Me.pnlExplorer.ResumeLayout(False)
        Me.ToolStrip2.ResumeLayout(False)
        Me.ToolStrip2.PerformLayout()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents ToolStrip1 As ToolStrip
    Private components As IContainer
    Friend WithEvents StatusLabel As ToolStripLabel
    Friend WithEvents tsiCompressionBar As i00CodeLib.Controls.NeroBarToolStripItem
    Friend WithEvents tsiCompression As ToolStripLabel
    Friend WithEvents pnlExplorer As Panel
    Friend WithEvents tsiFragmentation As ToolStripButton
    Friend WithEvents ToolStrip2 As ToolStrip
    Friend WithEvents tsiSearch As ToolStripTextBox
    Friend WithEvents ToolStripLabel1 As ToolStripLabel
    Friend WithEvents ToolStripDropDownButton1 As ToolStripDropDownButton
    Friend WithEvents tsiExit As ToolStripMenuItem
    Friend WithEvents tsiDefrag As ToolStripMenuItem
    Friend WithEvents ToolStripMenuItem1 As ToolStripSeparator
    Friend WithEvents EncryptToolStripMenuItem1 As ToolStripMenuItem
    Friend WithEvents MenuTextSeparator1 As i00CodeLib.Controls.MenuTextSeparator
    Friend WithEvents MenuTextSeparator2 As i00CodeLib.Controls.MenuTextSeparator
    Friend WithEvents tsiScan As ToolStripMenuItem
End Class
