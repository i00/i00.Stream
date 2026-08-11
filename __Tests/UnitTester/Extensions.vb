Friend Module Extensions

    <Runtime.CompilerServices.Extension()>
    Public Function Indent(Text As String, Optional Indenter As String = vbTab, Optional SkipFirstLine As Boolean = False) As String
        Dim Lines = Split(Text, vbCrLf)
        Dim ModedLines = Lines.Skip(If(SkipFirstLine, 1, 0)).Select(Function(x) Indenter & x)
        If SkipFirstLine Then
            ModedLines = Lines.Take(1).Union(ModedLines)
        End If
        Return Join(ModedLines.ToArray, vbCrLf)
    End Function

    <System.Runtime.CompilerServices.Extension>
    Iterator Function IndexPositionJoin(Of TOuter, TInner, TResult)(outer As IEnumerable(Of TOuter), inner As IEnumerable(Of TInner), resultSelector As Func(Of TOuter, TInner, TResult)) As IEnumerable(Of TResult)
        Dim Left = outer?.ToArray
        Dim Right = inner?.ToArray
        For i = 0 To Math.Max((Left?.Count).GetValueOrDefault(0) - 1, (Right?.Count).GetValueOrDefault(0) - 1)
            Dim LeftItem = If(i < (Left?.Count).GetValueOrDefault(0), Left(i), Nothing)
            Dim RightItem = If(i < (Right?.Count).GetValueOrDefault(0), Right(i), Nothing)
            Yield resultSelector.Invoke(LeftItem, RightItem)
        Next
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

    'Orig List:
    'A: 1,2,4
    'B: 3,4,5
    'Result:
    '   A       B
    '   1       -
    '   2       -
    '   -       3
    '   4       4
    '   -       5
    <System.Runtime.CompilerServices.Extension>
    Function FullOuterJoin(Of TOuter, TInner, TKey, TResult)(outer As IEnumerable(Of TOuter), inner As IEnumerable(Of TInner), outerKeySelector As Func(Of TOuter, TKey), innerKeySelector As Func(Of TInner, TKey), resultSelector As Func(Of TOuter, TInner, TResult)) As IEnumerable(Of TResult)
        Return outer.LeftSingularJoin(inner, outerKeySelector, innerKeySelector, resultSelector).
                     Union(
                               inner.LeftSingularJoin(outer, innerKeySelector, outerKeySelector, Function(x, y) New With {.y = x, .x = y}).'< reverse as right join
                                     Where(Function(x) Object.Equals(x.x, DirectCast(Nothing, TOuter))).'< like where x.x Is Nothing ... but works for values too ... like 0 = Nothing ... as: CObj(0) Is Nothing = False
                                     Select(Function(x) resultSelector.Invoke(x.x, x.y))
                           )
    End Function

    <System.Runtime.CompilerServices.Extension>
    Function LeftSingularJoin(Of TOuter, TInner, TKey, TResult)(outer As IEnumerable(Of TOuter), inner As IEnumerable(Of TInner), outerKeySelector As Func(Of TOuter, TKey), innerKeySelector As Func(Of TInner, TKey), resultSelector As Func(Of TOuter, TInner, TResult)) As IEnumerable(Of TResult)
        Return outer.GroupJoin(inner, outerKeySelector, innerKeySelector, Function(x, y) New With {x, y}).
                     SelectMany(Function(x) x.y.DefaultIfEmpty.Take(1), Function(x, y) resultSelector.Invoke(x.x, y))
    End Function

End Module
