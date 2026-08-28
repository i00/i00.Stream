' ================================================================================
' ChunkedStream Anchors
' ================================================================================
'
' Purpose
'   - Implements stable logical anchors.
'   - Allows logical data to be identified without relying on offsets that may
'     change due to inserts, removals or rebuild operations.
'
' Design
'   - Anchors are stored directly on extents using immutable AnchorIds.
'   - An AnchorId of zero means the extent is not anchored.
'   - A positive AnchorId identifies the start of anchored logical data.
'   - AnchorIds are immutable and never reused.
'   - At most one anchor may exist at any logical offset.
'   - Anchors belong to a specific ChunkedStream instance.
'   - Anchors are rebuilt into an in-memory AnchorId -> Extent index on open.
'
' Creation
'   - CreateAnchor(LogicalOffset):
'       - Anchors existing logical data.
'       - Splits the target extent if required.
'       - Throws if an anchor already exists at the requested logical offset.
'       - Throws if the logical offset is not within existing logical data.
'
'   - CreateAnchor(Data):
'       - Appends new logical data.
'       - Creates an anchor at the first appended byte.
'       - Throws when Data is Nothing.
'       - Throws when Data is empty.
'
' Lifetime
'   - Anchors survive:
'       - Normal writes
'       - Clear operations
'       - Copy-on-write
'       - Physical record relocation
'       - Compression rewrites
'       - Encryption rewrites
'       - Sparse/allocated conversions
'       - Defragmentation
'       - Rebuild operations
'       - ApplyOptions migrations
'
'   - Anchors are destroyed when:
'       - The anchored logical start is removed.
'       - Anchor.Remove() is called.
'       - Remove(Anchor, ...) removes anchored data.
'
' Insert Semantics
'   - AnchorActionsAtLogicalOffset controls anchor handling when logical data is
'     inserted at an anchored position.
'
'       TransformAway
'         - Existing data keeps the anchor.
'         - Inserted data does not inherit the anchor.
'         - The anchor moves with the original data.
'
'       Use
'         - Inserted data inherits the anchor.
'         - The anchor remains at the insertion position.
'
'   - Insert(LogicalOffset, ...) defaults to TransformAway.
'   - CloneInsert(LogicalOffset, ...) defaults to TransformAway.
'   - Insert(Anchor, ...) always uses Use.
'   - CloneInsert(..., Anchor) always uses Use.
'
' Replace Semantics
'   - Replace(LogicalOffset, ...) defaults to Use.
'   - The anchor at the replacement start may transfer to replacement data.
'   - Anchors strictly inside the replaced logical range are removed.
'   - Anchors at the replacement end survive.
'   - Replace(Anchor, ...) preserves the specified anchor whenever replacement
'     data remains.
'
' Remove Semantics
'   - Anchors whose logical starts fall inside a removed range are destroyed.
'   - Anchors at the logical end of the removed range survive.
'   - Removing anchored data removes the anchor.
'
' Clone Semantics
'   - Clone operations copy logical data only.
'   - Anchor identities are never cloned.
'   - Existing destination anchors are preserved according to the selected
'     AnchorActionsAtLogicalOffset behaviour.
'
' Checkpoints
'   - Anchor creation, removal and transfer participate in checkpoints.
'   - Rollback restores anchor state.
'   - Commit updates the anchor baseline.
'
' Recovery
'   - Anchors are recovered as part of normal metadata recovery.
'   - AnchorIds survive reopen, rollback and rebuild recovery.
'
' Validation
'   - AnchorIds must be unique.
'   - Anchored logical offsets must be unique.
'   - Anchor references must resolve to valid extents.
'   - Anchor index state is validated against extent metadata.
'
' ================================================================================

Imports System.Collections.ObjectModel
Imports System.IO

