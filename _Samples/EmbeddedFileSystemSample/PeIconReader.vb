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

    Private ReadOnly _Resource As Byte()
    Private ReadOnly _ResourceRva As UInteger

    Private Sub New(Resource As Byte(), ResourceRva As UInteger)
        _Resource = Resource
        _ResourceRva = ResourceRva
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

            For Index = 0 To SectionCount - 1
                Dim SectionOffset = SectionTableOffset + (Index * 40)
                Dim VirtualAddress = ReadUInt32(Headers, SectionOffset + 12)
                Dim VirtualSize = ReadUInt32(Headers, SectionOffset + 8)
                Dim RawPointer = ReadUInt32(Headers, SectionOffset + 20)
                Dim RawSize = ReadUInt32(Headers, SectionOffset + 16)
                Dim SectionSpan = Math.Max(VirtualSize, RawSize)
                If ResourceRva < VirtualAddress OrElse CLng(ResourceRva) >= CLng(VirtualAddress) + SectionSpan Then Continue For

                Dim Available = CInt(Math.Min(Math.Min(CLng(RawSize), CLng(MaximumResourceSectionBytes)), Length - RawPointer))
                If Available <= 16 Then Return Nothing
                Dim Resource = ReadBlock(Content, RawPointer, Available)
                Return New PeIconReader(Resource, VirtualAddress).ExtractDefaultGroup()
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
        Dim DataRva = ReadUInt32(_Resource, DataEntryOffset)
        Dim DataSize = CLng(ReadUInt32(_Resource, DataEntryOffset + 4))
        Dim Start = CLng(DataRva) - CLng(_ResourceRva)
        If Start < 0 OrElse DataSize <= 0 OrElse Start + DataSize > _Resource.Length Then Return Nothing

        Dim Result(CInt(DataSize) - 1) As Byte
        System.Buffer.BlockCopy(_Resource, CInt(Start), Result, 0, CInt(DataSize))
        Return Result
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
