' ================================================================================
' PositionedFileStream
' ================================================================================
'
' Purpose
'   - A Stream over a real file that implements IPositionedStreamAsync with genuine
'     concurrent positioned I/O to ONE file handle - the piece that was missing for
'     Options.MaxPhysicalWriteParallelism (and the existing lock-free concurrent read
'     path) to have any effect against real file storage. A plain FileStream declares
'     neither PositionedIoCapabilities flag, so ChunkedStream funnels every physical
'     read and write through its own lock when given one.
'
' How ReadAt/WriteAt avoid the shared Stream.Position race
'   - Stream.Read/Write implicitly use and advance Stream.Position, so two threads
'     calling them concurrently on the same instance race on that one mutable field.
'   - Win32's ReadFile/WriteFile accept an OVERLAPPED structure carrying an explicit
'     byte offset. Passing one - even against a handle that was NOT opened with
'     FILE_FLAG_OVERLAPPED - performs the read/write at that offset without using or
'     moving the file's position at all, and completes synchronously (verified
'     empirically: GetLastWin32Error() is 0, not ERROR_IO_PENDING/997). This is the
'     same mechanism .NET 6+'s System.IO.RandomAccess.Read/Write use internally on
'     Windows; this class is the .NET Framework 4.8 equivalent, needed because
'     RandomAccess itself does not exist before .NET 6.
'   - Concurrent calls at different offsets are therefore safe: nothing shared is
'     mutated, and the OS is free to service them concurrently (verified empirically
'     with 16 threads writing/reading disjoint 1MB regions of one handle at once).
'
' Async
'   - ReadAtAsync/WriteAtAsync offload the same synchronous P/Invoke call onto the
'     thread pool (Task.Run) rather than using IOCP-based true overlapped I/O - the
'     same "worker-thread offload" pattern already used elsewhere in this project for
'     other long-running operations (see DONE.md's async support entry). A thread is
'     held for the duration of each call, but multiple such calls still genuinely
'     overlap at the OS level, which is what PositionedIoCapabilities.LockFreeWrites
'     promises and what Options.MaxPhysicalWriteParallelism needs.
'
' Everything else (Position, Read, Write, Seek, SetLength, Flush, Length) delegates
' directly to a plain FileStream opened on the same path - ChunkedStream only ever
' calls those from one logical caller at a time (the state lock), so there is nothing
' to make concurrent there, and reusing FileStream's own implementation avoids
' reinventing it.
'
' ================================================================================

Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports Microsoft.Win32.SafeHandles

