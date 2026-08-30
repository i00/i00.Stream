Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' Pending-file recovery, recursive deletion, deep nesting and space reclamation
        ''' under a create / delete churn.
        ''' </summary>
        Public NotInheritable Class Recovery

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AbandonedPendingFilesAreFinalisedByRecovery()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)
                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "uploads")
                            Efs.CreateFile(Dir, "a.part")
                            Efs.CreateFile(Dir, "b.part")
                            Efs.CreateFile(Efs.RootAnchorId, "c.part")
                            ' None opened, so all three stay PendingFile.
                        End Using
                        Cs.Validate()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.FindEntry(Efs.RootAnchorId, "uploads").ChildAnchorId
                            AssertEqual(EmbeddedFileSystem.EntryTypes.PendingFile, Efs.FindEntry(Dir, "a.part").EntryType, "Expected a still-pending file.")

                            Dim Recovered = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Finalize)
                            AssertEqual(3, Recovered, "RecoverPendingFiles finalised the wrong number of files.")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Dir, "a.part").EntryType, "a.part should be finalised.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Efs.RootAnchorId, "c.part").EntryType, "c.part should be finalised.")

                            AssertEqual(0, Efs.RecoverPendingFiles(), "A second recovery pass should find nothing.")

                        End Using
                        Cs.Validate()
                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AbandonedPendingFilesCanBeRemovedByRecovery()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "tmp")
                            Efs.CreateFile(Dir, "junk1.part")
                            Efs.CreateFile(Dir, "junk2.part")
                            Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, "real.bin", CreateAsPending:=False), GenerateRandomData(4000, 5501))

                            Dim Removed = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Remove)
                            AssertEqual(2, Removed, "RecoverPendingFiles(Remove) removed the wrong number of files.")

                            Dim Names = Efs.GetDirectoryEntries(Dir).Select(Function(e) e.Name).ToArray()
                            AssertEqual("real.bin", String.Join("|", Names), "Only the finalised file should remain.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AnOpenPendingFileIsNotTouchedByRecovery()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim FileId = Efs.CreateFile(Efs.RootAnchorId, "live.part")

                            Using Stream = Efs.OpenFile(FileId)
                                Stream.Write(GenerateRandomData(100, 5601), 0, 100)

                                Dim Recovered = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Finalize)
                                AssertEqual(0, Recovered, "Recovery must skip a file that is currently open.")
                            End Using

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Efs.RootAnchorId, "live.part").EntryType, "The file should finalise on close as normal.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeeplyNestedDirectoriesCanBeCreatedAndRecursivelyDeleted()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Current = Efs.CreateDirectory(Efs.RootAnchorId, "level0")

                            For Depth = 1 To 24
                                Dim Next_ = Efs.CreateDirectory(Current, $"level{Depth}")
                                Basics.WriteWholeFile(Efs, Efs.CreateFile(Current, $"note{Depth}.txt", CreateAsPending:=False),
                                                      GenerateRandomData(200 + Depth, 5700 + Depth))
                                Current = Next_
                            Next

                            Dim PhysicalWithTree = Ms.Length

                            Efs.DeleteEntry(Efs.RootAnchorId, "level0")

                            AssertEqual(0, Efs.GetRootEntries().Count, "The whole tree should be gone.")
                            Cs.Validate()

                            ' Space is recoverable: defragment brings the file back near empty.
                            Cs.Defragment(ChunkedStream.DefragTypes.Move)
                            AssertTrue(
                                Ms.Length < PhysicalWithTree,
                                $"Defragment did not reclaim the deleted tree: {PhysicalWithTree:N0} -> {Ms.Length:N0}.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RepeatedCreateDeleteCyclesReclaimSpace()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "scratch")

                            ' Populate once so there is a stable baseline geometry.
                            For Index = 0 To 3
                                Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, $"keep{Index}.bin", CreateAsPending:=False),
                                                      GenerateRandomData(2000, 5800 + Index))
                            Next

                            For Cycle = 0 To 5
                                For Index = 0 To 3
                                    Dim Name = $"cycle{Index}.bin"
                                    Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, Name, CreateAsPending:=False),
                                                          GenerateRandomData(3000, 5900 + (Cycle * 10) + Index))
                                Next
                                For Index = 0 To 3
                                    Efs.DeleteEntry(Dir, $"cycle{Index}.bin")
                                Next
                            Next

                            Cs.Validate()

                            ' The keep-files are intact and the churn did not balloon the file.
                            For Index = 0 To 3
                                AssertBytesEqual(
                                    GenerateRandomData(2000, 5800 + Index),
                                    Basics.ReadWholeFile(Efs, Efs.FindEntry(Dir, $"keep{Index}.bin").ChildAnchorId),
                                    $"keep{Index}.bin was corrupted by the create/delete churn.")
                            Next

                            Cs.Defragment(ChunkedStream.DefragTypes.Move)
                            Cs.Validate()

                            AssertTrue(
                                Ms.Length < 512L * 1024L,
                                $"Create/delete churn over four small files should not grow the backing store past 512 KB (was {Ms.Length:N0}).")

                        End Using
                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
