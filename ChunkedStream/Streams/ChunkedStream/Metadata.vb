Imports System.IO
Imports System.Security.Cryptography

Namespace Streams
    Partial Class ChunkedStream

        Private NotInheritable Class MetadataReadResult
            Public Property IndexPageEntryCount As Integer
            Public Property IndexDirectoryEntryCount As Integer
            Public Property Extents As List(Of ExtentIndexEntry)
            Public Property PhysicalRecords As Dictionary(Of Long, PhysicalRecordEntry)
            Public Property NextPhysicalRecordId As Long
            Public Property NextAnchorId As Long
            Public Property ExtentPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)
            Public Property ExtentDirectoryPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)
            Public Property PhysicalRecordPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)
            Public Property PhysicalRecordDirectoryPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)
            Public Property HoleDirectoryPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)
            Public Property HoleRecords As List(Of HoleDirectoryRecord)
        End Class

        Private NotInheritable Class MetadataRootReadResult
            Public Property IndexPageEntryCount As Integer
            Public Property IndexDirectoryEntryCount As Integer
            Public Property ExtentCount As Integer
            Public Property PhysicalRecordCount As Integer
            Public Property NextPhysicalRecordId As Long
            Public Property NextAnchorId As Long
            Public Property DirectExtentPageDescriptors As List(Of MetadataPageDescriptor)
            Public Property ExtentDirectoryPageDescriptors As List(Of MetadataPageDescriptor)
            Public Property DirectPhysicalRecordPageDescriptors As List(Of MetadataPageDescriptor)
            Public Property PhysicalRecordDirectoryPageDescriptors As List(Of MetadataPageDescriptor)
            Public Property HoleDirectoryPageDescriptors As List(Of MetadataPageDescriptor)
        End Class

        Private Shared Function ComputeMac(Buffer As Byte(), Count As Integer, MacKey As Byte()) As Byte()

            Using Hmac As New HMACSHA256(MacKey)
                Return Hmac.ComputeHash(Buffer, 0, Count)
            End Using

        End Function

        Private Shared Function ComputeMac(Buffer As Byte(), MacKey As Byte()) As Byte()

            Return ComputeMac(Buffer, Buffer.Length, MacKey)

        End Function

        Private Function TryGetCompactMetadataWriteOffset(Length As Integer,
                                                          ByRef Offset As Long) As Boolean

            If Length <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            If _CompactMetadataWriteOffset.HasValue = False Then
                Offset = -1
                Return False
            End If

            Dim CandidateOffset = _CompactMetadataWriteOffset.Value
            Dim CandidateEndOffset = CandidateOffset + CLng(Length)

            If _CompactMetadataWriteLimit.HasValue AndAlso CandidateEndOffset > _CompactMetadataWriteLimit.Value Then
                Offset = -1
                Return False
            End If

            Offset = CandidateOffset
            _CompactMetadataWriteOffset = CandidateEndOffset

            Return True

        End Function

        Private Function ShouldPersistHoleDirectory(Durable As Boolean) As Boolean

            Select Case Options.HoleDirectoryMode
                Case ChunkedStreamOptions.HoleDirectoryModes.Never
                    Return False

                Case ChunkedStreamOptions.HoleDirectoryModes.Always
                    Return True

                Case ChunkedStreamOptions.HoleDirectoryModes.Auto
                    If Durable = False Then Return False
                    Return Math.Max(BaseStream.Length, GetDataEndFromIndex()) >= Options.HoleDirectoryAutoThresholdBytes

                Case Else
                    Return False
            End Select

        End Function

        Private Function BuildExtentPage(PageNumber As Integer) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))

            If _IndexPageEntryCount <= 0 Then
                Throw New InvalidDataException("Invalid extent page entry count.")
            End If

            Dim PageLengthWithoutMac =
                IndexPageHeaderSize +
                (_IndexPageEntryCount * ExtentEntrySize)

            Dim Page(PageLengthWithoutMac + MacSize - 1) As Byte
            Dim FirstIndex = PageNumber * _IndexPageEntryCount

            Dim EntryCount =
                Math.Max(0,
                         Math.Min(_IndexPageEntryCount,
                                  _Extents.Count - FirstIndex))

            Buffer.BlockCopy(IndexPageMagic, 0, Page, 0, IndexPageMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryTypes.ExtentPages)), 0, Page, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PageNumber), 0, Page, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(FirstIndex), 0, Page, 16, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EntryCount), 0, Page, 20, 4)

            Dim EntryOffset = IndexPageHeaderSize

            For EntryIndex = 0 To _IndexPageEntryCount - 1

                Dim SourceIndex = FirstIndex + EntryIndex

                If SourceIndex < _Extents.Count Then

                    Dim Entry = _Extents(SourceIndex)

                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.LogicalOffset), 0, Page, EntryOffset, 8)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.LogicalLength), 0, Page, EntryOffset + 8, 4)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.PhysicalRecordId), 0, Page, EntryOffset + 12, 8)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.PhysicalRecordOffset), 0, Page, EntryOffset + 20, 4)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.AnchorId), 0, Page, EntryOffset + 24, 8)

                End If

                EntryOffset += ExtentEntrySize

            Next

            Dim Mac = ComputeMac(Page,
                                 PageLengthWithoutMac,
                                 PublicIntegrityKey)

            Buffer.BlockCopy(Mac,
                             0,
                             Page,
                             PageLengthWithoutMac,
                             MacSize)

            Return Page

        End Function

        Private Sub WriteExtentPage(PageNumber As Integer)

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))

            Dim OldDescriptor As MetadataPageDescriptor = Nothing
            Dim HadOldDescriptor = _ExtentPageDescriptors.TryGetValue(PageNumber, OldDescriptor)
            Dim FirstIndex = PageNumber * _IndexPageEntryCount
            Dim EntryCount = Math.Max(0, Math.Min(_IndexPageEntryCount, _Extents.Count - FirstIndex))

            If EntryCount = 0 Then
                If HadOldDescriptor Then
                    If OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                        DeferFreeSpace(OldDescriptor.Offset, OldDescriptor.Length)
                    End If

                    _ExtentPageDescriptors.Remove(PageNumber)
                End If

                Return
            End If

            Dim Page = BuildExtentPage(PageNumber)
            Dim Offset = GetNextWriteOffset(Page.Length, Options.NewIndexPageWriteLocationPolicy, True)

            WriteAt(Offset, Page, 0, Page.Length)

            Dim Descriptor = New MetadataPageDescriptor With {
                .PageNumber = PageNumber,
                .Offset = Offset,
                .Length = Page.Length,
                .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
            }

            If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                DeferFreeSpace(OldDescriptor.Offset, OldDescriptor.Length)
            End If

            _ExtentPageDescriptors(PageNumber) = Descriptor

        End Sub

        Private Function BuildPhysicalRecordPage(PageNumber As Integer) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If _IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid physical-record page entry count.")

            Dim PageLengthWithoutMac =
                IndexPageHeaderSize +
                (_IndexPageEntryCount * PhysicalRecordEntrySize)

            Dim Page(PageLengthWithoutMac + MacSize - 1) As Byte
            Dim FirstIndex = PageNumber * _IndexPageEntryCount

            Dim PageRecordIds As SortedSet(Of Long) = Nothing

            If _PhysicalRecordIdsByPage.TryGetValue(PageNumber, PageRecordIds) = False Then
                PageRecordIds = New SortedSet(Of Long)()
            End If

            Dim EntryCount = PageRecordIds.Count

            If EntryCount > _IndexPageEntryCount Then
                Throw New InvalidDataException(
                    $"Physical-record page {PageNumber} contains too many entries.")
            End If

            Buffer.BlockCopy(IndexPageMagic, 0, Page, 0, IndexPageMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryTypes.PhysicalRecordPages)), 0, Page, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PageNumber), 0, Page, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(FirstIndex), 0, Page, 16, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EntryCount), 0, Page, 20, 4)

            Dim EntryOffset = IndexPageHeaderSize

            For Each RecordId In PageRecordIds

                Dim Entry = GetPhysicalRecord(RecordId)

                Buffer.BlockCopy(BitConverter.GetBytes(Entry.RecordId), 0, Page, EntryOffset, 8)
                Buffer.BlockCopy(BitConverter.GetBytes(Entry.PhysicalOffset), 0, Page, EntryOffset + 8, 8)
                Buffer.BlockCopy(BitConverter.GetBytes(Entry.PhysicalLength), 0, Page, EntryOffset + 16, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(Entry.PlainLength), 0, Page, EntryOffset + 20, 4)
                Buffer.BlockCopy(BitConverter.GetBytes(Entry.RefCount), 0, Page, EntryOffset + 24, 4)

                EntryOffset += PhysicalRecordEntrySize

            Next

            Dim Mac =
                ComputeMac(Page,
                           PageLengthWithoutMac,
                           PublicIntegrityKey)

            Buffer.BlockCopy(Mac,
                             0,
                             Page,
                             PageLengthWithoutMac,
                             MacSize)

            Return Page

        End Function

        Private Sub WritePhysicalRecordPage(PageNumber As Integer)

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))

            Dim OldDescriptor As MetadataPageDescriptor = Nothing
            Dim HadOldDescriptor =
                _PhysicalRecordPageDescriptors.TryGetValue(PageNumber,
                                                            OldDescriptor)

            Dim PageRecordIds As SortedSet(Of Long) = Nothing
            Dim EntryCount = 0

            If _PhysicalRecordIdsByPage.TryGetValue(PageNumber,
                                                    PageRecordIds) Then

                EntryCount = PageRecordIds.Count

            End If

            If EntryCount = 0 Then

                If HadOldDescriptor Then

                    If OldDescriptor.Offset > 0 AndAlso
                        OldDescriptor.Length > 0 Then

                        DeferFreeSpace(OldDescriptor.Offset,
                                                OldDescriptor.Length)

                    End If

                    _PhysicalRecordPageDescriptors.Remove(PageNumber)

                End If

                Return

            End If

            Dim Page = BuildPhysicalRecordPage(PageNumber)
            Dim Offset = GetNextWriteOffset(Page.Length, Options.NewIndexPageWriteLocationPolicy, True)

            WriteAt(Offset, Page, 0, Page.Length)

            Dim Descriptor =
                New MetadataPageDescriptor With {
                    .PageNumber = PageNumber,
                    .Offset = Offset,
                    .Length = Page.Length,
                    .Mac = ComputeMac(Page,
                                        Page.Length - MacSize,
                                        PublicIntegrityKey)
                }

            If HadOldDescriptor AndAlso
                OldDescriptor.Offset > 0 AndAlso
                OldDescriptor.Length > 0 Then

                DeferFreeSpace(OldDescriptor.Offset,
                                        OldDescriptor.Length)

            End If

            _PhysicalRecordPageDescriptors(PageNumber) = Descriptor

        End Sub

        Private Function BuildDirectoryPage(DirectoryType As DirectoryTypes,
                                            PageNumber As Integer,
                                            Entries As IList(Of MetadataPageDescriptor)) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If Entries Is Nothing Then Throw New ArgumentNullException(NameOf(Entries))
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata directory entry count.")

            Dim PageLengthWithoutMac = DirectoryPageHeaderSize + (_IndexDirectoryEntryCount * MetadataDescriptorSize)
            Dim Page(PageLengthWithoutMac + MacSize - 1) As Byte
            Dim FirstEntryIndex = PageNumber * _IndexDirectoryEntryCount
            Dim EntryCount = Math.Max(0, Math.Min(_IndexDirectoryEntryCount, Entries.Count - FirstEntryIndex))

            Buffer.BlockCopy(DirectoryPageMagic, 0, Page, 0, DirectoryPageMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryType)), 0, Page, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PageNumber), 0, Page, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EntryCount), 0, Page, 16, 4)

            Dim EntryOffset = DirectoryPageHeaderSize

            For EntryIndex = 0 To _IndexDirectoryEntryCount - 1
                Dim SourceIndex = FirstEntryIndex + EntryIndex

                If SourceIndex < Entries.Count Then
                    Dim Entry = Entries(SourceIndex)

                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.PageNumber), 0, Page, EntryOffset, 4)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.Offset), 0, Page, EntryOffset + 4, 8)
                    Buffer.BlockCopy(BitConverter.GetBytes(Entry.Length), 0, Page, EntryOffset + 12, 4)

                    If Entry.Mac IsNot Nothing Then
                        Buffer.BlockCopy(Entry.Mac, 0, Page, EntryOffset + 16, Math.Min(MacSize, Entry.Mac.Length))
                    End If
                End If

                EntryOffset += MetadataDescriptorSize
            Next

            Dim Mac = ComputeMac(Page, PageLengthWithoutMac, PublicIntegrityKey)
            Buffer.BlockCopy(Mac, 0, Page, PageLengthWithoutMac, MacSize)

            Return Page

        End Function

        Private Function WriteDirectoryPages(DirectoryType As DirectoryTypes,
                                             Descriptors As IEnumerable(Of MetadataPageDescriptor),
                                             ExistingDirectoryDescriptors As Dictionary(Of Integer, MetadataPageDescriptor)) As Dictionary(Of Integer, MetadataPageDescriptor)

            If Descriptors Is Nothing Then Throw New ArgumentNullException(NameOf(Descriptors))
            If ExistingDirectoryDescriptors Is Nothing Then Throw New ArgumentNullException(NameOf(ExistingDirectoryDescriptors))

            Dim DescriptorList = Descriptors.OrderBy(Function(descriptor) descriptor.PageNumber).ToList()
            Dim DirectoryPageCount = GetIndexPageCount(DescriptorList.Count, _IndexDirectoryEntryCount)
            Dim Result As New Dictionary(Of Integer, MetadataPageDescriptor)()

            For PageNumber = 0 To DirectoryPageCount - 1
                Dim Page = BuildDirectoryPage(DirectoryType, PageNumber, DescriptorList)
                Dim Offset = GetNextWriteOffset(Page.Length, Options.NewIndexDirectoryPageWriteLocationPolicy, True)
                Dim OldDescriptor As MetadataPageDescriptor = Nothing
                Dim HadOldDescriptor = ExistingDirectoryDescriptors.TryGetValue(PageNumber, OldDescriptor)

                WriteAt(Offset, Page, 0, Page.Length)

                Dim NewDescriptor = New MetadataPageDescriptor With {
                    .PageNumber = PageNumber,
                    .Offset = Offset,
                    .Length = Page.Length,
                    .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
                }

                If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                    DeferFreeSpace(OldDescriptor.Offset, OldDescriptor.Length)
                End If

                Result(PageNumber) = NewDescriptor
            Next

            Return Result

        End Function

        Private Function BuildHoleDirectoryPage(PageNumber As Integer,
                                                Records As IList(Of HoleDirectoryRecord)) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If Records Is Nothing Then Throw New ArgumentNullException(NameOf(Records))
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid hole directory entry count.")

            Dim PageLengthWithoutMac = DirectoryPageHeaderSize + (_IndexDirectoryEntryCount * HoleDirectoryEntrySize)
            Dim Page(PageLengthWithoutMac + MacSize - 1) As Byte
            Dim FirstEntryIndex = PageNumber * _IndexDirectoryEntryCount
            Dim EntryCount = Math.Max(0, Math.Min(_IndexDirectoryEntryCount, Records.Count - FirstEntryIndex))

            Buffer.BlockCopy(DirectoryPageMagic, 0, Page, 0, DirectoryPageMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryTypes.Holes)), 0, Page, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PageNumber), 0, Page, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EntryCount), 0, Page, 16, 4)

            Dim EntryOffset = DirectoryPageHeaderSize

            For EntryIndex = 0 To _IndexDirectoryEntryCount - 1
                Dim SourceIndex = FirstEntryIndex + EntryIndex

                If SourceIndex < Records.Count Then
                    Dim Record = Records(SourceIndex)

                    Buffer.BlockCopy(BitConverter.GetBytes(CInt(Record.SpaceType)), 0, Page, EntryOffset, 4)
                    Buffer.BlockCopy(BitConverter.GetBytes(Record.Offset), 0, Page, EntryOffset + 8, 8)
                    Buffer.BlockCopy(BitConverter.GetBytes(Record.Length), 0, Page, EntryOffset + 16, 8)
                End If

                EntryOffset += HoleDirectoryEntrySize
            Next

            Dim Mac = ComputeMac(Page, PageLengthWithoutMac, PublicIntegrityKey)
            Buffer.BlockCopy(Mac, 0, Page, PageLengthWithoutMac, MacSize)

            Return Page

        End Function

        Private Function WriteHoleDirectoryPages(Records As IList(Of HoleDirectoryRecord)) As Dictionary(Of Integer, MetadataPageDescriptor)

            Dim Result As New Dictionary(Of Integer, MetadataPageDescriptor)()

            If Records Is Nothing OrElse Records.Count = 0 Then
                Return Result
            End If

            Dim DirectoryPageCount = GetIndexPageCount(Records.Count, _IndexDirectoryEntryCount)

            For PageNumber = 0 To DirectoryPageCount - 1
                Dim Page = BuildHoleDirectoryPage(PageNumber, Records)
                Dim Offset = GetNextWriteOffset(Page.Length, Options.NewIndexDirectoryPageWriteLocationPolicy, True)
                Dim OldDescriptor As MetadataPageDescriptor = Nothing
                Dim HadOldDescriptor = _HoleDirectoryPageDescriptors.TryGetValue(PageNumber, OldDescriptor)

                WriteAt(Offset, Page, 0, Page.Length)

                Dim NewDescriptor = New MetadataPageDescriptor With {
                    .PageNumber = PageNumber,
                    .Offset = Offset,
                    .Length = Page.Length,
                    .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
                }

                If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                    DeferFreeSpace(OldDescriptor.Offset, OldDescriptor.Length)
                End If

                Result(PageNumber) = NewDescriptor
            Next

            Return Result

        End Function

        ''' <summary>
        ''' True when the entire file's physical-record state is exactly one unshared record
        ''' sitting at the deterministic start of the data area. In this state, no physical
        ''' record page or descriptor needs to be persisted at all - the record's own
        ''' self-describing header (PlainLength/PayloadLength) plus its fixed offset are enough
        ''' to reconstruct its index entry on Open(), and RefCount is trivially 1.
        ''' </summary>
        Private Function CanElidePhysicalRecordPaging() As Boolean

            If _Extents.Count <> 1 Then Return False
            If _Extents(0).PhysicalRecordId = SparsePhysicalRecordId Then Return False
            If _PhysicalRecords.Count <> 1 Then Return False

            Dim Record As PhysicalRecordEntry

            If _PhysicalRecords.TryGetValue(_Extents(0).PhysicalRecordId, Record) = False Then Return False

            If Record.RefCount <> 1 Then Return False
            If Record.PhysicalOffset <> DataStartOffset Then Return False

            Return True

        End Function

        Private Function BuildMetadataRoot(DirectExtentPageDescriptors As IEnumerable(Of MetadataPageDescriptor),
                                           ExtentDirectoryDescriptors As IEnumerable(Of MetadataPageDescriptor),
                                           DirectPhysicalRecordPageDescriptors As IEnumerable(Of MetadataPageDescriptor),
                                           PhysicalRecordDirectoryDescriptors As IEnumerable(Of MetadataPageDescriptor),
                                           HoleDirectoryDescriptors As IEnumerable(Of MetadataPageDescriptor)) As Byte()

            Dim DirectExtentList =
                If(DirectExtentPageDescriptors,
                   Enumerable.Empty(Of MetadataPageDescriptor)()).
                OrderBy(Function(descriptor) descriptor.PageNumber).
                ToList()

            Dim ExtentDirectoryList =
                If(ExtentDirectoryDescriptors,
                   Enumerable.Empty(Of MetadataPageDescriptor)()).
                OrderBy(Function(descriptor) descriptor.PageNumber).
                ToList()

            Dim DirectPhysicalRecordList =
                If(DirectPhysicalRecordPageDescriptors,
                   Enumerable.Empty(Of MetadataPageDescriptor)()).
                OrderBy(Function(descriptor) descriptor.PageNumber).
                ToList()

            Dim PhysicalRecordDirectoryList =
                If(PhysicalRecordDirectoryDescriptors,
                   Enumerable.Empty(Of MetadataPageDescriptor)()).
                OrderBy(Function(descriptor) descriptor.PageNumber).
                ToList()

            Dim HoleDirectoryList =
                If(HoleDirectoryDescriptors,
                   Enumerable.Empty(Of MetadataPageDescriptor)()).
                OrderBy(Function(descriptor) descriptor.PageNumber).
                ToList()

            Dim DescriptorCount =
                DirectExtentList.Count +
                ExtentDirectoryList.Count +
                DirectPhysicalRecordList.Count +
                PhysicalRecordDirectoryList.Count +
                HoleDirectoryList.Count

            Dim RootLengthWithoutMac =
                MetadataRootHeaderSize +
                (DescriptorCount * MetadataRootDescriptorSize)

            Dim Root(RootLengthWithoutMac + MacSize - 1) As Byte

            Dim EffectivePhysicalRecordCount =
                If(CanElidePhysicalRecordPaging(), 0, _PhysicalRecords.Count)

            Buffer.BlockCopy(MetadataRootMagic, 0, Root, 0, MetadataRootMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexPageEntryCount), 0, Root, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexDirectoryEntryCount), 0, Root, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_Extents.Count)), 0, Root, 16, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(EffectivePhysicalRecordCount)), 0, Root, 24, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(_NextPhysicalRecordId), 0, Root, 32, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(DirectExtentList.Count), 0, Root, 40, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(ExtentDirectoryList.Count), 0, Root, 44, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(DirectPhysicalRecordList.Count), 0, Root, 48, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PhysicalRecordDirectoryList.Count), 0, Root, 52, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(HoleDirectoryList.Count), 0, Root, 56, 4)

            '
            ' Bytes 60 through 63 are reserved.
            '
            Buffer.BlockCopy(BitConverter.GetBytes(_NextAnchorId), 0, Root, 64, 8)

            Dim EntryOffset = MetadataRootHeaderSize

            For Each Descriptor In DirectExtentList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.ExtentPages, Descriptor)
                EntryOffset += MetadataRootDescriptorSize
            Next

            For Each Descriptor In ExtentDirectoryList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.ExtentPages, Descriptor)
                EntryOffset += MetadataRootDescriptorSize
            Next

            For Each Descriptor In DirectPhysicalRecordList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.PhysicalRecordPages, Descriptor)
                EntryOffset += MetadataRootDescriptorSize
            Next

            For Each Descriptor In PhysicalRecordDirectoryList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.PhysicalRecordPages, Descriptor)
                EntryOffset += MetadataRootDescriptorSize
            Next

            For Each Descriptor In HoleDirectoryList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.Holes, Descriptor)
                EntryOffset += MetadataRootDescriptorSize
            Next

            Dim Mac = ComputeMac(Root,
                                 RootLengthWithoutMac,
                                 PublicIntegrityKey)

            Buffer.BlockCopy(Mac,
                             0,
                             Root,
                             RootLengthWithoutMac,
                             MacSize)

            Return Root

        End Function

        Private Shared Sub WriteMetadataRootDescriptor(Buffer As Byte(),
                                                       Offset As Integer,
                                                       DirectoryType As DirectoryTypes,
                                                       Descriptor As MetadataPageDescriptor)

            If Buffer Is Nothing Then Throw New ArgumentNullException(NameOf(Buffer))
            If Offset < 0 OrElse Offset + MetadataRootDescriptorSize > Buffer.Length Then Throw New ArgumentOutOfRangeException(NameOf(Offset))
            If Descriptor.Mac Is Nothing OrElse Descriptor.Mac.Length <> MacSize Then Throw New InvalidDataException("Invalid metadata descriptor MAC.")

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryType)), 0, Buffer, Offset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.PageNumber), 0, Buffer, Offset + 4, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.Offset), 0, Buffer, Offset + 8, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.Length), 0, Buffer, Offset + 16, 4)
            System.Buffer.BlockCopy(Descriptor.Mac, 0, Buffer, Offset + 20, MacSize)

        End Sub

        Private Sub PersistPagedMetadata(IndexOffset As Long,
                                         Durable As Boolean)

            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid index offset.")

            _IndexOffset = IndexOffset

            If _IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata page entry count.")
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata directory entry count.")

            Dim RequiredExtentPageCount =
                GetIndexPageCount(_Extents.Count,
                                    _IndexPageEntryCount)

            Dim RemovedExtentPages =
                _ExtentPageDescriptors.Keys.
                                        Where(Function(pageNumber) pageNumber >= RequiredExtentPageCount).
                                        ToArray()

            For Each PageNumber In RemovedExtentPages

                Dim Descriptor = _ExtentPageDescriptors(PageNumber)

                DeferFreeSpace(Descriptor.Offset,
                                        Descriptor.Length)

                _ExtentPageDescriptors.Remove(PageNumber)

            Next

            Dim RequiredPhysicalRecordPageCount =
                GetIndexPageCount(_PhysicalRecords.Count,
                                    _IndexPageEntryCount)

            If CanElidePhysicalRecordPaging() Then RequiredPhysicalRecordPageCount = 0

            Dim RemovedPhysicalRecordPages =
                _PhysicalRecordPageDescriptors.Keys.
                                                Where(Function(pageNumber) pageNumber >= RequiredPhysicalRecordPageCount).
                                                ToArray()

            For Each PageNumber In RemovedPhysicalRecordPages

                Dim Descriptor =
                    _PhysicalRecordPageDescriptors(PageNumber)

                DeferFreeSpace(Descriptor.Offset,
                                        Descriptor.Length)

                _PhysicalRecordPageDescriptors.Remove(PageNumber)

            Next

            For Each PageNumber In _DirtyExtentPages.ToArray()

                If PageNumber < RequiredExtentPageCount Then
                    WriteExtentPage(PageNumber)
                End If

            Next

            For Each PageNumber In _DirtyPhysicalRecordPages.ToArray()

                If PageNumber < RequiredPhysicalRecordPageCount Then
                    WritePhysicalRecordPage(PageNumber)
                End If

            Next

            _DirtyExtentPages.Clear()
            _DirtyPhysicalRecordPages.Clear()

            Dim DirectExtentPageDescriptors =
                _ExtentPageDescriptors.Values.
                                        OrderBy(Function(descriptor) descriptor.PageNumber).
                                        ToArray()

            Dim ExtentDirectoryDescriptors =
                Enumerable.Empty(Of MetadataPageDescriptor)().ToArray()

            If _ExtentPageDescriptors.Count > _IndexDirectoryEntryCount Then

                Dim NewExtentDirectoryDescriptors =
                    WriteDirectoryPages(DirectoryTypes.ExtentPages,
                                        _ExtentPageDescriptors.Values,
                                        _ExtentDirectoryPageDescriptors)

                _ExtentDirectoryPageDescriptors.Clear()

                For Each Pair In NewExtentDirectoryDescriptors
                    _ExtentDirectoryPageDescriptors(Pair.Key) = Pair.Value
                Next

                DirectExtentPageDescriptors =
                    Enumerable.Empty(Of MetadataPageDescriptor)().ToArray()

                ExtentDirectoryDescriptors =
                    _ExtentDirectoryPageDescriptors.Values.
                                                    OrderBy(Function(descriptor) descriptor.PageNumber).
                                                    ToArray()

            Else

                For Each Descriptor In _ExtentDirectoryPageDescriptors.Values.ToArray()

                    If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                        DeferFreeSpace(Descriptor.Offset,
                                                        Descriptor.Length)
                    End If

                Next

                _ExtentDirectoryPageDescriptors.Clear()

            End If

            Dim DirectPhysicalRecordPageDescriptors =
                _PhysicalRecordPageDescriptors.Values.
                                                OrderBy(Function(descriptor) descriptor.PageNumber).
                                                ToArray()

            Dim PhysicalRecordDirectoryDescriptors =
                Enumerable.Empty(Of MetadataPageDescriptor)().ToArray()

            If _PhysicalRecordPageDescriptors.Count > _IndexDirectoryEntryCount Then

                Dim NewPhysicalRecordDirectoryDescriptors =
                    WriteDirectoryPages(DirectoryTypes.PhysicalRecordPages,
                                        _PhysicalRecordPageDescriptors.Values,
                                        _PhysicalRecordDirectoryPageDescriptors)

                _PhysicalRecordDirectoryPageDescriptors.Clear()

                For Each Pair In NewPhysicalRecordDirectoryDescriptors
                    _PhysicalRecordDirectoryPageDescriptors(Pair.Key) = Pair.Value
                Next

                DirectPhysicalRecordPageDescriptors =
                    Enumerable.Empty(Of MetadataPageDescriptor)().ToArray()

                PhysicalRecordDirectoryDescriptors =
                    _PhysicalRecordDirectoryPageDescriptors.Values.
                                                            OrderBy(Function(descriptor) descriptor.PageNumber).
                                                            ToArray()

            Else

                For Each Descriptor In _PhysicalRecordDirectoryPageDescriptors.Values.ToArray()

                    If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                        DeferFreeSpace(Descriptor.Offset,
                                                        Descriptor.Length)
                    End If

                Next

                _PhysicalRecordDirectoryPageDescriptors.Clear()

            End If

            Dim NewHoleDirectoryDescriptors =
                New Dictionary(Of Integer, MetadataPageDescriptor)()

            If ShouldPersistHoleDirectory(Durable) Then
                NewHoleDirectoryDescriptors =
                    WriteHoleDirectoryPages(GetKnownHoleRecords())
            End If

            For Each Descriptor In _HoleDirectoryPageDescriptors.Values.ToArray()

                If NewHoleDirectoryDescriptors.ContainsKey(Descriptor.PageNumber) = False Then

                    If Descriptor.Offset > 0 AndAlso Descriptor.Length > 0 Then
                        DeferFreeSpace(Descriptor.Offset,
                                                        Descriptor.Length)
                    End If

                End If

            Next

            _HoleDirectoryPageDescriptors.Clear()

            For Each Pair In NewHoleDirectoryDescriptors
                _HoleDirectoryPageDescriptors(Pair.Key) = Pair.Value
            Next

            Dim HoleDirectoryDescriptors =
                _HoleDirectoryPageDescriptors.Values.
                                                OrderBy(Function(descriptor) descriptor.PageNumber).
                                                ToArray()

            Dim Root =
                BuildMetadataRoot(DirectExtentPageDescriptors,
                                    ExtentDirectoryDescriptors,
                                    DirectPhysicalRecordPageDescriptors,
                                    PhysicalRecordDirectoryDescriptors,
                                    HoleDirectoryDescriptors)

            Dim NewRootMac(MacSize - 1) As Byte
            Buffer.BlockCopy(Root, Root.Length - MacSize, NewRootMac, 0, MacSize)

            '
            ' Only republish the root when it actually changed. When every metadata page
            ' descriptor, count, and next-id is unchanged the rebuilt root is byte-for-byte
            ' identical to the one already persisted, so rewriting it would only churn free
            ' space (the old copy deferred, an identical copy appended) on every persist.
            '
            Dim RootChanged =
                _MetadataRootOffset <= 0 OrElse
                _MetadataRootLength <> Root.Length OrElse
                _MetadataRootMac Is Nothing OrElse
                _MetadataRootOffset + CLng(_MetadataRootLength) > BaseStream.Length OrElse
                FixedTimeEquals(_MetadataRootMac, 0, NewRootMac, 0, MacSize) = False

            If RootChanged Then

                Dim OldRootOffset = _MetadataRootOffset
                Dim OldRootLength = _MetadataRootLength
                Dim CompactRootOffset As Long

                If TryGetCompactMetadataWriteOffset(Root.Length, CompactRootOffset) Then
                    _MetadataRootOffset = CompactRootOffset
                Else
                    _MetadataRootOffset = Math.Max(BaseStream.Length, GetDataEndFromIndex())
                End If

                _MetadataRootLength = Root.Length

                WriteAt(_MetadataRootOffset, Root, 0, Root.Length)

                If OldRootOffset > 0 AndAlso OldRootLength > 0 Then
                    DeferFreeSpace(OldRootOffset, OldRootLength)
                End If

                _MetadataRootMac = NewRootMac

            End If

            If Durable Then FlushDurable()

            UpdateHeader(Durable)

            If Durable Then FlushDurable()

            '
            ' The new header is now durable. Every generation the deferred free space
            ' belonged to is superseded and can no longer be selected by Open, so the
            ' space is finally safe to reallocate.
            '
            If Durable Then PromoteDeferredFreeSpaces()

        End Sub

        Private Shared Function ReadMetadataRootDescriptor(Buffer As Byte(), Offset As Integer) As MetadataPageDescriptor

            If Buffer Is Nothing Then Throw New ArgumentNullException(NameOf(Buffer))
            If Offset < 0 OrElse Offset + MetadataRootDescriptorSize > Buffer.Length Then Throw New ArgumentOutOfRangeException(NameOf(Offset))

            Dim Mac(MacSize - 1) As Byte
            System.Buffer.BlockCopy(Buffer, Offset + 20, Mac, 0, Mac.Length)

            Return New MetadataPageDescriptor With {
                .PageNumber = BitConverter.ToInt32(Buffer, Offset + 4),
                .Offset = BitConverter.ToInt64(Buffer, Offset + 8),
                .Length = BitConverter.ToInt32(Buffer, Offset + 16),
                .Mac = Mac
            }

        End Function

        Private Shared Function ReadDirectoryMetadataPageDescriptor(Buffer As Byte(), Offset As Integer) As MetadataPageDescriptor

            If Buffer Is Nothing Then Throw New ArgumentNullException(NameOf(Buffer))
            If Offset < 0 OrElse Offset + MetadataDescriptorSize > Buffer.Length Then Throw New ArgumentOutOfRangeException(NameOf(Offset))

            Dim Mac(MacSize - 1) As Byte
            System.Buffer.BlockCopy(Buffer, Offset + 16, Mac, 0, Mac.Length)

            Return New MetadataPageDescriptor With {
                .PageNumber = BitConverter.ToInt32(Buffer, Offset),
                .Offset = BitConverter.ToInt64(Buffer, Offset + 4),
                .Length = BitConverter.ToInt32(Buffer, Offset + 12),
                .Mac = Mac
            }

        End Function

        Private Shared Function ReadPagedMetadata(BaseStream As Stream,
                                                  RootOffset As Long,
                                                  RootLength As Integer,
                                                  ExpectedMac As Byte(),
                                                  ByRef IndexPageEntryCount As Integer,
                                                  ByRef IndexDirectoryEntryCount As Integer) As MetadataReadResult

            Dim Root = ReadMetadataRoot(BaseStream, RootOffset, RootLength, ExpectedMac)

            IndexPageEntryCount = Root.IndexPageEntryCount
            IndexDirectoryEntryCount = Root.IndexDirectoryEntryCount

            Dim ExtentPageDescriptors =
                If(Root.DirectExtentPageDescriptors.Count > 0,
                   Root.DirectExtentPageDescriptors,
                   ReadDirectoryPages(BaseStream, Root.ExtentDirectoryPageDescriptors, DirectoryTypes.ExtentPages))

            Dim PhysicalRecordPageDescriptors =
                If(Root.DirectPhysicalRecordPageDescriptors.Count > 0,
                   Root.DirectPhysicalRecordPageDescriptors,
                   ReadDirectoryPages(BaseStream, Root.PhysicalRecordDirectoryPageDescriptors, DirectoryTypes.PhysicalRecordPages))

            Dim Extents = ReadExtentPages(BaseStream, ExtentPageDescriptors, Root.ExtentCount, IndexPageEntryCount)
            Dim PhysicalRecords = ReadPhysicalRecordPages(BaseStream, PhysicalRecordPageDescriptors, Root.PhysicalRecordCount, IndexPageEntryCount)
            Dim HoleRecords = ReadHoleDirectoryPages(BaseStream, Root.HoleDirectoryPageDescriptors)

            If PhysicalRecords.Count = 0 AndAlso
               Extents.Count = 1 AndAlso
               Extents(0).PhysicalRecordId <> SparsePhysicalRecordId Then

                Dim RecordId = Extents(0).PhysicalRecordId
                Dim Header(ChunkRecordHeaderSize - 1) As Byte

                BaseStream.Position = DataStartOffset
                ReadExactly(BaseStream, Header, 0, Header.Length)

                Dim StoredRecordId = BitConverter.ToInt64(Header, 0)

                If StoredRecordId <> RecordId Then
                    Throw New InvalidDataException(
                        $"Expected an elided physical record {RecordId} at the start of the data area, found {StoredRecordId}.")
                End If

                Dim PlainLength = BitConverter.ToInt32(Header, ChunkPlainLengthOffset)
                Dim PayloadLength = BitConverter.ToInt32(Header, ChunkPayloadLengthOffset)

                PhysicalRecords(RecordId) =
                    New PhysicalRecordEntry With {
                        .RecordId = RecordId,
                        .PhysicalOffset = DataStartOffset,
                        .PhysicalLength = ChunkRecordDataOffset + PayloadLength + MacSize,
                        .PlainLength = PlainLength,
                        .RefCount = 1
                    }

            End If

            Return New MetadataReadResult With {
                .IndexPageEntryCount = Root.IndexPageEntryCount,
                .IndexDirectoryEntryCount = Root.IndexDirectoryEntryCount,
                .Extents = Extents,
                .PhysicalRecords = PhysicalRecords,
                .NextPhysicalRecordId = Math.Max(1L, Root.NextPhysicalRecordId),
                .NextAnchorId = Math.Max(1L, Root.NextAnchorId),
                .ExtentPageDescriptors = ExtentPageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber),
                .ExtentDirectoryPageDescriptors = Root.ExtentDirectoryPageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber),
                .PhysicalRecordPageDescriptors = PhysicalRecordPageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber),
                .PhysicalRecordDirectoryPageDescriptors = Root.PhysicalRecordDirectoryPageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber),
                .HoleDirectoryPageDescriptors = Root.HoleDirectoryPageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber),
                .HoleRecords = HoleRecords
            }

        End Function

        'TODO: Check
        Private Shared Function ReadMetadataRoot(BaseStream As Stream,
                                                 RootOffset As Long,
                                                 RootLength As Integer,
                                                 ExpectedMac As Byte()) As MetadataRootReadResult

            If RootLength = 0 Then
                Return New MetadataRootReadResult With {
                    .IndexPageEntryCount = 256,
                    .IndexDirectoryEntryCount = 256,
                    .ExtentCount = 0,
                    .PhysicalRecordCount = 0,
                    .NextPhysicalRecordId = 1,
                    .NextAnchorId = 1,
                    .DirectExtentPageDescriptors = New List(Of MetadataPageDescriptor)(),
                    .ExtentDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)(),
                    .DirectPhysicalRecordPageDescriptors = New List(Of MetadataPageDescriptor)(),
                    .PhysicalRecordDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)(),
                    .HoleDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)()
                }
            End If

            If RootOffset < DataStartOffset Then Throw New InvalidDataException("Invalid metadata root offset.")
            If RootLength < MetadataRootHeaderSize + MacSize Then Throw New InvalidDataException("Invalid metadata root length.")
            If RootOffset + RootLength > BaseStream.Length Then Throw New InvalidDataException("Metadata root extends beyond end of stream.")

            Dim Root(RootLength - 1) As Byte

            BaseStream.Position = RootOffset
            ReadExactly(BaseStream, Root, 0, Root.Length)

            If FixedTimeEquals(MetadataRootMagic, 0, Root, 0, MetadataRootMagicSize) = False Then
                Throw New InvalidDataException("Invalid metadata root magic.")
            End If

            Dim RootMac = ComputeMac(Root, RootLength - MacSize, PublicIntegrityKey)

            If ExpectedMac IsNot Nothing AndAlso ExpectedMac.Length = MacSize Then
                If FixedTimeEquals(RootMac, 0, ExpectedMac, 0, MacSize) = False Then
                    Throw New CryptographicException("Metadata root MAC invalid.")
                End If
            End If

            If FixedTimeEquals(RootMac, 0, Root, RootLength - MacSize, MacSize) = False Then
                Throw New CryptographicException("Metadata root embedded MAC invalid.")
            End If

            Dim Result As New MetadataRootReadResult With {
                .IndexPageEntryCount = BitConverter.ToInt32(Root, 8),
                .IndexDirectoryEntryCount = BitConverter.ToInt32(Root, 12),
                .ExtentCount = CInt(BitConverter.ToInt64(Root, 16)),
                .PhysicalRecordCount = CInt(BitConverter.ToInt64(Root, 24)),
                .NextPhysicalRecordId = BitConverter.ToInt64(Root, 32),
                .NextAnchorId = BitConverter.ToInt64(Root, 64),
                .DirectExtentPageDescriptors = New List(Of MetadataPageDescriptor)(),
                .ExtentDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)(),
                .DirectPhysicalRecordPageDescriptors = New List(Of MetadataPageDescriptor)(),
                .PhysicalRecordDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)(),
                .HoleDirectoryPageDescriptors = New List(Of MetadataPageDescriptor)()
            }

            If Result.NextAnchorId <= 0 Then
                Throw New InvalidDataException("Invalid next anchor id.")
            End If

            Dim DirectExtentDescriptorCount = BitConverter.ToInt32(Root, 40)
            Dim ExtentDirectoryDescriptorCount = BitConverter.ToInt32(Root, 44)
            Dim DirectPhysicalRecordDescriptorCount = BitConverter.ToInt32(Root, 48)
            Dim PhysicalRecordDirectoryDescriptorCount = BitConverter.ToInt32(Root, 52)
            Dim HoleDirectoryDescriptorCount = BitConverter.ToInt32(Root, 56)

            If Result.IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata page entry count.")
            If Result.IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata directory entry count.")
            If Result.ExtentCount < 0 Then Throw New InvalidDataException("Invalid extent count.")
            If Result.PhysicalRecordCount < 0 Then Throw New InvalidDataException("Invalid physical-record count.")
            If Result.NextPhysicalRecordId <= 0 Then Throw New InvalidDataException("Invalid next physical record id.")

            Dim EntryOffset = MetadataRootHeaderSize
            Dim DescriptorEndOffset = RootLength - MacSize

            ReadRootDescriptorGroup(Root, EntryOffset, DescriptorEndOffset, DirectExtentDescriptorCount, DirectoryTypes.ExtentPages, Result.DirectExtentPageDescriptors)
            ReadRootDescriptorGroup(Root, EntryOffset, DescriptorEndOffset, ExtentDirectoryDescriptorCount, DirectoryTypes.ExtentPages, Result.ExtentDirectoryPageDescriptors)
            ReadRootDescriptorGroup(Root, EntryOffset, DescriptorEndOffset, DirectPhysicalRecordDescriptorCount, DirectoryTypes.PhysicalRecordPages, Result.DirectPhysicalRecordPageDescriptors)
            ReadRootDescriptorGroup(Root, EntryOffset, DescriptorEndOffset, PhysicalRecordDirectoryDescriptorCount, DirectoryTypes.PhysicalRecordPages, Result.PhysicalRecordDirectoryPageDescriptors)
            ReadRootDescriptorGroup(Root, EntryOffset, DescriptorEndOffset, HoleDirectoryDescriptorCount, DirectoryTypes.Holes, Result.HoleDirectoryPageDescriptors)

            Return Result

        End Function

        Private Shared Sub ReadRootDescriptorGroup(Buffer As Byte(),
                                                   ByRef EntryOffset As Integer,
                                                   DescriptorEndOffset As Integer,
                                                   Count As Integer,
                                                   ExpectedDirectoryType As DirectoryTypes,
                                                   Target As List(Of MetadataPageDescriptor))

            If Count < 0 Then Throw New InvalidDataException("Invalid metadata root descriptor count.")
            If Target Is Nothing Then Throw New ArgumentNullException(NameOf(Target))

            For Index = 0 To Count - 1
                If EntryOffset + MetadataRootDescriptorSize > DescriptorEndOffset Then
                    Throw New InvalidDataException("Metadata root descriptor area is truncated.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Buffer, EntryOffset), DirectoryTypes)

                If DirectoryType <> ExpectedDirectoryType Then
                    Throw New InvalidDataException("Metadata root contains an unexpected descriptor type.")
                End If

                Target.Add(ReadMetadataRootDescriptor(Buffer, EntryOffset))
                EntryOffset += MetadataRootDescriptorSize
            Next

        End Sub

        Private Shared Function ReadDirectoryPages(BaseStream As Stream,
                                                   Descriptors As IEnumerable(Of MetadataPageDescriptor),
                                                   ExpectedDirectoryType As DirectoryTypes) As List(Of MetadataPageDescriptor)

            Dim Result As New List(Of MetadataPageDescriptor)()

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                BaseStream.Position = Descriptor.Offset
                ReadExactly(BaseStream, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)

                If FixedTimeEquals(Mac, 0, Descriptor.Mac, 0, MacSize) = False Then
                    Throw New CryptographicException("Metadata directory page MAC invalid.")
                End If

                If FixedTimeEquals(DirectoryPageMagic, 0, Page, 0, DirectoryPageMagicSize) = False Then
                    Throw New InvalidDataException("Invalid metadata directory page magic.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Page, 8), DirectoryTypes)

                If DirectoryType <> ExpectedDirectoryType Then
                    Throw New InvalidDataException("Unexpected directory type while reading metadata directory pages.")
                End If

                Dim EntryCount = BitConverter.ToInt32(Page, 16)

                If EntryCount < 0 Then Throw New InvalidDataException("Invalid metadata directory page entry count.")
                If DirectoryPageHeaderSize + (EntryCount * MetadataDescriptorSize) + MacSize > Page.Length Then Throw New InvalidDataException("Metadata directory page entry area is truncated.")

                Dim EntryOffset = DirectoryPageHeaderSize

                For Index = 0 To EntryCount - 1
                    Result.Add(ReadDirectoryMetadataPageDescriptor(Page, EntryOffset))
                    EntryOffset += MetadataDescriptorSize
                Next
            Next

            Return Result

        End Function

        Private Shared Function ReadExtentPages(BaseStream As Stream,
                                                Descriptors As IEnumerable(Of MetadataPageDescriptor),
                                                ExtentCount As Integer,
                                                PageEntryCount As Integer) As List(Of ExtentIndexEntry)

            If BaseStream Is Nothing Then Throw New ArgumentNullException(NameOf(BaseStream))
            If Descriptors Is Nothing Then Throw New ArgumentNullException(NameOf(Descriptors))
            If ExtentCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ExtentCount))
            If PageEntryCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageEntryCount))

            Dim Result As New List(Of ExtentIndexEntry)(ExtentCount)

            For Index = 0 To ExtentCount - 1
                Result.Add(New ExtentIndexEntry())
            Next

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)

                Dim Page(Descriptor.Length - 1) As Byte

                BaseStream.Position = Descriptor.Offset
                ReadExactly(BaseStream, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page,
                                     Page.Length - MacSize,
                                     PublicIntegrityKey)

                If FixedTimeEquals(Mac,
                                   0,
                                   Descriptor.Mac,
                                   0,
                                   MacSize) = False Then

                    Throw New CryptographicException(
                        "Extent page MAC invalid.")

                End If

                If FixedTimeEquals(IndexPageMagic,
                                   0,
                                   Page,
                                   0,
                                   IndexPageMagicSize) = False Then

                    Throw New InvalidDataException(
                        "Invalid extent page magic.")

                End If

                Dim DirectoryType =
                    CType(BitConverter.ToInt32(Page, 8),
                          DirectoryTypes)

                If DirectoryType <> DirectoryTypes.ExtentPages Then
                    Throw New InvalidDataException(
                        "Unexpected directory type while reading extent page.")
                End If

                Dim PageNumber = BitConverter.ToInt32(Page, 12)
                Dim FirstIndex = BitConverter.ToInt32(Page, 16)
                Dim EntryCount = BitConverter.ToInt32(Page, 20)

                If PageNumber <> Descriptor.PageNumber Then
                    Throw New InvalidDataException(
                        "Extent page number mismatch.")
                End If

                If EntryCount < 0 OrElse EntryCount > PageEntryCount Then
                    Throw New InvalidDataException(
                        "Invalid extent page entry count.")
                End If

                Dim EntryOffset = IndexPageHeaderSize

                For EntryIndex = 0 To EntryCount - 1

                    Dim ExtentIndex = FirstIndex + EntryIndex

                    If ExtentIndex >= Result.Count Then Exit For

                    Result(ExtentIndex) =
                        New ExtentIndexEntry With {
                            .LogicalOffset = BitConverter.ToInt64(Page, EntryOffset),
                            .LogicalLength = BitConverter.ToInt32(Page, EntryOffset + 8),
                            .PhysicalRecordId = BitConverter.ToInt64(Page, EntryOffset + 12),
                            .PhysicalRecordOffset = BitConverter.ToInt32(Page, EntryOffset + 20),
                            .AnchorId = BitConverter.ToInt64(Page, EntryOffset + 24)
                        }

                    EntryOffset += ExtentEntrySize

                Next

            Next

            If Result.Count <> ExtentCount Then
                Throw New InvalidDataException("Loaded extent-record count does not match metadata root.")
            End If

            Return Result

        End Function

        Private Shared Function ReadPhysicalRecordPages(BaseStream As Stream,
                                                        Descriptors As IEnumerable(Of MetadataPageDescriptor),
                                                        PhysicalRecordCount As Integer,
                                                        PageEntryCount As Integer) As Dictionary(Of Long, PhysicalRecordEntry)

            If PhysicalRecordCount < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalRecordCount))
            If PageEntryCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageEntryCount))

            Dim Result As New Dictionary(Of Long, PhysicalRecordEntry)()

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                BaseStream.Position = Descriptor.Offset
                ReadExactly(BaseStream, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)

                If FixedTimeEquals(Mac, 0, Descriptor.Mac, 0, MacSize) = False Then
                    Throw New CryptographicException("Physical-record page MAC invalid.")
                End If

                If FixedTimeEquals(IndexPageMagic, 0, Page, 0, IndexPageMagicSize) = False Then
                    Throw New InvalidDataException("Invalid physical-record page magic.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Page, 8), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.PhysicalRecordPages Then
                    Throw New InvalidDataException("Unexpected directory type while reading physical-record page.")
                End If

                Dim EntryCount = BitConverter.ToInt32(Page, 20)

                If EntryCount < 0 OrElse EntryCount > PageEntryCount Then Throw New InvalidDataException("Invalid physical-record page entry count.")

                Dim EntryOffset = IndexPageHeaderSize

                For EntryIndex = 0 To EntryCount - 1
                    Dim Entry = New PhysicalRecordEntry With {
                        .RecordId = BitConverter.ToInt64(Page, EntryOffset),
                        .PhysicalOffset = BitConverter.ToInt64(Page, EntryOffset + 8),
                        .PhysicalLength = BitConverter.ToInt32(Page, EntryOffset + 16),
                        .PlainLength = BitConverter.ToInt32(Page, EntryOffset + 20),
                        .RefCount = BitConverter.ToInt32(Page, EntryOffset + 24)
                    }

                    If Entry.RecordId <= SparsePhysicalRecordId Then
                        Throw New InvalidDataException("Invalid physical record id.")
                    End If

                    Result(Entry.RecordId) = Entry
                    EntryOffset += PhysicalRecordEntrySize
                Next
            Next

            If Result.Count <> PhysicalRecordCount Then
                Throw New InvalidDataException("Loaded physical-record count does not match metadata root.")
            End If

            Return Result

        End Function

        Private Shared Function ReadHoleDirectoryPages(BaseStream As Stream,
                                                       Descriptors As IEnumerable(Of MetadataPageDescriptor)) As List(Of HoleDirectoryRecord)

            Dim Result As New List(Of HoleDirectoryRecord)()

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                BaseStream.Position = Descriptor.Offset
                ReadExactly(BaseStream, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)

                If FixedTimeEquals(Mac, 0, Descriptor.Mac, 0, MacSize) = False Then
                    Throw New CryptographicException("Hole directory page MAC invalid.")
                End If

                If FixedTimeEquals(DirectoryPageMagic, 0, Page, 0, DirectoryPageMagicSize) = False Then
                    Throw New InvalidDataException("Invalid hole directory page magic.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Page, 8), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.Holes Then
                    Throw New InvalidDataException("Unexpected directory type while reading hole directory pages.")
                End If

                Dim EntryCount = BitConverter.ToInt32(Page, 16)

                If EntryCount < 0 Then Throw New InvalidDataException("Invalid hole directory page entry count.")
                If DirectoryPageHeaderSize + (EntryCount * HoleDirectoryEntrySize) + MacSize > Page.Length Then Throw New InvalidDataException("Hole directory page entry area is truncated.")

                Dim EntryOffset = DirectoryPageHeaderSize

                For Index = 0 To EntryCount - 1
                    Dim SpaceType = CType(BitConverter.ToInt32(Page, EntryOffset), HoleSpaceTypes)
                    Dim Offset = BitConverter.ToInt64(Page, EntryOffset + 8)
                    Dim Length = BitConverter.ToInt64(Page, EntryOffset + 16)

                    If SpaceType <> HoleSpaceTypes.None AndAlso Offset >= DataStartOffset AndAlso Length > 0 Then
                        Result.Add(New HoleDirectoryRecord With {
                            .SpaceType = SpaceType,
                            .Offset = Offset,
                            .Length = Length
                        })
                    End If

                    EntryOffset += HoleDirectoryEntrySize
                Next
            Next

            Return Result

        End Function

        Private Sub MarkExtentPageDirty(ExtentIndex As Integer)

            If ExtentIndex < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ExtentIndex))
            If _IndexPageEntryCount <= 0 Then Return

            _DirtyExtentPages.Add(ExtentIndex \ _IndexPageEntryCount)

        End Sub

        Private Sub MarkPhysicalRecordPageDirtyByOrdinal(Ordinal As Integer)

            If Ordinal < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Ordinal))
            If _IndexPageEntryCount <= 0 Then Return

            _DirtyPhysicalRecordPages.Add(Ordinal \ _IndexPageEntryCount)

        End Sub

        Private Sub MarkAllMetadataPagesDirty()

            _DirtyExtentPages.Clear()
            _DirtyPhysicalRecordPages.Clear()

            Dim ExtentPageCount = GetIndexPageCount(_Extents.Count, _IndexPageEntryCount)

            For PageNumber = 0 To ExtentPageCount - 1
                _DirtyExtentPages.Add(PageNumber)
            Next

            Dim PhysicalRecordPageCount = GetIndexPageCount(_PhysicalRecords.Count, _IndexPageEntryCount)

            For PageNumber = 0 To PhysicalRecordPageCount - 1
                _DirtyPhysicalRecordPages.Add(PageNumber)
            Next

        End Sub

        Private Shared Function GetIndexPageCount(IndexCount As Integer, IndexPageEntryCount As Integer) As Integer

            If IndexCount <= 0 Then Return 0
            If IndexPageEntryCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(IndexPageEntryCount))

            Return CInt(((CLng(IndexCount) - 1L) \ CLng(IndexPageEntryCount)) + 1L)

        End Function

    End Class
End Namespace