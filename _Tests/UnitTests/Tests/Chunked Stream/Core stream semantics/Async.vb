Imports System.IO
Imports System.Threading
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        ''' <summary>
        ''' Exercises the asynchronous API. The synchronous path is unchanged, so these
        ''' tests focus on the async methods producing byte-identical results, honouring
        ''' the stream position and the cancellation token, and driving the asynchronous
        ''' <see cref="IPositionedStreamAsync" /> fast path when the backing store offers it.
        ''' </summary>
        Public NotInheritable Class AsyncIo

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Read path
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAsyncMatchesSyncForEveryRepresentation()

                Dim ChunkSize = 4096

                Dim Cases =
                    New Action(Of ChunkedStream.ChunkedStreamOptions)() {
                        Sub(o) o.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None,
                        Sub(o) o.CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate,
                        Sub(o) o.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9100))
                    }

                For CaseIndex = 0 To Cases.Length - 1

                    Using Ms As New MemoryStream()

                        Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = ChunkSize}
                        Cases(CaseIndex).Invoke(Options)

                        Dim Expected = GenerateRandomData(ChunkSize * 5 + 123, 9000 + CaseIndex)

                        Using Cs = ChunkedStream.Open(Ms, Options)

                            Cs.Write(0, Expected)
                            ' A sparse region so the async extent walk crosses a hole.
                            Cs.SetLength(Cs.Length + ChunkSize * 2)
                            Cs.Write(Cs.Length, GenerateRandomData(500, 9500 + CaseIndex))

                            Dim SyncAll = Cs.ToArray()

                            Dim AsyncAll = Cs.ToArrayAsync().GetAwaiter().GetResult()
                            AssertBytesEqual(SyncAll, AsyncAll, $"ToArrayAsync mismatch (case {CaseIndex}).")

                            Dim Slab(ChunkSize * 2 - 1) As Byte
                            Dim Read = Cs.ReadAsync(ChunkSize + 77, Slab, 0, Slab.Length).GetAwaiter().GetResult()
                            AssertEqual(Slab.Length, Read, $"Short positional ReadAsync (case {CaseIndex}).")
                            AssertBytesEqual(Slice(SyncAll, ChunkSize + 77, Slab.Length), Slab, $"Positional ReadAsync mismatch (case {CaseIndex}).")

                            Dim Range = Cs.ToArrayAsync(10, 4321).GetAwaiter().GetResult()
                            AssertBytesEqual(Slice(SyncAll, 10, 4321), Range, $"ToArrayAsync(range) mismatch (case {CaseIndex}).")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                    End Using

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub StreamReadAsyncAdvancesPosition()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Expected = GenerateRandomData(20000, 9310)
                        Cs.Write(0, Expected)

                        Cs.Position = 0

                        Dim First(7999) As Byte
                        Dim FirstRead = Cs.ReadAsync(First, 0, First.Length).GetAwaiter().GetResult()
                        AssertEqual(First.Length, FirstRead, "First async stream read was short.")
                        AssertEqual(8000L, Cs.Position, "Position did not advance after ReadAsync.")

                        Dim Rest(11999) As Byte
                        Dim RestRead = Cs.ReadAsync(Rest, 0, Rest.Length).GetAwaiter().GetResult()
                        AssertEqual(Rest.Length, RestRead, "Second async stream read was short.")
                        AssertEqual(20000L, Cs.Position, "Position wrong after second ReadAsync.")

                        AssertBytesEqual(Expected, CombineArrays(First, Rest), "Async stream reads did not reassemble the data.")

                        Dim Eof(9) As Byte
                        AssertEqual(0, Cs.ReadAsync(Eof, 0, Eof.Length).GetAwaiter().GetResult(), "ReadAsync past end should return 0.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAsyncDrivesTheAsyncPositionedFastPath()

                Using Backing As New AsyncPositionedMemoryStream(PositionedIoCapabilities.None)

                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 9410)

                    Using Cs = ChunkedStream.Open(Backing)

                        Cs.Write(0, Expected)
                        Backing.ResetCounters()

                        AssertBytesEqual(Expected, Cs.ToArrayAsync().GetAwaiter().GetResult(), "Async positioned round-trip mismatch.")

                        ' Snapshot before Validate(), which legitimately uses the synchronous read path.
                        Dim AsyncCalls = Backing.ReadAtAsyncCalls
                        Dim SyncCalls = Backing.SyncReadAtCallsSinceReset

                        Cs.Validate().ThrowIfErrors()

                        AssertTrue(AsyncCalls > 0, "ChunkedStream never used IPositionedStreamAsync.ReadAtAsync.")
                        AssertEqual(0, SyncCalls, "ChunkedStream fell back to the synchronous ReadAt on the async path.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadAsyncHonoursAPreCancelledToken()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(5000, 9610))

                        Using Cts As New CancellationTokenSource()

                            Cts.Cancel()

                            Dim Threw = False
                            Try
                                Dim Ignored = Cs.ToArrayAsync(Cts.Token).GetAwaiter().GetResult()
                            Catch Ex As OperationCanceledException
                                Threw = True
                            End Try

                            AssertTrue(Threw, "ToArrayAsync did not observe a pre-cancelled token.")

                        End Using

                        ' The stream is still usable after a cancelled async call.
                        AssertEqual(5000, Cs.ToArray().Length, "Stream unusable after a cancelled ReadAsync.")
                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Write / mutation path
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub AsyncWritesAndMutationsMatchTheSyncModel()

                For Each Encrypted In New Boolean() {False, True}

                    Dim SyncBytes As Byte()
                    Dim AsyncBytes As Byte()

                    ' Run the identical operation script synchronously and asynchronously and
                    ' require byte-identical results.
                    Using Ms As New MemoryStream()
                        Using Cs = ChunkedStream.Open(Ms, MakeOptions(Encrypted, 2000))
                            Dim Data = GenerateRandomData(9000, 4400)
                            Cs.Write(0, Data)
                            Cs.Insert(1500, GenerateRandomData(1200, 4401))
                            Cs.Replace(500, 800, GenerateRandomData(2500, 4402))
                            Cs.Remove(3000, 1000)
                            Cs.SetLength(Cs.Length + 3000)
                            Cs.Clear(200, 700)
                            Cs.InsertNullBytes(50, 400)
                            Cs.Clone(100, 900, Cs.Length)
                            Cs.Flush()
                            SyncBytes = Cs.ToArray()
                            Cs.Validate().ThrowIfErrors()
                        End Using
                    End Using

                    Using Ms As New MemoryStream()
                        Using Cs = ChunkedStream.Open(Ms, MakeOptions(Encrypted, 2000))
                            Dim Data = GenerateRandomData(9000, 4400)
                            Cs.WriteAsync(0, Data).GetAwaiter().GetResult()
                            Cs.InsertAsync(1500, GenerateRandomData(1200, 4401)).GetAwaiter().GetResult()
                            Cs.ReplaceAsync(500, 800, GenerateRandomData(2500, 4402)).GetAwaiter().GetResult()
                            Cs.RemoveAsync(3000, 1000).GetAwaiter().GetResult()
                            Cs.SetLengthAsync(Cs.Length + 3000).GetAwaiter().GetResult()
                            Cs.ClearAsync(200, 700).GetAwaiter().GetResult()
                            Cs.InsertNullBytesAsync(50, 400).GetAwaiter().GetResult()
                            Cs.CloneAsync(100, 900, Cs.Length).GetAwaiter().GetResult()
                            Cs.FlushAsync(CancellationToken.None).GetAwaiter().GetResult()
                            AsyncBytes = Cs.ToArrayAsync().GetAwaiter().GetResult()
                            Cs.Validate().ThrowIfErrors()
                        End Using
                    End Using

                    AssertBytesEqual(SyncBytes, AsyncBytes, $"Async op script diverged from the sync model (encrypted={Encrypted}).")

                Next

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AsyncWrittenStreamSurvivesReopen()

                Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 4 + 55, 4500)

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.WriteAsync(0, Expected).GetAwaiter().GetResult()
                        Cs.FlushAsync(CancellationToken.None).GetAwaiter().GetResult()
                        Cs.Validate().ThrowIfErrors()
                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Async-written data did not survive reopen.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub WriteAsyncDrivesTheAsyncPositionedFastPath()

                Using Backing As New AsyncPositionedMemoryStream(PositionedIoCapabilities.None)

                    Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3, 4600)

                    Using Cs = ChunkedStream.Open(Backing)

                        Backing.ResetCounters()
                        Cs.WriteAsync(0, Expected).GetAwaiter().GetResult()
                        Cs.FlushAsync(CancellationToken.None).GetAwaiter().GetResult()

                        Dim AsyncWriteCalls = Backing.WriteAtAsyncCalls
                        Dim SyncWriteCalls = Backing.SyncWriteAtCallsSinceReset

                        AssertBytesEqual(Expected, Cs.ToArrayAsync().GetAwaiter().GetResult(), "Async positioned write round-trip mismatch.")
                        Cs.Validate().ThrowIfErrors()

                        AssertTrue(AsyncWriteCalls > 0, "ChunkedStream never used IPositionedStreamAsync.WriteAtAsync.")
                        AssertEqual(0, SyncWriteCalls, "ChunkedStream fell back to the synchronous WriteAt on the async write path.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub WriteAsyncFaultsTheStreamOnFailureLikeTheSyncPath()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(1000, 4700))

                        Dim Threw = False
                        Try
                            Cs.WriteAsync(-1, New Byte() {1, 2, 3}).GetAwaiter().GetResult()
                        Catch Ex As ArgumentOutOfRangeException
                            Threw = True
                        End Try
                        AssertTrue(Threw, "WriteAsync with a negative offset should throw ArgumentOutOfRangeException.")

                        ' An argument guard rejects before any state mutation, so the stream stays usable.
                        AssertEqual(1000, Cs.ToArray().Length, "Stream unusable after a rejected WriteAsync.")
                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Open / anchors / checkpoints / maintenance
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub OpenAsyncMatchesOpenAndRunsRecovery()

                Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 3 + 7, 4800)

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Write(0, Expected)
                    End Using

                    Using Reopened = ChunkedStream.OpenAsync(Ms).GetAwaiter().GetResult()
                        AssertBytesEqual(Expected, Reopened.ToArrayAsync().GetAwaiter().GetResult(), "OpenAsync produced different content.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AnchorRelativeAsyncIoTracksTheAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms, New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 1024})

                        Cs.Write(0, GenerateRandomData(4000, 4900))

                        Dim Anchor = Cs.CreateAnchorAsync(2000L).GetAwaiter().GetResult()

                        ' Shift the anchored data by inserting before it, then write through the anchor.
                        Cs.Insert(500, GenerateRandomData(300, 4901))

                        Dim Patch = GenerateRandomData(200, 4902)
                        Cs.WriteAsync(Anchor, Patch).GetAwaiter().GetResult()

                        Dim Back(199) As Byte
                        Cs.ReadAsync(Anchor, Back, 0, Back.Length).GetAwaiter().GetResult()
                        AssertBytesEqual(Patch, Back, "Anchor-relative async read did not see the anchor-relative async write.")

                        ' The anchor's absolute offset moved by the insert length.
                        AssertEqual(2300L, Cs.GetAnchorOffset(Anchor.AnchorId), "Anchor offset did not track the insert.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointAsyncCommitAndRollbackBehaveLikeSync()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, GenerateRandomData(2000, 5000))
                        Dim V0 = Cs.ToArray()

                        Dim Cp = Cs.CreateCheckpointAsync().GetAwaiter().GetResult()
                        Try
                            Cs.Write(0, GenerateRandomData(2000, 5001))
                            Cp.RollbackAsync().GetAwaiter().GetResult()
                            AssertBytesEqual(V0, Cs.ToArray(), "Async rollback did not restore the checkpoint baseline.")

                            Cs.Write(500, GenerateRandomData(400, 5002))
                            Dim V1 = Cs.ToArray()
                            Cp.CommitAsync().GetAwaiter().GetResult()

                            Cs.Write(0, GenerateRandomData(2000, 5003))
                            Cp.RollbackAsync().GetAwaiter().GetResult()
                            AssertBytesEqual(V1, Cs.ToArray(), "Async rollback did not restore the committed baseline.")
                        Finally
                            Cp.CloseAsync().GetAwaiter().GetResult()
                        End Try

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentAsyncCompactsAndCanBeCancelled()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms, New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 512})

                        Cs.Write(0, GenerateRandomData(40000, 5100))
                        For Offset = 0 To 30000 Step 2000
                            Cs.Write(Offset, GenerateRandomData(700, 5100 + Offset))
                        Next

                        Dim Expected = Cs.ToArray()

                        Dim Saved = Cs.DefragmentAsync(ChunkedStream.DefragTypes.Sequence).GetAwaiter().GetResult()
                        AssertTrue(Saved >= 0, "DefragmentAsync reported a negative result without cancellation.")
                        AssertBytesEqual(Expected, Cs.ToArray(), "DefragmentAsync changed the logical content.")
                        Cs.Validate().ThrowIfErrors()

                        ' A pre-cancelled token makes the async defrag return the cancelled result.
                        Using Cts As New CancellationTokenSource()
                            Cts.Cancel()
                            Dim Result = Cs.DefragmentAsync(ChunkedStream.DefragTypes.Rebuild, Nothing, Cts.Token).GetAwaiter().GetResult()
                            AssertEqual(-1L, Result, "Cancelled DefragmentAsync should return -1.")
                        End Using

                        AssertBytesEqual(Expected, Cs.ToArray(), "Cancelled DefragmentAsync corrupted the stream.")
                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Function MakeOptions(Encrypted As Boolean, ChunkSize As Integer) As ChunkedStream.ChunkedStreamOptions

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = ChunkSize}
                If Encrypted Then Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(4321))
                Return Options

            End Function

            ''' <summary>
            ''' A seekable in-memory stream implementing <see cref="IPositionedStreamAsync" />,
            ''' counting async versus sync positioned calls so a test can prove the async path
            ''' does not silently fall back to the synchronous positioned methods.
            ''' </summary>
            Private NotInheritable Class AsyncPositionedMemoryStream
                Inherits Stream
                Implements IPositionedStreamAsync

                Private ReadOnly _Inner As MemoryStream
                Private ReadOnly _Capabilities As PositionedIoCapabilities
                Private ReadOnly _Gate As New Object()

                Public Sub New(Capabilities As PositionedIoCapabilities)
                    _Inner = New MemoryStream()
                    _Capabilities = Capabilities
                End Sub

                Public Property ReadAtAsyncCalls As Integer
                Public Property WriteAtAsyncCalls As Integer
                Public Property SyncReadAtCallsSinceReset As Integer
                Public Property SyncWriteAtCallsSinceReset As Integer

                Public Sub ResetCounters()
                    ReadAtAsyncCalls = 0
                    WriteAtAsyncCalls = 0
                    SyncReadAtCallsSinceReset = 0
                    SyncWriteAtCallsSinceReset = 0
                End Sub

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return _Capabilities
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer) As Integer _
                    Implements IPositionedStream.ReadAt

                    SyncLock _Gate
                        SyncReadAtCallsSinceReset += 1
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock

                End Function

                Public Sub WriteAt(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer) _
                    Implements IPositionedStream.WriteAt

                    SyncLock _Gate
                        SyncWriteAtCallsSinceReset += 1
                        If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                        _Inner.Position = PhysicalOffset
                        _Inner.Write(Buffer, BufferOffset, Count)
                    End SyncLock

                End Sub

                Public Async Function ReadAtAsync(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer,
                                                  CancellationToken As CancellationToken) As Task(Of Integer) _
                                                  Implements IPositionedStreamAsync.ReadAtAsync

                    Await Task.Yield()
                    CancellationToken.ThrowIfCancellationRequested()

                    SyncLock _Gate
                        ReadAtAsyncCalls += 1
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock

                End Function

                Public Async Function WriteAtAsync(PhysicalOffset As Long, Buffer As Byte(), BufferOffset As Integer, Count As Integer,
                                                   CancellationToken As CancellationToken) As Task _
                                                   Implements IPositionedStreamAsync.WriteAtAsync

                    Await Task.Yield()
                    CancellationToken.ThrowIfCancellationRequested()

                    SyncLock _Gate
                        WriteAtAsyncCalls += 1
                        If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                        _Inner.Position = PhysicalOffset
                        _Inner.Write(Buffer, BufferOffset, Count)
                    End SyncLock

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
                        Return True
                    End Get
                End Property

                Public Overrides ReadOnly Property Length As Long
                    Get
                        SyncLock _Gate
                            Return _Inner.Length
                        End SyncLock
                    End Get
                End Property

                Public Overrides Property Position As Long
                    Get
                        SyncLock _Gate
                            Return _Inner.Position
                        End SyncLock
                    End Get
                    Set
                        SyncLock _Gate
                            _Inner.Position = Value
                        End SyncLock
                    End Set
                End Property

                Public Overrides Sub Flush()
                    SyncLock _Gate
                        _Inner.Flush()
                    End SyncLock
                End Sub

                Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
                    SyncLock _Gate
                        Return _Inner.Read(Buffer, Offset, Count)
                    End SyncLock
                End Function

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
                    SyncLock _Gate
                        _Inner.Write(Buffer, Offset, Count)
                    End SyncLock
                End Sub

                Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                    SyncLock _Gate
                        Return _Inner.Seek(Offset, Origin)
                    End SyncLock
                End Function

                Public Overrides Sub SetLength(Value As Long)
                    SyncLock _Gate
                        _Inner.SetLength(Value)
                    End SyncLock
                End Sub

                Protected Overrides Sub Dispose(Disposing As Boolean)
                    If Disposing Then _Inner.Dispose()
                    MyBase.Dispose(Disposing)
                End Sub

            End Class

        End Class

    End Class

End Namespace
