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

        ''' <summary>Reads the raw cached physical-data end without the lazy recompute in GetDataEndFromIndex.</summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Function Debug_GetCachedPhysicalDataEnd() As Long

            Return _PhysicalDataEnd

        End Function

        ''' <summary>True when a record has left the live set at the end and the cached data end has not yet been recomputed.</summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Function Debug_PhysicalDataEndIsStale() As Boolean

            Return _PhysicalDataEndDirty

        End Function

        ''' <summary>Drives the next-anchor-id allocator backwards, so the next CreateAnchor would collide with an id in use.</summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptNextAnchorId(Value As Long)

            _NextAnchorId = Value

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
        ''' Files the lowest-id record on the first physical-record page onto the second page
        ''' too, and marks that page dirty - the torn state a process interrupted between the
        ''' two halves of a MovePhysicalRecordOrdinal leaves behind. The next metadata publish
        ''' must refuse to serialise it.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_DuplicateFirstPhysicalRecordOntoTheNextPage()

            Dim Pages = _PhysicalRecordIdsByPage.Keys.OrderBy(Function(pageNumber) pageNumber).ToList()

            If Pages.Count < 2 Then
                Throw New InvalidOperationException("At least two physical-record pages are needed to plant a cross-page duplicate.")
            End If

            Dim RecordId = _PhysicalRecordIdsByPage(Pages(0)).Min()

            _PhysicalRecordIdsByPage(Pages(1)).Add(RecordId)
            _DirtyPhysicalRecordPages.Add(Pages(1))

        End Sub

        ''' <summary>
        ''' Rewrites the second on-disk physical-record page so its first entry is a copy of
        ''' the first page's first entry - one record now on two pages, the record that held
        ''' that slot gone - fixing every MAC so the pages still verify, and blinds the other
        ''' header copy so Open cannot fall back to a clean generation. Reproduces the
        ''' torn-index file an interrupted process persisted before the publish self-check
        ''' existed. The stream must have been durably published (Flush) and hold at least two
        ''' directly-referenced physical-record pages.
        ''' </summary>
        <ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>
        Friend Sub Debug_CorruptPersistedPhysicalRecordPagesWithCrossPageDuplicate()

            Dim PageDescriptors = _PhysicalRecordPageDescriptors.Values.OrderBy(Function(descriptor) descriptor.PageNumber).ToList()

            If PageDescriptors.Count < 2 Then
                Throw New InvalidOperationException("At least two physical-record pages are needed.")
            End If

            Dim Page0(PageDescriptors(0).Length - 1) As Byte
            ReadAt(PageDescriptors(0).Offset, Page0, 0, Page0.Length)

            Dim Page1(PageDescriptors(1).Length - 1) As Byte
            ReadAt(PageDescriptors(1).Offset, Page1, 0, Page1.Length)

            Buffer.BlockCopy(Page0, IndexPageHeaderSize, Page1, IndexPageHeaderSize, PhysicalRecordEntrySize)

            Dim Page1Mac = ComputeMac(Page1, Page1.Length - MacSize, PublicIntegrityKey)
            Buffer.BlockCopy(Page1Mac, 0, Page1, Page1.Length - MacSize, MacSize)
            WriteAt(PageDescriptors(1).Offset, Page1, 0, Page1.Length)

            Dim Root(_MetadataRootLength - 1) As Byte
            ReadAt(_MetadataRootOffset, Root, 0, Root.Length)

            Dim DirectExtentCount = BitConverter.ToInt32(Root, 40)
            Dim ExtentDirectoryCount = BitConverter.ToInt32(Root, 44)
            Dim DirectPhysicalRecordCount = BitConverter.ToInt32(Root, 48)

            If DirectPhysicalRecordCount < 2 Then
                Throw New InvalidOperationException("The physical-record pages are stored through a directory; this helper only patches direct descriptors.")
            End If

            Dim Page1DescriptorOffset = MetadataRootHeaderSize +
                                        ((DirectExtentCount + ExtentDirectoryCount + 1) * MetadataRootDescriptorSize)

            Buffer.BlockCopy(Page1Mac, 0, Root, Page1DescriptorOffset + 20, MacSize)

            Dim RootMac = ComputeMac(Root, Root.Length - MacSize, PublicIntegrityKey)
            Buffer.BlockCopy(RootMac, 0, Root, Root.Length - MacSize, MacSize)
            WriteAt(_MetadataRootOffset, Root, 0, Root.Length)

            For CopyIndex = 0 To HeaderCopyCount - 1

                Dim HeaderOffset = CLng(CopyIndex) * HeaderSize
                Dim Header(HeaderSize - 1) As Byte
                ReadAt(HeaderOffset, Header, 0, HeaderSize)

                If CopyIndex = _ActiveHeaderCopy Then
                    Buffer.BlockCopy(RootMac, 0, Header, IndexMacOffset, MacSize)
                Else
                    Header(MagicOffset) = CByte(Header(MagicOffset) Xor 1)
                End If

                Buffer.BlockCopy(ComputeMac(Header, HeaderMacCoveredSize, PublicIntegrityKey), 0, Header, HeaderMacOffset, MacSize)
                WriteAt(HeaderOffset, Header, 0, HeaderSize)

            Next

            FlushDurable()

        End Sub

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