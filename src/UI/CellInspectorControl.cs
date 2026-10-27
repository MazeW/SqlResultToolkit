using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using SqlCellInspector.Format;
using SqlCellInspector.Grid;

namespace SqlCellInspector.UI
{
    /// <summary>
    /// The panel. Built in code rather than XAML so the assembly needs no markup compiler
    /// and nothing but the WPF reference assemblies to build.
    ///
    /// Three tabs: the cell's value, the whole row, and generated T-SQL.
    /// </summary>
    internal sealed class CellInspectorControl : UserControl
    {
        private const int MaxColourizedTokens = 60000;

        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace");

        // Remembered for the life of the SSMS session so the panel does not reset its
        // view mode every time the selection moves.
        private static ViewMode _rememberedMode = ViewMode.Auto;
        private static bool _rememberedWrap = true;
        private static int _rememberedTab;

        private Theme _theme;

        private readonly TextBlock _columnText = new TextBlock();
        private readonly TextBlock _typeText = new TextBlock();
        private readonly StackPanel _badges = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox _modePicker = new ComboBox { MinWidth = 86, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ToggleButton _wrapToggle = new ToggleButton { Content = "Wrap", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly ToggleButton _pinToggle = new ToggleButton { Content = "Pin", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly ToggleButton _editToggle = new ToggleButton { Content = "Edit", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _copyButton = new Button { Content = "Copy", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _saveButton = new Button { Content = "Save…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _scriptValueButton = new Button { Content = "Add change", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
        private readonly Button _unstageButton = new Button { Content = "Remove change", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
        private readonly CheckBox _nullCheck = new CheckBox { Content = "NULL", Margin = new Thickness(4, 5, 8, 0), Visibility = Visibility.Collapsed };

        private readonly ContentControl _contentHost = new ContentControl();
        private readonly TextBlock _noteText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
        private readonly StackPanel _factsPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        private readonly Expander _detailsExpander = new Expander { Header = "Details", IsExpanded = true };
        private readonly TextBlock _statusText = new TextBlock { Margin = new Thickness(8, 3, 8, 3) };
        private readonly Border _headerBorder = new Border();
        private readonly Border _toolbarBorder = new Border();
        private readonly Border _statusBorder = new Border();

        // Problems surface here rather than only in the activity log, which SSMS does not
        // write unless it was started with /log.
        private readonly Border _problemsBorder = new Border { Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0, 1, 0, 0) };
        private readonly TextBlock _problemsText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _problemsCopy = new Button { Content = "Copy", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _problemsDismiss = new Button { Content = "Dismiss", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
        private readonly ToggleButton _problemsDetails = new ToggleButton { Content = "Details", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
        private readonly TextBox _problemsList = new TextBox
        {
            IsReadOnly = true,
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            MaxHeight = 160,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            UndoLimit = 0
        };
        private readonly GridSplitter _splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };

        private readonly TabControl _tabs = new TabControl();
        private readonly TabItem _valueTab = new TabItem { Header = "Value" };
        private readonly TabItem _rowTab = new TabItem { Header = "Row" };
        private readonly TabItem _scriptTab = new TabItem { Header = "Script" };

        private RowView _rowView;
        private ScriptView _scriptView;

        private RenderedCell _rendered;
        private CellSnapshot _cell;
        private ViewMode _requestedMode = _rememberedMode;
        private bool _suppressModeEvent;
        private TextBox _editBox;

        public CellInspectorControl()
        {
            _theme = Theme.Current();
            CellHighlighter.SetDark(_theme.IsDark);
            _rowView = new RowView(_theme);
            _scriptView = new ScriptView(_theme);

            Build();
            ApplyTheme();
            ShowMessage("Open a query window, run a SELECT, then click a cell in the results grid.", null);

            // Paired -= before += so re-entering Loaded cannot double-subscribe.
            Loaded += (s, e) =>
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                VSColorTheme.ThemeChanged += OnThemeChanged;
                PendingEdits.Changed -= OnPendingEditsChanged;
                PendingEdits.Changed += OnPendingEditsChanged;
                Diagnostics.Changed -= OnDiagnosticsChanged;
                Diagnostics.Changed += OnDiagnosticsChanged;
                UpdateProblems();
            };

            Unloaded += (s, e) =>
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                PendingEdits.Changed -= OnPendingEditsChanged;
                Diagnostics.Changed -= OnDiagnosticsChanged;
            };
        }

        /// <summary>Raised when the user pins or unpins, so the monitor can stop following.</summary>
        public event EventHandler<bool> PinnedChanged;

        // ---- layout -----------------------------------------------------------------

        private void Build()
        {
            _columnText.FontWeight = FontWeights.SemiBold;
            _columnText.TextTrimming = TextTrimming.CharacterEllipsis;
            _typeText.Margin = new Thickness(0, 1, 0, 0);
            _typeText.TextTrimming = TextTrimming.CharacterEllipsis;

            var headerStack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            headerStack.Children.Add(_columnText);
            headerStack.Children.Add(_typeText);
            headerStack.Children.Add(_badges);
            _headerBorder.Child = headerStack;
            _headerBorder.BorderThickness = new Thickness(0, 0, 0, 1);

            _valueTab.Content = BuildValueTab();
            _rowTab.Content = _rowView;
            _scriptTab.Content = _scriptView;

            _tabs.Items.Add(_valueTab);
            _tabs.Items.Add(_rowTab);
            _tabs.Items.Add(_scriptTab);
            _tabs.SelectedIndex = Math.Min(_rememberedTab, 2);
            _tabs.SelectionChanged += OnTabChanged;
            _tabs.BorderThickness = new Thickness(0);

            _problemsList.FontFamily = MonoFont;
            _problemsList.FontSize = 11.5;

            _problemsCopy.Click += (s2, e) => CopyProblems();
            _problemsCopy.ToolTip = "Copy every recorded problem to the clipboard";
            _problemsDismiss.Click += (s2, e) => Diagnostics.Clear();
            _problemsDismiss.ToolTip = "Clear the recorded problems";
            _problemsDetails.Checked += (s2, e) => UpdateProblems();
            _problemsDetails.Unchecked += (s2, e) => UpdateProblems();

            var problemButtons = new StackPanel { Orientation = Orientation.Horizontal };
            problemButtons.Children.Add(_problemsDetails);
            problemButtons.Children.Add(_problemsCopy);
            problemButtons.Children.Add(_problemsDismiss);

            var problemHeader = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(problemButtons, Dock.Right);
            problemHeader.Children.Add(problemButtons);
            problemHeader.Children.Add(_problemsText);

            var problemPanel = new StackPanel { Margin = new Thickness(8, 3, 8, 3) };
            problemPanel.Children.Add(problemHeader);
            problemPanel.Children.Add(_problemsList);
            _problemsBorder.Child = problemPanel;

            _statusBorder.Child = _statusText;
            _statusBorder.BorderThickness = new Thickness(0, 1, 0, 0);

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            System.Windows.Controls.Grid.SetRow(_headerBorder, 0);
            root.Children.Add(_headerBorder);
            System.Windows.Controls.Grid.SetRow(_tabs, 1);
            root.Children.Add(_tabs);
            System.Windows.Controls.Grid.SetRow(_problemsBorder, 2);
            root.Children.Add(_problemsBorder);
            System.Windows.Controls.Grid.SetRow(_statusBorder, 3);
            root.Children.Add(_statusBorder);

            Content = root;
        }

        private UIElement BuildValueTab()
        {
            foreach (ViewMode mode in Enum.GetValues(typeof(ViewMode)))
            {
                _modePicker.Items.Add(new ComboBoxItem { Content = CellRenderer.ModeLabel(mode), Tag = mode });
            }

            _modePicker.SelectedIndex = 0;
            _modePicker.SelectionChanged += OnModeChanged;
            _modePicker.ToolTip = "How to format the value. Auto picks from the column type and the content.";

            _wrapToggle.IsChecked = _rememberedWrap;
            _wrapToggle.Checked += OnWrapChanged;
            _wrapToggle.Unchecked += OnWrapChanged;
            _wrapToggle.ToolTip = "Wrap long lines";

            _pinToggle.ToolTip = "Stop following the grid selection and keep showing this value";
            _pinToggle.Checked += (s, e) => PinnedChanged?.Invoke(this, true);
            _pinToggle.Unchecked += (s, e) => PinnedChanged?.Invoke(this, false);

            _editToggle.ToolTip = "Edit the value here, then turn it into an UPDATE statement. Nothing is written to the database.";
            _editToggle.Checked += (s, e) => Rerender();
            _editToggle.Unchecked += (s, e) => Rerender();

            _copyButton.Click += OnCopy;
            _copyButton.ToolTip = "Copy the formatted value to the clipboard";
            _saveButton.Click += OnSave;
            _saveButton.ToolTip = "Save the value to a file";
            _scriptValueButton.Click += OnScriptValue;
            _scriptValueButton.ToolTip = "Remember this new value and add it to the UPDATE on the Script tab. Change as many cells as you like; nothing is written to the database.";
            _unstageButton.Click += OnUnstage;
            _unstageButton.ToolTip = "Drop this cell's pending change";
            _nullCheck.ToolTip = "Set the column to NULL instead of the typed value";

            var toolbar = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
            toolbar.Children.Add(_modePicker);
            toolbar.Children.Add(_wrapToggle);
            toolbar.Children.Add(_pinToggle);
            toolbar.Children.Add(_editToggle);
            toolbar.Children.Add(_copyButton);
            toolbar.Children.Add(_saveButton);
            toolbar.Children.Add(_nullCheck);
            toolbar.Children.Add(_scriptValueButton);
            toolbar.Children.Add(_unstageButton);
            _toolbarBorder.Child = toolbar;
            _toolbarBorder.BorderThickness = new Thickness(0, 0, 0, 1);

            var detailsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _factsPanel
            };
            _detailsExpander.Content = detailsScroll;

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // toolbar
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // note
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });  // content
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // splitter
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });  // details

            System.Windows.Controls.Grid.SetRow(_toolbarBorder, 0);
            grid.Children.Add(_toolbarBorder);
            System.Windows.Controls.Grid.SetRow(_noteText, 1);
            grid.Children.Add(_noteText);
            System.Windows.Controls.Grid.SetRow(_contentHost, 2);
            grid.Children.Add(_contentHost);
            System.Windows.Controls.Grid.SetRow(_splitter, 3);
            grid.Children.Add(_splitter);
            System.Windows.Controls.Grid.SetRow(_detailsExpander, 4);
            grid.Children.Add(_detailsExpander);

            return grid;
        }

        private void ApplyTheme()
        {
            Background = _theme.Background;
            Foreground = _theme.Foreground;

            _headerBorder.Background = _theme.ChromeBackground;
            _headerBorder.BorderBrush = _theme.Border;
            _toolbarBorder.Background = _theme.ChromeBackground;
            _toolbarBorder.BorderBrush = _theme.Border;
            _statusBorder.Background = _theme.ChromeBackground;
            _statusBorder.BorderBrush = _theme.Border;

            _columnText.Foreground = _theme.Foreground;
            _typeText.Foreground = _theme.SubtleForeground;
            _statusText.Foreground = _theme.SubtleForeground;
            _noteText.Foreground = _theme.Warning;
            _detailsExpander.Foreground = _theme.Foreground;
            _tabs.Background = _theme.Background;
            _tabs.Foreground = _theme.Foreground;
            _nullCheck.Foreground = _theme.Foreground;

            _problemsBorder.Background = _theme.ChromeBackground;
            _problemsBorder.BorderBrush = _theme.Border;
            _problemsText.Foreground = _theme.Danger;
            _problemsList.Background = _theme.Background;
            _problemsList.Foreground = _theme.SubtleForeground;
            _problemsList.SelectionBrush = _theme.SelectionBackground;

            _rowView?.ApplyTheme(_theme);
            _scriptView?.ApplyTheme(_theme);
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            _theme = Theme.Current();
            CellHighlighter.SetDark(_theme.IsDark);
            ApplyTheme();
            Rerender();
        }

        // ---- public surface ----------------------------------------------------------

        public void Show(CellSnapshot cell)
        {
            _cell = cell;

            // Leaving edit mode on across a selection change would show the previous
            // cell's text against the new cell's column.
            if (_editToggle.IsChecked == true)
            {
                _editToggle.IsChecked = false;
            }

            Rerender();
        }

        public void ShowWaiting(int gridCount)
        {
            string message = gridCount > 0
                ? $"Watching {gridCount} results grid(s). Click a cell to inspect it."
                : "No results grid open yet. Run a query, then click a cell.";
            ShowMessage(message, null);
        }

        public void ShowError(string message, Exception ex)
        {
            ShowMessage(message, ex?.Message);
        }

        private void ShowMessage(string message, string detail)
        {
            _columnText.Text = "SQL Cell Inspector";
            _typeText.Text = string.Empty;
            _badges.Children.Clear();
            _factsPanel.Children.Clear();
            _noteText.Text = detail ?? string.Empty;
            _noteText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            _statusText.Text = string.Empty;

            _contentHost.Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
                Foreground = _theme.SubtleForeground
            };
        }

