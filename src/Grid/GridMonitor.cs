using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SqlCellInspector.Grid
{
    internal sealed class CellChangedEventArgs : EventArgs
    {
        public CellChangedEventArgs(CellSnapshot cell)
        {
            Cell = cell;
        }

        public CellSnapshot Cell { get; }
    }

    /// <summary>
    /// Finds every <see cref="GridControl"/> living in this SSMS process and reports the
    /// current cell of whichever one has focus.
    ///
    /// Discovery is by window handle rather than by walking SSMS's own object model: the
    /// results grid is a plain Win32-backed Windows Forms control, so
    /// <see cref="Control.FromHandle"/> hands us the real instance without touching any
    /// internal SQLEditors type. New grids appear only after a batch produces a result
    /// set, so we rescan periodically instead of once.
    ///
    /// Grids are tracked by object identity, not by HWND. Windows recycles handle values,
    /// so a handle-keyed set will silently refuse to hook a new grid that happens to land
    /// on a disposed one's handle — which is exactly what re-running a query does.
    /// </summary>
    internal sealed class GridMonitor : IDisposable
    {
        private const int RescanEveryTicks = 4;   // ~1 s at a 250 ms tick
        private const int TickMilliseconds = 250;

        /// <summary>The container SSMS stacks several result-set grids into.</summary>
        private const string ResultSetPanelTypeName = "MultiControlPanel";

        private readonly HashSet<GridControl> _grids = new HashSet<GridControl>();
        private readonly DispatcherTimer _timer;

        private GridControl _activeGrid;
        private GridControl _lastGrid;
        private long _lastRow = -1;
        private int _lastColumn = -1;
        private int _tick;
        private bool _disposed;

        public GridMonitor()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(TickMilliseconds)
            };
            _timer.Tick += OnTick;
        }

        public event EventHandler<CellChangedEventArgs> CellChanged;

        public int GridCount => _grids.Count;

        /// <summary>When true the panel keeps showing the pinned cell and stops following.</summary>
        public bool Paused { get; set; }

        public void Start()
        {
            Rescan();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        /// <summary>Re-reads the current cell even if it has not moved.</summary>
        public void Refresh()
        {
            _lastGrid = null;
            _lastRow = -1;
            _lastColumn = -1;
            Poll(null);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (++_tick % RescanEveryTicks == 0)
                {
                    Rescan();
                }

                Poll(null);
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Grid monitor tick failed.", ex);
            }
        }

        /// <param name="preferred">
        /// The grid that raised the event we are reacting to. It wins over the focused
        /// grid: with several result sets stacked in one tab, a grid can raise
        /// SelectionChanged while focus is still elsewhere, and reading the focused grid
        /// then shows a cell the user did not touch.
        /// </param>
        private void Poll(GridControl preferred)
        {
            if (Paused)
            {
                return;
            }

            var grid = preferred != null && !IsDead(preferred) ? preferred : PickActiveGrid();
            if (grid == null)
            {
                return;
            }

            long row;
            int uiColumn;
            try
            {
                grid.GetCurrentCell(out row, out uiColumn);
            }
            catch (Exception)
            {
                // The grid can be mid-teardown; it will be dropped on the next rescan.
                return;
            }

            if (row < 0 || uiColumn < 0)
            {
                return;
            }

            if (ReferenceEquals(grid, _lastGrid) && row == _lastRow && uiColumn == _lastColumn)
            {
                return;
            }

            _lastGrid = grid;
            _lastRow = row;
            _lastColumn = uiColumn;

            LocateInResultSets(grid, out int resultSetIndex, out int resultSetCount);

            var snapshot = CellReader.Read(grid, row, uiColumn);
            if (snapshot != null)
            {
                snapshot.ResultSetIndex = resultSetIndex;
                snapshot.ResultSetCount = resultSetCount;
                CellChanged?.Invoke(this, new CellChangedEventArgs(snapshot));
            }
        }

        /// <summary>
        /// Works out which of the result-set grids stacked in this tab the given grid is,
        /// by finding their shared container and ordering the siblings top to bottom.
        /// Reports 0/0 when the grid is not inside such a container (a grid in a dialog,
        /// say), and the panel then says nothing about result sets.
        /// </summary>
        private void LocateInResultSets(GridControl grid, out int index, out int count)
        {
            index = 0;
            count = 0;

            try
            {
                var panel = FindResultSetPanel(grid);
                if (panel == null)
                {
                    return;
                }

                var siblings = _grids
                    .Where(g => !IsDead(g) && ReferenceEquals(FindResultSetPanel(g), panel))
                    .Select(g => new { Grid = g, Top = ScreenTop(g) })
                    .OrderBy(x => x.Top)
                    .Select(x => x.Grid)
                    .ToList();

                count = siblings.Count;
                int position = siblings.FindIndex(g => ReferenceEquals(g, grid));
                index = position < 0 ? 0 : position + 1;
            }
            catch (Exception)
            {
                index = 0;
                count = 0;
            }
        }

        private static Control FindResultSetPanel(Control control)
        {
            for (var parent = control?.Parent; parent != null; parent = parent.Parent)
            {
                if (string.Equals(parent.GetType().Name, ResultSetPanelTypeName, StringComparison.Ordinal))
                {
                    return parent;
                }
            }

            return null;
        }

        private static int ScreenTop(Control control)
        {
            try
            {
                return control.PointToScreen(Point.Empty).Y;
            }
            catch (Exception)
            {
                return int.MaxValue;
            }
        }

        private GridControl PickActiveGrid()
        {
            foreach (var grid in _grids)
            {
                try
                {
                    if (!IsDead(grid) && (grid.Focused || grid.ContainsFocus))
                    {
                        _activeGrid = grid;
                        return grid;
                    }
                }
                catch (Exception)
                {
                    // ignore and let the rescan prune it
                }
            }

            if (_activeGrid != null && IsDead(_activeGrid))
            {
                _activeGrid = null;
            }

            return _activeGrid;
        }

        private static bool IsDead(GridControl grid)
        {
            try
            {
                return grid == null || grid.IsDisposed || !grid.IsHandleCreated;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void Rescan()
        {
            _grids.RemoveWhere(IsDead);

            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            NativeMethods.EnumWindows((top, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(top, out uint owner);
                if (owner == pid)
                {
                    Visit(top);
                    NativeMethods.EnumChildWindows(top, (child, __) =>
                    {
                        Visit(child);
                        return true;
                    }, IntPtr.Zero);
                }

                return true;
            }, IntPtr.Zero);
        }

        private void Visit(IntPtr handle)
        {
            GridControl grid;
            try
            {
                grid = Control.FromHandle(handle) as GridControl;
            }
            catch (Exception)
            {
                return;
            }

            if (grid == null || IsDead(grid) || !_grids.Add(grid))
            {
                return;
            }

            // Event hook for immediate response; the timer poll is the safety net for
            // navigation paths that do not raise SelectionChanged.
            try
            {
                grid.SelectionChanged += OnGridSelectionChanged;
                grid.Disposed += OnGridDisposed;

                // CellHighlighter.Attach(grid) belongs here, and the tinting is proven to
                // work against a bare GridControl (test\run-tint-test.ps1 samples the
                // painted pixels). It does NOT take effect inside SSMS and the reason is
                // not yet known, so it stays off rather than running a handler on every
                // painted cell for no visible result. See BACKLOG.md.
            }
            catch (Exception ex)
            {
                SqlCellInspectorPackage.Log.Error("Could not hook a results grid.", ex);
                _grids.Remove(grid);
            }
        }

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var grid = sender as GridControl;
            if (grid != null)
            {
                _activeGrid = grid;
            }

            Poll(grid);
        }

        private void OnGridDisposed(object sender, EventArgs e)
        {
            if (!(sender is GridControl grid))
            {
                return;
            }

            Unhook(grid);
            _grids.Remove(grid);

            if (ReferenceEquals(grid, _activeGrid))
            {
                _activeGrid = null;
            }

            if (ReferenceEquals(grid, _lastGrid))
            {
                _lastGrid = null;
            }
        }

        private void Unhook(GridControl grid)
        {
            CellHighlighter.Detach(grid);

            try
            {
                grid.SelectionChanged -= OnGridSelectionChanged;
                grid.Disposed -= OnGridDisposed;
            }
            catch (Exception)
            {
                // nothing useful to do
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;

            foreach (var grid in _grids)
            {
                Unhook(grid);
            }

            _grids.Clear();
            _activeGrid = null;
            _lastGrid = null;
        }

        private static class NativeMethods
        {
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        }
    }
}
