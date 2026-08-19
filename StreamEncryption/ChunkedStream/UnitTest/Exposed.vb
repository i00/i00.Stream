#If DEBUG Then ' < This exposes some data for unit tests

Namespace Streams

    Partial Class ChunkedStream

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Shared ReadOnly Property Debug_HeaderSize() As Integer
            Get
                Return HeaderSize
            End Get
        End Property

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Shared ReadOnly Property Debug_HeaderSequenceOffset() As Integer
            Get
                Return HeaderSequenceOffset
            End Get
        End Property

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Shared ReadOnly Property Debug_ChunkCompressionEvaluatedPercentOffset() As Integer
            Get
                Return ChunkCompressionEvaluatedPercentOffset
            End Get
        End Property

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Shared ReadOnly Property Debug_ChunkFlagsOffset() As Integer
            Get
                Return ChunkFlagsOffset
            End Get
        End Property

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Sub Debug_WritePhysicalRecordMoveRecoveryState(State As RecoveryStates,
                                                              RecordId As Long,
                                                              OldOffset As Long,
                                                              OldLength As Integer,
                                                              NewOffset As Long,
                                                              NewLength As Integer)
            WritePhysicalRecordMoveRecoveryState(State, RecordId, OldOffset, OldLength, NewOffset, NewLength)
        End Sub

        Public Sub Debug_WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength As Long)
            WriteChunkSizeRebuildRecoveryState(OriginalPhysicalLength)
        End Sub

    End Class

End Namespace

#End If