        // ---- rendering ---------------------------------------------------------------

        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, _tabs))
            {
                return;   // a ComboBox inside a tab also raises SelectionChanged
            }

            _rememberedTab = _tabs.SelectedIndex;
            RefreshActiveTab();
        }

        private void RefreshActiveTab()
        {
            if (_cell == null)
            {
                return;
            }

            if (ReferenceEquals(_tabs.SelectedItem, _rowTab))
            {
                _rowView.SetCell(_cell);
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _scriptTab))
            {
                _scriptView.SetCell(_cell);
            }
        }

        private void Rerender()
        {
            if (_cell == null)
            {
                return;
            }

            _rendered = CellRenderer.Render(_cell, _requestedMode);
            UpdateTabHeaders();
            UpdateHeader(_rendered);
            UpdateModePicker(_rendered);
            UpdateContent(_rendered);
            UpdateFacts(_rendered);
            UpdateStatus(_rendered);
            RefreshActiveTab();
        }

        private void UpdateHeader(RenderedCell r)
        {
            _columnText.Text = r.Cell.ColumnName ?? "(no column)";
            _typeText.Text = r.Cell.TypeLabel + "  •  " + SqlTypeCatalog.Describe(r.Category);

            _badges.Children.Clear();

            if (r.Cell.IsNull)
            {
                AddBadge("NULL", _theme.Accent);
            }

            if (r.Cell.MayBeTruncated)
            {
                AddBadge("possibly truncated", _theme.Warning);
            }

            if (r.Cell.ValueUnavailable)
            {
                AddBadge("string form only", _theme.Warning);
            }

            if (r.ContentType != null)
            {
                AddBadge(r.ContentType, _theme.SubtleForeground);
            }

            if (r.Formatted != null && r.Formatted.Truncated)
            {
                AddBadge("view capped", _theme.Warning);
            }

            if (!string.IsNullOrEmpty(r.Cell.GridHeaderName))
            {
                AddBadge("header says \"" + r.Cell.GridHeaderName + "\"", _theme.Warning);
            }

            var staged = CurrentStagedEdit();
            if (staged != null)
            {
                AddBadge("pending change → " + (staged.Display ?? string.Empty), _theme.Accent);
            }
        }

        private void AddBadge(string text, Brush colour)
        {
            _badges.Children.Add(new Border
            {
                BorderBrush = colour,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, 3, 4, 0),
                Child = new TextBlock { Text = text, Foreground = colour, FontSize = 10.5 }
            });
        }

        private void UpdateModePicker(RenderedCell r)
        {
            _suppressModeEvent = true;
            try
            {
                foreach (ComboBoxItem item in _modePicker.Items)
                {
                    var mode = (ViewMode)item.Tag;
                    bool available = r.AvailableModes.Contains(mode);
                    item.IsEnabled = available;
                    item.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

                    if (mode == ViewMode.Auto)
                    {
                        item.Content = $"Auto ({CellRenderer.ModeLabel(r.EffectiveMode)})";
                    }

                    if (mode == _requestedMode)
                    {
                        _modePicker.SelectedItem = item;
                    }
                }
            }
            finally
            {
                _suppressModeEvent = false;
            }
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeEvent)
            {
                return;
            }

            if (_modePicker.SelectedItem is ComboBoxItem item && item.Tag is ViewMode mode)
            {
                _requestedMode = mode;
                _rememberedMode = mode;
                Rerender();
            }
        }

        private void OnWrapChanged(object sender, RoutedEventArgs e)
        {
            _rememberedWrap = _wrapToggle.IsChecked == true;
            Rerender();
        }

        private void UpdateContent(RenderedCell r)
        {
            _noteText.Text = r.Formatted?.Note ?? string.Empty;
            _noteText.Visibility = string.IsNullOrEmpty(_noteText.Text) ? Visibility.Collapsed : Visibility.Visible;

            bool editing = _editToggle.IsChecked == true;
            var staged = CurrentStagedEdit();

            _scriptValueButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            _nullCheck.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            _unstageButton.Visibility = staged != null ? Visibility.Visible : Visibility.Collapsed;
            _scriptValueButton.Content = staged != null ? "Update change" : "Add change";

            if (editing)
            {
                // Seed the box with the staged value when this cell already has one, so
                // the edit can be refined rather than retyped.
                string seed = staged?.Display ?? r.Formatted?.Text ?? string.Empty;
                _editBox = BuildPlainView(seed, _wrapToggle.IsChecked == true);
                _editBox.IsReadOnly = false;
                _editBox.UndoLimit = 100;
                _contentHost.Content = _editBox;
                return;
            }

            _editBox = null;

            if (r.EffectiveMode == ViewMode.Image && r.Bytes != null)
            {
                _contentHost.Content = BuildImageView(r.Bytes);
                return;
            }

            var formatted = r.Formatted;
            bool wrap = _wrapToggle.IsChecked == true;

            if (formatted?.Tokens != null && formatted.Tokens.Count <= MaxColourizedTokens)
            {
                _contentHost.Content = BuildColourizedView(formatted, wrap);
            }
            else
            {
                _contentHost.Content = BuildPlainView(formatted?.Text ?? string.Empty, wrap);
            }
        }

        private UIElement BuildColourizedView(FormattedValue formatted, bool wrap)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = double.NaN,
                TextAlignment = TextAlignment.Left
            };

            foreach (var token in formatted.Tokens)
            {
                // Runs cannot carry newlines in a flow document, so split them out.
                int start = 0;
                while (start <= token.Text.Length)
                {
                    int newline = token.Text.IndexOf('\n', start);
                    int end = newline < 0 ? token.Text.Length : newline;

                    if (end > start)
                    {
                        paragraph.Inlines.Add(new Run(token.Text.Substring(start, end - start))
                        {
                            Foreground = _theme.ForToken(token.Kind)
                        });
                    }

                    if (newline < 0)
                    {
                        break;
                    }

                    paragraph.Inlines.Add(new LineBreak());
                    start = newline + 1;
                }
            }

            var document = new FlowDocument(paragraph)
            {
                FontFamily = MonoFont,
                FontSize = 12.5,
                PagePadding = new Thickness(8, 6, 8, 6),
                Background = Brushes.Transparent,
                Foreground = _theme.Foreground
            };

            if (!wrap)
            {
                // A very wide page stops the layout engine from wrapping.
                document.PageWidth = 20000;
            }

            var viewer = new RichTextBox(document)
            {
                IsReadOnly = true,
                IsDocumentEnabled = false,
                BorderThickness = new Thickness(0),
                Background = _theme.Background,
                Foreground = _theme.Foreground,
                SelectionBrush = _theme.SelectionBackground,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                AcceptsReturn = false,
                AcceptsTab = false
            };

            viewer.SetValue(System.Windows.Controls.SpellCheck.IsEnabledProperty, false);
            return viewer;
        }

        private TextBox BuildPlainView(string text, bool wrap)
        {
            return new TextBox
            {
                Text = text,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                BorderThickness = new Thickness(0),
                Background = _theme.Background,
                Foreground = _theme.Foreground,
                SelectionBrush = _theme.SelectionBackground,
                CaretBrush = _theme.Foreground,
                FontFamily = MonoFont,
                FontSize = 12.5,
                Padding = new Thickness(8, 6, 8, 6),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                UndoLimit = 0
            };
        }

        private UIElement BuildImageView(byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();

                var caption = new TextBlock
                {
                    Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px, {bytes.Length:N0} bytes",
                    Foreground = _theme.SubtleForeground,
                    Margin = new Thickness(8, 6, 8, 2)
                };

                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8)
                };

                var panel = new StackPanel();
                panel.Children.Add(caption);
                panel.Children.Add(image);

                return new ScrollViewer
                {
                    Content = panel,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = _theme.Background
                };
            }
            catch (Exception ex)
            {
                return BuildPlainView("Could not decode the image: " + ex.Message, true);
            }
        }

        private void UpdateFacts(RenderedCell r)
        {
            _factsPanel.Children.Clear();

            if (r.Facts == null)
            {
                return;
            }

            var table = new System.Windows.Controls.Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            foreach (var fact in r.Facts)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = fact.Label,
                    Foreground = _theme.SubtleForeground,
                    Margin = new Thickness(0, 1, 12, 1),
                    TextWrapping = TextWrapping.NoWrap
                };
                System.Windows.Controls.Grid.SetRow(label, row);
                System.Windows.Controls.Grid.SetColumn(label, 0);
                table.Children.Add(label);

                var value = new TextBox
                {
                    Text = fact.Value ?? string.Empty,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = _theme.Foreground,
                    SelectionBrush = _theme.SelectionBackground,
                    Margin = new Thickness(0, 1, 0, 1),
                    Padding = new Thickness(0),
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = fact.Monospace ? MonoFont : SystemFonts.MessageFontFamily,
                    UndoLimit = 0
                };
                System.Windows.Controls.Grid.SetRow(value, row);
                System.Windows.Controls.Grid.SetColumn(value, 1);
                table.Children.Add(value);

                row++;
            }

            _factsPanel.Children.Add(table);
        }

        private void UpdateStatus(RenderedCell r)
        {
            var parts = new List<string>();

            if (r.Cell.ResultSetCount > 1)
            {
                parts.Add($"result set {r.Cell.ResultSetIndex} / {r.Cell.ResultSetCount}");
            }

            parts.Add(r.Cell.TotalRows >= 0
                ? $"row {r.Cell.Row + 1:N0} / {r.Cell.TotalRows:N0}"
                : $"row {r.Cell.Row + 1:N0}");

            if (r.Cell.StorageColumn >= 0)
            {
                parts.Add(r.Cell.ColumnCount > 0
                    ? $"column {r.Cell.StorageColumn + 1} / {r.Cell.ColumnCount}"
                    : $"column {r.Cell.StorageColumn + 1}");
            }

            if (r.Bytes != null)
            {
                parts.Add($"{r.Bytes.Length:N0} bytes");
            }
            else if (r.Formatted != null)
            {
                parts.Add($"{r.Formatted.Text.Length:N0} chars shown");
            }

            parts.Add(CellRenderer.ModeLabel(r.EffectiveMode));

            _statusText.Text = string.Join("   •   ", parts);
        }

        // ---- commands ----------------------------------------------------------------

        /// <summary>
        /// Staging, unstaging or discarding happens on either tab, so the shell has to
        /// refresh too: otherwise the Script tab keeps a stale count and the header keeps
        /// a stale "pending change" badge after the changes are gone.
        /// </summary>
        private void OnDiagnosticsChanged(object sender, EventArgs e)
        {
            // Diagnostics can be recorded from a background thread.
            if (Dispatcher.CheckAccess())
            {
                UpdateProblems();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(UpdateProblems));
            }
        }

        /// <summary>
        /// Shows what has gone wrong, in the panel, where it can actually be seen. The bar
        /// stays hidden until something is recorded.
        /// </summary>
        private void UpdateProblems()
        {
            int count = Diagnostics.Count;
            if (count == 0)
            {
                _problemsBorder.Visibility = Visibility.Collapsed;
                _problemsList.Visibility = Visibility.Collapsed;
                _problemsDetails.IsChecked = false;
                return;
            }

            var latest = Diagnostics.Latest;
            string summary = count == 1 ? "1 problem" : count + " problems";
            string message = latest?.Message ?? string.Empty;
            if (!string.IsNullOrEmpty(latest?.Detail))
            {
                message += "  (" + latest.Detail + ")";
            }

            _problemsText.Text = "⚠ " + summary + " — " + message;
            _problemsBorder.Visibility = Visibility.Visible;

            bool showing = _problemsDetails.IsChecked == true;
            _problemsList.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            if (showing)
            {
                _problemsList.Text = string.Join(
                    Environment.NewLine,
                    Diagnostics.Recent().Select(entry => entry.ToString()));
            }
        }

        private void CopyProblems()
        {
            try
            {
                Clipboard.SetText(Diagnostics.ToText());
                _statusText.Text = "Copied the problem list.";
            }
            catch (Exception)
            {
                // Nothing useful to do: reporting a copy failure needs the clipboard.
            }
        }

        private void OnPendingEditsChanged(object sender, EventArgs e)
        {
            if (_cell != null)
            {
                Rerender();
            }
            else
            {
                UpdateTabHeaders();
            }
        }

        /// <summary>Puts the pending-change count on the Script tab so it is never hidden.</summary>
        private void UpdateTabHeaders()
        {
            int count = _cell?.SourceGrid != null ? PendingEdits.CountFor(_cell.SourceGrid) : 0;
            _scriptTab.Header = count > 0 ? $"Script ({count})" : "Script";
        }

        private PendingEdit CurrentStagedEdit()
        {
            if (_cell?.SourceGrid == null || _cell.StorageColumn < 0)
            {
                return null;
            }

            return PendingEdits.Get(_cell.SourceGrid, _cell.Row, _cell.StorageColumn);
        }

        /// <summary>
        /// Stages the typed value for this cell. Staged edits accumulate across cells and
        /// rows; the Script tab turns all of them into one UPDATE script. Nothing is
        /// written to the database at any point.
        /// </summary>
        private void OnScriptValue(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rendered == null || _cell?.SourceGrid == null || _cell.StorageColumn < 0)
                {
                    return;
                }

                string typed = _nullCheck.IsChecked == true ? null : (_editBox?.Text ?? string.Empty);
                bool parsed = true;

                string literal = typed == null
                    ? SqlLiteral.Null
                    : SqlLiteral.FromUserText(typed, _rendered.Category, _cell.ServerTypeName, out parsed);

                PendingEdits.Set(new PendingEdit
                {
                    Grid = _cell.SourceGrid,
                    Row = _cell.Row,
                    DataColumn = _cell.StorageColumn,
                    ColumnName = _cell.ColumnName,
                    Literal = literal,
                    Display = typed ?? "NULL",
                    OldDisplay = _cell.IsNull ? "NULL" : (_cell.Text ?? string.Empty),
                    Parsed = parsed
                });

                int count = PendingEdits.CountFor(_cell.SourceGrid);
                _statusText.Text = parsed
                    ? $"Change added. {count} pending change(s) — see the Script tab."
                    : $"Change added, but the text did not parse as {_cell.ServerTypeName ?? "this column's type"} and was quoted as a string.";

                _editToggle.IsChecked = false;
                _tabs.SelectedItem = _scriptTab;
                _scriptView.ShowStagedEdits(_cell);
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Staging the edited value failed.", ex);
                _statusText.Text = "Could not stage that value: " + ex.Message;
            }
        }

        private void OnUnstage(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cell?.SourceGrid == null)
                {
                    return;
                }

                PendingEdits.Remove(_cell.SourceGrid, _cell.Row, _cell.StorageColumn);
                _statusText.Text = $"Change removed. {PendingEdits.CountFor(_cell.SourceGrid)} pending change(s) left.";
                Rerender();
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Unstaging failed.", ex);
            }
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = _editBox?.Text ?? _rendered?.Formatted?.Text;
                if (string.IsNullOrEmpty(text) && _rendered?.Bytes != null)
                {
                    text = HexDump.ToSqlLiteral(_rendered.Bytes, int.MaxValue);
                }

                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                    _statusText.Text = $"Copied {text.Length:N0} characters.";
                }
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Copy failed.", ex);
                _statusText.Text = "Could not copy: " + ex.Message;
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_rendered == null)
            {
                return;
            }

            try
            {
                string extension = _rendered.SuggestedExtension ?? ".txt";
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save cell value",
                    FileName = SanitiseFileName(_rendered.Cell.ColumnName) + "_row" + (_rendered.Cell.Row + 1) + extension,
                    DefaultExt = extension,
                    Filter = BuildFilter(extension),
                    AddExtension = true,
                    OverwritePrompt = true
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                // Binary values go to disk byte-exact; text goes as UTF-8 with a BOM so
                // other editors get the encoding right.
                bool writeBytes = _rendered.Bytes != null &&
                                  (_rendered.EffectiveMode == ViewMode.Hex ||
                                   _rendered.EffectiveMode == ViewMode.Image ||
                                   extension != ".txt");

                if (writeBytes)
                {
                    File.WriteAllBytes(dialog.FileName, _rendered.Bytes);
                    _statusText.Text = $"Wrote {_rendered.Bytes.Length:N0} bytes to {Path.GetFileName(dialog.FileName)}.";
                }
                else
                {
                    string text = _rendered.Formatted?.Text ?? string.Empty;
                    File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));
                    _statusText.Text = $"Wrote {text.Length:N0} characters to {Path.GetFileName(dialog.FileName)}.";
                }
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Save failed.", ex);
                _statusText.Text = "Could not save: " + ex.Message;
            }
        }

        private static string BuildFilter(string extension)
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            return $"{label} file (*{extension})|*{extension}|All files (*.*)|*.*";
        }

        private static string SanitiseFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "cell";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            return sb.ToString();
        }
    }
}
