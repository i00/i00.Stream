Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Security

''' <summary>
''' Provides shared plugin attributes used by the generic plugin manager.
''' </summary>
Public NotInheritable Class PluginManager

    Private Sub New()
    End Sub

    ''' <summary>
    ''' Specifies whether plugin instances for the decorated plugin type or plugin contract should be reused.
    ''' </summary>
    ''' <remarks>
    ''' When omitted, plugins are treated as reusable by default.
    ''' </remarks>
    <AttributeUsage(AttributeTargets.Class Or AttributeTargets.Interface)>
    Public Class PluginReusableAttribute
        Inherits Attribute

        ''' <summary>
        ''' Gets a value indicating whether plugin instances should be reused.
        ''' </summary>
        Public ReadOnly Property Reusable As Boolean

        ''' <summary>
        ''' Initialises a new instance of the <see cref="PluginReusableAttribute"/> class.
        ''' </summary>
        ''' <param name="Reusable">True to reuse plugin instances; otherwise, false.</param>
        Public Sub New(Reusable As Boolean)
            Me.Reusable = Reusable
        End Sub
    End Class

    ''' <summary>
    ''' Hides the decorated plugin class from plugin discovery.
    ''' </summary>
    <AttributeUsage(AttributeTargets.Class)>
    Public Class PluginHiddenAttribute
        Inherits Attribute
    End Class

    ''' <summary>
    ''' Specifies the relative discovery order weight for a plugin class.
    ''' </summary>
    ''' <remarks>
    ''' Plugins with a higher weight are returned before plugins with a lower weight.
    ''' </remarks>
    <AttributeUsage(AttributeTargets.Class)>
    Public Class PluginWeightAttribute
        Inherits Attribute

        ''' <summary>
        ''' Gets the discovery order weight for the plugin class.
        ''' </summary>
        Public ReadOnly Property Weight As Double

        ''' <summary>
        ''' Initialises a new instance of the <see cref="PluginWeightAttribute"/> class.
        ''' </summary>
        ''' <param name="Weight">The discovery order weight for the plugin class.</param>
        Public Sub New(Weight As Double)
            Me.Weight = Weight
        End Sub
    End Class

End Class

