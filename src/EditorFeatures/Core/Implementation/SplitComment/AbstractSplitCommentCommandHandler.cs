﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.SplitComment
{
    internal abstract class AbstractSplitCommentCommandHandler : ICommandHandler<ReturnKeyCommandArgs>
    {
        protected readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        protected readonly IEditorOperationsFactoryService _editorOperationsFactoryService;

        protected AbstractSplitCommentCommandHandler(ITextUndoHistoryRegistry undoHistoryRegistry, IEditorOperationsFactoryService editorOperationsFactoryService)
        {
            _undoHistoryRegistry = undoHistoryRegistry;
            _editorOperationsFactoryService = editorOperationsFactoryService;
        }

        protected abstract string CommentStart { get; }
        // protected abstract AbstractCommentSplitter CreateCommentSplitter(Document document, SyntaxNode root, DocumentOptionSet options, int position, CancellationToken cancellationToken);

        public string DisplayName => EditorFeaturesResources.Split_comment;

        public CommandState GetCommandState(ReturnKeyCommandArgs args)
            => CommandState.Unspecified;

        public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext context)
        {
            var textView = args.TextView;
            var subjectBuffer = args.SubjectBuffer;
            var spans = textView.Selection.GetSnapshotSpansOnBuffer(subjectBuffer);

            // Don't split comments if there is any actual selection.
            if (spans.Count != 1 || !spans[0].IsEmpty)
                return false;

            var position = spans[0].Start;
            // Quick check.  If the line doesn't contain a comment in it before the caret,
            // then no point in doing any more expensive synchronous work.
            var line = subjectBuffer.CurrentSnapshot.GetLineFromPosition(position);
            if (!LineProbablyContainsComment(line, position))
                return false;

            using (context.OperationContext.AddScope(allowCancellation: true, EditorFeaturesResources.Split_comment))
            {
                var cancellationToken = context.OperationContext.UserCancellationToken;
                return SplitCommentAsync(textView, subjectBuffer, position, cancellationToken).WaitAndGetResult(cancellationToken);
            }
        }

        private bool LineProbablyContainsComment(ITextSnapshotLine line, int caretPosition)
        {
            var commentStart = this.CommentStart;

            var end = Math.Max(caretPosition, line.Length);
            for (var i = 0; i < end; i++)
            {
                if (MatchesCommentStart(line, commentStart, i))
                    return true;
            }

            return false;
        }

        private static bool MatchesCommentStart(ITextSnapshotLine line, string commentStart, int index)
        {
            var lineStart = line.Start;
            for (var c = 0; c < commentStart.Length; c++)
            {
                if (line.Snapshot[lineStart + index] != commentStart[c])
                    return false;
            }

            return true;
        }

        private async Task<bool> SplitCommentAsync(
            ITextView textView, ITextBuffer subjectBuffer, int position, CancellationToken cancellationToken)
        {
            var document = subjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
                return false;

            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var enabled = options.GetOption(SplitCommentOptions.Enabled);
            if (!enabled)
                return false;

            var root = document.GetSyntaxRootSynchronously(cancellationToken);
            Contract.ThrowIfNull(root);

            var syntaxKinds = document.GetRequiredLanguageService<ISyntaxKindsService>();
            var trivia = root.FindTrivia(position);
            if (syntaxKinds.SingleLineCommentTrivia != trivia.RawKind)
                return false;

            // We're inside a comment.  Instead of inserting just a newline here, insert
            // 1. a newline
            // 2. spaces up to the indentation of the current comment
            // 3. the comment prefix
            var textSnapshot = subjectBuffer.CurrentSnapshot;
            var triviaLine = textSnapshot.GetLineFromPosition(trivia.SpanStart);
            var commentStartColumn = triviaLine.GetColumnFromLineOffset(trivia.SpanStart - triviaLine.Start, textView.Options);

            var useTabs = options.GetOption(FormattingOptions.UseTabs);
            var tabSize = options.GetOption(FormattingOptions.TabSize);
            var insertionText =
                options.GetOption(FormattingOptions.NewLine) +
                commentStartColumn.CreateIndentationString(useTabs, tabSize) +
                this.CommentStart;

            using var transaction = CaretPreservingEditTransaction.TryCreate(
                EditorFeaturesResources.Split_comment, textView, _undoHistoryRegistry, _editorOperationsFactoryService);

            subjectBuffer.Insert(position, insertionText);

            transaction.Complete();
            return true;
        }
    }
}
