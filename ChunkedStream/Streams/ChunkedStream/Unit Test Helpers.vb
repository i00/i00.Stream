Namespace Streams

    Partial Class ChunkedStream

        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptPhysicalRecordMetadataRefCount(RecordId As Long,
                                                               NewRefCount As Integer)

            Dim Record = _PhysicalRecords(RecordId)

            Record.RefCount = NewRefCount

            _PhysicalRecords(RecordId) = Record

        End Sub

        ''' <summary>
        ''' Total number of physical-record entries currently tracked, including any that
        ''' no live extent references.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Function Debug_GetPhysicalRecordCount() As Integer

            Return _PhysicalRecords.Count

        End Function

        ''' <summary>
        ''' Number of tracked physical-record entries that no live extent references.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Function Debug_GetUnreferencedPhysicalRecordCount() As Integer

            Dim Referenced As New HashSet(Of Long)()

            For Each Extent In _Extents
                If Extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                    Referenced.Add(Extent.PhysicalRecordId)
                End If
            Next

            Dim Result = 0

            For Each RecordId In _PhysicalRecords.Keys
                If Referenced.Contains(RecordId) = False Then Result += 1
            Next

            Return Result

        End Function

    End Class

End Namespace