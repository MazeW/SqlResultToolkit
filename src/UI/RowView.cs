using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlCellInspector.Format;
using SqlCellInspector.Grid;

namespace SqlCellInspector.UI
{
    /// <summary>
    /// The whole current row, one column per line.
    ///
    /// This is the view that makes a 40-column result set readable: instead of scrolling
    /// the grid sideways, you see every column of the row you are on, with the current one
    /// highlighted, and you can copy the row as a list of name/value pairs.
    /// </summary>
    internal sealed class RowView : UserControl
    {
        private const int MaxValueChars = 300;

        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace");

        private Theme _theme;

        private readonly TextBlock _header = new TextBlock { Margin = new Thickness(8, 6, 8, 2), TextWrapping = TextWrapping.Wrap };
        private readonly Button _copyButton = new Button { Content = "Copy row", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _copyNamesButton = new Button { Content = "Copy names", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        private readonly StackPanel _fieldPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(8, 2, 8, 4) };

        private List<RowField> _fields = new List<RowField>();
        private CellSnapshot _cell;

        public RowView(Theme theme)
        {
            _theme = theme;
            Build();
            ApplyTheme(theme);
        }

        public void SetCell(CellSnapshot cell)
        {
            _cell = cell;

            try
            {
                var grid = cell?.SourceGrid;
                _fields = grid == null || grid.IsDisposed
                    ? new List<RowField>()
                    : SelectionReader.ReadRow(grid, cell.Row, cell.StorageColumn);
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Reading the row failed.", ex);
                _fields = new List<RowField>();
            }

            Render();
        }

        public void ApplyTheme(Theme theme)
        {
            _theme = theme;
            Background = theme.Background;
            Foreground = theme.Foreground;
            _header.Foreground = theme.Foreground;
            _status.Foreground = theme.SubtleForeground;
            Render();
        }

        private void Build()
        {
            _copyButton.Click += (s, e) => Copy(namesOnly: false);
            _copyButton.ToolTip = "Copy every column of this row as name = value lines";
            _copyNamesButton.Click += (s, e) => Copy(namesOnly: true);
            _copyNamesButton.ToolTip = "Copy the column names as a comma-separated list";

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 4) };
            toolbar.Children.Add(_copyButton);
            toolbar.Children.Add(_copyNamesButton);

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            System.Windows.Controls.Grid.SetRow(_header, 0);
            root.Children.Add(_header);
            System.Windows.Controls.Grid.SetRow(toolbar, 1);
            root.Children.Add(toolbar);

            var scroll = new ScrollViewer
            {
                Content = _fieldPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            System.Windows.Controls.Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            System.Windows.Controls.Grid.SetRow(_status, 3);
            root.Children.Add(_status);

            Content = root;
        }

        private void Render()
        {
            _fieldPanel.Children.Clear();

            if (_cell == null)
            {
                _header.Text = "No cell selected.";
                return;
            }

            _header.Text = _cell.TotalRows >= 0
                ? $"Row {_cell.Row + 1:N0} of {_cell.TotalRows:N0}"
                : $"Row {_cell.Row + 1:N0}";

            if (_fields.Count == 0)
            {
                _status.Text = "This grid exposes no column metadata.";
                return;
            }

            var table = new System.Windows.Controls.Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var nameBlock = new TextBlock
                {
                    Text = field.Column.Name,
                    FontWeight = field.IsCurrent ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = field.IsCurrent ? _theme.Accent : _theme.Foreground,
                    Margin = new Thickness(0, 1, 10, 1)
                };

                if (field.Column.IsKey)
                {
                    nameBlock.Text += "  \U0001F511";
                    nameBlock.ToolTip = "Key column";
                }

                System.Windows.Controls.Grid.SetRow(nameBlock, i);
                System.Windows.Controls.Grid.SetColumn(nameBlock, 0);
                table.Children.Add(nameBlock);

                var typeBlock = new TextBlock
                {
                    Text = field.Column.ServerTypeName ?? string.Empty,
                    Foreground = _theme.SubtleForeground,
                    Margin = new Thickness(0, 1, 10, 1)
                };
                System.Windows.Controls.Grid.SetRow(typeBlock, i);
                System.Windows.Controls.Grid.SetColumn(typeBlock, 1);
                table.Children.Add(typeBlock);

                var valueBox = new TextBox
                {
                    Text = Display(field),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = field.IsNull ? _theme.Accent : _theme.Foreground,
                    SelectionBrush = _theme.SelectionBackground,
                    FontFamily = MonoFont,
                    FontStyle = field.IsNull ? FontStyles.Italic : FontStyles.Normal,
                    Margin = new Thickness(0, 1, 0, 1),
                    Padding = new Thickness(0),
                    TextWrapping = TextWrapping.NoWrap,
                    UndoLimit = 0
                };
                System.Windows.Controls.Grid.SetRow(valueBox, i);
                System.Windows.Controls.Grid.SetColumn(valueBox, 2);
                table.Children.Add(valueBox);
            }

            _fieldPanel.Children.Add(table);
            _status.Text = $"{_fields.Count:N0} column(s)" +
                           (_fields.Any(f => f.Column.IsKey) ? ", key marked" : string.Empty);
        }

        private string Display(RowField field)
        {
            if (field.IsNull)
            {
                return "NULL";
            }

            string text = field.Text ?? Convert.ToString(field.Value) ?? string.Empty;
            text = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

            return text.Length <= MaxValueChars
                ? text
                : text.Substring(0, MaxValueChars) + " \u2026 (+" + (text.Length - MaxValueChars).ToString("N0") + " chars)";
        }

        private void Copy(bool namesOnly)
        {
            try
            {
                if (_fields.Count == 0)
                {
                    return;
                }

                string text = namesOnly
                    ? string.Join(", ", _fields.Select(f => f.Column.Name))
                    : BuildRowText();

                Clipboard.SetText(text);
                _status.Text = $"Copied {text.Length:N0} characters.";
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Copying the row failed.", ex);
                _status.Text = "Could not copy: " + ex.Message;
            }
        }

        private string BuildRowText()
        {
            int width = _fields.Max(f => f.Column.Name.Length);
            var sb = new StringBuilder();

            foreach (var field in _fields)
            {
                sb.Append(field.Column.Name.PadRight(width))
                  .Append(" = ")
                  .AppendLine(field.IsNull ? "NULL" : field.Text ?? string.Empty);
            }

            return sb.ToString();
        }
    }
}
