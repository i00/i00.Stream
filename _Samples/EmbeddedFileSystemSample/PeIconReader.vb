Imports System.IO

''' <summary>
''' Reads the default application icon straight out of a portable-executable stream: parses the PE
''' headers, locates the resource section, and returns the <c>RT_GROUP_ICON</c> directory together
''' with the <c>RT_ICON</c> images it references. Nothing is written to disk and only the headers and
''' the resource section are read, not the whole executable.
''' </summary>
Friend NotInheritable Class PeIconReader

    Private Const ResourceTypeIcon As Integer = 3
    Private Const ResourceTypeGroupIcon As Integer = 14
    Private Const MaximumResourceSectionBytes As Integer = 32 * 1024 * 1024

    ''' <summary>The <c>RT_GROUP_ICON</c> directory bytes plus every <c>RT_ICON</c> image it points at, keyed by icon id.</summary>
    Friend NotInheritable Class IconGroup
        Public Sub New(Directory As Byte(), Images As Dictionary(Of Integer, Byte()))
            Me.Directory = Directory
            Me.Images = Images
        End Sub

        Public ReadOnly Property Directory As Byte()
        Public ReadOnly Property Images As Dictionary(Of Integer, Byte())
    End Class

    ''' <summary>Just enough of a section header to map a resource RVA back to a file offset.</summary>
    Private Structure SectionSpan
        Public VirtualAddress As UInteger
        Public VirtualSize As UInteger
        Public RawPointer As UInteger
        Public RawSize As UInteger
    End Structure

    Private ReadOnly _Resource As Byte()
    Private ReadOnly _Content As Stream
    Private ReadOnly _ContentLength As Long
    Private ReadOnly _Sections As List(Of SectionSpan)

    Private Sub New(Resource As Byte(), Content As Stream, ContentLength As Long, Sections As List(Of SectionSpan))
        _Resource = Resource
        _Content = Content
        _ContentLength = ContentLength
        _Sections = Sections
    End Sub

    ''' <summary>
    ''' Returns the module's default icon group, or Nothing when the stream is not a PE image, carries
    ''' no icon, or is malformed.
    ''' </summary>
    Public Shared Function ReadDefaultIconGroup(Content As Stream, Length As Long) As IconGroup
        If Content Is Nothing OrElse Content.CanSeek = False OrElse Length < 512 Then Return Nothing

        Try
            Dim Headers = ReadBlock(Content, 0, CInt(Math.Min(Length, 8192L)))
            If Headers.Length < 64 OrElse ReadUInt16(Headers, 0) <> &H5A4D Then Return Nothing   ' 'MZ'

            Dim PeOffset = CInt(ReadUInt32(Headers, &H3C))
            If PeOffset <= 0 Then Return Nothing
            If PeOffset + 248 > Headers.Length Then
                Headers = ReadBlock(Content, 0, CInt(Math.Min(Length, 65536L)))
                If PeOffset + 24 > Headers.Length Then Return Nothing
            End If
            If ReadUInt32(Headers, PeOffset) <> &H4550UI Then Return Nothing   ' 'PE\0\0'

            Dim SectionCount = ReadUInt16(Headers, PeOffset + 6)
            Dim OptionalHeaderSize = ReadUInt16(Headers, PeOffset + 20)
            Dim OptionalHeaderOffset = PeOffset + 24
            If OptionalHeaderOffset + 2 > Headers.Length Then Return Nothing
            Dim Magic = ReadUInt16(Headers, OptionalHeaderOffset)
            Dim ResourceDirectoryEntryOffset = OptionalHeaderOffset + If(Magic = &H20B, 112, 96) + (2 * 8)

            Dim SectionTableOffset = OptionalHeaderOffset + OptionalHeaderSize
            Dim SectionTableEnd = SectionTableOffset + (SectionCount * 40)
            If SectionTableEnd > Headers.Length Then
                Headers = ReadBlock(Content, 0, CInt(Math.Min(Length, CLng(SectionTableEnd + 4))))
            End If
            If ResourceDirectoryEntryOffset + 8 > Headers.Length OrElse SectionTableEnd > Headers.Length Then Return Nothing

            Dim ResourceRva = ReadUInt32(Headers, ResourceDirectoryEntryOffset)
            Dim ResourceSize = ReadUInt32(Headers, ResourceDirectoryEntryOffset + 4)
            If ResourceRva = 0UI OrElse ResourceSize = 0UI Then Return Nothing

            Dim Sections As New List(Of SectionSpan)(SectionCount)
            For Index = 0 To SectionCount - 1
                Dim SectionOffset = SectionTableOffset + (Index * 40)
                Sections.Add(New SectionSpan With {
                    .VirtualSize = ReadUInt32(Headers, SectionOffset + 8),
                    .VirtualAddress = ReadUInt32(Headers, SectionOffset + 12),
                    .RawSize = ReadUInt32(Headers, SectionOffset + 16),
                    .RawPointer = ReadUInt32(Headers, SectionOffset + 20)
                })
            Next

            ' The resource directory tree itself lives in the section named by the data directory; a
            ' packed executable (ASPack, ...) keeps the tree there but relocates the leaf data - icons,
            ' the group directory - into another section, which ReadResourceData resolves per RVA.
            For Each Section In Sections
                Dim Span = Math.Max(Section.VirtualSize, Section.RawSize)
                If ResourceRva < Section.VirtualAddress OrElse CLng(ResourceRva) >= CLng(Section.VirtualAddress) + Span Then Continue For

                Dim Available = CInt(Math.Min(Math.Min(CLng(Section.RawSize), CLng(MaximumResourceSectionBytes)), Length - Section.RawPointer))
                If Available <= 16 Then Return Nothing
                Dim Resource = ReadBlock(Content, Section.RawPointer, Available)
                Return New PeIconReader(Resource, Content, Length, Sections).ExtractDefaultGroup()
            Next
            Return Nothing
        Catch
            Return Nothing
        End Try
    End Function

    Private Function ExtractDefaultGroup() As IconGroup
        Dim GroupTypeDirectory = FindTypeDirectory(ResourceTypeGroupIcon)
        Dim IconTypeDirectory = FindTypeDirectory(ResourceTypeIcon)
        If GroupTypeDirectory < 0 OrElse IconTypeDirectory < 0 Then Return Nothing

        Dim GroupLanguageDirectory = FirstEntrySubdirectory(GroupTypeDirectory)
        Dim GroupData = FirstEntryLeafData(GroupLanguageDirectory)
        If GroupData Is Nothing OrElse GroupData.Length < 6 Then Return Nothing

        Dim Count = ReadUInt16(GroupData, 4)
        If 6 + (Count * 14) > GroupData.Length Then Return Nothing

        Dim Images As New Dictionary(Of Integer, Byte())()
        For Index = 0 To Count - 1
            Dim IconId = ReadUInt16(GroupData, 6 + (Index * 14) + 12)
            Dim LanguageDirectory = FindIdEntrySubdirectory(IconTypeDirectory, IconId)
            Dim ImageBytes = FirstEntryLeafData(LanguageDirectory)
            If ImageBytes IsNot Nothing Then Images(IconId) = ImageBytes
        Next
        If Images.Count = 0 Then Return Nothing
        Return New IconGroup(GroupData, Images)
    End Function

    Private Function FindTypeDirectory(TypeId As Integer) As Integer
        Return FindIdEntrySubdirectory(0, TypeId)
    End Function

    Private Function FindIdEntrySubdirectory(DirectoryOffset As Integer, Id As Integer) As Integer
        If DirectoryOffset < 0 OrElse DirectoryOffset + 16 > _Resource.Length Then Return -1
        Dim NamedEntries = ReadUInt16(_Resource, DirectoryOffset + 12)
        Dim IdEntries = ReadUInt16(_Resource, DirectoryOffset + 14)
        Dim EntriesStart = DirectoryOffset + 16

        For Index = NamedEntries To NamedEntries + IdEntries - 1
            Dim EntryOffset = EntriesStart + (Index * 8)
            If EntryOffset + 8 > _Resource.Length Then Return -1
            Dim NameOrId = ReadUInt32(_Resource, EntryOffset)
            If (NameOrId And &H80000000UI) = 0UI AndAlso CInt(NameOrId) = Id Then Return SubdirectoryOffset(EntryOffset)
        Next
        Return -1
    End Function

    Private Function FirstEntrySubdirectory(DirectoryOffset As Integer) As Integer
        If DirectoryOffset < 0 OrElse DirectoryOffset + 24 > _Resource.Length Then Return -1
        If ReadUInt16(_Resource, DirectoryOffset + 12) + ReadUInt16(_Resource, DirectoryOffset + 14) = 0 Then Return -1
        Return SubdirectoryOffset(DirectoryOffset + 16)
    End Function

    Private Function SubdirectoryOffset(EntryOffset As Integer) As Integer
        Dim OffsetToData = ReadUInt32(_Resource, EntryOffset + 4)
        If (OffsetToData And &H80000000UI) = 0UI Then Return -1
        Return CInt(OffsetToData And &H7FFFFFFFUI)
    End Function

    Private Function FirstEntryLeafData(DirectoryOffset As Integer) As Byte()
        If DirectoryOffset < 0 OrElse DirectoryOffset + 24 > _Resource.Length Then Return Nothing
        If ReadUInt16(_Resource, DirectoryOffset + 12) + ReadUInt16(_Resource, DirectoryOffset + 14) = 0 Then Return Nothing

        Dim EntryOffset = DirectoryOffset + 16
        Dim OffsetToData = ReadUInt32(_Resource, EntryOffset + 4)
        If (OffsetToData And &H80000000UI) <> 0UI Then Return FirstEntryLeafData(CInt(OffsetToData And &H7FFFFFFFUI))

        Dim DataEntryOffset = CInt(OffsetToData)
        If DataEntryOffset < 0 OrElse DataEntryOffset + 16 > _Resource.Length Then Return Nothing
        Return ReadResourceData(ReadUInt32(_Resource, DataEntryOffset), CLng(ReadUInt32(_Resource, DataEntryOffset + 4)))
    End Function

    ''' <summary>
    ''' Reads a resource leaf's bytes given its RVA, mapping the RVA to a file offset through whichever
    ''' section actually contains it - not assuming it sits inside the resource directory's own section.
    ''' </summary>
    Private Function ReadResourceData(DataRva As UInteger, DataSize As Long) As Byte()
        If DataSize <= 0 OrElse DataSize > MaximumResourceSectionBytes Then Return Nothing

        For Each Section In _Sections
            Dim SectionEnd = CLng(Section.VirtualAddress) + Math.Max(Section.VirtualSize, Section.RawSize)
            If DataRva < Section.VirtualAddress OrElse CLng(DataRva) >= SectionEnd Then Continue For

            Dim Delta = CLng(DataRva) - CLng(Section.VirtualAddress)
            If Delta >= Section.RawSize Then Return Nothing   ' lives only in the section's uninitialised tail
            Dim FileOffset = CLng(Section.RawPointer) + Delta
            If FileOffset < 0 OrElse FileOffset + DataSize > _ContentLength Then Return Nothing

            Dim Result = ReadBlock(_Content, FileOffset, CInt(DataSize))
            Return If(Result.Length = CInt(DataSize), Result, Nothing)
        Next
        Return Nothing
    End Function

    Private Shared Function ReadBlock(Content As Stream, Position As Long, Count As Integer) As Byte()
        Content.Position = Position
        Dim Buffer(Count - 1) As Byte
        Dim Total = 0
        While Total < Count
            Dim ThisRead = Content.Read(Buffer, Total, Count - Total)
            If ThisRead = 0 Then Exit While
            Total += ThisRead
        End While
        If Total = Count Then Return Buffer

        Dim Trimmed(Total - 1) As Byte
        System.Buffer.BlockCopy(Buffer, 0, Trimmed, 0, Total)
        Return Trimmed
    End Function

    Private Shared Function ReadUInt16(Data As Byte(), Offset As Integer) As Integer
        Return CInt(Data(Offset)) Or (CInt(Data(Offset + 1)) << 8)
    End Function

    Private Shared Function ReadUInt32(Data As Byte(), Offset As Integer) As UInteger
        Return CUInt(Data(Offset)) Or
               (CUInt(Data(Offset + 1)) << 8) Or
               (CUInt(Data(Offset + 2)) << 16) Or
               (CUInt(Data(Offset + 3)) << 24)
    End Function
End Class