Namespace Streams

    ''' <summary>
    ''' A file-backed <see cref="Stream" /> that also implements
    ''' <see cref="IPositionedStreamAsync" /> with genuine concurrent positioned I/O to a
    ''' single file handle, via Win32 ReadFile/WriteFile and an explicit-offset OVERLAPPED
    ''' structure rather than the file's shared position. Declares
    ''' <see cref="PositionedIoCapabilities.Full" />.
    ''' </summary>
    Public NotInheritable Class PositionedFileStream
        Inherits Stream
        Implements IPositionedStreamAsync

        <StructLayout(LayoutKind.Sequential)>
        Private Structure NATIVE_OVERLAPPED
            Public Internal As IntPtr
            Public InternalHigh As IntPtr
            Public OffsetLow As Integer
            Public OffsetHigh As Integer
            Public hEvent As IntPtr
        End Structure

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function ReadFile(hFile As SafeFileHandle,
                                         lpBuffer As Byte(),
                                         nNumberOfBytesToRead As Integer,
                                         ByRef lpNumberOfBytesRead As Integer,
                                         ByRef lpOverlapped As NATIVE_OVERLAPPED) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function WriteFile(hFile As SafeFileHandle,
                                          lpBuffer As Byte(),
                                          nNumberOfBytesToWrite As Integer,
                                          ByRef lpNumberOfBytesWritten As Integer,
                                          ByRef lpOverlapped As NATIVE_OVERLAPPED) As Boolean
        End Function

        Private Const ErrorHandleEof As Integer = 38 ' Win32 ERROR_HANDLE_EOF - a positioned read starting at/past EOF.

        Private ReadOnly _Inner As FileStream
        Private ReadOnly _Handle As SafeFileHandle
        Private _Disposed As Boolean

        ''' <summary>
        ''' Opens a file, matching <see cref="FileStream" />'s common constructor shape.
        ''' Always effectively unbuffered (bufferSize:=1) - each pooled/positioned caller's
        ''' Read/Write is already one explicit syscall; a FileStream-level buffer would only
        ''' risk one handle's buffer going stale relative to bytes this class writes straight
        ''' through to the OS.
        ''' </summary>
        Public Sub New(Path As String, Mode As FileMode, Access As FileAccess, Share As FileShare)
            _Inner = New FileStream(Path, Mode, Access, Share, 1, FileOptions.None)
            _Handle = _Inner.SafeFileHandle
        End Sub

        ''' <summary>
        ''' Durably flushes the file (<see cref="FileStream.Flush(Boolean)" /> with
        ''' <c>flushToDisk:=True</c>) - pass <c>AddressOf this method</c> (or
        ''' <c>Sub() stream.FlushDurable()</c>) as <c>ChunkedStream.Open</c>'s
        ''' <c>FlushDurableAction</c>, since this class is not itself a <see cref="FileStream"/>
        ''' so ChunkedStream's own FileStream fallback for durable flush does not apply.
        ''' </summary>
        Public Sub FlushDurable()
            _Inner.Flush(True)
        End Sub

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

        Private Shared Function MakeOverlapped(PhysicalOffset As Long) As NATIVE_OVERLAPPED
            Dim Result As New NATIVE_OVERLAPPED()
            Result.OffsetLow = CInt(PhysicalOffset And &HFFFFFFFFL)
            Result.OffsetHigh = CInt((PhysicalOffset >> 32) And &HFFFFFFFFL)
            Result.hEvent = IntPtr.Zero
            Return Result
        End Function

        ''' <summary>
        ''' Reads at an explicit offset without touching <see cref="Position" />, safe to call
        ''' concurrently with any other ReadAt/WriteAt on this instance. May return fewer bytes
        ''' than requested only at end of file (matching the <see cref="IPositionedStream" />
        ''' contract), including zero at or past EOF.
        ''' </summary>
        Public Function ReadAt(PhysicalOffset As Long,
                              Buffer As Byte(),
                              BufferOffset As Integer,
                              Count As Integer) As Integer Implements IPositionedStream.ReadAt

            ValidateArgs(Buffer, BufferOffset, Count)
            If Count = 0 Then Return 0
            If PhysicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalOffset))

            Dim Overlapped = MakeOverlapped(PhysicalOffset)

            ' ReadFile only fills a caller-sized temp buffer starting at index 0; BufferOffset
            ' is handled by copying into the caller's array afterwards, since the OVERLAPPED
            ' offset parameter is for the FILE position, not the buffer position.
            Dim Temp(Count - 1) As Byte
            Dim BytesRead As Integer
            Dim Ok = ReadFile(_Handle, Temp, Count, BytesRead, Overlapped)

            If Ok = False Then
                Dim Err = Marshal.GetLastWin32Error()
                If Err = ErrorHandleEof Then Return 0
                Throw New IOException($"ReadFile failed at offset {PhysicalOffset}: Win32 error {Err}.")
            End If

            If BytesRead > 0 Then System.Buffer.BlockCopy(Temp, 0, Buffer, BufferOffset, BytesRead)
            Return BytesRead

        End Function

        ''' <summary>
        ''' Writes at an explicit offset without touching <see cref="Position" />, safe to call
        ''' concurrently with any other ReadAt/WriteAt on this instance. Writes the complete
        ''' requested range or throws, per the <see cref="IPositionedStream" /> contract.
        ''' </summary>
        Public Sub WriteAt(PhysicalOffset As Long,
                           Buffer As Byte(),
                           BufferOffset As Integer,
                           Count As Integer) Implements IPositionedStream.WriteAt

            ValidateArgs(Buffer, BufferOffset, Count)
            If Count = 0 Then Return
            If PhysicalOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(PhysicalOffset))

            Dim Temp(Count - 1) As Byte
            System.Buffer.BlockCopy(Buffer, BufferOffset, Temp, 0, Count)

            Dim Overlapped = MakeOverlapped(PhysicalOffset)
            Dim BytesWritten As Integer
            Dim Ok = WriteFile(_Handle, Temp, Count, BytesWritten, Overlapped)

            If Ok = False Then
                Throw New IOException($"WriteFile failed at offset {PhysicalOffset}: Win32 error {Marshal.GetLastWin32Error()}.")
            End If

            If BytesWritten <> Count Then
                Throw New IOException($"WriteFile wrote {BytesWritten} of {Count} requested bytes at offset {PhysicalOffset}.")
            End If

        End Sub

        ''' <summary>
        ''' Offloads <see cref="ReadAt" /> onto the thread pool. A thread is held for the
        ''' duration of the call (this is worker-thread-offload async, matching the pattern
        ''' this project already uses for other long-running operations - see DONE.md's async
        ''' support entry), but multiple concurrent calls still genuinely overlap at the OS
        ''' level, which is the property that matters for lock-free concurrent reads/writes.
        ''' </summary>
        Public Function ReadAtAsync(PhysicalOffset As Long,
                                    Buffer As Byte(),
                                    BufferOffset As Integer,
                                    Count As Integer,
                                    CancellationToken As CancellationToken) As Task(Of Integer) _
                                    Implements IPositionedStreamAsync.ReadAtAsync

            CancellationToken.ThrowIfCancellationRequested()
            Return Task.Run(Function() ReadAt(PhysicalOffset, Buffer, BufferOffset, Count), CancellationToken)

        End Function

        ''' <summary>
        ''' Offloads <see cref="WriteAt" /> onto the thread pool - see the remarks on
        ''' <see cref="ReadAtAsync" />.
        ''' </summary>
        Public Function WriteAtAsync(PhysicalOffset As Long,
                                     Buffer As Byte(),
                                     BufferOffset As Integer,
                                     Count As Integer,
                                     CancellationToken As CancellationToken) As Task _
                                     Implements IPositionedStreamAsync.WriteAtAsync

            CancellationToken.ThrowIfCancellationRequested()
            Return Task.Run(Sub() WriteAt(PhysicalOffset, Buffer, BufferOffset, Count), CancellationToken)

        End Function

        ' Everything below is a single logical caller at a time under ChunkedStream's own
        ' state lock, so it delegates straight to the inner FileStream - nothing here needs
        ' to be (or is) safe for concurrent multi-threaded use.

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return _Inner.CanRead
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return _Inner.CanSeek
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return _Inner.CanWrite
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Return _Inner.Length
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Return _Inner.Position
            End Get
            Set
                _Inner.Position = Value
            End Set
        End Property

        Public Overrides Sub Flush()
            _Inner.Flush()
        End Sub

        Public Overrides Function Read(Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
            Return _Inner.Read(Buffer, Offset, Count)
        End Function

        Public Overrides Sub Write(Buffer As Byte(), Offset As Integer, Count As Integer)
            _Inner.Write(Buffer, Offset, Count)
        End Sub

        Public Overrides Function Seek(Offset As Long, Origin As SeekOrigin) As Long
            Return _Inner.Seek(Offset, Origin)
        End Function

        Public Overrides Sub SetLength(Value As Long)
            _Inner.SetLength(Value)
        End Sub

        Protected Overrides Sub Dispose(Disposing As Boolean)
            If _Disposed Then Return
            _Disposed = True
            If Disposing Then _Inner.Dispose()
            MyBase.Dispose(Disposing)
        End Sub

    End Class

End Namespace
