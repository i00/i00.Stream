' ================================================================================
' PositionedMemoryStream
' ================================================================================
'
' Purpose
'   - A general-purpose, production IPositionedStreamAsync implementation over an
'     in-memory buffer. Not primarily about speed - a plain MemoryStream wrapped with
'     a lock would already be "fast enough" for most in-memory uses, and there is no
'     real device queue depth to exploit - but it is genuinely useful wherever a
'     caller wants ChunkedStream's lock-free concurrent-read/write code paths
'     exercised without touching a real file: tests, transient/scratch archives,
'     benchmarking ChunkedStream's own overhead in isolation from real storage
'     latency, or an in-memory staging area before a batch write-out to disk.
'
' Concurrency
'   - Backed by a growable byte array protected by a ReaderWriterLockSlim: multiple
'     ReadAt/ReadAtAsync calls run genuinely concurrently (shared read lock), while
'     WriteAt/WriteAtAsync take the exclusive write lock. Declares
'     PositionedIoCapabilities.Full accordingly.
'   - ReadAtAsync/WriteAtAsync complete synchronously (Task.FromResult /
'     Task.CompletedTask) rather than offloading to the thread pool - there is no
'     blocking I/O to hide for an in-memory copy, so a worker-thread hop would be
'     pure overhead with nothing to gain, unlike PositionedFileStream's file-backed
'     ReadAt/WriteAt.
'
' Not durable
'   - Does not implement IDurableFlush: there is nothing to fsync. Flush() is a
'     legitimate no-op, exactly like MemoryStream's own.
'
' ================================================================================

Imports System.IO
Imports System.Threading

