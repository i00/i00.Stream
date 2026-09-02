' ================================================================================
' ChunkedStream Deferred Metadata Publishing
' ================================================================================
'
' Purpose
'   - Folds the metadata publish (PersistIndexAndHeader) that normally follows every
'     mutating operation into a single explicit publish.
'
' Design
'   - DeferPublish returns a scope. Scopes are reference-counted, not stacked, so
'     independent callers may hold one at the same time and dispose them in any
'     order. Only the outermost scope owns the publish and the rollback baseline.
'   - The outermost scope captures a CheckpointState snapshot when it opens.
'   - Scope.Publish() persists the accumulated metadata durably and re-baselines the
'     snapshot. It acts only at the outermost level; a nested Publish() is ignored,
'     so if any operation in the batch throws before the outermost Publish() the
'     whole batch is rolled back.
'   - Disposing the outermost scope WITHOUT a preceding Publish() rolls the metadata
'     tables back to the snapshot (RestoreCheckpointState), truncating any physical
'     records written in the window and leaving the stream usable - it is NOT
'     faulted. Disposing it after a clean Publish() does nothing.
'   - Physical records are written to the backing stream as operations run; only the
'     index / physical-record table / header publish is held back.
'
' Difference from a checkpoint
'   - No per-operation recovery-state header write.
'   - The published close path does no work: it does not run RestoreCheckpointState
'     and does not mark every metadata page dirty. This is what keeps a burst of
'     small operations from bloating and fragmenting the file.
'   - A crash during a checkpoint truncates back to the recovery length on reopen; a
'     crash during a DeferPublish window does not, so its records are left as orphans.
'
' Relationship to checkpoints
'   - A checkpoint may NOT be created while a DeferPublish scope is open
'     (CreateCheckpoint throws): a checkpoint commit publishes durably, which the
'     scope's rollback contract cannot honour.
'   - A DeferPublish scope MAY be opened inside a checkpoint. Publishing is already
'     suspended by the checkpoint, so the scope adds nothing: Publish() does not
'     persist and closing the scope neither publishes nor rolls back. The checkpoint
'     is the only transaction boundary; if it rolls back, the scope's work goes with
'     it. This lets a helper that batches with DeferPublish be called from inside a
'     caller's checkpoint without special-casing.
'
' Crash behaviour
'   - No recovery state is written. A crash before Publish() reopens the stream at
'     the last published generation; records written in the open window become
'     orphaned physical records, reclaimed by Defragment or the unreferenced-record
'     sweep. Space freed inside the window is protected by the deferred-free window -
'     the header sequence does not advance while publishing is suspended, so a freed
'     span never matures back into the allocator before the crash or a rollback.
'
' Notes
'   - Defragment and ApplyOptions may not run while a scope is open.
'
' ================================================================================

