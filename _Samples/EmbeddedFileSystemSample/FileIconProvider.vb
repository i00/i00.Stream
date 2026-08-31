Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Runtime.InteropServices

''' <summary>
''' A session-lifetime cache of Windows shell icons, rendered to independent 32-bit ARGB bitmaps and
''' drawn by the owner-drawn file list (no <see cref="Windows.Forms.ImageList"/> holds any real icon,
''' which is what previously mangled their alpha). Extension and folder icons are keyed by extension
''' (".txt", plus folder / generic sentinels) and extracted synchronously on first use; an executable's
''' own icon is keyed by anchor ID and produced on a worker thread.
''' </summary>
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

    <DllImport("shell32.dll", SetLastError:=True)>
    Private Shared Function SHGetImageList(ImageListKind As Integer, ByRef InterfaceId As Guid, ByRef ImageList As IntPtr) As Integer
    End Function

    <DllImport("comctl32.dll", SetLastError:=True)>
    Private Shared Function ImageList_GetIcon(ImageList As IntPtr, Index As Integer, Flags As Integer) As IntPtr
    End Function

    Private Const ShgfiIcon As Integer = 1 << 8
    Private Const ShgfiLargeIcon As Integer = 0
    Private Const ShgfiSmallIcon As Integer = 1 << 0
    Private Const ShgfiUseFileAttributes As Integer = 1 << 4
    Private Const ShgfiSysIconIndex As Integer = &H4000
    Private Const FileAttributeNormal As Integer = 1 << 7
    Private Const FileAttributeDirectory As Integer = 1 << 4

    Private Const LoadResourceDefaultColour As UInteger = 0
    Private Const IconResourceVersion As UInteger = &H30000UI
    Private Const ShilJumbo As Integer = 4
    Private Const IldTransparent As Integer = 1
    Private Shared ReadOnly ImageListInterfaceId As New Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")

    Public Const SmallIconSize As Integer = 16
    Public Const LargeIconSize As Integer = 32
    Public Const JumboIconSize As Integer = 256

    Public Const FolderKey As String = ":folder:"
    Public Const GenericFileKey As String = ":file:"

    Private ReadOnly _ShellIcons As New Dictionary(Of String, Dictionary(Of Integer, Bitmap))(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _ExecutableIcons As New Dictionary(Of Long, Dictionary(Of Integer, Bitmap))()
    Private ReadOnly _ExecutablePending As New HashSet(Of Long)()
    Private ReadOnly _TreeImages As New ImageList() With {
        .ColorDepth = ColorDepth.Depth32Bit,
        .ImageSize = New Size(SmallIconSize, SmallIconSize)
    }
    Private _Disposed As Boolean

    Public Sub New()
        Using FolderIcon = LoadShellIcon(String.Empty, True, False)
            If FolderIcon IsNot Nothing Then _TreeImages.Images.Add(FolderKey, RenderToBitmap(FolderIcon, SmallIconSize))
        End Using
    End Sub

    ''' <summary>A one-icon image list (the folder glyph) for the folder tree, which is not owner-drawn.</summary>
    Public ReadOnly Property TreeImages As ImageList
        Get
            Return _TreeImages
        End Get
    End Property

    ''' <summary>The cache key (extension, or a folder / generic sentinel) for a directory entry.</summary>
    Public Shared Function KeyForEntry(Name As String, IsDirectory As Boolean) As String
        If IsDirectory Then Return FolderKey
        Dim Extension = Path.GetExtension(If(Name, String.Empty)).ToLowerInvariant()
        Return If(Extension.Length = 0, GenericFileKey, Extension)
    End Function

    ''' <summary>
    ''' Returns the shell icon for <paramref name="Key"/> rendered to <paramref name="Size"/> px, extracting
    ''' it on first use and keeping it for the session (across folder changes). Fast enough for the paint path.
    ''' </summary>
    Public Function GetShellIcon(Key As String, Size As Integer) As Bitmap
        Dim BySize As Dictionary(Of Integer, Bitmap) = Nothing
        If _ShellIcons.TryGetValue(Key, BySize) = False Then
            BySize = New Dictionary(Of Integer, Bitmap)()
            _ShellIcons(Key) = BySize
        End If

        Dim Result As Bitmap = Nothing
        If BySize.TryGetValue(Size, Result) = False Then
            Result = ExtractShellIcon(Key, Size)
            BySize(Size) = Result   ' a Nothing is cached too, so a failed extraction is not retried every paint
        End If
        Return Result
    End Function

    Public Function HasExecutableIcon(AnchorId As Long) As Boolean
        Return _ExecutableIcons.ContainsKey(AnchorId)
    End Function

    ''' <summary>Returns a cached executable icon at <paramref name="Size"/> px, or Nothing if not loaded yet.</summary>
    Public Function TryGetExecutableIcon(AnchorId As Long, Size As Integer) As Bitmap
        Dim BySize As Dictionary(Of Integer, Bitmap) = Nothing
        If _ExecutableIcons.TryGetValue(AnchorId, BySize) = False Then Return Nothing
        Dim Result As Bitmap = Nothing
        BySize.TryGetValue(Size, Result)
        Return Result
    End Function

    ''' <summary>Reserves an anchor for a background load. Returns False if already loaded or in flight.</summary>
    Public Function BeginExecutableLoad(AnchorId As Long) As Boolean
        If _ExecutableIcons.ContainsKey(AnchorId) Then Return False
        Return _ExecutablePending.Add(AnchorId)
    End Function

    ''' <summary>Publishes background-rendered executable icons for an anchor. Ownership transfers here.</summary>
    Public Sub CompleteExecutableLoad(AnchorId As Long, BySize As Dictionary(Of Integer, Bitmap))
        _ExecutablePending.Remove(AnchorId)
        If BySize Is Nothing OrElse _ExecutableIcons.ContainsKey(AnchorId) Then
            DisposeAll(BySize)
            Return
        End If
        _ExecutableIcons(AnchorId) = BySize
    End Sub

    ''' <summary>
    ''' Reads an executable's default icon straight from its bytes and renders it to bitmaps at each of
    ''' <paramref name="Sizes"/>. Pure native + GDI+, so safe to call from a worker thread.
    ''' </summary>
    Public Shared Function ExtractExecutableIconBitmaps(Content As Stream, Length As Long, Sizes As IEnumerable(Of Integer)) As Dictionary(Of Integer, Bitmap)
        Dim Group = PeIconReader.ReadDefaultIconGroup(Content, Length)
        If Group Is Nothing Then Return Nothing

        Dim Result As New Dictionary(Of Integer, Bitmap)()
        For Each Size In Sizes
            Dim SourceIcon = CreateIconFromGroup(Group, Size)
            If SourceIcon IsNot Nothing Then
                Using SourceIcon
                    Result(Size) = RenderToBitmap(SourceIcon, Size)
                End Using
            End If
        Next
        Return If(Result.Count = 0, Nothing, Result)
    End Function

    Private Shared Function ExtractShellIcon(Key As String, Size As Integer) As Bitmap
        Dim IsDirectory = Key = FolderKey
        Dim Extension = If(Key = FolderKey OrElse Key = GenericFileKey, String.Empty, Key)

        Dim SourceIcon As Icon
        If Size > LargeIconSize Then
            SourceIcon = LoadJumboIcon(Extension, IsDirectory)
        Else
            SourceIcon = LoadShellIcon(Extension, IsDirectory, Size > SmallIconSize)
        End If
        If SourceIcon Is Nothing Then Return Nothing

        Using SourceIcon
            Return RenderToBitmap(SourceIcon, Size)
        End Using
    End Function

    ''' <summary>Rasterises an icon into a fresh, independent 32-bit ARGB bitmap of the requested size.</summary>
    Private Shared Function RenderToBitmap(SourceIcon As Icon, Size As Integer) As Bitmap
        Dim Result = New Bitmap(Size, Size, PixelFormat.Format32bppArgb)
        Using Canvas = Graphics.FromImage(Result)
            Canvas.InterpolationMode = InterpolationMode.HighQualityBicubic
            Canvas.PixelOffsetMode = PixelOffsetMode.HighQuality
            Using SourceBitmap = SourceIcon.ToBitmap()
                Canvas.DrawImage(SourceBitmap, New Rectangle(0, 0, Size, Size))
            End Using
        End Using
        Return Result
    End Function

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

    ''' <summary>The shell's 256px "jumbo" icon for an extension or folder, or Nothing when unavailable.</summary>
    Private Shared Function LoadJumboIcon(Extension As String, IsDirectory As Boolean) As Icon
        Dim Info As New SHFILEINFO()
        Dim Attributes = If(IsDirectory, FileAttributeDirectory, FileAttributeNormal)
        Dim SamplePath = If(IsDirectory, "folder", If(String.IsNullOrEmpty(Extension), "file", $"file{Extension}"))
        If SHGetFileInfo(SamplePath, Attributes, Info, Marshal.SizeOf(GetType(SHFILEINFO)),
                         ShgfiSysIconIndex Or ShgfiUseFileAttributes) = IntPtr.Zero Then Return Nothing

        Dim ImageListHandle As IntPtr
        Dim InterfaceId = ImageListInterfaceId
        If SHGetImageList(ShilJumbo, InterfaceId, ImageListHandle) <> 0 OrElse ImageListHandle = IntPtr.Zero Then Return Nothing

        Dim Handle = ImageList_GetIcon(ImageListHandle, Info.iIcon, IldTransparent)
        If Handle = IntPtr.Zero Then Return Nothing
        Try
            Return DirectCast(Icon.FromHandle(Handle).Clone(), Icon)
        Finally
            DestroyIcon(Handle)
        End Try
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

    Private Shared Sub DisposeAll(Bitmaps As Dictionary(Of Integer, Bitmap))
        If Bitmaps Is Nothing Then Return
        For Each Bitmap In Bitmaps.Values
            Bitmap?.Dispose()
        Next
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _Disposed Then Return
        _Disposed = True

        _TreeImages.Dispose()
        For Each BySize In _ShellIcons.Values
            DisposeAll(BySize)
        Next
        _ShellIcons.Clear()
        For Each BySize In _ExecutableIcons.Values
            DisposeAll(BySize)
        Next
        _ExecutableIcons.Clear()
    End Sub
End Class
