Imports System.IO
Imports System.Security.Cryptography

Namespace Streams

    Partial Class ChunkedStream

        Private Shared Function ComputeMac(Buffer As Byte(), Count As Integer, MacKey As Byte()) As Byte()
            Using Hmac As New HMACSHA256(MacKey)
                Return Hmac.ComputeHash(Buffer, 0, Count)
            End Using
        End Function

        Private Shared Function ComputeMac(Buffer As Byte(), MacKey As Byte()) As Byte()
            Return ComputeMac(Buffer, Buffer.Length, MacKey)
        End Function

        Private Function GetNextIndexPageWriteOffset(Length As Integer) As Long
            If Length <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Select Case Options.NewIndexPageWriteLocationPolicy
                Case ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles,
                     ChunkedStreamOptions.NewWriteLocationPolicies.FillHolesFromStart

                    Dim Offset As Long

                    If _FreeIndexPageSpaces.TryAllocate(Length, Offset) Then
                        Return Offset
                    End If
            End Select

            Return Math.Max(_Fs.Length, GetDataEndFromIndex())
        End Function

        Private Function GetNextIndexDirectoryPageWriteOffset(Length As Integer) As Long
            If Length <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

            Select Case Options.NewIndexDirectoryPageWriteLocationPolicy
                Case ChunkedStreamOptions.NewWriteLocationPolicies.FillHoles,
                     ChunkedStreamOptions.NewWriteLocationPolicies.FillHolesFromStart

                    Dim Offset As Long

                    If _FreeIndexDirectoryPageSpaces.TryAllocate(Length, Offset) Then
                        Return Offset
                    End If
            End Select

            Return Math.Max(_Fs.Length, GetDataEndFromIndex())
        End Function

        Private Function BuildIndexPage(PageNumber As Integer) As Byte()
            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If _IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid index page entry count.")

            Dim PageLengthWithoutMac = IndexPageHeaderSize + (_IndexPageEntryCount * IndexEntrySize)
            Dim Page(PageLengthWithoutMac + MacSize - 1) As Byte

            Dim FirstChunkIndex = PageNumber * _IndexPageEntryCount
            Dim EntryCount = Math.Max(0, Math.Min(_IndexPageEntryCount, _Index.Count - FirstChunkIndex))

            Buffer.BlockCopy(IndexPageMagic, 0, Page, 0, IndexPageMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryTypes.ChunkIndexPages)), 0, Page, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(PageNumber), 0, Page, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(FirstChunkIndex), 0, Page, 16, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(EntryCount), 0, Page, 20, 4)

            Dim EntryOffset = IndexPageHeaderSize

            For EntryIndex = 0 To _IndexPageEntryCount - 1
                Dim ChunkIndex = FirstChunkIndex + EntryIndex
                Dim Entry = If(ChunkIndex < _Index.Count, _Index(ChunkIndex), New ChunkIndexEntry())

                Buffer.BlockCopy(BitConverter.GetBytes(Entry.Offset), 0, Page, EntryOffset, 8)
                Buffer.BlockCopy(BitConverter.GetBytes(Entry.RecordLength), 0, Page, EntryOffset + 8, 4)

                EntryOffset += IndexEntrySize
            Next

            Dim Mac = ComputeMac(Page, PageLengthWithoutMac, PublicIntegrityKey)

            Buffer.BlockCopy(Mac, 0, Page, PageLengthWithoutMac, MacSize)

            Return Page
        End Function

        Private Function WriteIndexPage(PageNumber As Integer) As MetadataPageDescriptor
            Dim Page = BuildIndexPage(PageNumber)
            Dim Offset = GetNextIndexPageWriteOffset(Page.Length)

            Dim OldDescriptor As MetadataPageDescriptor = Nothing
            Dim HadOldDescriptor = _IndexPageDescriptors.TryGetValue(PageNumber, OldDescriptor)

            _Fs.Position = Offset
            _Fs.Write(Page, 0, Page.Length)

            Dim Descriptor = New MetadataPageDescriptor With {
                .PageNumber = PageNumber,
                .Offset = Offset,
                .Length = Page.Length,
                .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
            }

            If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                AddFreeIndexPageSpace(OldDescriptor.Offset, OldDescriptor.Length)
            End If

            _IndexPageDescriptors(PageNumber) = Descriptor

            Return Descriptor
        End Function

        Private Function BuildDirectoryPage(DirectoryType As DirectoryTypes,
                                            PageNumber As Integer,
                                            Entries As IList(Of MetadataPageDescriptor)) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If Entries Is Nothing Then Throw New ArgumentNullException(NameOf(Entries))
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid index directory entry count.")

            Dim EntrySize = MetadataDescriptorSize
            Dim PageLengthWithoutMac = DirectoryPageHeaderSize + (_IndexDirectoryEntryCount * EntrySize)
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

                EntryOffset += EntrySize
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
            Dim DirectoryPageCount = If(DescriptorList.Count = 0,
                                        0,
                                        CInt(((DescriptorList.Count - 1) \ _IndexDirectoryEntryCount) + 1))

            Dim Result As New Dictionary(Of Integer, MetadataPageDescriptor)()

            For PageNumber = 0 To DirectoryPageCount - 1
                Dim Page = BuildDirectoryPage(DirectoryType, PageNumber, DescriptorList)
                Dim Offset = GetNextIndexDirectoryPageWriteOffset(Page.Length)

                Dim OldDescriptor As MetadataPageDescriptor = Nothing
                Dim HadOldDescriptor = ExistingDirectoryDescriptors.TryGetValue(PageNumber, OldDescriptor)

                _Fs.Position = Offset
                _Fs.Write(Page, 0, Page.Length)

                Dim NewDescriptor = New MetadataPageDescriptor With {
                    .PageNumber = PageNumber,
                    .Offset = Offset,
                    .Length = Page.Length,
                    .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
                }

                If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                    AddFreeIndexDirectoryPageSpace(OldDescriptor.Offset, OldDescriptor.Length)
                End If

                Result(PageNumber) = NewDescriptor
            Next

            Return Result
        End Function

        Private Function BuildHoleDirectoryPage(PageNumber As Integer,
                                                Records As IList(Of HoleDirectoryRecord)) As Byte()

            If PageNumber < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PageNumber))
            If Records Is Nothing Then Throw New ArgumentNullException(NameOf(Records))
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid index directory entry count.")

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

            Dim DirectoryPageCount = CInt(((Records.Count - 1) \ _IndexDirectoryEntryCount) + 1)

            For PageNumber = 0 To DirectoryPageCount - 1
                Dim Page = BuildHoleDirectoryPage(PageNumber, Records)
                Dim Offset = GetNextIndexDirectoryPageWriteOffset(Page.Length)

                Dim OldDescriptor As MetadataPageDescriptor = Nothing
                Dim HadOldDescriptor = _HoleDirectoryPageDescriptors.TryGetValue(PageNumber, OldDescriptor)

                _Fs.Position = Offset
                _Fs.Write(Page, 0, Page.Length)

                Dim NewDescriptor = New MetadataPageDescriptor With {
                    .PageNumber = PageNumber,
                    .Offset = Offset,
                    .Length = Page.Length,
                    .Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)
                }

                If HadOldDescriptor AndAlso OldDescriptor.Offset > 0 AndAlso OldDescriptor.Length > 0 Then
                    AddFreeIndexDirectoryPageSpace(OldDescriptor.Offset, OldDescriptor.Length)
                End If

                Result(PageNumber) = NewDescriptor
            Next

            Return Result
        End Function

        Private Function BuildMetadataRoot(ChunkIndexDirectoryDescriptors As IEnumerable(Of MetadataPageDescriptor),
                                           HoleDirectoryDescriptors As IEnumerable(Of MetadataPageDescriptor)) As Byte()

            Dim ChunkDirectoryList = If(ChunkIndexDirectoryDescriptors, Enumerable.Empty(Of MetadataPageDescriptor)()).
                                     OrderBy(Function(descriptor) descriptor.PageNumber).
                                     ToList()

            Dim HoleDirectoryList = If(HoleDirectoryDescriptors, Enumerable.Empty(Of MetadataPageDescriptor)()).
                                    OrderBy(Function(descriptor) descriptor.PageNumber).
                                    ToList()

            Dim DescriptorCount = ChunkDirectoryList.Count + HoleDirectoryList.Count
            Dim RootLengthWithoutMac = MetadataRootHeaderSize + (DescriptorCount * MetadataDescriptorSize)
            Dim Root(RootLengthWithoutMac + MacSize - 1) As Byte

            Buffer.BlockCopy(MetadataRootMagic, 0, Root, 0, MetadataRootMagic.Length)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexPageEntryCount), 0, Root, 8, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(_IndexDirectoryEntryCount), 0, Root, 12, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(CLng(_Index.Count)), 0, Root, 16, 8)
            Buffer.BlockCopy(BitConverter.GetBytes(ChunkDirectoryList.Count), 0, Root, 24, 4)
            Buffer.BlockCopy(BitConverter.GetBytes(HoleDirectoryList.Count), 0, Root, 28, 4)

            Dim EntryOffset = MetadataRootHeaderSize

            For Each Descriptor In ChunkDirectoryList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.ChunkIndexPages, Descriptor)
                EntryOffset += MetadataDescriptorSize
            Next

            For Each Descriptor In HoleDirectoryList
                WriteMetadataRootDescriptor(Root, EntryOffset, DirectoryTypes.Holes, Descriptor)
                EntryOffset += MetadataDescriptorSize
            Next

            Dim Mac = ComputeMac(Root, RootLengthWithoutMac, PublicIntegrityKey)

            Buffer.BlockCopy(Mac, 0, Root, RootLengthWithoutMac, MacSize)

            Return Root
        End Function

        Private Shared Sub WriteMetadataRootDescriptor(Buffer As Byte(),
                                                       Offset As Integer,
                                                       DirectoryType As DirectoryTypes,
                                                       Descriptor As MetadataPageDescriptor)

            System.Buffer.BlockCopy(BitConverter.GetBytes(CInt(DirectoryType)), 0, Buffer, Offset, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.PageNumber), 0, Buffer, Offset + 4, 4)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.Offset), 0, Buffer, Offset + 8, 8)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Descriptor.Length), 0, Buffer, Offset + 16, 4)

            If Descriptor.Mac IsNot Nothing Then
                System.Buffer.BlockCopy(Descriptor.Mac, 0, Buffer, Offset + 20, Math.Min(MacSize, Descriptor.Mac.Length))
            End If
        End Sub

        Private Sub PersistPagedMetadata(IndexOffset As Long, Durable As Boolean)
            If IndexOffset < DataStartOffset Then Throw New InvalidDataException("Invalid index offset.")

            _IndexOffset = IndexOffset

            If _IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid index page entry count.")
            If _IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid index directory entry count.")

            Dim RequiredIndexPageCount = GetIndexPageCount(_Index.Count, _IndexPageEntryCount)

            For Each PageNumber In _IndexPageDescriptors.Keys.Where(Function(x) x >= RequiredIndexPageCount).ToList()
                Dim Descriptor = _IndexPageDescriptors(PageNumber)
                AddFreeIndexPageSpace(Descriptor.Offset, Descriptor.Length)
                _IndexPageDescriptors.Remove(PageNumber)
            Next

            For Each PageNumber In _DirtyIndexPages.ToList()
                If PageNumber < RequiredIndexPageCount Then
                    WriteIndexPage(PageNumber)
                End If
            Next

            _DirtyIndexPages.Clear()

            Dim NewChunkDirectoryDescriptors =
                WriteDirectoryPages(DirectoryTypes.ChunkIndexPages,
                                    _IndexPageDescriptors.Values,
                                    _ChunkIndexDirectoryPageDescriptors)

            _ChunkIndexDirectoryPageDescriptors.Clear()

            For Each pair In NewChunkDirectoryDescriptors
                _ChunkIndexDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            Dim NewHoleDirectoryDescriptors As New Dictionary(Of Integer, MetadataPageDescriptor)()

            If Options.StoreHoleDirectory Then
                NewHoleDirectoryDescriptors = WriteHoleDirectoryPages(GetKnownHoleRecords())
            End If

            _HoleDirectoryPageDescriptors.Clear()

            For Each pair In NewHoleDirectoryDescriptors
                _HoleDirectoryPageDescriptors(pair.Key) = pair.Value
            Next

            Dim Root = BuildMetadataRoot(_ChunkIndexDirectoryPageDescriptors.Values,
                                         _HoleDirectoryPageDescriptors.Values)

            Dim OldRootOffset = _MetadataRootOffset
            Dim OldRootLength = _MetadataRootLength

            _MetadataRootOffset = Math.Max(_Fs.Length, GetDataEndFromIndex())
            _MetadataRootLength = Root.Length

            _Fs.Position = _MetadataRootOffset
            _Fs.Write(Root, 0, Root.Length)

            If OldRootOffset > 0 AndAlso OldRootLength > 0 Then
                AddFreeIndexDirectoryPageSpace(OldRootOffset, OldRootLength)
            End If

            If Durable Then FlushDurable(_Fs)

            UpdateHeader(Durable)

            If Durable Then FlushDurable(_Fs)
        End Sub

        Private Shared Function ReadMetadataPageDescriptor(Buffer As Byte(), Offset As Integer) As MetadataPageDescriptor
            Dim Mac(MacSize - 1) As Byte

            System.Buffer.BlockCopy(Buffer, Offset + 20, Mac, 0, Mac.Length)

            Return New MetadataPageDescriptor With {
                .PageNumber = BitConverter.ToInt32(Buffer, Offset + 4),
                .Offset = BitConverter.ToInt64(Buffer, Offset + 8),
                .Length = BitConverter.ToInt32(Buffer, Offset + 16),
                .Mac = Mac
            }
        End Function

        Private Shared Function ReadMetadataRoot(Fs As Stream,
                                                 RootOffset As Long,
                                                 RootLength As Integer,
                                                 ExpectedMac As Byte()) As Tuple(Of Integer, Integer, Integer, List(Of MetadataPageDescriptor), List(Of MetadataPageDescriptor))

            If RootLength = 0 Then
                Return Tuple.Create(256,
                                    256,
                                    0,
                                    New List(Of MetadataPageDescriptor)(),
                                    New List(Of MetadataPageDescriptor)())
            End If

            If RootOffset < DataStartOffset Then Throw New InvalidDataException("Invalid metadata root offset.")
            If RootLength < MetadataRootHeaderSize + MacSize Then Throw New InvalidDataException("Invalid metadata root length.")
            If RootOffset + RootLength > Fs.Length Then Throw New InvalidDataException("Metadata root extends beyond end of stream.")

            Dim Root(RootLength - 1) As Byte

            Fs.Position = RootOffset
            ReadExactly(Fs, Root, 0, Root.Length)

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

            Dim IndexPageEntryCount = BitConverter.ToInt32(Root, 8)
            Dim IndexDirectoryEntryCount = BitConverter.ToInt32(Root, 12)
            Dim IndexCount = CInt(BitConverter.ToInt64(Root, 16))
            Dim ChunkDirectoryPageCount = BitConverter.ToInt32(Root, 24)
            Dim HoleDirectoryPageCount = BitConverter.ToInt32(Root, 28)

            If IndexPageEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata index page entry count.")
            If IndexDirectoryEntryCount <= 0 Then Throw New InvalidDataException("Invalid metadata directory entry count.")
            If IndexCount < 0 Then Throw New InvalidDataException("Invalid metadata index count.")

            Dim ChunkDirectoryDescriptors As New List(Of MetadataPageDescriptor)()
            Dim HoleDirectoryDescriptors As New List(Of MetadataPageDescriptor)()

            Dim EntryOffset = MetadataRootHeaderSize

            For Index = 0 To ChunkDirectoryPageCount - 1
                Dim DirectoryType = CType(BitConverter.ToInt32(Root, EntryOffset), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.ChunkIndexPages Then
                    Throw New InvalidDataException("Metadata root contains an unexpected chunk-index directory descriptor.")
                End If

                ChunkDirectoryDescriptors.Add(ReadMetadataPageDescriptor(Root, EntryOffset))
                EntryOffset += MetadataDescriptorSize
            Next

            For Index = 0 To HoleDirectoryPageCount - 1
                Dim DirectoryType = CType(BitConverter.ToInt32(Root, EntryOffset), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.Holes Then
                    Throw New InvalidDataException("Metadata root contains an unexpected hole directory descriptor.")
                End If

                HoleDirectoryDescriptors.Add(ReadMetadataPageDescriptor(Root, EntryOffset))
                EntryOffset += MetadataDescriptorSize
            Next

            Return Tuple.Create(IndexPageEntryCount,
                                IndexDirectoryEntryCount,
                                IndexCount,
                                ChunkDirectoryDescriptors,
                                HoleDirectoryDescriptors)
        End Function

        Private Shared Function ReadChunkIndexDirectoryPages(Fs As Stream,
                                                            Descriptors As IEnumerable(Of MetadataPageDescriptor)) As List(Of MetadataPageDescriptor)

            Dim Result As New List(Of MetadataPageDescriptor)()

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                Fs.Position = Descriptor.Offset
                ReadExactly(Fs, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)

                If FixedTimeEquals(Mac, 0, Descriptor.Mac, 0, MacSize) = False Then
                    Throw New CryptographicException("Chunk-index directory page MAC invalid.")
                End If

                If FixedTimeEquals(DirectoryPageMagic, 0, Page, 0, DirectoryPageMagicSize) = False Then
                    Throw New InvalidDataException("Invalid chunk-index directory page magic.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Page, 8), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.ChunkIndexPages Then
                    Throw New InvalidDataException("Unexpected directory type while reading chunk-index directory pages.")
                End If

                Dim EntryCount = BitConverter.ToInt32(Page, 16)
                Dim EntryOffset = DirectoryPageHeaderSize

                For Index = 0 To EntryCount - 1
                    Result.Add(ReadMetadataPageDescriptor(Page, EntryOffset))
                    EntryOffset += MetadataDescriptorSize
                Next
            Next

            Return Result
        End Function

        Private Shared Function ReadPagedIndexTable(Fs As Stream,
                                                    RootOffset As Long,
                                                    RootLength As Integer,
                                                    RootMac As Byte(),
                                                    ByRef IndexPageEntryCount As Integer,
                                                    ByRef IndexDirectoryEntryCount As Integer,
                                                    ByRef ChunkDirectoryDescriptors As Dictionary(Of Integer, MetadataPageDescriptor),
                                                    ByRef HoleDirectoryDescriptors As Dictionary(Of Integer, MetadataPageDescriptor),
                                                    ByRef IndexPageDescriptors As Dictionary(Of Integer, MetadataPageDescriptor),
                                                    ByRef HoleRecords As List(Of HoleDirectoryRecord)) As List(Of ChunkIndexEntry)

            Dim Root = ReadMetadataRoot(Fs, RootOffset, RootLength, RootMac)

            IndexPageEntryCount = Root.Item1
            IndexDirectoryEntryCount = Root.Item2

            Dim IndexCount = Root.Item3
            Dim ChunkDirectoryList = Root.Item4
            Dim HoleDirectoryList = Root.Item5

            ChunkDirectoryDescriptors = ChunkDirectoryList.ToDictionary(Function(descriptor) descriptor.PageNumber)
            HoleDirectoryDescriptors = HoleDirectoryList.ToDictionary(Function(descriptor) descriptor.PageNumber)

            Dim PageDescriptors = ReadChunkIndexDirectoryPages(Fs, ChunkDirectoryList)

            IndexPageDescriptors = PageDescriptors.ToDictionary(Function(descriptor) descriptor.PageNumber)

            Dim Index As New List(Of ChunkIndexEntry)(IndexCount)

            For IndexNumber = 0 To IndexCount - 1
                Index.Add(New ChunkIndexEntry())
            Next

            For Each Descriptor In PageDescriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                Fs.Position = Descriptor.Offset
                ReadExactly(Fs, Page, 0, Page.Length)

                Dim Mac = ComputeMac(Page, Page.Length - MacSize, PublicIntegrityKey)

                If FixedTimeEquals(Mac, 0, Descriptor.Mac, 0, MacSize) = False Then
                    Throw New CryptographicException("Index page MAC invalid.")
                End If

                If FixedTimeEquals(IndexPageMagic, 0, Page, 0, IndexPageMagicSize) = False Then
                    Throw New InvalidDataException("Invalid index page magic.")
                End If

                Dim DirectoryType = CType(BitConverter.ToInt32(Page, 8), DirectoryTypes)

                If DirectoryType <> DirectoryTypes.ChunkIndexPages Then
                    Throw New InvalidDataException("Unexpected directory type while reading index page.")
                End If

                Dim PageNumber = BitConverter.ToInt32(Page, 12)
                Dim FirstChunkIndex = BitConverter.ToInt32(Page, 16)
                Dim EntryCount = BitConverter.ToInt32(Page, 20)

                If PageNumber <> Descriptor.PageNumber Then
                    Throw New InvalidDataException("Index page number mismatch.")
                End If

                Dim EntryOffset = IndexPageHeaderSize

                For EntryIndex = 0 To EntryCount - 1
                    Dim ChunkIndex = FirstChunkIndex + EntryIndex

                    If ChunkIndex >= Index.Count Then Exit For

                    Dim Entry = New ChunkIndexEntry With {
                        .Offset = BitConverter.ToInt64(Page, EntryOffset),
                        .RecordLength = BitConverter.ToInt32(Page, EntryOffset + 8)
                    }

                    If Entry.Offset <> 0 OrElse Entry.RecordLength <> 0 Then
                        If Entry.Offset < DataStartOffset OrElse Entry.RecordLength < MinChunkRecordSize Then
                            Throw New InvalidDataException($"Invalid index entry {ChunkIndex}.")
                        End If
                    End If

                    Index(ChunkIndex) = Entry
                    EntryOffset += IndexEntrySize
                Next
            Next

            HoleRecords = ReadHoleDirectoryPages(Fs, HoleDirectoryList)

            Return Index
        End Function

        Private Shared Function ReadHoleDirectoryPages(Fs As Stream,
                                                       Descriptors As IEnumerable(Of MetadataPageDescriptor)) As List(Of HoleDirectoryRecord)

            Dim Result As New List(Of HoleDirectoryRecord)()

            For Each Descriptor In Descriptors.OrderBy(Function(x) x.PageNumber)
                Dim Page(Descriptor.Length - 1) As Byte

                Fs.Position = Descriptor.Offset
                ReadExactly(Fs, Page, 0, Page.Length)

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

    End Class

End Namespace