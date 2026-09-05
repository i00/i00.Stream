' ================================================================================
' PooledPositionedFileStream
' ================================================================================
'
' Purpose
'   - PositionedFileStream's ReadAtAsync/WriteAtAsync offload a synchronous P/Invoke
'     ReadFile/WriteFile call onto the thread pool (Task.Run) - a thread is held for
'     the whole duration of each call. That is fine when the underlying I/O is fast
'     (a warm page cache), but under real, slower I/O - or a burst of many concurrent
'     calls against a not-yet-warmed-up thread pool - holding a whole thread per
'     pending operation is real overhead, and was measured to sometimes make things
'     worse, not better (see the session notes on read-parallelism benchmarking).
'
'   - True "no thread held while an I/O is pending" async on Windows means IOCP (I/O
'     Completion Ports): ReadFile/WriteFile against a handle opened with
'     FILE_FLAG_OVERLAPPED return immediately, and completion is delivered as an
'     event the CLR's own thread-pool-integrated dispatcher picks up - never a
'     thread blocked for the wait. The direct .NET API for driving this yourself,
'     System.Threading.ThreadPoolBoundHandle.AllocateNativeOverlapped, returns
'     NativeOverlapped* - an unsafe pointer type, which VB.NET cannot express at all
'     (no unsafe/pointer support, unlike C#), so that API is not usable from this
'     project without a second-language helper assembly.
'
'   - This class gets the same practical benefit - genuinely no thread held for a
'     pending I/O - by a different, fully-managed route: a small pool of ordinary
'     FileStream instances opened on the same path with FileOptions.Asynchronous
'     (useAsync:=True). That flag is what makes .NET's OWN FileStream.ReadAsync /
'     WriteAsync use real IOCP internally on Windows - so borrowing one, awaiting
'     its ReadAsync/WriteAsync, and returning it gets genuine IOCP-backed positioned
'     I/O with zero P/Invoke and zero unsafe code, at the cost of N open handles to
'     the same file instead of one.
'
' Concurrency
'   - Declares PositionedIoCapabilities.Full: ReadAt/WriteAt each borrow a pool
'     member (blocking only until one is free, not for the I/O itself in the async
'     case), set its Position (safe - only the borrower touches that instance while
'     checked out), and use it exclusively for the duration of that one call.
'   - Pool size is fixed at construction. It should be at least as large as whatever
'     Options.MaxPhysicalReadParallelism / MaxPhysicalWriteParallelism the caller
'     configures - a smaller pool still works, it just caps how much of that
'     configured parallelism can actually be in flight at once.
'   - A separate, dedicated handle serves the plain Stream API (Position/Read/Write/
'     Seek/SetLength/Flush), since ChunkedStream only ever calls those from one
'     logical caller at a time - no borrowing/contention needed there.
'
' ================================================================================

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Threading

