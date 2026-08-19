#If DEBUG Then ' < This exposes some data for unit tests

Namespace Streams

    Partial Class ChunkedStream

        <System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Public Sub Debug_CorruptPhysicalRecordMetadataRefCount(RecordId As Long,
                                                               NewRefCount As Integer)

            Dim Record = _PhysicalRecords(RecordId)

            Record.RefCount = NewRefCount

            _PhysicalRecords(RecordId) = Record

        End Sub

    End Class

End Namespace

#End If