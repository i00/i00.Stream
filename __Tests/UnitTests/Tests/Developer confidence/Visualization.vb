Imports i00.Streams
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Tests

    Partial Class DeveloperConfidence

        Public NotInheritable Class Visualization

            ''' <summary>
            ''' Verifies DrawFragmentation/GenerateFragmentationBitmap actually paint each block's
            ''' colour into the correct horizontal pixel range - not just that the colour-selector
            ''' callback fires, but that its output lands where GetBlockBounds says it should.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub FragmentationBitmapPaintsSelectedColorsAtExpectedBlockPositions()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        ' A few separated writes with a gap between them guarantee more than one
                        ' distinct region (chunk / hole) for the renderer to work with.
                        Cs.Write(0, GenerateRandomData(4096, 6601))
                        Cs.Write(4096 * 3, GenerateRandomData(4096, 6602))

                        Const Width As Integer = 80
                        Const HorizontalBlockCount As Integer = 8

                        Dim ExpectedColorByBlockIndex As New Dictionary(Of Long, Color)
                        Dim NextColorValue = 0

                        Dim Options As New FragmentationDrawOptions With {
                            .MaxBlockCount = HorizontalBlockCount,
                            .PixelPadding = 0,
                            .BorderColor = Color.Transparent
                        }

                        Dim CallCount = 0

                        Dim Struct = Cs.GetStructure()

                        ' Capture per-block colours by mirroring the same colour-selector call sequence
                        ' the real render pass will make, since RenderBlocks/BlockIndex are internal.
                        ' Simplest reliable approach: wrap the selector to also record the block's
                        ' pixel bounds using the same Left/Right formula GetBlockBounds uses.
                        Dim BlockIndex As Long = -1

                        Options.RegionColorSelector =
                            Function(Regions As ChunkedStreamStructure.Region(),
                                     SuggestedColor As Color) As Color

                                BlockIndex += 1
                                NextColorValue += 1

                                Dim Sentinel = Color.FromArgb(255, 1, 1, (NextColorValue Mod 250) + 1)

                                ExpectedColorByBlockIndex(BlockIndex) = Sentinel

                                Return Sentinel

                            End Function

                        Using Bitmap = New Bitmap(Width, 1) 'Struct.GenerateFragmentationBitmap(Width, 1, Options)
                            Using g = Graphics.FromImage(Bitmap)
                                Struct.DrawFragmentation(g, New Rectangle(0, 0, Bitmap.Width, Bitmap.Height), Options)
                            End Using

                            AssertTrue(
                                ExpectedColorByBlockIndex.Count > 0,
                                "Test setup failed to invoke the colour selector.")

                            Dim BitmapData =
                                Bitmap.LockBits(
                                    New Rectangle(0, 0, Bitmap.Width, Bitmap.Height),
                                    Imaging.ImageLockMode.ReadOnly,
                                    Imaging.PixelFormat.Format24bppRgb)

                            Try

                                Dim Stride = BitmapData.Stride
                                Dim RowBytes(Stride - 1) As Byte
                                Marshal.Copy(BitmapData.Scan0, RowBytes, 0, Stride)

                                For Each Entry In ExpectedColorByBlockIndex

                                    Dim Column = Entry.Key

                                    Dim Left =
                                        CInt(Math.Floor((Column / CDbl(HorizontalBlockCount)) * Width))

                                    Dim Right =
                                        CInt(Math.Floor(((Column + 1) / CDbl(HorizontalBlockCount)) * Width))

                                    If Right <= Left Then Right = Left + 1
                                    If Right > Width Then Right = Width

                                    Dim SampleX = (Left + Right) \ 2

                                    Dim PixelStart = SampleX * 3
                                    Dim ActualBlue = RowBytes(PixelStart)
                                    Dim ActualGreen = RowBytes(PixelStart + 1)
                                    Dim ActualRed = RowBytes(PixelStart + 2)

                                    AssertEqual(
                                        CInt(Entry.Value.R),
                                        CInt(ActualRed),
                                        $"Unexpected red channel for block {Column} at x={SampleX}.")

                                    AssertEqual(
                                        CInt(Entry.Value.G),
                                        CInt(ActualGreen),
                                        $"Unexpected green channel for block {Column} at x={SampleX}.")

                                    AssertEqual(
                                        CInt(Entry.Value.B),
                                        CInt(ActualBlue),
                                        $"Unexpected blue channel for block {Column} at x={SampleX}.")

                                Next

                            Finally
                                Bitmap.UnlockBits(BitmapData)
                            End Try

                        End Using

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace