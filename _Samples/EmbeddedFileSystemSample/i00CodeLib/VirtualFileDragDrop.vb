Imports System.Diagnostics.CodeAnalysis
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Runtime.InteropServices.ComTypes

'DOWNLOADED FROM http://blogs.msdn.com/b/delay/archive/2009/11/04/creating-something-from-nothing-asynchronously-developer-friendly-virtual-file-implementation-for-net-improved.aspx
'AND MODIFIED BY i00

Namespace VirtualDragCopyFiles


    ''' <summary>
    ''' A COM <see cref="System.Runtime.InteropServices.ComTypes.IDataObject"/> shim: the shell negotiates
    ''' formats by calling members that signal "not supported" by throwing an HRESULT exception
    ''' (<c>DV_E_FORMATETC</c> and friends). That is by design and is caught at the COM boundary, so the
    ''' whole class is marked non-user code to stop the debugger first-chance-breaking on it - it only
    ''' looked like a crash under F5; Ctrl+F5 was always fine.
    ''' </summary>
    <System.Diagnostics.DebuggerNonUserCode>
    Public NotInheritable Class VirtualFileDataObject
        Implements IDataObject
        Implements IAsyncOperation


#Region "i00 EASE OF USE"

        Public Enum Action
            Copy
            Drag
        End Enum

        Public Shared Sub Start(Action As Action, Files As IEnumerable(Of FileDescriptor), Optional OnStart As Action(Of VirtualFileDataObject) = Nothing, Optional OnEnd As Action(Of VirtualFileDataObject) = Nothing)
            If Debugger.IsAttached Then
                Try
                    Throw New NotSupportedException("Virtual File drag / copying is not supported in the debugger")
                Catch ex As Exception
                    MsgBox(Nothing, "Virtual File drag / copying is not supported in the debugger", MsgBoxStyle.OkOnly Or MsgBoxStyle.Critical)
                End Try
            Else
                Dim DefaultStartEndSub = Sub(x As VirtualFileDataObject)
                                             'do nothing ... needs this to be async??
                                         End Sub
                If OnStart Is Nothing Then OnStart = DefaultStartEndSub
                If OnEnd Is Nothing Then OnEnd = DefaultStartEndSub

                Dim VirtualFileDataObject = New VirtualFileDataObject(OnStart, OnEnd)

                VirtualFileDataObject.SetData(Files)

                If Action = VirtualDragCopyFiles.VirtualFileDataObject.Action.Copy Then
                    VirtualFileDataObject.PreferredDropEffect = DragDropEffects.Copy
                    Clipboard.SetDataObject(VirtualFileDataObject)
                Else
                    VirtualFileDataObject.DoDragDrop(VirtualFileDataObject, DragDropEffects.Copy)
                End If

            End If

        End Sub


#End Region


        Public Property IsAsynchronous() As Boolean
            Get
                Return m_IsAsynchronous
            End Get
            Set(value As Boolean)
                m_IsAsynchronous = value
            End Set
        End Property
        Private m_IsAsynchronous As Boolean

        Private Shared Function ToShort(Number As Integer) As Short
            Return BitConverter.ToInt16(BitConverter.GetBytes(Number), 0)
        End Function

        Private Declare Auto Function RegisterClipboardFormat Lib "user32.dll" (<MarshalAs(UnmanagedType.LPTStr)> lpString As String) As UInteger

        Public Shared Function RegisterClipboardDataFormat(FormatName As String) As Short
            Return ToShort(CInt(RegisterClipboardFormat(FormatName)))
        End Function

        ' Registered clipboard-format ids are in the 0xC000-0xFFFF range, so CShort() on them overflows.
        ' ToShort() reinterprets the low 16 bits (the C# "(short)(ushort)x" idiom) - feed it the raw value.
        Private Shared FILECONTENTS As Short = ToShort(CInt(RegisterClipboardFormat(NativeMethods.CFSTR_FILECONTENTS)))

        Private Shared FILEDESCRIPTORW As Short = ToShort(CInt(RegisterClipboardFormat(NativeMethods.CFSTR_FILEDESCRIPTORW)))

        Private Shared m_PASTESUCCEEDED As Short = ToShort(CInt(RegisterClipboardFormat(NativeMethods.CFSTR_PASTESUCCEEDED)))

        Private Shared m_PERFORMEDDROPEFFECT As Short = ToShort(CInt(RegisterClipboardFormat(NativeMethods.CFSTR_PERFORMEDDROPEFFECT)))

        Private Shared m_PREFERREDDROPEFFECT As Short = ToShort(CInt(RegisterClipboardFormat(NativeMethods.CFSTR_PREFERREDDROPEFFECT)))

        Private _dataObjects As New List(Of DataObject)

        Private _inOperation As Boolean

        Private _startAction As Action(Of VirtualFileDataObject)

        Private _endAction As Action(Of VirtualFileDataObject)

        Public Sub New()
            IsAsynchronous = True
        End Sub

        Public Sub New(startAction As Action(Of VirtualFileDataObject), endAction As Action(Of VirtualFileDataObject))
            Me.New()
            Me._startAction = startAction
            Me._endAction = endAction
        End Sub

#Region "IDataObject Members"
        ' Explicit interface implementation hides the technical details from users of VirtualFileDataObject.

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Function System_Runtime_InteropServices_ComTypes_IDataObject_DAdvise(ByRef pFormatetc As FORMATETC, advf As ADVF, adviseSink As IAdviseSink, ByRef connection As Integer) As Integer Implements System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise
            Marshal.ThrowExceptionForHR(NativeMethods.OLE_E_ADVISENOTSUPPORTED)
            Throw New NotImplementedException()
        End Function

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Sub System_Runtime_InteropServices_ComTypes_IDataObject_DUnadvise(connection As Integer) Implements System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise
            Marshal.ThrowExceptionForHR(NativeMethods.OLE_E_ADVISENOTSUPPORTED)
            Throw New NotImplementedException()
        End Sub

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Function System_Runtime_InteropServices_ComTypes_IDataObject_EnumDAdvise(ByRef enumAdvise As IEnumSTATDATA) As Integer Implements System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise
            Marshal.ThrowExceptionForHR(NativeMethods.OLE_E_ADVISENOTSUPPORTED)
            Throw New NotImplementedException()
        End Function

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Function System_Runtime_InteropServices_ComTypes_IDataObject_EnumFormatEtc(direction As DATADIR) As IEnumFORMATETC Implements System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc
            If direction = DATADIR.DATADIR_GET Then
                If 0 = _dataObjects.Count Then
                    ' Note: SHCreateStdEnumFmtEtc fails for a count of 0; throw helpful exception
                    Throw New InvalidOperationException("VirtualFileDataObject requires at least one data object to enumerate.")
                End If

                ' Create enumerator and return it
                Dim enumerator As IEnumFORMATETC = Nothing

                If NativeMethods.SUCCEEDED(NativeMethods.SHCreateStdEnumFmtEtc(CUInt(_dataObjects.Count), _dataObjects.[Select](Function(d) d.FORMATETC).ToArray(), enumerator)) Then
                    Return enumerator
                End If

                ' Returning null here can cause an AV in the caller; throw instead
                Marshal.ThrowExceptionForHR(NativeMethods.E_FAIL)
            End If
            Throw New NotImplementedException()
        End Function

        ''' <summary>
        ''' Provides a standard FORMATETC structure that is logically equivalent to a more complex structure.
        ''' </summary>
        ''' <param name="formatIn">A pointer to a FORMATETC structure that defines the format, medium, and target device that the caller would like to use to retrieve data in a subsequent call such as GetData.</param>
        ''' <param name="formatOut">When this method returns, contains a pointer to a FORMATETC structure that contains the most general information possible for a specific rendering, making it canonically equivalent to formatetIn.</param>
        ''' <returns>HRESULT success code.</returns>
        Private Function System_Runtime_InteropServices_ComTypes_IDataObject_GetCanonicalFormatEtc(ByRef formatIn As FORMATETC, ByRef formatOut As FORMATETC) As Integer Implements System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc
            Throw New NotImplementedException()
        End Function

        ''' <summary>
        ''' Obtains data from a source data object.
        ''' </summary>
        ''' <param name="format">A pointer to a FORMATETC structure that defines the format, medium, and target device to use when passing the data.</param>
        ''' <param name="medium">When this method returns, contains a pointer to the STGMEDIUM structure that indicates the storage medium containing the returned data through its tymed member, and the responsibility for releasing the medium through the value of its pUnkForRelease member.</param>
        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Sub System_Runtime_InteropServices_ComTypes_IDataObject_GetData(ByRef format As FORMATETC, ByRef medium As STGMEDIUM) Implements System.Runtime.InteropServices.ComTypes.IDataObject.GetData
            medium = New STGMEDIUM()
            Dim hr = DirectCast(Me, System.Runtime.InteropServices.ComTypes.IDataObject).QueryGetData(format)
            If NativeMethods.SUCCEEDED(hr) Then
                ' Find the best match
                Dim formatCopy = format
                ' Cannot use ref or out parameter inside an anonymous method, lambda expression, or query expression
                Dim dataObject = _dataObjects.FirstOrDefault(Function(d) (d.FORMATETC.cfFormat = formatCopy.cfFormat) AndAlso (d.FORMATETC.dwAspect = formatCopy.dwAspect) AndAlso (0 <> (d.FORMATETC.tymed And formatCopy.tymed) AndAlso (d.FORMATETC.lindex = formatCopy.lindex)))
                If dataObject IsNot Nothing Then
                    If Not IsAsynchronous AndAlso (FILEDESCRIPTORW = dataObject.FORMATETC.cfFormat) AndAlso Not _inOperation Then
                        ' Enter the operation and call the start action
                        _inOperation = True
                        _startAction.Invoke(Me)
                    End If

                    ' Populate the STGMEDIUM
                    medium.tymed = dataObject.FORMATETC.tymed
                    Dim result = dataObject.GetData().Invoke
                    ' Possible call to user code
                    hr = result.Item2
                    If NativeMethods.SUCCEEDED(hr) Then
                        medium.unionmember = result.Item1
                    End If
                Else
                    ' Couldn't find a match
                    hr = NativeMethods.DV_E_FORMATETC
                End If
            End If
            If Not NativeMethods.SUCCEEDED(hr) Then
                ' Not redundant; hr gets updated in the block above
                Marshal.ThrowExceptionForHR(hr)
            End If
        End Sub

        Private Sub System_Runtime_InteropServices_ComTypes_IDataObject_GetDataHere(ByRef format As FORMATETC, ByRef medium As STGMEDIUM) Implements System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere
            Throw New NotImplementedException()
        End Sub

        Private Function System_Runtime_InteropServices_ComTypes_IDataObject_QueryGetData(ByRef format As FORMATETC) As Integer Implements System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData
            Dim formatCopy = format
            ' Cannot use ref or out parameter inside an anonymous method, lambda expression, or query expression
            Dim formatMatches = _dataObjects.Where(Function(d) d.FORMATETC.cfFormat = formatCopy.cfFormat)
            If Not formatMatches.Any() Then
                Return NativeMethods.DV_E_FORMATETC
            End If
            Dim tymedMatches = formatMatches.Where(Function(d) 0 <> (d.FORMATETC.tymed And formatCopy.tymed))
            If Not tymedMatches.Any() Then
                Return NativeMethods.DV_E_TYMED
            End If
            Dim aspectMatches = tymedMatches.Where(Function(d) d.FORMATETC.dwAspect = formatCopy.dwAspect)
            If Not aspectMatches.Any() Then
                Return NativeMethods.DV_E_DVASPECT
            End If
            Return NativeMethods.S_OK
        End Function

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Sub System_Runtime_InteropServices_ComTypes_IDataObject_SetData(ByRef formatIn As FORMATETC, ByRef medium As STGMEDIUM, release As Boolean) Implements System.Runtime.InteropServices.ComTypes.IDataObject.SetData
            Dim handled = False
            If (formatIn.dwAspect = DVASPECT.DVASPECT_CONTENT) AndAlso (formatIn.tymed = TYMED.TYMED_HGLOBAL) AndAlso (medium.tymed = formatIn.tymed) Then
                ' Supported format; capture the data
                Dim ptr = NativeMethods.GlobalLock(medium.unionmember)
                If IntPtr.Zero <> ptr Then
                    Try
                        Dim length = NativeMethods.GlobalSize(ptr).ToInt32()
                        Dim data = New Byte(length - 1) {}
                        Marshal.Copy(ptr, data, 0, length)
                        ' Store it in our own format
                        SetData(formatIn.cfFormat, data)
                        handled = True
                    Finally
                        NativeMethods.GlobalUnlock(medium.unionmember)
                    End Try
                End If

                ' Release memory if we now own it
                If release Then
                    Marshal.FreeHGlobal(medium.unionmember)
                End If
            End If

            ' Handle synchronous mode
            If Not IsAsynchronous AndAlso (m_PERFORMEDDROPEFFECT = formatIn.cfFormat) AndAlso _inOperation Then
                ' Call the end action and exit the operation
                _endAction.Invoke(Me)
                _inOperation = False
            End If

            ' Throw if unhandled
            If Not handled Then
                Throw New NotImplementedException()
            End If
        End Sub

