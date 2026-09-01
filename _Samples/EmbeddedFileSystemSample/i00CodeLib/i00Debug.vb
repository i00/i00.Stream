Public NotInheritable Class i00Debug
    Public NotInheritable Class Thread
        Private Sub New()

        End Sub

        'The ThreadStarted allows the debugger to capture when a thread is started and add error handling to the thread :)...
        Public Shared Event ThreadStarted()

        Friend Shared Sub OnThreadStarted()
            RaiseEvent ThreadStarted()
        End Sub

        Public Class ThreadOptions
            Public Property ApartmentState As Threading.ApartmentState = Threading.ApartmentState.STA
            Public Property AllowAbort As Boolean = True
            Public Property CloseWithApp As Boolean
        End Class

        Public Shared Function Create(FriendlyName As String, start As Threading.ParameterizedThreadStart, Optional ThreadOptions As ThreadOptions = Nothing, Optional DropInnerThreadAbortExceptions As Boolean = True) As System.Threading.Thread
            Static ThreadOptionsDefault As New ThreadOptions

            If ThreadOptions Is Nothing Then ThreadOptions = ThreadOptionsDefault
            Create = New System.Threading.Thread(Sub(Pram As Object)
                                                     OnThreadStarted()
                                                     Try
                                                         start.Invoke(Pram)

                                                         'Catch ex As System.Threading.ThreadAbortException '< this is automatically handled within a thread anyway ... so no need to capture it
                                                     Catch ex As Exception When DropInnerThreadAbortExceptions AndAlso ex.Recurse(Function(x) {x.InnerException}).OfType(Of System.Threading.ThreadAbortException).Any
                                                         'capture all errors when an inner exception is a ThreadAbortException
                                                     End Try
                                                 End Sub)
            If FriendlyName <> "" Then Create.Name = FriendlyName
            Create.SetApartmentState(ThreadOptions.ApartmentState)
            Create.IsBackground = ThreadOptions.CloseWithApp
            RegisterThread(Create,, ThreadOptions.AllowAbort)
            'Create.IsBackground = True
        End Function

        Public Shared Event ThreadRegistering(Thread As Threading.Thread, FriendlyName As String, AllowAbort As Boolean)

        Public Shared Sub RegisterThread(Thread As Threading.Thread, Optional FriendlyName As String = Nothing, Optional AllowAbort As Boolean = True)
            If Thread IsNot Nothing Then
                RaiseEvent ThreadRegistering(Thread, FriendlyName, AllowAbort)
            End If
        End Sub
    End Class
End Class