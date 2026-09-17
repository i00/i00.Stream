Imports System.Linq.Expressions

Namespace ExpressionHelpers
    Friend Module ExpressionExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Function Visit(Of T As Expression)(ByVal exp As Expression, ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As Expression
            Return ExpressionVisitor(Of T).Visit(exp, visitor, visitReplacement)
        End Function

        '<System.Runtime.CompilerServices.Extension> _
        'Public Function Visit(Of T As Expression, TExp As Expression)(ByVal exp As TExp, ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As TExp
        '	Return CType(ExpressionVisitor(Of T).Visit(exp, visitor, visitReplacement), TExp)
        'End Function

        <System.Runtime.CompilerServices.Extension>
        Public Function Visit(Of T As Expression, TDelegate)(ByVal exp As Expression(Of TDelegate), ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As Expression(Of TDelegate)
            Return ExpressionVisitor(Of T).Visit(Of TDelegate)(exp, visitor, visitReplacement)
        End Function

        <System.Runtime.CompilerServices.Extension>
        Public Function Visit(Of T As Expression, TSource)(ByVal source As IQueryable(Of TSource), ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As IQueryable(Of TSource)
            Return source.Provider.CreateQuery(Of TSource)(ExpressionVisitor(Of T).Visit(source.Expression, visitor, visitReplacement))
        End Function
    End Module

    ''' <summary>
    ''' This class visits every Parameter expression in an expression tree and calls a delegate
    ''' to optionally replace the parameter.  This is useful where two expression trees need to
    ''' be merged (and they don't share the same ParameterExpressions).
    ''' </summary>
    Friend Class ExpressionVisitor(Of T As Expression)
        Inherits ExpressionVisitor
        Private visitor As Func(Of T, Expression)
        Private visitReplacement As Boolean

        'Protected Overrides Function VisitMethodCall(node As MethodCallExpression) As Expression
        '    'https://referencesource.microsoft.com/#System.Data.Linq/SqlClient/Query/Funcletizer.cs,3f3bd6fc7fdf9cad

        '    Dim obj As Expression = Me.Visit(node.Object)
        '    Dim args As IEnumerable(Of Expression) = Me.VisitExpressionList(node.Arguments)
        '    If obj IsNot node.Object OrElse args IsNot node.Arguments Then
        '        Return Expression.Call(obj, node.Method, args)
        '    End If
        '    Return node

        '    '''http://stackoverflow.com/questions/7731905/how-to-convert-an-expression-tree-to-a-partial-sql-query/7891426#7891426
        '    ''For Each item In node.Arguments
        '    ''    Return Me.Visit(item)
        '    ''Next
        '    ''for each item in ....Arguments
        '    ''    Expression nextExpression = m.Arguments[0];
        '    ''    Return this.Visit(nextExpression);
        '    ''next
        '    'Dim LinqMapping = node.Method.GetCustomAttributes(True).OfType(Of System.Data.Linq.Mapping.FunctionAttribute).FirstOrDefault
        '    'If LinqMapping IsNot Nothing Then
        '    '    'we are a linq
        '    'End If
        '    'Return MyBase.VisitMethodCall(node)
        'End Function

        'Friend Overridable Function VisitExpressionList(original As System.Collections.ObjectModel.ReadOnlyCollection(Of Expression)) As System.Collections.ObjectModel.ReadOnlyCollection(Of Expression)
        '    Dim list As List(Of Expression) = Nothing
        '    Dim i As Integer = 0, n As Integer = original.Count
        '    While i < n
        '        Dim p As Expression = Me.Visit(original(i))
        '        If list IsNot Nothing Then
        '            list.Add(p)
        '        ElseIf p IsNot original(i) Then
        '            list = New List(Of Expression)(n)
        '            For j As Integer = 0 To i - 1
        '                list.Add(original(j))
        '            Next
        '            list.Add(p)
        '        End If
        '        i += 1
        '    End While
        '    If list IsNot Nothing Then
        '        Return New System.Collections.ObjectModel.ReadOnlyCollection(Of Expression)(list)
        '    End If
        '    Return original
        'End Function

        'Private Function ParseLinqMappingExpression(expression As MethodCallExpression) As Boolean
        '    'Dim sizeExpression As ConstantExpression = DirectCast(expression.Arguments(1), ConstantExpression)

        '    'Dim size As Integer
        '    'If Integer.TryParse(sizeExpression.Value.ToString(), size) Then
        '    '    _take = size
        '    '    Return True
        '    'End If

        '    'Return False
        'End Function

        Public Sub New(ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True)
            Me.visitor = visitor
            Me.visitReplacement = visitReplacement
        End Sub

        Public Overloads Shared Function Visit(ByVal exp As Expression, ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As Expression
            Return New ExpressionVisitor(Of T)(visitor, visitReplacement).Visit(exp)
        End Function

        Public Overloads Shared Function Visit(Of TDelegate)(ByVal exp As Expression(Of TDelegate), ByVal visitor As Func(Of T, Expression), Optional ByVal visitReplacement As Boolean = True) As Expression(Of TDelegate)
            Return CType(New ExpressionVisitor(Of T)(visitor, visitReplacement).Visit(exp), Expression(Of TDelegate))
        End Function

        Public Overrides Function Visit(ByVal exp As Expression) As Expression
            Dim result = If(TypeOf exp Is T AndAlso visitor IsNot Nothing, visitor(CType(exp, T)), exp)

            Return If(result IsNot exp AndAlso (Not visitReplacement), result, MyBase.Visit(result))
        End Function
    End Class
End Namespace
