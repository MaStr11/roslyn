﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

#nullable enable

namespace Microsoft.CodeAnalysis.CSharp.CodeRefactorings.ConvertStringConcatToInterpolated
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = "TODO" /* PredefinedCodeRefactoringProviderNames.AddAwait*/), Shared]
    internal sealed class CSharpConvertStringConcatToInterpolatedRefactoringProvider : CodeRefactoringProvider
    {
        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public CSharpConvertStringConcatToInterpolatedRefactoringProvider()
        {
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, _, cancellationToken) = context;
            if (document.Project.Solution.Workspace.Kind == WorkspaceKind.MiscellaneousFiles)
            {
                return;
            }

            var binaryExpression = await context.TryGetRelevantNodeAsync<BinaryExpressionSyntax>().ConfigureAwait(false);
            if (binaryExpression?.IsKind(SyntaxKind.AddExpression) == true)
            {
                while (binaryExpression.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } parent)
                {
                    binaryExpression = parent;
                }
                var sematicModel = await context.Document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (IsStringConcatination(sematicModel, binaryExpression, cancellationToken))
                {
                    context.RegisterRefactoring(new MyCodeAction(
                        title: "TODO",
                        c => UpdateDocumentAsync(document, binaryExpression, c)),
                        binaryExpression.Span);
                }
            }
        }

        private static bool IsStringConcatination(SemanticModel semanticModel, BinaryExpressionSyntax binaryExpression, CancellationToken cancellationToken)
        {
            var operation = semanticModel.GetOperation(binaryExpression, cancellationToken);
            if (operation is IBinaryOperation binaryOperation)
            {
                return (binaryOperation.OperatorKind == BinaryOperatorKind.Add && binaryOperation.Type.Equals(semanticModel.Compilation.GetSpecialType(SpecialType.System_String)));
            }

            return false;
        }

        private static void WalkBinaryExpression(SemanticModel semanticModel, ArrayBuilder<ExpressionSyntax> arrayBuilder, BinaryExpressionSyntax binaryExpression, CancellationToken cancellationToken)
        {
            WalkBinaryExpression(semanticModel, arrayBuilder, binaryExpression.Left, cancellationToken);
            WalkBinaryExpression(semanticModel, arrayBuilder, binaryExpression.Right, cancellationToken);
        }

        private static void WalkBinaryExpression(SemanticModel semanticModel, ArrayBuilder<ExpressionSyntax> arrayBuilder, ExpressionSyntax expression, CancellationToken cancellationToken)
        {
            if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } potentialStringConcatination &&
                IsStringConcatination(semanticModel, potentialStringConcatination, cancellationToken))
            {
                WalkBinaryExpression(semanticModel, arrayBuilder, potentialStringConcatination, cancellationToken);
            }
            else
            {
                arrayBuilder.Add(expression);
            }
        }

        private static async Task<Document> UpdateDocumentAsync(Document document, BinaryExpressionSyntax binaryExpression, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            using var _ = ArrayBuilder<ExpressionSyntax>.GetInstance(out var builder);
            WalkBinaryExpression(semanticModel, builder, binaryExpression, cancellationToken);
            MergeContiguousStringLiterals(builder);
            var concatExpressions = builder.ToImmutable();

            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var editor = new SyntaxEditor(root, CSharpSyntaxGenerator.Instance);
            var interpolatedContent = List(concatExpressions.Select<ExpressionSyntax, InterpolatedStringContentSyntax>(expr => expr switch
            {
                var expression when IsStringLiteralExpression(expression, out var literal)
                    => InterpolatedStringText(StringLiteralTokenToInterpolatedStringTextToken(literal.Token)),
                // ToDo: Handle expr is interpolated string
                _ => Interpolation(expr.WithoutTrivia()) // ToDo: Use ParenthesizedExpressionSyntaxExtensions.CanRemoveParentheses to remove any superfluous paranthesis
            }));
            var interpolated = InterpolatedStringExpression(
                Token(SyntaxKind.InterpolatedStringStartToken),
                interpolatedContent);
            editor.ReplaceNode(binaryExpression, interpolated);
            return document.WithSyntaxRoot(editor.GetChangedRoot());
        }

        private static void MergeContiguousStringLiterals(ArrayBuilder<ExpressionSyntax> builder)
        {
            ExpressionSyntax? lastElement = null;
            var i = 0;
            while (i < builder.Count)
            {
                if (IsStringLiteralExpression(lastElement, out var literal1) &&
                    IsStringLiteralExpression(builder[i], out var literal2))
                {
                    var valueText = $"{literal1.Token.ValueText}{literal2.Token.ValueText}";
                    builder[i] = LiteralExpression(SyntaxKind.StringLiteralExpression, Token(TriviaList(), SyntaxKind.StringLiteralToken, $"\"{valueText}\"", valueText, TriviaList()));
                    lastElement = builder[i];
                    builder.RemoveAt(i - 1);
                }
                else
                {
                    lastElement = builder[i];
                    i++;
                }
            }
        }

        private static SyntaxToken StringLiteralTokenToInterpolatedStringTextToken(SyntaxToken stringLiteralToken)
            => Token(TriviaList(), SyntaxKind.InterpolatedStringTextToken, stringLiteralToken.ValueText, stringLiteralToken.ValueText, TriviaList());

        private static bool IsStringLiteralExpression(ExpressionSyntax? expression, [NotNullWhen(true)] out LiteralExpressionSyntax? literal)
        {
            if (expression is LiteralExpressionSyntax foundLiteral &&
                foundLiteral.IsKind(SyntaxKind.StringLiteralExpression) &&
                !foundLiteral.Token.IsVerbatimStringLiteral())
            {
                literal = foundLiteral;
                return true;
            }

            literal = null;
            return false;
        }

        private sealed class MyCodeAction : CodeActions.CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument)
            {
            }
        }
    }
}
