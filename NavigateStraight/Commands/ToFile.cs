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
		/// Rules (manual and generated treated equally, focus on exact file-name match):
		/// 1) Caret NOT on declaration:
		///    - Go to exact match if it exists (TypeName.cs / TypeName.g.cs / TypeName.g.i.cs).
		///    - Else, if exactly one manual file exists, go there.
		///    - Else, fallback to __Edit.GoToDefinition__.
		/// 2) Caret ON declaration:
		///    - Go to exact match if it exists and is not the current file.
		///    - Else, if there are exactly two files, switch to the other file.
		///    - Else, fallback to __Edit.GoToDefinition__.
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

				var onDeclaration = IsCaretOnAnyDeclaration(locations, view, textView);
				var typeName = GetPreferredTypeName(symbol);
				var exactMatches = FindAllExactMatches(locations, typeName);

				if (!onDeclaration)
				{
					// 1) Not on declaration
					if (exactMatches.Length == 1)
					{
						await NavigateToAsync(exactMatches[0]).ConfigureAwait(false);
						return;
					}
					else if (exactMatches.Length > 1)
					{
						// Ambiguous exact matches -> fallback
						await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
						return;
					}

					// No exact match: if there's exactly one manual file, go there; else fallback
					var manual = locations.Where(l => !IsGeneratedFile(l.SourceTree?.FilePath)).ToArray();
					if (manual.Length == 1)
					{
						await NavigateToAsync(manual[0]).ConfigureAwait(false);
						return;
					}

					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}
				else
				{
					// 2) On declaration
					if (exactMatches.Length > 0)
					{
						// Prefer any exact match that isn't the current file
						var currentPath = view.FilePath ?? string.Empty;
						var target = exactMatches.FirstOrDefault(l => !IsSamePath(l.GetLineSpan().Path, currentPath));
						if (target != null)
						{
							await NavigateToAsync(target).ConfigureAwait(false);
							return;
						}
						// If all exact matches are the current file, fall through to two-file toggle/fallback
					}

					// Exactly two distinct files? Toggle to the other one.
					var current = view.FilePath ?? string.Empty;
					var distinctFiles = locations
						.Select(l => l.GetLineSpan().Path)
						.Where(p => !string.IsNullOrEmpty(p))
						.Distinct(System.StringComparer.OrdinalIgnoreCase)
						.ToArray();

					if (distinctFiles.Length == 2)
					{
						var target = locations.FirstOrDefault(l => !IsSamePath(l.GetLineSpan().Path, current));
						if (target != null)
						{
							await NavigateToAsync(target).ConfigureAwait(false);
							return;
						}
					}

					// Fallback to built-in
					await VS.Commands.ExecuteAsync("Edit.GoToDefinition");
					return;
				}
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

		private static string? GetPreferredTypeName(ISymbol symbol)
		{
			// Prefer declared type name; otherwise use containing type; fallback to symbol name
			if (symbol is INamedTypeSymbol nts) return nts.Name;
			return symbol.ContainingType?.Name ?? symbol.Name;
		}

		private static Location[] FindAllExactMatches(Location[] locations, string? typeName)
		{
			if (string.IsNullOrEmpty(typeName)) return [];
			return locations
				.Where(l =>
				{
					var path = l.SourceTree?.FilePath;
					if (string.IsNullOrEmpty(path)) return false;
					var fileName = Path.GetFileName(path);
					// Treat manual and generated equally, allow exact TypeName.cs / .g.cs / .g.i.cs
					return fileName.Equals(typeName + ".cs", System.StringComparison.OrdinalIgnoreCase)
						|| fileName.Equals(typeName + ".g.cs", System.StringComparison.OrdinalIgnoreCase)
						|| fileName.Equals(typeName + ".g.i.cs", System.StringComparison.OrdinalIgnoreCase);
				})
				.ToArray();
		}

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