Namespace Streams
    Partial Class ChunkedStream

        ''' <summary>
        ''' Controls how an anchor located at the target logical offset is treated when data
        ''' is inserted there.
        ''' </summary>
        Public Enum AnchorActionsAtLogicalOffset

            ''' <summary>
            ''' The anchor stays with the existing data and moves after the inserted data.
            ''' The inserted data does not inherit the anchor.
            ''' </summary>
            TransformAway = 0

            ''' <summary>
            ''' The inserted data inherits the anchor, which remains at the insertion offset.
            ''' </summary>
            Use = 1

        End Enum

        ''' <summary>
        ''' A stable, immutable reference to the start of a logical data range within a
        ''' specific ChunkedStream instance.
        ''' </summary>
        ''' <remarks>
        ''' Anchor identities survive logical movement, copy-on-write, compression and
        ''' encryption changes, defragmentation and rebuilds. An anchor belongs to the
        ''' ChunkedStream that created it and cannot be used with another instance.
        ''' </remarks>
        Public NotInheritable Class Anchor

            Private ReadOnly _Owner As ChunkedStream

            Friend Function IsOwnedBy(Owner As ChunkedStream) As Boolean
                Return Object.ReferenceEquals(_Owner, Owner)
            End Function

            Friend Sub New(Owner As ChunkedStream,
                           AnchorId As Long)

                If Owner Is Nothing Then Throw New ArgumentNullException(NameOf(Owner))
                If AnchorId <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(AnchorId))

                _Owner = Owner
                Me.AnchorId = AnchorId

            End Sub

            ''' <summary>
            ''' Gets the immutable identifier of this anchor. Anchor identifiers are never reused.
            ''' </summary>
            Public ReadOnly Property AnchorId As Long

            ''' <summary>
            ''' Gets a value indicating whether the anchored logical data still exists.
            ''' Returns False once the anchored data or the anchor itself has been removed.
            ''' </summary>
            Public ReadOnly Property IsValid As Boolean
                Get
                    Return _Owner.ContainsAnchor(AnchorId)
                End Get
            End Property

            ''' <summary>
            ''' Gets the current logical offset of the anchored data.
            ''' </summary>
            ''' <exception cref="Collections.Generic.KeyNotFoundException">The anchor is no longer valid.</exception>
            Public ReadOnly Property Offset As Long
                Get
                    Return _Owner.GetAnchorOffset(AnchorId)
                End Get
            End Property

            ''' <summary>
            ''' Removes this anchor. The underlying logical data is not affected.
            ''' </summary>
            Public Sub Remove()
                _Owner.RemoveAnchor(Me)
            End Sub

            ''' <summary>
            ''' Returns a diagnostic description of the anchor and its current logical offset.
            ''' </summary>
            Public Overrides Function ToString() As String

                If IsValid = False Then
                    Return $"Anchor {AnchorId} [removed]"
                End If

                Return $"Anchor {AnchorId} at logical offset {Offset}"

            End Function

        End Class

        Private Sub EnsureAnchorOwner(Anchor As Anchor)

            If Anchor Is Nothing Then
                Throw New ArgumentNullException(NameOf(Anchor))
            End If

            If Anchor.IsOwnedBy(Me) = False Then
                Throw New ArgumentException(
                    "The anchor belongs to a different ChunkedStream.",
                    NameOf(Anchor))
            End If

        End Sub

        Private Structure AnchoredBoundary
            Public AnchorId As Long
            Public RelativeOffset As Long
        End Structure

        Private ReadOnly _ExtentIndexesByAnchorId As New Dictionary(Of Long, Integer)()
        Private _NextAnchorId As Long = 1

        ''' <summary>
        ''' Creates an anchor identifying the start of existing logical data at the specified
        ''' offset. The target extent is split if required.
        ''' </summary>
        ''' <param name="LogicalOffset">Logical offset to anchor. Must fall within existing logical data.</param>
        ''' <returns>The new anchor.</returns>
        ''' <exception cref="InvalidOperationException">An anchor already exists at <paramref name="LogicalOffset" />.</exception>
        Public Function CreateAnchor(LogicalOffset As Long) As Anchor

            Using EnterStateLock()
                Return CreateAnchorCore(LogicalOffset)
            End Using

        End Function

        Private Function CreateAnchorCore(LogicalOffset As Long) As Anchor


            ThrowIfDisposed()
            ThrowIfFaulted()

            If LogicalOffset < 0 OrElse LogicalOffset >= _Length Then
                Throw New ArgumentOutOfRangeException(
                    NameOf(LogicalOffset),
                    "An anchor must identify the start of existing logical data.")
            End If

            If FindAnchorIdAtLogicalOffset(LogicalOffset) > 0 Then
                Throw New InvalidOperationException(
                    $"An anchor already exists at logical offset {LogicalOffset}.")
            End If

            Try

                SplitExtentAt(LogicalOffset)

                Dim ExtentIndex = FindExtentInsertIndex(LogicalOffset)

                If ExtentIndex < 0 OrElse ExtentIndex >= _Extents.Count Then
                    Throw New InvalidDataException(
                        $"No extent begins at logical offset {LogicalOffset}.")
                End If

                Dim Extent = _Extents(ExtentIndex)

                If Extent.LogicalOffset <> LogicalOffset Then
                    Throw New InvalidDataException(
                        $"No extent begins at logical offset {LogicalOffset}.")
                End If

                If Extent.AnchorId > 0 Then
                    Throw New InvalidOperationException(
                        $"An anchor already exists at logical offset {LogicalOffset}.")
                End If

                Dim AnchorId = AllocateAnchorId()

                Extent.AnchorId = AnchorId
                _Extents(ExtentIndex) = Extent

                RebuildAnchorIndex()
                MarkExtentPageRangeDirty(ExtentIndex, ExtentIndex)

                If MetadataPublishSuspended = False Then
                    PersistIndexAndHeader(_IndexOffset)
                End If

                Return New Anchor(Me, AnchorId)

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Appends the supplied data to the end of the logical stream and creates an anchor
        ''' identifying its first byte.
        ''' </summary>
        ''' <param name="Data">Data to append. Must not be Nothing or empty.</param>
        ''' <returns>The new anchor.</returns>
        Public Function CreateAnchor(Data As Byte()) As Anchor

            Using EnterStateLock()
                Return CreateAnchorCore(Data)
            End Using

        End Function

        Private Function CreateAnchorCore(Data As Byte()) As Anchor

            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            If Data.Length = 0 Then
                Throw New ArgumentException(
                    "Anchored data cannot be empty.",
                    NameOf(Data))
            End If


            ThrowIfDisposed()
            ThrowIfFaulted()

            Try

                Dim AnchorOffset = _Length
                Dim AnchorId = AllocateAnchorId()
                Dim NewExtents = BuildExtentsFromBuffer(Data, 0, Data.Length)

                If NewExtents.Count = 0 Then
                    Throw New InvalidOperationException(
                        "No extents were created for the anchored data.")
                End If

                Dim FirstExtent = NewExtents(0)
                FirstExtent.AnchorId = AnchorId
                NewExtents(0) = FirstExtent

                InvalidateChunkCache()

                InsertExtentsCore(AnchorOffset,
                                  NewExtents,
                                  AnchorActionsAtLogicalOffset.Use)

                If MetadataPublishSuspended = False Then
                    PersistIndexAndHeader(_IndexOffset)
                End If

                Return New Anchor(Me, AnchorId)

            Catch

                _Faulted = True
                Throw

            End Try


        End Function

        ''' <summary>
        ''' Resolves an anchor by its identifier.
        ''' </summary>
        ''' <param name="AnchorId">Identifier of the anchor to resolve.</param>
        ''' <returns>The anchor with the specified identifier.</returns>
        ''' <exception cref="Collections.Generic.KeyNotFoundException">No anchor with the specified identifier exists.</exception>
        Public Function GetAnchor(AnchorId As Long) As Anchor

            Using EnterStateLock()
                Return GetAnchorCore(AnchorId)
            End Using

        End Function

        Private Function GetAnchorCore(AnchorId As Long) As Anchor


            ThrowIfDisposed()

            If AnchorId <= 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(AnchorId))
            End If

            If _ExtentIndexesByAnchorId.ContainsKey(AnchorId) = False Then
                Throw New KeyNotFoundException(
                    $"Anchor {AnchorId} was not found.")
            End If

            Return New Anchor(Me, AnchorId)


        End Function

        ''' <summary>
        ''' Attempts to resolve an anchor by its identifier.
        ''' </summary>
        ''' <param name="AnchorId">Identifier of the anchor to resolve.</param>
        ''' <param name="Anchor">
        ''' When this method returns True, receives the resolved anchor; otherwise Nothing.
        ''' </param>
        ''' <returns>True when the anchor was found; otherwise False.</returns>
        Public Function TryGetAnchor(AnchorId As Long,
                                     ByRef Anchor As Anchor) As Boolean

            Using EnterStateLock()
                Return TryGetAnchorCore(AnchorId, Anchor)
            End Using

        End Function

        Private Function TryGetAnchorCore(AnchorId As Long,
                                     ByRef Anchor As Anchor) As Boolean


            ThrowIfDisposed()

            If AnchorId <= 0 OrElse
               _ExtentIndexesByAnchorId.ContainsKey(AnchorId) = False Then

                Anchor = Nothing
                Return False

            End If

            Anchor = New Anchor(Me, AnchorId)
            Return True


        End Function

        ''' <summary>
        ''' Returns all anchors currently defined on the stream, ordered by anchor identifier.
        ''' </summary>
        Public Function GetAnchors() As IReadOnlyList(Of Anchor)

            Using EnterStateLock()
                Return GetAnchorsCore()
            End Using

        End Function

        Private Function GetAnchorsCore() As IReadOnlyList(Of Anchor)


            ThrowIfDisposed()

            Dim Result =
                _ExtentIndexesByAnchorId.Keys.
                OrderBy(Function(anchorId) anchorId).
                Select(Function(anchorId) New Anchor(Me, anchorId)).
                ToList()

            Return New ReadOnlyCollection(Of Anchor)(Result)


        End Function

        ''' <summary>
        ''' Determines whether an anchor with the specified identifier currently exists.
        ''' </summary>
        ''' <param name="AnchorId">Identifier to test.</param>
        ''' <returns>True when the anchor exists; otherwise False.</returns>
        Public Function ContainsAnchor(AnchorId As Long) As Boolean

            Using EnterStateLock()
                Return ContainsAnchorCore(AnchorId)
            End Using

        End Function

        Private Function ContainsAnchorCore(AnchorId As Long) As Boolean


            If _Disposed Then Return False
            If AnchorId <= 0 Then Return False

            Return _ExtentIndexesByAnchorId.ContainsKey(AnchorId)


        End Function

        ''' <summary>
        ''' Returns the current logical offset of the data identified by the specified anchor.
        ''' </summary>
        ''' <param name="AnchorId">Identifier of the anchor to locate.</param>
        ''' <returns>The logical offset of the anchored data.</returns>
        ''' <exception cref="Collections.Generic.KeyNotFoundException">No anchor with the specified identifier exists.</exception>
        Public Function GetAnchorOffset(AnchorId As Long) As Long

            Using EnterStateLock()
                Return GetAnchorOffsetCore(AnchorId)
            End Using

        End Function

        Private Function GetAnchorOffsetCore(AnchorId As Long) As Long


            ThrowIfDisposed()

            Dim ExtentIndex As Integer

            If _ExtentIndexesByAnchorId.TryGetValue(AnchorId,
                                                    ExtentIndex) = False Then

                Throw New KeyNotFoundException(
                    $"Anchor {AnchorId} was not found.")

            End If

            If ExtentIndex < 0 OrElse ExtentIndex >= _Extents.Count Then
                Throw New InvalidDataException(
                    $"Anchor {AnchorId} has an invalid extent index.")
            End If

            Dim Extent = _Extents(ExtentIndex)

            If Extent.AnchorId <> AnchorId Then
                Throw New InvalidDataException(
                    $"Anchor index mismatch for anchor {AnchorId}.")
            End If

            Return Extent.LogicalOffset


        End Function

        Private Sub RemoveAnchor(Anchor As Anchor)

            Using EnterStateLock()
                RemoveAnchorCore(Anchor)
            End Using

        End Sub

        Private Sub RemoveAnchorCore(Anchor As Anchor)

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            EnsureAnchorOwner(Anchor)


            ThrowIfDisposed()
            ThrowIfFaulted()

            Dim ExtentIndex As Integer

            If _ExtentIndexesByAnchorId.TryGetValue(Anchor.AnchorId,
                                                    ExtentIndex) = False Then

                Throw New KeyNotFoundException(
                    $"Anchor {Anchor.AnchorId} was not found.")

            End If

            Dim Extent = _Extents(ExtentIndex)

            If Extent.AnchorId <> Anchor.AnchorId Then
                Throw New InvalidDataException(
                    $"Anchor index mismatch for anchor {Anchor.AnchorId}.")
            End If

            Try

                Extent.AnchorId = 0
                _Extents(ExtentIndex) = Extent

                RebuildAnchorIndex()
                MarkExtentPageRangeDirty(ExtentIndex, ExtentIndex)

                If MetadataPublishSuspended = False Then
                    PersistIndexAndHeader(_IndexOffset)
                End If

            Catch

                _Faulted = True
                Throw

            End Try


        End Sub

        ''' <summary>
        ''' Writes data at the current logical offset of the specified anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying where the write begins.</param>
        ''' <param name="Input">Source buffer.</param>
        ''' <param name="DataOffset">Offset within <paramref name="Input" /> of the first byte to write.</param>
        ''' <param name="Count">Number of bytes to write, or Nothing to write to the end of <paramref name="Input" />.</param>
        ''' <returns>The number of bytes written.</returns>
        Public Overloads Function Write(Anchor As Anchor,
                                        Input As Byte(),
                                        Optional DataOffset As Integer = 0,
                                        Optional Count As Integer? = Nothing) As Integer

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            EnsureAnchorOwner(Anchor)

            Return Write(GetAnchorOffset(Anchor.AnchorId),
                         Input,
                         DataOffset,
                         Count)

        End Function

        ''' <summary>
        ''' Reads data starting at the current logical offset of the specified anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying where the read begins.</param>
        ''' <param name="Output">Destination buffer.</param>
        ''' <param name="OutputOffset">Offset within <paramref name="Output" /> at which to begin storing data.</param>
        ''' <param name="Count">Maximum number of bytes to read, or Nothing to fill <paramref name="Output" /> from <paramref name="OutputOffset" />.</param>
        ''' <returns>The number of bytes read.</returns>
        Public Overloads Function Read(Anchor As Anchor,
                                       Output As Byte(),
                                       Optional OutputOffset As Integer = 0,
                                       Optional Count As Integer? = Nothing) As Integer

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            EnsureAnchorOwner(Anchor)

            Return Read(GetAnchorOffset(Anchor.AnchorId),
                        Output,
                        OutputOffset,
                        Count)

        End Function

        ''' <summary>
        ''' Replaces <paramref name="Count" /> logical bytes with zero bytes, starting at the
        ''' current logical offset of the specified anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying where the cleared range begins.</param>
        ''' <param name="Count">Number of logical bytes to clear.</param>
        Public Overloads Sub Clear(Anchor As Anchor,
                                   Count As Long)

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            EnsureAnchorOwner(Anchor)

            Clear(GetAnchorOffset(Anchor.AnchorId),
                  Count)

        End Sub

        ''' <summary>
        ''' Inserts <paramref name="Count" /> zero bytes at the current logical offset of the
        ''' specified anchor. The anchor is retained at the start of the inserted bytes.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying the insertion offset.</param>
        ''' <param name="Count">Number of zero bytes to insert.</param>
        Public Overloads Sub InsertNullBytes(Anchor As Anchor,
                                             Count As Long)

            If Anchor Is Nothing Then
                Throw New ArgumentNullException(NameOf(Anchor))
            End If

            EnsureAnchorOwner(Anchor)

            InsertNullBytes(GetAnchorOffset(Anchor.AnchorId),
                    Count,
                    AnchorActionsAtLogicalOffset.Use)

        End Sub

        ''' <summary>
        ''' Removes <paramref name="Length" /> logical bytes starting at the current logical
        ''' offset of the specified anchor. Removing anchored data destroys the anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying where removal begins.</param>
        ''' <param name="Length">Number of logical bytes to remove.</param>
        Public Overloads Sub Remove(Anchor As Anchor,
                                    Length As Long)

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            Remove(GetAnchorOffset(Anchor.AnchorId),
                   Length)

        End Sub

        ''' <summary>
        ''' Inserts data at the current logical offset of the specified anchor. The inserted
        ''' data inherits the anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying the insertion offset.</param>
        ''' <param name="Data">Data to insert.</param>
        Public Overloads Sub Insert(Anchor As Anchor,
                                    Data As Byte())

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))
            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            EnsureAnchorOwner(Anchor)

            Insert(GetAnchorOffset(Anchor.AnchorId),
                   Data,
                   0,
                   Data.Length,
                   AnchorActionsAtLogicalOffset.Use)

        End Sub

        ''' <summary>
        ''' Inserts a region of the supplied buffer at the current logical offset of the
        ''' specified anchor. The inserted data inherits the anchor.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying the insertion offset.</param>
        ''' <param name="Data">Buffer containing the data to insert.</param>
        ''' <param name="DataOffset">Offset within <paramref name="Data" /> of the first byte to insert.</param>
        ''' <param name="Count">Number of bytes to insert from <paramref name="Data" />.</param>
        Public Overloads Sub Insert(Anchor As Anchor,
                                    Data As Byte(),
                                    DataOffset As Integer,
                                    Count As Integer)

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))

            Insert(GetAnchorOffset(Anchor.AnchorId),
                   Data,
                   DataOffset,
                   Count,
                   AnchorActionsAtLogicalOffset.Use)

        End Sub

        ''' <summary>
        ''' Replaces <paramref name="Length" /> logical bytes, starting at the current logical
        ''' offset of the specified anchor, with the supplied data. The anchor is preserved
        ''' whenever replacement data remains.
        ''' </summary>
        ''' <param name="Anchor">Anchor identifying where the replaced range begins.</param>
        ''' <param name="Length">Number of existing logical bytes to replace.</param>
        ''' <param name="Data">Replacement data.</param>
        Public Sub Replace(Anchor As Anchor,
                           Length As Long,
                           Data As Byte())

            If Anchor Is Nothing Then Throw New ArgumentNullException(NameOf(Anchor))
            If Data Is Nothing Then Throw New ArgumentNullException(NameOf(Data))

            EnsureAnchorOwner(Anchor)

            Replace(GetAnchorOffset(Anchor.AnchorId),
                    Length,
                    Data,
                    0,
                    Data.Length,
                    AnchorActionsAtLogicalOffset.Use)

        End Sub

        ''' <summary>
        ''' Copies a logical range and inserts the copy at the current logical offset of the
        ''' specified target anchor. Source anchor identities are never cloned.
        ''' </summary>
        ''' <param name="SourceLogicalOffset">Logical offset of the data to clone.</param>
        ''' <param name="CloneLength">Number of logical bytes to clone.</param>
        ''' <param name="TargetAnchor">Anchor identifying where the cloned data is inserted.</param>
        Public Sub CloneInsert(SourceLogicalOffset As Long,
                               CloneLength As Long,
                               TargetAnchor As Anchor)

            If TargetAnchor Is Nothing Then
                Throw New ArgumentNullException(NameOf(TargetAnchor))
            End If

            EnsureAnchorOwner(TargetAnchor)

            CloneInsert(SourceLogicalOffset,
                        CloneLength,
                        GetAnchorOffset(TargetAnchor.AnchorId),
                        AnchorActionsAtLogicalOffset.Use)

        End Sub

        Private Function AllocateAnchorId() As Long

            Dim Result = Math.Max(1L, _NextAnchorId)

            If Result = Long.MaxValue Then
                Throw New InvalidOperationException(
                    "No more anchor ids are available.")
            End If

            _NextAnchorId = Result + 1

            Return Result

        End Function

        Private Sub RebuildAnchorIndex()

            _ExtentIndexesByAnchorId.Clear()

            Dim HighestAnchorId As Long = 0

            For ExtentIndex = 0 To _Extents.Count - 1

                Dim Extent = _Extents(ExtentIndex)

                If Extent.AnchorId < 0 Then
                    Throw New InvalidDataException(
                        $"Extent {ExtentIndex} has an invalid anchor id.")
                End If

                If Extent.AnchorId = 0 Then Continue For

                If _ExtentIndexesByAnchorId.ContainsKey(Extent.AnchorId) Then
                    Throw New InvalidDataException(
                        $"Duplicate anchor id {Extent.AnchorId}.")
                End If

                _ExtentIndexesByAnchorId.Add(Extent.AnchorId,
                                             ExtentIndex)

                HighestAnchorId = Math.Max(HighestAnchorId,
                                           Extent.AnchorId)

            Next

            _NextAnchorId =
                Math.Max(Math.Max(1L, _NextAnchorId),
                         HighestAnchorId + 1L)

        End Sub

        Private Function FindAnchorIdAtLogicalOffset(LogicalOffset As Long) As Long

            If LogicalOffset < 0 OrElse LogicalOffset >= _Length Then
                Return 0
            End If

            Dim ExtentIndex = FindExtentIndex(LogicalOffset)

            If ExtentIndex < 0 Then Return 0

            Dim Extent = _Extents(ExtentIndex)

            If Extent.LogicalOffset <> LogicalOffset Then Return 0

            Return Extent.AnchorId

        End Function

        Private Function CaptureAnchoredBoundaries(LogicalOffset As Long,
                                                   Length As Long,
                                                   IncludeStart As Boolean,
                                                   IncludeEnd As Boolean) As List(Of AnchoredBoundary)

            Dim Result As New List(Of AnchoredBoundary)()

            If Length < 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Length))
            End If

            Dim EndOffset = LogicalOffset + Length

            For Each pair In _ExtentIndexesByAnchorId

                Dim Extent = _Extents(pair.Value)
                Dim AnchorOffset = Extent.LogicalOffset

                If AnchorOffset < LogicalOffset Then Continue For
                If AnchorOffset = LogicalOffset AndAlso IncludeStart = False Then Continue For
                If AnchorOffset > EndOffset Then Continue For
                If AnchorOffset = EndOffset AndAlso IncludeEnd = False Then Continue For

                Result.Add(
                    New AnchoredBoundary With {
                        .AnchorId = pair.Key,
                        .RelativeOffset = AnchorOffset - LogicalOffset
                    })

            Next

            Result.Sort(
                Function(left, right)
                    Return left.RelativeOffset.CompareTo(right.RelativeOffset)
                End Function)

            Return Result

        End Function

        Private Function ApplyAnchoredBoundariesToExtents(Extents As IList(Of ExtentIndexEntry),
                                                          Boundaries As IEnumerable(Of AnchoredBoundary),
                                                          TotalLength As Long) As List(Of ExtentIndexEntry)

            If Extents Is Nothing Then Throw New ArgumentNullException(NameOf(Extents))

            Dim Result = Extents.Select(Function(extent)
                                            Dim NewExtent = extent
                                            NewExtent.AnchorId = 0
                                            Return NewExtent
                                        End Function).
                                 ToList()

            If Boundaries Is Nothing Then Return Result

            For Each Boundary In Boundaries.OrderBy(Function(item) item.RelativeOffset)

                If Boundary.AnchorId <= 0 Then
                    Throw New InvalidDataException("Invalid anchor id.")
                End If

                If Boundary.RelativeOffset < 0 OrElse
                   Boundary.RelativeOffset >= TotalLength Then

                    Continue For

                End If

                Result = SplitDetachedExtentLayoutAt(Result,
                                                     Boundary.RelativeOffset)

                Dim CurrentOffset As Long = 0
                Dim Found = False

                For Index = 0 To Result.Count - 1

                    Dim Extent = Result(Index)

                    If CurrentOffset = Boundary.RelativeOffset Then

                        If Extent.AnchorId > 0 AndAlso
                           Extent.AnchorId <> Boundary.AnchorId Then

                            Throw New InvalidOperationException(
                                $"Two anchors would identify logical offset {Boundary.RelativeOffset}.")
                        End If

                        Extent.AnchorId = Boundary.AnchorId
                        Result(Index) = Extent

                        Found = True
                        Exit For

                    End If

                    CurrentOffset += Extent.LogicalLength

                Next

                If Found = False Then
                    Throw New InvalidDataException(
                        $"Unable to recreate anchored boundary at relative offset {Boundary.RelativeOffset}.")
                End If

            Next

            Return Result

        End Function

        Private Function SplitDetachedExtentLayoutAt(Extents As IList(Of ExtentIndexEntry),
                                                     RelativeOffset As Long) As List(Of ExtentIndexEntry)

            If Extents Is Nothing Then Throw New ArgumentNullException(NameOf(Extents))
            If RelativeOffset <= 0 Then Return New List(Of ExtentIndexEntry)(Extents)

            Dim Result As New List(Of ExtentIndexEntry)(Extents.Count + 1)
            Dim CurrentOffset As Long = 0

            For Each Extent In Extents

                Dim ExtentEnd = CurrentOffset + Extent.LogicalLength

                If RelativeOffset <= CurrentOffset OrElse
                   RelativeOffset >= ExtentEnd Then

                    Result.Add(Extent)
                    CurrentOffset = ExtentEnd
                    Continue For

                End If

                Dim LeftLength = CInt(RelativeOffset - CurrentOffset)
                Dim RightLength = Extent.LogicalLength - LeftLength

                Dim LeftExtent = Extent
                LeftExtent.LogicalLength = LeftLength

                Dim RightExtent = Extent
                RightExtent.LogicalLength = RightLength
                RightExtent.AnchorId = 0

                If Extent.PhysicalRecordId = SparsePhysicalRecordId Then
                    LeftExtent.PhysicalRecordOffset = 0
                    RightExtent.PhysicalRecordOffset = 0
                Else
                    RightExtent.PhysicalRecordOffset =
                        Extent.PhysicalRecordOffset + LeftLength
                End If

                If Extent.PhysicalRecordId <> SparsePhysicalRecordId Then
                    IncrementPhysicalRecordRefCount(Extent.PhysicalRecordId)
                End If

                Result.Add(LeftExtent)
                Result.Add(RightExtent)

                CurrentOffset = ExtentEnd

            Next

            Return Result

        End Function

        Private Sub ValidateAnchors()

            Dim SeenAnchorIds As New HashSet(Of Long)()
            Dim SeenOffsets As New HashSet(Of Long)()

            For Each Extent In _Extents

                If Extent.AnchorId < 0 Then
                    Throw New InvalidDataException(
                        "Extent has a negative anchor id.")
                End If

                If Extent.AnchorId = 0 Then Continue For

                If SeenAnchorIds.Add(Extent.AnchorId) = False Then
                    Throw New InvalidDataException(
                        $"Duplicate anchor id {Extent.AnchorId}.")
                End If

                If SeenOffsets.Add(Extent.LogicalOffset) = False Then
                    Throw New InvalidDataException(
                        $"Multiple anchors identify logical offset {Extent.LogicalOffset}.")
                End If

            Next

            If SeenAnchorIds.Count <> _ExtentIndexesByAnchorId.Count Then
                Throw New InvalidDataException(
                    "Anchor index count does not match the anchored extent count.")
            End If

        End Sub

    End Class
End Namespace
