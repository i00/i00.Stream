' ================================================================================
' EmbeddedFileSystem Compatibility
' ================================================================================
' Targets .NET Framework 4.8 and requires i00.Streams.ChunkedStream.
' Directory record: {DataType}{ParentAnchorId}{LengthOfSelf}{ContentListEntry...}
' File record:      {DataType}{ParentAnchorId}{FileData...}
' Entry record:     {EntryType}{ChildAnchorId}{Length}{UTF-16 name[256]}
' The root is anchored at logical offset zero and has ParentAnchorId = 0.
' This format is incompatible with the earlier directory format without ParentAnchorId.
' ================================================================================
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Text
Imports i00.Streams

Namespace Streams
    ''' <summary>Stores an anchor-addressed hierarchical file system in a ChunkedStream.</summary>
    Public NotInheritable Class EmbeddedFileSystem
        Implements IDisposable

        ''' <summary>Identifies the record stored at an anchor.</summary>
        Public Enum DataType As Long
            Directory = 1
            File = 2
        End Enum

        ''' <summary>Identifies a directory entry and a file's current state.</summary>
        Public Enum EntryTypes As Long
            Directory = 1
            File = 2
            PendingFile = 3
        End Enum

        ''' <summary>Specifies how abandoned pending files are recovered.</summary>
        Public Enum PendingFileRecoveryActions
            Finalize = 0
            Remove = 1
        End Enum

        ''' <summary>Describes one child entry stored in a directory.</summary>
        Public NotInheritable Class ContentListEntry
            Friend Sub New(EntryType As EntryTypes, ChildAnchorId As Long, LengthOfDataAtEntry As Long, Name As String)
                Me.EntryType = EntryType
                Me.ChildAnchorId = ChildAnchorId
                Me.LengthOfDataAtEntry = LengthOfDataAtEntry
                Me.Name = Name
            End Sub

            ''' <summary>Gets the entry type and file state.</summary>
            Public ReadOnly Property EntryType As EntryTypes
            ''' <summary>Gets the child's stable anchor ID.</summary>
            Public ReadOnly Property ChildAnchorId As Long
            ''' <summary>Gets the file-data length or complete directory-record length.</summary>
            Public ReadOnly Property LengthOfDataAtEntry As Long
            ''' <summary>Gets the child name.</summary>
            Public ReadOnly Property Name As String
        End Class

        Private NotInheritable Class EntryLocation
            Public Property Parent As ChunkedStream.Anchor
            Public Property Entry As ContentListEntry
            Public Property Index As Integer
        End Class

        Private Const Int64Size As Integer = 8
        Private Const ParentOffset As Integer = 8
        Private Const DirectoryLengthOffset As Integer = 16
        Private Const DirectoryHeaderSize As Integer = 24
        Private Const FileHeaderSize As Integer = 16
        Private Const NameCharacterCapacity As Integer = 256
        Private Const NameByteCapacity As Integer = 512
        Private Const EntrySize As Integer = 536

        Public ReadOnly ChunkedStream As ChunkedStream
        Private ReadOnly _SyncRoot As New Object()
        Private ReadOnly _OpenFileIds As New HashSet(Of Long)()
        Private _Root As ChunkedStream.Anchor
        Private _Disposed As Boolean

        ''' <summary>Opens or creates an embedded file system.</summary>
        ''' <param name="ChunkedStream">The readable, writable, seekable backing ChunkedStream.</param>
        Public Sub New(ChunkedStream As ChunkedStream)
            If ChunkedStream Is Nothing Then Throw New ArgumentNullException(NameOf(ChunkedStream))
            If ChunkedStream.CanRead = False OrElse ChunkedStream.CanWrite = False OrElse ChunkedStream.CanSeek = False Then
                Throw New ArgumentException("The ChunkedStream must be readable, writable, and seekable.", NameOf(ChunkedStream))
            End If
            Me.ChunkedStream = ChunkedStream
            If Me.ChunkedStream.Length = 0 Then CreateRoot() Else OpenRoot()
        End Sub

        ''' <summary>Gets the non-removable root directory anchor ID.</summary>
        Public ReadOnly Property RootAnchorId As Long
            Get
                SyncLock _SyncRoot
                    ThrowIfDisposed()
                    Return _Root.AnchorId
                End SyncLock
            End Get
        End Property

        ''' <summary>Gets the entries directly contained by the root.</summary>
        Public Function GetRootEntries() As IReadOnlyList(Of ContentListEntry)
            Return GetDirectoryEntries(RootAnchorId)
        End Function

        ''' <summary>Gets the entries directly contained by a directory.</summary>
        Public Function GetDirectoryEntries(DirectoryAnchorId As Long) As IReadOnlyList(Of ContentListEntry)
            SyncLock _SyncRoot
                ThrowIfDisposed()
                Return ReadEntries(GetDirectory(DirectoryAnchorId))
            End SyncLock
        End Function

        ''' <summary>Gets the parent anchor ID stored in a file or directory record.</summary>
        Public Function GetParentAnchorId(AnchorId As Long) As Long
            SyncLock _SyncRoot
                ThrowIfDisposed()
                Dim Anchor = GetAnchor(AnchorId)
                ReadType(Anchor)
                Return ReadInt64(Anchor.Offset + ParentOffset)
            End SyncLock
        End Function

        ''' <summary>Creates an empty child directory and returns its anchor ID.</summary>
        Public Function CreateDirectory(ParentDirectoryAnchorId As Long, Name As String) As Long
            SyncLock _SyncRoot
                ThrowIfDisposed()
                ValidateName(Name)
                Dim Parent = GetDirectory(ParentDirectoryAnchorId)
                EnsureNameAvailable(Parent, Name)
                Using Scope = ChunkedStream.DeferPublish()
                    Dim Data = BuildDirectory(Parent.AnchorId)
                    Dim Child = ChunkedStream.CreateAnchor(Data)
                    AppendEntry(Parent, New ContentListEntry(EntryTypes.Directory, Child.AnchorId, Data.LongLength, Name))
                    Scope.Publish()
                    Return Child.AnchorId
                End Using
            End SyncLock
        End Function

        ''' <summary>Creates an empty child file and returns its anchor ID.</summary>
        Public Function CreateFile(ParentDirectoryAnchorId As Long, Name As String, Optional CreateAsPending As Boolean = True) As Long
            SyncLock _SyncRoot
                ThrowIfDisposed()
                ValidateName(Name)
                Dim Parent = GetDirectory(ParentDirectoryAnchorId)
                EnsureNameAvailable(Parent, Name)
                Using Scope = ChunkedStream.DeferPublish()
                    Dim Child = ChunkedStream.CreateAnchor(BuildFile(Parent.AnchorId))
                    AppendEntry(Parent, New ContentListEntry(If(CreateAsPending, EntryTypes.PendingFile, EntryTypes.File), Child.AnchorId, 0, Name))
                    Scope.Publish()
                    Return Child.AnchorId
                End Using
            End SyncLock
        End Function

        ''' <summary>Opens a file as a standard seekable .NET stream.</summary>
        ''' <remarks>The parent entry is PendingFile until the returned stream is disposed.</remarks>
        Public Function OpenFile(FileAnchorId As Long) As Stream
            SyncLock _SyncRoot
                ThrowIfDisposed()
                Dim FileAnchor = GetAnchor(FileAnchorId)
                EnsureType(FileAnchor, DataType.File)
                If _OpenFileIds.Add(FileAnchorId) = False Then Throw New IOException($"File anchor {FileAnchorId} is already open.")
                Try
                    Dim Location = GetParentEntry(FileAnchor)
                    If Location.Entry.EntryType <> EntryTypes.File AndAlso Location.Entry.EntryType <> EntryTypes.PendingFile Then
                        Throw New InvalidDataException($"Anchor {FileAnchorId} is not referenced as a file.")
                    End If
                    SetEntryType(Location, EntryTypes.PendingFile)
                    Return New FileStreamView(Me, FileAnchor)
                Catch
                    _OpenFileIds.Remove(FileAnchorId)
                    Throw
                End Try
            End SyncLock
        End Function

        ''' <summary>Finds a named child directly in a directory.</summary>
        Public Function FindEntry(ParentDirectoryAnchorId As Long, Name As String) As ContentListEntry
            SyncLock _SyncRoot
                ThrowIfDisposed()
                ValidateName(Name)
                Return FindByName(GetDirectory(ParentDirectoryAnchorId), Name).Entry
            End SyncLock
        End Function

        ''' <summary>Removes a named child. Directory removal is recursive.</summary>
        Public Sub DeleteEntry(ParentDirectoryAnchorId As Long, Name As String)
            SyncLock _SyncRoot
                ThrowIfDisposed()
                ValidateName(Name)
                DeleteCore(FindByName(GetDirectory(ParentDirectoryAnchorId), Name))
            End SyncLock
        End Sub

        ''' <summary>Recursively finalizes or removes abandoned PendingFile entries.</summary>
        ''' <remarks>This maintenance operation intentionally traverses the directory tree.</remarks>
        Public Function RecoverPendingFiles(Optional Action As PendingFileRecoveryActions = PendingFileRecoveryActions.Finalize) As Integer
            SyncLock _SyncRoot
                ThrowIfDisposed()
                Using Scope = ChunkedStream.DeferPublish()
                    Dim Result = RecoverPending(_Root, Action, New HashSet(Of Long)())
                    Scope.Publish()
                    Return Result
                End Using
            End SyncLock
        End Function

        Private Sub CreateRoot()
            _Root = ChunkedStream.CreateAnchor(BuildDirectory(0))
            If _Root.AnchorId <= 0 OrElse _Root.Offset <> 0 Then Throw New InvalidDataException("Invalid root anchor.")
        End Sub

        Private Sub OpenRoot()
            Dim Candidate = ChunkedStream.GetAnchors().Where(Function(anchor) anchor.IsValid AndAlso anchor.Offset = 0).SingleOrDefault()
            If Candidate Is Nothing Then Throw New InvalidDataException("No root anchor exists at logical offset zero.")
            EnsureType(Candidate, DataType.Directory)
            If ReadInt64(Candidate.Offset + ParentOffset) <> 0 Then Throw New InvalidDataException("The root parent anchor ID must be zero.")
            ValidateDirectory(Candidate)
            _Root = Candidate
        End Sub

        Private Shared Function BuildDirectory(ParentAnchorId As Long) As Byte()
            If ParentAnchorId < 0 Then Throw New ArgumentOutOfRangeException(NameOf(ParentAnchorId))
            Dim Data(DirectoryHeaderSize - 1) As Byte
            PutInt64(Data, 0, CLng(DataType.Directory))
            PutInt64(Data, ParentOffset, ParentAnchorId)
            PutInt64(Data, DirectoryLengthOffset, DirectoryHeaderSize)
            Return Data
        End Function

        Private Shared Function BuildFile(ParentAnchorId As Long) As Byte()
            If ParentAnchorId <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(ParentAnchorId))
            Dim Data(FileHeaderSize - 1) As Byte
            PutInt64(Data, 0, CLng(DataType.File))
            PutInt64(Data, ParentOffset, ParentAnchorId)
            Return Data
        End Function

        Private Function GetParentEntry(Child As ChunkedStream.Anchor) As EntryLocation
            Dim ParentId = ReadInt64(Child.Offset + ParentOffset)
            If ParentId <= 0 Then Throw New InvalidDataException($"Anchor {Child.AnchorId} has no valid parent.")
            Return FindByChildId(GetDirectory(ParentId), Child.AnchorId)
        End Function

        Private Function ReadEntries(DirectoryAnchor As ChunkedStream.Anchor) As IReadOnlyList(Of ContentListEntry)
            ValidateDirectory(DirectoryAnchor)
            Dim Length = ReadInt64(DirectoryAnchor.Offset + DirectoryLengthOffset)
            Dim Count = CInt((Length - DirectoryHeaderSize) \ EntrySize)
            Dim Result As New List(Of ContentListEntry)(Count)
            Dim Data(EntrySize - 1) As Byte
            For Index = 0 To Count - 1
                ReadExactly(DirectoryAnchor.Offset + DirectoryHeaderSize + (CLng(Index) * EntrySize), Data, 0, Data.Length)
                Result.Add(DecodeEntry(Data))
            Next
            Return New ReadOnlyCollection(Of ContentListEntry)(Result)
        End Function

        Private Sub ValidateDirectory(Anchor As ChunkedStream.Anchor)
            EnsureType(Anchor, DataType.Directory)
            Dim ParentId = ReadInt64(Anchor.Offset + ParentOffset)
            If Anchor.Offset = 0 Then
                If ParentId <> 0 Then Throw New InvalidDataException("The root parent must be zero.")
            ElseIf ParentId <= 0 Then
                Throw New InvalidDataException($"Directory anchor {Anchor.AnchorId} has an invalid parent.")
            End If
            Dim Length = ReadInt64(Anchor.Offset + DirectoryLengthOffset)
            If Length < DirectoryHeaderSize OrElse (Length - DirectoryHeaderSize) Mod EntrySize <> 0 Then
                Throw New InvalidDataException("Invalid directory length.")
            End If
            If Anchor.Offset + Length > ChunkedStream.Length Then Throw New InvalidDataException("Directory extends beyond the stream.")
        End Sub

        Private Sub AppendEntry(Parent As ChunkedStream.Anchor, Entry As ContentListEntry)
            Dim OldLength = ReadInt64(Parent.Offset + DirectoryLengthOffset)
            Dim Data = EncodeEntry(Entry)
            ChunkedStream.Insert(Parent.Offset + OldLength, Data)
            SetDirectoryLength(Parent, OldLength + EntrySize)
        End Sub

        Private Sub RemoveEntry(Location As EntryLocation)
            Dim OldLength = ReadInt64(Location.Parent.Offset + DirectoryLengthOffset)
            ChunkedStream.Remove(Location.Parent.Offset + DirectoryHeaderSize + (CLng(Location.Index) * EntrySize), EntrySize)
            SetDirectoryLength(Location.Parent, OldLength - EntrySize)
        End Sub

        Private Sub SetDirectoryLength(DirectoryAnchor As ChunkedStream.Anchor, Length As Long)
            WriteInt64(DirectoryAnchor.Offset + DirectoryLengthOffset, Length)
            If DirectoryAnchor.AnchorId <> _Root.AnchorId Then SetEntryLength(GetParentEntry(DirectoryAnchor), Length)
        End Sub

        Private Sub DeleteCore(Location As EntryLocation)
            If Location.Entry.ChildAnchorId = _Root.AnchorId Then Throw New InvalidOperationException("The root cannot be removed.")
            If _OpenFileIds.Contains(Location.Entry.ChildAnchorId) Then Throw New IOException("The file is open.")
            Using Scope = ChunkedStream.DeferPublish()
                Dim Child = GetAnchor(Location.Entry.ChildAnchorId)
                Dim ChildType = ReadType(Child)
                If ChildType = DataType.Directory Then
                    Dim Children = ReadEntries(Child).ToList()
                    For Index = Children.Count - 1 To 0 Step -1
                        DeleteCore(New EntryLocation With {.Parent = Child, .Entry = Children(Index), .Index = Index})
                    Next
                ElseIf ChildType <> DataType.File Then
                    Throw New InvalidDataException("Unsupported child type.")
                End If
                Dim StoredLength = If(ChildType = DataType.Directory,
                                      ReadInt64(Child.Offset + DirectoryLengthOffset),
                                      FileHeaderSize + Location.Entry.LengthOfDataAtEntry)
                RemoveEntry(Location)
                ChunkedStream.Remove(Child.Offset, StoredLength)
                Scope.Publish()
            End Using
        End Sub

        Private Function FindByName(Parent As ChunkedStream.Anchor, Name As String) As EntryLocation
            Dim Entries = ReadEntries(Parent)
            For Index = 0 To Entries.Count - 1
                If String.Equals(Entries(Index).Name, Name, StringComparison.OrdinalIgnoreCase) Then
                    Return New EntryLocation With {.Parent = Parent, .Entry = Entries(Index), .Index = Index}
                End If
            Next
            Throw New FileNotFoundException($"Entry '{Name}' was not found.", Name)
        End Function

        Private Function FindByChildId(Parent As ChunkedStream.Anchor, ChildAnchorId As Long) As EntryLocation
            Dim Entries = ReadEntries(Parent)
            For Index = 0 To Entries.Count - 1
                If Entries(Index).ChildAnchorId = ChildAnchorId Then
                    Return New EntryLocation With {.Parent = Parent, .Entry = Entries(Index), .Index = Index}
                End If
            Next
            Throw New InvalidDataException($"Parent anchor {Parent.AnchorId} does not reference child anchor {ChildAnchorId}.")
        End Function

        Private Function RecoverPending(DirectoryAnchor As ChunkedStream.Anchor, Action As PendingFileRecoveryActions,
                                        Visited As HashSet(Of Long)) As Integer
            If Visited.Add(DirectoryAnchor.AnchorId) = False Then Throw New InvalidDataException("Directory cycle detected.")
            Dim Recovered = 0
            Dim Index = 0
            While True
                Dim Entries = ReadEntries(DirectoryAnchor)
                If Index >= Entries.Count Then Exit While
                Dim Entry = Entries(Index)
                Dim Location = New EntryLocation With {.Parent = DirectoryAnchor, .Entry = Entry, .Index = Index}
                If Entry.EntryType = EntryTypes.Directory Then
                    Recovered += RecoverPending(GetDirectory(Entry.ChildAnchorId), Action, Visited)
                    Index += 1
                ElseIf Entry.EntryType = EntryTypes.PendingFile AndAlso _OpenFileIds.Contains(Entry.ChildAnchorId) = False Then
                    If Action = PendingFileRecoveryActions.Finalize Then
                        SetEntryType(Location, EntryTypes.File)
                        Index += 1
                    ElseIf Action = PendingFileRecoveryActions.Remove Then
                        DeleteCore(Location)
                    Else
                        Throw New ArgumentOutOfRangeException(NameOf(Action))
                    End If
                    Recovered += 1
                Else
                    Index += 1
                End If
            End While
            Visited.Remove(DirectoryAnchor.AnchorId)
            Return Recovered
        End Function

        Private Sub EnsureNameAvailable(Parent As ChunkedStream.Anchor, Name As String)
            If ReadEntries(Parent).Any(Function(entry) String.Equals(entry.Name, Name, StringComparison.OrdinalIgnoreCase)) Then
                Throw New IOException($"An entry named '{Name}' already exists.")
            End If
        End Sub

        Private Function GetAnchor(AnchorId As Long) As ChunkedStream.Anchor
            If AnchorId <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(AnchorId))
            Return ChunkedStream.GetAnchor(AnchorId)
        End Function

        Private Function GetDirectory(AnchorId As Long) As ChunkedStream.Anchor
            Dim Anchor = GetAnchor(AnchorId)
            EnsureType(Anchor, DataType.Directory)
            Return Anchor
        End Function

        Private Function ReadType(Anchor As ChunkedStream.Anchor) As DataType
            Dim Result = CType(ReadInt64(Anchor.Offset), DataType)
            If Result <> DataType.Directory AndAlso Result <> DataType.File Then Throw New InvalidDataException("Unsupported data type.")
            Return Result
        End Function

        Private Sub EnsureType(Anchor As ChunkedStream.Anchor, Expected As DataType)
            Dim Actual = ReadType(Anchor)
            If Actual <> Expected Then Throw New InvalidDataException($"Anchor {Anchor.AnchorId} contains {Actual}, expected {Expected}.")
        End Sub

        Private Sub SetEntryType(Location As EntryLocation, EntryType As EntryTypes)
            WriteInt64(Location.Parent.Offset + DirectoryHeaderSize + (CLng(Location.Index) * EntrySize), CLng(EntryType))
            Location.Entry = New ContentListEntry(EntryType, Location.Entry.ChildAnchorId,
                                                  Location.Entry.LengthOfDataAtEntry, Location.Entry.Name)
        End Sub

        Private Sub SetEntryLength(Location As EntryLocation, Length As Long)
            WriteInt64(Location.Parent.Offset + DirectoryHeaderSize + (CLng(Location.Index) * EntrySize) + 16, Length)
            Location.Entry = New ContentListEntry(Location.Entry.EntryType, Location.Entry.ChildAnchorId, Length, Location.Entry.Name)
        End Sub

        Private Shared Function EncodeEntry(Entry As ContentListEntry) As Byte()
            Dim Data(EntrySize - 1) As Byte
            PutInt64(Data, 0, CLng(Entry.EntryType))
            PutInt64(Data, 8, Entry.ChildAnchorId)
            PutInt64(Data, 16, Entry.LengthOfDataAtEntry)
            Dim NameData = Encoding.Unicode.GetBytes(Entry.Name)
            System.Buffer.BlockCopy(NameData, 0, Data, 24, NameData.Length)
            Return Data
        End Function

        Private Shared Function DecodeEntry(Data As Byte()) As ContentListEntry
            Dim EntryType = CType(BitConverter.ToInt64(Data, 0), EntryTypes)
            If EntryType <> EntryTypes.Directory AndAlso EntryType <> EntryTypes.File AndAlso EntryType <> EntryTypes.PendingFile Then
                Throw New InvalidDataException("Unsupported entry type.")
            End If
            Dim ChildId = BitConverter.ToInt64(Data, 8)
            Dim Length = BitConverter.ToInt64(Data, 16)
            If ChildId <= 0 OrElse Length < 0 Then Throw New InvalidDataException("Invalid directory entry.")
            Dim NameLength = 0
            While NameLength + 1 < NameByteCapacity
                If Data(24 + NameLength) = 0 AndAlso Data(25 + NameLength) = 0 Then Exit While
                NameLength += 2
            End While
            Dim Name = Encoding.Unicode.GetString(Data, 24, NameLength)
            ValidateName(Name)
            Return New ContentListEntry(EntryType, ChildId, Length, Name)
        End Function

        Private Shared Sub ValidateName(Name As String)
            If String.IsNullOrWhiteSpace(Name) Then Throw New ArgumentException("The name cannot be empty.", NameOf(Name))
            If Name.IndexOf(ChrW(0)) >= 0 Then Throw New ArgumentException("The name cannot contain a null character.", NameOf(Name))
            If Name.Length > NameCharacterCapacity Then Throw New ArgumentException("The name cannot exceed 256 UTF-16 characters.", NameOf(Name))
        End Sub

        Private Function GetFileLocation(FileAnchor As ChunkedStream.Anchor) As EntryLocation
            EnsureType(FileAnchor, DataType.File)
            Return GetParentEntry(FileAnchor)
        End Function

        Private Function GetFileLength(FileAnchor As ChunkedStream.Anchor) As Long
            Return GetFileLocation(FileAnchor).Entry.LengthOfDataAtEntry
        End Function

        Private Function ReadFile(FileAnchor As ChunkedStream.Anchor, Position As Long, Data As Byte(), Offset As Integer, Count As Integer) As Integer
            SyncLock _SyncRoot
                ValidateBuffer(Data, Offset, Count)
                Dim Length = GetFileLength(FileAnchor)
                If Position >= Length OrElse Count = 0 Then Return 0
                Dim Required = CInt(Math.Min(CLng(Count), Length - Position))
                Dim Result = ChunkedStream.Read(FileAnchor.Offset + FileHeaderSize + Position, Data, Offset, Required)
                If Result <> Required Then Throw New EndOfStreamException()
                Return Result
            End SyncLock
        End Function

        Private Sub WriteFile(FileAnchor As ChunkedStream.Anchor, Position As Long, Data As Byte(), Offset As Integer, Count As Integer)
            SyncLock _SyncRoot
                ValidateBuffer(Data, Offset, Count)
                If Count = 0 Then Return
                Dim Location = GetFileLocation(FileAnchor)
                Dim OldLength = Location.Entry.LengthOfDataAtEntry
                Dim NewEnd = Position + Count
                If NewEnd <= OldLength Then
                    ChunkedStream.Write(FileAnchor.Offset + FileHeaderSize + Position, Data, Offset, Count)
                    Return
                End If

                '
                ' The file grows. The data write and the entry-length update are folded
                ' into one metadata publish. If anything throws before the publish the
                ' grow is rolled back and the entry stays PendingFile for recovery.
                '
                Using Scope = ChunkedStream.DeferPublish()
                    If Position > OldLength Then ChunkedStream.InsertNullBytes(FileAnchor.Offset + FileHeaderSize + OldLength, Position - OldLength)
                    Dim Existing = CInt(Math.Min(CLng(Count), Math.Max(0L, OldLength - Position)))
                    If Existing > 0 Then ChunkedStream.Write(FileAnchor.Offset + FileHeaderSize + Position, Data, Offset, Existing)
                    Dim Appended = Count - Existing
                    If Appended > 0 Then
                        Dim Tail(Appended - 1) As Byte
                        System.Buffer.BlockCopy(Data, Offset + Existing, Tail, 0, Appended)
                        ChunkedStream.Insert(FileAnchor.Offset + FileHeaderSize + Math.Max(OldLength, Position), Tail)
                    End If
                    SetEntryLength(Location, NewEnd)
                    Scope.Publish()
                End Using
            End SyncLock
        End Sub

        Private Sub SetFileLength(FileAnchor As ChunkedStream.Anchor, Length As Long)
            SyncLock _SyncRoot
                Dim Location = GetFileLocation(FileAnchor)
                Dim OldLength = Location.Entry.LengthOfDataAtEntry
                If Length = OldLength Then Return
                Using Scope = ChunkedStream.DeferPublish()
                    If Length > OldLength Then
                        ChunkedStream.InsertNullBytes(FileAnchor.Offset + FileHeaderSize + OldLength, Length - OldLength)
                    Else
                        ChunkedStream.Remove(FileAnchor.Offset + FileHeaderSize + Length, OldLength - Length)
                    End If
                    SetEntryLength(Location, Length)
                    Scope.Publish()
                End Using
            End SyncLock
        End Sub

        Private Sub CloseFile(FileAnchorId As Long)
            SyncLock _SyncRoot
                If _Disposed OrElse _OpenFileIds.Remove(FileAnchorId) = False Then Return
                SetEntryType(GetFileLocation(GetAnchor(FileAnchorId)), EntryTypes.File)
            End SyncLock
        End Sub

        Private Sub ReadExactly(LogicalOffset As Long, Data As Byte(), Offset As Integer, Count As Integer)
            Dim Total = 0
            While Total < Count
                Dim Current = ChunkedStream.Read(LogicalOffset + Total, Data, Offset + Total, Count - Total)
                If Current = 0 Then Throw New EndOfStreamException()
                Total += Current
            End While
        End Sub

        Private Function ReadInt64(LogicalOffset As Long) As Long
            Dim Data(7) As Byte
            ReadExactly(LogicalOffset, Data, 0, 8)
            Return BitConverter.ToInt64(Data, 0)
        End Function

        Private Sub WriteInt64(LogicalOffset As Long, Value As Long)
            ChunkedStream.Write(LogicalOffset, BitConverter.GetBytes(Value))
        End Sub

        Private Shared Sub PutInt64(Data As Byte(), Offset As Integer, Value As Long)
            System.Buffer.BlockCopy(BitConverter.GetBytes(Value), 0, Data, Offset, 8)
        End Sub

        Private Shared Sub ValidateBuffer(Data As Byte(), Offset As Integer, Count As Integer)
            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))
            If Offset < 0 OrElse Count < 0 OrElse Offset > Data.Length - Count Then Throw New ArgumentOutOfRangeException()
        End Sub

        Private Sub ThrowIfDisposed()
            If _Disposed Then Throw New ObjectDisposedException(NameOf(EmbeddedFileSystem))
        End Sub

        ''' <summary>Disposes this file system and optionally the backing ChunkedStream.</summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _SyncRoot
                If _Disposed Then Return
                If _OpenFileIds.Count > 0 Then Throw New InvalidOperationException("All file streams must be disposed first.")
                _Disposed = True
            End SyncLock
        End Sub

        Private NotInheritable Class FileStreamView
            Inherits Stream

            Private Const WriteBufferFlushThreshold As Integer = 4 * 1024 * 1024

            Private ReadOnly _Owner As EmbeddedFileSystem
            Private ReadOnly _Anchor As ChunkedStream.Anchor
            Private ReadOnly _WriteBuffer As New MemoryStream()
            Private _Position As Long
            Private _Disposed As Boolean

            Public Sub New(Owner As EmbeddedFileSystem, Anchor As ChunkedStream.Anchor)
                _Owner = Owner
                _Anchor = Anchor
            End Sub

            '
            ' Sequential appends past the current end are buffered and materialised in one
            ' Insert per few MB, so each Insert lands full chunk records instead of growing
            ' the file's tail chunk one small write at a time (the latter frees an
            ' intermediate record for every write and is the main source of EFS bloat).
            ' Each drain is published by WriteFile as one durable step. Anything else - a
            ' random-access write, a read, a seek, a length query - drains the buffer first.
            '
            Private ReadOnly Property BufferedEndPosition As Long
                Get
                    Return _Owner.GetFileLength(_Anchor) + _WriteBuffer.Length
                End Get
            End Property

            Private Sub DrainWriteBuffer()
                If _WriteBuffer.Length = 0 Then Return
                Dim Tail = _WriteBuffer.ToArray()
                _WriteBuffer.SetLength(0)
                _Owner.WriteFile(_Anchor, _Owner.GetFileLength(_Anchor), Tail, 0, Tail.Length)
            End Sub

            Public Overrides ReadOnly Property CanRead As Boolean
                Get
                    Return _Disposed = False
                End Get
            End Property
            Public Overrides ReadOnly Property CanSeek As Boolean
                Get
                    Return _Disposed = False
                End Get
            End Property
            Public Overrides ReadOnly Property CanWrite As Boolean
                Get
                    Return _Disposed = False
                End Get
            End Property
            Public Overrides ReadOnly Property Length As Long
                Get
                    CheckDisposed()
                    Return BufferedEndPosition
                End Get
            End Property
            Public Overrides Property Position As Long
                Get
                    CheckDisposed()
                    Return _Position
                End Get
                Set
                    CheckDisposed()
                    If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Position))
                    _Position = Value
                End Set
            End Property

            Public Overrides Sub Flush()
                CheckDisposed()
                DrainWriteBuffer()
                _Owner.ChunkedStream.Flush()
            End Sub

            Public Overrides Function Read(Data As Byte(), Offset As Integer, Count As Integer) As Integer
                CheckDisposed()
                DrainWriteBuffer()
                Dim Result = _Owner.ReadFile(_Anchor, _Position, Data, Offset, Count)
                _Position += Result
                Return Result
            End Function

            Public Overrides Sub Write(Data As Byte(), Offset As Integer, Count As Integer)
                CheckDisposed()

                If _Position = BufferedEndPosition Then
                    _WriteBuffer.Write(Data, Offset, Count)
                    _Position += Count
                    If _WriteBuffer.Length >= WriteBufferFlushThreshold Then DrainWriteBuffer()
                    Return
                End If

                DrainWriteBuffer()
                _Owner.WriteFile(_Anchor, _Position, Data, Offset, Count)
                _Position += Count
            End Sub

            Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                CheckDisposed()
                DrainWriteBuffer()
                Dim Result = If(Origin = SeekOrigin.Begin, Offset,
                                If(Origin = SeekOrigin.Current, _Position + Offset,
                                   If(Origin = SeekOrigin.End, Length + Offset, Long.MinValue)))
                If Result < 0 Then Throw New IOException("Cannot seek before the file.")
                _Position = Result
                Return Result
            End Function

            Public Overrides Sub SetLength(Value As Long)
                CheckDisposed()
                If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Value))
                DrainWriteBuffer()
                _Owner.SetFileLength(_Anchor, Value)
                If _Position > Value Then _Position = Value
            End Sub

            Private Sub CheckDisposed()
                If _Disposed Then Throw New ObjectDisposedException(NameOf(FileStreamView))
            End Sub

            Protected Overrides Sub Dispose(Disposing As Boolean)
                If _Disposed Then Return
                If Disposing Then
                    DrainWriteBuffer()
                    _WriteBuffer.Dispose()
                    _Owner.CloseFile(_Anchor.AnchorId)
                End If
                _Disposed = True
                MyBase.Dispose(Disposing)
            End Sub
        End Class
    End Class
End Namespace
