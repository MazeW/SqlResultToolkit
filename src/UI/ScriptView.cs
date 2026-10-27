using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using SqlResultToolkit.Format;
using SqlResultToolkit.Grid;

namespace SqlResultToolkit.UI
{
    /// <summary>
    /// Builds INSERT / UPDATE / DELETE / SELECT / IN-list statements from whatever is
    /// selected in the results grid, and from any values the user has staged in the Value
    /// tab.
    ///
    /// Nothing here runs against a server. The script is produced for the user to read,
    /// edit, copy, or open in a query window, and every assumption the generator had to
    /// make shows up as a WARNING comment at the top.
    /// </summary>
    internal sealed class ScriptView : UserControl
    {
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace");

        // Kept across re-renders and panel reopens within this SSMS session, because
        // retyping the table and key on every click would make the feature useless.
        private static string _rememberedTable;
        private static string _rememberedKeys;
        private static ScriptKind _rememberedKind = ScriptKind.Update;

        private Theme _theme;

        private readonly ComboBox _kindPicker = new ComboBox { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBox _tableBox = new TextBox { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBox _keyBox = new TextBox { MinWidth = 110, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _regenerateButton = new Button { Content = "Regenerate", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _copyButton = new Button { Content = "Copy", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _openButton = new Button { Content = "New query", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };

        private readonly Border _stagedBar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        private readonly TextBlock _stagedText = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _stagedList = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
        private readonly Button _clearEditsButton = new Button { Content = "Discard all", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };

        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2) };

        /// <summary>
        /// The generated script, which the user can edit before copying or running it.
        /// Tweaking a WHERE clause or commenting a row out is the normal thing to want.
        /// </summary>
        private readonly TextBox _output = new TextBox
        {
            IsReadOnly = false,
            BorderThickness = new Thickness(0),
            FontSize = 12.5,
            Padding = new Thickness(8, 6, 8, 6),
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            AcceptsTab = true,
            UndoLimit = 200,
            SpellCheck = { IsEnabled = false }
        };

        private CellSnapshot _cell;
        private bool _suppressEvents;

        /// <summary>True once the user has typed in the script box since it was generated.</summary>
        private bool _edited;

        /// <summary>Set while we replace the text ourselves, so it does not count as an edit.</summary>
        private bool _writingOutput;

        /// <summary>True when the Table box was filled by parsing the query, not by the provider.</summary>
        private bool _tableWasGuessed;

        /// <summary>What that guess was made for, so it can be redone when the selection moves.</summary>
        private object _guessedForGrid;
        private int _guessedForResultSet = -1;

        public ScriptView(Theme theme)
        {
            _theme = theme;
            _output.FontFamily = MonoFont;
            Build();
            ApplyTheme(theme);

            // Re-subscribe on every Loaded, not once in the constructor: a WPF TabControl
            // unloads the content of a tab you switch away from, so a constructor-only
            // subscription is silently dropped the first time the user leaves this tab.
            // The paired -= keeps it from doubling up.
            Loaded += (s, e) =>
            {
                PendingEdits.Changed -= OnStagedEditsChanged;
                PendingEdits.Changed += OnStagedEditsChanged;
                Regenerate(force: !_edited);
            };

            Unloaded += (s, e) => PendingEdits.Changed -= OnStagedEditsChanged;
        }

        public void SetCell(CellSnapshot cell)
        {
            _cell = cell;

            // Moving the grid selection must not throw away a script the user has started
            // editing; it keeps their text and offers Regenerate instead.
            Regenerate(force: false);
        }

        /// <summary>Called when the Value tab stages an edit, so the script rebuilds at once.</summary>
        public void ShowStagedEdits(CellSnapshot cell)
        {
            _cell = cell;

            _suppressEvents = true;
            SelectKind(ScriptKind.Update);
            _suppressEvents = false;

            Regenerate(force: true);
        }

        public void ApplyTheme(Theme theme)
        {
            _theme = theme;
            Background = theme.Background;
            Foreground = theme.Foreground;
            _output.Background = theme.Background;
            _output.Foreground = theme.Foreground;
            _output.SelectionBrush = theme.SelectionBackground;
            _output.CaretBrush = theme.Foreground;
            _summary.Foreground = theme.SubtleForeground;
            _hint.Foreground = theme.Warning;
            _stagedBar.Background = theme.ChromeBackground;
            _stagedBar.BorderBrush = theme.Border;
            _stagedText.Foreground = theme.Accent;

            foreach (var box in new[] { _tableBox, _keyBox })
            {
                box.Background = theme.ChromeBackground;
                box.Foreground = theme.Foreground;
                box.BorderBrush = theme.Border;
                box.CaretBrush = theme.Foreground;
            }
        }

        private void Build()
        {
            foreach (ScriptKind kind in Enum.GetValues(typeof(ScriptKind)))
            {
                _kindPicker.Items.Add(new ComboBoxItem { Content = Label(kind), Tag = kind });
            }

            SelectKind(_rememberedKind);
            _kindPicker.SelectionChanged += (s, e) => OnInputChanged();

            _tableBox.Text = _rememberedTable ?? string.Empty;
            _keyBox.Text = _rememberedKeys ?? string.Empty;
            _tableBox.TextChanged += (s, e) => OnInputChanged();
            _keyBox.TextChanged += (s, e) => OnInputChanged();
            _tableBox.ToolTip = "Table to script against, e.g. dbo.Orders. A plain SELECT does not report its base table, so this is usually needed.";
            _keyBox.ToolTip = "Comma-separated key column(s) for the WHERE clause, e.g. OrderId. Without one, the predicate matches on every column.";

            _regenerateButton.Click += (s, e) => Regenerate(force: true);
            _regenerateButton.IsEnabled = false;
            _regenerateButton.ToolTip = "Rebuild the script from the selection and staged edits, discarding changes typed here";
            _output.TextChanged += OnOutputChanged;

            _copyButton.Click += OnCopy;
            _copyButton.ToolTip = "Copy the script, including anything you changed here";
            _openButton.Click += OnOpenInQueryWindow;
            _openButton.ToolTip = "Open the script in a new SSMS query window";

            _clearEditsButton.ToolTip = "Drop every pending change for this grid";
            _clearEditsButton.Click += (s, e) =>
            {
                if (_cell?.SourceGrid != null)
                {
                    PendingEdits.Clear(_cell.SourceGrid);
                }

                // Rebuild here rather than relying on the Changed event, so this works
                // even if the subscription is not live for some reason.
                Regenerate(force: true);
            };

            var toolbar = new WrapPanel { Margin = new Thickness(8, 4, 8, 2) };
            toolbar.Children.Add(_kindPicker);
            toolbar.Children.Add(Label("Table"));
            toolbar.Children.Add(_tableBox);
            toolbar.Children.Add(Label("Key"));
            toolbar.Children.Add(_keyBox);
            toolbar.Children.Add(_regenerateButton);
            toolbar.Children.Add(_copyButton);
            toolbar.Children.Add(_openButton);

            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_clearEditsButton, Dock.Right);
            header.Children.Add(_clearEditsButton);
            header.Children.Add(_stagedText);

            var stagedPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            stagedPanel.Children.Add(header);
            stagedPanel.Children.Add(new ScrollViewer
            {
                Content = _stagedList,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            _stagedBar.Child = stagedPanel;

            var root = new System.Windows.Controls.Grid();
            for (int i = 0; i < 4; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            System.Windows.Controls.Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);
            System.Windows.Controls.Grid.SetRow(_stagedBar, 1);
            root.Children.Add(_stagedBar);
            System.Windows.Controls.Grid.SetRow(_hint, 2);
            root.Children.Add(_hint);
            System.Windows.Controls.Grid.SetRow(_summary, 3);
            root.Children.Add(_summary);
            System.Windows.Controls.Grid.SetRow(_output, 4);
            root.Children.Add(_output);

            Content = root;
        }

        private TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 4, 4, 0),
            Foreground = _theme.SubtleForeground
        };

        private static string Label(ScriptKind kind)
        {
            switch (kind)
            {
                case ScriptKind.Insert: return "INSERT";
                case ScriptKind.Update: return "UPDATE";
                case ScriptKind.Delete: return "DELETE";
                case ScriptKind.Select: return "SELECT";
                case ScriptKind.InList: return "IN list";
                case ScriptKind.ValueList: return "Values";
                default: return kind.ToString();
            }
        }

        private void SelectKind(ScriptKind kind)
        {
            foreach (ComboBoxItem item in _kindPicker.Items)
            {
                if ((ScriptKind)item.Tag == kind)
                {
                    _kindPicker.SelectedItem = item;
                    return;
                }
            }
        }

        private ScriptKind CurrentKind =>
            _kindPicker.SelectedItem is ComboBoxItem item && item.Tag is ScriptKind kind ? kind : ScriptKind.Update;

        private void OnStagedEditsChanged(object sender, EventArgs e) => Regenerate(force: true);

        private void OnInputChanged()
        {
            if (_suppressEvents)
            {
                return;
            }

            Remember();

            // Changing the kind, table or key IS a request for a new script, so this
            // overwrites edits typed into the box. Ctrl+Z brings them back.
            Regenerate(force: true);
        }

        private void Remember()
        {
            _rememberedTable = _tableBox.Text;
            _rememberedKeys = _keyBox.Text;
            _rememberedKind = CurrentKind;
        }

        /// <summary>Replaces the script without counting as a user edit.</summary>
        private void SetOutput(string text)
        {
            _writingOutput = true;
            try
            {
                _output.Text = text;
            }
            finally
            {
                _writingOutput = false;
            }

            _edited = false;
            _regenerateButton.IsEnabled = false;
        }

        private void OnOutputChanged(object sender, TextChangedEventArgs e)
        {
            if (_writingOutput || _edited)
            {
                return;
            }

            _edited = true;
            _regenerateButton.IsEnabled = true;
            _summary.Text = "Edited here. Copy and New query use what is in the box; Regenerate rebuilds it.";
        }

        private void Regenerate(bool force)
        {
            if (_edited && !force)
            {
                _regenerateButton.IsEnabled = true;
                _summary.Text = "Edited here — the grid selection has moved since. Regenerate to rebuild from it.";
                return;
            }

            try
            {
                var grid = _cell?.SourceGrid;
                if (grid == null || grid.IsDisposed)
                {
                    UpdateStagedBar(null);
                    SetOutput("-- Select a cell in a results grid.");
                    _summary.Text = string.Empty;
                    _hint.Text = "Run a query, click a cell, then come back here.";
                    _hint.Visibility = Visibility.Visible;
                    return;
                }

                var staged = PendingEdits.For(grid);
                UpdateStagedBar(staged);

                // The typed keys go in before the read, so those columns are fetched even
                // when they are neither selected nor flagged as keys by the provider.
                // The staged rows go in for the same reason: their key values are needed
                // even when the selection has since moved elsewhere.
                var keys = ParseKeys(_keyBox.Text);
                var stagedRows = staged.Select(x => x.Row).Distinct().ToList();

                var selection = SelectionReader.ReadSelection(grid, keys, stagedRows);
                if (selection == null)
                {
                    SetOutput("-- Nothing selected, or this grid exposes no result-set metadata.");
                    _summary.Text = string.Empty;
                    return;
                }

                selection.TableOverride = _tableBox.Text;
                selection.KeyColumnOverride = keys;
                PrefillFromProvider(selection);

                if (staged.Count > 0)
                {
                    var map = new Dictionary<long, Dictionary<string, string>>();
                    foreach (var edit in staged)
                    {
                        if (!map.TryGetValue(edit.Row, out var byColumn))
                        {
                            byColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            map[edit.Row] = byColumn;
                        }

                        byColumn[edit.ColumnName] = edit.Literal;
                    }

                    selection.StagedEdits = map;
                }

                SetOutput(ScriptGenerator.Generate(selection, CurrentKind));
                _summary.Text = Describe(selection, staged.Count);
                UpdateHint(selection, staged.Count);
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Script generation failed.", ex);
                SetOutput("-- Could not build the script: " + ex.Message);
                _summary.Text = string.Empty;
            }
        }

        /// <summary>
        /// Lists the pending changes as "row N · Column   old → new", each with its own
        /// remove button. A one-line summary was too cramped to actually read, and the
        /// whole point of staging is that you can see what you have queued up.
        /// </summary>
        private void UpdateStagedBar(IReadOnlyList<PendingEdit> staged)
        {
            _stagedList.Children.Clear();

            if (staged == null || staged.Count == 0)
            {
                _stagedBar.Visibility = Visibility.Collapsed;
                return;
            }

            int rows = staged.Select(e => e.Row).Distinct().Count();
            _stagedText.Text = $"{staged.Count} pending change{(staged.Count == 1 ? "" : "s")} on " +
                               $"{rows} row{(rows == 1 ? "" : "s")}" +
                               (CurrentKind == ScriptKind.Update ? string.Empty : "   (only UPDATE uses them)");

            foreach (var edit in staged)
            {
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };

                var remove = new Button
                {
                    Content = "×",
                    Width = 18,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "Drop this change",
                    Tag = edit
                };
                remove.Click += OnRemoveOne;
                DockPanel.SetDock(remove, Dock.Right);
                line.Children.Add(remove);

                var where = new TextBlock
                {
                    Text = $"row {edit.Row + 1} · {edit.ColumnName}",
                    Foreground = _theme.SubtleForeground,
                    MinWidth = 150,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                DockPanel.SetDock(where, Dock.Left);
                line.Children.Add(where);

                var change = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                change.Inlines.Add(new System.Windows.Documents.Run(Shorten(edit.OldDisplay))
                {
                    Foreground = _theme.SubtleForeground,
                    TextDecorations = TextDecorations.Strikethrough
                });
                change.Inlines.Add(new System.Windows.Documents.Run("  →  ") { Foreground = _theme.SubtleForeground });
                change.Inlines.Add(new System.Windows.Documents.Run(Shorten(edit.Display))
                {
                    Foreground = edit.Parsed ? _theme.Accent : _theme.Warning
                });

                if (!edit.Parsed)
                {
                    change.ToolTip = "This text did not parse as the column's type and was quoted as a string.";
                }

                line.Children.Add(change);
                _stagedList.Children.Add(line);
            }

            _stagedBar.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// One line telling the user the next useful thing to do. The panel has a lot of
        /// controls and none of them explain themselves, so the two cases that actually
        /// block a runnable script get said out loud.
        /// </summary>
        private void UpdateHint(ScriptSelection selection, int stagedCount)
        {
            string text = null;

            if (string.IsNullOrWhiteSpace(_tableBox.Text) && !selection.HasProviderTable)
            {
                text = "Set Table above to name the target — a plain SELECT does not tell SSMS which table its rows came from.";
            }
            else if (_tableWasGuessed)
            {
                text = "Table was read from your query text, not from the server — check it before running the script.";
            }
            else if (ParseKeys(_keyBox.Text).Count == 0 && !selection.HasProviderKeys)
            {
                text = "Set Key above (e.g. Id) so each statement matches one row. Without it the WHERE clause lists every column.";
            }
            else if (stagedCount == 0 && CurrentKind == ScriptKind.Update)
            {
                text = "Tip: on the Value tab, press Edit, retype a value and press Add change. Do that on as many cells as you like and they all land in one script.";
            }

            _hint.Text = text ?? string.Empty;
            _hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnRemoveOne(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PendingEdit edit)
            {
                PendingEdits.Remove(edit.Grid, edit.Row, edit.DataColumn);
                Regenerate(force: true);
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "''";
            }

            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 28 ? text : text.Substring(0, 28) + "…";
        }

        /// <summary>
        /// Fills the Table box from the provider the first time we see a result set that
        /// actually reports one, so the common case needs no typing at all.
        /// </summary>
        private void PrefillFromProvider(ScriptSelection selection)
        {
            object grid = _cell?.SourceGrid;
            int resultSet = _cell?.ResultSetIndex ?? 0;
            bool movedResultSet = !ReferenceEquals(grid, _guessedForGrid) || resultSet != _guessedForResultSet;

            // A name the user typed is theirs and is left alone. A name we guessed is
            // re-guessed when the selection moves to a different result set, since the
            // second SELECT in a batch almost certainly came from a different table.
            if (!string.IsNullOrWhiteSpace(_tableBox.Text) && !(_tableWasGuessed && movedResultSet))
            {
                return;
            }

            string name = null;
            var column = selection.Columns.FirstOrDefault(c => !c.HasNoBaseTable);

            if (column != null)
            {
                name = string.IsNullOrEmpty(column.BaseSchema)
                    ? column.BaseTable
                    : column.BaseSchema + "." + column.BaseTable;
                _tableWasGuessed = false;
            }
            else
            {
                // Nothing from the provider, which is the normal case. Read the table out
                // of the query the user actually wrote, and mark it as a guess so the hint
                // can say so.
                name = SqlTableFinder.ForResultSet(EditorText.GetActiveDocumentText(), resultSet);
                _tableWasGuessed = name != null;
                _guessedForGrid = grid;
                _guessedForResultSet = resultSet;
            }

            if (name == null)
            {
                return;
            }

            _suppressEvents = true;
            _tableBox.Text = name;
            _suppressEvents = false;
            selection.TableOverride = name;

            var keys = selection.Columns.Where(c => c.IsKey).Select(c => c.TargetName).ToList();
            if (keys.Count > 0 && string.IsNullOrWhiteSpace(_keyBox.Text))
            {
                _suppressEvents = true;
                _keyBox.Text = string.Join(", ", keys);
                _suppressEvents = false;
            }

            Remember();
        }

        private static List<string> ParseKeys(string text) =>
            (text ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Trim('[', ']'))
                .Where(p => p.Length > 0)
                .ToList();

        private string Describe(ScriptSelection selection, int stagedCount)
        {
            var parts = new List<string>();

            if (stagedCount > 0 && CurrentKind == ScriptKind.Update)
            {
                parts.Add($"built from {stagedCount:N0} staged edit(s)");
            }
            else
            {
                parts.Add($"{selection.Rows.Count:N0} row(s) × {selection.SelectedColumns.Count:N0} selected column(s)");
            }

            if (!selection.HasProviderTable && string.IsNullOrWhiteSpace(_tableBox.Text))
            {
                parts.Add("no base table reported — set Table above");
            }

            if (!selection.HasProviderKeys && ParseKeys(_keyBox.Text).Count == 0)
            {
                parts.Add("no key reported — set Key above");
            }

            return string.Join("   •   ", parts);
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_output.Text))
                {
                    Clipboard.SetText(_output.Text);
                    _summary.Text = $"Copied {_output.Text.Length:N0} characters.";
                }
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Copying the script failed.", ex);
                _summary.Text = "Could not copy: " + ex.Message;
            }
        }

        /// <summary>
        /// Writes the script to a temp .sql file and opens it in SSMS. The new window is
        /// not connected to anything; that is deliberate, since connecting it would mean
        /// guessing which server the user wants a DELETE pointed at.
        /// </summary>
        private void OnOpenInQueryWindow(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (string.IsNullOrWhiteSpace(_output.Text))
                {
                    return;
                }

                string directory = Path.Combine(Path.GetTempPath(), "SqlResultToolkit");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(
                    directory,
                    "Script_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql");

                File.WriteAllText(path, _output.Text, new UTF8Encoding(true));

                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
                _summary.Text = "Opened " + Path.GetFileName(path) + " (not connected to a server).";
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Opening the script in a query window failed.", ex);
                _summary.Text = "Could not open a query window: " + ex.Message + ". Use Copy instead.";
            }
        }
    }
}
