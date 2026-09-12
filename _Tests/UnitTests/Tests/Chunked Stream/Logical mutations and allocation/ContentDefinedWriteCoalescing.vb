Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        ''' <summary>
        ''' Verifies write coalescing (see WriteCoalescing.vb and CurrentChunkWriteCaching.vb) is
        ''' content-defined-chunking-aware: under Options.ChunkSizeVariance, extending the current
        ''' last chunk in place (unconditionally) or filling the Options.CurrentChunkWriteCaching
        ''' buffer both stop at the same Gear-hash boundary a one-shot write of the identical bytes
        ''' would have chosen, instead of always targeting a fixed Options.ChunkSize regardless of
        ''' content - closing the original EFS/dedup-alignment gap write coalescing was built for.
        ''' </summary>
        Public NotInheritable Class ContentDefinedWriteCoalescing

            Private Sub New()
            End Sub

            Private Shared Function MakeCdcOptions() As ChunkedStream.ChunkedStreamOptions

                Return New ChunkedStream.ChunkedStreamOptions With {
                    .ChunkSize = 32,
                    .ChunkSizeVariance = 0.5
                }

            End Function

            Private Shared Sub AssertSameExtentBoundaries(A As ChunkedStream, B As ChunkedStream, Message As String)

                AssertEqual(A.Debug_GetExtentCount(), B.Debug_GetExtentCount(), Message & " (extent count)")

                For Index = 0 To A.Debug_GetExtentCount() - 1
                    AssertEqual(A.Debug_GetExtentLength(Index), B.Debug_GetExtentLength(Index), Message & $" (extent {Index} length)")
                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub UnconditionalExtendInPlaceMatchesAOneShotWriteUnderCdc()

                Dim Data = GenerateRandomData(4096, 501)

                Using MsIncremental As New MemoryStream()
                    Using CsIncremental = ChunkedStream.Open(MsIncremental, MakeCdcOptions())

                        ' One byte at a time - the smallest possible unit of coalescing - so every
                        ' single Write() call has to go through TryExtendLastChunkAsync's CDC scan.
                        For Offset = 0 To Data.Length - 1
                            CsIncremental.Write(CLng(Offset), New Byte() {Data(Offset)})
                        Next

                        Using MsOneShot As New MemoryStream()
                            Using CsOneShot = ChunkedStream.Open(MsOneShot, MakeCdcOptions())

                                CsOneShot.Write(0, Data)

                                AssertSameExtentBoundaries(CsIncremental, CsOneShot, "Byte-at-a-time appends should chunk identically to a one-shot write under CDC.")

                                Dim ReadBack(Data.Length - 1) As Byte
                                CsIncremental.Read(0, ReadBack)
                                AssertBytesEqual(Data, ReadBack, "The incrementally written data should read back correctly.")

                            End Using
                        End Using

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CurrentChunkWriteCachingMatchesAOneShotWriteUnderCdc()

                Dim Data = GenerateRandomData(4096, 502)

                Using MsIncremental As New MemoryStream()

                    Dim IncrementalOptions = MakeCdcOptions()
                    IncrementalOptions.CurrentChunkWriteCaching = True

                    Using CsIncremental = ChunkedStream.Open(MsIncremental, IncrementalOptions)

                        ' Odd-sized, non-chunk-aligned bursts, closer to how a real caller might
                        ' drive the write-cache buffer than perfectly regular single bytes.
                        Dim Offset = 0
                        Dim BurstSizes = {3, 7, 1, 11, 2, 5}
                        Dim BurstIndex = 0

                        While Offset < Data.Length

                            Dim BurstSize = Math.Min(BurstSizes(BurstIndex Mod BurstSizes.Length), Data.Length - Offset)
                            Dim Segment(BurstSize - 1) As Byte
                            Buffer.BlockCopy(Data, Offset, Segment, 0, BurstSize)

                            CsIncremental.Write(CLng(Offset), Segment)

                            Offset += BurstSize
                            BurstIndex += 1

                        End While

                        CsIncremental.FlushCurrentChunkWriteCache()

                        Using MsOneShot As New MemoryStream()
                            Using CsOneShot = ChunkedStream.Open(MsOneShot, MakeCdcOptions())

                                CsOneShot.Write(0, Data)

                                AssertSameExtentBoundaries(CsIncremental, CsOneShot, "Buffered bursty appends should chunk identically to a one-shot write under CDC once flushed.")

                                Dim ReadBack(Data.Length - 1) As Byte
                                CsIncremental.Read(0, ReadBack)
                                AssertBytesEqual(Data, ReadBack, "The buffered, incrementally written data should read back correctly.")

                            End Using
                        End Using

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ExtendInPlaceStopsAtAContentBoundaryInsteadOfAlwaysReachingChunkSize()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, MakeCdcOptions())

                        Dim Data = GenerateRandomData(4096, 503)

                        For Offset = 0 To Data.Length - 1
                            Cs.Write(CLng(Offset), New Byte() {Data(Offset)})
                        Next

                        AssertTrue(Cs.Debug_GetExtentCount() > 1, "Sanity check: 4096 bytes at ChunkSize=32 should produce more than one chunk.")

                        Dim SawAShorterThanMaxChunk = False

                        For Index = 0 To Cs.Debug_GetExtentCount() - 2 ' every chunk except the true (possibly still-open) tail

                            Dim Length = Cs.Debug_GetExtentLength(Index)

                            AssertTrue(Length <= 48, $"Extent {Index} (length {Length}) exceeded MaxChunkSize (32 * 1.5 = 48).")

                            If Length < 48 Then SawAShorterThanMaxChunk = True

                        Next

                        AssertTrue(SawAShorterThanMaxChunk, "At least one non-tail chunk should have closed on a Gear-hash boundary short of MaxChunkSize, not just kept extending to the cap.")

                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' A one-shot write's non-tail chunks are always genuinely closed - by a Gear-hash
            ''' boundary or MaxChunkSize, never merely because the data ran out (only the true
            ''' final chunk of a write can end for that reason). Truncating right at the boundary
            ''' between the first and second chunk leaves the stream's last extent as that first,
            ''' already-closed chunk - appending past it must start a brand new record rather than
            ''' growing the closed one further, matching what a one-shot write of the combined
            ''' bytes would have produced.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AppendAfterAContentBoundaryStartsANewRecordNotAFurtherExtension()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, MakeCdcOptions())

                        Dim Data = GenerateRandomData(4096, 504)

                        Cs.Write(0, Data)

                        AssertTrue(Cs.Debug_GetExtentCount() > 1, "Sanity check: 4096 bytes at ChunkSize=32 should produce more than one chunk.")

                        Dim FirstChunkLength = Cs.Debug_GetExtentLength(0)

                        Cs.SetLength(FirstChunkLength)

                        AssertEqual(1, Cs.Debug_GetExtentCount(), "Sanity check: truncating exactly at the first chunk's own boundary should leave it as the only, unsplit extent.")

                        Dim RecordCountBeforeAppend = Cs.Debug_GetPhysicalRecordCount()
                        Dim FirstChunkRecordId = Cs.Debug_GetPhysicalRecordIdAt(0)

                        Cs.Write(CLng(FirstChunkLength), New Byte() {42})

                        AssertEqual(RecordCountBeforeAppend + 1, Cs.Debug_GetPhysicalRecordCount(), "A closed chunk must not be extended further - the append should start a new record.")
                        AssertFalse(Cs.Debug_GetPhysicalRecordIdAt(FirstChunkLength) = FirstChunkRecordId, "The appended byte should live in a new record, not the closed one.")
                        AssertEqual(FirstChunkLength, Cs.Debug_GetExtentLength(0), "The closed chunk's own extent must be completely unchanged by the append.")

                        Dim ReadBack(FirstChunkLength) As Byte
                        Cs.Read(0, ReadBack)

                        Dim Expected(FirstChunkLength) As Byte
                        Buffer.BlockCopy(Data, 0, Expected, 0, FirstChunkLength)
                        Expected(FirstChunkLength) = 42

                        AssertBytesEqual(Expected, ReadBack, "All bytes, including the one appended after the boundary, should read back correctly.")

                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
