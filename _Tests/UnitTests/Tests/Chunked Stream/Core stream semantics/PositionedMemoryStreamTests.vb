Imports System.IO
Imports System.Threading
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        ''' <summary>
        ''' PositionedMemoryStream is a general-purpose, production IPositionedStreamAsync
        ''' implementation over an in-memory buffer - not about raw speed (there is no device
        ''' queue depth to exploit in memory), but genuinely useful for tests, transient/
        ''' scratch archives, or exercising ChunkedStream's lock-free concurrent code paths
        ''' without touching a real file.
        ''' </summary>
        Public NotInheritable Class PositionedMemoryStreamTests

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeclaresFullLockFreeCapability()

                Using Pms As New PositionedMemoryStream()
                    AssertEqual(PositionedIoCapabilities.Full, Pms.PositionedIoCapabilities, "Expected full lock-free capability.")
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAtWriteAtRoundTripDirectly()

                Using Pms As New PositionedMemoryStream()

                    Dim DataA = GenerateRandomData(50000, 8101)
                    Dim DataB = GenerateRandomData(30000, 8102)

                    ' Deliberately out of offset order, mirroring the PositionedFileStream test.
                    Pms.WriteAt(60000, DataB, 0, DataB.Length)
                    Pms.WriteAt(0, DataA, 0, DataA.Length)

                    Dim ReadBackA(DataA.Length - 1) As Byte
                    AssertEqual(DataA.Length, Pms.ReadAt(0, ReadBackA, 0, ReadBackA.Length), "Short ReadAt for region A.")
                    AssertBytesEqual(DataA, ReadBackA, "ReadAt/WriteAt round trip mismatch for region A.")

                    Dim ReadBackB(DataB.Length - 1) As Byte
                    AssertEqual(DataB.Length, Pms.ReadAt(60000, ReadBackB, 0, ReadBackB.Length), "Short ReadAt for region B.")
                    AssertBytesEqual(DataB, ReadBackB, "ReadAt/WriteAt round trip mismatch for region B.")

                    AssertEqual(0, Pms.ReadAt(0, ReadBackA, 0, 0), "Zero-length ReadAt should return 0.")
                    AssertEqual(0, Pms.ReadAt(Pms.Length, ReadBackA, 0, ReadBackA.Length), "ReadAt at EOF should return 0.")

                    Dim Tail(9999) As Byte
                    AssertEqual(100, Pms.ReadAt(Pms.Length - 100, Tail, 0, Tail.Length),
                                "A read extending past EOF should return only the bytes actually present.")

                    ' A gap between WriteAt calls (never explicitly written) reads back as zero,
                    ' matching a real file's sparse-extend behaviour.
                    Dim GapBuffer(999) As Byte
                    Pms.ReadAt(DataA.Length + 5000, GapBuffer, 0, GapBuffer.Length)
                    For Each B In GapBuffer
                        AssertEqual(0, CInt(B), "An unwritten gap should read back as zero.")
                    Next

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ToArrayReturnsCurrentContentIndependentOfPosition()

                Using Pms As New PositionedMemoryStream()
                    Dim Expected = GenerateRandomData(12345, 8103)
                    Pms.Write(Expected, 0, Expected.Length)
                    Pms.Position = 500 ' should not affect ToArray()
                    AssertBytesEqual(Expected, Pms.ToArray(), "ToArray did not return the full written content.")
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReopeningFromToArrayPreservesContent()

                Dim Expected = GenerateRandomData(8000, 8104)
                Dim Bytes As Byte()

                Using Pms As New PositionedMemoryStream()
                    Pms.Write(Expected, 0, Expected.Length)
                    Bytes = Pms.ToArray()
                End Using

                Using Reopened As New PositionedMemoryStream(Bytes)
                    AssertBytesEqual(Expected, Reopened.ToArray(), "Reopening from ToArray() lost data.")
                End Using

            End Sub

            ''' <summary>
            ''' Genuinely concurrent reads: several threads reading disjoint regions at once
            ''' must all complete correctly and without serialising on the write lock (a plain
            ''' single-lock design would still be correct here, so this mainly guards against a
            ''' future change accidentally taking an exclusive lock for reads).
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ConcurrentReadsFromMultipleThreadsAllReturnCorrectData()

                Using Pms As New PositionedMemoryStream()

                    Const RegionCount As Integer = 8
                    Const RegionSize As Integer = 100000

                    Dim Regions As New List(Of Byte())()
                    For i = 0 To RegionCount - 1
                        Dim Region = GenerateRandomData(RegionSize, 8200 + i)
                        Pms.WriteAt(CLng(i) * RegionSize, Region, 0, Region.Length)
                        Regions.Add(Region)
                    Next

                    Dim Failure As Exception = Nothing
                    Dim Threads As New List(Of Thread)()

                    For i = 0 To RegionCount - 1
                        Dim Index = i
                        Dim T As New Thread(
                            Sub()
                                Try
                                    Dim Buffer(RegionSize - 1) As Byte
                                    Pms.ReadAt(CLng(Index) * RegionSize, Buffer, 0, Buffer.Length)
                                    AssertBytesEqual(Regions(Index), Buffer, $"Concurrent read of region {Index} returned the wrong bytes.")
                                Catch Ex As Exception
                                    Interlocked.CompareExchange(Failure, Ex, Nothing)
                                End Try
                            End Sub)
                        Threads.Add(T)
                    Next

                    For Each T In Threads : T.Start() : Next
                    For Each T In Threads : T.Join() : Next

                    If Failure IsNot Nothing Then Throw New Exception("A concurrent reader failed: " & Failure.Message, Failure)

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ChunkedStreamRoundTripsThroughPositionedMemoryStream()

                Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 6 + 321, 8301)

                Using Pms As New PositionedMemoryStream()
                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8302))
                    }
                    Using Cs = ChunkedStream.Open(Pms, Options)
                        Cs.Write(0, Expected)
                        Cs.Flush()
                        Cs.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Cs.ToArray(), "PositionedMemoryStream round trip mismatch.")
                    End Using

                    Using Reopened = ChunkedStream.Open(Pms, New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(8302))})
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Reopen on the same PositionedMemoryStream instance lost data.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' Regression test for a real bug in EnsureCapacity's growth strategy: its
            ''' Int32-overflow guard used to clamp a too-large doubling request down to exactly
            ''' the required size, rather than to the largest still-safe allocation. That
            ''' silently killed the amortised-growth property right at the point it matters
            ''' most in the real ~2GB-array-limited class (~1 GB, where RequiredLength * 2 first
            ''' threatens to exceed Int32.MaxValue): every subsequent write needing more
            ''' capacity then paid its own full reallocation + copy of the whole buffer so far -
            ''' O(n) per call, O(n^2) overall - made drastically worse in practice by GC
            ''' pressure from repeatedly abandoning gigabyte-sized arrays. Measured before the
            ''' fix: 40+ seconds for a single 64 MB write once the stream passed ~1 GB.
            '''
            ''' Reproduced here at a tiny, safe scale via Debug_MaxArrayLengthOverride instead
            ''' of actually growing a multi-gigabyte buffer (which OOM'd inside the shared test
            ''' process - a 64MB block plus a transient ~3GB peak during the reallocation itself
            ''' is a lot to ask of a process that has already run 300+ other tests). Reallocation
            ''' count (the backing array's reference changing) stands in for timing: amortised
            ''' doubling growth needs O(log n) reallocations, not one per write.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub GrowthNearTheCapacityLimitStaysAmortised()

                Using Pms As New PositionedMemoryStream()
                    Pms.Debug_MaxArrayLengthOverride = 10000 ' tiny stand-in for the real ~2GB cap

                    Dim ReallocationCount = 0
                    Dim LastArray As Byte() = Nothing

                    Dim WriteBlock(99) As Byte
                    Dim Written = 0
                    While Written < 9500 ' comfortably past where doubling first exceeds the override

                        Pms.WriteAt(Written, WriteBlock, 0, WriteBlock.Length)

                        Dim CurrentArray = Pms.Debug_GetBufferArray()
                        If Not ReferenceEquals(CurrentArray, LastArray) Then
                            ReallocationCount += 1
                            LastArray = CurrentArray
                        End If

                        Written += WriteBlock.Length

                    End While

                    ' log2(9500 / 256) ~ 5-6 reallocations for genuine amortised doubling: the
                    ' old bug degraded to one reallocation per write (95 of them) once doubling
                    ' first exceeded the (here tiny) cap.
                    AssertTrue(ReallocationCount < 20,
                               $"Expected O(log n) reallocations, got {ReallocationCount} - growth is no longer amortised near the capacity limit.")

                End Using

            End Sub

        End Class

    End Class

End Namespace
