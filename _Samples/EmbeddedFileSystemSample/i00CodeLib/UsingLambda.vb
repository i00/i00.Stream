Public Class UsingLambda
    Inherits UsingLambda(Of Boolean)
    Public Shared Function Create(Of T)(Optional Begin As Func(Of T) = Nothing, Optional [End] As Action(Of T) = Nothing, Optional SwallowErrors As SwallowErrors = SwallowErrors.None) As UsingLambda(Of T)
        Return New UsingLambda(Of T)(Begin, [End], SwallowErrors)
    End Function
    Public Sub New(Optional Begin As Action = Nothing, Optional [End] As Action = Nothing, Optional SwallowErrors As SwallowErrors = SwallowErrors.None)
        MyBase.New(If(Begin Is Nothing, Nothing, Function()
                                                     Begin.Invoke()
                                                     Return True
                                                 End Function),
                   If([End] Is Nothing, Nothing, Sub(Data As Boolean)
                                                     [End].Invoke
                                                 End Sub))
    End Sub

    <Flags>
    Public Enum SwallowErrors
        None = 0
        Begin = 1 << 0
        [End] = 1 << 1
        All = Begin + [End]
    End Enum
End Class


Public Class UsingLambda(Of T)
    Implements IDisposable

    Dim EndAction As Action(Of T)

    Dim mc_SwallowErrors As UsingLambda.SwallowErrors

    Public ReadOnly Property TransitionalObject As T
    Public Sub New(Optional Begin As Func(Of T) = Nothing, Optional [End] As Action(Of T) = Nothing, Optional SwallowErrors As UsingLambda.SwallowErrors = UsingLambda.SwallowErrors.None)
        If SwallowErrors.HasFlag(UsingLambda.SwallowErrors.Begin) Then
            Try
                If Begin IsNot Nothing Then TransitionalObject = Begin.Invoke
            Catch
            End Try
        Else
            If Begin IsNot Nothing Then TransitionalObject = Begin.Invoke
        End If
        Me.EndAction = [End]
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If mc_SwallowErrors.HasFlag(UsingLambda.SwallowErrors.End) Then
            Try
                If EndAction IsNot Nothing Then EndAction.Invoke(TransitionalObject)
            Catch
            End Try
        Else
            If EndAction IsNot Nothing Then EndAction.Invoke(TransitionalObject)
        End If
    End Sub

End Class