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
        ''' Overwrites the cached physical-data end, to reproduce the incrementally
        ''' maintained value drifting above the real end of the live data (observed after
        ''' heavy churn - it only ratchets down when the record at the very end is the one
        ''' that moves). Operations that trim the backing store must recompute it rather
        ''' than trust this cache.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptCachedPhysicalDataEnd(Value As Long)

            _PhysicalDataEnd = Value

        End Sub

        ''' <summary>
        ''' Patches every on-disk header copy's index-offset field to <paramref name="NewValue" />
        ''' and refreshes its MAC, leaving a structurally valid header that carries a stale
        ''' allocation hint - the state a process that faulted mid-operation can persist.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptPersistedHeaderIndexOffset(NewValue As Long)

            For CopyIndex = 0 To HeaderCopyCount - 1

                Dim HeaderOffset = CLng(CopyIndex) * HeaderSize
                Dim Header(HeaderSize - 1) As Byte

                ReadAt(HeaderOffset, Header, 0, HeaderSize)
                Buffer.BlockCopy(BitConverter.GetBytes(NewValue), 0, Header, IndexOffsetOffset, 8)
                Buffer.BlockCopy(ComputeMac(Header, HeaderMacCoveredSize, PublicIntegrityKey), 0, Header, HeaderMacOffset, MacSize)
                WriteAt(HeaderOffset, Header, 0, HeaderSize)

            Next

            FlushDurable()

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