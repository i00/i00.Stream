Imports System.Runtime.CompilerServices

Namespace Streams
    Public Module Extensions

        Public Class FragmentationDrawOptions

            Public Enum RenderLengthModes

                ''' <summary>
                ''' Render using the physical end offset of the highest referenced live chunk record.
                ''' </summary>
                LiveDataEndOffset

                ''' <summary>
                ''' Render using the physical end offset of the live data and metadata area.
                ''' </summary>
                DataAreaEndOffset

                ''' <summary>
                ''' Render using the physical end offset of the active metadata root.
                ''' </summary>
                MetadataRootEndOffset

                ''' <summary>
                ''' Render using the full physical backing-stream length.
                ''' </summary>
                PhysicalLength

            End Enum

            Public Delegate Sub RegionPainterDelegate(Surface As Graphics,
                                                      Bounds As Rectangle,
                                                      SuggestedColor As Color,
                                                      Regions As Streams.ChunkedStreamStructure.Region())

            Public Delegate Function RegionColorSelectorDelegate(Regions As Streams.ChunkedStreamStructure.Region(),
                                                                 SuggestedColor As Color) As Color

            Public Shared ReadOnly DefaultRegionPainter As RegionPainterDelegate =
                Sub(Surface, Bounds, SuggestedColor, Regions)

                    If Surface Is Nothing Then Throw New ArgumentNullException(NameOf(Surface))
                    If Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then Return
                    If SuggestedColor.A = 0 Then Return

                    Using Brush As New SolidBrush(SuggestedColor)
                        Surface.FillRectangle(Brush, Bounds)
                    End Using

                End Sub

            Public Shared ReadOnly DefaultRegionColorSelector As RegionColorSelectorDelegate =
                Function(Regions, SuggestedColor)
                    Return SuggestedColor
                End Function

            Private _RegionPainter As RegionPainterDelegate = DefaultRegionPainter
            Private _RegionColorSelector As RegionColorSelectorDelegate = DefaultRegionColorSelector

            ''' <summary>
            ''' Colours used for each physical region type.
            ''' </summary>
            Public Property Colors As New Dictionary(Of Streams.ChunkedStreamStructure.RegionTypes, Color) From
            {
                {Streams.ChunkedStreamStructure.RegionTypes.Header, Color.DarkBlue},
                {Streams.ChunkedStreamStructure.RegionTypes.Chunk, Color.LimeGreen},
                {Streams.ChunkedStreamStructure.RegionTypes.Hole, Color.Red},
                {Streams.ChunkedStreamStructure.RegionTypes.Index, Color.DarkGreen},
                {Streams.ChunkedStreamStructure.RegionTypes.IndexPage, Color.DarkGreen},
                {Streams.ChunkedStreamStructure.RegionTypes.ChunkIndexDirectoryPage, Color.ForestGreen},
                {Streams.ChunkedStreamStructure.RegionTypes.HoleDirectoryPage, Color.Orange},
                {Streams.ChunkedStreamStructure.RegionTypes.MetadataRoot, Color.Gold},
                {Streams.ChunkedStreamStructure.RegionTypes.Unused, Color.LightGray},
                {Streams.ChunkedStreamStructure.RegionTypes.Unknown, Color.Transparent}
            }

            ''' <summary>
            ''' Background colour used before rendering structure blocks.
            ''' </summary>
            Public Property BackgroundColor As Color = Color.Black

            ''' <summary>
            ''' Optional border colour drawn around the output rectangle.
            ''' </summary>
            Public Property BorderColor As Color = Color.Transparent

            ''' <summary>
            ''' Padding applied inside each rendered block.
            ''' </summary>
            Public Property PixelPadding As Integer = 0

            ''' <summary>
            ''' Maximum number of horizontal blocks used when rendering.
            ''' If Nothing, less than or equal to zero, or greater than the target width,
            ''' the target width is used.
            ''' </summary>
            ''' <remarks>
            ''' For example, a 400px wide bitmap with MaxBlockCount = 10 renders
            ''' 10 horizontal blocks, each approximately 40px wide.
            ''' If MaxBlockCount = 8000 for the same bitmap, 400 horizontal blocks are used.
            ''' </remarks>
            Public Property MaxXBlockCount As Integer?

            ''' <summary>
            ''' Maximum number of vertical blocks used when rendering.
            ''' If Nothing, less than or equal to zero, or greater than the target height,
            ''' the target height is used.
            ''' </summary>
            Public Property MaxYBlockCount As Integer?
            ''' <summary>
            ''' Controls which stream length is used when mapping physical offsets to blocks.
            ''' </summary>
            Public Property RenderLengthMode As RenderLengthModes = RenderLengthModes.LiveDataEndOffset

            ''' <summary>
            ''' Selects the final colour used for a rendered block after the default
            ''' dominant-region colour has been calculated.
            ''' </summary>
            Public Property RegionColorSelector As RegionColorSelectorDelegate
                Get
                    Return _RegionColorSelector
                End Get
                Set
                    If Value Is Nothing Then
                        _RegionColorSelector = DefaultRegionColorSelector
                    Else
                        _RegionColorSelector = Value
                    End If
                End Set
            End Property

            ''' <summary>
            ''' Paints a rendered block.
            ''' The regions array contains all physical regions that overlap the block.
            ''' </summary>
            Public Property RegionPainter As RegionPainterDelegate
                Get
                    Return _RegionPainter
                End Get
                Set
                    If Value Is Nothing Then
                        _RegionPainter = DefaultRegionPainter
                    Else
                        _RegionPainter = Value
                    End If
                End Set
            End Property

        End Class

        Private NotInheritable Class RenderSegment

            Public Property Offset As Long

            Public Property EndOffset As Long

            Public Property Color As Color

            Public Property Region As Streams.ChunkedStreamStructure.Region

        End Class

        Private NotInheritable Class RenderBlock

            Public Property BlockIndex As Long

            Public Property Offset As Long

            Public Property EndOffset As Long

            Public Property SuggestedColor As Color

            Public Property Regions As Streams.ChunkedStreamStructure.Region()

        End Class

        <Extension>
        Public Sub DrawFragmentation(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                     Graphics As Graphics,
                                     Rect As Rectangle,
                                     Optional Options As FragmentationDrawOptions = Nothing)

            If ChunkedStreamStructure Is Nothing Then Throw New ArgumentNullException(NameOf(ChunkedStreamStructure))
            If Graphics Is Nothing Then Throw New ArgumentNullException(NameOf(Graphics))
            If Rect.Width <= 0 OrElse Rect.Height <= 0 Then Return

            If Options Is Nothing Then
                Options = New FragmentationDrawOptions()
            End If

            DrawFragmentationBlocks(ChunkedStreamStructure, Graphics, Rect, Options)

            If Options.BorderColor.A > 0 Then
                Using BorderPen As New Pen(Options.BorderColor)
                    Graphics.DrawRectangle(BorderPen, Rect.Left, Rect.Top, Rect.Width - 1, Rect.Height - 1)
                End Using
            End If

        End Sub

        <Extension>
        Public Function GenerateFragmentationBitmap(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                                    Width As Integer,
                                                    Height As Integer,
                                                    Optional Options As FragmentationDrawOptions = Nothing) As Bitmap

            Return ChunkedStreamStructure.GenerateFragmentationBitmap(New Size(Width, Height), Options)

        End Function

        <Extension>
        Public Function GenerateFragmentationBitmap(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                                    Size As Size,
                                                    Optional Options As FragmentationDrawOptions = Nothing) As Bitmap

            If ChunkedStreamStructure Is Nothing Then Throw New ArgumentNullException(NameOf(ChunkedStreamStructure))
            If Size.Width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Size), "Bitmap width must be greater than zero.")
            If Size.Height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Size), "Bitmap height must be greater than zero.")

            If Options Is Nothing Then
                Options = New FragmentationDrawOptions()
            End If

            Dim Bitmap As New Bitmap(Size.Width, Size.Height, Imaging.PixelFormat.Format24bppRgb)

            Using Surface = Graphics.FromImage(Bitmap)
                DrawFragmentationBlocks(ChunkedStreamStructure, Surface, New Rectangle(Point.Empty, Size), Options)

                If Options.BorderColor.A > 0 Then
                    Using BorderPen As New Pen(Options.BorderColor)
                        Surface.DrawRectangle(BorderPen, 0, 0, Bitmap.Width - 1, Bitmap.Height - 1)
                    End Using
                End If
            End Using

            Return Bitmap

        End Function

        Private Sub DrawFragmentationBlocks(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                            Surface As Graphics,
                                            Rect As Rectangle,
                                            Options As FragmentationDrawOptions)

            Using BackgroundBrush As New SolidBrush(Options.BackgroundColor)
                Surface.FillRectangle(BackgroundBrush, Rect)
            End Using

            Dim HorizontalBlockCount = GetHorizontalBlockCount(Rect.Width, Options)
            Dim VerticalBlockCount = GetVerticalBlockCount(Rect.Height, Options)

            Dim RenderBlocks =
                BuildRenderBlocks(ChunkedStreamStructure,
                                  HorizontalBlockCount,
                                  VerticalBlockCount,
                                  Options)

            ''TODO: maybe have some callback in here in here so the user has some way for the user to do something like this to make fragmentation odvious?? ... maybe something called EvalBlocks?
            'Dim Frag = ChunkedStreamStructure.FragmentationRatio
            ''.Size = Math.Max(0L, Math.Min(BlockEndOffset, x.EndOffset) - Math.Max(BlockOffset, x.Offset))})
            'Dim BlocksByWorseToBest = RenderBlocks.OrderByDescending(Function(x) x.Regions.Count(Function(y) y.RegionType = Streams.ChunkedStreamStructure.RegionTypes.Hole)).ToArray()
            'For iBlock = 0 To BlocksByWorseToBest.Count - 1
            '    Dim Block = BlocksByWorseToBest(iBlock
            '                                   )
            '    Dim PercentThrough = iBlock / BlocksByWorseToBest.Count
            '    If PercentThrough < Frag Then
            '        Block.SuggestedColor = Color.Red
            '    Else
            '        Block.SuggestedColor = Color.LimeGreen
            '    End If
            'Next


            For Each Block In RenderBlocks

                Dim Bounds = GetBlockBounds(Rect, HorizontalBlockCount, VerticalBlockCount, Block.BlockIndex)

                If Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then Continue For

                If Options.PixelPadding > 0 Then

                    Bounds.Inflate(-Options.PixelPadding, -Options.PixelPadding)

                    If Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then Continue For

                End If

                Options.RegionPainter.Invoke(Surface,
                                             Bounds,
                                             Block.SuggestedColor,
                                             Block.Regions)

            Next

        End Sub

        Private Function BuildRenderSegments(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                             Options As FragmentationDrawOptions) As List(Of RenderSegment)

            Dim RenderSegments As New List(Of RenderSegment)

            For Each Region In ChunkedStreamStructure.Regions

                Dim RegionColor = GetRegionColor(Region.RegionType, Options)

                RenderSegments.Add(New RenderSegment With {
                    .Offset = Region.PhysicalOffset,
                    .EndOffset = Region.PhysicalEndOffset,
                    .Color = RegionColor,
                    .Region = Region
                })

            Next

            RenderSegments.Sort(
                Function(left, right)
                    Dim OffsetComparison = left.Offset.CompareTo(right.Offset)

                    If OffsetComparison <> 0 Then
                        Return OffsetComparison
                    End If

                    Return left.EndOffset.CompareTo(right.EndOffset)
                End Function)

            Return RenderSegments

        End Function

        Private Function BuildRenderBlocks(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                           HorizontalBlockCount As Integer,
                                           VerticalBlockCount As Integer,
                                           Options As FragmentationDrawOptions) As List(Of RenderBlock)

            If HorizontalBlockCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(HorizontalBlockCount))
            If VerticalBlockCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(VerticalBlockCount))

            Dim RenderSegments = BuildRenderSegments(ChunkedStreamStructure, Options)
            Dim RenderBlocks As New List(Of RenderBlock)

            Dim TotalBlocks = CLng(HorizontalBlockCount) * CLng(VerticalBlockCount)
            Dim TotalBytes = GetRenderLength(ChunkedStreamStructure, Options)

            If TotalBlocks <= 0 Then Return RenderBlocks

            Dim SegmentStartIndex = 0

            For BlockIndex = 0L To TotalBlocks - 1L

                Dim BlockOffset = CLng(Math.Floor((BlockIndex / CDbl(TotalBlocks)) * TotalBytes))
                Dim BlockEndOffset = CLng(Math.Ceiling(((BlockIndex + 1L) / CDbl(TotalBlocks)) * TotalBytes))

                If BlockEndOffset <= BlockOffset Then
                    BlockEndOffset = BlockOffset + 1L
                End If

                If BlockEndOffset > TotalBytes Then
                    BlockEndOffset = TotalBytes
                End If

                While SegmentStartIndex < RenderSegments.Count AndAlso
                      RenderSegments(SegmentStartIndex).EndOffset <= BlockOffset

                    SegmentStartIndex += 1

                End While

                Dim OverlappingSegments As New List(Of RenderSegment)
                Dim SegmentIndex = SegmentStartIndex

                While SegmentIndex < RenderSegments.Count

                    Dim Segment = RenderSegments(SegmentIndex)

                    If Segment.Offset >= BlockEndOffset Then Exit While

                    If Segment.EndOffset > BlockOffset Then
                        OverlappingSegments.Add(Segment)
                    End If

                    SegmentIndex += 1

                End While

                If OverlappingSegments.Count = 0 Then Continue For

                Dim Regions = OverlappingSegments.Select(Function(segment) segment.Region).ToArray()
                Dim DefaultColor = GetDominantColor(OverlappingSegments, BlockOffset, BlockEndOffset)
                Dim SuggestedColor = Options.RegionColorSelector.Invoke(Regions, DefaultColor)

                RenderBlocks.Add(New RenderBlock With {
                    .BlockIndex = BlockIndex,
                    .Offset = BlockOffset,
                    .EndOffset = BlockEndOffset,
                    .SuggestedColor = SuggestedColor,
                    .Regions = Regions
                })

            Next

            Return RenderBlocks

        End Function

        Private Function GetDominantColor(Segments As IEnumerable(Of RenderSegment),
                                      BlockOffset As Long,
                                      BlockEndOffset As Long) As Color

            Dim WeightedLinearR As Double = 0
            Dim WeightedLinearG As Double = 0
            Dim WeightedLinearB As Double = 0

            Dim TotalWeight As Double = 0

            For Each Segment In Segments

                If Segment.Color.A = 0 Then Continue For

                Dim OverlapLength =
                Math.Max(
                    0L,
                    Math.Min(BlockEndOffset, Segment.EndOffset) -
                    Math.Max(BlockOffset, Segment.Offset))

                If OverlapLength <= 0 Then Continue For

                Dim Weight = CDbl(OverlapLength)

                ' Convert from sRGB to linear light before averaging.
                Dim LinearR = Math.Pow(Segment.Color.R / 255.0R, 2.2R)
                Dim LinearG = Math.Pow(Segment.Color.G / 255.0R, 2.2R)
                Dim LinearB = Math.Pow(Segment.Color.B / 255.0R, 2.2R)

                WeightedLinearR += LinearR * Weight
                WeightedLinearG += LinearG * Weight
                WeightedLinearB += LinearB * Weight

                TotalWeight += Weight

            Next

            If TotalWeight <= 0 Then
                Return Color.Transparent
            End If

            Dim AverageLinearR = WeightedLinearR / TotalWeight
            Dim AverageLinearG = WeightedLinearG / TotalWeight
            Dim AverageLinearB = WeightedLinearB / TotalWeight

            Return Color.FromArgb(
            CInt(Math.Round(Math.Pow(AverageLinearR, 1.0R / 2.2R) * 255)),
            CInt(Math.Round(Math.Pow(AverageLinearG, 1.0R / 2.2R) * 255)),
            CInt(Math.Round(Math.Pow(AverageLinearB, 1.0R / 2.2R) * 255)))

        End Function

        Private Function GetHorizontalBlockCount(Width As Integer,
                                                 Options As FragmentationDrawOptions) As Integer

            If Width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Width))

            If Options.MaxXBlockCount.HasValue Then
                Dim RequestedBlockCount = Options.MaxXBlockCount.Value

                If RequestedBlockCount > 0 AndAlso RequestedBlockCount <= Width Then
                    Return RequestedBlockCount
                End If

            End If

            Return Width

        End Function

        Private Function GetVerticalBlockCount(Height As Integer,
                                               Options As FragmentationDrawOptions) As Integer

            If Height <= 0 Then
                Throw New ArgumentOutOfRangeException(NameOf(Height))
            End If

            If Options.MaxYBlockCount.HasValue Then

                Dim RequestedBlockCount = Options.MaxYBlockCount.Value

                If RequestedBlockCount > 0 AndAlso RequestedBlockCount <= Height Then

                    Return RequestedBlockCount

                End If

            End If

            Return Height

        End Function

        Private Function GetBlockBounds(BaseRect As Rectangle,
                                        HorizontalBlockCount As Integer,
                                        VerticalBlockCount As Integer,
                                        BlockIndex As Long) As Rectangle

            If HorizontalBlockCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(HorizontalBlockCount))
            If VerticalBlockCount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(VerticalBlockCount))
            If BlockIndex < 0 Then Throw New ArgumentOutOfRangeException(NameOf(BlockIndex))

            Dim Row = CInt(BlockIndex \ HorizontalBlockCount)
            Dim Column = CInt(BlockIndex Mod HorizontalBlockCount)

            If Row >= VerticalBlockCount Then
                Return Rectangle.Empty
            End If

            Dim Left =
                BaseRect.Left +
                CInt(Math.Floor((Column / CDbl(HorizontalBlockCount)) * BaseRect.Width))

            Dim Right =
                BaseRect.Left +
                CInt(Math.Floor(((Column + 1) / CDbl(HorizontalBlockCount)) * BaseRect.Width))

            If Right <= Left Then
                Right = Left + 1
            End If

            If Right > BaseRect.Right Then
                Right = BaseRect.Right
            End If

            Dim Top =
                BaseRect.Top +
                CInt(Math.Floor((Row / CDbl(VerticalBlockCount)) * BaseRect.Height))

            Dim Bottom =
                BaseRect.Top +
                CInt(Math.Floor(((Row + 1) / CDbl(VerticalBlockCount)) * BaseRect.Height))

            If Bottom <= Top Then
                Bottom = Top + 1
            End If

            If Bottom > BaseRect.Bottom Then
                Bottom = BaseRect.Bottom
            End If

            Return New Rectangle(
                Left,
                Top,
                Math.Max(0, Right - Left),
                Math.Max(0, Bottom - Top))

        End Function

        Private Function GetRenderLength(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                         Options As FragmentationDrawOptions) As Long

            Select Case Options.RenderLengthMode

                Case FragmentationDrawOptions.RenderLengthModes.LiveDataEndOffset
                    Return Math.Max(1L, ChunkedStreamStructure.LiveDataEndOffset)

                Case FragmentationDrawOptions.RenderLengthModes.DataAreaEndOffset
                    Return Math.Max(1L, ChunkedStreamStructure.DataAreaEndOffset)

                Case FragmentationDrawOptions.RenderLengthModes.MetadataRootEndOffset
                    Return Math.Max(1L, ChunkedStreamStructure.MetadataRootEndOffset)

                Case FragmentationDrawOptions.RenderLengthModes.PhysicalLength
                    Return Math.Max(1L, ChunkedStreamStructure.PhysicalLength)

                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(Options.RenderLengthMode))

            End Select

        End Function

        Private Function GetRegionColor(RegionType As Streams.ChunkedStreamStructure.RegionTypes,
                                        Options As FragmentationDrawOptions) As Color

            Dim RegionColor As Color = Color.Transparent

            If Options.Colors IsNot Nothing AndAlso Options.Colors.TryGetValue(RegionType, RegionColor) Then
                Return RegionColor
            End If

            Return Color.Transparent

        End Function

    End Module

