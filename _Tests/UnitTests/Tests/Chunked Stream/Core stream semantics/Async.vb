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

                            Cs.Validate()

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

                        Cs.Validate()

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

                        Cs.Validate()

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
                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

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
                Public Property SyncReadAtCallsSinceReset As Integer

                Public Sub ResetCounters()
                    ReadAtAsyncCalls = 0
                    SyncReadAtCallsSinceReset = 0
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
