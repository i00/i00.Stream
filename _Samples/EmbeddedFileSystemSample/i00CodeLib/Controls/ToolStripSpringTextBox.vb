Namespace Controls

    <ComponentModel.DesignerCategory("")>
    Public Class ToolStripSpringTextBox
        Inherits ToolStripTextBox
        Implements ToolStripSpringHelper.ToolStripSpringItem

        <ComponentModel.DefaultValue(True)>
        Public Property AutoSpring As Boolean = True

        Public Overrides Function GetPreferredSize(ByVal constrainingSize As Size) As Size
            If AutoSpring AndAlso Me.DesignMode = False Then
                Return ToolStripSpringHelper.GetPreferredSize(Me, constrainingSize, DefaultSize, AddressOf MyBase.GetPreferredSize)
            Else
                Return MyBase.GetPreferredSize(constrainingSize)
            End If
        End Function

    End Class

End Namespace