Namespace Streams

    ''' <summary>
    ''' An in-memory <see cref="Stream" /> that also implements
    ''' <see cref="IPositionedStreamAsync" /> with genuinely concurrent reads (a shared
    ''' <see cref="ReaderWriterLockSlim" />) and declares
    ''' <see cref="PositionedIoCapabilities.Full" />. See the file header remarks for why this
    ''' exists despite there being no device queue depth to exploit in memory.
    ''' </summary>
    Public NotInheritable Class PositionedMemoryStream
        Inherits Stream
        Implements IPositionedStreamAsync

        Private ReadOnly _Lock As New ReaderWriterLockSlim()
        Private _Buffer As Byte()
        Private _Length As Long
        Private _Position As Long
        Private _Disposed As Boolean

        Public Sub New()
            Me.New(0)
        End Sub

        Public Sub New(InitialCapacity As Integer)
            If InitialCapacity < 0 Then Throw New ArgumentOutOfRangeException(NameOf(InitialCapacity))
            _Buffer = If(InitialCapacity = 0, Array.Empty(Of Byte)(), New Byte(InitialCapacity - 1) {})
        End Sub

        ''' <summary>
        ''' Wraps a copy of <paramref name="InitialBytes" /> as the stream's starting content,
        ''' for reopening a previously-captured <see cref="ToArray" /> result.
        ''' </summary>
        Public Sub New(InitialBytes As Byte())
            If InitialBytes Is Nothing Then Throw New ArgumentNullException(NameOf(InitialBytes))
            _Buffer = CType(InitialBytes.Clone(), Byte())
            _Length = InitialBytes.LongLength
        End Sub

        ''' <summary>Returns a copy of the stream's current content, independent of Position.</summary>
        Public Function ToArray() As Byte()
            _Lock.EnterReadLock()
            Try
                Dim Result(CInt(_Length) - 1) As Byte
                If _Length > 0 Then System.Buffer.BlockCopy(_Buffer, 0, Result, 0, CInt(_Length))
                Return Result
            Finally
                _Lock.ExitReadLock()
            End Try
        End Function

        Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
            Implements IPositionedStream.PositionedIoCapabilities
            Get
                Return PositionedIoCapabilities.Full
            End Get
        End Property

        Private Shared Sub ValidateArgs(Buffer As Byte(), BufferOffset As Integer, Count As Integer)
            If Buffer Is Nothing Then Throw New ArgumentNullException(NameOf(Buffer))
            If BufferOffset < 0 OrElse Count < 0 OrElse BufferOffset > Buffer.Length - Count Then
                Throw New ArgumentOutOfRangeException(NameOf(Count))
            End If
        End Sub

        ' Called only while holding the write lock.
        Private Sub EnsureCapacity(RequiredLength As Long)
            If RequiredLength <= _Buffer.LongLength Then Return
            If RequiredLength > Integer.MaxValue Then
                Throw New IOException("PositionedMemoryStream cannot grow beyond 2 GB.")
            End If
            Dim NewCapacity = Math.Max(RequiredLength, Math.Max(256L, _Buffer.LongLength * 2L))
            If NewCapacity > Integer.MaxValue Then NewCapacity = RequiredLength
            Dim NewBuffer(CInt(NewCapacity) - 1) As Byte
            If _Length > 0 Then System.Buffer.BlockCopy(_Buffer, 0, NewBuffer, 0, CInt(_Length))
            _Buffer = NewBuffer
        End Sub

        ''' <summary>
        ''' Reads at an explicit offset without touching <see cref="Position" />. Safe to call
        ''' concurrently with any other ReadAt/ReadAtAsync (shared lock); serialises against a
        ''' concurrent WriteAt/WriteAtAsync.
        ''' </summary>
        Public Function ReadAt(PhysicalOffset As Long,
                              Buffer As Byte(),
                              BufferOffset As Integer,
                              Count As Integer) As Integer Implements IPositionedStream.ReadAt

            ValidateArgs(Buffer, BufferOffset, Count)
            If PhysicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalOffset))
            If Count = 0 Then Return 0

            _Lock.EnterReadLock()
            Try
                If PhysicalOffset >= _Length Then Return 0
                Dim Available = CInt(Math.Min(CLng(Count), _Length - PhysicalOffset))
                System.Buffer.BlockCopy(_Buffer, CInt(PhysicalOffset), Buffer, BufferOffset, Available)
                Return Available
            Finally
                _Lock.ExitReadLock()
            End Try

        End Function

        ''' <summary>
        ''' Writes at an explicit offset without touching <see cref="Position" />, growing the
        ''' stream if necessary. Takes the exclusive write lock - serialises against every
        ''' other ReadAt/WriteAt.
        ''' </summary>
        Public Sub WriteAt(PhysicalOffset As Long,
                           Buffer As Byte(),
                           BufferOffset As Integer,
                           Count As Integer) Implements IPositionedStream.WriteAt

            ValidateArgs(Buffer, BufferOffset, Count)
            If PhysicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalOffset))
            If Count = 0 Then Return

            _Lock.EnterWriteLock()
            Try
                Dim RequiredLength = PhysicalOffset + Count
                EnsureCapacity(RequiredLength)
                System.Buffer.BlockCopy(Buffer, BufferOffset, _Buffer, CInt(PhysicalOffset), Count)
                If RequiredLength > _Length Then _Length = RequiredLength
            Finally
                _Lock.ExitWriteLock()
            End Try

        End Sub

        ''' <summary>
        ''' Completes synchronously (<see cref="Task.FromResult(Of TResult)" />) - an in-memory
        ''' copy has no blocking I/O to hide behind a worker-thread hop.
        ''' </summary>
        Public Function ReadAtAsync(PhysicalOffset As Long,
                                    Buffer As Byte(),
                                    BufferOffset As Integer,
                                    Count As Integer,
                                    CancellationToken As CancellationToken) As Task(Of Integer) _
                                    Implements IPositionedStreamAsync.ReadAtAsync

            CancellationToken.ThrowIfCancellationRequested()
            Return Task.FromResult(ReadAt(PhysicalOffset, Buffer, BufferOffset, Count))

        End Function

        ''' <summary>
        ''' Completes synchronously (<see cref="Task.CompletedTask" />) - see the remarks on
        ''' <see cref="ReadAtAsync" />.
        ''' </summary>
        Public Function WriteAtAsync(PhysicalOffset As Long,
                                     Buffer As Byte(),
                                     BufferOffset As Integer,
                                     Count As Integer,
                                     CancellationToken As CancellationToken) As Task _
                                     Implements IPositionedStreamAsync.WriteAtAsync

            CancellationToken.ThrowIfCancellationRequested()
            WriteAt(PhysicalOffset, Buffer, BufferOffset, Count)
            Return Task.CompletedTask

        End Function

        ' Everything below is a single logical caller at a time under ChunkedStream's own
        ' state lock (Position-based Stream.Read/Write are never called concurrently with
        ' each other), but still takes the same lock as ReadAt/WriteAt for correctness against
        ' a concurrent positioned caller touching the same buffer.

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return Not _Disposed
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return Not _Disposed
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return Not _Disposed
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                _Lock.EnterReadLock()
                Try
                    Return _Length
                Finally
                    _Lock.ExitReadLock()
                End Try
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return Interlocked.Read(_Position)
            End Get
            Set
                If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Position))
                Interlocked.Exchange(_Position, Value)
            End Set
        End Property

        Public Overrides Sub Flush()
            ' Nothing to durably persist - matches MemoryStream.Flush's own no-op.
        End Sub

        Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
            Dim Result = ReadAt(Position, Buffer, Offset, Count)
            Position += Result
            Return Result
        End Function

        Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
            WriteAt(Position, Buffer, Offset, Count)
            Position += Count
        End Sub

        Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long

            Dim NewPosition =
                If(Origin = SeekOrigin.Begin, Offset,
                   If(Origin = SeekOrigin.Current, Position + Offset,
                      Length + Offset))

            If NewPosition < 0 Then Throw New IOException("Cannot seek before the start of the stream.")
            Position = NewPosition
            Return NewPosition

        End Function

        Public Overrides Sub SetLength(Value As Long)
            If Value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Value))
            _Lock.EnterWriteLock()
            Try
                EnsureCapacity(Value)
                If Value < _Length Then
                    Array.Clear(_Buffer, CInt(Value), CInt(_Length - Value))
                End If
                _Length = Value
            Finally
                _Lock.ExitWriteLock()
            End Try
            If Position > Value Then Position = Value
        End Sub

        Protected Overrides Sub Dispose(Disposing As Boolean)
            If _Disposed Then Return
            _Disposed = True
            If Disposing Then _Lock.Dispose()
            MyBase.Dispose(Disposing)
        End Sub

    End Class

End Namespace
