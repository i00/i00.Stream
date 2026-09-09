Public Class Drawing

    Public Shared Function BlendColor(ByVal FromColor As Color, ByVal ToColor As Color, Optional ByVal alpha As Integer = 127) As Color
        BlendColor = Color.FromArgb(CInt(((FromColor.A * (255 - alpha)) / 255) + ((ToColor.A * alpha) / 255)),
                                    CInt(((FromColor.R * (255 - alpha)) / 255) + ((ToColor.R * alpha) / 255)),
                                    CInt(((FromColor.G * (255 - alpha)) / 255) + ((ToColor.G * alpha) / 255)),
                                    CInt(((FromColor.B * (255 - alpha)) / 255) + ((ToColor.B * alpha) / 255)))
    End Function

    'Sets the alpha channel of a color by a certain amount (0 to 255)
    Public Shared Function AlphaColor(ByVal theColor As Color, Optional ByVal AlphaLevel As Byte = 255) As Color
        AlphaColor = Color.FromArgb(CByte((theColor.A / 255) * AlphaLevel), theColor.R, theColor.G, theColor.B)
    End Function

    Public Shared Function DrawBall(ByVal Color As Color, size As Integer) As Bitmap

        Using b As New Bitmap(size * 2, size * 2) '<< - *2 = nicer anti aliasing :)
            Using GImage As Graphics = Graphics.FromImage(b)
                GImage.SmoothingMode = Drawing2D.SmoothingMode.HighQuality
                Using GradPath As New System.Drawing.Drawing2D.GraphicsPath
                    GradPath.AddEllipse(0, 0, b.Width - 1, b.Height - 1)
                    Using pgb As New System.Drawing.Drawing2D.PathGradientBrush(GradPath)
                        pgb.CenterColor = Color
                        pgb.CenterPoint = New Point(CInt(b.Width * 0.75), CInt(b.Height * 0.25))
                        Dim arrColors() As Color = {BlendColor(pgb.CenterColor, Color.Black, 127)}
                        pgb.SurroundColors = arrColors
                        GImage.FillEllipse(pgb, pgb.Rectangle)
                    End Using
                End Using
            End Using
            DrawBall = New Bitmap(size, size)
            Using GImage As Graphics = Graphics.FromImage(DrawBall)
                GImage.InterpolationMode = Drawing2D.InterpolationMode.High
                GImage.DrawImage(b, New Rectangle(0, 0, DrawBall.Width - 1, DrawBall.Height - 1))
            End Using
        End Using
    End Function

    Public Shared Function CreateMetafile(Optional Bounds As SizeF? = Nothing, Optional EmfType As Imaging.EmfType = Imaging.EmfType.EmfPlusOnly) As System.Drawing.Imaging.Metafile
        Using offScreenBufferGraphics = Graphics.FromHwndInternal(IntPtr.Zero)
            Try
                Dim deviceContextHandle As IntPtr = offScreenBufferGraphics.GetHdc()

                Using stream As New IO.MemoryStream

                    CreateMetafile = If(Bounds.HasValue = False,
                                        New System.Drawing.Imaging.Metafile(stream, deviceContextHandle, EmfType),
                                        New System.Drawing.Imaging.Metafile(stream, deviceContextHandle, New RectangleF(0, 0, Bounds.Value.Width, Bounds.Value.Height), Imaging.MetafileFrameUnit.Pixel, EmfType))

                End Using
            Finally
                offScreenBufferGraphics.ReleaseHdc()
            End Try
        End Using
    End Function

End Class