#End Region

        Public Sub SetData(dataFormat As Short, data As IEnumerable(Of Byte))
            _dataObjects.Add(New DataObject() With {
                 .FORMATETC = New FORMATETC() With {
                     .cfFormat = dataFormat,
                     .ptd = IntPtr.Zero,
                     .dwAspect = DVASPECT.DVASPECT_CONTENT,
                     .lindex = -1,
                     .tymed = TYMED.TYMED_HGLOBAL
                },
                 .GetData = Function()
                                Dim dataArray = data.ToArray()
                                Dim ptr = Marshal.AllocHGlobal(dataArray.Length)
                                Marshal.Copy(dataArray, 0, ptr, dataArray.Length)
                                Return New Tuple(Of IntPtr, Integer)(ptr, NativeMethods.S_OK)

                            End Function
            })
        End Sub

        Public Sub SetData(dataFormat As Short, index As Integer, streamData As Action(Of Stream))
            _dataObjects.Add(New DataObject() With {
                 .FORMATETC = New FORMATETC() With {
                     .cfFormat = dataFormat,
                     .ptd = IntPtr.Zero,
                     .dwAspect = DVASPECT.DVASPECT_CONTENT,
                     .lindex = index,
                     .tymed = TYMED.TYMED_ISTREAM
                },
                 .GetData = Function()
                                ' Create IStream for data
                                Dim ptr = IntPtr.Zero
                                Dim iStream = NativeMethods.CreateStreamOnHGlobal(IntPtr.Zero, True)
                                If streamData IsNot Nothing Then
                                    ' Wrap in a .NET-friendly Stream and call provided code to fill it
                                    Using stream = New IStreamWrapper(iStream)
                                        streamData(stream)
                                    End Using
                                End If
                                ' Return an IntPtr for the IStream
                                ptr = Marshal.GetComInterfaceForObject(iStream, GetType(IStream))
                                Marshal.ReleaseComObject(iStream)
                                Return New Tuple(Of IntPtr, Integer)(ptr, NativeMethods.S_OK)

                            End Function
            })
        End Sub

        Private Shared Function ToInt(Number As Long) As Integer
            Return BitConverter.ToInt32(BitConverter.GetBytes(Number), 0)
        End Function

        Private Shared Function ToUInt(Number As Long) As UInteger
            Return BitConverter.ToUInt32(BitConverter.GetBytes(Number), 0)
        End Function

        Public Sub SetData(fileDescriptors As IEnumerable(Of FileDescriptor))
            ' Prepare buffer
            Dim bytes = New List(Of Byte)
            ' Add FILEGROUPDESCRIPTOR header
            bytes.AddRange(StructureBytes(New NativeMethods.FILEGROUPDESCRIPTOR() With {
                 .cItems = CUInt(fileDescriptors.Count())
            }))
            ' Add n FILEDESCRIPTORs
            For Each fileDescriptor__1 In fileDescriptors
                ' Set required fields
                Dim FILEDESCRIPTOR__2 = New NativeMethods.FILEDESCRIPTOR() With {
                     .cFileName = fileDescriptor__1.Name,
                     .dwFlags = NativeMethods.FD_ATTRIBUTES Or NativeMethods.FD_SHOWPROGRESSUI
                }
                ' Set optional timestamp
                If fileDescriptor__1.ChangeTimeUtc.HasValue Then
                    FILEDESCRIPTOR__2.dwFlags = FILEDESCRIPTOR__2.dwFlags Or NativeMethods.FD_CREATETIME Or NativeMethods.FD_WRITESTIME
                    Dim changeTime = fileDescriptor__1.ChangeTimeUtc.Value.ToLocalTime().ToFileTime()
                    Dim changeTimeFileTime = New System.Runtime.InteropServices.ComTypes.FILETIME() With {
                         .dwLowDateTime = ToInt(changeTime And &HFFFFFFFFUI),
                         .dwHighDateTime = ToInt(changeTime >> 32)
                    }
                    FILEDESCRIPTOR__2.ftLastWriteTime = changeTimeFileTime
                    FILEDESCRIPTOR__2.ftCreationTime = changeTimeFileTime
                End If
                ' Set optional length
                If fileDescriptor__1.Length.HasValue Then
                    FILEDESCRIPTOR__2.dwFlags = FILEDESCRIPTOR__2.dwFlags Or NativeMethods.FD_FILESIZE
                    FILEDESCRIPTOR__2.nFileSizeLow = ToUInt(fileDescriptor__1.Length.Value And &HFFFFFFFFUI)
                    FILEDESCRIPTOR__2.nFileSizeHigh = ToUInt(fileDescriptor__1.Length.Value >> 32)
                End If
                ' Add structure to buffer
                bytes.AddRange(StructureBytes(FILEDESCRIPTOR__2))
            Next

            ' Set CFSTR_FILEDESCRIPTORW
            SetData(FILEDESCRIPTORW, bytes)
            ' Set n CFSTR_FILECONTENTS
            Dim index = 0
            For Each fileDescriptor__1 In fileDescriptors
                SetData(FILECONTENTS, index, fileDescriptor__1.StreamContents)
                index += 1
            Next
        End Sub

        Public Property PasteSucceeded() As System.Nullable(Of DragDropEffects)
            Get
                Return GetDropEffect(m_PASTESUCCEEDED)
            End Get
            Set(value As System.Nullable(Of DragDropEffects))
                SetData(m_PASTESUCCEEDED, BitConverter.GetBytes(CUInt(value.Value)))
            End Set
        End Property

        Public Property PerformedDropEffect() As System.Nullable(Of DragDropEffects)
            Get
                Return GetDropEffect(m_PERFORMEDDROPEFFECT)
            End Get
            Set(value As System.Nullable(Of DragDropEffects))
                SetData(m_PERFORMEDDROPEFFECT, BitConverter.GetBytes(CUInt(value)))
            End Set
        End Property

        Public Property PreferredDropEffect() As System.Nullable(Of DragDropEffects)
            Get
                Return GetDropEffect(m_PREFERREDDROPEFFECT)
            End Get
            Set(value As System.Nullable(Of DragDropEffects))
                SetData(m_PREFERREDDROPEFFECT, BitConverter.GetBytes(CUInt(value)))
            End Set
        End Property

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Function GetDropEffect(format As Short) As System.Nullable(Of DragDropEffects)
            ' Get the most recent setting
            Dim dataObject = _dataObjects.Where(Function(d) (format = d.FORMATETC.cfFormat) AndAlso (DVASPECT.DVASPECT_CONTENT = d.FORMATETC.dwAspect) AndAlso (TYMED.TYMED_HGLOBAL = d.FORMATETC.tymed)).LastOrDefault()
            If dataObject IsNot Nothing Then
                ' Read the value and return it
                Dim result = dataObject.GetData().Invoke
                If NativeMethods.SUCCEEDED(result.Item2) Then
                    Dim ptr = NativeMethods.GlobalLock(result.Item1)
                    If IntPtr.Zero <> ptr Then
                        Try
                            Dim length = NativeMethods.GlobalSize(ptr).ToInt32()
                            If 4 = length Then
                                Dim data = New Byte(length - 1) {}
                                Marshal.Copy(ptr, data, 0, length)
                                Return DirectCast(CInt(BitConverter.ToUInt32(data, 0)), DragDropEffects)
                            End If
                        Finally
                            NativeMethods.GlobalUnlock(result.Item1)
                        End Try
                    End If
                End If
            End If
            Return Nothing
        End Function

