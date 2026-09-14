Imports System.Linq.Expressions

Namespace ExpressionHelpers
    Partial Module ExpressionExtensions

        <System.Runtime.CompilerServices.Extension()>
        Public Function ConvertToUnchecked(Of TDelegate)(Expression As Expression(Of TDelegate)) As Expression(Of TDelegate)
            If Expression Is Nothing Then Throw New ArgumentNullException(NameOf(Expression))

            Return DirectCast(New UncheckedExpressionVisitor().Visit(Expression), Expression(Of TDelegate))
        End Function

        Private NotInheritable Class UncheckedExpressionVisitor
            Inherits ExpressionVisitor

            Protected Overrides Function VisitBinary(Node As BinaryExpression) As Expression
                Dim Left = Visit(Node.Left)
                Dim Right = Visit(Node.Right)
                Dim Conversion = DirectCast(Visit(Node.Conversion), LambdaExpression)

                Dim NodeType As ExpressionType

                Select Case Node.NodeType
                    Case ExpressionType.AddChecked
                        NodeType = ExpressionType.Add

                    Case ExpressionType.SubtractChecked
                        NodeType = ExpressionType.Subtract

                    Case ExpressionType.MultiplyChecked
                        NodeType = ExpressionType.Multiply

                    Case ExpressionType.AddAssignChecked
                        NodeType = ExpressionType.AddAssign

                    Case ExpressionType.SubtractAssignChecked
                        NodeType = ExpressionType.SubtractAssign

                    Case ExpressionType.MultiplyAssignChecked
                        NodeType = ExpressionType.MultiplyAssign

                    Case Else
                        Return Node.Update(Left, Conversion, Right)
                End Select

                Return System.Linq.Expressions.Expression.MakeBinary(NodeType,
                                                                     Left,
                                                                     Right,
                                                                     Node.IsLiftedToNull,
                                                                     Node.Method,
                                                                     Conversion)
            End Function

            Protected Overrides Function VisitUnary(Node As UnaryExpression) As Expression
                Dim Operand = Visit(Node.Operand)

                Select Case Node.NodeType
                    Case ExpressionType.ConvertChecked
                        Return System.Linq.Expressions.Expression.Convert(Operand, Node.Type, Node.Method)

                    Case ExpressionType.NegateChecked
                        Return System.Linq.Expressions.Expression.Negate(Operand, Node.Method)

                    Case Else
                        Return Node.Update(Operand)
                End Select
            End Function
        End Class
    End Module
End Namespace