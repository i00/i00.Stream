Public Class Drawing

    Public Shared Function BlendColor(ByVal FromColor As Color, ByVal ToColor As Color, Optional ByVal alpha As Integer = 127) As Color
        BlendColor = Color.FromArgb(CInt(((FromColor.A * (255 - alpha)) / 255) + ((ToColor.A * alpha) / 255)),
                                    CInt(((FromColor.R * (255 - alpha)) / 255) + ((ToColor.R * alpha) / 255)),
                                    CInt(((FromColor.G * (255 - alpha)) / 255) + ((ToColor.G * alpha) / 255)),
                                    CInt(((FromColor.B * (255 - alpha)) / 255) + ((ToColor.B * alpha) / 255)))
    End Function

End Class
