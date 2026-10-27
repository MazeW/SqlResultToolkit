using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.Grid;
using SqlResultToolkit.Grid;

namespace SqlResultToolkit.Tests
{
    /// <summary>
    /// Proves the cell tinting works by driving the REAL SSMS GridControl, painting it to
    /// a bitmap and sampling the pixels. No SSMS, no screen coordinates, no eyeballing: it
    /// either finds the tint colour at the edited cell or it does not.
    ///
    /// This exists because verifying it through SSMS's UI meant clicking at guessed
    /// coordinates, which was slow and kept producing inconclusive runs.
    /// </summary>
    internal static class TintHarness
    {
        private const int Rows = 3;
        private const int DataColumns = 3;

        private static int _passed;
        private static int _failed;

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();

            using (var form = new Form { Width = 700, Height = 240, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) })
            using (var grid = new GridControl())
            {
                var storage = new FakeStorage();

                grid.Dock = DockStyle.Fill;
                form.Controls.Add(grid);
                form.Show();

                // One gutter column plus the data columns, the way SSMS lays a result set
                // out, so the gutter arithmetic is exercised rather than assumed away.
                for (int i = 0; i < DataColumns + 1; i++)
                {
                    grid.AddColumn(new GridColumnInfo { ColumnWidth = 120, WidthType = GridColumnWidthType.InPixels });
                }

                grid.GridStorage = storage;
                grid.UpdateGrid();
                Application.DoEvents();

                CellHighlighter.SetDark(false);
                CellHighlighter.Attach(grid);

                Color before = SampleCell(form, grid, row: 0, dataColumn: 1);

                PendingEdits.Set(new PendingEdit
                {
                    Grid = grid,
                    Row = 0,
                    DataColumn = 1,
                    ColumnName = "B",
                    Literal = "N'tinted'",
                    Display = "tinted",
                    OldDisplay = "r0c1",
                    Parsed = true
                });

                Application.DoEvents();

                Color edited = SampleCell(form, grid, row: 0, dataColumn: 1);
                Color neighbourRight = SampleCell(form, grid, row: 0, dataColumn: 2);
                Color neighbourDown = SampleCell(form, grid, row: 1, dataColumn: 1);

                Check("the edited cell changed colour", !Similar(before, edited));
                Check("the edited cell is the amber tint", Similar(edited, Color.FromArgb(0xFF, 0xF0, 0xB3)));
                Check("the cell to its right is untouched", Similar(neighbourRight, before));
                Check("the cell below is untouched", Similar(neighbourDown, before));

                // An unparsed value gets the red tint instead.
                PendingEdits.Set(new PendingEdit
                {
                    Grid = grid,
                    Row = 2,
                    DataColumn = 0,
                    ColumnName = "A",
                    Literal = "N'oops'",
                    Display = "oops",
                    OldDisplay = "r2c0",
                    Parsed = false
                });

                Application.DoEvents();
                Check("an unparsed value gets the red tint",
                    Similar(SampleCell(form, grid, 2, 0), Color.FromArgb(0xF8, 0xD0, 0xD4)));

                // Removing it puts the cell back.
                PendingEdits.Remove(grid, 0, 1);
                Application.DoEvents();
                Check("removing the change restores the cell", Similar(SampleCell(form, grid, 0, 1), before));

                // Dark theme uses the other palette.
                CellHighlighter.SetDark(true);
                PendingEdits.Set(new PendingEdit
                {
                    Grid = grid, Row = 1, DataColumn = 2, ColumnName = "C",
                    Literal = "1", Display = "1", OldDisplay = "r1c2", Parsed = true
                });
                Application.DoEvents();
                Check("the dark palette is used when dark",
                    Similar(SampleCell(form, grid, 1, 2), Color.FromArgb(0x4A, 0x3B, 0x1E)));

                CellHighlighter.DetachAll();
                PendingEdits.ClearAll();
                form.Hide();
            }

            Console.WriteLine();
            Console.WriteLine($"passed {_passed}, failed {_failed}");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>Paints the grid to a bitmap and reads the middle of one cell.</summary>
        private static Color SampleCell(Form form, GridControl grid, long row, int dataColumn)
        {
            grid.Refresh();
            Application.DoEvents();

            using (var bitmap = new Bitmap(grid.Width, grid.Height))
            {
                grid.DrawToBitmap(bitmap, new Rectangle(0, 0, grid.Width, grid.Height));

                // +1 for the gutter column the grid draws in front of the data.
                Rectangle cell = grid.GetVisibleCellRectangle(row, dataColumn + 1);
                int x = Math.Min(bitmap.Width - 1, Math.Max(0, cell.Left + cell.Width / 2));
                int y = Math.Min(bitmap.Height - 1, Math.Max(0, cell.Top + cell.Height / 2));
                return bitmap.GetPixel(x, y);
            }
        }

        /// <summary>Tolerant compare: anti-aliased text can shift a sampled pixel slightly.</summary>
        private static bool Similar(Color a, Color b, int tolerance = 12) =>
            Math.Abs(a.R - b.R) <= tolerance &&
            Math.Abs(a.G - b.G) <= tolerance &&
            Math.Abs(a.B - b.B) <= tolerance;

        private static void Check(string what, bool condition)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  ok    " + what);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + what);
            }
        }

        /// <summary>A minimal IGridStorage: enough columns and rows to paint.</summary>
        private sealed class FakeStorage : IGridStorage
        {
            /// <summary>Read by ResultSetAccessor through reflection, exactly as QEResultSet's is.</summary>
            public int NumberOfDataColumns => DataColumns;

            public long NumRows() => Rows;

            public long EnsureRowsInBuf(long first, long last) => Rows;

            public string GetCellDataAsString(long row, int column) =>
                column == 0 ? (row + 1).ToString() : "r" + row + "c" + (column - 1);

            public int IsCellEditable(long row, int column) => 0;

            public Bitmap GetCellDataAsBitmap(long row, int column) => null;

            public void GetCellDataForButton(long row, int column, out ButtonCellState state, out Bitmap image, out string text)
            {
                state = ButtonCellState.Normal;
                image = null;
                text = string.Empty;
            }

            public GridCheckBoxState GetCellDataForCheckBox(long row, int column) => GridCheckBoxState.None;

            public void FillControlWithData(long row, int column, IGridEmbeddedControl control) { }

            public bool SetCellDataFromControl(long row, int column, IGridEmbeddedControl control) => false;
        }
    }
}

namespace SqlResultToolkit
{
    /// <summary>
    /// Stands in for the package's activity-log wrapper, which the harness cannot pull in
    /// because it would drag the whole Visual Studio shell with it.
    /// </summary>
    internal static class SqlResultToolkitPackage
    {
        internal static class Log
        {
            public static void Error(string message, Exception ex = null) =>
                Console.WriteLine("  [log] " + message + (ex == null ? string.Empty : " :: " + ex.Message));

            public static void Info(string message) => Console.WriteLine("  [log] " + message);
        }
    }
}
