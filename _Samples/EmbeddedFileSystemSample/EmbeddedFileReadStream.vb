Imports System.IO
Imports i00.Streams

''' <summary>
''' A seekable, read-only view over a single embedded file's bytes. It reads straight from the backing
''' <see cref="ChunkedStream"/> instead of opening the file through <see cref="EmbeddedFileSystem"/>, so
''' the directory entry is never flipped to <c>PendingFile</c> and no metadata is written just to read.
''' Every read goes through the ChunkedStream's own state lock, so an instance is safe to use from a
''' worker thread while the UI thread mutates the file system.
''' </summary>
Friend NotInheritable Class EmbeddedFileReadStream
    Inherits Stream

    ' Embedded file record layout: {DataType:8}{ParentAnchorId:8}{FileData...}
    Private Const FileDataOffset As Integer = 16

    Private ReadOnly _Chunked As ChunkedStream
    Private ReadOnly _Anchor As ChunkedStream.Anchor
    Private ReadOnly _Length As Long
    Private _Position As Long

    Private Sub New(Chunked As ChunkedStream, Anchor As ChunkedStream.Anchor, Length As Long)
        _Chunked = Chunked
        _Anchor = Anchor
        _Length = Length
    End Sub

    ''' <summary>
    ''' Opens a read-only view over the file referenced by <paramref name="Entry"/>, or Nothing when its
    ''' anchor no longer exists.
    ''' </summary>
    Public Shared Function TryOpen(FileSystem As EmbeddedFileSystem, Entry As EmbeddedFileSystem.ContentListEntry) As EmbeddedFileReadStream
        Return TryOpen(FileSystem, Entry.ChildAnchorId, Entry.LengthOfDataAtEntry)
    End Function

    ''' <summary>
    ''' Opens a read-only view over the file at <paramref name="FileAnchorId"/>, or Nothing when that
    ''' anchor no longer exists. <paramref name="Length"/> is the file's data length (its entry length).
    ''' </summary>
    Public Shared Function TryOpen(FileSystem As EmbeddedFileSystem, FileAnchorId As Long, Length As Long) As EmbeddedFileReadStream
        Dim Anchor As ChunkedStream.Anchor = Nothing
        If FileSystem.ChunkedStream.TryGetAnchor(FileAnchorId, Anchor) = False Then Return Nothing
        Return New EmbeddedFileReadStream(FileSystem.ChunkedStream, Anchor, Length)
    End Function

    Public Overrides ReadOnly Property CanRead As Boolean
        Get
            Return True
        End Get
    End Property

    Public Overrides ReadOnly Property CanSeek As Boolean
        Get
            Return True
        End Get
    End Property

    Public Overrides ReadOnly Property CanWrite As Boolean
        Get
            Return False
        End Get
    End Property

    Public Overrides ReadOnly Property Length As Long
        Get
            Return _Length
        End Get
    End Property

    Public Overrides Property Position As Long
        Get
            Return _Position
        End Get
        Set
            If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Position))
            _Position = Value
        End Set
    End Property

    Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
        If _Position >= _Length OrElse Count <= 0 Then Return 0
        Dim ToRead = CInt(Math.Min(CLng(Count), _Length - _Position))
        ' Resolve the anchor's current offset on every read: a concurrent file-system
        ' mutation can shift the file's logical position while a long read is in flight
        ' (a large executable's icon load can take seconds), and a cached base offset
        ' would then read the wrong bytes.
        Dim BaseOffset As Long
        Try
            BaseOffset = _Anchor.Offset + FileDataOffset
        Catch ex As Collections.Generic.KeyNotFoundException
            Return 0 ' the anchor was removed
        End Try
        Dim BytesRead = _Chunked.Read(BaseOffset + _Position, Buffer, Offset, ToRead)
        _Position += BytesRead
        Return BytesRead
    End Function

    Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
        Dim Target As Long
        Select Case Origin
            Case SeekOrigin.Begin
                Target = Offset
            Case SeekOrigin.Current
                Target = _Position + Offset
            Case SeekOrigin.End
                Target = _Length + Offset
            Case Else
                Throw New ArgumentException("Invalid seek origin.", NameOf(Origin))
        End Select
        If Target < 0 Then Throw New IOException("Cannot seek before the start of the file.")
        _Position = Target
        Return Target
    End Function

    Public Overrides Sub Flush()
    End Sub

    Public Overrides Sub SetLength(Value As Long)
        Throw New NotSupportedException()
    End Sub

    Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
        Throw New NotSupportedException()
    End Sub
End Class