End Namespace

'OLD fast method that did not use the ChunkedStreamStructure
'''' <summary>
'''' Generates a bitmap visualisation of the physical storage layout.
'''' </summary>
'''' <param name="Width">Bitmap width.</param>
'''' <param name="Height">Bitmap height.</param>
'''' <returns>A bitmap showing live data, fragmented space and the index table.</returns>
'Public Function GenerateFragmentationBitmap(Width As Integer,
'                                            Height As Integer) As Bitmap

'    SyncLock _SyncRoot

'        ThrowIfDisposed()

'        If Width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Width))
'        If Height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(Height))

'        Dim Result As New Bitmap(Width,
'                                 Height,
'                                 Imaging.PixelFormat.Format24bppRgb)

'        Dim DataEnd = GetDataEndFromIndex()

'        If DataEnd <= DataStartOffset Then

'            Using Graphics = System.Drawing.Graphics.FromImage(Result)
'                Graphics.Clear(Color.Black)
'            End Using

'            Return Result

'        End If

'        Dim Segments As New List(Of Tuple(Of Long,
'                                          Long,
'                                          Color))

'        '
'        ' Live chunks.
'        '
'        For Each Entry In _Index

'            If Entry.Offset = 0 OrElse
'               Entry.RecordLength <= 0 Then

