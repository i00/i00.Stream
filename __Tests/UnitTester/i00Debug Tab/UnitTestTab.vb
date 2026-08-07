Public Class UnitTestTab
    Inherits i00CodeLib.i00Debug.DebugTab

    Dim _Control As New UnitTestTabControl
    Public Overrides ReadOnly Property Control As Control
        Get
            Return _Control
        End Get
    End Property

    Public Overrides ReadOnly Property Icon As Image
        Get
            Return My.Resources.Test
        End Get
    End Property

    Public Overrides ReadOnly Property TabName As String = "Unit Tests"

    Public Overrides Sub TabActivated()

    End Sub

    Public Overrides Sub TabDeactivated()

    End Sub
End Class
