#If DEBUG Then ' < This is only for unit tests

Namespace Streams

    Partial Class ChunkedStream

        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptPhysicalRecordMetadataRefCount(RecordId As Long,
                                                               NewRefCount As Integer)

            Dim Record = _PhysicalRecords(RecordId)

            Record.RefCount = NewRefCount

            _PhysicalRecords(RecordId) = Record

        End Sub

    End Class

End Namespace

#End If