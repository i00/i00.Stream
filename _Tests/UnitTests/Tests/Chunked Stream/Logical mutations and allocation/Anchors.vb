Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class Anchors

            Private Sub New()
            End Sub

#Region "Anchor creation and lookup"

            ''' <summary>
            ''' Verifies that an anchor can be created at an existing logical offset.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CreateAnchorAtExistingOffset()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                4096,
                                123)

                        Cs.Write(0, Data)

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        AssertTrue(
                            Anchor.IsValid,
                            "Anchor should be valid after creation.")

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchor offset mismatch.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that multiple anchors cannot identify the same logical offset.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DuplicateAnchorOffsetThrows()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Cs.CreateAnchor(1000)

                        AssertThrows(Of InvalidOperationException)(
                            Sub()
                                Cs.CreateAnchor(1000)
                            End Sub,
                            "Creating duplicate anchors at the same logical position should fail.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchors cannot be created at the logical end of the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CreateAnchorAtEndOfStreamThrows()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                1024,
                                1))

                        AssertThrows(Of ArgumentOutOfRangeException)(
                            Sub()
                                Cs.CreateAnchor(Cs.Length)
                            End Sub,
                            "Anchors should identify existing logical data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchored data cannot be created from an empty buffer.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CreateAnchorWithEmptyDataThrows()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        AssertThrows(Of ArgumentException)(
                            Sub()
                                Cs.CreateAnchor(New Byte() {})
                            End Sub,
                            "Anchored data should not allow empty buffers.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that CreateAnchor(Data) appends data and anchors the first byte.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CreateAnchorWithDataAppendsData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Prefix =
                            GeneratePatternData(
                                1000,
                                1)

                        Dim AnchoredData =
                            GeneratePatternData(
                                500,
                                2)

                        Cs.Write(
                            0,
                            Prefix)

                        Dim Anchor =
                            Cs.CreateAnchor(
                                AnchoredData)

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchored data should begin immediately after existing data.")

                        AssertEqual(
                            1500L,
                            Cs.Length,
                            "Length mismatch after anchored append.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchor identities can be reconstructed from persisted ids.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub GetAnchorReconstructsAnchorFromId()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim OriginalAnchor =
                            Cs.CreateAnchor(1000)

                        Dim RestoredAnchor =
                            Cs.GetAnchor(
                                OriginalAnchor.AnchorId)

                        AssertEqual(
                            OriginalAnchor.AnchorId,
                            RestoredAnchor.AnchorId,
                            "Anchor id mismatch.")

                        AssertEqual(
                            OriginalAnchor.Offset,
                            RestoredAnchor.Offset,
                            "Anchor offset mismatch.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchor ids are never reused.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnchorIdsAreNotReused()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim FirstAnchor =
                            Cs.CreateAnchor(1000)

                        Dim FirstId =
                            FirstAnchor.AnchorId

                        FirstAnchor.Remove()

                        Dim SecondAnchor =
                            Cs.CreateAnchor(2000)

                        AssertTrue(
                            SecondAnchor.AnchorId > FirstId,
                            "Anchor ids must not be reused.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Insert behaviour"

            ''' <summary>
            ''' Verifies that TransformAway causes an anchor at the insertion point
            ''' to remain attached to the original logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertTransformAwayMovesAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Insert(
                            1000,
                            GeneratePatternData(
                                300,
                                2),
                            ChunkedStream.AnchorActionsAtLogicalOffset.TransformAway)

                        AssertEqual(
                            1300L,
                            Anchor.Offset,
                            "Anchor should move with the original data.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Use transfers the anchor to inserted data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertUseTransfersAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Insert(
                            1000,
                            GeneratePatternData(
                                300,
                                2),
                            ChunkedStream.AnchorActionsAtLogicalOffset.Use)

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchor should stay at the insertion point.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchor overloads implicitly use Use semantics.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertAnchorUsesUseSemantics()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Insert(
                            Anchor,
                            GeneratePatternData(
                                300,
                                2))

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchor overloads should use Use semantics.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Remove behaviour"

            ''' <summary>
            ''' Verifies that removing a range covering an anchor destroys the anchor.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveCoveringAnchorDestroysAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Remove(500, 1000)

                        AssertFalse(
                            Anchor.IsValid,
                            "Anchors within a removed range should be removed.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchors positioned at the end boundary survive removal.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveEndingAtAnchorPreservesAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Remove(500, 500)

                        AssertTrue(
                            Anchor.IsValid,
                            "Anchor positioned at the removal end should survive.")

                        AssertEqual(
                            500L,
                            Anchor.Offset,
                            "Logical offset mismatch after removal.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing anchored data destroys the anchor.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveAnchorDestroysAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Remove(
                            Anchor,
                            500)

                        AssertFalse(
                            Anchor.IsValid,
                            "Removing anchored data should destroy the anchor.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Replace behaviour"

            ''' <summary>
            ''' Verifies that Use preserves the anchor at the replacement start.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReplaceUsePreservesAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Replace(
                            1000,
                            500,
                            GeneratePatternData(
                                700,
                                2),
                            ChunkedStream.AnchorActionsAtLogicalOffset.Use)

                        AssertTrue(
                            Anchor.IsValid,
                            "Anchor should survive replacement.")

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchor offset mismatch after replacement.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that TransformAway removes the anchor at the replacement start.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReplaceTransformAwayRemovesAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.Replace(
                            1000,
                            500,
                            GeneratePatternData(
                                700,
                                2),
                            ChunkedStream.AnchorActionsAtLogicalOffset.TransformAway)

                        AssertFalse(
                            Anchor.IsValid,
                            "TransformAway should remove the original anchor.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that anchors inside the replaced range are removed.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ReplaceRemovesInternalAnchors()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim StartAnchor =
                            Cs.CreateAnchor(1000)

                        Dim InternalAnchor =
                            Cs.CreateAnchor(1250)

                        Dim EndAnchor =
                            Cs.CreateAnchor(1500)

                        Cs.Replace(
                            1000,
                            500,
                            GeneratePatternData(
                                500,
                                2),
                            ChunkedStream.AnchorActionsAtLogicalOffset.Use)

                        AssertTrue(
                            StartAnchor.IsValid,
                            "Replacement start anchor should survive.")

                        AssertFalse(
                            InternalAnchor.IsValid,
                            "Internal anchors should be removed.")

                        AssertTrue(
                            EndAnchor.IsValid,
                            "Anchors at the replacement end should survive.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Clone behaviour"

            ''' <summary>
            ''' Verifies that clone operations do not duplicate source anchor identities.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneDoesNotDuplicateSourceAnchors()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1000)

                        Cs.CloneInsert(
                            900,
                            500,
                            Cs.Length,
                            ChunkedStream.AnchorActionsAtLogicalOffset.TransformAway)

                        AssertEqual(
                            1,
                            Cs.GetAnchors().Count,
                            "CloneInsert should not copy source anchor identities.")

                        AssertTrue(
                            Anchor.IsValid,
                            "Original anchor should remain valid.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Persistence and checkpoints"

            ''' <summary>
            ''' Verifies that anchors survive reopen.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnchorSurvivesReopen()

                Using Ms As New MemoryStream()

                    Dim AnchorId As Long

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        AnchorId =
                            Cs.CreateAnchor(1000).AnchorId

                    End Using

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Anchor =
                            Cs.GetAnchor(AnchorId)

                        AssertTrue(
                            Anchor.IsValid,
                            "Anchor should survive reopen.")

                        AssertEqual(
                            1000L,
                            Anchor.Offset,
                            "Anchor offset mismatch after reopen.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rollback removes anchors created during the checkpoint.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CheckpointRollbackRemovesCreatedAnchor()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                4096,
                                1))

                        Dim AnchorId As Long

                        Using Checkpoint = Cs.CreateCheckpoint()

                            AnchorId =
                                Cs.CreateAnchor(1000).AnchorId

                        End Using

                        AssertThrows(Of KeyNotFoundException)(
                            Sub()
                                Cs.GetAnchor(AnchorId)
                            End Sub,
                            "Rollback should remove created anchors.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Policy migration and rebuild"

            ''' <summary>
            ''' Verifies that chunk-size migration preserves anchors.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ApplyOptionsChunkSizeRewritePreservesAnchors()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(
                            0,
                            GeneratePatternData(
                                8192,
                                1))

                        Dim Anchor =
                            Cs.CreateAnchor(1500)

                        Dim AnchorId =
                            Anchor.AnchorId

                        Cs.Options.ChunkSize = 700

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.ChunkSize)

                        Dim RestoredAnchor =
                            Cs.GetAnchor(AnchorId)

                        AssertEqual(
                            1500L,
                            RestoredAnchor.Offset,
                            "Chunk-size rewrite should preserve anchors.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that rebuild defragmentation preserves anchors.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub DefragmentRebuildPreservesAnchors()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        CreateFragmentedLayout(Cs)

                        Dim Anchor =
                            Cs.CreateAnchor(1500)

                        Dim AnchorId =
                            Anchor.AnchorId

                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Rebuild)

                        Dim RestoredAnchor =
                            Cs.GetAnchor(AnchorId)

                        AssertEqual(
                            1500L,
                            RestoredAnchor.Offset,
                            "Rebuild defragmentation should preserve anchors.")

                    End Using

                End Using

            End Sub

#End Region

#Region "Anchor id allocator"

            ''' <summary>
            ''' Verifies that an anchor id issued inside a checkpoint is not handed out again
            ''' after the checkpoint is rolled back. The allocator must stay monotonic across
            ''' rollback so a stale Anchor handle can never resolve to unrelated data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnchorIdIsNotReusedAfterCheckpointRollback()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(4096, 1))

                        Dim RolledBackId As Long

                        Using Checkpoint = Cs.CreateCheckpoint()

                            Dim InnerAnchor = Cs.CreateAnchor(1000)
                            RolledBackId = InnerAnchor.AnchorId

                            Checkpoint.Rollback()

                            AssertFalse(
                                InnerAnchor.IsValid,
                                "The anchor created inside the checkpoint should be invalid after rollback.")

                        End Using

                        Dim AfterAnchor = Cs.CreateAnchor(2000)

                        AssertNotEqual(
                            RolledBackId,
                            AfterAnchor.AnchorId,
                            "An anchor id issued inside a rolled-back checkpoint was reused.")

                        AssertTrue(
                            AfterAnchor.AnchorId > RolledBackId,
                            "Anchor ids must keep increasing across a checkpoint rollback.")

                        Dim StaleHandle As ChunkedStream.Anchor = Nothing

                        AssertFalse(
                            Cs.TryGetAnchor(RolledBackId, StaleHandle),
                            "The rolled-back anchor id must not resolve to the newly created anchor.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies the same monotonic-allocator guarantee for a DeferPublish scope that
            ''' issues an anchor id and is then abandoned without publishing.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnchorIdIsNotReusedAfterDeferPublishRollback()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GeneratePatternData(4096, 2))

                        Dim RolledBackId As Long

                        Using Scope = Cs.DeferPublish()
                            RolledBackId = Cs.CreateAnchor(1500).AnchorId
                            ' No Scope.Publish() - the scope rolls back on dispose.
                        End Using

                        Dim AfterAnchor = Cs.CreateAnchor(2500)

                        AssertTrue(
                            AfterAnchor.AnchorId > RolledBackId,
                            "An anchor id issued inside an abandoned DeferPublish scope was reused.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

#End Region

        End Class

    End Class

End Namespace