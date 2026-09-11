' ================================================================================
' ChunkedStream Deduplication
' ================================================================================
'
' Purpose
'   - Content-addressed deduplication of physical chunk records.
'
' Status
'   - In progress. This file currently holds only the core extendible-hash
'     algorithm (ExtendibleHashTable), built and tested in isolation before any
'     on-disk persistence, header wiring or write-path integration exists.
'
' ================================================================================
Namespace Streams
    Partial Class ChunkedStream

        ''' <summary>
        ''' A pure in-memory extendible hash table mapping a fixed-size byte-array key
        ''' (in practice, an HMAC) to a <see cref="Long"/> value (a physical record id).
        ''' </summary>
        ''' <remarks>
        ''' This class has no knowledge of persistence, encryption or ChunkedStream itself -
        ''' it only implements the extendible-hashing directory/bucket-split algorithm, so it
        ''' can be built and verified in isolation from the on-disk deduplication index it
        ''' will eventually back.
        '''
        ''' Extendible hashing grows by splitting one overflowing bucket at a time (using one
        ''' more bit of the key than that bucket currently distinguishes on), occasionally
        ''' doubling the directory when a bucket's own depth catches up to the directory's -
        ''' never by rehashing every existing entry the way a plain "hash mod N" table would
        ''' need to when N changes. Multiple directory slots may point at the same bucket;
        ''' only the slots for the bucket being split are ever repointed.
        ''' </remarks>
        Friend NotInheritable Class ExtendibleHashTable

            Private Structure Entry
                Public Key As Byte()
                Public Value As Long
            End Structure

            Private NotInheritable Class Bucket
                Public LocalDepth As Integer
                Public Entries As New List(Of Entry)()
            End Class

            Private ReadOnly _BucketCapacity As Integer
            Private _GlobalDepth As Integer
            Private _Directory As Bucket()

            Public Sub New(BucketCapacity As Integer)

                If BucketCapacity <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(BucketCapacity))

                _BucketCapacity = BucketCapacity
                _GlobalDepth = 0
                _Directory = {New Bucket With {.LocalDepth = 0}}

            End Sub

            ''' <summary>
            ''' Number of bits of each key currently used to address the directory. The
            ''' directory has <c>2^GlobalDepth</c> slots.
            ''' </summary>
            Public ReadOnly Property GlobalDepth As Integer
                Get
                    Return _GlobalDepth
                End Get
            End Property

            ''' <summary>
            ''' Total number of entries currently stored, across every bucket.
            ''' </summary>
            Public ReadOnly Property Count As Integer
                Get
                    Dim Total = 0
                    For Each DistinctBucket In DistinctBuckets()
                        Total += DistinctBucket.Entries.Count
                    Next
                    Return Total
                End Get
            End Property

            Private Iterator Function DistinctBuckets() As IEnumerable(Of Bucket)

                Dim Seen As New HashSet(Of Bucket)()

                For Each DirectoryBucket In _Directory
                    If Seen.Add(DirectoryBucket) Then
                        Yield DirectoryBucket
                    End If
                Next

            End Function

            ''' <summary>
            ''' Looks up <paramref name="Key"/>. Returns True and sets <paramref name="Value"/>
            ''' if an entry with an identical key exists.
            ''' </summary>
            Public Function TryGetValue(Key As Byte(), ByRef Value As Long) As Boolean

                If Key Is Nothing Then Throw New ArgumentNullException(NameOf(Key))

                Dim TargetBucket = _Directory(DirectoryIndex(Key))

                For Each Item In TargetBucket.Entries

                    If KeysEqual(Item.Key, Key) Then
                        Value = Item.Value
                        Return True
                    End If

                Next

                Value = 0
                Return False

            End Function

            '
            ' The directory doubles in size (2^GlobalDepth entries) on every step, so it's an
            ' OutOfMemoryException, not a slow failure, that would result from actually chasing
            ' duplicate/non-random keys out to Key.Length * 8 bits of depth - a real directory
            ' can never usefully need anywhere near this many buckets, so this is purely a
            ' cheap circuit breaker to fail with a clear diagnostic well before memory pressure
            ' would.
            '
            Private Const MaxGlobalDepth As Integer = 24

            ''' <summary>
            ''' Adds an entry for <paramref name="Key"/>. Does not check whether an entry for
            ''' this exact key already exists - callers are expected to have already done a
            ''' <see cref="TryGetValue"/> lookup and only insert on a miss.
            ''' </summary>
            Public Sub Insert(Key As Byte(), Value As Long)

                If Key Is Nothing Then Throw New ArgumentNullException(NameOf(Key))

                Do

                    If _GlobalDepth >= MaxGlobalDepth Then
                        Throw New InvalidOperationException(
                            "Extendible hash table directory depth exceeded its safety limit - this points at duplicate or non-random keys, not normal growth.")
                    End If

                    Dim TargetBucket = _Directory(DirectoryIndex(Key))

                    If TargetBucket.Entries.Count < _BucketCapacity Then
                        TargetBucket.Entries.Add(New Entry With {.Key = Key, .Value = Value})
                        Return
                    End If

                    If TargetBucket.LocalDepth = _GlobalDepth Then
                        DoubleDirectory()
                    End If

                    SplitBucket(TargetBucket)

                Loop

            End Sub

            ''' <summary>
            ''' Enumerates every (Key, Value) entry currently stored, in no particular order.
            ''' </summary>
            Public Iterator Function GetAllEntries() As IEnumerable(Of (Key As Byte(), Value As Long))

                For Each DistinctBucket In DistinctBuckets()
                    For Each Item In DistinctBucket.Entries
                        Yield (Item.Key, Item.Value)
                    Next
                Next

            End Function

            Private Function DirectoryIndex(Key As Byte()) As Integer

                Return CInt(TopBits(Key, _GlobalDepth))

            End Function

            Private Sub DoubleDirectory()

                Dim NewDirectory(_Directory.Length * 2 - 1) As Bucket

                For Index = 0 To _Directory.Length - 1
                    NewDirectory(Index) = _Directory(Index)
                    NewDirectory(Index + _Directory.Length) = _Directory(Index)
                Next

                _Directory = NewDirectory
                _GlobalDepth += 1

            End Sub

            Private Sub SplitBucket(OldBucket As Bucket)

                Dim NewLocalDepth = OldBucket.LocalDepth + 1
                Dim SiblingBucket As New Bucket With {.LocalDepth = NewLocalDepth}

                Dim EntriesToRedistribute = OldBucket.Entries
                OldBucket.Entries = New List(Of Entry)()
                OldBucket.LocalDepth = NewLocalDepth

                For Each Item In EntriesToRedistribute

                    If BitAt(Item.Key, NewLocalDepth - 1) = 0 Then
                        OldBucket.Entries.Add(Item)
                    Else
                        SiblingBucket.Entries.Add(Item)
                    End If

                Next

                '
                ' Every directory slot whose low NewLocalDepth bits match OldBucket's identity
                ' prefix currently points at OldBucket. Repoint exactly the half of those whose
                ' newly-significant bit is 1 - the rest correctly keep pointing at OldBucket.
                ' Bit ordering here must match TopBits: key-bit 0 is the directory index's
                ' lowest-order bit, so the bit a given local depth newly distinguishes on is at
                ' index position (NewLocalDepth - 1), not counted from the top.
                '
                For Index = 0 To _Directory.Length - 1

                    If ReferenceEquals(_Directory(Index), OldBucket) Then

                        Dim Bit = CInt((CULng(Index) >> (NewLocalDepth - 1)) And 1UL)

                        If Bit = 1 Then
                            _Directory(Index) = SiblingBucket
                        End If

                    End If

                Next

            End Sub

            Private Shared Function KeysEqual(Left As Byte(), Right As Byte()) As Boolean

                If Left.Length <> Right.Length Then Return False

                For Index = 0 To Left.Length - 1
                    If Left(Index) <> Right(Index) Then Return False
                Next

                Return True

            End Function

            Private Shared Function BitAt(Key As Byte(), BitPosition As Integer) As Integer

                Dim ByteIndex = BitPosition \ 8
                Dim BitInByte = 7 - (BitPosition Mod 8)

                Return (Key(ByteIndex) >> BitInByte) And 1

            End Function

            ''' <summary>
            ''' Packs the first BitCount bits of Key into a directory index, with key-bit 0
            ''' (the key's own most-significant bit) as the index's LOWEST-order bit and each
            ''' later key-bit progressively higher. This ordering - not the more intuitive
            ''' "earlier bits are more significant" - is what makes DoubleDirectory's simple
            ''' "copy the low half into the high half" doubling correct: growing global depth
            ''' by one adds exactly one new key-bit, and it must land in the new high-order
            ''' position for the copy to preserve every existing entry's addressability.
            ''' </summary>
            Private Shared Function TopBits(Key As Byte(), BitCount As Integer) As ULong

                Dim Result As ULong = 0

                For Index = 0 To BitCount - 1
                    Result = Result Or (CULng(BitAt(Key, Index)) << Index)
                Next

                Return Result

            End Function

        End Class

    End Class
End Namespace
