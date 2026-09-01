Namespace Controls

    <ComponentModel.DesignerCategory("")>
    Friend Class ToolStripSpringHelper

        Friend Interface ToolStripSpringItem

        End Interface

        Public Shared Function GetPreferredSize(ByVal Control As ToolStripItem, ByVal constrainingSize As Size, ByVal DefaultSize As Size, ByVal BaseGetPreferredSize As Func(Of Size, Size)) As Size
            Dim Vert = Control.Owner.Orientation = Orientation.Vertical

            ' Use the default size if the text box is on the overflow menu
            If Control.IsOnOverflow Then
                Return DefaultSize
            End If

            ' Declare a variable to store the total available width as
            ' it is calculated, starting with the display width of the
            ' owning ToolStrip.
            Dim Length = If(Vert, Control.Owner.DisplayRectangle.Height - Control.Owner.Padding.Vertical, Control.Owner.DisplayRectangle.Width - Control.Owner.Padding.Horizontal)
            Dim OverflowFullLength = If(Vert, Control.Owner.OverflowButton.Height - Control.Owner.OverflowButton.Margin.Vertical(), Control.Owner.OverflowButton.Width - Control.Owner.OverflowButton.Margin.Horizontal())

            ' Subtract the width of the overflow button if it is displayed.
            If Control.Owner.OverflowButton.Visible Then
                Length -= OverflowFullLength
            End If

            ' Declare a variable to maintain a count of ToolStripSpringTextBox
            ' items currently displayed in the owning ToolStrip.
            Dim springBoxCount As Int32 = 0

            For Each item As ToolStripItem In Control.Owner.Items
                ' Ignore items on the overflow menu or invisible ones.
                If item.IsOnOverflow OrElse item.Visible = False Then
                    'Continue For
                Else
                    If TypeOf (item) Is ToolStripSpringItem Then
                        ' For ToolStripSpringTextBox items, increment the count and
                        ' subtract the margin width from the total available width.
                        springBoxCount += 1
                        Length -= If(Vert, item.Margin.Vertical, item.Margin.Horizontal)
                    Else
                        ' For all other items, subtract the full width from the total
                        ' available width.
                        Length -= If(Vert, item.Height + item.Margin.Vertical, item.Width + item.Margin.Horizontal)
                    End If
                End If
            Next

            ' If there are multiple ToolStripSpringTextBox items in the owning
            ' ToolStrip, divide the total available width between them.
            If springBoxCount > 1 Then Length = CInt(Length / springBoxCount)

            ' If the available width is less than the default width, use the
            ' default width, forcing one or more items onto the overflow menu.
            'DOESNT WORK FOR MULTIPLE ITEMS :(
            'If width < DefaultSize.Width Then width = DefaultSize.Width

            ' Retrieve the preferred size from the base class, but change the
            ' width to the calculated width.
            Dim preferredSize As Size = BaseGetPreferredSize(constrainingSize)
            If Vert Then
                preferredSize.Height = Length
            Else
                preferredSize.Width = Length
            End If
            Return preferredSize
        End Function
    End Class

End Namespace