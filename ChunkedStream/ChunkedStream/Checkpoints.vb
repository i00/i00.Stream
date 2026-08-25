' ================================================================================
' ChunkedStream Checkpoints
' ================================================================================
'
' Purpose
'   - Implements data-only checkpoint support.
'
' Design
'   - Checkpoints capture logical length, index state, physical length and
'     header flags.
'   - Nested checkpoints are supported using a stack.
'   - Commit advances the checkpoint baseline.
'   - Rollback restores the checkpoint baseline.
'   - Dispose restores the baseline and closes the checkpoint.
'
' Recovery
'   - The outermost checkpoint owns recovery state.
'   - Recovery information is written to the header recovery area.
'
' Notes
'   - Checkpoints do not protect option changes.
'   - Checkpoints do not protect encryption configuration changes.
'
' ================================================================================

Namespace Streams

    Partial Class ChunkedStream

        Private ReadOnly _CheckpointStack As New List(Of ChunkedStreamCheckpoint)

        ''' <summary>
        ''' True when one or more data-only checkpoints are active.
        ''' </summary>
        Public ReadOnly Property HasActiveCheckpoint As Boolean
            Get
                Using EnterStateLock()
                    Return HasActiveCheckpointCore()
                End Using
            End Get
        End Property

        Private Function HasActiveCheckpointCore() As Boolean
            ThrowIfDisposed()
            Return HasOpenCheckpoint
        End Function

        ''' <summary>
        ''' Number of currently active nested checkpoints.
        ''' </summary>
        Public ReadOnly Property CheckpointDepth As Integer
            Get
                Using EnterStateLock()
                    Return GetCheckpointDepthCore()
                End Using
            End Get
        End Property

        Private Function GetCheckpointDepthCore() As Integer
            ThrowIfDisposed()
            Return _CheckpointStack.Count
        End Function

        Private ReadOnly Property HasOpenCheckpoint As Boolean
            Get
                Return _CheckpointStack.Count > 0
            End Get
        End Property

        ''' <summary>
        ''' A reusable data-only checkpoint for ChunkedStream.
        ''' </summary>
        ''' <remarks>
        ''' Checkpoints may be nested, but must be disposed, committed or rolled back in LIFO order.
        '''
        ''' Commit updates this checkpoint's baseline to the current stream data state and keeps the checkpoint active.
        ''' Rollback restores the stream data to this checkpoint's current baseline and keeps the checkpoint active.
        ''' Dispose restores the stream data to this checkpoint's current baseline and closes the checkpoint.
        '''
        ''' Inner checkpoint commits are still part of their parent checkpoint and will be rolled back if the parent checkpoint is rolled back or disposed.
        ''' Checkpoints protect stream data only. Options, encryption information and key wrapping changes are not rolled back.
        ''' </remarks>
        Public NotInheritable Class ChunkedStreamCheckpoint
            Implements IDisposable

            Private ReadOnly _Owner As ChunkedStream
            Private _IsDisposed As Boolean
            Private _CommitCount As Integer
            Private _RollbackCount As Integer

            Friend ReadOnly Property State As CheckpointState

            Friend Sub New(Owner As ChunkedStream, Depth As Integer)

                If Owner Is Nothing Then Throw New ArgumentNullException(NameOf(Owner))

                _Owner = Owner
                Me.Depth = Depth

                State = New CheckpointState()
                State.Capture(Owner)

            End Sub

            ''' <summary>
            ''' Gets the checkpoint nesting depth.
            ''' </summary>
            Public ReadOnly Property Depth As Integer

            ''' <summary>
            ''' True while the checkpoint has not been disposed.
            ''' </summary>
            Public ReadOnly Property IsActive As Boolean
                Get
                    Return Not _IsDisposed
                End Get
            End Property

            ''' <summary>
            ''' True when this checkpoint has been disposed and removed from the active checkpoint stack.
            ''' </summary>
            Public ReadOnly Property IsDisposed As Boolean
                Get
                    Return _IsDisposed
                End Get
            End Property

            ''' <summary>
            ''' Number of times Commit has updated this checkpoint's baseline.
            ''' </summary>
            Public ReadOnly Property CommitCount As Integer
                Get
                    Return _CommitCount
                End Get
            End Property

            ''' <summary>
            ''' Number of times Rollback has restored this checkpoint's baseline.
            ''' </summary>
            Public ReadOnly Property RollbackCount As Integer
                Get
                    Return _RollbackCount
                End Get
            End Property

            ''' <summary>
            ''' Updates this checkpoint's baseline to the current stream data state and keeps the checkpoint active.
            ''' </summary>
            ''' <param name="Durable">
            ''' If True, and this is the outermost checkpoint, the new checkpoint baseline is flushed to durable storage when the backing stream supports it.
            ''' Nested checkpoint commits are in-memory only until the outermost checkpoint commits.
            ''' </param>
            Public Sub Commit(Optional Durable As Boolean = True)

                If Not IsActive Then
                    Throw New InvalidOperationException("Checkpoint is no longer active.")
                End If

                _Owner.CommitCheckpoint(Me, Durable)

            End Sub

            ''' <summary>
            ''' Restores the stream data to this checkpoint's current baseline and keeps the checkpoint active.
            ''' </summary>
            ''' <remarks>
            ''' Rollback does not close the checkpoint. Call Dispose, usually through a Using block, to close the checkpoint.
            ''' </remarks>
            Public Sub Rollback()

                If Not IsActive Then
                    Throw New InvalidOperationException("Checkpoint is no longer active.")
                End If

                _Owner.RollbackCheckpoint(Me)

            End Sub

            Friend Sub MarkCommitted()

                _CommitCount += 1

            End Sub

            Friend Sub MarkRolledBack()

                _RollbackCount += 1

            End Sub

            Friend Sub MarkDisposed()

                _IsDisposed = True

            End Sub

            ''' <summary>
            ''' Restores the stream data to this checkpoint's current baseline and closes the checkpoint.
            ''' </summary>
            Public Sub Dispose() Implements IDisposable.Dispose

                If _IsDisposed Then Return

                _Owner.CloseCheckpoint(Me)

            End Sub

        End Class

        Friend NotInheritable Class CheckpointState

            Public Property LogicalLength As Long
            Public Property PhysicalLength As Long
            Public Property IndexOffset As Long
            Public Property HeaderFlags As HeaderFlags
            Public Property Extents As List(Of ExtentIndexEntry)
            Public Property PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
            Public Property NextPhysicalRecordId As Long
            Public Property NextAnchorId As Long

            Public Sub Capture(Owner As ChunkedStream)

                If Owner Is Nothing Then Throw New ArgumentNullException(NameOf(Owner))

                LogicalLength = Owner._Length
                PhysicalLength = Owner._Fs.Length
                IndexOffset = Owner._IndexOffset
                HeaderFlags = Owner._HeaderFlags
                Extents = New List(Of ExtentIndexEntry)(Owner._Extents)
                PhysicalRecords = Owner._PhysicalRecords.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
                NextPhysicalRecordId = Owner._NextPhysicalRecordId
                NextAnchorId = Owner._NextAnchorId

            End Sub

        End Class

        ''' <summary>
        ''' Creates a reusable data-only checkpoint.
        ''' </summary>
        ''' <remarks>
        ''' Writes and length changes made while the checkpoint is active are visible to reads immediately.
        '''
        ''' Commit updates the checkpoint baseline to the current stream data state and keeps the checkpoint active.
        ''' Rollback restores the stream data to the current checkpoint baseline and keeps the checkpoint active.
        ''' Dispose restores the stream data to the current checkpoint baseline and closes the checkpoint.
        '''
        ''' Checkpoints may be nested, but must be committed, rolled back or disposed in LIFO order.
        ''' A committed nested checkpoint is still part of its parent checkpoint and will be rolled back if the parent checkpoint is rolled back or disposed.
        '''
        ''' Checkpoints protect stream data only. Options, encryption information and file master key wrapping changes are not rolled back.
        '''
        ''' Defragmentation is not allowed while a checkpoint is active.
        ''' </remarks>
        Public Function CreateCheckpoint() As ChunkedStreamCheckpoint

            Using EnterStateLock()
                Return CreateCheckpointCore()
            End Using

        End Function

        Private Function CreateCheckpointCore() As ChunkedStreamCheckpoint


            ThrowIfDisposed()

            Dim Checkpoint = New ChunkedStreamCheckpoint(Me, _CheckpointStack.Count + 1)

            _CheckpointStack.Add(Checkpoint)

            ' Only the outermost checkpoint owns the recovery state.
            If _CheckpointStack.Count = 1 Then
                WriteCheckpointRecoveryState()
            End If

            Return Checkpoint


        End Function

        Private Sub CommitCheckpoint(Checkpoint As ChunkedStreamCheckpoint,
                                     Durable As Boolean)

            Using EnterStateLock()
                CommitCheckpointCore(Checkpoint, Durable)
            End Using

        End Sub

        Private Sub CommitCheckpointCore(Checkpoint As ChunkedStreamCheckpoint,
                                     Durable As Boolean)


            ThrowIfDisposed()
            EnsureTopCheckpoint(Checkpoint)

            If _CheckpointStack.Count > 1 Then
                Checkpoint.State.Capture(Me)
                Checkpoint.MarkCommitted()
                Return
            End If

            Dim CommitIndexOffset = Math.Max(_Fs.Length, GetDataEndFromIndex())

            PersistIndexAndHeader(CommitIndexOffset, Durable)

            Checkpoint.State.Capture(Me)

            ReclaimPendingPhysicalRecords()
            WriteCheckpointRecoveryState()

            Checkpoint.MarkCommitted()


        End Sub

        Private Sub RollbackCheckpoint(Checkpoint As ChunkedStreamCheckpoint)

            Using EnterStateLock()
                RollbackCheckpointCore(Checkpoint)
            End Using

        End Sub

        Private Sub RollbackCheckpointCore(Checkpoint As ChunkedStreamCheckpoint)


            ThrowIfDisposed()
            EnsureTopCheckpoint(Checkpoint)

            RestoreCheckpointState(Checkpoint.State)

            If _CheckpointStack.Count = 1 Then
                WriteCheckpointRecoveryState()
            End If

            Checkpoint.MarkRolledBack()


        End Sub

        Private Sub CloseCheckpoint(Checkpoint As ChunkedStreamCheckpoint)

            Using EnterStateLock()
                CloseCheckpointCore(Checkpoint)
            End Using

        End Sub

        Private Sub CloseCheckpointCore(Checkpoint As ChunkedStreamCheckpoint)


            If _Disposed Then
                Return
            End If

            ThrowIfDisposed()
            EnsureTopCheckpoint(Checkpoint)

            RestoreCheckpointState(Checkpoint.State)

            _CheckpointStack.RemoveAt(_CheckpointStack.Count - 1)
            Checkpoint.MarkDisposed()

            If _CheckpointStack.Count = 0 Then

                ClearRecoveryState()
                ReclaimPendingPhysicalRecords()

                If RemoveUnusedFileMasterKeyIfPossible() Then
                    PersistIndexAndHeader(_IndexOffset, True)
                End If

            End If


        End Sub

        Private Sub RestoreCheckpointState(State As CheckpointState)

            If State Is Nothing Then Throw New ArgumentNullException(NameOf(State))

            InvalidateChunkCache()
            ClearFreeSpaceMaps()
            DiscardPendingPhysicalRecordReclaims()

            _Length = State.LogicalLength
            _IndexOffset = State.IndexOffset
            _HeaderFlags = State.HeaderFlags
            _NextPhysicalRecordId = State.NextPhysicalRecordId
            _NextAnchorId = State.NextAnchorId

            _Extents.Clear()
            _Extents.AddRange(State.Extents)

            _PhysicalRecords.Clear()

            For Each pair In State.PhysicalRecords
                _PhysicalRecords(pair.Key) = pair.Value
            Next

            RebuildPhysicalRecordOrdinals()
            RebuildAnchorIndex()

            _ExtentPageDescriptors.Clear()
            _ExtentDirectoryPageDescriptors.Clear()
            _PhysicalRecordPageDescriptors.Clear()
            _PhysicalRecordDirectoryPageDescriptors.Clear()
            _HoleDirectoryPageDescriptors.Clear()

            MarkAllMetadataPagesDirty()

            If _Fs.Length > State.PhysicalLength Then
                _Fs.SetLength(State.PhysicalLength)
            End If

        End Sub

        Private Sub EnsureTopCheckpoint(Checkpoint As ChunkedStreamCheckpoint)

            If Checkpoint Is Nothing Then Throw New ArgumentNullException(NameOf(Checkpoint))

            If _CheckpointStack.Count = 0 OrElse Not Object.ReferenceEquals(_CheckpointStack(_CheckpointStack.Count - 1), Checkpoint) Then
                Throw New InvalidOperationException("Checkpoints must be committed, rolled back or disposed in LIFO order.")
            End If

        End Sub

    End Class
End Namespace
