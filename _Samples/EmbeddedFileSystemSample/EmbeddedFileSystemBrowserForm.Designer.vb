Imports System.ComponentModel
Imports System.Diagnostics

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class EmbeddedFileSystemBrowserForm

    Private Components As IContainer

    Private _SplitContainer As SplitContainer
    Private _StatusStrip As StatusStrip
    Private _StatusLabel As ToolStripStatusLabel
    Private WithEvents _FolderContextMenu As ContextMenuStrip
    Private WithEvents _FileContextMenu As ContextMenuStrip
    Private WithEvents _EmptyFileContextMenu As ContextMenuStrip
    Private _NameColumnHeader As ColumnHeader
    Private _SizeColumnHeader As ColumnHeader
    Private _StateColumnHeader As ColumnHeader
    Friend WithEvents tvFolders As TreeView
    Friend WithEvents lvFiles As ListView

    <DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Components = New Container()
        _SplitContainer = New SplitContainer()
        tvFolders = New TreeView()
        lvFiles = New ListView()
        _NameColumnHeader = New ColumnHeader()
        _SizeColumnHeader = New ColumnHeader()
        _StateColumnHeader = New ColumnHeader()
        _StatusStrip = New StatusStrip()
        _StatusLabel = New ToolStripStatusLabel()
        _FolderContextMenu = New ContextMenuStrip(Components)
        _FileContextMenu = New ContextMenuStrip(Components)
        _EmptyFileContextMenu = New ContextMenuStrip(Components)
        CType(_SplitContainer, ISupportInitialize).BeginInit()
        _SplitContainer.Panel1.SuspendLayout()
        _SplitContainer.Panel2.SuspendLayout()
        _SplitContainer.SuspendLayout()
        _StatusStrip.SuspendLayout()
        SuspendLayout()
        '
        ' _SplitContainer
        '
        _SplitContainer.Dock = DockStyle.Fill
        _SplitContainer.Location = New Point(0, 0)
        _SplitContainer.Name = "_SplitContainer"
        '
        ' _SplitContainer.Panel1
        '
        _SplitContainer.Panel1.Controls.Add(tvFolders)
        _SplitContainer.Panel1MinSize = 180
        '
        ' _SplitContainer.Panel2
        '
        _SplitContainer.Panel2.Controls.Add(lvFiles)
        _SplitContainer.Size = New Size(1084, 639)
        _SplitContainer.SplitterDistance = 320
        _SplitContainer.TabIndex = 0
        '
        ' tvFolders
        '
        tvFolders.AllowDrop = True
        tvFolders.ContextMenuStrip = _FolderContextMenu
        tvFolders.Dock = DockStyle.Fill
        tvFolders.HideSelection = False
        tvFolders.LabelEdit = False
        tvFolders.Location = New Point(0, 0)
        tvFolders.Name = "tvFolders"
        tvFolders.ShowNodeToolTips = True
        tvFolders.Size = New Size(320, 639)
        tvFolders.TabIndex = 0
        '
        ' lvFiles
        '
        lvFiles.AllowDrop = True
        lvFiles.Columns.AddRange(New ColumnHeader() {_NameColumnHeader, _SizeColumnHeader, _StateColumnHeader})
        lvFiles.ContextMenuStrip = _EmptyFileContextMenu
        lvFiles.Dock = DockStyle.Fill
        lvFiles.FullRowSelect = True
        lvFiles.GridLines = False
        lvFiles.HideSelection = False
        lvFiles.Location = New Point(0, 0)
        lvFiles.MultiSelect = True
        lvFiles.Name = "lvFiles"
        lvFiles.Size = New Size(760, 639)
        lvFiles.TabIndex = 0
        lvFiles.UseCompatibleStateImageBehavior = False
        lvFiles.View = View.Details
        '
        ' _NameColumnHeader
        '
        _NameColumnHeader.Text = "Name"
        _NameColumnHeader.Width = 420
        '
        ' _SizeColumnHeader
        '
        _SizeColumnHeader.Text = "Size"
        _SizeColumnHeader.TextAlign = HorizontalAlignment.Right
        _SizeColumnHeader.Width = 120
        '
        ' _StateColumnHeader
        '
        _StateColumnHeader.Text = "State"
        _StateColumnHeader.Width = 120
        '
        ' _StatusStrip
        '
        _StatusStrip.Items.AddRange(New ToolStripItem() {_StatusLabel})
        _StatusStrip.Location = New Point(0, 639)
        _StatusStrip.Name = "_StatusStrip"
        _StatusStrip.Size = New Size(1084, 22)
        _StatusStrip.TabIndex = 1
        '
        ' _StatusLabel
        '
        _StatusLabel.Name = "_StatusLabel"
        _StatusLabel.Size = New Size(0, 17)
        '
        ' EmbeddedFileSystemBrowserForm
        '
        AutoScaleDimensions = New SizeF(6.0F, 13.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(1084, 661)
        Controls.Add(_SplitContainer)
        Controls.Add(_StatusStrip)
        KeyPreview = True
        MinimumSize = New Size(760, 480)
        Name = "EmbeddedFileSystemBrowserForm"
        StartPosition = FormStartPosition.CenterParent
        Text = "Embedded File System"
        _SplitContainer.Panel1.ResumeLayout(False)
        _SplitContainer.Panel2.ResumeLayout(False)
        CType(_SplitContainer, ISupportInitialize).EndInit()
        _SplitContainer.ResumeLayout(False)
        _StatusStrip.ResumeLayout(False)
        _StatusStrip.PerformLayout()
        ResumeLayout(False)
        PerformLayout()
    End Sub
End Class
