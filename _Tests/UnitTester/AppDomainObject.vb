' Usefull links
' https://www.brad-smith.info/blog/archives/500
'

Public Class AppDomainObject
    Implements IDisposable

    ' The remote object living inside the created AppDomain
    Public ReadOnly Property DomainObject As MarshalByRefObject

    ' The sandboxed AppDomain instance
    Public ReadOnly Property AppDomain As AppDomain

    Public Shared Function CreateClassOnNewAppDomain(Of T As MarshalByRefObject)(TrustLevels As System.Security.SecurityZone) As AppDomainObject(Of T)
        Dim AppDomainAndObject = CreateDomainAndObject(GetType(T).Assembly.FullName, GetType(T).FullName, TrustLevels)
        Return New AppDomainObject(Of T)(AppDomainAndObject.AppDomain, AppDomainAndObject.Object)
    End Function

    Public Shared Function CreateClassOnNewAppDomain(MarshalByRefObjectType As Type, TrustLevels As System.Security.SecurityZone) As AppDomainObject
        Return CreateClassOnNewAppDomain(MarshalByRefObjectType.Assembly.FullName, MarshalByRefObjectType.FullName, TrustLevels)
    End Function

    Public Shared Function CreateClassOnNewAppDomain(AssemblyFullName As String, TypeFullName As String, TrustLevels As System.Security.SecurityZone) As AppDomainObject
        Dim AppDomainAndObject = CreateDomainAndObject(AssemblyFullName, TypeFullName, TrustLevels)
        Return New AppDomainObject(AppDomainAndObject.AppDomain, AppDomainAndObject.Object)
    End Function


    Private Class AppDomainAndObject
        Public ReadOnly Property AppDomain As AppDomain
        Public ReadOnly Property [Object] As MarshalByRefObject

        Public Sub New(AppDomain As AppDomain, [Object] As MarshalByRefObject)
            Me.AppDomain = AppDomain
            Me.Object = [Object]
        End Sub
    End Class

    Private Shared Function CreateDomainAndObject(AssemblyFullName As String, TypeFullName As String, TrustLevels As System.Security.SecurityZone) As AppDomainAndObject

        Dim hostEvidence As New System.Security.Policy.Evidence()
        hostEvidence.AddHostEvidence(New System.Security.Policy.Zone(TrustLevels))

        Dim pset = System.Security.SecurityManager.GetStandardSandbox(hostEvidence)

        Dim ads As New AppDomainSetup()
        ads.ApplicationBase = Misc.MakePathAbsolute("")

        Dim ad = AppDomain.CreateDomain("Sandbox", hostEvidence, ads, pset, Nothing)

        Dim o = DirectCast(ad.CreateInstanceAndUnwrap(
                            AssemblyFullName,
                            TypeFullName), MarshalByRefObject)

        Return New AppDomainAndObject(ad, o)
    End Function


    Friend Sub New(AppDomain As AppDomain, DomainObject As MarshalByRefObject)
        Me.AppDomain = AppDomain
        Me.DomainObject = DomainObject

        ' No sponsorship required anymore.
        ' The AppDomain lifetime itself controls cleanup.
    End Sub


    Public Sub Dispose() Implements IDisposable.Dispose
        Try
            If Me.AppDomain IsNot Nothing Then
                System.AppDomain.Unload(Me.AppDomain)
            End If
        Catch
            ' AppDomain unload can throw if already unloading/invalid state
            ' Swallow to make Dispose safe and repeatable
        End Try
    End Sub

End Class


Public NotInheritable Class AppDomainObject(Of T As MarshalByRefObject)
    Inherits AppDomainObject

    Public Shadows ReadOnly Property DomainObject As T
        Get
            Return DirectCast(MyBase.DomainObject, T)
        End Get
    End Property

    Friend Sub New(AppDomain As AppDomain, DomainObject As MarshalByRefObject)
        MyBase.New(AppDomain, DomainObject)
    End Sub

End Class