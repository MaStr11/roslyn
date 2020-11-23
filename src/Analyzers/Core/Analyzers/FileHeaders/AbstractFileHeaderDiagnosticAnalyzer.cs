﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FileHeaders
{
    internal abstract class AbstractFileHeaderDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    {
        protected AbstractFileHeaderDiagnosticAnalyzer(string language)
            : base(
                IDEDiagnosticIds.FileHeaderMismatch,
                CodeStyleOptions2.FileHeaderTemplate,
                language,
                new LocalizableResourceString(nameof(AnalyzersResources.The_file_header_is_missing_or_not_located_at_the_top_of_the_file), AnalyzersResources.ResourceManager, typeof(AnalyzersResources)),
                new LocalizableResourceString(nameof(AnalyzersResources.A_source_file_is_missing_a_required_header), AnalyzersResources.ResourceManager, typeof(AnalyzersResources)))
        {
            RoslynDebug.AssertNotNull(DescriptorId);

            var invalidHeaderTitle = new LocalizableResourceString(nameof(AnalyzersResources.The_file_header_does_not_match_the_required_text), AnalyzersResources.ResourceManager, typeof(AnalyzersResources));
            var invalidHeaderMessage = new LocalizableResourceString(nameof(AnalyzersResources.A_source_file_contains_a_header_that_does_not_match_the_required_text), AnalyzersResources.ResourceManager, typeof(AnalyzersResources));
            InvalidHeaderDescriptor = CreateDescriptorWithId(DescriptorId, invalidHeaderTitle, invalidHeaderMessage);
        }

        protected abstract ISyntaxFacts SyntaxFacts { get; }

        internal DiagnosticDescriptor MissingHeaderDescriptor => Descriptor;

        internal DiagnosticDescriptor InvalidHeaderDescriptor { get; }

        public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
            => DiagnosticAnalyzerCategory.SyntaxTreeWithoutSemanticsAnalysis;

        protected override void InitializeWorker(AnalysisContext context)
            => context.RegisterSyntaxTreeAction(HandleSyntaxTree);

        private void HandleSyntaxTree(SyntaxTreeAnalysisContext context)
        {
            var tree = context.Tree;
            var root = tree.GetRoot(context.CancellationToken);

            // don't process empty files
            if (root.FullSpan.IsEmpty)
            {
                return;
            }

            if (!context.Options.TryGetEditorConfigOption<string>(CodeStyleOptions2.FileHeaderTemplate, tree, out var fileHeaderTemplate)
                || string.IsNullOrEmpty(fileHeaderTemplate))
            {
                return;
            }

            var fileHeader = ParseFileHeader(root);
            if (fileHeader.IsMissing)
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingHeaderDescriptor, fileHeader.GetLocation(tree)));
                return;
            }

            var expectedFileHeader = fileHeaderTemplate.Replace("{fileName}", Path.GetFileName(tree.FilePath));

            // Compare the current fileHeader with the expected file header, assuming the expected file header is written as text
            // e.g. file_header_template = Copyright
            if (!CompareCopyrightText(expectedFileHeader, fileHeader.CopyrightText))
            {
                // Compare the current fileHeader with the expected file header, assuming the expected file header is written as a comment
                // e.g. file_header_template = /* Copyright */
                var sourceText = tree.GetText(context.CancellationToken);
                sourceText = sourceText.GetSubText(fileHeader.HeaderSpan);
                if (!CompareCopyrightText(expectedFileHeader, sourceText.ToString()))
                {
                    // The existing file header doesn't match the file_header_template
                    context.ReportDiagnostic(Diagnostic.Create(InvalidHeaderDescriptor, fileHeader.GetLocation(tree)));
                    return;
                }
            }
        }

        private static bool CompareCopyrightText(string expectedFileHeader, string copyrightText)
        {
            var reformattedCopyrightTextParts = NormalizeCopyrightText(expectedFileHeader).Split('\n');
            var fileHeaderCopyrightTextParts = NormalizeCopyrightText(copyrightText).Split('\n');

            if (reformattedCopyrightTextParts.Length != fileHeaderCopyrightTextParts.Length)
            {
                return false;
            }

            // compare line by line, ignoring leading and trailing whitespace on each line.
            for (var i = 0; i < reformattedCopyrightTextParts.Length; i++)
            {
                if (string.CompareOrdinal(reformattedCopyrightTextParts[i].Trim(), fileHeaderCopyrightTextParts[i].Trim()) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Make sure that both \n and \r\n are accepted from the settings and leading and trailing whitespace is ignored.
        /// </summary>
        /// <param name="original">The original header text from the document or editorconfig</param>
        /// <returns>A normalized text for comparison.</returns>
        private static string NormalizeCopyrightText(string original)
            => original.Trim().Replace("\r\n", "\n");

        private FileHeader ParseFileHeader(SyntaxNode root)
        {
            var banner = SyntaxFacts.GetFileBanner(root);
            if (banner.Length == 0)
            {
                return GetMissingHeader(root);
            }

            using var _ = PooledStringBuilder.GetInstance(out var sb);
            int? fileHeaderStart = null;
            int? fileHeaderEnd = null;
            string? commentPrefix = null;

            foreach (var trivia in banner)
            {
                if (SyntaxFacts.IsRegularComment(trivia))
                {
                    var comment = SyntaxFacts.GetCommentText(trivia);
                    fileHeaderStart ??= trivia.FullSpan.Start;
                    fileHeaderEnd = trivia.FullSpan.End;
                    commentPrefix ??= SyntaxFacts.GetCommentPrefix(trivia);

                    sb.AppendLine(comment.Trim());
                }
            }

            return fileHeaderStart is int start && fileHeaderEnd is int end && commentPrefix is string { Length: var commentPrefixLength }
                ? new FileHeader(sb.ToString(), start, end, commentPrefixLength)
                : GetMissingHeader(root);

            static FileHeader GetMissingHeader(SyntaxNode root)
            {
                var missingHeaderOffset = root.GetLeadingTrivia().FirstOrDefault(t => t.IsDirective).FullSpan.End;
                return FileHeader.MissingFileHeader(missingHeaderOffset);
            }
        }
    }
}
