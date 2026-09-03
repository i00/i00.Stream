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

            Private Shared Sub RunOnThreads(Count As Integer, Body As Action)

                Dim Threads As New List(Of Thread)()

                For Index = 0 To Count - 1
                    Threads.Add(New Thread(Sub() Body()))
                Next

                For Each T In Threads : T.Start() : Next
                For Each T In Threads : T.Join() : Next

            End Sub

        End Class

    End Class

End Namespace
