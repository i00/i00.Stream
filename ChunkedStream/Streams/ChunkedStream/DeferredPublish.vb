' ================================================================================
' ChunkedStream Deferred Metadata Publishing
' ================================================================================
'
' Purpose
'   - Folds the metadata publish (PersistIndexAndHeader) that normally follows every
'     mutating operation into a single publish, without a checkpoint's rollback
'     snapshot or recovery journal.
'
' Design
'   - DeferPublish returns a disposable scope. Scopes are reference-counted, not
'     stacked, so unrelated callers may hold one at the same time and dispose them
'     in any order.
'   - Physical records are still written to the backing stream as operations run.
'     Only the index / physical-record table / header publish is held back.
'   - The publish happens when the last scope is disposed, on Flush, on Dispose and
'     on every checkpoint commit.
'
' Crash behaviour
'   - No snapshot and no recovery state are written. A crash before the publish
'     reopens the stream at the last published generation; records written in the
'     open window become orphaned physical records, reclaimed by Defragment or the
'     unreferenced-record sweep. Space freed inside the window is protected by the
'     deferred-free window - the header sequence does not advance while publishing
'     is suspended, so a freed span never matures back into the allocator before the
'     crash.
'
' Notes
'   - Defragment and ApplyOptions may not run while a scope is open.
'
' ================================================================================

Namespace Streams

    Partial Class ChunkedStream

        Private _DeferPublishDepth As Integer

        '
        ' True while the per-operation metadata publish is held back - by a checkpoint,
        ' a DeferPublish scope, or both. Only PersistIndexAndHeader is gated on this;
        ' reclamation, free-space and anchor bookkeeping keep using HasOpenCheckpoint,
        ' because they turn on whether a rollback can still occur - which DeferPublish
        ' never introduces.
        '
        Private ReadOnly Property MetadataPublishSuspended As Boolean
            Get
                Return HasOpenCheckpoint OrElse _DeferPublishDepth > 0
            End Get
        End Property

        ''' <summary>
        ''' Suspends the metadata publish that normally follows each mutating operation
        ''' until every scope returned by this method is disposed, then publishes once.
        ''' </summary>
        ''' <remarks>
        ''' Unlike a checkpoint this keeps no rollback snapshot and writes no recovery
        ''' state. Physical records are written to the backing stream as operations run,
        ''' but a crash before the publish reopens the stream at the last published
        ''' generation and leaves the window's records as orphans for
        ''' <see cref="Defragment" /> or a later edit to reclaim. Use it to fold a burst
        ''' of writes - a streamed file copy, a batch import - into a single publish.
        ''' Scopes are reference-counted rather than stacked, so independent callers may
        ''' hold one at the same time and dispose them in any order.
        '''
        ''' <see cref="Flush" /> publishes the pending metadata without ending the
        ''' suspension. <see cref="Defragment" /> and ApplyOptions cannot run while a
        ''' scope is open.
        ''' </remarks>
        Public Function DeferPublish() As IDisposable

            Using EnterStateLock()
                ThrowIfDisposed()
                ThrowIfFaulted()
                _DeferPublishDepth += 1
                Return New DeferPublishScope(Me)
            End Using

        End Function

        Private Sub EndDeferPublish()

            Using EnterStateLock()

                If _Disposed Then Return
                If _DeferPublishDepth = 0 Then Return

                _DeferPublishDepth -= 1

                If _DeferPublishDepth = 0 AndAlso
                   HasOpenCheckpoint = False AndAlso
                   _Faulted = False AndAlso
                   BaseStream.CanWrite Then

                    PersistIndexAndHeader(_IndexOffset, True)

                End If

            End Using

        End Sub

        Private NotInheritable Class DeferPublishScope
            Implements IDisposable

            Private ReadOnly _Owner As ChunkedStream
            Private _Disposed As Boolean

            Friend Sub New(Owner As ChunkedStream)
                _Owner = Owner
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose

                If _Disposed Then Return
                _Disposed = True
                _Owner.EndDeferPublish()

            End Sub

        End Class

    End Class

End Namespace
