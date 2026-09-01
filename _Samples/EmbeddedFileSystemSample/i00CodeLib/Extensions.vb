Imports System.Runtime.InteropServices

Friend Module Extensions

    <System.Runtime.CompilerServices.Extension>
    Public Function getThreadAbortException(ex As Exception) As System.Threading.ThreadAbortException
        Return ex.Recurse(Function(x) {x.InnerException}, True).OfType(Of System.Threading.ThreadAbortException).FirstOrDefault()
    End Function

    'can be used to return a single set of elements where a recursive function would normally be required
    '... eg:
    'Dim AllNestedControls =  DirectCast(Form, Control).Recurse(Function(x) x.Controls?.OfType(Of Control))
    <System.Runtime.CompilerServices.Extension>
    Public Function Recurse(Of T)(BaseObject As T, RecurseFunction As Func(Of T, IEnumerable(Of T)), Optional IncludeBaseObject As Boolean = False, Optional AllowDuplicates As Boolean = True) As IEnumerable(Of T)
        Return RecurseInternal(BaseObject, RecurseFunction, IncludeBaseObject, If(AllowDuplicates, Nothing, New HashSet(Of T)))
    End Function

    Private Iterator Function RecurseInternal(Of T)(BaseObject As T, RecurseFunction As Func(Of T, IEnumerable(Of T)), Optional IncludeBaseObject As Boolean = False, Optional ReturnedItems As HashSet(Of T) = Nothing) As IEnumerable(Of T)
        If IncludeBaseObject Then
            Yield BaseObject
            ReturnedItems?.Add(BaseObject)
        End If
        Dim NewItems = RecurseFunction.Invoke(BaseObject)
        If NewItems Is Nothing Then
        Else
            For Each item In NewItems
                If item IsNot Nothing Then
                    If (ReturnedItems?.Contains(item)).GetValueOrDefault(False) = False Then
                        Yield item
                        ReturnedItems?.Add(item)
                        For Each item2 In RecurseInternal(item, RecurseFunction, , ReturnedItems)
                            If (ReturnedItems?.Contains(item2)).GetValueOrDefault(False) = False Then
                                Yield item2
                                ReturnedItems?.Add(item2)
                            End If
                        Next
                    End If
                End If
            Next
        End If
    End Function


    <System.Runtime.InteropServices.DllImport("user32")>
    Private Function GetSystemMenu(hWnd As IntPtr, bRevert As Boolean) As IntPtr
    End Function

    <System.Runtime.InteropServices.DllImport("user32")>
    Private Function EnableMenuItem(hMenu As IntPtr, itemId As UInteger, uEnable As UInteger) As Boolean
    End Function

    <System.Runtime.CompilerServices.Extension>
    Public Sub DisableCloseButton(form As Form)
        ' The 1 parameter means to grey out. 0xF060 is SC_CLOSE.
        EnableMenuItem(GetSystemMenu(form.Handle, False), &HF060, 1)
    End Sub

    <System.Runtime.CompilerServices.Extension>
    Public Sub EnableCloseButton(form As Form)
        ' The zero parameter means to enable. 0xF060 is SC_CLOSE.
        EnableMenuItem(GetSystemMenu(form.Handle, False), &HF060, 0)
    End Sub

    <Runtime.CompilerServices.Extension()>
    Public Sub DelayInvoke(method As Action)
        Dim ao = System.ComponentModel.AsyncOperationManager.CreateOperation(Nothing)
        ao.Post(Sub(state) method.Invoke, Nothing)
    End Sub

    Private ReadOnly FormatFileSizeLimits As Long() = New Long() {1099511627776, 1073741824, 1048576, 1024}
    Private ReadOnly FormatFileSizeUnits As String() = New String() {"TB", "GB", "MB", "KB"}
    <System.Runtime.CompilerServices.Extension>
    Friend Function FormatFileSizeFromBytes(size As Long, Optional DecimalPlaces As Integer = 1) As String
        For Index As Integer = 0 To FormatFileSizeLimits.Length - 1
            If size >= FormatFileSizeLimits(Index) Then
                Return String.Format(
                    "{0:#,##0." & New String("#"c, DecimalPlaces) & "} " & FormatFileSizeUnits(Index),
                    size / CDbl(FormatFileSizeLimits(Index)))
            End If
        Next

        Return $"{size} B"
    End Function

    <System.Runtime.CompilerServices.Extension>
    Public Function InvokeIfRequired(Of T)(Control As Control, Action As Func(Of T)) As T
        If Control.InvokeRequired Then
            Return DirectCast(Control.Invoke(Action), T)
        Else
            Return Action.Invoke()
        End If
    End Function

    <System.Runtime.CompilerServices.Extension>
    Public Sub InvokeIfRequired(Control As Control, Action As Action)
        If Control.InvokeRequired Then
            Control.Invoke(Action)
        Else
            Action.Invoke()
        End If
    End Sub

    <Runtime.CompilerServices.Extension()>
    Public Function Middle(rect As RectangleF) As PointF
        Return New PointF(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2))
    End Function

    <Runtime.CompilerServices.Extension()>
    Public Function SetMiddle(rect As RectangleF, Point As PointF) As RectangleF
        Return New RectangleF(Point.X - (rect.Width / 2), Point.Y - (rect.Height / 2), rect.Width, rect.Height)
    End Function

    <Runtime.CompilerServices.Extension()>
    Public Function MidPoint(rect As Rectangle) As Point
        Return New Point(rect.X + (rect.Width \ 2), rect.Y + (rect.Height \ 2))
    End Function

    <Serializable>
    <System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>
    Private Structure RECT
        Public Left As Integer
        Public Top As Integer
        Public Right As Integer
        Public Bottom As Integer

        Public Function ToRectangle() As Rectangle
            Return Rectangle.FromLTRB(Left, Top, Right, Bottom)
        End Function
    End Structure

    Private Function GetWindowRect(handle As IntPtr) As Rectangle
        Dim rect As RECT
        GetWindowRect(handle, rect)
        Return rect.ToRectangle()
    End Function

    <DllImport("dwmapi.dll")>
    Private Function DwmGetWindowAttribute(hwnd As IntPtr, dwAttribute As Integer, ByRef pvAttribute As RECT, cbAttribute As Integer) As Integer
    End Function

    Private Enum Dwmwindowattribute
        DwmwaExtendedFrameBounds = 9
    End Enum

    Private Function DWMWA_EXTENDED_FRAME_BOUNDS(handle As IntPtr, ByRef rectangle As Rectangle) As Boolean
        Dim rect As RECT
        Dim result = DwmGetWindowAttribute(handle, CInt(Dwmwindowattribute.DwmwaExtendedFrameBounds), rect, Marshal.SizeOf(GetType(RECT)))
        rectangle = rect.ToRectangle()
        Return result >= 0
    End Function

    <System.Runtime.CompilerServices.Extension()>
    Friend Function GetWindowRectangle(handle As IntPtr) As Rectangle
        If Environment.OSVersion.Version.Major < 6 Then
            Return GetWindowRect(handle)
        Else
            Dim rectangle As Rectangle
            Return If(DWMWA_EXTENDED_FRAME_BOUNDS(handle, rectangle), rectangle, GetWindowRect(handle))
        End If
    End Function

    <DllImport("user32.dll")>
    Private Function GetWindowRect(hWnd As IntPtr, ByRef lpRect As RECT) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

End Module