Namespace Streams

    ''' <summary>
    ''' A file-backed <see cref="Stream" /> that implements <see cref="IPositionedStreamAsync" />
    ''' with genuinely non-blocking (IOCP-backed) async positioned I/O, via a small pool of
    ''' <see cref="FileStream" /> handles opened with <see cref="FileOptions.Asynchronous" />
    ''' rather than P/Invoke. See the file header remarks for why this route was chosen over
    ''' the pointer-typed <see cref="Threading.ThreadPoolBoundHandle" /> API, which VB.NET
    ''' cannot call. Declares <see cref="PositionedIoCapabilities.Full" /> and implements
    ''' <see cref="IDurableFlush" />.
    ''' </summary>
    Public NotInheritable Class PooledPositionedFileStream
        Inherits Stream
        Implements IPositionedStreamAsync, IDurableFlush

        Public Const DefaultPoolSize As Integer = 8

        Private ReadOnly _Primary As FileStream
        Private ReadOnly _Pool As New ConcurrentBag(Of FileStream)()
        Private ReadOnly _PoolAvailability As SemaphoreSlim
        Private _Disposed As Boolean

        ''' <summary>
        ''' Opens <paramref name="PoolSize" /> additional handles on <paramref name="Path" />
        ''' (plus one dedicated handle for the plain <see cref="Stream" /> API), all with
        ''' <see cref="FileShare" /> extended to permit that - <paramref name="Share" /> is used
        ''' for every handle, so it must already allow the other handles this class itself
        ''' opens (typically <see cref="FileShare.ReadWrite" /> or <see cref="FileShare.None" />
        ''' is too restrictive for more than the first handle).
        ''' </summary>
        Public Sub New(Path As String, Mode As FileMode, Access As FileAccess, Share As FileShare,
                       Optional PoolSize As Integer = DefaultPoolSize)

            If PoolSize <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(PoolSize))

            _Primary = New FileStream(Path, Mode, Access, Share, 1, FileOptions.None)

            ' Every pooled handle after the first opens the file that now already exists -
            ' FileMode.Create/CreateNew would fail or re-truncate on a second open, so those
            ' become OpenOrCreate/Open respectively for the pool (the primary handle above
            ' already did whatever creation semantics were requested).
            Dim PoolMode = If(Mode = FileMode.Create OrElse Mode = FileMode.CreateNew OrElse Mode = FileMode.OpenOrCreate,
                              FileMode.OpenOrCreate,
                              FileMode.Open)

            _PoolAvailability = New SemaphoreSlim(PoolSize, PoolSize)
            For i = 1 To PoolSize
                _Pool.Add(New FileStream(Path, PoolMode, Access, Share, 1, FileOptions.Asynchronous))
            Next

        End Sub

        ''' <summary>
        ''' Durably flushes the file (<see cref="FileStream.Flush(Boolean)" /> with
        ''' <c>flushToDisk:=True</c>) via the dedicated primary handle - fsync is a
        ''' file-level, not handle-level, guarantee, so it covers bytes written through the
        ''' pooled handles too. Implements <see cref="IDurableFlush" />, so
        ''' <c>ChunkedStream.Open</c> picks this up automatically.
        ''' </summary>
        Public Sub FlushDurable() Implements IDurableFlush.FlushDurable
            _Primary.Flush(True)
        End Sub

        Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
            Implements IPositionedStream.PositionedIoCapabilities
            Get
                Return PositionedIoCapabilities.Full
            End Get
        End Property

        Private Function BorrowPooled() As FileStream
            _PoolAvailability.Wait()
            Dim Result As FileStream = Nothing
            While _Pool.TryTake(Result) = False
                ' The semaphore only admits as many waiters as there are bag entries, so this
                ' spins at most briefly against a concurrent Add still in flight - it cannot
                ' spin forever.
            End While
            Return Result
        End Function

        Private Async Function BorrowPooledAsync(CancellationToken As CancellationToken) As Task(Of FileStream)
            Await _PoolAvailability.WaitAsync(CancellationToken).ConfigureAwait(False)
            Dim Result As FileStream = Nothing
            While _Pool.TryTake(Result) = False
            End While
            Return Result
        End Function

        Private Sub ReturnPooled(Instance As FileStream)
            _Pool.Add(Instance)
            _PoolAvailability.Release()
        End Sub

        ''' <summary>
        ''' Reads at an explicit offset without touching this class's own <see cref="Position" />.
        ''' Safe to call concurrently with any other ReadAt/WriteAt - each call exclusively owns
        ''' a borrowed pool member for its duration.
        ''' </summary>
        Public Function ReadAt(PhysicalOffset As Long,
                              Buffer As Byte(),
                              BufferOffset As Integer,
                              Count As Integer) As Integer Implements IPositionedStream.ReadAt

            If Count = 0 Then Return 0
            Dim Instance = BorrowPooled()
            Try
                Instance.Position = PhysicalOffset
                Return Instance.Read(Buffer, BufferOffset, Count)
            Finally
                ReturnPooled(Instance)
            End Try

        End Function

        ''' <summary>
        ''' Writes at an explicit offset without touching this class's own <see cref="Position" />.
        ''' Safe to call concurrently with any other ReadAt/WriteAt.
        ''' </summary>
        Public Sub WriteAt(PhysicalOffset As Long,
                           Buffer As Byte(),
                           BufferOffset As Integer,
                           Count As Integer) Implements IPositionedStream.WriteAt

            If Count = 0 Then Return
            Dim Instance = BorrowPooled()
            Try
                Instance.Position = PhysicalOffset
                Instance.Write(Buffer, BufferOffset, Count)
            Finally
                ReturnPooled(Instance)
            End Try

        End Sub

        ''' <summary>
        ''' Genuinely non-blocking: borrows a pool member (awaiting, not blocking, if none are
        ''' free) and awaits its real IOCP-backed <see cref="FileStream.ReadAsync" /> - no
        ''' thread is held for the duration of the actual I/O.
        ''' </summary>
        Public Async Function ReadAtAsync(PhysicalOffset As Long,
                                          Buffer As Byte(),
                                          BufferOffset As Integer,
                                          Count As Integer,
                                          CancellationToken As CancellationToken) As Task(Of Integer) _
                                          Implements IPositionedStreamAsync.ReadAtAsync

            If Count = 0 Then Return 0
            Dim Instance = Await BorrowPooledAsync(CancellationToken).ConfigureAwait(False)
            Try
                Instance.Position = PhysicalOffset
                Return Await Instance.ReadAsync(Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
            Finally
                ReturnPooled(Instance)
            End Try

        End Function

        ''' <summary>
        ''' Genuinely non-blocking - see the remarks on <see cref="ReadAtAsync" />.
        ''' </summary>
        Public Async Function WriteAtAsync(PhysicalOffset As Long,
                                           Buffer As Byte(),
                                           BufferOffset As Integer,
                                           Count As Integer,
                                           CancellationToken As CancellationToken) As Task _
                                           Implements IPositionedStreamAsync.WriteAtAsync

            If Count = 0 Then Return
            Dim Instance = Await BorrowPooledAsync(CancellationToken).ConfigureAwait(False)
            Try
                Instance.Position = PhysicalOffset
                Await Instance.WriteAsync(Buffer, BufferOffset, Count, CancellationToken).ConfigureAwait(False)
            Finally
                ReturnPooled(Instance)
            End Try

        End Function

        ' Everything below is a single logical caller at a time under ChunkedStream's own
        ' state lock, delegated to the dedicated primary handle - nothing here contends with
        ' ReadAt/WriteAt's pooled handles.

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return _Primary.CanRead
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return _Primary.CanSeek
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return _Primary.CanWrite
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return _Primary.Length
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return _Primary.Position
            End Get
            Set
                _Primary.Position = Value
            End Set
        End Property

        Public Overrides Sub Flush()
            _Primary.Flush()
        End Sub

        Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
            Return _Primary.Read(Buffer, Offset, Count)
        End Function

        Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
            _Primary.Write(Buffer, Offset, Count)
        End Sub

        Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
            Return _Primary.Seek(Offset, Origin)
        End Function

        Public Overrides Sub SetLength(Value As Long)
            _Primary.SetLength(Value)
        End Sub

        Protected Overrides Sub Dispose(Disposing As Boolean)
            If _Disposed Then Return
            _Disposed = True
            If Disposing Then
                _Primary.Dispose()
                Dim Instance As FileStream = Nothing
                While _Pool.TryTake(Instance)
                    Instance.Dispose()
                End While
                _PoolAvailability.Dispose()
            End If
            MyBase.Dispose(Disposing)
        End Sub

    End Class

End Namespace
