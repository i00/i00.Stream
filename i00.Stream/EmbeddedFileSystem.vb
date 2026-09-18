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
Imports System.Security.Cryptography
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
            ''' <summary>A file whose write stream has not been closed, or was abandoned by a crash.</summary>
            PendingFile = 3
            ''' <summary>A file that <see cref="Mark" /> flagged as backed by data a chunked-stream validation could not read.</summary>
            CorruptFile = 4
            ''' <summary>
            ''' A directory whose own content list <see cref="Mark" /> flagged as intersecting - or
            ''' being unreadable because of - a chunked-stream validation problem. Its children
            ''' cannot be enumerated; remove it with <see cref="DeleteEntry" /> or
            ''' <see cref="RecoverPendingFiles" />.
            ''' </summary>
            CorruptDirectory = 5
            ''' <summary>
            ''' A file <see cref="Mark" /> flagged as backed by data a chunked-stream validation
            ''' could not read SOME, but not all, of - as opposed to <see cref="CorruptFile" />,
            ''' where none of it could be trusted. <see cref="Mark" /> never reassigns an entry
            ''' away from this type once set (re-marking an already-<see cref="PartlyRecoveredFile" />
            ''' entry just re-reports it), so it - and, if <see cref="Mark" />'s
            ''' <c>RenamePartlyRecoveredFile</c> was used, the renamed name that goes with it -
            ''' survives until something explicitly acts on it. <see cref="RecoverPendingFiles" />'s
            ''' default (Finalize) action promotes it to a plain <see cref="File" />, same as
            ''' <see cref="CorruptFile" />; pass <see cref="PendingFileRecoveryActions.Remove" /> to
            ''' delete it outright instead.
            ''' </summary>
            PartlyRecoveredFile = 6
        End Enum

        ''' <summary>What <see cref="RecoverPendingFiles" /> does with a pending, corrupt or unreferenced entry.</summary>
        Public Enum PendingFileRecoveryActions
            ''' <summary>Take no action and omit the entry from the result.</summary>
            None = 0
            ''' <summary>Take no action, but include the entry in the result for inspection.</summary>
            List = 1
            ''' <summary>
            ''' Promote a pending entry to a normal file, or re-home an unreferenced record under
            ''' <c>\_Recovered</c>, keeping whatever data it currently holds.
            ''' </summary>
            Finalize = 2
            ''' <summary>Delete the entry / record and its data.</summary>
            Remove = 3
        End Enum

        ''' <summary>
        ''' Everything <see cref="RecoverPendingFiles" /> found notable about a candidate. A
        ''' reachable candidate carries the on-disk condition (<see cref="Pending" /> /
        ''' <see cref="CorruptData" />); an unreferenced record carries what the scan could infer -
        ''' <see cref="Orphaned" /> and/or <see cref="Unreachable" />, and <see cref="CorruptData" />
        ''' only when its bytes are provably damaged. <see cref="Pending" /> is never inferred for an
        ''' unreferenced record - the flag lived in the lost directory entry.
        ''' </summary>
        <Flags>
        Public Enum RecoveryConditions
            None = 0
            ''' <summary>The entry's write stream was never closed, or a crash abandoned it.</summary>
            Pending = 1 << 0
            ''' <summary>The record's bytes intersect a <see cref="ChunkedStream" /> validation problem (zero-filled by a repair).</summary>
            CorruptData = 1 << 1
            ''' <summary>The record's own direct parent link could not be resolved to a readable directory.</summary>
            Orphaned = 1 << 2
            ''' <summary>No path leads from the record back to the root.</summary>
            Unreachable = 1 << 3
        End Enum

        ''' <summary>
        ''' A pending, corrupt or unreferenced record offered to the selector passed to
        ''' <see cref="RecoverPendingFiles" />.
        ''' </summary>
        Public NotInheritable Class PendingFileRecoveryCandidate
            Friend Sub New(Path As String, AnchorId As Long, ParentDirectoryAnchorId As Long, State As EntryTypes,
                           IsDirectory As Boolean, Conditions As RecoveryConditions, DataLength As Long, BytesZeroed As Long)
                Me.Path = Path
                Me.AnchorId = AnchorId
                Me.ParentDirectoryAnchorId = ParentDirectoryAnchorId
                Me.State = State
                Me.IsDirectory = IsDirectory
                Me.Conditions = Conditions
                Me.DataLength = DataLength
                Me.BytesZeroed = BytesZeroed
            End Sub
            ''' <summary>
            ''' Full path of the entry, e.g. <c>\dir\sub\file.txt</c>. For an unreferenced record
            ''' this is a best-effort reconstruction rooted at <c>\?</c>.
            ''' </summary>
            Public ReadOnly Property Path As String
            ''' <summary>The record's stable anchor id.</summary>
            Public ReadOnly Property AnchorId As Long
            ''' <summary>Anchor id of the directory that directly contains this entry, or the record's claimed parent for an unreferenced record.</summary>
            Friend ReadOnly Property ParentDirectoryAnchorId As Long
            ''' <summary>
            ''' <see cref="EntryTypes.PendingFile" />, <see cref="EntryTypes.CorruptFile" /> or
            ''' <see cref="EntryTypes.CorruptDirectory" /> for a reachable entry;
            ''' <see cref="EntryTypes.File" /> or <see cref="EntryTypes.Directory" /> for an
            ''' unreferenced record (see <see cref="Conditions" />).
            ''' </summary>
            Public ReadOnly Property State As EntryTypes
            ''' <summary><see langword="True" /> when the record is a directory.</summary>
            Public ReadOnly Property IsDirectory As Boolean
            ''' <summary>Everything notable about this candidate.</summary>
            Public ReadOnly Property Conditions As RecoveryConditions
            ''' <summary>The entry's data length, or the directory record length for a directory.</summary>
            Public ReadOnly Property DataLength As Long
            ''' <summary>
            ''' Zero-filled (sparse) bytes currently in the record's data - what a preceding
            ''' <see cref="ChunkedStream.ValidationReport.Repair" /> replaced with zeros.
            ''' </summary>
            Public ReadOnly Property BytesZeroed As Long
        End Class

        ''' <summary>Describes one record acted on by <see cref="RecoverPendingFiles" />.</summary>
        Public NotInheritable Class PendingFileRecoveryResult
            Friend Sub New(Path As String, AnchorId As Long, PreviousState As EntryTypes, Conditions As RecoveryConditions,
                           IsDirectory As Boolean, Action As PendingFileRecoveryActions, DataLength As Long,
                           BytesZeroed As Long, RecoveredPath As String)
                Me.Path = Path
                Me.AnchorId = AnchorId
                Me.PreviousState = PreviousState
                Me.Conditions = Conditions
                Me.IsDirectory = IsDirectory
                Me.Action = Action
                Me.DataLength = DataLength
                Me.BytesZeroed = BytesZeroed
                Me.RecoveredPath = RecoveredPath
            End Sub
            ''' <summary>Path of the record before recovery, e.g. <c>\dir\sub\file.txt</c> or a <c>\?</c>-rooted reconstruction.</summary>
            Public ReadOnly Property Path As String
            ''' <summary>The record's stable anchor id.</summary>
            Public ReadOnly Property AnchorId As Long
            ''' <summary>
            ''' The entry state before recovery - <see cref="EntryTypes.PendingFile" />,
            ''' <see cref="EntryTypes.CorruptFile" />, <see cref="EntryTypes.CorruptDirectory" />,
            ''' or <see cref="EntryTypes.File" /> / <see cref="EntryTypes.Directory" /> for an
            ''' unreferenced record.
            ''' </summary>
            Public ReadOnly Property PreviousState As EntryTypes
            ''' <summary>What the scan found notable about the record.</summary>
            Public ReadOnly Property Conditions As RecoveryConditions
            ''' <summary><see langword="True" /> when the record is a directory.</summary>
            Public ReadOnly Property IsDirectory As Boolean
            ''' <summary>What was done to the record (<see cref="PendingFileRecoveryActions.List" /> = reported only).</summary>
            Public ReadOnly Property Action As PendingFileRecoveryActions
            ''' <summary>The record's data length at the time of recovery.</summary>
            Public ReadOnly Property DataLength As Long
            ''' <summary>
            ''' Zero-filled (sparse) bytes in the record's data at the time of recovery - what a
            ''' preceding <see cref="ChunkedStream.ValidationReport.Repair" /> replaced with zeros.
            ''' </summary>
            Public ReadOnly Property BytesZeroed As Long
            ''' <summary>
            ''' Where a <see cref="PendingFileRecoveryActions.Finalize" />d unreferenced record was
            ''' re-homed, e.g. <c>\_Recovered\Directory1</c>; <see langword="Nothing" /> otherwise.
            ''' </summary>
            Public ReadOnly Property RecoveredPath As String
        End Class

        ''' <summary>Describes one entry marked by <see cref="Mark" />.</summary>
        Public NotInheritable Class CorruptEntryMark
            Friend Sub New(Path As String, AnchorId As Long, LostBytes As Long, IsDirectory As Boolean,
                          Optional RenamedTo As String = Nothing)
                Me.Path = Path
                Me.AnchorId = AnchorId
                Me.LostBytes = LostBytes
                Me.IsDirectory = IsDirectory
                Me.RenamedTo = RenamedTo
            End Sub
            ''' <summary>Full path of the marked entry as it was when <see cref="Mark" /> found it.</summary>
            Public ReadOnly Property Path As String
            ''' <summary>The marked entry's stable anchor id.</summary>
            Public ReadOnly Property AnchorId As Long
            ''' <summary>
            ''' Logical bytes of this entry that fall inside a reported problem range. Zero when a
            ''' directory was flagged only because its content list could not be parsed.
            ''' </summary>
            Public ReadOnly Property LostBytes As Long
            ''' <summary>
            ''' <see langword="True" /> for a directory flagged <see cref="EntryTypes.CorruptDirectory" />
            ''' (its children could not be enumerated); <see langword="False" /> for a file flagged
            ''' <see cref="EntryTypes.CorruptFile" /> or <see cref="EntryTypes.PartlyRecoveredFile" />.
            ''' </summary>
            Public ReadOnly Property IsDirectory As Boolean
            ''' <summary>
            ''' The full path this entry was renamed to, when it was newly flagged
            ''' <see cref="EntryTypes.PartlyRecoveredFile" /> and <c>RenamePartlyRecoveredFile</c> was
            ''' <see langword="True" />; <see langword="Nothing" /> otherwise (including when the
            ''' entry was already <see cref="EntryTypes.PartlyRecoveredFile" /> before this call, or the
            ''' new name would have exceeded the 256-character limit).
            ''' </summary>
            Public ReadOnly Property RenamedTo As String
        End Class

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

        Private Const ParentOffset As Integer = 8
        Private Const DirectoryLengthOffset As Integer = 16
        Private Const DirectoryHeaderSize As Integer = 24
        Private Const FileHeaderSize As Integer = 16
        Private Const NameCharacterCapacity As Integer = 256
        Private Const NameByteCapacity As Integer = 512
        Private Const EntrySize As Integer = 536
        Private Const RecoveredFolderName As String = "_Recovered"

        Public ReadOnly ChunkedStream As ChunkedStream
        Private ReadOnly _SyncRoot As New Object()
        Private ReadOnly _OpenFileIds As New HashSet(Of Long)()
        Private _Root As ChunkedStream.Anchor
        Private _RecoveredAnchorId As Long
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
        ''' <remarks>
        ''' The parent entry is <see cref="EntryTypes.PendingFile" /> while the stream is open. On
        ''' dispose it is finalised back to <see cref="EntryTypes.File" /> unless
        ''' <see cref="FileStreamView.PendingOnClose" /> is set - pass <paramref name="PendingOnClose" />
        ''' (or set the property before the failure) to keep a partially written file pending for
        ''' <see cref="RecoverPendingFiles" /> when a write is abandoned part way through. Clear it on
        ''' the success path so the last statement before the stream closes commits the file.
        ''' <paramref name="WriteBufferFlushThreshold" /> sizes the buffer a sequential append fills
        ''' before it is materialised and durably published - raise it for a large sequential upload
        ''' to cut the number of fsyncs (see the comment on <c>FileStreamView.BufferedEndPosition</c>).
        ''' <paramref name="WriteBufferFlushIntervalMilliseconds" /> is a second, time-based publish
        ''' trigger alongside the byte threshold, so a slow or throttled upload cannot sit unpublished
        ''' for an unbounded stretch of wall-clock time just because it hasn't filled the buffer yet.
        ''' </remarks>
        Public Function OpenFile(FileAnchorId As Long, Optional PendingOnClose As Boolean = False,
                                 Optional WriteBufferFlushThreshold As Integer = FileStreamView.DefaultWriteBufferFlushThreshold,
                                 Optional WriteBufferFlushIntervalMilliseconds As Integer = FileStreamView.DefaultWriteBufferFlushIntervalMilliseconds) As FileStreamView
            SyncLock _SyncRoot
                ThrowIfDisposed()
                If WriteBufferFlushThreshold <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(WriteBufferFlushThreshold))
                If WriteBufferFlushIntervalMilliseconds <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(WriteBufferFlushIntervalMilliseconds))
                Dim FileAnchor = GetAnchor(FileAnchorId)
                EnsureType(FileAnchor, DataType.File)
                If _OpenFileIds.Add(FileAnchorId) = False Then Throw New IOException($"File anchor {FileAnchorId} is already open.")
                Try
                    Dim Location = GetParentEntry(FileAnchor)
                    If Location.Entry.EntryType <> EntryTypes.File AndAlso Location.Entry.EntryType <> EntryTypes.PendingFile Then
                        Throw New InvalidDataException($"Anchor {FileAnchorId} is not referenced as a file.")
                    End If
                    SetEntryType(Location, EntryTypes.PendingFile)
                    Return New FileStreamView(Me, FileAnchor, WriteBufferFlushThreshold, WriteBufferFlushIntervalMilliseconds) With {.PendingOnClose = PendingOnClose}
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

        ''' <summary>
        ''' Renames a named child. The child anchor and its contents are untouched - only the fixed-size
        ''' name field of the parent's entry record is rewritten, so nothing in the stream moves. Changing
        ''' only the letter case of the existing name is allowed.
        ''' </summary>
        Public Sub RenameEntry(ParentDirectoryAnchorId As Long, CurrentName As String, NewName As String)
            SyncLock _SyncRoot
                ThrowIfDisposed()
                ValidateName(CurrentName)
                ValidateName(NewName)
                Dim Parent = GetDirectory(ParentDirectoryAnchorId)
                Dim Location = FindByName(Parent, CurrentName)
                If String.Equals(CurrentName, NewName, StringComparison.OrdinalIgnoreCase) = False Then
                    EnsureNameAvailable(Parent, NewName)
                End If
                Using Scope = ChunkedStream.DeferPublish()
                    SetEntryName(Location, NewName)
                    Scope.Publish()
                End Using
            End SyncLock
        End Sub

        ''' <summary>
        ''' Moves an entry into another directory, optionally renaming it in the same step. The anchor
        ''' and its contents are untouched - its directory entry is relocated and its own stored parent
        ''' link rewritten, so nothing in the moved entry's data moves. The entry's current directory and
        ''' name are resolved from <paramref name="AnchorId" /> itself (see <see cref="GetParentAnchorId" />),
        ''' so the caller need only hold the anchor id, not track where the entry currently lives. Moving
        ''' within the same directory is equivalent to <see cref="RenameEntry" />.
        ''' </summary>
        Public Sub Move(AnchorId As Long, DestinationParentDirectoryAnchorId As Long, Optional NewName As String = Nothing)
            SyncLock _SyncRoot
                ThrowIfDisposed()
                If AnchorId = _Root.AnchorId Then Throw New InvalidOperationException("The root cannot be moved.")

                Dim Child = GetAnchor(AnchorId)
                Dim Location = GetParentEntry(Child)
                Dim TargetName = If(NewName, Location.Entry.Name)
                ValidateName(TargetName)

                Dim DestinationParent = GetDirectory(DestinationParentDirectoryAnchorId)

                If Location.Parent.AnchorId = DestinationParent.AnchorId Then
                    ' Moving onto its own directory is just a rename - reuse RenameEntry's exact
                    ' in-place field rewrite rather than relocating the entry to the end of the list.
                    If String.Equals(Location.Entry.Name, TargetName, StringComparison.OrdinalIgnoreCase) = False Then
                        EnsureNameAvailable(DestinationParent, TargetName)
                    End If
                    Using Scope = ChunkedStream.DeferPublish()
                        SetEntryName(Location, TargetName)
                        Scope.Publish()
                    End Using
                    Return
                End If

                If (Location.Entry.EntryType = EntryTypes.Directory OrElse Location.Entry.EntryType = EntryTypes.CorruptDirectory) AndAlso
                   IsSameOrAncestor(AnchorId, DestinationParent.AnchorId) Then
                    Throw New InvalidOperationException("A directory cannot be moved into itself or one of its own subdirectories.")
                End If

                EnsureNameAvailable(DestinationParent, TargetName)

                Using Scope = ChunkedStream.DeferPublish()
                    RemoveEntry(Location)
                    WriteInt64(Child.Offset + ParentOffset, DestinationParent.AnchorId)
                    AppendEntry(DestinationParent, New ContentListEntry(Location.Entry.EntryType, AnchorId,
                                                                        Location.Entry.LengthOfDataAtEntry, TargetName))
                    Scope.Publish()
                End Using
            End SyncLock
        End Sub

        ''' <summary>
        ''' Copies an entry - recursively, for a directory - into another directory, optionally under a
        ''' new name; the source's current directory and name are resolved from <paramref name="AnchorId" />
        ''' itself (see <see cref="GetParentAnchorId" />). The copy shares the source's physical bytes via
        ''' <see cref="ChunkedStream.Clone" /> (copy-on-write) rather than duplicating them, so it is cheap
        ''' regardless of size; a later write to either copy allocates its own storage the normal way. The
        ''' clone is always a plain <see cref="EntryTypes.File" /> or <see cref="EntryTypes.Directory" /> -
        ''' a <see cref="EntryTypes.PendingFile" /> source clones whatever has been durably published so
        ''' far. Cloning a <see cref="EntryTypes.CorruptFile" />, <see cref="EntryTypes.CorruptDirectory" />
        ''' or <see cref="EntryTypes.PartlyRecoveredFile" /> entry - anywhere in the subtree, for a directory -
        ''' is refused, since the copy would silently carry the same damage forward as if it were ordinary
        ''' data.
        ''' </summary>
        ''' <returns>The anchor id of the new top-level clone.</returns>
        Public Function Clone(AnchorId As Long, DestinationParentDirectoryAnchorId As Long, Optional NewName As String = Nothing) As Long
            SyncLock _SyncRoot
                ThrowIfDisposed()
                Dim SourceAnchor = GetAnchor(AnchorId)
                Dim Location = GetParentEntry(SourceAnchor)
                Dim TargetName = If(NewName, Location.Entry.Name)
                ValidateName(TargetName)

                Dim DestinationParent = GetDirectory(DestinationParentDirectoryAnchorId)
                EnsureNameAvailable(DestinationParent, TargetName)

                Using Scope = ChunkedStream.DeferPublish()
                    Dim NewAnchorId = CloneEntry(SourceAnchor, Location.Entry.EntryType,
                                                 Location.Entry.LengthOfDataAtEntry, DestinationParent, TargetName)
                    Scope.Publish()
                    Return NewAnchorId
                End Using
            End SyncLock
        End Function

        ''' <summary>
        ''' Clones one entry into <paramref name="NewParent" /> as <paramref name="Name" />, recursing
        ''' into a directory's children. For a directory, <see cref="ReadEntries" /> is snapshotted
        ''' before this clone's own entry is registered in <paramref name="NewParent" /> - if the caller
        ''' is cloning a directory directly into itself, <paramref name="NewParent" /> and
        ''' <paramref name="SourceAnchor" /> are the same anchor, and appending first would make the new
        ''' entry show up in that same read, recursing into cloning itself.
        ''' </summary>
        Private Function CloneEntry(SourceAnchor As ChunkedStream.Anchor, SourceEntryType As EntryTypes, SourceLength As Long,
                                    NewParent As ChunkedStream.Anchor, Name As String) As Long
            If SourceEntryType = EntryTypes.CorruptFile OrElse SourceEntryType = EntryTypes.CorruptDirectory OrElse
               SourceEntryType = EntryTypes.PartlyRecoveredFile Then
                Throw New InvalidOperationException($"'{Name}' is flagged {SourceEntryType} and cannot be cloned.")
            End If

            If SourceEntryType = EntryTypes.Directory Then
                Dim Children = ReadEntries(SourceAnchor)
                Dim NewDirectory = ChunkedStream.CreateAnchor(BuildDirectory(NewParent.AnchorId))
                AppendEntry(NewParent, New ContentListEntry(EntryTypes.Directory, NewDirectory.AnchorId, DirectoryHeaderSize, Name))
                For Each Child In Children
                    CloneEntry(GetAnchor(Child.ChildAnchorId), Child.EntryType, Child.LengthOfDataAtEntry, NewDirectory, Child.Name)
                Next
                Return NewDirectory.AnchorId
            End If

            ' File or PendingFile: the clone is always a complete, ordinary File - nothing is being
            ' written to it, so "pending" cannot apply.
            Dim NewFile = ChunkedStream.CreateAnchor(BuildFile(NewParent.AnchorId))
            If SourceLength > 0 Then ChunkedStream.Clone(SourceAnchor.Offset + FileHeaderSize, SourceLength, ChunkedStream.Length)
            AppendEntry(NewParent, New ContentListEntry(EntryTypes.File, NewFile.AnchorId, SourceLength, Name))
            Return NewFile.AnchorId
        End Function

        ''' <summary>True when <paramref name="AnchorId" /> is <paramref name="CandidateAncestorAnchorId" />
        ''' itself, or lies anywhere beneath it - used to stop a directory being moved into itself or one
        ''' of its own subdirectories.</summary>
        Private Function IsSameOrAncestor(CandidateAncestorAnchorId As Long, AnchorId As Long) As Boolean
            Dim Current = AnchorId
            Dim Guard As New HashSet(Of Long)()
            While Current > 0 AndAlso Guard.Add(Current)
                If Current = CandidateAncestorAnchorId Then Return True
                If Current = _Root.AnchorId Then Return False
                Current = ReadParentIdOrZero(Current)
            End While
            Return False
        End Function

        ''' <summary>
        ''' Applies <paramref name="Action" /> to every pending or corrupt entry, and to every
        ''' record the root can no longer reach, returning a description of every record acted on.
        ''' Pass <see cref="PendingFileRecoveryActions.List" /> to enumerate them without changing
        ''' anything. A <see cref="EntryTypes.CorruptDirectory" /> entry - and an unreferenced
        ''' directory whose own content list is unreadable - is only acted on by
        ''' <see cref="PendingFileRecoveryActions.Remove" /> or
        ''' <see cref="PendingFileRecoveryActions.List" />, so the default
        ''' <see cref="PendingFileRecoveryActions.Finalize" /> leaves it untouched; a
        ''' <see cref="EntryTypes.PartlyRecoveredFile" /> entry is finalised into a plain
        ''' <see cref="EntryTypes.File" /> instead, same as <see cref="EntryTypes.CorruptFile" />.
        ''' </summary>
        ''' <remarks>This maintenance operation traverses the directory tree and scans every anchor.</remarks>
        Public Function RecoverPendingFiles(Optional Action As PendingFileRecoveryActions = PendingFileRecoveryActions.Finalize) As IReadOnlyList(Of PendingFileRecoveryResult)
            Return RecoverPendingFiles(Function(candidate) Action)
        End Function

        ''' <summary>
        ''' Recovers each pending, corrupt or unreferenced record with the action
        ''' <paramref name="Selector" /> returns for it, so the decision is per record, e.g.
        ''' <c>RecoverPendingFiles(Function(c) If(c.Conditions.HasFlag(RecoveryConditions.Pending), PendingFileRecoveryActions.Finalize, PendingFileRecoveryActions.Remove))</c>.
        ''' <see cref="PendingFileRecoveryActions.None" /> skips a record entirely;
        ''' <see cref="PendingFileRecoveryActions.List" /> includes it in the result without
        ''' changing it. Returns a description of every record that was not skipped.
        ''' </summary>
        ''' <remarks>
        ''' Records are offered in two groups: reachable pending / corrupt entries first (walked
        ''' from the root), then every anchor the tree does not reach - ordered parent-before-child.
        ''' A <see cref="RecoveryConditions.Orphaned" /> / <see cref="RecoveryConditions.Unreachable" />
        ''' record is <see cref="PendingFileRecoveryActions.Remove" />d (its record dropped; its
        ''' children fall through and are offered in turn) or
        ''' <see cref="PendingFileRecoveryActions.Finalize" />d (re-homed under <c>\_Recovered</c>,
        ''' its readable subtree following with real names). A
        ''' <see cref="EntryTypes.CorruptDirectory" /> entry cannot be finalised, so
        ''' <see cref="PendingFileRecoveryActions.Finalize" /> is a skip for one; a
        ''' <see cref="EntryTypes.PartlyRecoveredFile" /> entry, unlike a
        ''' <see cref="EntryTypes.CorruptDirectory" /> one, IS finalised - into a plain
        ''' <see cref="EntryTypes.File" />, clearing the marker.
        ''' </remarks>
        Public Function RecoverPendingFiles(Selector As Func(Of PendingFileRecoveryCandidate, PendingFileRecoveryActions)) As IReadOnlyList(Of PendingFileRecoveryResult)
            If Selector Is Nothing Then Throw New ArgumentNullException(NameOf(Selector))

            SyncLock _SyncRoot
                ThrowIfDisposed()
                Using Scope = ChunkedStream.DeferPublish()

                    '
                    ' Enumerate every candidate first, describing each (including its zeroed-byte
                    ' count) against the un-mutated stream, then act on the selected ones by
                    ' re-locating them via their stable anchor id - a Remove shifts the logical
                    ' offsets every later lookup and the sparse map depend on.
                    '
                    Dim Sparse As New SparseByteMap(Me)
                    Dim Reachable As New HashSet(Of Long)()
                    Dim EntryCandidates As New List(Of PendingFileRecoveryCandidate)()
                    CollectPendingCandidates(_Root, "", Sparse, New HashSet(Of Long)(), Reachable, EntryCandidates)

                    Dim OrphanCandidates As New List(Of PendingFileRecoveryCandidate)()
                    CollectOrphanCandidates(Reachable, Sparse, OrphanCandidates)

                    Dim Results As New List(Of PendingFileRecoveryResult)()

                    For Each Candidate In EntryCandidates

                        Dim Action = ValidateSelectorAction(Selector, Candidate)
                        If Action = PendingFileRecoveryActions.None Then Continue For

                        If _OpenFileIds.Contains(Candidate.AnchorId) Then Continue For
                        If ChunkedStream.ContainsAnchor(Candidate.AnchorId) = False Then Continue For

                        Dim Location As EntryLocation
                        If Candidate.State = EntryTypes.CorruptDirectory Then
                            ' Re-locate through the parent's content list - the directory record's
                            ' own header may be part of what is unreadable.
                            Location = FindByChildId(GetDirectory(Candidate.ParentDirectoryAnchorId), Candidate.AnchorId)
                        Else
                            Location = GetParentEntry(GetAnchor(Candidate.AnchorId))
                        End If

                        If Location.Entry.EntryType <> EntryTypes.PendingFile AndAlso
                           Location.Entry.EntryType <> EntryTypes.CorruptFile AndAlso
                           Location.Entry.EntryType <> EntryTypes.CorruptDirectory AndAlso
                           Location.Entry.EntryType <> EntryTypes.PartlyRecoveredFile Then Continue For

                        ' A corrupt directory has nothing to promote to a file, so Finalize is a
                        ' skip for one. A PartlyRecoveredFile has somewhere to go, unlike
                        ' CorruptDirectory - Finalize promotes it to a plain File below, same as
                        ' CorruptFile/PendingFile, clearing the marker (and any rename stays as-is,
                        ' since Finalize never touches the name).
                        If Location.Entry.EntryType = EntryTypes.CorruptDirectory AndAlso
                           Action <> PendingFileRecoveryActions.Remove AndAlso Action <> PendingFileRecoveryActions.List Then Continue For

                        Results.Add(New PendingFileRecoveryResult(Candidate.Path, Candidate.AnchorId,
                                                                  Location.Entry.EntryType, Candidate.Conditions, Candidate.IsDirectory,
                                                                  Action, Location.Entry.LengthOfDataAtEntry, Candidate.BytesZeroed, Nothing))

                        Select Case Action
                            Case PendingFileRecoveryActions.Finalize
                                SetEntryType(Location, EntryTypes.File)
                            Case PendingFileRecoveryActions.Remove
                                DeleteCore(Location)
                        End Select

                    Next

                    ProcessUnreferencedRecords(OrphanCandidates, Selector, Results)

                    Scope.Publish()
                    Return New ReadOnlyCollection(Of PendingFileRecoveryResult)(Results)
                End Using
            End SyncLock
        End Function

        Private Shared Function ValidateSelectorAction(Selector As Func(Of PendingFileRecoveryCandidate, PendingFileRecoveryActions),
                                                       Candidate As PendingFileRecoveryCandidate) As PendingFileRecoveryActions
            Dim Action = Selector(Candidate)
            If Action <> PendingFileRecoveryActions.None AndAlso
               Action <> PendingFileRecoveryActions.List AndAlso
               Action <> PendingFileRecoveryActions.Finalize AndAlso
               Action <> PendingFileRecoveryActions.Remove Then
                Throw New ArgumentOutOfRangeException(NameOf(Selector),
                    $"The selector returned an unsupported action ({Action}) for '{Candidate.Path}'.")
            End If
            Return Action
        End Function

        '
        ' Second recovery phase: every anchor the root walk did not reach, offered parent-first.
        ' Each candidate's condition is re-derived at its turn because an earlier Remove or Finalize
        ' in this same loop may have changed its parent.
        '
        Private Sub ProcessUnreferencedRecords(Candidates As List(Of PendingFileRecoveryCandidate),
                                               Selector As Func(Of PendingFileRecoveryCandidate, PendingFileRecoveryActions),
                                               Results As List(Of PendingFileRecoveryResult))
            For Each Candidate In Candidates

                If ChunkedStream.ContainsAnchor(Candidate.AnchorId) = False Then Continue For
                If _OpenFileIds.Contains(Candidate.AnchorId) Then Continue For
                If IsReachableFromRoot(Candidate.AnchorId) Then Continue For

                Dim Current As New PendingFileRecoveryCandidate(Candidate.Path, Candidate.AnchorId, Candidate.ParentDirectoryAnchorId,
                                                                Candidate.State, Candidate.IsDirectory,
                                                                ReclassifyUnreferenced(Candidate), Candidate.DataLength, Candidate.BytesZeroed)

                Dim Action = ValidateSelectorAction(Selector, Current)
                If Action = PendingFileRecoveryActions.None Then Continue For

                ' A directory whose own content list is unreadable cannot be re-homed - its children
                ' would look reachable but could never be enumerated. Remove it (its children were
                ' classified as orphan roots and are offered in turn) or list it.
                If Action = PendingFileRecoveryActions.Finalize AndAlso Current.IsDirectory AndAlso
                   Current.Conditions.HasFlag(RecoveryConditions.CorruptData) Then Continue For

                Dim RecoveredPath As String = Nothing
                Select Case Action
                    Case PendingFileRecoveryActions.Finalize
                        RecoveredPath = RehomeUnreferencedRecord(Current)
                    Case PendingFileRecoveryActions.Remove
                        RemoveUnreferencedRecord(Current)
                End Select

                Results.Add(New PendingFileRecoveryResult(Current.Path, Current.AnchorId, Current.State, Current.Conditions,
                                                          Current.IsDirectory, Action, Current.DataLength, Current.BytesZeroed, RecoveredPath))
            Next
        End Sub

        '
        ' Lazily builds a list of the stream's sparse (zero-filled) logical ranges from one
        ' GetStructure() call, so RecoverPendingFiles pays that cost once and only when there
        ' is actually a pending or corrupt entry to describe.
        '
        Private NotInheritable Class SparseByteMap
            Private ReadOnly _Owner As EmbeddedFileSystem
            Private _Ranges As List(Of KeyValuePair(Of Long, Long))

            Public Sub New(Owner As EmbeddedFileSystem)
                _Owner = Owner
            End Sub

            Public Function BytesIn(Offset As Long, Length As Long) As Long
                If _Ranges Is Nothing Then
                    _Ranges = New List(Of KeyValuePair(Of Long, Long))()
                    For Each Chunk In _Owner.ChunkedStream.GetStructure().Chunks
                        If Chunk.IsSparse Then _Ranges.Add(New KeyValuePair(Of Long, Long)(Chunk.LogicalOffset, Chunk.LogicalEndOffset - Chunk.LogicalOffset))
                    Next
                End If

                Dim Total As Long = 0
                For Each Range In _Ranges
                    Dim OverlapStart = Math.Max(Range.Key, Offset)
                    Dim OverlapEnd = Math.Min(Range.Key + Range.Value, Offset + Length)
                    If OverlapEnd > OverlapStart Then Total += OverlapEnd - OverlapStart
                Next
                Return Total
            End Function
        End Class

        ''' <summary>
        ''' Flags every file whose logical range intersects a problem reported by
        ''' <see cref="ChunkedStream.Validate" /> with <see cref="EntryTypes.CorruptFile" /> (none of
        ''' its data can be trusted) or <see cref="EntryTypes.PartlyRecoveredFile" /> (some of it survives),
        ''' and every directory whose own content list intersects one - or cannot be parsed - with
        ''' <see cref="EntryTypes.CorruptDirectory" />, so the damage is recorded before the chunked
        ''' stream is repaired (which zero-fills the ranges and clears the problem). A flagged
        ''' directory is not descended into, so the rest of the tree is still walked and marked.
        ''' Returns a description of every entry marked; <see cref="CorruptEntryMark.IsDirectory" />
        ''' distinguishes a flagged directory from a file (either kind). Call this before
        ''' <see cref="ChunkedStream.ValidationReport.Repair" />.
        ''' </summary>
        ''' <param name="Report">The validation report describing the damage to flag ahead of.</param>
        ''' <param name="RenamePartlyRecoveredFile">
        ''' When <see langword="True" />, a file newly flagged <see cref="EntryTypes.PartlyRecoveredFile" />
        ''' by this call (not one that already was) is also renamed to make the partial loss visible
        ''' in its own name: <c>"name.ext"</c> becomes <c>"name.recovered.ext"</c>, or
        ''' <c>"name.recovered2.ext"</c>, <c>"name.recovered3.ext"</c>, ... if that name is already
        ''' taken, skipping the rename (but not the flag) if every name up to the 256-character limit
        ''' is unavailable. <see cref="CorruptEntryMark.RenamedTo" /> reports the result per entry.
        ''' </param>
        ''' <remarks>
        ''' Still throws when the stream cannot be read at all for a reason a repair cannot address,
        ''' such as a missing file master key. A <see cref="EntryTypes.CorruptDirectory" /> entry is
        ''' removed by <see cref="DeleteEntry" /> or <see cref="RecoverPendingFiles" />.
        ''' <see cref="EntryTypes.PartlyRecoveredFile" /> is a marker this method never reassigns
        ''' once set (re-marking just re-reports it) - it is <see cref="RecoverPendingFiles" /> that
        ''' eventually clears it, promoting it to a plain <see cref="EntryTypes.File" /> under its
        ''' default (Finalize) action, same as <see cref="EntryTypes.CorruptFile" />.
        ''' </remarks>
        Public Function Mark(Report As ChunkedStream.ValidationReport,
                             Optional RenamePartlyRecoveredFile As Boolean = False) As IReadOnlyList(Of CorruptEntryMark)
            If Report Is Nothing Then Throw New ArgumentNullException(NameOf(Report))
            SyncLock _SyncRoot
                ThrowIfDisposed()

                Dim Ranges = Report.Problems.
                                    SelectMany(Function(problem) problem.AffectedRanges).
                                    Where(Function(range) range.Length > 0).
                                    ToList()

                If Ranges.Count = 0 Then Return New ReadOnlyCollection(Of CorruptEntryMark)(New List(Of CorruptEntryMark)())

                Using Scope = ChunkedStream.DeferPublish()
                    Dim Marks As New List(Of CorruptEntryMark)()
                    Try
                        MarkCorrupt(_Root, "", Ranges, New HashSet(Of Long)(), Marks, RenamePartlyRecoveredFile)
                    Catch ex As Exception When IsCorruptionFault(ex)
                        '
                        ' The root's own content list is unreadable, so there is no parent entry to
                        ' re-type and nothing else can be walked. Report it anyway so the caller
                        ' still gets a result and can proceed to Repair rather than an abort.
                        '
                        Dim RootRecordLength As Long
                        If TryReadDirectoryRecordLength(_Root, RootRecordLength) = False Then RootRecordLength = DirectoryHeaderSize
                        Marks.Add(New CorruptEntryMark("\", _Root.AnchorId, RangeOverlap(Ranges, _Root.Offset, RootRecordLength), IsDirectory:=True))
                    End Try
                    '
                    ' This publish only retypes the entries just marked - it says nothing
                    ' about the rest of the damage Report describes. Some of THAT damage
                    ' (an extent still pointing at a physical record Report already knows is
                    ' missing) can easily share a metadata page with one of the entries just
                    ' retyped, e.g. two files whose directory extents happen to land on the
                    ' same page - dirtying that page for the retype would otherwise make the
                    ' publish's self-check trip over a dangling reference this call was never
                    ' trying to fix, before Repair(IncludeDataLoss) ever gets a chance to.
                    '
                    ChunkedStream.RunAllowingDanglingExtentReferences(Sub() Scope.Publish())
                    Return New ReadOnlyCollection(Of CorruptEntryMark)(Marks)
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

                If Location.Entry.EntryType = EntryTypes.CorruptDirectory Then
                    '
                    ' The content list is unreadable, so the children cannot be enumerated to be
                    ' removed one by one. Drop the entry and reclaim the directory record's own
                    ' span - run Repair first so the span reads back as zeros. The now-unreferenced
                    ' descendant records are picked up by RecoverPendingFiles (which scans every
                    ' anchor) and removed or re-homed under \_Recovered.
                    '
                    Dim CorruptChild = GetAnchor(Location.Entry.ChildAnchorId)
                    Dim ClaimedLength = Math.Max(Location.Entry.LengthOfDataAtEntry, CLng(DirectoryHeaderSize))
                    RemoveEntry(Location)
                    ChunkedStream.Remove(CorruptChild.Offset, Math.Min(ClaimedLength, ChunkedStream.Length - CorruptChild.Offset))
                    Scope.Publish()
                    Return
                End If

                Dim Child As ChunkedStream.Anchor = Nothing
                Dim ChildType As DataType
                Try
                    Child = GetAnchor(Location.Entry.ChildAnchorId)
                    ChildType = ReadType(Child)
                Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                    '
                    ' The child anchor is gone, or its record's own DataType tag cannot be trusted -
                    ' most often because a prior Repair(IncludeDataLoss) zero-filled an unreadable
                    ' range that happened to reach the record's own header, which Mark() has no way
                    ' to avoid since it operates below the EFS layer. Whether or not this entry was
                    ' ever flagged CorruptFile / CorruptDirectory, we can no longer tell file from
                    ' directory here, so take the same remedy as an already-flagged CorruptDirectory:
                    ' drop the entry and reclaim whatever span we can still identify. Any descendants
                    ' this orphans are picked up by RecoverPendingFiles, which scans every anchor.
                    '
                    RemoveEntry(Location)
                    If TypeOf ex IsNot KeyNotFoundException Then
                        Dim ClaimedLength = Math.Max(FileHeaderSize + Location.Entry.LengthOfDataAtEntry, CLng(FileHeaderSize))
                        ChunkedStream.Remove(Child.Offset, Math.Min(ClaimedLength, ChunkedStream.Length - Child.Offset))
                    End If
                    Scope.Publish()
                    Return
                End Try
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

        Private Sub CollectPendingCandidates(DirectoryAnchor As ChunkedStream.Anchor, PathPrefix As String,
                                             Sparse As SparseByteMap, Visited As HashSet(Of Long),
                                             Reachable As HashSet(Of Long), Candidates As List(Of PendingFileRecoveryCandidate))
            If Visited.Add(DirectoryAnchor.AnchorId) = False Then Throw New InvalidDataException("Directory cycle detected.")
            Reachable.Add(DirectoryAnchor.AnchorId)
            For Each Entry In ReadEntries(DirectoryAnchor)
                Dim EntryPath = PathPrefix & "\" & Entry.Name
                Reachable.Add(Entry.ChildAnchorId)
                If Entry.EntryType = EntryTypes.Directory Then
                    CollectPendingCandidates(GetDirectory(Entry.ChildAnchorId), EntryPath, Sparse, Visited, Reachable, Candidates)
                ElseIf Entry.EntryType = EntryTypes.CorruptDirectory Then
                    ' The content list is unreadable, so the subtree cannot be walked. Offer the
                    ' directory itself for removal but do not descend into it.
                    Dim RecordOffset = GetAnchor(Entry.ChildAnchorId).Offset
                    Dim RecordLength = Math.Max(Entry.LengthOfDataAtEntry, CLng(DirectoryHeaderSize))
                    Candidates.Add(New PendingFileRecoveryCandidate(
                        EntryPath, Entry.ChildAnchorId, DirectoryAnchor.AnchorId, Entry.EntryType, IsDirectory:=True,
                        Conditions:=RecoveryConditions.CorruptData, DataLength:=RecordLength,
                        BytesZeroed:=Sparse.BytesIn(RecordOffset, RecordLength)))
                ElseIf (Entry.EntryType = EntryTypes.PendingFile OrElse Entry.EntryType = EntryTypes.CorruptFile OrElse
                        Entry.EntryType = EntryTypes.PartlyRecoveredFile) AndAlso
                       _OpenFileIds.Contains(Entry.ChildAnchorId) = False Then
                    Dim DataOffset = GetAnchor(Entry.ChildAnchorId).Offset + FileHeaderSize
                    Dim Conditions = If(Entry.EntryType = EntryTypes.PendingFile, RecoveryConditions.Pending, RecoveryConditions.CorruptData)
                    Candidates.Add(New PendingFileRecoveryCandidate(
                        EntryPath, Entry.ChildAnchorId, DirectoryAnchor.AnchorId, Entry.EntryType, IsDirectory:=False,
                        Conditions:=Conditions, DataLength:=Entry.LengthOfDataAtEntry,
                        BytesZeroed:=Sparse.BytesIn(DataOffset, Entry.LengthOfDataAtEntry)))
                End If
            Next
            Visited.Remove(DirectoryAnchor.AnchorId)
        End Sub

        '
        ' Every anchor the root walk did not reach, built into candidates ordered parent-before-child
        ' (so a Remove/Finalize of a parent is visible when its children come up). Spans, and the
        ' zeroed-byte counts, are read here against the un-mutated stream.
        '
        Private Sub CollectOrphanCandidates(Reachable As HashSet(Of Long), Sparse As SparseByteMap,
                                            Candidates As List(Of PendingFileRecoveryCandidate))

            Dim Anchors = ChunkedStream.GetAnchors()
            If Anchors.All(Function(anchor) Reachable.Contains(anchor.AnchorId)) Then Return
            Dim Unreachable = Anchors.Where(Function(anchor) Reachable.Contains(anchor.AnchorId) = False).ToList()

            ' EFS records are contiguous in logical order, so a record spans the gap to the next anchor.
            Dim ByOffset = Anchors.OrderBy(Function(anchor) anchor.Offset).ToList()
            Dim SpanById As New Dictionary(Of Long, Long)()
            For Index = 0 To ByOffset.Count - 1
                Dim EndOffset = If(Index + 1 < ByOffset.Count, ByOffset(Index + 1).Offset, ChunkedStream.Length)
                SpanById(ByOffset(Index).AnchorId) = EndOffset - ByOffset(Index).Offset
            Next

            Dim UnreachableIds As New HashSet(Of Long)(Unreachable.Select(Function(anchor) anchor.AnchorId))
            Dim ParentOf As New Dictionary(Of Long, Long)()
            Dim IsDir As New Dictionary(Of Long, Boolean)()
            Dim HeaderOk As New Dictionary(Of Long, Boolean)()
            Dim ListReadable As New Dictionary(Of Long, Boolean)()
            For Each Anchor In Unreachable
                Dim DataTypeValue As Long, ParentId As Long
                Dim Ok = TryReadRecordHeader(Anchor, DataTypeValue, ParentId)
                HeaderOk(Anchor.AnchorId) = Ok
                ParentOf(Anchor.AnchorId) = ParentId
                IsDir(Anchor.AnchorId) = (DataTypeValue = CLng(DataType.Directory))
                ListReadable(Anchor.AnchorId) = Ok AndAlso IsDir(Anchor.AnchorId) AndAlso IsReadableDirectory(Anchor.AnchorId)
            Next

            ' A descendant is an unreachable record whose parent is an unreachable directory whose
            ' own content list can still be read; every other unreachable record is an orphan root -
            ' its own parent link is the break.
            Dim IsDescendant As Func(Of Long, Boolean) =
                Function(node) UnreachableIds.Contains(ParentOf(node)) AndAlso ListReadable(ParentOf(node))

            Dim ChildrenByParent = UnreachableIds.Where(Function(node) IsDescendant(node)).
                                                 GroupBy(Function(node) ParentOf(node)).
                                                 ToDictionary(Function(g) g.Key, Function(g) g.OrderBy(Function(child) child).ToList())

            Dim OrderedIds As New List(Of Long)()
            Dim Seen As New HashSet(Of Long)()
            Dim Queue As New Queue(Of Long)()
            For Each Id In UnreachableIds.OrderBy(Function(node) node).Where(Function(node) IsDescendant(node) = False)
                Queue.Enqueue(Id)
                Seen.Add(Id)
            Next
            While Queue.Count > 0
                Dim Id = Queue.Dequeue()
                OrderedIds.Add(Id)
                Dim Kids As List(Of Long) = Nothing
                If ChildrenByParent.TryGetValue(Id, Kids) Then
                    For Each ChildId In Kids
                        If Seen.Add(ChildId) Then Queue.Enqueue(ChildId)
                    Next
                End If
            End While
            ' Anything left is in a parent cycle - treat each as its own root.
            For Each Id In UnreachableIds.OrderBy(Function(node) node)
                If Seen.Add(Id) Then OrderedIds.Add(Id)
            Next

            Dim PathById As New Dictionary(Of Long, String)()
            For Each Id In OrderedIds
                Dim Anchor = GetAnchor(Id)
                Dim ParentId = ParentOf(Id)
                Dim Directory = IsDir(Id)
                Dim HeaderSize = If(Directory, CLng(DirectoryHeaderSize), CLng(FileHeaderSize))
                Dim DataLength = If(Directory, SpanById(Id), Math.Max(0L, SpanById(Id) - HeaderSize))
                Dim BytesZeroed = Sparse.BytesIn(Anchor.Offset + HeaderSize, DataLength)

                Dim Conditions = RecoveryConditions.Unreachable
                If IsDescendant(Id) = False Then Conditions = Conditions Or RecoveryConditions.Orphaned
                If BytesZeroed > 0 OrElse HeaderOk(Id) = False OrElse (Directory AndAlso ListReadable(Id) = False) Then
                    Conditions = Conditions Or RecoveryConditions.CorruptData
                End If

                Dim ParentPath As String = Nothing
                Dim NodePath = $"\?{Id}"
                If PathById.TryGetValue(ParentId, ParentPath) Then
                    Dim Name = TryReadChildName(ParentId, Id)
                    NodePath = ParentPath & "\" & If(Name, $"?{Id}")
                End If
                PathById(Id) = NodePath

                Candidates.Add(New PendingFileRecoveryCandidate(
                    NodePath, Id, ParentId, If(Directory, EntryTypes.Directory, EntryTypes.File),
                    Directory, Conditions, DataLength, BytesZeroed))
            Next
        End Sub

        Private Function TryReadRecordHeader(Anchor As ChunkedStream.Anchor, ByRef DataTypeValue As Long, ByRef ParentId As Long) As Boolean
            Try
                DataTypeValue = ReadInt64(Anchor.Offset)
                ParentId = ReadInt64(Anchor.Offset + ParentOffset)
                Return True
            Catch ex As Exception When IsCorruptionFault(ex)
                DataTypeValue = 0
                ParentId = 0
                Return False
            End Try
        End Function

        Private Function TryReadChildName(ParentAnchorId As Long, ChildAnchorId As Long) As String
            Try
                Return ReadEntries(GetAnchor(ParentAnchorId)).
                       Where(Function(entry) entry.ChildAnchorId = ChildAnchorId).
                       Select(Function(entry) entry.Name).
                       FirstOrDefault()
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                Return Nothing
            End Try
        End Function

        Private Function ResolvesToDirectory(AnchorId As Long) As Boolean
            If AnchorId <= 0 OrElse ChunkedStream.ContainsAnchor(AnchorId) = False Then Return False
            Try
                Return ReadType(GetAnchor(AnchorId)) = DataType.Directory
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                Return False
            End Try
        End Function

        Private Function IsReadableDirectory(AnchorId As Long) As Boolean
            Try
                ReadEntries(GetAnchor(AnchorId))
                Return True
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                Return False
            End Try
        End Function

        Private Function ReadParentIdOrZero(AnchorId As Long) As Long
            Try
                Return ReadInt64(GetAnchor(AnchorId).Offset + ParentOffset)
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                Return 0
            End Try
        End Function

        Private Function IsReachableFromRoot(AnchorId As Long) As Boolean
            Dim Current = AnchorId
            Dim Guard As New HashSet(Of Long)()
            While Current > 0 AndAlso Guard.Add(Current)
                If Current = _Root.AnchorId Then Return True
                If ChunkedStream.ContainsAnchor(Current) = False Then Return False
                Current = ReadParentIdOrZero(Current)
            End While
            Return False
        End Function

        Private Function ReclassifyUnreferenced(Candidate As PendingFileRecoveryCandidate) As RecoveryConditions
            Dim Conditions = RecoveryConditions.Unreachable
            If ResolvesToDirectory(ReadParentIdOrZero(Candidate.AnchorId)) = False Then Conditions = Conditions Or RecoveryConditions.Orphaned
            If Candidate.Conditions.HasFlag(RecoveryConditions.CorruptData) Then Conditions = Conditions Or RecoveryConditions.CorruptData
            Return Conditions
        End Function

        Private Sub RemoveUnreferencedRecord(Candidate As PendingFileRecoveryCandidate)
            Dim Anchor = GetAnchor(Candidate.AnchorId)
            Dim HeaderSize = If(Candidate.IsDirectory, CLng(DirectoryHeaderSize), CLng(FileHeaderSize))
            Dim Span = If(Candidate.IsDirectory, Candidate.DataLength, HeaderSize + Candidate.DataLength)
            ChunkedStream.Remove(Anchor.Offset, Math.Min(Span, ChunkedStream.Length - Anchor.Offset))
        End Sub

        '
        ' Re-homes an unreferenced record under \_Recovered by adding one entry there and rewriting
        ' the record's own parent pointer. A readable directory's subtree follows untouched, its
        ' descendants becoming reachable and dropping out of the recovery loop.
        '
        Private Function RehomeUnreferencedRecord(Candidate As PendingFileRecoveryCandidate) As String
            Dim RecoveredAnchorId = EnsureRecoveredFolder()
            Dim Name = AllocateRecoveredName(RecoveredAnchorId, Candidate.IsDirectory)

            ' A directory only reaches here with a readable content list (see ProcessUnreferencedRecords).
            Dim EntryState As EntryTypes
            If Candidate.IsDirectory Then
                EntryState = EntryTypes.Directory
            ElseIf Candidate.Conditions.HasFlag(RecoveryConditions.CorruptData) Then
                EntryState = EntryTypes.CorruptFile
            Else
                EntryState = EntryTypes.File
            End If

            AppendEntry(GetAnchor(RecoveredAnchorId), New ContentListEntry(EntryState, Candidate.AnchorId, Candidate.DataLength, Name))
            WriteInt64(GetAnchor(Candidate.AnchorId).Offset + ParentOffset, RecoveredAnchorId)
            Return "\" & RecoveredFolderName & "\" & Name
        End Function

        Private Function EnsureRecoveredFolder() As Long
            If _RecoveredAnchorId > 0 AndAlso ChunkedStream.ContainsAnchor(_RecoveredAnchorId) Then Return _RecoveredAnchorId
            Try
                _RecoveredAnchorId = FindByName(_Root, RecoveredFolderName).Entry.ChildAnchorId
            Catch ex As FileNotFoundException
                Dim Data = BuildDirectory(_Root.AnchorId)
                Dim Child = ChunkedStream.CreateAnchor(Data)
                AppendEntry(_Root, New ContentListEntry(EntryTypes.Directory, Child.AnchorId, Data.LongLength, RecoveredFolderName))
                _RecoveredAnchorId = Child.AnchorId
            End Try
            Return _RecoveredAnchorId
        End Function

        Private Function AllocateRecoveredName(RecoveredAnchorId As Long, IsDirectory As Boolean) As String
            Dim Prefix = If(IsDirectory, "Directory", "File")
            Dim Highest = 0
            For Each Entry In ReadEntries(GetAnchor(RecoveredAnchorId))
                If Entry.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) Then
                    Dim Suffix = 0
                    If Integer.TryParse(Entry.Name.Substring(Prefix.Length), Suffix) AndAlso Suffix > Highest Then Highest = Suffix
                End If
            Next
            Return Prefix & (Highest + 1)
        End Function

        Private Sub MarkCorrupt(DirectoryAnchor As ChunkedStream.Anchor, PathPrefix As String,
                                Ranges As List(Of ChunkedStream.LogicalRange), Visited As HashSet(Of Long),
                                Marks As List(Of CorruptEntryMark), RenamePartlyRecoveredFile As Boolean)
            If Visited.Add(DirectoryAnchor.AnchorId) = False Then Throw New InvalidDataException("Directory cycle detected.")
            Try
                For Each Entry In ReadEntries(DirectoryAnchor)
                    Dim EntryPath = PathPrefix & "\" & Entry.Name
                    If Entry.EntryType = EntryTypes.Directory OrElse Entry.EntryType = EntryTypes.CorruptDirectory Then
                        MarkCorruptChildDirectory(DirectoryAnchor, Entry, EntryPath, Ranges, Visited, Marks, RenamePartlyRecoveredFile)
                    Else
                        MarkCorruptChildFile(DirectoryAnchor, Entry, EntryPath, Ranges, Marks, RenamePartlyRecoveredFile)
                    End If
                Next
            Finally
                Visited.Remove(DirectoryAnchor.AnchorId)
            End Try
        End Sub

        '
        ' Walks a child directory. When its own content list intersects a problem range, cannot be
        ' resolved or cannot be parsed, the entry is flagged CorruptDirectory and the walk stops
        ' descending - the rest of the tree is still marked. An already-flagged entry is re-reported
        ' without being changed.
        '
        Private Sub MarkCorruptChildDirectory(ParentAnchor As ChunkedStream.Anchor, Entry As ContentListEntry,
                                              EntryPath As String, Ranges As List(Of ChunkedStream.LogicalRange),
                                              Visited As HashSet(Of Long), Marks As List(Of CorruptEntryMark),
                                              RenamePartlyRecoveredFile As Boolean)

            ' A directory entry keeps the whole record length in LengthOfDataAtEntry; floor it at the
            ' header size in case that cached length is itself stale.
            Dim RecordLength = Math.Max(Entry.LengthOfDataAtEntry, CLng(DirectoryHeaderSize))

            Dim RecordOffset As Long
            Try
                RecordOffset = GetAnchor(Entry.ChildAnchorId).Offset
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                FlagDirectoryCorrupt(ParentAnchor, Entry, EntryPath, 0, Marks)
                Return
            End Try

            Dim LostBytes = RangeOverlap(Ranges, RecordOffset, RecordLength)

            If Entry.EntryType = EntryTypes.CorruptDirectory Then
                Marks.Add(New CorruptEntryMark(EntryPath, Entry.ChildAnchorId, LostBytes, IsDirectory:=True))
                Return
            End If

            If LostBytes > 0 Then
                FlagDirectoryCorrupt(ParentAnchor, Entry, EntryPath, LostBytes, Marks)
                Return
            End If

            '
            ' The index looks intact. Descend, but treat a read failure inside the subtree's own
            ' content list as damage to this directory rather than letting it abort the whole walk.
            '
            Try
                MarkCorrupt(GetDirectory(Entry.ChildAnchorId), EntryPath, Ranges, Visited, Marks, RenamePartlyRecoveredFile)
            Catch ex As Exception When IsCorruptionFault(ex) OrElse TypeOf ex Is KeyNotFoundException
                FlagDirectoryCorrupt(ParentAnchor, Entry, EntryPath, LostBytes, Marks)
            End Try
        End Sub

        Private Sub MarkCorruptChildFile(ParentAnchor As ChunkedStream.Anchor, Entry As ContentListEntry,
                                         EntryPath As String, Ranges As List(Of ChunkedStream.LogicalRange),
                                         Marks As List(Of CorruptEntryMark), RenamePartlyRecoveredFile As Boolean)
            Dim Child = GetAnchor(Entry.ChildAnchorId)
            Dim DataOffset = Child.Offset + FileHeaderSize
            Dim DataLength = Entry.LengthOfDataAtEntry
            Dim RecordOffset = Child.Offset
            Dim RecordLength = FileHeaderSize + DataLength

            Dim RecordTouched = Ranges.Any(Function(range) range.Offset < RecordOffset + RecordLength AndAlso RecordOffset < range.Offset + range.Length)
            If RecordTouched = False Then Return

            Dim LostBytes = RangeOverlap(Ranges, DataOffset, DataLength)
            Dim Location = FindByChildId(ParentAnchor, Entry.ChildAnchorId)
            Dim RenamedTo As String = Nothing

            If Location.Entry.EntryType = EntryTypes.File OrElse Location.Entry.EntryType = EntryTypes.PendingFile Then

                If LostBytes > 0 AndAlso LostBytes < DataLength Then

                    SetEntryType(Location, EntryTypes.PartlyRecoveredFile)

                    If RenamePartlyRecoveredFile Then
                        Dim NewName As String = Nothing
                        If TryGenerateRecoveredName(ParentAnchor, Location.Entry.Name, NewName) Then
                            SetEntryName(Location, NewName)
                            RenamedTo = EntryPath.Substring(0, EntryPath.Length - Entry.Name.Length) & NewName
                        End If
                    End If

                Else
                    SetEntryType(Location, EntryTypes.CorruptFile)
                End If

            End If

            Marks.Add(New CorruptEntryMark(EntryPath, Entry.ChildAnchorId, LostBytes, IsDirectory:=False, RenamedTo:=RenamedTo))
        End Sub

        ''' <summary>
        ''' Finds an unused name for a file <see cref="Mark" /> is renaming after flagging it
        ''' <see cref="EntryTypes.PartlyRecoveredFile" />: <c>"name.ext"</c> becomes
        ''' <c>"name.recovered.ext"</c>, or <c>"name.recovered2.ext"</c>, <c>"name.recovered3.ext"</c>,
        ''' ... if that is already taken. Returns <see langword="False" /> (leaving
        ''' <paramref name="NewName" /> <see langword="Nothing" />) without throwing if every
        ''' candidate up to the 256-character name limit is either taken or too long - the caller
        ''' just keeps the original name.
        ''' </summary>
        Private Function TryGenerateRecoveredName(Parent As ChunkedStream.Anchor, CurrentName As String,
                                                   ByRef NewName As String) As Boolean
            Dim Ext = Path.GetExtension(CurrentName)
            Dim BaseName = Path.GetFileNameWithoutExtension(CurrentName)
            Dim ExistingNames = New HashSet(Of String)(
                ReadEntries(Parent).Select(Function(e) e.Name), StringComparer.OrdinalIgnoreCase)

            Dim Candidate = $"{BaseName}.recovered{Ext}"
            If Candidate.Length <= NameCharacterCapacity AndAlso ExistingNames.Contains(Candidate) = False Then
                NewName = Candidate
                Return True
            End If

            For Suffix = 2 To 999999
                Candidate = $"{BaseName}.recovered{Suffix}{Ext}"
                If Candidate.Length > NameCharacterCapacity Then Exit For
                If ExistingNames.Contains(Candidate) = False Then
                    NewName = Candidate
                    Return True
                End If
            Next

            NewName = Nothing
            Return False
        End Function

        Private Sub FlagDirectoryCorrupt(ParentAnchor As ChunkedStream.Anchor, Entry As ContentListEntry,
                                         EntryPath As String, LostBytes As Long, Marks As List(Of CorruptEntryMark))
            Dim Location = FindByChildId(ParentAnchor, Entry.ChildAnchorId)
            If Location.Entry.EntryType = EntryTypes.Directory Then SetEntryType(Location, EntryTypes.CorruptDirectory)
            Marks.Add(New CorruptEntryMark(EntryPath, Entry.ChildAnchorId, LostBytes, IsDirectory:=True))
        End Sub

        '
        ' Total bytes of the logical span [Offset, Offset + Length) that fall inside any problem range.
        '
        Private Shared Function RangeOverlap(Ranges As List(Of ChunkedStream.LogicalRange), Offset As Long, Length As Long) As Long
            Dim Total As Long = 0
            For Each Range In Ranges
                Dim OverlapStart = Math.Max(Range.Offset, Offset)
                Dim OverlapEnd = Math.Min(Range.Offset + Range.Length, Offset + Length)
                If OverlapEnd > OverlapStart Then Total += OverlapEnd - OverlapStart
            Next
            Return Total
        End Function

        '
        ' Whether a fault raised while reading a record's own bytes means the backing data is
        ' corrupt, as opposed to a defect or an environmental failure (such as a missing key) that
        ' a repair cannot address.
        '
        Private Shared Function IsCorruptionFault(Fault As Exception) As Boolean
            Return TypeOf Fault Is InvalidDataException OrElse
                   TypeOf Fault Is EndOfStreamException OrElse
                   TypeOf Fault Is CryptographicException
        End Function

        Private Function TryReadDirectoryRecordLength(Anchor As ChunkedStream.Anchor, ByRef Length As Long) As Boolean
            Try
                Length = ReadInt64(Anchor.Offset + DirectoryLengthOffset)
            Catch ex As Exception When IsCorruptionFault(ex)
                Return False
            End Try
            Return Length >= DirectoryHeaderSize
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

        Private Sub SetEntryName(Location As EntryLocation, Name As String)
            ' The name occupies bytes 24..24+NameByteCapacity of the entry record; a full-width,
            ' zero-filled write both stores the new name and clears any tail of the previous one.
            Dim Buffer(NameByteCapacity - 1) As Byte
            Dim Encoded = Encoding.Unicode.GetBytes(Name)
            System.Buffer.BlockCopy(Encoded, 0, Buffer, 0, Encoded.Length)
            ChunkedStream.Write(Location.Parent.Offset + DirectoryHeaderSize + (CLng(Location.Index) * EntrySize) + 24, Buffer)
            Location.Entry = New ContentListEntry(Location.Entry.EntryType, Location.Entry.ChildAnchorId,
                                                  Location.Entry.LengthOfDataAtEntry, Name)
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
            If EntryType <> EntryTypes.Directory AndAlso EntryType <> EntryTypes.File AndAlso
               EntryType <> EntryTypes.PendingFile AndAlso EntryType <> EntryTypes.CorruptFile AndAlso
               EntryType <> EntryTypes.CorruptDirectory AndAlso EntryType <> EntryTypes.PartlyRecoveredFile Then
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

        Private Sub CloseFile(FileAnchorId As Long, LeavePending As Boolean)
            SyncLock _SyncRoot
                If _Disposed OrElse _OpenFileIds.Remove(FileAnchorId) = False Then Return
                If LeavePending Then Return
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

        ''' <summary>
        ''' Marks this file system disposed. The backing <see cref="ChunkedStream" /> is
        ''' caller-owned and is deliberately left open - dispose it yourself once you are
        ''' finished with the file system. Throws if any file stream opened from this file
        ''' system is still open.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _SyncRoot
                If _Disposed Then Return
                If _OpenFileIds.Count > 0 Then Throw New InvalidOperationException("All file streams must be disposed first.")
                _Disposed = True
            End SyncLock
        End Sub

        ''' <summary>
        ''' The seekable <see cref="Stream" /> returned by <see cref="OpenFile" />. Only
        ''' <see cref="OpenFile" /> creates one; the parent entry stays
        ''' <see cref="EntryTypes.PendingFile" /> for the stream's lifetime.
        ''' </summary>
        Public NotInheritable Class FileStreamView
            Inherits Stream

            ''' <summary>
            ''' Default for <see cref="OpenFile" />'s <c>WriteBufferFlushThreshold</c> parameter.
            ''' </summary>
            Public Const DefaultWriteBufferFlushThreshold As Integer = 4 * 1024 * 1024

            ''' <summary>
            ''' Default for <see cref="OpenFile" />'s <c>WriteBufferFlushIntervalMilliseconds</c>
            ''' parameter.
            ''' </summary>
            Public Const DefaultWriteBufferFlushIntervalMilliseconds As Integer = 5000

            Private ReadOnly _Owner As EmbeddedFileSystem
            Private ReadOnly _Anchor As ChunkedStream.Anchor
            Private ReadOnly _WriteBuffer As New MemoryStream()
            Private ReadOnly _WriteBufferFlushThreshold As Integer
            Private ReadOnly _WriteBufferFlushInterval As TimeSpan
            Private _LastFlushUtc As DateTime
            Private _Position As Long
            Private _Disposed As Boolean

            Friend Sub New(Owner As EmbeddedFileSystem, Anchor As ChunkedStream.Anchor,
                          WriteBufferFlushThreshold As Integer,
                          WriteBufferFlushIntervalMilliseconds As Integer)
                _Owner = Owner
                _Anchor = Anchor
                _WriteBufferFlushThreshold = WriteBufferFlushThreshold
                _WriteBufferFlushInterval = TimeSpan.FromMilliseconds(WriteBufferFlushIntervalMilliseconds)
                _LastFlushUtc = DateTime.UtcNow
            End Sub

            ''' <summary>
            ''' When set, disposing the stream leaves the parent entry
            ''' <see cref="EntryTypes.PendingFile" /> instead of finalising it to
            ''' <see cref="EntryTypes.File" />, and the write buffer is dropped rather than flushed.
            ''' Set it while a write is in progress and clear it once the write has completed, so an
            ''' abandoned upload is left for <see cref="RecoverPendingFiles" /> without a catch block.
            ''' Call <see cref="Flush" /> first if a partial file's buffered tail must still be kept.
            ''' </summary>
            Public Property PendingOnClose As Boolean

            '
            ' Sequential appends past the current end are buffered and materialised in one
            ' Insert per _WriteBufferFlushThreshold bytes, so each Insert lands full chunk
            ' records instead of growing the file's tail chunk one small write at a time
            ' (the latter frees an intermediate record for every write and is the main
            ' source of EFS bloat). Each drain is published by WriteFile as one durable
            ' step - a FileStream.Flush(True), i.e. an fsync - so the threshold also sets
            ' how often an upload pays that cost. A large sequential upload (GB-TB scale)
            ' wants this raised via OpenFile's WriteBufferFlushThreshold parameter: a
            ' bigger threshold means far fewer fsyncs, at the cost of a larger in-memory
            ' buffer and a bigger crash-recovery replay window (RecoverPendingFiles still
            ' finalises correctly either way - only how far an abandoned upload rewinds
            ' changes). Anything else - a random-access write, a read, a seek, a length
            ' query - drains the buffer first.
            '
            ' The byte threshold alone bounds the replay window in bytes, not time - a slow
            ' or throttled upload could sit unpublished for an unbounded wall-clock stretch
            ' before accumulating that many bytes. WriteBufferFlushIntervalMilliseconds adds a
            ' second, time-based trigger (mirroring ZFS's transaction-group commit timer)
            ' so a crash never loses more than that many seconds of progress, independent of
            ' throughput.
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
                _LastFlushUtc = DateTime.UtcNow
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

            ''' <summary>
            ''' Returns the whole file as a new array, independent of the current position. Buffered
            ''' appends are flushed first. Throws if the file is larger than
            ''' <see cref="Integer.MaxValue" /> bytes.
            ''' </summary>
            Public Function ToArray() As Byte()
                CheckDisposed()
                DrainWriteBuffer()

                Dim FileLength = _Owner.GetFileLength(_Anchor)
                If FileLength = 0 Then Return Array.Empty(Of Byte)()
                If FileLength > Integer.MaxValue Then
                    Throw New IOException($"The file is {FileLength:N0} bytes and cannot be returned as a single array.")
                End If

                Dim Result(CInt(FileLength) - 1) As Byte
                Dim Total = 0
                While Total < Result.Length
                    Dim BytesRead = _Owner.ReadFile(_Anchor, Total, Result, Total, Result.Length - Total)
                    If BytesRead = 0 Then Throw New EndOfStreamException()
                    Total += BytesRead
                End While
                Return Result
            End Function

            Public Overrides Sub Write(Data As Byte(), Offset As Integer, Count As Integer)
                CheckDisposed()

                If _Position = BufferedEndPosition Then
                    _WriteBuffer.Write(Data, Offset, Count)
                    _Position += Count
                    If _WriteBuffer.Length >= _WriteBufferFlushThreshold OrElse
                       DateTime.UtcNow - _LastFlushUtc >= _WriteBufferFlushInterval Then
                        DrainWriteBuffer()
                    End If
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
                    Try
                        If PendingOnClose = False Then DrainWriteBuffer()
                    Finally
                        _WriteBuffer.Dispose()
                        _Owner.CloseFile(_Anchor.AnchorId, PendingOnClose)
                    End Try
                End If
                _Disposed = True
                MyBase.Dispose(Disposing)
            End Sub
        End Class
    End Class
End Namespace
