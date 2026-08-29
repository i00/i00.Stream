Imports System.IO
Imports System.Threading
Imports i00.Streams

Namespace Tests

    Partial Class CoreStreamSemantics

        ''' <summary>
        ''' Exercises the <see cref="IPositionedStream" /> fast path: when the backing store
        ''' can read and write at an explicit offset, ChunkedStream routes chunk-record and
        ''' metadata-page I/O through <c>ReadAt</c> / <c>WriteAt</c> instead of seeking, and
        ''' the declared lock-free capabilities decide which of those calls take the shared
        ''' physical-I/O lock.
        ''' </summary>
        Public NotInheritable Class PositionedStream

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub PositionedStreamRoundTripsWhenLockingIsRequired()

                RoundTripThroughPositionedStream(PositionedIoCapabilities.None, 5501)

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub PositionedStreamRoundTripsWhenFullyLockFree()

                RoundTripThroughPositionedStream(PositionedIoCapabilities.Full, 5502)

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub PositionedStreamReadAtWriteAtActuallyGetUsed()

                Using Backing As New PositionedMemoryStream(PositionedIoCapabilities.None)

                    Using Cs = ChunkedStream.Open(Backing)
                        Cs.Write(0, GenerateRandomData(Cs.Options.ChunkSize * 4, 5511))
                        Cs.Validate()
                        Dim RoundTrip = Cs.ToArray()
                        AssertEqual(CLng(Cs.Options.ChunkSize * 4), CLng(RoundTrip.Length), "Unexpected logical length.")
                    End Using

                    AssertTrue(Backing.WriteAtCalls > 0, "ChunkedStream never used IPositionedStream.WriteAt for chunk I/O.")
                    AssertTrue(Backing.ReadAtCalls > 0, "ChunkedStream never used IPositionedStream.ReadAt for chunk I/O.")

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub PositionedStreamDataSurvivesReopenThroughANewInstance()

                Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 7, 5521)
                Dim Bytes As Byte()

                Using Backing As New PositionedMemoryStream(PositionedIoCapabilities.Full)
                    Using Cs = ChunkedStream.Open(Backing)
                        Cs.Write(0, Expected)
                        Cs.Validate()
                    End Using
                    Bytes = Backing.ToArray()
                End Using

                Using Backing As New PositionedMemoryStream(PositionedIoCapabilities.Full, Bytes)
                    Using Reopened = ChunkedStream.Open(Backing)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Positioned-stream data did not survive reopen.")
                        Reopened.Validate()
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub PositionedStreamHandlesEditsCompressionAndDefrag()

                Using Backing As New PositionedMemoryStream(PositionedIoCapabilities.LockFreeReads)

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 512,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Deflate
                    }

                    Using Cs = ChunkedStream.Open(Backing, Options)

                        Dim Expected = GeneratePartiallyCompressibleDataForLength(0.6R, 30000, 512, 5531)
                        Cs.Write(0, Expected)

                        Cs.Remove(4000, 6000)
                        Expected = CombineArrays(Slice(Expected, 0, 4000), Slice(Expected, 10000, Expected.Length - 10000))

                        Dim Patch = GenerateRandomData(2000, 5532)
                        Cs.Write(1000, Patch)
                        Overlay(Expected, Patch, 1000)

                        Cs.Defragment(ChunkedStream.DefragTypes.Sequence)

                        AssertBytesEqual(Expected, Cs.ToArray(), "Edits over a positioned stream did not match the model.")
                        Cs.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Helpers
            ' ================================================================================

            Private Shared Sub RoundTripThroughPositionedStream(Capabilities As PositionedIoCapabilities,
                                                                Seed As Integer)

                Dim Expected = GenerateRandomData(ChunkedStream.DefaultChunkSize * 6, Seed)

                Using Backing As New PositionedMemoryStream(Capabilities)

                    Using Cs = ChunkedStream.Open(Backing)

                        Cs.Write(0, Expected)

                        Dim Patch = GeneratePatternData(777, Seed + 1)
                        Cs.Write(ChunkedStream.DefaultChunkSize + 100, Patch)
                        Overlay(Expected, Patch, ChunkedStream.DefaultChunkSize + 100)

                        AssertBytesEqual(Expected, Cs.ToArray(), "Positioned-stream round-trip mismatch.")
                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Backing)
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Positioned-stream data lost across reopen.")
                        Reopened.Validate()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' A seekable in-memory stream that also offers explicit offset-based I/O with a
            ''' configurable lock-free capability set, and counts how often the positioned
            ''' methods are used.
            ''' </summary>
            Private NotInheritable Class PositionedMemoryStream
                Inherits Stream
                Implements IPositionedStream

                Private ReadOnly _Inner As MemoryStream
                Private ReadOnly _Capabilities As PositionedIoCapabilities
                Private ReadOnly _Gate As New Object()

                Public Sub New(Capabilities As PositionedIoCapabilities)
                    _Inner = New MemoryStream()
                    _Capabilities = Capabilities
                End Sub

                Public Sub New(Capabilities As PositionedIoCapabilities, InitialBytes As Byte())
                    _Inner = New MemoryStream()
                    If InitialBytes IsNot Nothing AndAlso InitialBytes.Length > 0 Then
                        _Inner.Write(InitialBytes, 0, InitialBytes.Length)
                        _Inner.Position = 0
                    End If
                    _Capabilities = Capabilities
                End Sub

                Public Property ReadAtCalls As Integer
                Public Property WriteAtCalls As Integer

                Public Function ToArray() As Byte()
                    SyncLock _Gate
                        Return _Inner.ToArray()
                    End SyncLock
                End Function

                Public ReadOnly Property PositionedIoCapabilities As PositionedIoCapabilities _
                    Implements IPositionedStream.PositionedIoCapabilities
                    Get
                        Return _Capabilities
                    End Get
                End Property

                Public Function ReadAt(PhysicalOffset As Long,
                                       Buffer As Byte(),
                                       BufferOffset As Integer,
                                       Count As Integer) As Integer Implements IPositionedStream.ReadAt

                    SyncLock _Gate
                        ReadAtCalls += 1
                        If PhysicalOffset >= _Inner.Length Then Return 0
                        _Inner.Position = PhysicalOffset
                        Return _Inner.Read(Buffer, BufferOffset, Count)
                    End SyncLock

                End Function

                Public Sub WriteAt(PhysicalOffset As Long,
                                   Buffer As Byte(),
                                   BufferOffset As Integer,
                                   Count As Integer) Implements IPositionedStream.WriteAt

                    SyncLock _Gate
                        WriteAtCalls += 1
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
