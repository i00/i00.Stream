' ================================================================================
' ChunkedStream Positioned Storage Contract
' ================================================================================
'
' Compatibility
'   - Designed for .NET Framework 4.8+.
'
' Purpose
'   - Allows a seekable Stream to expose physical offset-based reads and writes.
'   - Allows ChunkedStream to avoid changing Stream.Position when the backing stream
'     has a specialised positional implementation.
'   - Allows read and write locking requirements to be declared independently.
'
' Contract
'   - Implementations must also inherit System.IO.Stream.
'   - ReadAt reads from PhysicalOffset without using or changing Stream.Position.
'   - WriteAt writes at PhysicalOffset without using or changing Stream.Position.
'   - ReadAt may return fewer bytes than requested and returns zero only at end of data.
'   - WriteAt must write the complete requested range or throw.
'   - LockFreeReads means ReadAt may execute concurrently with every operation that
'     ChunkedStream is also permitted to execute without its physical-I/O lock.
'   - LockFreeWrites means WriteAt may execute concurrently with every operation that
'     ChunkedStream is also permitted to execute without its physical-I/O lock.
'   - Returning neither capability is valid. ChunkedStream will serialise both methods
'     through its shared physical-I/O semaphore while still avoiding Stream.Position.
'
' Async
'   - A stream that also implements IPositionedStreamAsync additionally exposes
'     awaitable positioned reads and writes. ChunkedStream prefers these on its async
'     code paths and falls back to Stream.ReadAsync / Stream.WriteAsync otherwise.
'   - ReadAtAsync / WriteAtAsync follow the same contract as ReadAt / WriteAt and reuse
'     the same PositionedIoCapabilities: a stream that is lock-free for the synchronous
'     positioned methods is treated as lock-free for the asynchronous equivalents.
'
' ================================================================================

Imports System.IO

Namespace Streams

    <Flags>
    Public Enum PositionedIoCapabilities

        None = 0

        ''' <summary>
        ''' Positional reads do not require ChunkedStream's physical-I/O lock.
        ''' </summary>
        LockFreeReads = 1 << 0

        ''' <summary>
        ''' Positional writes do not require ChunkedStream's physical-I/O lock.
        ''' </summary>
        LockFreeWrites = 1 << 1

        Full = LockFreeReads Or LockFreeWrites

    End Enum

    ''' <summary>
    ''' Optional contract implemented by a Stream that supports physical offset-based I/O.
    ''' </summary>
    Public Interface IPositionedStream

        ''' <summary>
        ''' Gets the physical-I/O concurrency capabilities of this implementation.
        ''' </summary>
        ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities

        ''' <summary>
        ''' Reads bytes from an explicit physical offset without using Stream.Position.
        ''' </summary>
        Function ReadAt(PhysicalOffset As Long,
                        Buffer As Byte(),
                        BufferOffset As Integer,
                        Count As Integer) As Integer

        ''' <summary>
        ''' Writes bytes at an explicit physical offset without using Stream.Position.
        ''' The implementation must write Count bytes or throw.
        ''' </summary>
        Sub WriteAt(PhysicalOffset As Long,
                    Buffer As Byte(),
                    BufferOffset As Integer,
                    Count As Integer)

    End Interface

    ''' <summary>
    ''' Optional contract implemented by a <see cref="IPositionedStream" /> that also
    ''' supports awaitable physical offset-based I/O. ChunkedStream prefers these methods
    ''' on its asynchronous code paths.
    ''' </summary>
    Public Interface IPositionedStreamAsync
        Inherits IPositionedStream

        ''' <summary>
        ''' Asynchronously reads bytes from an explicit physical offset without using
        ''' Stream.Position. May return fewer bytes than requested and returns zero only
        ''' at end of data.
        ''' </summary>
        Function ReadAtAsync(PhysicalOffset As Long,
                             Buffer As Byte(),
                             BufferOffset As Integer,
                             Count As Integer,
                             CancellationToken As Threading.CancellationToken) As Task(Of Integer)

        ''' <summary>
        ''' Asynchronously writes bytes at an explicit physical offset without using
        ''' Stream.Position. The implementation must write Count bytes or throw.
        ''' </summary>
        Function WriteAtAsync(PhysicalOffset As Long,
                              Buffer As Byte(),
                              BufferOffset As Integer,
                              Count As Integer,
                              CancellationToken As Threading.CancellationToken) As Task

    End Interface

    ''' <summary>
    ''' Optional contract implemented by a backing <see cref="Stream" /> that is not itself a
    ''' <see cref="FileStream" /> but can still perform a genuine durable flush (an fsync /
    ''' <c>FlushFileBuffers</c> - all previously written bytes provably on stable storage, not
    ''' merely handed to the OS). ChunkedStream's own <see cref="FileStream" /> fallback for
    ''' durable flush (<c>FileStream.Flush(True)</c>) only recognises an actual
    ''' <see cref="FileStream" /> instance; a wrapper stream that is not one - such as
    ''' <see cref="PositionedFileStream" /> - would otherwise silently fall through to a plain,
    ''' non-durable <see cref="Stream.Flush" />, with nothing but a missed
    ''' <c>FlushDurableAction</c> parameter standing between "durable" and "not" - a real
    ''' footgun for a design built entirely around durable header rotation. Implementing this
    ''' interface makes the durable flush automatic, the same way <see cref="IPositionedStream" />
    ''' capabilities are auto-detected, instead of relying on every caller remembering to pass
    ''' <c>FlushDurableAction</c> explicitly.
    ''' </summary>
    Public Interface IDurableFlush

        ''' <summary>
        ''' Durably flushes every byte written so far - the wrapper's equivalent of
        ''' <c>FileStream.Flush(flushToDisk:=True)</c>.
        ''' </summary>
        Sub FlushDurable()

    End Interface

End Namespace