'                Continue For

'            End If

'            Segments.Add(
'                Tuple.Create(
'                    Entry.Offset,
'                    Entry.Offset + CLng(Entry.RecordLength),
'                    Color.LimeGreen))

'        Next

'        '
'        ' Index table.
'        '
'        Dim IndexBytes =
'            CLng(_Index.Count) * IndexEntrySize

'        If IndexBytes > 0 Then

'            Segments.Add(
'                Tuple.Create(
'                    _IndexOffset,
'                    _IndexOffset + IndexBytes,
'                    Color.DodgerBlue))

'        End If

'        Segments.Sort(
'            Function(left, right)
'                Return left.Item1.CompareTo(right.Item1)
'            End Function)

'        '
'        ' Insert dead-space segments.
'        '
'        Dim RenderSegments As New List(Of Tuple(Of Long,
'                                                Long,
'                                                Color))

'        Dim Cursor = CLng(DataStartOffset)

'        For Each Segment In Segments

'            If Segment.Item1 > Cursor Then

'                RenderSegments.Add(
'                    Tuple.Create(
'                        Cursor,
'                        Segment.Item1,
'                        Color.Red))

'            End If

'            RenderSegments.Add(Segment)

'            Cursor =
'                Math.Max(
'                    Cursor,
'                    Segment.Item2)

