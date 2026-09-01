<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class MessageBox
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
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
        Me.FlexibleMessageBoxFormBindingSource = New System.Windows.Forms.BindingSource(Me.components)
        Me.pnlButtons = New System.Windows.Forms.Panel()
        Me.pnlContentHolder = New System.Windows.Forms.Panel()
        Me.rtbMessage = New System.Windows.Forms.RichTextBox()
        Me.pnlImageSpacer = New System.Windows.Forms.Panel()
        Me.picIcon = New System.Windows.Forms.PictureBox()
        CType(Me.FlexibleMessageBoxFormBindingSource, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.pnlContentHolder.SuspendLayout()
        CType(Me.picIcon, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'pnlButtons
        '
        Me.pnlButtons.Dock = System.Windows.Forms.DockStyle.Bottom
        Me.pnlButtons.Location = New System.Drawing.Point(0, 84)
        Me.pnlButtons.Name = "pnlButtons"
        Me.pnlButtons.Padding = New System.Windows.Forms.Padding(12)
        Me.pnlButtons.Size = New System.Drawing.Size(284, 48)
        Me.pnlButtons.TabIndex = 0
        '
        'pnlContentHolder
        '
        Me.pnlContentHolder.BackColor = System.Drawing.SystemColors.Window
        Me.pnlContentHolder.Controls.Add(Me.rtbMessage)
        Me.pnlContentHolder.Controls.Add(Me.pnlImageSpacer)
        Me.pnlContentHolder.Controls.Add(Me.picIcon)
        Me.pnlContentHolder.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlContentHolder.Location = New System.Drawing.Point(0, 0)
        Me.pnlContentHolder.Name = "pnlContentHolder"
        Me.pnlContentHolder.Padding = New System.Windows.Forms.Padding(24)
        Me.pnlContentHolder.Size = New System.Drawing.Size(284, 84)
        Me.pnlContentHolder.TabIndex = 9
        '
        'rtbMessage
        '
        Me.rtbMessage.BackColor = System.Drawing.SystemColors.Window
        Me.rtbMessage.BorderStyle = System.Windows.Forms.BorderStyle.None
        Me.rtbMessage.Dock = System.Windows.Forms.DockStyle.Fill
        Me.rtbMessage.Font = New System.Drawing.Font("Microsoft Sans Serif", 9.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.rtbMessage.Location = New System.Drawing.Point(64, 24)
        Me.rtbMessage.Name = "rtbMessage"
        Me.rtbMessage.ReadOnly = True
        Me.rtbMessage.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical
        Me.rtbMessage.Size = New System.Drawing.Size(196, 36)
        Me.rtbMessage.TabIndex = 0
        Me.rtbMessage.TabStop = False
        Me.rtbMessage.Tag = "x"
        Me.rtbMessage.Text = "Message"
        '
        'pnlImageSpacer
        '
        Me.pnlImageSpacer.Dock = System.Windows.Forms.DockStyle.Left
        Me.pnlImageSpacer.Location = New System.Drawing.Point(56, 24)
        Me.pnlImageSpacer.Name = "pnlImageSpacer"
        Me.pnlImageSpacer.Size = New System.Drawing.Size(8, 36)
        Me.pnlImageSpacer.TabIndex = 9
        '
        'picIcon
        '
        Me.picIcon.BackColor = System.Drawing.Color.Transparent
        Me.picIcon.Dock = System.Windows.Forms.DockStyle.Left
        Me.picIcon.Location = New System.Drawing.Point(24, 24)
        Me.picIcon.MaximumSize = New System.Drawing.Size(32, 32)
        Me.picIcon.MinimumSize = New System.Drawing.Size(32, 32)
        Me.picIcon.Name = "picIcon"
        Me.picIcon.Size = New System.Drawing.Size(32, 32)
        Me.picIcon.TabIndex = 8
        Me.picIcon.TabStop = False
        '
        'MessageBox
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(284, 132)
        Me.Controls.Add(Me.pnlContentHolder)
        Me.Controls.Add(Me.pnlButtons)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle
        Me.KeyPreview = True
        Me.MaximizeBox = False
        Me.MinimizeBox = False
        Me.Name = "MessageBox"
        Me.ShowIcon = False
        Me.ShowInTaskbar = False
        Me.Text = "MessageBox"
        CType(Me.FlexibleMessageBoxFormBindingSource, System.ComponentModel.ISupportInitialize).EndInit()
        Me.pnlContentHolder.ResumeLayout(False)
        CType(Me.picIcon, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)

    End Sub
    Friend WithEvents pnlButtons As System.Windows.Forms.Panel
    Friend WithEvents FlexibleMessageBoxFormBindingSource As System.Windows.Forms.BindingSource
    Friend WithEvents pnlContentHolder As System.Windows.Forms.Panel
    Friend WithEvents picIcon As System.Windows.Forms.PictureBox
    Friend WithEvents rtbMessage As System.Windows.Forms.RichTextBox
    Friend WithEvents pnlImageSpacer As System.Windows.Forms.Panel
End Class
