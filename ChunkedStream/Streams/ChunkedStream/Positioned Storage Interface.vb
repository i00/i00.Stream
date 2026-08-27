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

End Namespace
