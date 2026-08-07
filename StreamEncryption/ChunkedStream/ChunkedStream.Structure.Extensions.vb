Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices

Public Module Extensions

    Public Class FragmentationDrawOptions

        Public Enum SegmentColorTypes
            Header
            Chunk
            Hole
            Index
            Unused
            Unknown
        End Enum

        Public Enum RenderLengthModes
            LiveDataEndOffset
            DataAreaEndOffset
            IndexEndOffset
            PhysicalLength
        End Enum

        Public Property Colors As New Dictionary(Of SegmentColorTypes, Color) From
        {
            {SegmentColorTypes.Header, Color.DarkBlue},
            {SegmentColorTypes.Chunk, Color.LimeGreen},
            {SegmentColorTypes.Hole, Color.Red},
            {SegmentColorTypes.Index, Color.DarkGreen},
            {SegmentColorTypes.Unused, Color.LightGray},
            {SegmentColorTypes.Unknown, Color.Transparent}
        }

        Public Property BackgroundColor As Color = Color.Black

        Public Property BorderColor As Color = Color.Transparent

        Public Property PixelPadding As Integer = 0

        ''' <summary>
        ''' Controls which stream length is used when mapping physical offsets to pixels.
        ''' LiveDataEndOffset most closely matches the original in-stream renderer.
        ''' </summary>
        Public Property RenderLengthMode As RenderLengthModes = RenderLengthModes.LiveDataEndOffset

        Public Delegate Sub RegionPainterDelegate(Surface As Graphics,
                                                   Bounds As Rectangle,
                                                   SuggestedColor As Color,
                                                   Region As Streams.ChunkedStreamStructure.Region)

        Public Property RegionPainter As RegionPainterDelegate

    End Class

    Private NotInheritable Class RenderSegment

        Public Property Offset As Long

        Public Property EndOffset As Long

        Public Property Colour As Color

        Public Property Region As Streams.ChunkedStreamStructure.Region

    End Class

    <Extension>
    Public Sub DrawFragmentation(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                 Surface As Graphics,
                                 Rect As Rectangle,
                                 Optional Options As FragmentationDrawOptions = Nothing)

        If ChunkedStreamStructure Is Nothing Then Throw New ArgumentNullException(NameOf(ChunkedStreamStructure))
        If Surface Is Nothing Then Throw New ArgumentNullException(NameOf(Surface))
        If Rect.Width <= 0 OrElse Rect.Height <= 0 Then Return

        If Options Is Nothing Then
            Options = New FragmentationDrawOptions()
        End If

        If Options.RegionPainter Is Nothing AndAlso Options.PixelPadding <= 0 Then
            Using Bitmap As New Bitmap(Rect.Width, Rect.Height, Imaging.PixelFormat.Format24bppRgb)
                RenderToBitmap(ChunkedStreamStructure, Bitmap, Options)
                Surface.DrawImageUnscaled(Bitmap, Rect.Location)
            End Using
        Else
            DrawFragmentationWithGraphics(ChunkedStreamStructure, Surface, Rect, Options)
        End If

        If Options.BorderColor.A > 0 Then
            Using BorderPen As New Pen(Options.BorderColor)
                Surface.DrawRectangle(BorderPen, Rect.Left, Rect.Top, Rect.Width - 1, Rect.Height - 1)
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

        If Options.RegionPainter Is Nothing AndAlso Options.PixelPadding <= 0 Then
            RenderToBitmap(ChunkedStreamStructure, Bitmap, Options)
        Else
            Using Surface = Graphics.FromImage(Bitmap)
                ChunkedStreamStructure.DrawFragmentation(Surface, New Rectangle(Point.Empty, Size), Options)
            End Using
        End If

        If Options.BorderColor.A > 0 Then
            Using Surface = Graphics.FromImage(Bitmap)
                Using BorderPen As New Pen(Options.BorderColor)
                    Surface.DrawRectangle(BorderPen, 0, 0, Bitmap.Width - 1, Bitmap.Height - 1)
                End Using
            End Using
        End If

        Return Bitmap

    End Function

    Private Sub RenderToBitmap(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                               Bitmap As Bitmap,
                               Options As FragmentationDrawOptions)

        Dim BitmapData = Bitmap.LockBits(New Rectangle(0, 0, Bitmap.Width, Bitmap.Height),
                                         Imaging.ImageLockMode.WriteOnly,
                                         Imaging.PixelFormat.Format24bppRgb)

        Try
            Dim Stride = BitmapData.Stride
            Dim AbsStride = Math.Abs(Stride)
            Dim Buffer(AbsStride * Bitmap.Height - 1) As Byte

            FillBuffer(Buffer,
                       AbsStride,
                       Bitmap.Width,
                       Bitmap.Height,
                       Options.BackgroundColor)

            Dim RenderSegments = BuildRenderSegments(ChunkedStreamStructure, Options)
            Dim TotalPixels = CLng(Bitmap.Width) * CLng(Bitmap.Height)
            Dim TotalBytes = GetRenderLength(ChunkedStreamStructure, Options)

            For Each segment In RenderSegments

                If segment.Colour.A = 0 Then Continue For

                Dim StartPixel = CLng(Math.Floor((segment.Offset / CDbl(TotalBytes)) * TotalPixels))
                Dim EndPixel = CLng(Math.Ceiling((segment.EndOffset / CDbl(TotalBytes)) * TotalPixels))

                If EndPixel <= StartPixel Then
                    EndPixel = StartPixel + 1
                End If

                If StartPixel < 0 Then StartPixel = 0
                If EndPixel > TotalPixels Then EndPixel = TotalPixels
                If StartPixel >= TotalPixels Then Continue For

                PaintPixelRange(Buffer,
                                AbsStride,
                                Bitmap.Width,
                                Bitmap.Height,
                                StartPixel,
                                EndPixel,
                                segment.Colour)

            Next

            Marshal.Copy(Buffer, 0, BitmapData.Scan0, Buffer.Length)

        Finally
            Bitmap.UnlockBits(BitmapData)
        End Try

    End Sub

    Private Sub DrawFragmentationWithGraphics(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                              Surface As Graphics,
                                              Rect As Rectangle,
                                              Options As FragmentationDrawOptions)

        Using BackgroundBrush As New SolidBrush(Options.BackgroundColor)
            Surface.FillRectangle(BackgroundBrush, Rect)
        End Using

        Dim RenderSegments = BuildRenderSegments(ChunkedStreamStructure, Options)
        Dim TotalPixels = CLng(Rect.Width) * CLng(Rect.Height)
        Dim TotalBytes = GetRenderLength(ChunkedStreamStructure, Options)

        For Each segment In RenderSegments

            If segment.Colour.A = 0 Then Continue For

            Dim StartPixel = CLng(Math.Floor((segment.Offset / CDbl(TotalBytes)) * TotalPixels))
            Dim EndPixel = CLng(Math.Ceiling((segment.EndOffset / CDbl(TotalBytes)) * TotalPixels))

            If EndPixel <= StartPixel Then
                EndPixel = StartPixel + 1
            End If

            If StartPixel < 0 Then StartPixel = 0
            If EndPixel > TotalPixels Then EndPixel = TotalPixels
            If StartPixel >= TotalPixels Then Continue For

            PaintPixelRangeWithGraphics(Surface,
                                        Rect,
                                        StartPixel,
                                        EndPixel,
                                        segment.Colour,
                                        segment.Region,
                                        Options)

        Next

    End Sub

    Private Function BuildRenderSegments(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                         Options As FragmentationDrawOptions) As List(Of RenderSegment)

        Dim RenderSegments As New List(Of RenderSegment)

        For Each region In ChunkedStreamStructure.Regions

            Dim SegmentType = GetSegmentType(region)
            Dim SegmentColour = GetSegmentColor(SegmentType, Options)

            If SegmentColour.A = 0 Then Continue For

            RenderSegments.Add(New RenderSegment With {
                .Offset = region.Offset,
                .EndOffset = region.EndOffset,
                .Colour = SegmentColour,
                .Region = region
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

    Private Function GetSegmentType(Region As Streams.ChunkedStreamStructure.Region) As FragmentationDrawOptions.SegmentColorTypes

        Select Case Region.RegionType

            Case Streams.ChunkedStreamStructure.RegionTypes.Header
                Return FragmentationDrawOptions.SegmentColorTypes.Header

            Case Streams.ChunkedStreamStructure.RegionTypes.Chunk
                Return FragmentationDrawOptions.SegmentColorTypes.Chunk

            Case Streams.ChunkedStreamStructure.RegionTypes.Hole
                Return FragmentationDrawOptions.SegmentColorTypes.Hole

            Case Streams.ChunkedStreamStructure.RegionTypes.Index
                Return FragmentationDrawOptions.SegmentColorTypes.Index

            Case Streams.ChunkedStreamStructure.RegionTypes.Unused
                Return FragmentationDrawOptions.SegmentColorTypes.Unused

            Case Else
                Return FragmentationDrawOptions.SegmentColorTypes.Unknown

        End Select

    End Function

    Private Function GetRenderLength(ChunkedStreamStructure As Streams.ChunkedStreamStructure,
                                     Options As FragmentationDrawOptions) As Long

        Select Case Options.RenderLengthMode

            Case FragmentationDrawOptions.RenderLengthModes.LiveDataEndOffset
                Return Math.Max(1L, ChunkedStreamStructure.LiveDataEndOffset)

            Case FragmentationDrawOptions.RenderLengthModes.DataAreaEndOffset
                Return Math.Max(1L, ChunkedStreamStructure.DataAreaEndOffset)

            Case FragmentationDrawOptions.RenderLengthModes.IndexEndOffset
                Return Math.Max(1L, ChunkedStreamStructure.IndexEndOffset)

            Case FragmentationDrawOptions.RenderLengthModes.PhysicalLength
                Return Math.Max(1L, ChunkedStreamStructure.PhysicalLength)

            Case Else
                Throw New ArgumentOutOfRangeException(NameOf(Options.RenderLengthMode))

        End Select

    End Function

    Private Sub FillBuffer(Buffer As Byte(),
                           AbsStride As Integer,
                           Width As Integer,
                           Height As Integer,
                           Colour As Color)

        For Y = 0 To Height - 1

            Dim RowOffset = Y * AbsStride

            For X = 0 To Width - 1

                Dim BufferOffset = RowOffset + (X * 3)

                Buffer(BufferOffset) = Colour.B
                Buffer(BufferOffset + 1) = Colour.G
                Buffer(BufferOffset + 2) = Colour.R

            Next

        Next

    End Sub

    Private Sub PaintPixelRange(Buffer As Byte(),
                                AbsStride As Integer,
                                Width As Integer,
                                Height As Integer,
                                StartPixel As Long,
                                EndPixel As Long,
                                Colour As Color)

        For PixelIndex = StartPixel To EndPixel - 1

            Dim X = CInt(PixelIndex Mod Width)
            Dim Y = CInt(PixelIndex \ Width)

            If Y >= Height Then Exit For

            Dim BufferOffset = (Y * AbsStride) + (X * 3)

            Buffer(BufferOffset) = Colour.B
            Buffer(BufferOffset + 1) = Colour.G
            Buffer(BufferOffset + 2) = Colour.R

        Next

    End Sub

    Private Sub PaintPixelRangeWithGraphics(Surface As Graphics,
                                            Rect As Rectangle,
                                            StartPixel As Long,
                                            EndPixel As Long,
                                            SuggestedColor As Color,
                                            Region As Streams.ChunkedStreamStructure.Region,
                                            Options As FragmentationDrawOptions)

        Dim CurrentPixel = StartPixel

        While CurrentPixel < EndPixel

            Dim Row = CInt(CurrentPixel \ Rect.Width)
            Dim X = CInt(CurrentPixel Mod Rect.Width)

            If Row >= Rect.Height Then Exit While

            Dim RowEndPixel = Math.Min(EndPixel, (CLng(Row) + 1L) * Rect.Width)
            Dim RunLength = CInt(RowEndPixel - CurrentPixel)

            Dim Bounds As New Rectangle(Rect.Left + X,
                                        Rect.Top + Row,
                                        RunLength,
                                        1)

            If Options.PixelPadding > 0 Then

                Bounds.Inflate(-Options.PixelPadding, -Options.PixelPadding)

                If Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then
                    CurrentPixel = RowEndPixel
                    Continue While
                End If

            End If

            If Options.RegionPainter IsNot Nothing Then
                Options.RegionPainter.Invoke(Surface, Bounds, SuggestedColor, Region)
            Else
                Using Brush As New SolidBrush(SuggestedColor)
                    Surface.FillRectangle(Brush, Bounds)
                End Using
            End If

            CurrentPixel = RowEndPixel

        End While

    End Sub

    Private Function GetSegmentColor(SegmentType As FragmentationDrawOptions.SegmentColorTypes,
                                     Options As FragmentationDrawOptions) As Color

        Dim Colour As Color = Color.Transparent

        If Options.Colors IsNot Nothing AndAlso Options.Colors.TryGetValue(SegmentType, Colour) Then
            Return Colour
        End If

        Return Color.Transparent

    End Function

End Module

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