'        Next

'        If Cursor < DataEnd Then

'            RenderSegments.Add(
'                Tuple.Create(
'                    Cursor,
'                    DataEnd,
'                    Color.Red))

'        End If

'        '
'        ' Black background.
'        '
'        Dim BitmapData =
'            Result.LockBits(
'                New Rectangle(0, 0, Width, Height),
'                Imaging.ImageLockMode.WriteOnly,
'                Imaging.PixelFormat.Format24bppRgb)

'        Try

'            Dim Stride = BitmapData.Stride
'            Dim Buffer(Math.Abs(Stride) * Height - 1) As Byte

'            '
'            ' Buffer is already zero-initialised, giving us a black background.
'            '

'            Dim TotalPixels = Width * Height
'            Dim TotalBytes = CDbl(DataEnd)

'            For Each Segment In RenderSegments

'                Dim StartPixel =
'                    CInt(
'                        Math.Floor(
'                            (Segment.Item1 / TotalBytes) *
'                            TotalPixels))

'                Dim EndPixel =
'                    CInt(
'                        Math.Ceiling(
'                            (Segment.Item2 / TotalBytes) *
'                            TotalPixels))

'                If EndPixel <= StartPixel Then
'                    EndPixel = StartPixel + 1
'                End If

'                If EndPixel > TotalPixels Then
'                    EndPixel = TotalPixels
'                End If

'                Dim Colour = Segment.Item3

'                Dim Blue = Colour.B
'                Dim Green = Colour.G
'                Dim Red = Colour.R

'                For PixelIndex = StartPixel To EndPixel - 1

'                    Dim X = PixelIndex Mod Width
'                    Dim Y = PixelIndex \ Width

'                    If Y >= Height Then Exit For

'                    Dim BufferOffset =
'                        (Y * Stride) +
'                        (X * 3)

'                    Buffer(BufferOffset) = Blue
'                    Buffer(BufferOffset + 1) = Green
'                    Buffer(BufferOffset + 2) = Red

'                Next

'            Next

'            Runtime.InteropServices.Marshal.Copy(
'                Buffer,
'                0,
'                BitmapData.Scan0,
'                Buffer.Length)

'        Finally

'            Result.UnlockBits(BitmapData)

'        End Try

'        Return Result

'    End SyncLock

'End Function