#Region "IAsyncOperation Members"
        ' Explicit interface implementation hides the technical details from users of VirtualFileDataObject.

        Private Sub IAsyncOperation_SetAsyncMode(fDoOpAsync As Integer) Implements IAsyncOperation.SetAsyncMode
            IsAsynchronous = Not (NativeMethods.VARIANT_FALSE = fDoOpAsync)
        End Sub

        Private Sub IAsyncOperation_GetAsyncMode(ByRef pfIsOpAsync As Integer) Implements IAsyncOperation.GetAsyncMode
            pfIsOpAsync = If(IsAsynchronous, NativeMethods.VARIANT_TRUE, NativeMethods.VARIANT_FALSE)
        End Sub

        Private Sub IAsyncOperation_StartOperation(pbcReserved As IBindCtx) Implements IAsyncOperation.StartOperation
            _inOperation = True
            _startAction.Invoke(Me)
        End Sub

        Private Sub IAsyncOperation_InOperation(ByRef pfInAsyncOp As Integer) Implements IAsyncOperation.InOperation
            pfInAsyncOp = If(_inOperation, NativeMethods.VARIANT_TRUE, NativeMethods.VARIANT_FALSE)
        End Sub

        Private Sub IAsyncOperation_EndOperation(hResult As Integer, pbcReserved As IBindCtx, dwEffects As UInteger) Implements IAsyncOperation.EndOperation
            _endAction.Invoke(Me)
            _inOperation = False
        End Sub

