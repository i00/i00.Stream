<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class UnitTestTabControl
    Inherits System.Windows.Forms.UserControl

    'UserControl overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(UnitTestTabControl))
        Me.NotifyIcon = New System.Windows.Forms.NotifyIcon(Me.components)
        Me.tvTests = New Global.BrightIdeasSoftware2012.TreeListView()
        Me.ToolStrip1 = New System.Windows.Forms.ToolStrip()
        Me.tsiRun = New System.Windows.Forms.ToolStripSplitButton()
        Me.tsiRunSelected = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiRunAll = New System.Windows.Forms.ToolStripMenuItem()
        Me.ToolStripMenuItem1 = New System.Windows.Forms.ToolStripSeparator()
        Me.tsiRunFailed = New System.Windows.Forms.ToolStripMenuItem()
        Me.tsiClearFilter = New System.Windows.Forms.ToolStripButton()
        Me.txtSearch = New System.Windows.Forms.ToolStripTextBox()
        Me.ToolStripLabel1 = New System.Windows.Forms.ToolStripLabel()
        Me.tsiStop = New System.Windows.Forms.ToolStripButton()
        Me.tsiResults = New System.Windows.Forms.ToolStripLabel()
        Me.TreeBindingNavigator1 = New BrightIdeasSoftware2012.Extras.TreeBindingNavigator()
        Me.BindingNavigatorCountItem = New System.Windows.Forms.ToolStripLabel()
        Me.BindingNavigatorMoveFirstItem = New System.Windows.Forms.ToolStripButton()
        Me.BindingNavigatorMovePreviousItem = New System.Windows.Forms.ToolStripButton()
        Me.BindingNavigatorSeparator = New System.Windows.Forms.ToolStripSeparator()
        Me.BindingNavigatorPositionItem = New System.Windows.Forms.ToolStripTextBox()
        Me.BindingNavigatorSeparator1 = New System.Windows.Forms.ToolStripSeparator()
        Me.BindingNavigatorMoveNextItem = New System.Windows.Forms.ToolStripButton()
        Me.BindingNavigatorMoveLastItem = New System.Windows.Forms.ToolStripButton()
        Me.tsiWorkItem = New System.Windows.Forms.ToolStripLabel()
        Me.tsiSave = New System.Windows.Forms.ToolStripButton()
        CType(Me.tvTests, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.ToolStrip1.SuspendLayout()
        CType(Me.TreeBindingNavigator1, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.TreeBindingNavigator1.SuspendLayout()
        Me.SuspendLayout()
        '
        'NotifyIcon
        '
        Me.NotifyIcon.Icon = CType(resources.GetObject("NotifyIcon.Icon"), System.Drawing.Icon)
        Me.NotifyIcon.Text = "Unit Tests"
        '
        'tvTests
        '
        Me.tvTests.CellEditUseWholeCell = False
        Me.tvTests.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvTests.HighlightBackgroundColor = System.Drawing.Color.Empty
        Me.tvTests.HighlightForegroundColor = System.Drawing.Color.Empty
        Me.tvTests.Location = New System.Drawing.Point(0, 25)
        Me.tvTests.Name = "tvTests"
        Me.tvTests.ShowGroups = False
        Me.tvTests.Size = New System.Drawing.Size(400, 250)
        Me.tvTests.TabIndex = 0
        Me.tvTests.UseCompatibleStateImageBehavior = False
        Me.tvTests.View = System.Windows.Forms.View.Details
        Me.tvTests.VirtualMode = True
        '
        'ToolStrip1
        '
        Me.ToolStrip1.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden
        Me.ToolStrip1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiRun, Me.tsiClearFilter, Me.txtSearch, Me.ToolStripLabel1, Me.tsiStop, Me.tsiResults})
        Me.ToolStrip1.Location = New System.Drawing.Point(0, 0)
        Me.ToolStrip1.Name = "ToolStrip1"
        Me.ToolStrip1.Size = New System.Drawing.Size(400, 25)
        Me.ToolStrip1.TabIndex = 0
        Me.ToolStrip1.Text = "ToolStrip1"
        '
        'tsiRun
        '
        Me.tsiRun.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiRun.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.tsiRunSelected, Me.tsiRunAll, Me.ToolStripMenuItem1, Me.tsiRunFailed})
        Me.tsiRun.Image = CType(resources.GetObject("tsiRun.Image"), System.Drawing.Image)
        Me.tsiRun.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiRun.Name = "tsiRun"
        Me.tsiRun.Size = New System.Drawing.Size(32, 22)
        Me.tsiRun.Text = "Run"
        Me.tsiRun.ToolTipText = "Run Selected"
        '
        'tsiRunSelected
        '
        Me.tsiRunSelected.Image = CType(resources.GetObject("tsiRunSelected.Image"), System.Drawing.Image)
        Me.tsiRunSelected.Name = "tsiRunSelected"
        Me.tsiRunSelected.Size = New System.Drawing.Size(118, 22)
        Me.tsiRunSelected.Text = "Selected"
        '
        'tsiRunAll
        '
        Me.tsiRunAll.Image = CType(resources.GetObject("tsiRunAll.Image"), System.Drawing.Image)
        Me.tsiRunAll.Name = "tsiRunAll"
        Me.tsiRunAll.Size = New System.Drawing.Size(118, 22)
        Me.tsiRunAll.Text = "All"
        Me.tsiRunAll.Visible = False
        '
        'ToolStripMenuItem1
        '
        Me.ToolStripMenuItem1.Name = "ToolStripMenuItem1"
        Me.ToolStripMenuItem1.Size = New System.Drawing.Size(115, 6)
        Me.ToolStripMenuItem1.Visible = False
        '
        'tsiRunFailed
        '
        Me.tsiRunFailed.Image = CType(resources.GetObject("tsiRunFailed.Image"), System.Drawing.Image)
        Me.tsiRunFailed.Name = "tsiRunFailed"
        Me.tsiRunFailed.Size = New System.Drawing.Size(118, 22)
        Me.tsiRunFailed.Text = "Failed"
        Me.tsiRunFailed.Visible = False
        '
        'tsiClearFilter
        '
        Me.tsiClearFilter.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiClearFilter.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiClearFilter.Image = CType(resources.GetObject("tsiClearFilter.Image"), System.Drawing.Image)
        Me.tsiClearFilter.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiClearFilter.Name = "tsiClearFilter"
        Me.tsiClearFilter.Size = New System.Drawing.Size(23, 22)
        Me.tsiClearFilter.Text = "Clear Search"
        Me.tsiClearFilter.Visible = False
        '
        'txtSearch
        '
        Me.txtSearch.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.txtSearch.Name = "txtSearch"
        Me.txtSearch.Size = New System.Drawing.Size(100, 25)
        Me.txtSearch.Visible = False
        '
        'ToolStripLabel1
        '
        Me.ToolStripLabel1.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.ToolStripLabel1.Name = "ToolStripLabel1"
        Me.ToolStripLabel1.Size = New System.Drawing.Size(45, 22)
        Me.ToolStripLabel1.Text = "Search:"
        Me.ToolStripLabel1.Visible = False
        '
        'tsiStop
        '
        Me.tsiStop.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiStop.Image = CType(resources.GetObject("tsiStop.Image"), System.Drawing.Image)
        Me.tsiStop.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiStop.Name = "tsiStop"
        Me.tsiStop.Size = New System.Drawing.Size(23, 22)
        Me.tsiStop.Text = "Stop"
        Me.tsiStop.Visible = False
        '
        'tsiResults
        '
        Me.tsiResults.Name = "tsiResults"
        Me.tsiResults.Size = New System.Drawing.Size(96, 22)
        Me.tsiResults.Text = "1 / 2 tests passed"
        Me.tsiResults.Visible = False
        '
        'TreeBindingNavigator1
        '
        Me.TreeBindingNavigator1.AddNewItem = Nothing
        Me.TreeBindingNavigator1.CountItem = Me.BindingNavigatorCountItem
        Me.TreeBindingNavigator1.DeleteItem = Nothing
        Me.TreeBindingNavigator1.Dock = System.Windows.Forms.DockStyle.Bottom
        Me.TreeBindingNavigator1.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.BindingNavigatorMoveFirstItem, Me.BindingNavigatorMovePreviousItem, Me.BindingNavigatorSeparator, Me.BindingNavigatorPositionItem, Me.BindingNavigatorCountItem, Me.BindingNavigatorSeparator1, Me.BindingNavigatorMoveNextItem, Me.BindingNavigatorMoveLastItem, Me.tsiWorkItem, Me.tsiSave})
        Me.TreeBindingNavigator1.Location = New System.Drawing.Point(0, 275)
        Me.TreeBindingNavigator1.MoveFirstItem = Me.BindingNavigatorMoveFirstItem
        Me.TreeBindingNavigator1.MoveLastItem = Me.BindingNavigatorMoveLastItem
        Me.TreeBindingNavigator1.MoveNextItem = Me.BindingNavigatorMoveNextItem
        Me.TreeBindingNavigator1.MovePreviousItem = Me.BindingNavigatorMovePreviousItem
        Me.TreeBindingNavigator1.Name = "TreeBindingNavigator1"
        Me.TreeBindingNavigator1.PositionItem = Me.BindingNavigatorPositionItem
        Me.TreeBindingNavigator1.Size = New System.Drawing.Size(400, 25)
        Me.TreeBindingNavigator1.TabIndex = 1
        Me.TreeBindingNavigator1.Text = "TreeBindingNavigator1"
        Me.TreeBindingNavigator1.TreeView = Me.tvTests
        '
        'BindingNavigatorCountItem
        '
        Me.BindingNavigatorCountItem.Name = "BindingNavigatorCountItem"
        Me.BindingNavigatorCountItem.Size = New System.Drawing.Size(35, 22)
        Me.BindingNavigatorCountItem.Text = "of {0}"
        Me.BindingNavigatorCountItem.ToolTipText = "Total number of items"
        '
        'BindingNavigatorMoveFirstItem
        '
        Me.BindingNavigatorMoveFirstItem.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.BindingNavigatorMoveFirstItem.Image = CType(resources.GetObject("BindingNavigatorMoveFirstItem.Image"), System.Drawing.Image)
        Me.BindingNavigatorMoveFirstItem.Name = "BindingNavigatorMoveFirstItem"
        Me.BindingNavigatorMoveFirstItem.RightToLeftAutoMirrorImage = True
        Me.BindingNavigatorMoveFirstItem.Size = New System.Drawing.Size(23, 22)
        Me.BindingNavigatorMoveFirstItem.Text = "Move first"
        '
        'BindingNavigatorMovePreviousItem
        '
        Me.BindingNavigatorMovePreviousItem.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.BindingNavigatorMovePreviousItem.Image = CType(resources.GetObject("BindingNavigatorMovePreviousItem.Image"), System.Drawing.Image)
        Me.BindingNavigatorMovePreviousItem.Name = "BindingNavigatorMovePreviousItem"
        Me.BindingNavigatorMovePreviousItem.RightToLeftAutoMirrorImage = True
        Me.BindingNavigatorMovePreviousItem.Size = New System.Drawing.Size(23, 22)
        Me.BindingNavigatorMovePreviousItem.Text = "Move previous"
        '
        'BindingNavigatorSeparator
        '
        Me.BindingNavigatorSeparator.Name = "BindingNavigatorSeparator"
        Me.BindingNavigatorSeparator.Size = New System.Drawing.Size(6, 25)
        '
        'BindingNavigatorPositionItem
        '
        Me.BindingNavigatorPositionItem.AccessibleName = "Position"
        Me.BindingNavigatorPositionItem.AutoSize = False
        Me.BindingNavigatorPositionItem.Name = "BindingNavigatorPositionItem"
        Me.BindingNavigatorPositionItem.Size = New System.Drawing.Size(50, 23)
        Me.BindingNavigatorPositionItem.Text = "0"
        Me.BindingNavigatorPositionItem.ToolTipText = "Current position"
        '
        'BindingNavigatorSeparator1
        '
        Me.BindingNavigatorSeparator1.Name = "BindingNavigatorSeparator1"
        Me.BindingNavigatorSeparator1.Size = New System.Drawing.Size(6, 25)
        '
        'BindingNavigatorMoveNextItem
        '
        Me.BindingNavigatorMoveNextItem.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.BindingNavigatorMoveNextItem.Image = CType(resources.GetObject("BindingNavigatorMoveNextItem.Image"), System.Drawing.Image)
        Me.BindingNavigatorMoveNextItem.Name = "BindingNavigatorMoveNextItem"
        Me.BindingNavigatorMoveNextItem.RightToLeftAutoMirrorImage = True
        Me.BindingNavigatorMoveNextItem.Size = New System.Drawing.Size(23, 22)
        Me.BindingNavigatorMoveNextItem.Text = "Move next"
        '
        'BindingNavigatorMoveLastItem
        '
        Me.BindingNavigatorMoveLastItem.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.BindingNavigatorMoveLastItem.Image = CType(resources.GetObject("BindingNavigatorMoveLastItem.Image"), System.Drawing.Image)
        Me.BindingNavigatorMoveLastItem.Name = "BindingNavigatorMoveLastItem"
        Me.BindingNavigatorMoveLastItem.RightToLeftAutoMirrorImage = True
        Me.BindingNavigatorMoveLastItem.Size = New System.Drawing.Size(23, 22)
        Me.BindingNavigatorMoveLastItem.Text = "Move last"
        '
        'tsiWorkItem
        '
        Me.tsiWorkItem.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiWorkItem.Name = "tsiWorkItem"
        Me.tsiWorkItem.Size = New System.Drawing.Size(114, 22)
        Me.tsiWorkItem.Text = "No work item found"
        '
        'tsiSave
        '
        Me.tsiSave.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right
        Me.tsiSave.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image
        Me.tsiSave.Image = CType(resources.GetObject("tsiSave.Image"), System.Drawing.Image)
        Me.tsiSave.ImageTransparentColor = System.Drawing.Color.Magenta
        Me.tsiSave.Name = "tsiSave"
        Me.tsiSave.Size = New System.Drawing.Size(23, 22)
        Me.tsiSave.Text = "&Save"
        Me.tsiSave.Visible = False
        '
        'UnitTestTabControl
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.Controls.Add(Me.tvTests)
        Me.Controls.Add(Me.TreeBindingNavigator1)
        Me.Controls.Add(Me.ToolStrip1)
        Me.Name = "UnitTestTabControl"
        Me.Size = New System.Drawing.Size(400, 300)
        CType(Me.tvTests, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ToolStrip1.ResumeLayout(False)
        Me.ToolStrip1.PerformLayout()
        CType(Me.TreeBindingNavigator1, System.ComponentModel.ISupportInitialize).EndInit()
        Me.TreeBindingNavigator1.ResumeLayout(False)
        Me.TreeBindingNavigator1.PerformLayout()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents NotifyIcon As NotifyIcon
    Friend WithEvents tvTests As Global.BrightIdeasSoftware2012.TreeListView
    Friend WithEvents ToolStrip1 As ToolStrip
    Friend WithEvents tsiRun As ToolStripSplitButton
    Friend WithEvents tsiRunSelected As ToolStripMenuItem
    Friend WithEvents tsiRunAll As ToolStripMenuItem
    Friend WithEvents TreeBindingNavigator1 As BrightIdeasSoftware2012.Extras.TreeBindingNavigator
    Friend WithEvents BindingNavigatorCountItem As ToolStripLabel
    Friend WithEvents BindingNavigatorMoveFirstItem As ToolStripButton
    Friend WithEvents BindingNavigatorMovePreviousItem As ToolStripButton
    Friend WithEvents BindingNavigatorSeparator As ToolStripSeparator
    Friend WithEvents BindingNavigatorPositionItem As ToolStripTextBox
    Friend WithEvents BindingNavigatorSeparator1 As ToolStripSeparator
    Friend WithEvents BindingNavigatorMoveNextItem As ToolStripButton
    Friend WithEvents BindingNavigatorMoveLastItem As ToolStripButton
    Friend WithEvents ToolStripMenuItem1 As ToolStripSeparator
    Friend WithEvents tsiRunFailed As ToolStripMenuItem
    Friend WithEvents txtSearch As ToolStripTextBox
    Friend WithEvents ToolStripLabel1 As ToolStripLabel
    Friend WithEvents tsiStop As ToolStripButton
    Friend WithEvents tsiWorkItem As ToolStripLabel
    Friend WithEvents tsiClearFilter As ToolStripButton
    Friend WithEvents tsiSave As ToolStripButton
    Friend WithEvents tsiResults As ToolStripLabel
End Class
