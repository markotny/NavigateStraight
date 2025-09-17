using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.IO;
using System.Linq;
using System.Threading;

namespace NavigateStraight
{
	[Command(PackageIds.ToFile)]
	internal sealed class ToFile : BaseCommand<ToFile>
	{
		/// <summary>
		/// Find the definition files for selected symbol and navigate to it.
		/// - If caret is on a declaration and there are exactly two files, switch directly to the other file.
		/// - If caret is on a declaration and there are multiple parts, delegate to the built-in picker (shows ALL parts, including generated).
		/// - Otherwise, prefer user-authored files by ignoring generated files (*.g.cs, *.g.i.cs). If exactly one remains, navigate directly.
		/// - Falls back to __Edit.GoToDefinition__ in other cases.
		/// </summary>
		protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
		{
			try
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

				var view = await VS.Documents.GetActiveDocumentViewAsync();
				if (view is null || view.TextView is null)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				var textView = view.TextView;
				var caretPoint = textView.Caret.Position.BufferPosition;
				var textBuffer = textView.TextBuffer;

				// Get Roslyn Document from the active buffer (EditorFeatures.Text provides AsTextContainer)
				var container = textBuffer.AsTextContainer();
				if (!Workspace.TryGetWorkspace(container, out var workspace))
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				var docId = workspace.GetDocumentIdInCurrentContext(container);
				if (docId == null)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				var document = workspace.CurrentSolution.GetDocument(docId);
				if (document == null)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				var ct = CancellationToken.None;

				// Find the symbol at the caret (behaves like Go To Definition)
				var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, caretPoint.Position, ct).ConfigureAwait(false);
				if (symbol == null)
				{
					// Fallback: try declared symbol
					var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
					var token = root?.FindToken(caretPoint.Position);
					var node = token?.Parent;
					if (node != null)
					{
						var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
						symbol = model?.GetDeclaredSymbol(node, ct) ?? model?.GetSymbolInfo(node, ct).Symbol;
					}
				}

				if (symbol == null)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				// All source locations (handles partials)
				var locations = symbol.OriginalDefinition.Locations.Where(l => l.IsInSource).ToArray();
				if (locations.Length == 0)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				// Special-case: caret is on declaration
				if (IsCaretOnAnyDeclaration(locations, view, textView))
				{
					// If there are exactly two distinct files, jump straight to the other file.
					var currentPath = view.FilePath ?? string.Empty;
					var distinctFiles = locations
						.Select(l => l.GetLineSpan().Path)
						.Where(p => !string.IsNullOrEmpty(p))
						.Distinct(System.StringComparer.OrdinalIgnoreCase)
						.ToArray();

					if (distinctFiles.Length == 2)
					{
						var target = locations.FirstOrDefault(l => !IsSamePath(l.GetLineSpan().Path, currentPath));
						if (target != null)
						{
							await NavigateToAsync(target).ConfigureAwait(false);
							return;
						}
					}

					// Otherwise (more than two, or two locations in the same file), show the built-in picker.
					if (locations.Length > 1)
					{
						await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
						return;
					}
				}

				// Otherwise, filter out generated files (*.g.cs, *.g.i.cs)
				var nonGenerated = locations
					.Where(l =>
					{
						var path = l.SourceTree?.FilePath;
						return !IsGeneratedFile(path);
					})
					.ToArray();

				if (nonGenerated.Length == 0)
				{
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}

				if (nonGenerated.Length == 1)
				{
					await NavigateToAsync(nonGenerated[0]).ConfigureAwait(false);
					return;
				}

				// Multiple user-authored locations: let VS show the standard picker
				await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
			}
			catch
			{
				await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
			}
		}

		private static bool IsGeneratedFile(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			var fileName = Path.GetFileName(path);
			return fileName.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase)
				|| fileName.EndsWith(".g.i.cs", System.StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsSamePath(string? a, string? b) =>
			!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
			string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

		private static async Task NavigateToAsync(Location location)
		{
			var span = location.GetLineSpan();
			var filePath = span.Path;
			var line = span.StartLinePosition.Line;
			var character = span.StartLinePosition.Character;

			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			var view = await VS.Documents.OpenAsync(filePath);
			var snapshot = view.TextView.TextBuffer.CurrentSnapshot;

			line = System.Math.Min(System.Math.Max(line, 0), snapshot.LineCount - 1);
			var snapLine = snapshot.GetLineFromLineNumber(line);
			var column = System.Math.Min(System.Math.Max(character, 0), snapLine.Length);
			var point = snapLine.Start + column;

			var caret = new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, point);
			view.TextView.Caret.MoveTo(caret);
			view.TextView.ViewScroller.EnsureSpanVisible(new Microsoft.VisualStudio.Text.SnapshotSpan(caret, 0));
		}

		private static bool IsCaretOnAnyDeclaration(Location[] locations, DocumentView view, Microsoft.VisualStudio.Text.Editor.ITextView textView)
		{
			foreach (var loc in locations)
			{
				if (!loc.IsInSource) continue;

				var lineSpan = loc.GetLineSpan();
				if (!string.Equals(lineSpan.Path, view.FilePath, System.StringComparison.OrdinalIgnoreCase))
					continue;

				var caretPos = textView.Caret.Position.BufferPosition.Position;
				var snapshot = textView.TextBuffer.CurrentSnapshot;
				var caretLine = snapshot.GetLineNumberFromPosition(caretPos);
				var caretLineStart = snapshot.GetLineFromLineNumber(caretLine).Start.Position;
				var caretColumn = caretPos - caretLineStart;

				var start = lineSpan.StartLinePosition;
				var end = lineSpan.EndLinePosition;

				var inRange =
					(caretLine > start.Line || (caretLine == start.Line && caretColumn >= start.Character)) &&
					(caretLine < end.Line || (caretLine == end.Line && caretColumn <= end.Character));

				if (inRange) return true;
			}
			return false;
		}
	}
}