''' <summary>
''' Discovers, loads and creates plugin instances that implement or inherit from the specified plugin contract.
''' </summary>
''' <typeparam name="TPlugin">The base class or interface that plugin types must implement.</typeparam>
Public NotInheritable Class PluginManager(Of TPlugin As Class)

    Private Shared ReadOnly SyncRoot As New Object()
    Private Shared ReadOnly PluginDirectories As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Public Shared ReadOnly LoadedAssembliesByPath As New Dictionary(Of String, Assembly)(StringComparer.OrdinalIgnoreCase)
    Private Shared ReadOnly FailedAssemblyPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private Shared ReadOnly LoadableTypesByAssembly As New Dictionary(Of Assembly, Type())()

    'With the ReusablePluginsByType Key below:
    '   - Missing = reusable state has not been checked yet
    '   - Nothing = checked, plugin is not reusable
    '   - TPlugin class = checked, plugin is reusable and this is the cached instance of it
    Private Shared ReadOnly ReusablePluginsByType As New Dictionary(Of Type, TPlugin)()

    Private Shared AssemblyResolverInstalled As Boolean

    Private Sub New()
    End Sub

    ''' <summary>
    ''' Gets all discoverable plugin types for the specified plugin contract.
    ''' </summary>
    ''' <param name="PluginPath">
    ''' The directory to scan for plugin assemblies. When omitted, the current application base directory is used.
    ''' </param>
    ''' <param name="Recursive">
    ''' True to scan subdirectories recursively; otherwise, false to scan only the specified directory.
    ''' </param>
    ''' <returns>
    ''' An array of plugin types ordered by descending plugin weight, then by full type name.
    ''' </returns>
    Public Shared Function GetPluginTypes(Optional PluginPath As String = Nothing,
                                          Optional Recursive As Boolean = False) As Type()

        If String.IsNullOrWhiteSpace(PluginPath) Then
            PluginPath = AppDomain.CurrentDomain.BaseDirectory
        End If

        EnsureAssembliesLoaded(PluginPath, Recursive)

        Return AppDomain.CurrentDomain.GetAssemblies().
                                       Where(Function(assembly) assembly.IsDynamic = False).
                                       SelectMany(Function(assembly) GetLoadableTypes(assembly)).
                                       OrderByDescending(Function(pluginType) GetPluginWeight(pluginType)).
                                       ThenBy(Function(pluginType) pluginType.FullName).
                                       ToArray()

    End Function

    ''' <summary>
    ''' Creates plugin instances for all discoverable plugin types for the specified plugin contract.
    ''' </summary>
    ''' <param name="PluginPath">
    ''' The directory to scan for plugin assemblies. When omitted, the current application base directory is used.
    ''' </param>
    ''' <param name="Recursive">
    ''' True to scan subdirectories recursively; otherwise, false to scan only the specified directory.
    ''' </param>
    ''' <returns>
    ''' An array of plugin instances.
    ''' </returns>
    Public Shared Function CreatePlugins(Optional PluginPath As String = Nothing,
                                         Optional Recursive As Boolean = False) As TPlugin()

        Return GetPluginTypes(PluginPath, Recursive).
               Select(Function(pluginType) CreatePlugin(pluginType)).
               ToArray()

    End Function

    Private Shared Function CreatePlugin(PluginType As Type) As TPlugin

        SyncLock SyncRoot

            Dim ExistingPlugin As TPlugin = Nothing

            If ReusablePluginsByType.TryGetValue(PluginType, ExistingPlugin) Then

                If ExistingPlugin IsNot Nothing Then
                    Return ExistingPlugin
                End If

                Return DirectCast(Activator.CreateInstance(PluginType), TPlugin)

            End If

            If IsPluginReusable(PluginType) Then

                Dim NewPlugin = DirectCast(Activator.CreateInstance(PluginType), TPlugin)
                ReusablePluginsByType.Add(PluginType, NewPlugin)

                Return NewPlugin

            End If

            ReusablePluginsByType.Add(PluginType, Nothing)

        End SyncLock

        Return DirectCast(Activator.CreateInstance(PluginType), TPlugin)

    End Function

    Private Shared Function IsPluginType(PluginType As Type) As Boolean

        Dim RequestedType = GetType(TPlugin)

        If PluginType Is Nothing Then
            Return False
        End If

        If PluginType Is RequestedType Then
            Return False
        End If

        If PluginType.IsClass = False Then
            Return False
        End If

        If PluginType.IsAbstract Then
            Return False
        End If

        If RequestedType.IsAssignableFrom(PluginType) = False Then
            Return False
        End If

        If Attribute.IsDefined(PluginType,
                               GetType(PluginManager.PluginHiddenAttribute),
                               False) Then
            Return False
        End If

        If PluginType.GetConstructor(Type.EmptyTypes) Is Nothing Then
            Return False
        End If

        Return True

    End Function

    Private Shared Function GetPluginWeight(PluginType As Type) As Double

        Dim WeightAttribute =
            DirectCast(Attribute.GetCustomAttribute(PluginType,
                                                    GetType(PluginManager.PluginWeightAttribute),
                                                    False),
                       PluginManager.PluginWeightAttribute)

        If WeightAttribute Is Nothing Then
            Return 0
        End If

        Return WeightAttribute.Weight

    End Function

    Private Shared Function IsPluginReusable(PluginType As Type) As Boolean

        Dim CurrentType = PluginType

        While CurrentType IsNot Nothing AndAlso CurrentType IsNot GetType(Object)

            Dim ReusableAttribute = GetPluginReusableAttribute(CurrentType)

            If ReusableAttribute IsNot Nothing Then
                Return ReusableAttribute.Reusable
            End If

            CurrentType = CurrentType.BaseType

        End While

        Dim RequestedType = GetType(TPlugin)
        Dim RequestedReusableAttribute = GetPluginReusableAttribute(RequestedType)

        If RequestedReusableAttribute IsNot Nothing Then
            Return RequestedReusableAttribute.Reusable
        End If

        For Each interfaceType In PluginType.GetInterfaces()

            Dim ReusableAttribute = GetPluginReusableAttribute(interfaceType)

            If ReusableAttribute IsNot Nothing Then
                Return ReusableAttribute.Reusable
            End If

        Next

        Return True

    End Function

    Private Shared Function GetPluginReusableAttribute(TargetType As Type) As PluginManager.PluginReusableAttribute

        If TargetType Is Nothing Then
            Return Nothing
        End If

        Return DirectCast(Attribute.GetCustomAttribute(TargetType,
                                                       GetType(PluginManager.PluginReusableAttribute),
                                                       False),
                          PluginManager.PluginReusableAttribute)

    End Function

    Private Shared Sub EnsureAssembliesLoaded(PluginPath As String,
                                              Recursive As Boolean)

        If Directory.Exists(PluginPath) = False Then
            Return
        End If

        Dim FullPluginPath = Path.GetFullPath(PluginPath)

        SyncLock SyncRoot

            PluginDirectories.Add(FullPluginPath)

            If AssemblyResolverInstalled = False Then
                AddHandler AppDomain.CurrentDomain.AssemblyResolve,
                           AddressOf ResolvePluginAssembly

                AssemblyResolverInstalled = True
            End If

        End SyncLock

        Dim SearchOptionValue =
            If(Recursive,
               SearchOption.AllDirectories,
               SearchOption.TopDirectoryOnly)

        Dim AssemblyFiles =
            Directory.EnumerateFiles(FullPluginPath, "*.dll", SearchOptionValue).
                      Concat(Directory.EnumerateFiles(FullPluginPath, "*.exe", SearchOptionValue))

        For Each assemblyFile In AssemblyFiles
            TryLoadAssembly(assemblyFile)
        Next

    End Sub

    Private Shared Function TryLoadAssembly(AssemblyPath As String) As Assembly

        If File.Exists(AssemblyPath) = False Then
            Return Nothing
        End If

        Dim FullAssemblyPath = Path.GetFullPath(AssemblyPath)

        SyncLock SyncRoot

            If FailedAssemblyPaths.Contains(FullAssemblyPath) Then
                Return Nothing
            End If

            Dim ExistingAssembly As Assembly = Nothing

            If LoadedAssembliesByPath.TryGetValue(FullAssemblyPath, ExistingAssembly) Then
                Return ExistingAssembly
            End If

        End SyncLock

        Try

            Dim RequestedAssemblyName = AssemblyName.GetAssemblyName(FullAssemblyPath)

            Dim AlreadyLoaded =
                AppDomain.CurrentDomain.GetAssemblies().
                                        Where(Function(assembly) assembly.IsDynamic = False).
                                        FirstOrDefault(Function(assembly)
                                                           Return String.Equals(assembly.FullName,
                                                                                RequestedAssemblyName.FullName,
                                                                                StringComparison.OrdinalIgnoreCase)
                                                       End Function)

            If AlreadyLoaded IsNot Nothing Then

                SyncLock SyncRoot

                    If LoadedAssembliesByPath.ContainsKey(FullAssemblyPath) = False Then
                        LoadedAssembliesByPath.Add(FullAssemblyPath, AlreadyLoaded)
                    End If

                End SyncLock

                Return AlreadyLoaded

            End If

            Dim LoadedAssembly = Assembly.LoadFrom(FullAssemblyPath)

            SyncLock SyncRoot

                If LoadedAssembliesByPath.ContainsKey(FullAssemblyPath) = False Then
                    LoadedAssembliesByPath.Add(FullAssemblyPath, LoadedAssembly)
                End If

            End SyncLock

            Return LoadedAssembly

        Catch Ex As BadImageFormatException
            ' Not a .NET assembly, so it cannot contain plugin types.
        Catch Ex As FileLoadException
            ' The assembly could not be loaded into this AppDomain.
        Catch Ex As IOException
            ' The file may be locked or unavailable.
        Catch Ex As UnauthorizedAccessException
            ' The current process cannot access this file.
        Catch Ex As SecurityException
            ' The current process is not permitted to load this file.

        End Try

        SyncLock SyncRoot
            FailedAssemblyPaths.Add(FullAssemblyPath)
        End SyncLock

        Return Nothing

    End Function

    Private Shared Function GetLoadableTypes(CandidateAssembly As Assembly) As IEnumerable(Of Type)

        Dim CachedTypes As Type() = Nothing

        SyncLock SyncRoot

            If LoadableTypesByAssembly.TryGetValue(CandidateAssembly, CachedTypes) Then
                Return CachedTypes
            End If

        End SyncLock

        Dim LoadableTypes = Enumerable.Empty(Of Type)()

        Try

            LoadableTypes = CandidateAssembly.GetTypes()

        Catch Ex As ReflectionTypeLoadException

            LoadableTypes = Ex.Types.
                               Where(Function(t) t IsNot Nothing)

        Catch Ex As NotSupportedException

        End Try

        LoadableTypes = LoadableTypes.Where(Function(x) IsPluginType(x))

        SyncLock SyncRoot

            If LoadableTypesByAssembly.ContainsKey(CandidateAssembly) = False Then
                LoadableTypesByAssembly.Add(CandidateAssembly, LoadableTypes.ToArray())
            End If

            Return LoadableTypesByAssembly(CandidateAssembly)

        End SyncLock

    End Function

    Private Shared Function ResolvePluginAssembly(Sender As Object,
                                                  Arguments As ResolveEventArgs) As Assembly

        Dim RequestedAssemblyName = New AssemblyName(Arguments.Name)
        Dim Directories As String()

        SyncLock SyncRoot
            Directories = PluginDirectories.ToArray()
        End SyncLock

        For Each pluginDirectory In Directories

            Dim DllPath = Path.Combine(pluginDirectory, $"{RequestedAssemblyName.Name}.dll")
            Dim ExePath = Path.Combine(pluginDirectory, $"{RequestedAssemblyName.Name}.exe")

            Dim ResolvedAssembly = TryLoadAssembly(DllPath)

            If ResolvedAssembly IsNot Nothing Then
                Return ResolvedAssembly
            End If

            ResolvedAssembly = TryLoadAssembly(ExePath)

            If ResolvedAssembly IsNot Nothing Then
                Return ResolvedAssembly
            End If

        Next

        Return Nothing

    End Function

End Class