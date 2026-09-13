Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies that the two places BuildCloneExtentsAsync materialises a brand new physical
        ''' record instead of sharing the source extent's own record - a small cloned fragment
        ''' below Options.BisectLimit, and two adjacent cloned fragments merged across an extent
        ''' boundary - go through the ordinary dedup-checked write path (WritePhysicalRecordAsync)
        ''' rather than bypassing it. Both already did, since neither call site has its own write
        ''' logic - they call the same low-level function every other write uses, which checks
        ''' Options.Deduplication unconditionally. These are regression guards for that fact, not
        ''' fixes for a gap: RemoveRangeCoreAsync's own adjacent-boundary merge (used by Remove)
        ''' shares the exact same code path and needs no separate test. Anchor-related splitting
        ''' (SplitExtentAt, SplitDetachedExtentLayoutAt) never writes new physical-record content at
        ''' all - it only re-slices metadata over an already-existing record - so there is nothing
        ''' for a dedup check to do there.
        ''' </summary>
        Public NotInheritable Class DedupCloneAndSplit

            Private Sub New()
            End Sub

            Private Shared Function MakeOptions() As ChunkedStream.ChunkedStreamOptions

                Return New ChunkedStream.ChunkedStreamOptions With {
                    .ChunkSize = 128,
                    .ChunkSizeVariance = 0,
                    .Deduplication = True,
                    .BisectLimit = 16
                }

            End Function

            ''' <summary>
            ''' A clone shorter than BisectLimit is materialised into its own new record rather
            ''' than sharing the source record's sub-range (see BuildCloneExtentsAsync's BisectLimit
            ''' branch). If that materialisation matches an already-registered record's content, it
            ''' should dedup onto it instead of allocating a duplicate.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub BisectLimitMaterialisedCloneDedupsAgainstAnExistingRecord()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, MakeOptions())

                        Dim TargetContent = GenerateRandomData(8, 701)

                        ' Registers TargetContent as its own physical record in the dedup index.
                        Cs.Write(0, TargetContent)

                        ' A larger, unrelated record with TargetContent's exact bytes embedded
                        ' inside it, at a sub-range that isn't chunk-aligned with anything.
                        Dim Prefix = GenerateRandomData(20, 702)
                        Dim Suffix = GenerateRandomData(20, 703)
                        Dim SourceBlob(Prefix.Length + TargetContent.Length + Suffix.Length - 1) As Byte
                        Buffer.BlockCopy(Prefix, 0, SourceBlob, 0, Prefix.Length)
                        Buffer.BlockCopy(TargetContent, 0, SourceBlob, Prefix.Length, TargetContent.Length)
                        Buffer.BlockCopy(Suffix, 0, SourceBlob, Prefix.Length + TargetContent.Length, Suffix.Length)

                        Cs.Write(1000, SourceBlob)

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: two unrelated records so far.")

                        Dim EmbeddedOffset = 1000L + Prefix.Length
                        Dim TargetOffset = Cs.Length

                        ' CloneLength (8) is below BisectLimit (16), so this must materialise
                        ' rather than share SourceBlob's own record.
                        Cs.Clone(EmbeddedOffset, TargetContent.Length, TargetOffset)

                        AssertEqual(2, Cs.Debug_GetPhysicalRecordCount(), "The materialised clone should dedup-match TargetContent's existing record, not create a third one.")

                        Dim OriginalRecordId = Cs.Debug_GetPhysicalRecordIdAt(0)
                        Dim ClonedRecordId = Cs.Debug_GetPhysicalRecordIdAt(TargetOffset)

                        AssertEqual(OriginalRecordId, ClonedRecordId, "The cloned fragment should reference TargetContent's original record.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(OriginalRecordId), "TargetContent's record should now have two references: the original write and the dedup-matched clone.")

                        Dim ReadBack(TargetContent.Length - 1) As Byte
                        Cs.Read(TargetOffset, ReadBack)
                        AssertBytesEqual(TargetContent, ReadBack, "The cloned range should read back as TargetContent.")

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' A clone spanning exactly two adjacent extents, each contributing a fragment shorter
            ''' than BisectLimit, gets combined into a single new record rather than left as two
            ''' tiny fragments (see BuildCloneExtentsAsync's ShouldMaterialiseAdjacentBoundaryFragments
            ''' branch). If the combined bytes match an already-registered record, it should dedup
            ''' onto it instead of allocating a duplicate.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AdjacentBoundaryMergeDedupsAgainstAnExistingRecord()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, MakeOptions())

                        Dim ExtentAData = GenerateRandomData(40, 711)
                        Dim ExtentBData = GenerateRandomData(40, 712)

                        ' The exact 12 bytes a merge across the ExtentA/ExtentB boundary will
                        ' produce: ExtentA's last 6 bytes followed by ExtentB's first 6 bytes.
                        Dim CombinedTarget(11) As Byte
                        Buffer.BlockCopy(ExtentAData, 34, CombinedTarget, 0, 6)
                        Buffer.BlockCopy(ExtentBData, 0, CombinedTarget, 6, 6)

                        ' Registers CombinedTarget as its own physical record in the dedup index.
                        Cs.Write(0, CombinedTarget)

                        ' ExtentB first, then ExtentA inserted immediately before it - Insert
                        ' always splices in a fresh, non-coalesced extent, so ExtentA and ExtentB
                        ' end up as two distinct, adjacent records rather than one extended chunk.
                        Cs.Write(1000, ExtentBData)
                        Cs.Insert(1000, ExtentAData)

                        AssertEqual(3, Cs.Debug_GetPhysicalRecordCount(), "Sanity check: CombinedTarget, ExtentA and ExtentB should be three distinct records.")

                        Dim BoundaryOffset = 1000L + 34 ' Last 6 bytes of ExtentA, first 6 of ExtentB follow immediately.
                        Dim TargetOffset = Cs.Length

                        Cs.Clone(BoundaryOffset, 12, TargetOffset)

                        AssertEqual(3, Cs.Debug_GetPhysicalRecordCount(), "The merged boundary clone should dedup-match CombinedTarget's existing record, not create a fourth one.")

                        Dim OriginalRecordId = Cs.Debug_GetPhysicalRecordIdAt(0)
                        Dim ClonedRecordId = Cs.Debug_GetPhysicalRecordIdAt(TargetOffset)

                        AssertEqual(OriginalRecordId, ClonedRecordId, "The merged clone should reference CombinedTarget's original record.")
                        AssertEqual(2, Cs.Debug_GetPhysicalRecordRefCount(OriginalRecordId), "CombinedTarget's record should now have two references: the original write and the dedup-matched clone.")

                        Dim ReadBack(11) As Byte
                        Cs.Read(TargetOffset, ReadBack)
                        AssertBytesEqual(CombinedTarget, ReadBack, "The cloned range should read back as CombinedTarget.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