Namespace Streams

    Partial Class ChunkedStream

        Private _DeferPublishDepth As Integer
        Private _DeferPublishState As CheckpointState
        Private _DeferPublishRequested As Boolean

        '
        ' True while the per-operation metadata publish is held back - by a checkpoint,
        ' a DeferPublish scope, or both. Only PersistIndexAndHeader is gated on this;
        ' reclamation, free-space and anchor bookkeeping keep using HasOpenCheckpoint
        ' because the deferred-free window already prevents a freed span from being
        ' reused while publishing is suspended, which is what makes a rollback safe.
        '
        Private ReadOnly Property MetadataPublishSuspended As Boolean
            Get
                Return HasOpenCheckpoint OrElse _DeferPublishDepth > 0
            End Get
        End Property

        ''' <summary>
        ''' Suspends the metadata publish that normally follows each mutating operation.
        ''' Call <see cref="DeferPublishScope.Publish" /> on the returned scope to persist
        ''' the accumulated changes durably; disposing the scope without it rolls those
        ''' changes back and leaves the stream usable.
        ''' </summary>
        ''' <remarks>
        ''' The outermost scope keeps an in-memory rollback snapshot but, unlike a
        ''' checkpoint, writes no recovery-state header and does no work on a published
        ''' close. Physical records are written to the backing stream as operations run,
        ''' so a crash before <see cref="DeferPublishScope.Publish" /> reopens the stream
        ''' at the last published generation and leaves the window's records as orphans
        ''' for <see cref="Defragment" /> or a later edit to reclaim.
        '''
        ''' Scopes are reference-counted rather than stacked. Only the outermost scope
        ''' publishes or rolls back; a nested <see cref="DeferPublishScope.Publish" /> is
        ''' ignored, so if any operation in the batch throws before the outermost
        ''' Publish() the whole batch is rolled back.
        '''
        ''' <see cref="Flush" /> publishes the pending metadata and re-baselines the
        ''' rollback snapshot without ending the suspension. <see cref="Defragment" />
        ''' and ApplyOptions cannot run while a scope is open.
        ''' </remarks>
        Public Function DeferPublish() As DeferPublishScope

            Using EnterStateLock()

                ThrowIfDisposed()
                ThrowIfFaulted()

                If _DeferPublishDepth = 0 Then
                    _DeferPublishState = New CheckpointState()
                    _DeferPublishState.Capture(Me)
                    _DeferPublishRequested = False
                End If

                _DeferPublishDepth += 1

                Return New DeferPublishScope(Me)

            End Using

        End Function

        ''' <summary>
        ''' Asynchronously suspends the per-operation metadata publish. Opening the scope
        ''' only captures an in-memory snapshot, so this rarely blocks; it is provided for
        ''' symmetry with <see cref="DeferPublishScope.PublishAsync" /> and
        ''' <see cref="DeferPublishScope.CloseAsync" />.
        ''' </summary>
        Public Function DeferPublishAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task(Of DeferPublishScope)

            Return Task.Run(Function() DeferPublish(), CancellationToken)

        End Function

        Private Sub PublishDeferred()

            Using EnterStateLock()

                ThrowIfDisposed()
                ThrowIfFaulted()

                If _DeferPublishDepth = 0 Then
                    Throw New InvalidOperationException("There is no active DeferPublish scope to publish.")
                End If

                '
                ' Only the outermost scope owns the publish. A nested Publish() call is
                ' deliberately a no-op: the outermost scope decides whether the whole
                ' batch is persisted or rolled back.
                '
                If _DeferPublishDepth > 1 Then Return

                _DeferPublishRequested = True

                If HasOpenCheckpoint = False AndAlso _Faulted = False AndAlso BaseStream.CanWrite Then
                    PersistIndexAndHeader(_IndexOffset, True)
                    _DeferPublishState.Capture(Me)
                End If

            End Using

        End Sub

        Private Sub EndDeferPublish()

            Using EnterStateLock()

                If _DeferPublishDepth = 0 Then Return

                _DeferPublishDepth -= 1
                If _DeferPublishDepth > 0 Then Return

                Dim State = _DeferPublishState
                Dim Requested = _DeferPublishRequested
                _DeferPublishState = Nothing
                _DeferPublishRequested = False

                If State Is Nothing Then Return
                If _Disposed Then Return
                If BaseStream Is Nothing OrElse BaseStream.CanWrite = False Then Return
                If HasOpenCheckpoint Then Return

                '
                ' Nothing to do unless the window left the in-memory metadata ahead of
                ' the last durable publish. After a clean Publish() with no later edit
                ' this is the common path, and it must not touch the metadata pages.
                '
                Dim HasUnpublishedWork = _Faulted OrElse
                                         _DirtyExtentPages.Count > 0 OrElse
                                         _DirtyPhysicalRecordPages.Count > 0

                If HasUnpublishedWork = False Then Return

                If Requested AndAlso _Faulted = False Then
                    PersistIndexAndHeader(_IndexOffset, True)
                Else
                    RestoreCheckpointState(State)
                End If

            End Using

        End Sub

        ''' <summary>
        ''' A reference-counted scope that holds back the ChunkedStream metadata publish.
        ''' </summary>
        Public NotInheritable Class DeferPublishScope
            Implements IDisposable

            Private ReadOnly _Owner As ChunkedStream
            Private _Disposed As Boolean

            Friend Sub New(Owner As ChunkedStream)
                _Owner = Owner
            End Sub

            ''' <summary>
            ''' Persists the metadata accumulated since the outermost scope opened, durably.
            ''' Call this as the final step of a successful operation. Disposing the scope
            ''' without calling it rolls the accumulated metadata changes back.
            ''' </summary>
            Public Sub Publish()
                If _Disposed Then Throw New ObjectDisposedException(NameOf(DeferPublishScope))
                _Owner.PublishDeferred()
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If _Disposed Then Return
                _Disposed = True
                _Owner.EndDeferPublish()
            End Sub

            ''' <summary>
            ''' Asynchronously persists the accumulated metadata durably. The asynchronous
            ''' equivalent of <see cref="Publish" />.
            ''' </summary>
            Public Function PublishAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task
                If _Disposed Then Throw New ObjectDisposedException(NameOf(DeferPublishScope))
                Return Task.Run(Sub() _Owner.PublishDeferred(), CancellationToken)
            End Function

            ''' <summary>
            ''' Asynchronously closes the scope, publishing or rolling back the batch. The
            ''' asynchronous equivalent of <see cref="Dispose" />; call it explicitly since
            ''' .NET Framework 4.8 has no <c>Await Using</c>.
            ''' </summary>
            Public Function CloseAsync(Optional CancellationToken As Threading.CancellationToken = Nothing) As Task
                If _Disposed Then Return Task.CompletedTask
                _Disposed = True
                Return Task.Run(Sub() _Owner.EndDeferPublish(), CancellationToken)
            End Function

        End Class

    End Class

End Namespace
