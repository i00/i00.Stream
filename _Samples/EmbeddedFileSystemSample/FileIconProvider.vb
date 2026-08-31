Imports System.Drawing
Imports System.IO
Imports System.Runtime.InteropServices

''' <summary>
''' Supplies Windows shell icons for the folder tree and file list and owns the shared
''' <see cref="Windows.Forms.ImageList"/> instances they draw from - a 16x16 list for the tree and the
''' small-icon/details views and a 32x32 list for the large-icon and tile views. All icons are 32-bit
''' with a full alpha channel.
''' </summary>
''' <remarks>
''' Extension and folder icons are resolved with <c>SHGFI_USEFILEATTRIBUTES</c>, so no file need exist
''' on disk. An executable's own icon is read straight out of its bytes by <see cref="PeIconReader"/>
''' and rendered by the shell's own <c>CreateIconFromResourceEx</c> - no temporary file is written.
''' Every member that touches an image list must be called on the UI thread;
''' <see cref="ExtractExecutableIcons"/> is the only member safe to call from a worker thread.
''' </remarks>
Friend NotInheritable Class FileIconProvider
    Implements IDisposable

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Auto)>
    Private Structure SHFILEINFO
        Public hIcon As IntPtr
        Public iIcon As Integer
        Public dwAttributes As Integer
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
        Public szDisplayName As String
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=80)>
        Public szTypeName As String
    End Structure

    <DllImport("shell32.dll", CharSet:=CharSet.Auto)>
    Private Shared Function SHGetFileInfo(Path As String, FileAttributes As Integer, ByRef FileInfo As SHFILEINFO,
                                         FileInfoSize As Integer, Flags As Integer) As IntPtr
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function DestroyIcon(Icon As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function LookupIconIdFromDirectoryEx(ResourceBits As Byte(), IsIcon As Boolean,
                                                       DesiredWidth As Integer, DesiredHeight As Integer,
                                                       Flags As UInteger) As Integer
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function CreateIconFromResourceEx(ResourceBits As Byte(), ResourceSize As UInteger, IsIcon As Boolean,
                                                    Version As UInteger, DesiredWidth As Integer, DesiredHeight As Integer,
                                                    Flags As UInteger) As IntPtr
    End Function

    Private Const ShgfiIcon As Integer = 1 << 8
    Private Const ShgfiLargeIcon As Integer = 0
    Private Const ShgfiSmallIcon As Integer = 1 << 0
    Private Const ShgfiUseFileAttributes As Integer = 1 << 4
    Private Const FileAttributeNormal As Integer = 1 << 7
    Private Const FileAttributeDirectory As Integer = 1 << 4

    Private Const LoadResourceDefaultColour As UInteger = 0
    Private Const IconResourceVersion As UInteger = &H30000UI

    Public Const SmallIconSize As Integer = 16
    Public Const LargeIconSize As Integer = 32

    Public Const FolderKey As String = ":folder:"
    Public Const GenericFileKey As String = ":file:"

    ''' <summary>An executable's own icon rendered at both list sizes. Either size may be Nothing.</summary>
    Friend NotInheritable Class ExecutableIcons
        Implements IDisposable

        Public Sub New(SmallIcon As Icon, LargeIcon As Icon)
            Me.SmallIcon = SmallIcon
            Me.LargeIcon = LargeIcon
        End Sub

        Public ReadOnly Property SmallIcon As Icon
        Public ReadOnly Property LargeIcon As Icon

        Public Sub Dispose() Implements IDisposable.Dispose
            SmallIcon?.Dispose()
            LargeIcon?.Dispose()
        End Sub
    End Class

    Private ReadOnly _SmallImages As New ImageList() With {
        .ColorDepth = ColorDepth.Depth32Bit,
        .ImageSize = New Size(SmallIconSize, SmallIconSize),
        .TransparentColor = Color.Transparent
    }
    Private ReadOnly _LargeImages As New ImageList() With {
        .ColorDepth = ColorDepth.Depth32Bit,
        .ImageSize = New Size(LargeIconSize, LargeIconSize),
        .TransparentColor = Color.Transparent
    }
    Private ReadOnly _OwnedImages As New List(Of IDisposable)()
    Private _Disposed As Boolean

    Public Sub New()
        RegisterShellIcon(GenericFileKey, Nothing, False)
        RegisterShellIcon(FolderKey, Nothing, True)
    End Sub

    ''' <summary>The 16x16 list used by the folder tree and the small-icon, list, and details views.</summary>
    Public ReadOnly Property SmallImages As ImageList
        Get
            Return _SmallImages
        End Get
    End Property

    ''' <summary>The 32x32 list used by the large-icon and tile views.</summary>
    Public ReadOnly Property LargeImages As ImageList
        Get
            Return _LargeImages
        End Get
    End Property

    ''' <summary>Returns True when an icon is already registered under <paramref name="Key"/>.</summary>
    Public Function ContainsKey(Key As String) As Boolean
        Return _SmallImages.Images.ContainsKey(Key)
    End Function

    ''' <summary>
    ''' Ensures the extension-based icon for <paramref name="FileName"/> is registered in both lists and
    ''' returns its key.
    ''' </summary>
    Public Function EnsureExtensionIcon(FileName As String) As String
        Dim Extension = Path.GetExtension(If(FileName, String.Empty)).ToLowerInvariant()
        If Extension.Length = 0 Then Return GenericFileKey

        Dim Key = $"ext:{Extension}"
        If _SmallImages.Images.ContainsKey(Key) = False Then RegisterShellIcon(Key, Extension, False)
        Return Key
    End Function

    ''' <summary>
    ''' Registers an executable's own icon under <paramref name="Key"/>. The provider takes ownership of
    ''' <paramref name="Icons"/> and frees it on <see cref="Dispose"/>; the caller must not dispose it.
    ''' </summary>
    Public Sub AddExecutableIcon(Key As String, Icons As ExecutableIcons)
        If Icons Is Nothing Then Return
        If _SmallImages.Images.ContainsKey(Key) Then
            Icons.Dispose()
            Return
        End If

        Dim SmallSource = If(Icons.SmallIcon, Icons.LargeIcon)
        Dim LargeSource = If(Icons.LargeIcon, Icons.SmallIcon)
        RetainAndAdd(_SmallImages, Key, SmallSource)
        RetainAndAdd(_LargeImages, Key, LargeSource)
    End Sub

    ''' <summary>
    ''' Reads the default icon out of an executable's bytes and renders it at both list sizes with the
    ''' shell's own rasteriser. Returns Nothing when the stream carries no usable icon. Safe to call off
    ''' the UI thread; the caller passes the result to <see cref="AddExecutableIcon"/>.
    ''' </summary>
    Public Shared Function ExtractExecutableIcons(Content As Stream, Length As Long) As ExecutableIcons
        Dim Group = PeIconReader.ReadDefaultIconGroup(Content, Length)
        If Group Is Nothing Then Return Nothing

        Dim SmallIcon = CreateIconFromGroup(Group, SmallIconSize)
        Dim LargeIcon = CreateIconFromGroup(Group, LargeIconSize)
        If SmallIcon Is Nothing AndAlso LargeIcon Is Nothing Then Return Nothing
        Return New ExecutableIcons(SmallIcon, LargeIcon)
    End Function

    Private Shared Function CreateIconFromGroup(Group As PeIconReader.IconGroup, Size As Integer) As Icon
        Dim IconId = LookupIconIdFromDirectoryEx(Group.Directory, True, Size, Size, LoadResourceDefaultColour)
        Dim ImageBytes As Byte() = Nothing
        If IconId = 0 OrElse Group.Images.TryGetValue(IconId, ImageBytes) = False Then Return Nothing

        Dim Handle = CreateIconFromResourceEx(ImageBytes, CUInt(ImageBytes.Length), True, IconResourceVersion,
                                              Size, Size, LoadResourceDefaultColour)
        If Handle = IntPtr.Zero Then Return Nothing
        Try
            Return DirectCast(Icon.FromHandle(Handle).Clone(), Icon)
        Finally
            DestroyIcon(Handle)
        End Try
    End Function

    Private Sub RegisterShellIcon(Key As String, Extension As String, IsDirectory As Boolean)
        RetainAndAdd(_SmallImages, Key, LoadShellIcon(Extension, IsDirectory, False))
        RetainAndAdd(_LargeImages, Key, LoadShellIcon(Extension, IsDirectory, True))
    End Sub

    Private Sub RetainAndAdd(Images As ImageList, Key As String, Icon As Icon)
        If Images.Images.ContainsKey(Key) Then Return

        If Icon Is Nothing Then
            Dim Blank As Image = New Bitmap(Images.ImageSize.Width, Images.ImageSize.Height)
            _OwnedImages.Add(Blank)
            Images.Images.Add(Key, Blank)
            Return
        End If

        ' An image list copies its images lazily, so the source icon must outlive it.
        If _OwnedImages.Contains(Icon) = False Then _OwnedImages.Add(Icon)
        Images.Images.Add(Key, Icon)
    End Sub

    Private Shared Function LoadShellIcon(Extension As String, IsDirectory As Boolean, Large As Boolean) As Icon
        Dim Info As New SHFILEINFO()
        Dim Attributes = If(IsDirectory, FileAttributeDirectory, FileAttributeNormal)
        Dim SamplePath = If(IsDirectory, "folder", If(String.IsNullOrEmpty(Extension), "file", $"file{Extension}"))
        Dim SizeFlag = If(Large, ShgfiLargeIcon, ShgfiSmallIcon)
        Dim Result = SHGetFileInfo(SamplePath, Attributes, Info, Marshal.SizeOf(GetType(SHFILEINFO)),
                                   ShgfiIcon Or SizeFlag Or ShgfiUseFileAttributes)
        If Result = IntPtr.Zero OrElse Info.hIcon = IntPtr.Zero Then Return Nothing
        Try
            Return DirectCast(Icon.FromHandle(Info.hIcon).Clone(), Icon)
        Finally
            DestroyIcon(Info.hIcon)
        End Try
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _Disposed Then Return
        _Disposed = True

        _SmallImages.Dispose()
        _LargeImages.Dispose()
        For Each ownedImage In _OwnedImages
            ownedImage.Dispose()
        Next
        _OwnedImages.Clear()
    End Sub
End Class
