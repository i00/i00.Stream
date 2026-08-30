Imports System.Drawing
Imports System.IO
Imports System.Runtime.InteropServices

''' <summary>
''' Supplies small Windows shell icons for the folder tree and file list and manages the shared
''' <see cref="Windows.Forms.ImageList"/> they are drawn from. Extension icons are resolved with
''' <c>SHGFI_USEFILEATTRIBUTES</c> so no file needs to exist on disk; an executable's own icon is
''' extracted by spilling its bytes to a temporary file and asking the shell for the real icon.
''' </summary>
''' <remarks>
''' Every member that touches <see cref="Images"/> must be called on the UI thread.
''' <see cref="ExtractExecutableIcon"/> is the only member safe to call from a worker thread.
''' The provider owns every icon handed to it and frees them all on <see cref="Dispose"/>; an
''' <see cref="ImageList"/> copies its images lazily, so nothing added to it may be disposed earlier.
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

    Private Const ShgfiIcon As Integer = 1 << 8
    Private Const ShgfiSmallIcon As Integer = 1 << 0
    Private Const ShgfiUseFileAttributes As Integer = 1 << 4
    Private Const FileAttributeNormal As Integer = 1 << 7
    Private Const FileAttributeDirectory As Integer = 1 << 4

    ''' <summary>Largest executable whose bytes are spilled to disk to read its embedded icon.</summary>
    Private Const MaximumExecutableIconScanBytes As Long = 96L * 1024L * 1024L

    Public Const FolderKey As String = ":folder:"
    Public Const GenericFileKey As String = ":file:"

    Private ReadOnly _Images As New ImageList() With {
        .ColorDepth = ColorDepth.Depth32Bit,
        .ImageSize = New Size(16, 16),
        .TransparentColor = Color.Transparent
    }
    Private ReadOnly _OwnedImages As New List(Of IDisposable)()
    Private _Disposed As Boolean

    Public Sub New()
        RegisterImage(GenericFileKey, LoadShellIcon(Nothing, False))
        RegisterImage(FolderKey, LoadShellIcon(Nothing, True))
    End Sub

    ''' <summary>The image list shared by the folder tree and the file list.</summary>
    Public ReadOnly Property Images As ImageList
        Get
            Return _Images
        End Get
    End Property

    ''' <summary>Returns True when an icon is already registered under <paramref name="Key"/>.</summary>
    Public Function ContainsKey(Key As String) As Boolean
        Return _Images.Images.ContainsKey(Key)
    End Function

    ''' <summary>
    ''' Ensures the extension-based icon for <paramref name="FileName"/> is registered and returns its key.
    ''' </summary>
    Public Function EnsureExtensionIcon(FileName As String) As String
        Dim Extension = Path.GetExtension(If(FileName, String.Empty)).ToLowerInvariant()
        If Extension.Length = 0 Then Return GenericFileKey

        Dim Key = $"ext:{Extension}"
        If _Images.Images.ContainsKey(Key) = False Then RegisterImage(Key, LoadShellIcon(Extension, False))
        Return Key
    End Function

    ''' <summary>
    ''' Hands an extracted executable icon to the provider, which owns it from now on and frees it on
    ''' <see cref="Dispose"/>. If an icon is already registered under <paramref name="Key"/> the new one
    ''' is retained but not shown.
    ''' </summary>
    Public Sub AddExecutableIcon(Key As String, Icon As Icon)
        If Icon Is Nothing Then Return
        _OwnedImages.Add(Icon)
        If _Images.Images.ContainsKey(Key) = False Then _Images.Images.Add(Key, Icon)
    End Sub

    Private Sub RegisterImage(Key As String, Icon As Icon)
        If Icon Is Nothing Then
            Dim Blank As New Bitmap(16, 16)
            _OwnedImages.Add(Blank)
            _Images.Images.Add(Key, Blank)
            Return
        End If

        _OwnedImages.Add(Icon)
        _Images.Images.Add(Key, Icon)
    End Sub

    Private Shared Function LoadShellIcon(Extension As String, IsDirectory As Boolean) As Icon
        Dim Info As New SHFILEINFO()
        Dim Attributes = If(IsDirectory, FileAttributeDirectory, FileAttributeNormal)
        Dim SamplePath = If(IsDirectory, "folder", If(String.IsNullOrEmpty(Extension), "file", $"file{Extension}"))
        Dim Result = SHGetFileInfo(SamplePath, Attributes, Info, Marshal.SizeOf(GetType(SHFILEINFO)),
                                   ShgfiIcon Or ShgfiSmallIcon Or ShgfiUseFileAttributes)
        Return MaterialiseIcon(Result, Info)
    End Function

    ''' <summary>
    ''' Spills <paramref name="Content"/> to a temporary <c>.exe</c> and returns its embedded small
    ''' icon, or Nothing when the file is too large or the shell has no icon for it. Safe to call
    ''' off the UI thread; the caller registers the result with <see cref="AddExecutableIcon"/>.
    ''' </summary>
    Public Shared Function ExtractExecutableIcon(Content As Stream, Length As Long) As Icon
        If Content Is Nothing OrElse Length <= 0 OrElse Length > MaximumExecutableIconScanBytes Then Return Nothing

        Dim TemporaryPath = Path.Combine(Path.GetTempPath(), $"efsicon_{Guid.NewGuid():N}.exe")
        Try
            Using Temporary = New FileStream(TemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Content.CopyTo(Temporary, 1024 * 1024)
            End Using

            Dim Info As New SHFILEINFO()
            Dim Result = SHGetFileInfo(TemporaryPath, 0, Info, Marshal.SizeOf(GetType(SHFILEINFO)),
                                       ShgfiIcon Or ShgfiSmallIcon)
            Return MaterialiseIcon(Result, Info)
        Catch
            Return Nothing
        Finally
            Try
                File.Delete(TemporaryPath)
            Catch
                ' A transient shell handle can briefly hold the file; it is a temp file and will be swept later.
            End Try
        End Try
    End Function

    Private Shared Function MaterialiseIcon(Result As IntPtr, Info As SHFILEINFO) As Icon
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

        _Images.Dispose()
        For Each ownedImage In _OwnedImages
            ownedImage.Dispose()
        Next
        _OwnedImages.Clear()
    End Sub
End Class