#End Region

        <SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification:="Method doesn't decrease security.")>
        Private Shared Function StructureBytes(source As Object) As IEnumerable(Of Byte)
            ' Set up for call to StructureToPtr
            Dim size = Marshal.SizeOf(source.[GetType]())
            Dim ptr = Marshal.AllocHGlobal(size)
            Dim bytes = New Byte(size - 1) {}
            Try
                Marshal.StructureToPtr(source, ptr, False)
                ' Copy marshalled bytes to buffer
                Marshal.Copy(ptr, bytes, 0, size)
            Finally
                Marshal.FreeHGlobal(ptr)
            End Try
            Return bytes
        End Function

        <SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible", Justification:="Deliberate to provide obvious coupling.")>
        Public Class FileDescriptor
            Public Property Name() As String
                Get
                    Return m_Name
                End Get
                Set(value As String)
                    m_Name = value
                End Set
            End Property
            Private m_Name As String

            Public Property Length() As System.Nullable(Of Int64)
                Get
                    Return m_Length
                End Get
                Set(value As System.Nullable(Of Int64))
                    m_Length = value
                End Set
            End Property
            Private m_Length As System.Nullable(Of Int64)

            Public Property ChangeTimeUtc() As System.Nullable(Of DateTime)
                Get
                    Return m_ChangeTimeUtc
                End Get
                Set(value As System.Nullable(Of DateTime))
                    m_ChangeTimeUtc = value
                End Set
            End Property
            Private m_ChangeTimeUtc As System.Nullable(Of DateTime)

            Public Property StreamContents() As Action(Of Stream)
                Get
                    Return m_StreamContents
                End Get
                Set(value As Action(Of Stream))
                    m_StreamContents = value
                End Set
            End Property
            Private m_StreamContents As Action(Of Stream)
        End Class

        Private Class DataObject
            Public Property FORMATETC() As FORMATETC
                Get
                    Return m_FORMATETC
                End Get
                Set(value As FORMATETC)
                    m_FORMATETC = value
                End Set
            End Property
            Private m_FORMATETC As FORMATETC

            Public Property GetData() As Func(Of Tuple(Of IntPtr, Integer))
                Get
                    Return m_GetData
                End Get
                Set(value As Func(Of Tuple(Of IntPtr, Integer)))
                    m_GetData = value
                End Set
            End Property
            Private m_GetData As Func(Of Tuple(Of IntPtr, Integer))
        End Class

        Private Class Tuple(Of T1, T2)
            Public Property Item1() As T1
                Get
                    Return m_Item1
                End Get
                Private Set(value As T1)
                    m_Item1 = value
                End Set
            End Property
            Private m_Item1 As T1

            Public Property Item2() As T2
                Get
                    Return m_Item2
                End Get
                Private Set(value As T2)
                    m_Item2 = value
                End Set
            End Property
            Private m_Item2 As T2

            Public Sub New(item1__1 As T1, item2__2 As T2)
                Item1 = item1__1
                Item2 = item2__2
            End Sub
        End Class

        Private Class IStreamWrapper
            Inherits Stream
            Private _iStream As IStream

            Public Sub New(iStream As IStream)
                _iStream = iStream
            End Sub

            Public Overrides ReadOnly Property CanRead() As Boolean
                Get
                    Return False
                End Get
            End Property

            Public Overrides ReadOnly Property CanSeek() As Boolean
                Get
                    Return False
                End Get
            End Property

            Public Overrides ReadOnly Property CanWrite() As Boolean
                Get
                    Return True
                End Get
            End Property

            Public Overrides Sub Flush()
                Throw New NotImplementedException()
            End Sub

            Public Overrides ReadOnly Property Length() As Long
                Get
                    Throw New NotImplementedException()
                End Get
            End Property

            Public Overrides Property Position() As Long
                Get
                    Throw New NotImplementedException()
                End Get
                Set(value As Long)
                    Throw New NotImplementedException()
                End Set
            End Property

            Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
                Throw New NotImplementedException()
            End Function

            Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
                Throw New NotImplementedException()
            End Function

            Public Overrides Sub SetLength(value As Long)
                Throw New NotImplementedException()
            End Sub

            Public Overrides Sub Write(buffer As Byte(), offset As Integer, count As Integer)
                If offset = 0 Then
                    ' Optimize common case to avoid creating extra buffers
                    _iStream.Write(buffer, count, IntPtr.Zero)
                Else
                    ' Easy way to provide the relevant byte[]
                    _iStream.Write(buffer.Skip(offset).ToArray(), count, IntPtr.Zero)
                End If
            End Sub
        End Class

        <SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId:="dragSource", Justification:="Parameter is present so the signature matches that of System.Windows.DragDrop.DoDragDrop.")>
        Public Shared Function DoDragDrop(dataObject As System.Runtime.InteropServices.ComTypes.IDataObject, allowedEffects As DragDropEffects) As DragDropEffects
            Dim finalEffect As Integer() = New Integer(0) {}
            Try
                NativeMethods.DoDragDrop(dataObject, New DropSource(), CInt(allowedEffects), finalEffect)
            Finally
                Dim virtualFileDataObject = TryCast(dataObject, VirtualFileDataObject)
                If (virtualFileDataObject IsNot Nothing) AndAlso Not virtualFileDataObject.IsAsynchronous AndAlso virtualFileDataObject._inOperation Then
                    ' Call the end action and exit the operation
                    If virtualFileDataObject._endAction IsNot Nothing Then
                        virtualFileDataObject._endAction(virtualFileDataObject)
                    End If
                    virtualFileDataObject._inOperation = False
                End If
            End Try
            Return DirectCast(finalEffect(0), DragDropEffects)
        End Function

        <Flags>
        Public Enum DragDropKeyStates
            None = 0
            LeftMouseButton = 1 << 0
            RightMouseButton = 1 << 1
            ShiftKey = 1 << 2
            ControlKey = 1 << 3
            MiddleMouseButton = 1 << 4
            AltKey = 1 << 5
        End Enum

        Private Class DropSource
            Implements NativeMethods.IDropSource
            Public Function QueryContinueDrag(fEscapePressed As Integer, grfKeyState As UInteger) As Integer Implements NativeMethods.IDropSource.QueryContinueDrag
                Dim escapePressed = (0 <> fEscapePressed)
                Dim keyStates = DirectCast(CInt(grfKeyState), DragDropKeyStates)
                If escapePressed Then
                    Return NativeMethods.DRAGDROP_S_CANCEL
                ElseIf DragDropKeyStates.None = (keyStates And DragDropKeyStates.LeftMouseButton) Then
                    Return NativeMethods.DRAGDROP_S_DROP
                End If
                Return NativeMethods.S_OK
            End Function

            Public Function GiveFeedback(dwEffect As UInteger) As Integer Implements NativeMethods.IDropSource.GiveFeedback
                Return NativeMethods.DRAGDROP_S_USEDEFAULTCURSORS
            End Function
        End Class

        Private NotInheritable Class NativeMethods
            Private Sub New()
            End Sub
            Public Const DRAGDROP_S_DROP As Integer = &H40100
            Public Const DRAGDROP_S_CANCEL As Integer = &H40101
            Public Const DRAGDROP_S_USEDEFAULTCURSORS As Integer = &H40102
            Public Const DV_E_DVASPECT As Integer = -2147221397
            Public Const DV_E_FORMATETC As Integer = -2147221404
            Public Const DV_E_TYMED As Integer = -2147221399
            Public Const E_FAIL As Integer = -2147467259
            Public Const FD_CREATETIME As UInteger = &H8
            Public Const FD_WRITESTIME As UInteger = &H20
            Public Const FD_FILESIZE As UInteger = &H40
            Public Const OLE_E_ADVISENOTSUPPORTED As Integer = -2147221501
            Public Const S_OK As Integer = 0
            Public Const S_FALSE As Integer = 1
            Public Const VARIANT_FALSE As Integer = 0
            Public Const VARIANT_TRUE As Integer = -1

            Public Const FD_ATTRIBUTES As UInteger = &H4
            Public Const FD_SHOWPROGRESSUI As UInteger = &H4000

            Public Const CFSTR_FILECONTENTS As String = "FileContents"
            Public Const CFSTR_FILEDESCRIPTORW As String = "FileGroupDescriptorW"
            Public Const CFSTR_PASTESUCCEEDED As String = "Paste Succeeded"
            Public Const CFSTR_PERFORMEDDROPEFFECT As String = "Performed DropEffect"
            Public Const CFSTR_PREFERREDDROPEFFECT As String = "Preferred DropEffect"

            <SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes", Justification:="Structure exists for interop.")>
            <StructLayout(LayoutKind.Sequential)>
            Public Structure FILEGROUPDESCRIPTOR
                Public cItems As UInt32
                ' Followed by 0 or more FILEDESCRIPTORs
            End Structure

            <SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes", Justification:="Structure exists for interop.")>
            <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
            Public Structure FILEDESCRIPTOR
                Public dwFlags As UInt32
                Public clsid As Guid
                Public sizelcx As Int32
                Public sizelcy As Int32
                Public pointlx As Int32
                Public pointly As Int32
                Public dwFileAttributes As UInt32
                Public ftCreationTime As System.Runtime.InteropServices.ComTypes.FILETIME
                Public ftLastAccessTime As System.Runtime.InteropServices.ComTypes.FILETIME
                Public ftLastWriteTime As System.Runtime.InteropServices.ComTypes.FILETIME
                Public nFileSizeHigh As UInt32
                Public nFileSizeLow As UInt32
                <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
                Public cFileName As String
            End Structure

            <ComImport>
            <Guid("00000121-0000-0000-C000-000000000046")>
            <InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
            Public Interface IDropSource
                <PreserveSig>
                Function QueryContinueDrag(fEscapePressed As Integer, grfKeyState As UInteger) As Integer
                <PreserveSig>
                Function GiveFeedback(dwEffect As UInteger) As Integer
            End Interface

            <SuppressMessage("Microsoft.Design", "CA1021:AvoidOutParameters", MessageId:="2#", Justification:="Win32 API.")>
            <DllImport("shell32.dll")>
            Public Shared Function SHCreateStdEnumFmtEtc(cfmt As UInteger, afmt As FORMATETC(), ByRef ppenumFormatEtc As IEnumFORMATETC) As Integer
            End Function

            <DllImport("ole32.dll", PreserveSig:=False)>
            Public Shared Function CreateStreamOnHGlobal(hGlobal As IntPtr, <MarshalAs(UnmanagedType.Bool)> fDeleteOnRelease As Boolean) As <MarshalAs(UnmanagedType.[Interface])> IStream
            End Function

            <DllImport("ole32.dll", CharSet:=CharSet.Auto, ExactSpelling:=True, PreserveSig:=False)>
            Public Shared Sub DoDragDrop(dataObject As System.Runtime.InteropServices.ComTypes.IDataObject, dropSource As IDropSource, allowedEffects As Integer, finalEffect As Integer())
            End Sub

            <DllImport("kernel32.dll")>
            Public Shared Function GlobalLock(hMem As IntPtr) As IntPtr
            End Function

            <DllImport("kernel32.dll")>
            Public Shared Function GlobalUnlock(hMem As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
            End Function

            <DllImport("kernel32.dll")>
            Public Shared Function GlobalSize(handle As IntPtr) As IntPtr
            End Function

            Public Shared Function SUCCEEDED(hr As Integer) As Boolean
                Return (0 <= hr)
            End Function
        End Class
    End Class

    <ComImport>
    <Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")>
    <InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
    Friend Interface IAsyncOperation
        Sub SetAsyncMode(<[In]> fDoOpAsync As Int32)
        Sub GetAsyncMode(<Out> ByRef pfIsOpAsync As Int32)
        Sub StartOperation(<[In]> pbcReserved As IBindCtx)
        Sub InOperation(<Out> ByRef pfInAsyncOp As Int32)
        Sub EndOperation(<[In]> hResult As Int32, <[In]> pbcReserved As IBindCtx, <[In]> dwEffects As UInt32)
    End Interface

End Namespace