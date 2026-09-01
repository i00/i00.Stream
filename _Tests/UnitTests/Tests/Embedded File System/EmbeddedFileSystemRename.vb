Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' In-place renaming of files and directories: content and anchors are preserved, the change
        ''' survives a reopen, and collisions, blanks and missing entries are rejected.
        ''' </summary>
        Public NotInheritable Class Rename

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub FilesAndDirectoriesAreRenamedWithoutMovingContent()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Payload = GenerateRandomData(20000, 7701)
                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim FileId = Efs.CreateFile(Docs, "old.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, FileId, Payload)

                            Efs.RenameEntry(Docs, "old.txt", "new name.txt")
                            Efs.RenameEntry(Efs.RootAnchorId, "docs", "documents")

                            AssertEqual("documents", String.Join("|", Efs.GetRootEntries().Select(Function(e) e.Name)), "The directory was not renamed.")

                            Dim DirEntry = Efs.FindEntry(Efs.RootAnchorId, "documents")
                            AssertEqual(Docs, DirEntry.ChildAnchorId, "Renaming the directory changed its anchor.")

                            Dim FileEntry = Efs.FindEntry(Docs, "new name.txt")
                            AssertEqual(FileId, FileEntry.ChildAnchorId, "Renaming the file changed its anchor.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, FileEntry.EntryType, "Renaming the file changed its type.")
                            AssertEqual(CLng(Payload.Length), FileEntry.LengthOfDataAtEntry, "Renaming the file changed its length.")
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, FileId), "Renaming the file changed its content.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RenameToADifferentCaseOfTheSameNameIsAllowed()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Efs.CreateDirectory(Efs.RootAnchorId, "readme")
                            Efs.RenameEntry(Efs.RootAnchorId, "readme", "README")

                            AssertEqual("README", Efs.FindEntry(Efs.RootAnchorId, "readme").Name, "The case change was not stored.")

                            Cs.Validate()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RenameRejectsCollisionsBlanksAndMissingEntries()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Efs.CreateDirectory(Efs.RootAnchorId, "b")

                            AssertThrows(Of IOException)(
                                Sub() Efs.RenameEntry(Efs.RootAnchorId, "a", "B"),
                                "Renaming onto an existing name should be rejected case-insensitively.")

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.RenameEntry(Efs.RootAnchorId, "nope", "x"),
                                "Renaming a missing entry should throw FileNotFoundException.")

                            AssertThrows(Of ArgumentException)(
                                Sub() Efs.RenameEntry(Efs.RootAnchorId, "a", "   "),
                                "A blank new name should be rejected.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RenamesSurviveAReopen()

                Using Ms As New MemoryStream()

                    Dim Payload = GenerateRandomData(9000, 7702)

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)
                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Dim FileId = Efs.CreateFile(Dir, "first.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, FileId, Payload)
                            Efs.RenameEntry(Efs.RootAnchorId, "a", "alpha")
                            Efs.RenameEntry(Dir, "first.bin", "second.bin")
                        End Using
                        Cs.Validate()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.FindEntry(Efs.RootAnchorId, "alpha").ChildAnchorId
                            Dim FileEntry = Efs.FindEntry(Dir, "second.bin")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, FileEntry.EntryType, "The reopened file lost its type.")
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, FileEntry.ChildAnchorId), "The reopened renamed file lost its content.")

                        End Using
                        Cs.Validate()
                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
