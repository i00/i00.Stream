''' <summary>Lets <see cref="MemoizedEnumerable(Of T)"/>'s type argument be inferred from the source,
''' e.g. <c>MemoizedEnumerable.Create(source)</c> instead of <c>New MemoizedEnumerable(Of T)(source)</c>.</summary>
Public NotInheritable Class MemoizedEnumerable
    Private Sub New()
    End Sub

    Public Shared Function Create(Of T)(Source As IEnumerable(Of T)) As MemoizedEnumerable(Of T)
        Return New MemoizedEnumerable(Of T)(Source)
    End Function
End Class

''' <summary>
''' Wraps a single-pass <see cref="IEnumerable(Of T)"/> so it can be enumerated any number of times -
''' including concurrently, from different threads - without the source ever being walked more than
''' once. The first enumerator to reach a given position pulls the next item from the source and caches
''' it; every other enumerator, whether already there or arriving later, is served that cached item
''' instead of re-running the source.
'''
''' Built for cases like a disk walk feeding both a file copy and a background size scan: whichever one
''' is further ahead ends up doing the actual directory enumeration, and the other rides along for free.
''' </summary>
Public NotInheritable Class MemoizedEnumerable(Of T)
    Implements IEnumerable(Of T)

    Private ReadOnly _SyncRoot As New Object()
    Private ReadOnly _Source As IEnumerable(Of T)
    Private ReadOnly _Buffer As New List(Of T)()
    Private _SourceEnumerator As IEnumerator(Of T)
    Private _SourceExhausted As Boolean

    Public Sub New(Source As IEnumerable(Of T))
        If Source Is Nothing Then Throw New ArgumentNullException(NameOf(Source))
        _Source = Source
    End Sub

    Public Function GetEnumerator() As IEnumerator(Of T) Implements IEnumerable(Of T).GetEnumerator
        Return New Cursor(Me)
    End Function

    Private Function GetEnumeratorUntyped() As IEnumerator Implements IEnumerable.GetEnumerator
        Return GetEnumerator()
    End Function

    ''' <summary>Puts the item at Index into <paramref name="Item"/>, pulling one more item from the
    ''' source if Index has not been reached yet. Returns False once the source is exhausted before
    ''' reaching Index. Thread-safe - concurrent callers converge on the same cached items.</summary>
    Private Function TryFetch(Index As Integer, ByRef Item As T) As Boolean
        SyncLock _SyncRoot
            If Index < _Buffer.Count Then
                Item = _Buffer(Index)
                Return True
            End If
            If _SourceExhausted Then Return False

            If _SourceEnumerator Is Nothing Then _SourceEnumerator = _Source.GetEnumerator()
            If _SourceEnumerator.MoveNext() = False Then
                _SourceExhausted = True
                _SourceEnumerator.Dispose()
                Return False
            End If

            Item = _SourceEnumerator.Current
            _Buffer.Add(Item)
            Return True
        End SyncLock
    End Function

    Private NotInheritable Class Cursor
        Implements IEnumerator(Of T)

        Private ReadOnly _Owner As MemoizedEnumerable(Of T)
        Private _Index As Integer = -1
        Private _Current As T

        Public Sub New(Owner As MemoizedEnumerable(Of T))
            _Owner = Owner
        End Sub

        Public ReadOnly Property Current As T Implements IEnumerator(Of T).Current
            Get
                Return _Current
            End Get
        End Property

        Private ReadOnly Property CurrentUntyped As Object Implements IEnumerator.Current
            Get
                Return Current
            End Get
        End Property

        Public Function MoveNext() As Boolean Implements IEnumerator.MoveNext
            Dim NextItem As T = Nothing
            If _Owner.TryFetch(_Index + 1, NextItem) = False Then Return False
            _Index += 1
            _Current = NextItem
            Return True
        End Function

        Public Sub Reset() Implements IEnumerator.Reset
            _Index = -1
            _Current = Nothing
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class
End Class
