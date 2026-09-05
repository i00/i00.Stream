Imports System.IO
Imports System.Threading
Imports i00.Streams

Namespace Tests

    Partial Class DeveloperConfidence

        ''' <summary>
        ''' ChunkedStream is a single-writer type, but every public operation takes the
        ''' state lock, so concurrent readers - and a reader racing a writer - must never
        ''' see a torn snapshot, deadlock or throw. These are smoke tests for that lock.
        ''' </summary>
        Public NotInheritable Class Concurrency

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ManyConcurrentReadersSeeConsistentData()

                Using Ms As New MemoryStream()

                    Dim Expected As Byte()

                    Using Cs = ChunkedStream.Open(Ms)
                        Expected = GenerateRandomData(Cs.Options.ChunkSize * 4, 6601)
                        Cs.Write(0, Expected)
                        Cs.Validate().ThrowIfErrors()

                        Dim Failure As Exception = Nothing

                        Dim Body =
                            Sub()
                                Try
                                    For Iteration = 0 To 15
                                        AssertBytesEqual(Expected, Cs.ToArray(), "Concurrent reader saw wrong data.")

                                        Dim Slab(4095) As Byte
                                        Dim Read = Cs.Read(1234, Slab, 0, Slab.Length)
                                        AssertEqual(Slab.Length, Read, "Short read under concurrency.")
                                        AssertBytesEqual(Slice(Expected, 1234, Slab.Length), Slab, "Concurrent positional read mismatch.")

                                        Dim Snapshot = Cs.GetStructure()
                                        AssertEqual(CLng(Expected.Length), Snapshot.LogicalLength, "Concurrent GetStructure saw wrong length.")

                                        Cs.Validate().ThrowIfErrors()
                                    Next
                                Catch Ex As Exception
                                    Interlocked.CompareExchange(Failure, Ex, Nothing)
                                End Try
                            End Sub

                        RunOnThreads(6, Body)

                        If Failure IsNot Nothing Then
                            Throw New Exception("A concurrent reader failed: " & Failure.Message, Failure)
                        End If

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ReadersRacingAWriterNeverSeeATornSnapshot()

                Using Ms As New MemoryStream()

                    Dim Length = ChunkedStream.DefaultChunkSize * 2

                    Dim Versions As New List(Of Byte())()
                    For Version = 0 To 7
                        Versions.Add(GenerateRandomData(Length, 6700 + Version))
                    Next

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(0, Versions(0))

                        Dim Failure As Exception = Nothing

                        Dim KnownSet As New HashSet(Of String)(
                            Versions.Select(Function(v) Convert.ToBase64String(v)))

                        Dim Reader =
                            Sub()
                                Try
                                    For Iteration = 0 To 79
                                        Dim Snapshot = Cs.ToArray()
                                        AssertEqual(CLng(Length), CLng(Snapshot.Length), "Reader saw a wrong-length snapshot.")
                                        AssertTrue(
                                            KnownSet.Contains(Convert.ToBase64String(Snapshot)),
                                            "Reader saw a snapshot that is neither a committed version - a torn write.")
                                        Thread.Yield()
                                    Next
                                Catch Ex As Exception
                                    Interlocked.CompareExchange(Failure, Ex, Nothing)
                                End Try
                            End Sub

                        Dim Writer =
                            Sub()
                                Try
                                    For Round = 0 To 5
                                        For Version = 1 To Versions.Count - 1
                                            Cs.Write(0, Versions(Version))
                                        Next
                                    Next
                                Catch Ex As Exception
                                    Interlocked.CompareExchange(Failure, Ex, Nothing)
                                End Try
                            End Sub

                        Dim Threads As New List(Of Thread)()
                        For ReaderIndex = 0 To 4
                            Threads.Add(New Thread(Sub() Reader()))
                        Next
                        Threads.Add(New Thread(Sub() Writer()))

                        For Each T In Threads : T.Start() : Next
                        For Each T In Threads : T.Join() : Next

                        If Failure IsNot Nothing Then
                            Throw New Exception("A racing reader or the writer failed: " & Failure.Message, Failure)
                        End If

                        AssertBytesEqual(Versions(Versions.Count - 1), Cs.ToArray(), "Final content is wrong after the write race.")
                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Large positional reads (ToArray, Read(LogicalOffset, ...)) take the state lock
            ''' shared, so several run at once. This drives that deterministically: the backing
            ''' store makes every reader block inside a chunk read until all of them have
            ''' arrived, which can only complete if the reads truly overlap. A serialising read
            ''' path would leave each reader waiting alone until the gate timed out.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ConcurrentPositionalReadsOverlapRatherThanSerialise()

                Const ReaderCount As Integer = 4

                Using Backing As New GatedReadStream(ReaderCount)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(6801))
                    }

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Dim Expected = GenerateRandomData(Cs.Options.ChunkSize * 4, 6802)
                        Cs.Write(0, Expected)
                        Cs.Validate().ThrowIfErrors()

                        Dim Failure As Exception = Nothing

                        Backing.ArmGate()

                        Dim Body =
                            Sub()
                                Try
                                    AssertBytesEqual(Expected, Cs.ToArray(), "A gated concurrent reader saw wrong data.")

                                    Dim Slab(4095) As Byte
                                    Dim Read = Cs.Read(2048, Slab, 0, Slab.Length)
                                    AssertEqual(Slab.Length, Read, "Short gated positional read.")
                                    AssertBytesEqual(Slice(Expected, 2048, Slab.Length), Slab, "Gated positional read mismatch.")
                                Catch Ex As Exception
                                    Interlocked.CompareExchange(Failure, Ex, Nothing)
                                End Try
                            End Sub

                        RunOnThreads(ReaderCount, Body)

                        If Failure IsNot Nothing Then
                            Throw New Exception("A gated concurrent reader failed: " & Failure.Message, Failure)
                        End If

                        AssertTrue(
                            Backing.MaxConcurrentReaders >= ReaderCount,
                            $"Reads never overlapped ({Backing.MaxConcurrentReaders} of {ReaderCount} at once) - the read path still serialises.")

                    End Using

                End Using

            End Sub

            Private Shared Sub RunOnThreads(Count As Integer, Body As Action)

                Dim Threads As New List(Of Thread)()

                For Index = 0 To Count - 1
                    Threads.Add(New Thread(Sub() Body()))
                Next

                For Each T In Threads : T.Start() : Next
                For Each T In Threads : T.Join() : Next

            End Sub

            ''' <summary>
            ''' A seekable in-memory backing store that, once armed, makes every reader block
            ''' inside a positioned chunk read until <c>ReaderCount</c> of them are present, and
            ''' records the peak overlap. It advertises lock-free reads so ChunkedStream does
            ''' not funnel the reads through its own physical-I/O lock first.
            ''' </summary>
            Private NotInheritable Class GatedReadStream
                Inherits Stream
                Implements IPositionedStream

                Private Const GateTimeoutMs As Integer = 5000

                Private ReadOnly _Inner As New MemoryStream()
                Private ReadOnly _Sync As New Object()
                Private ReadOnly _Arrived As CountdownEvent
                Private ReadOnly _ArrivedThreads As New HashSet(Of Integer)()
                Private _Armed As Boolean
                Private _InFlight As Integer
                Private _MaxConcurrentReaders As Integer

                Public Sub New(ReaderCount As Integer)
                    _Arrived = New CountdownEvent(ReaderCount)
                End Sub

                Public Sub ArmGate()
                    SyncLock _Sync
                        _Armed = True
                    End SyncLock
                End Sub

                Public ReadOnly Property MaxConcurrentReaders As Integer
                    Get
                        SyncLock _Sync
                            Return _MaxConcurrentReaders
                        End SyncLock
                    End Get
                End Property

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return PositionedIoCapabilities.LockFreeReads
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long,
                                       Buffer As Byte(),
                                       BufferOffset As Integer,
                                       Count As Integer) As Integer Implements IPositionedStream.ReadAt

                    ' Each reader thread rendezvouses exactly once, on its first armed read.
                    ' Later reads (and any read from another thread once the rendezvous is
                    ' done) pass straight through, so the peak overlap is exactly the number
                    ' of threads that met at the gate.
                    Dim WaitAtGate As Boolean

                    SyncLock _Sync
                        If _Armed AndAlso _Arrived.CurrentCount > 0 AndAlso
                           _ArrivedThreads.Add(Thread.CurrentThread.ManagedThreadId) Then

                            WaitAtGate = True
                            _InFlight += 1
                            _MaxConcurrentReaders = Math.Max(_MaxConcurrentReaders, _InFlight)
                            _Arrived.Signal()
                        End If
                    End SyncLock

                    If WaitAtGate Then
                        _Arrived.Wait(GateTimeoutMs)
                        SyncLock _Sync
                            _InFlight -= 1
                        End SyncLock
                    End If

                    SyncLock _Sync
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock

                End Function

                Public Sub WriteAt(PhysicalOffset As Long,
                                   Buffer As Byte(),
                                   BufferOffset As Integer,
                                   Count As Integer) Implements IPositionedStream.WriteAt

                    SyncLock _Sync
                        If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                        _Inner.Position = PhysicalOffset
                        _Inner.Write(Buffer, BufferOffset, Count)
                    End SyncLock

                End Sub

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
                        SyncLock _Sync
                            Return _Inner.Length
                        End SyncLock
                    End Get
                End Property

                Public Overrides Property Position As Long
                    Get
                        SyncLock _Sync
                            Return _Inner.Position
                        End SyncLock
                    End Get
                    Set
                        SyncLock _Sync
                            _Inner.Position = Value
                        End SyncLock
                    End Set
                End Property

                Public Overrides Sub Flush()
                    SyncLock _Sync
                        _Inner.Flush()
                    End SyncLock
                End Sub

                Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
                    SyncLock _Sync
                        Return _Inner.Read(Buffer, Offset, Count)
                    End SyncLock
                End Function

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
                    SyncLock _Sync
                        _Inner.Write(Buffer, Offset, Count)
                    End SyncLock
                End Sub

                Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                    SyncLock _Sync
                        Return _Inner.Seek(Offset, Origin)
                    End SyncLock
                End Function

                Public Overrides Sub SetLength(Value As Long)
                    SyncLock _Sync
                        _Inner.SetLength(Value)
                    End SyncLock
                End Sub

                Protected Overrides Sub Dispose(Disposing As Boolean)
                    If Disposing Then
                        _Inner.Dispose()
                        _Arrived.Dispose()
                    End If
                    MyBase.Dispose(Disposing)
                End Sub

            End Class

            ''' <summary>
            ''' A single WriteAsync spanning enough chunks to trigger the parallel-CPU-prep
            ''' path (BuildExtentsInParallelAsync) now also batches its physical writes through
            ''' PlaceChunkRecordsAsync, which is allowed to issue them concurrently when the
            ''' backing store declares PositionedIoCapabilities.LockFreeWrites and implements
            ''' IPositionedStreamAsync. This drives that deterministically: every WriteAtAsync
            ''' call blocks (without occupying a thread - the wait is a real await, not a
            ''' blocking Wait) until WriterCount of them have arrived, which can only complete
            ''' if the writes truly overlap.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ConcurrentAsyncWritesOverlapWhenTheBackingStoreAllowsIt()

                Const WriterCount As Integer = 8
                Const ChunkSize As Integer = 4096

                Using Backing As New GatedWriteStreamAsync(WriterCount)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .MaxCryptoParallelism = WriterCount,
                        .MaxPhysicalWriteParallelism = WriterCount,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(6901))
                    }

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Dim Expected = GenerateRandomData(ChunkSize * WriterCount, 6902)

                        Backing.ArmGate()

                        Dim Written = Cs.WriteAsync(0, Expected).GetAwaiter().GetResult()
                        AssertEqual(Expected.Length, Written, "Gated async batched write reported the wrong count.")

                        Cs.Validate().ThrowIfErrors()
                        AssertBytesEqual(Expected, Cs.ToArray(), "Gated async batched write lost data.")

                        AssertTrue(
                            Backing.MaxConcurrentWriters >= WriterCount,
                            $"Writes never overlapped ({Backing.MaxConcurrentWriters} of {WriterCount} at once) - the batched write path still serialises.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' A seekable in-memory backing store that, once armed, makes every WriteAtAsync
            ''' block (via an awaited gate, not a blocking wait) until WriterCount of them are
            ''' outstanding at once, and records the peak overlap. Declares LockFreeWrites so
            ''' ChunkedStream does not funnel the writes through its own physical-I/O lock, and
            ''' implements IPositionedStreamAsync so PlaceChunkRecordsAsync's batched path (only
            ''' reachable via WriteAtAsync) is what gets exercised.
            ''' </summary>
            Private NotInheritable Class GatedWriteStreamAsync
                Inherits Stream
                Implements IPositionedStreamAsync

                Private Const GateTimeoutMs As Integer = 5000

                Private ReadOnly _Inner As New MemoryStream()
                Private ReadOnly _Sync As New Object()
                Private ReadOnly _TargetArrivals As Integer
                Private ReadOnly _Gate As New TaskCompletionSource(Of Boolean)()
                Private _Armed As Boolean
                Private _Arrivals As Integer
                Private _InFlight As Integer
                Private _MaxConcurrentWriters As Integer

                Public Sub New(WriterCount As Integer)
                    _TargetArrivals = WriterCount
                End Sub

                Public Sub ArmGate()
                    SyncLock _Sync
                        _Armed = True
                    End SyncLock
                End Sub

                Public ReadOnly Property MaxConcurrentWriters As Integer
                    Get
                        SyncLock _Sync
                            Return _MaxConcurrentWriters
                        End SyncLock
                    End Get
                End Property

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return PositionedIoCapabilities.LockFreeWrites
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long,
                                       Buffer As Byte(),
                                       BufferOffset As Integer,
                                       Count As Integer) As Integer Implements IPositionedStream.ReadAt
                    SyncLock _Sync
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock
                End Function

                Public Sub WriteAt(PhysicalOffset As Long,
                                   Buffer As Byte(),
                                   BufferOffset As Integer,
                                   Count As Integer) Implements IPositionedStream.WriteAt
                    SyncLock _Sync
                        If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                        _Inner.Position = PhysicalOffset
                        _Inner.Write(Buffer, BufferOffset, Count)
                    End SyncLock
                End Sub

                Public Function ReadAtAsync(PhysicalOffset As Long,
                                            Buffer As Byte(),
                                            BufferOffset As Integer,
                                            Count As Integer,
                                            CancellationToken As CancellationToken) As Task(Of Integer) _
                                            Implements IPositionedStreamAsync.ReadAtAsync
                    Return Task.FromResult(ReadAt(PhysicalOffset, Buffer, BufferOffset, Count))
                End Function

                Public Async Function WriteAtAsync(PhysicalOffset As Long,
                                                   Buffer As Byte(),
                                                   BufferOffset As Integer,
                                                   Count As Integer,
                                                   CancellationToken As CancellationToken) As Task _
                                                   Implements IPositionedStreamAsync.WriteAtAsync

                    ' Each call rendezvouses exactly once, up to _TargetArrivals - the gate opens
                    ' (releasing every waiter together) only once that many are outstanding at
                    ' the same time, so the recorded peak is a true concurrent-overlap count,
                    ' not just "several calls happened during the test".
                    Dim ShouldWaitAtGate As Boolean

                    SyncLock _Sync
                        If _Armed AndAlso _Arrivals < _TargetArrivals Then
                            _Arrivals += 1
                            _InFlight += 1
                            _MaxConcurrentWriters = Math.Max(_MaxConcurrentWriters, _InFlight)
                            ShouldWaitAtGate = True
                            If _Arrivals = _TargetArrivals Then _Gate.TrySetResult(True)
                        End If
                    End SyncLock

                    If ShouldWaitAtGate Then
                        Await Task.WhenAny(_Gate.Task, Task.Delay(GateTimeoutMs)).ConfigureAwait(False)
                        SyncLock _Sync
                            _InFlight -= 1
                        End SyncLock
                    End If

                    WriteAt(PhysicalOffset, Buffer, BufferOffset, Count)

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
                        SyncLock _Sync
                            Return _Inner.Length
                        End SyncLock
                    End Get
                End Property

                Public Overrides Property Position As Long
                    Get
                        SyncLock _Sync
                            Return _Inner.Position
                        End SyncLock
                    End Get
                    Set
                        SyncLock _Sync
                            _Inner.Position = Value
                        End SyncLock
                    End Set
                End Property

                Public Overrides Sub Flush()
                    SyncLock _Sync
                        _Inner.Flush()
                    End SyncLock
                End Sub

                Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
                    SyncLock _Sync
                        Return _Inner.Read(Buffer, Offset, Count)
                    End SyncLock
                End Function

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
                    SyncLock _Sync
                        _Inner.Write(Buffer, Offset, Count)
                    End SyncLock
                End Sub

                Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                    SyncLock _Sync
                        Return _Inner.Seek(Offset, Origin)
                    End SyncLock
                End Function

                Public Overrides Sub SetLength(Value As Long)
                    SyncLock _Sync
                        _Inner.SetLength(Value)
                    End SyncLock
                End Sub

                Protected Overrides Sub Dispose(Disposing As Boolean)
                    If Disposing Then _Inner.Dispose()
                    MyBase.Dispose(Disposing)
                End Sub

            End Class

            ''' <summary>
            ''' A single async bulk read spanning enough chunks to trigger the parallel-decrypt
            ''' path (ShouldReadRangeInParallel) now also fetches its distinct physical records
            ''' via ReadRangeInParallelAsync, which is allowed to issue those ReadAtAsync calls
            ''' concurrently when the backing store declares LockFreeReads and implements
            ''' IPositionedStreamAsync - mirroring the write-side proof above, for the read side.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ConcurrentAsyncBulkReadFetchesOverlapWhenTheBackingStoreAllowsIt()

                Const RecordCount As Integer = 8
                Const ChunkSize As Integer = 4096

                Using Backing As New GatedReadStreamAsync(RecordCount)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .MaxCryptoParallelism = RecordCount,
                        .MaxPhysicalReadParallelism = RecordCount,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7001))
                    }

                    Dim Expected = GenerateRandomData(ChunkSize * RecordCount, 7002)

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Cs.Write(0, Expected)
                        Cs.Validate().ThrowIfErrors()

                        Backing.ArmGate()

                        Dim ReadBuffer(Expected.Length - 1) As Byte
                        Dim ReadCount = Cs.ReadAsync(0, ReadBuffer, 0, ReadBuffer.Length).GetAwaiter().GetResult()

                        AssertEqual(Expected.Length, ReadCount, "Gated async bulk read reported the wrong count.")
                        AssertBytesEqual(Expected, ReadBuffer, "Gated async bulk read returned the wrong bytes.")

                        AssertTrue(
                            Backing.MaxConcurrentReaders >= RecordCount,
                            $"Reads never overlapped ({Backing.MaxConcurrentReaders} of {RecordCount} at once) - the bulk-read fetch path still serialises.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' A seekable in-memory backing store that, once armed, makes every ReadAtAsync
            ''' block (via an awaited gate, not a blocking wait) until RecordCount of them are
            ''' outstanding at once, and records the peak overlap. Declares LockFreeReads so
            ''' ChunkedStream does not funnel the reads through its own physical-I/O lock, and
            ''' implements IPositionedStreamAsync so ReadRangeInParallelAsync's concurrent fetch
            ''' path (only reachable via ReadAtAsync) is what gets exercised.
            ''' </summary>
            Private NotInheritable Class GatedReadStreamAsync
                Inherits Stream
                Implements IPositionedStreamAsync

                Private Const GateTimeoutMs As Integer = 5000

                Private ReadOnly _Inner As New MemoryStream()
                Private ReadOnly _Sync As New Object()
                Private ReadOnly _TargetArrivals As Integer
                Private ReadOnly _Gate As New TaskCompletionSource(Of Boolean)()
                Private _Armed As Boolean
                Private _Arrivals As Integer
                Private _InFlight As Integer
                Private _MaxConcurrentReaders As Integer

                Public Sub New(RecordCount As Integer)
                    _TargetArrivals = RecordCount
                End Sub

                Public Sub ArmGate()
                    SyncLock _Sync
                        _Armed = True
                    End SyncLock
                End Sub

                Public ReadOnly Property MaxConcurrentReaders As Integer
                    Get
                        SyncLock _Sync
                            Return _MaxConcurrentReaders
                        End SyncLock
                    End Get
                End Property

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return PositionedIoCapabilities.LockFreeReads
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long,
                                       Buffer As Byte(),
                                       BufferOffset As Integer,
                                       Count As Integer) As Integer Implements IPositionedStream.ReadAt
                    SyncLock _Sync
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock
                End Function

                Public Sub WriteAt(PhysicalOffset As Long,
                                   Buffer As Byte(),
                                   BufferOffset As Integer,
                                   Count As Integer) Implements IPositionedStream.WriteAt
                    SyncLock _Sync
                        If PhysicalOffset > _Inner.Length Then _Inner.SetLength(PhysicalOffset)
                        _Inner.Position = PhysicalOffset
                        _Inner.Write(Buffer, BufferOffset, Count)
                    End SyncLock
                End Sub

                Public Function WriteAtAsync(PhysicalOffset As Long,
                                             Buffer As Byte(),
                                             BufferOffset As Integer,
                                             Count As Integer,
                                             CancellationToken As CancellationToken) As Task _
                                             Implements IPositionedStreamAsync.WriteAtAsync
                    WriteAt(PhysicalOffset, Buffer, BufferOffset, Count)
                    Return Task.CompletedTask
                End Function

                Public Async Function ReadAtAsync(PhysicalOffset As Long,
                                                  Buffer As Byte(),
                                                  BufferOffset As Integer,
                                                  Count As Integer,
                                                  CancellationToken As CancellationToken) As Task(Of Integer) _
                                                  Implements IPositionedStreamAsync.ReadAtAsync

                    ' Same hard-rendezvous gate as GatedWriteStreamAsync.WriteAtAsync, mirrored
                    ' for reads: nothing proceeds until _TargetArrivals calls are all
                    ' outstanding at once, so the recorded peak is a true overlap count.
                    Dim ShouldWaitAtGate As Boolean

                    SyncLock _Sync
                        If _Armed AndAlso _Arrivals < _TargetArrivals Then
                            _Arrivals += 1
                            _InFlight += 1
                            _MaxConcurrentReaders = Math.Max(_MaxConcurrentReaders, _InFlight)
                            ShouldWaitAtGate = True
                            If _Arrivals = _TargetArrivals Then _Gate.TrySetResult(True)
                        End If
                    End SyncLock

                    If ShouldWaitAtGate Then
                        Await Task.WhenAny(_Gate.Task, Task.Delay(GateTimeoutMs)).ConfigureAwait(False)
                        SyncLock _Sync
                            _InFlight -= 1
                        End SyncLock
                    End If

                    Return ReadAt(PhysicalOffset, Buffer, BufferOffset, Count)

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
                        SyncLock _Sync
                            Return _Inner.Length
                        End SyncLock
                    End Get
                End Property

                Public Overrides Property Position As Long
                    Get
                        SyncLock _Sync
                            Return _Inner.Position
                        End SyncLock
                    End Get
                    Set
                        SyncLock _Sync
                            _Inner.Position = Value
                        End SyncLock
                    End Set
                End Property

                Public Overrides Sub Flush()
                    SyncLock _Sync
                        _Inner.Flush()
                    End SyncLock
                End Sub

                Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
                    SyncLock _Sync
                        Return _Inner.Read(Buffer, Offset, Count)
                    End SyncLock
                End Function

                Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
                    SyncLock _Sync
                        _Inner.Write(Buffer, Offset, Count)
                    End SyncLock
                End Sub

                Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
                    SyncLock _Sync
                        Return _Inner.Seek(Offset, Origin)
                    End SyncLock
                End Function

                Public Overrides Sub SetLength(Value As Long)
                    SyncLock _Sync
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
