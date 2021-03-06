﻿' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports TypeKind = Microsoft.CodeAnalysis.TypeKind

Namespace Microsoft.CodeAnalysis.VisualBasic
    Partial Friend NotInheritable Class LocalRewriter

        Private Function RewriteConstant(node As BoundExpression, constantValue As ConstantValue) As BoundExpression
            Dim result As BoundNode = node

            If Not inExpressionLambda AndAlso Not node.HasErrors Then
                If constantValue.Discriminator = ConstantValueTypeDiscriminator.Decimal Then
                    Return RewriteDecimalConstant(node, constantValue, Me.topMethod, Me.diagnostics)

                ElseIf constantValue.Discriminator = ConstantValueTypeDiscriminator.DateTime Then
                    Return RewriteDateConstant(node, constantValue, Me.topMethod, Me.diagnostics)
                End If
            End If

            Return If(node.Kind = BoundKind.Literal, node, New BoundLiteral(node.Syntax, constantValue, node.Type, hasErrors:=constantValue.IsBad))
        End Function

        Private Shared Function RewriteDecimalConstant(node As BoundExpression, nodeValue As ConstantValue, currentMethod As MethodSymbol, diagnostics As DiagnosticBag) As BoundExpression
            Dim assembly As AssemblySymbol = currentMethod.ContainingAssembly

            Dim decInfo As DecimalData = nodeValue.DecimalValue.GetBits()

            Dim isNegative As Boolean = decInfo.sign

            ' if we have a number which only uses the bottom 4 bytes and
            ' has no fraction part, then we can generate more optimal code

            If decInfo.scale = 0 AndAlso decInfo.Hi32 = 0 AndAlso decInfo.Mid32 = 0 Then

                ' If we are building static constructor of System.Decimal, accessing static fields 
                ' would be bad.
                If currentMethod.MethodKind <> MethodKind.SharedConstructor OrElse
                   currentMethod.ContainingType.SpecialType <> SpecialType.System_Decimal Then

                    Dim useField As Symbol = Nothing

                    If decInfo.Lo32 = 0 Then
                        ' whole value == 0 if we get here
                        useField = assembly.GetSpecialTypeMember(SpecialMember.System_Decimal__Zero)
                    ElseIf decInfo.Lo32 = 1 Then
                        If isNegative Then
                            ' whole value == -1 if we get here
                            useField = assembly.GetSpecialTypeMember(SpecialMember.System_Decimal__MinusOne)
                        Else
                            ' whole value == 1 if we get here
                            useField = assembly.GetSpecialTypeMember(SpecialMember.System_Decimal__One)
                        End If
                    End If

                    If useField IsNot Nothing AndAlso useField.GetUseSiteErrorInfo() Is Nothing AndAlso useField.ContainingType.GetUseSiteErrorInfo() Is Nothing Then
                        Dim fieldSymbol = DirectCast(useField, FieldSymbol)
                        Return New BoundFieldAccess(node.Syntax, Nothing, fieldSymbol, IsLValue:=False, Type:=fieldSymbol.Type)
                    End If
                End If

                ' Convert from unsigned to signed.  To do this, store into a
                ' larger data type (this won't do sign extension), and then set the sign
                ' 
                Dim value As Int64 = decInfo.Lo32

                If isNegative Then
                    value = -value
                End If

                Dim decCtorInt64 As MethodSymbol
                decCtorInt64 = DirectCast(assembly.GetSpecialTypeMember(SpecialMember.System_Decimal__CtorInt64), MethodSymbol)

                If decCtorInt64 IsNot Nothing AndAlso decCtorInt64.GetUseSiteErrorInfo() Is Nothing AndAlso decCtorInt64.ContainingType.GetUseSiteErrorInfo() Is Nothing Then

                    ' generate New Decimal(value)
                    Return New BoundObjectCreationExpression(
                        node.Syntax,
                        decCtorInt64,
                        ImmutableArrayExtensions.AsImmutableOrNull(Of BoundExpression)(
                            {New BoundLiteral(node.Syntax, ConstantValue.Create(value), decCtorInt64.Parameters(0).Type)}),
                        Nothing,
                        node.Type)
                End If
            End If

            ' Looks like we have to do it the hard way
            ' Emit all parts of the value, including Sign info and Scale info
            ' 
            'Public Sub New( _
            ' lo As Integer, _
            ' mid As Integer, _
            ' hi As Integer, _
            ' isNegative As Boolean, _
            ' scale As Byte _
            ')

            Dim decCtor As MethodSymbol = Nothing

            Const memberId As SpecialMember = SpecialMember.System_Decimal__CtorInt32Int32Int32BooleanByte
            decCtor = DirectCast(assembly.GetSpecialTypeMember(memberId), MethodSymbol)

            If Not ReportMissingOrBadRuntimeHelper(node, memberId, decCtor, diagnostics) Then
                ' generate New Decimal(lo, mid, hi, isNegative, scale)
                Return New BoundObjectCreationExpression(
                    node.Syntax,
                    decCtor,
                    ImmutableArrayExtensions.AsImmutableOrNull(Of BoundExpression)(
                        {New BoundLiteral(node.Syntax, ConstantValue.Create(UncheckedCInt(decInfo.Lo32)), decCtor.Parameters(0).Type),
                         New BoundLiteral(node.Syntax, ConstantValue.Create(UncheckedCInt(decInfo.Mid32)), decCtor.Parameters(1).Type),
                         New BoundLiteral(node.Syntax, ConstantValue.Create(UncheckedCInt(decInfo.Hi32)), decCtor.Parameters(2).Type),
                         New BoundLiteral(node.Syntax, ConstantValue.Create(decInfo.sign), decCtor.Parameters(3).Type),
                         New BoundLiteral(node.Syntax, ConstantValue.Create(decInfo.scale), decCtor.Parameters(4).Type)}),
                   Nothing,
                   node.Type)
            End If

            Return node ' We get here only if we failed to rewrite the constant
        End Function

        Private Shared Function RewriteDateConstant(node As BoundExpression, nodeValue As ConstantValue, currentMethod As MethodSymbol, diagnostics As DiagnosticBag) As BoundExpression
            Dim assembly As AssemblySymbol = currentMethod.ContainingAssembly

            Dim dt As Date = nodeValue.DateTimeValue

            ' If we are building static constructor of System.DateTime, accessing static fields 
            ' would be bad.
            If dt = Date.MinValue AndAlso
                (currentMethod.MethodKind <> MethodKind.SharedConstructor OrElse
                currentMethod.ContainingType.SpecialType <> SpecialType.System_DateTime) Then

                Dim dtMinValue = DirectCast(assembly.GetSpecialTypeMember(SpecialMember.System_DateTime__MinValue), FieldSymbol)

                If dtMinValue IsNot Nothing AndAlso dtMinValue.GetUseSiteErrorInfo() Is Nothing AndAlso dtMinValue.ContainingType.GetUseSiteErrorInfo() Is Nothing Then
                    Return New BoundFieldAccess(node.Syntax, Nothing, dtMinValue, isLValue:=False, type:=dtMinValue.Type)
                End If
            End If

            ' This one makes a call to System.DateTime::.ctor(int64) in mscorlib

            Dim dtCtorInt64 As MethodSymbol

            Const memberId As SpecialMember = SpecialMember.System_DateTime__CtorInt64
            dtCtorInt64 = DirectCast(assembly.GetSpecialTypeMember(memberId), MethodSymbol)

            If Not ReportMissingOrBadRuntimeHelper(node, memberId, dtCtorInt64, diagnostics) Then

                ' generate New Decimal(value)
                Return New BoundObjectCreationExpression(
                    node.Syntax,
                    dtCtorInt64,
                    ImmutableArrayExtensions.AsImmutableOrNull(Of BoundExpression)(
                        {New BoundLiteral(node.Syntax, ConstantValue.Create(dt.Ticks), dtCtorInt64.Parameters(0).Type)}),
                    Nothing,
                    node.Type)
            End If

            Return node ' We get here only if we failed to rewrite the constant
        End Function

    End Class
End Namespace
