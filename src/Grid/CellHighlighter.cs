using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SqlResultToolkit.Grid
{
    /// <summary>
    /// Tints cells that have a pending change, in the real SSMS results grid.
    ///
    /// This hangs off <see cref="GridControl.CustomizeCellGDIObjects"/>, the same public
    /// event SSMS itself uses to colour its NULL cells. The grid seeds the event's
    /// BKBrush and TextBrush before raising it and reads them back afterwards, so a
    /// handler only has to touch the cells it cares about — an override does not leak onto
    /// the next cell painted.
    ///
    /// The handler runs once per painted cell, so the lookup is a hash probe against a set
    /// rebuilt only when the pending changes actually change.
    /// </summary>
    internal static class CellHighlighter
    {
        private static readonly HashSet<GridControl> Grids = new HashSet<GridControl>();
        private static readonly Dictionary<GridControl, int> Gutters = new Dictionary<GridControl, int>();
        private static readonly HashSet<CellKey> Changed = new HashSet<CellKey>();
        private static readonly HashSet<CellKey> Unparsed = new HashSet<CellKey>();

        private static SolidBrush _changedBrush;
        private static SolidBrush _unparsedBrush;
        private static bool _dark;
        private static bool _subscribed;

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public CellKey(GridControl grid, long row, int dataColumn)
            {
                Grid = grid;
                Row = row;
                DataColumn = dataColumn;
            }

            public GridControl Grid { get; }

            public long Row { get; }

            public int DataColumn { get; }

            public bool Equals(CellKey other) =>
                ReferenceEquals(Grid, other.Grid) && Row == other.Row && DataColumn == other.DataColumn;

            public override bool Equals(object obj) => obj is CellKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Grid);
                    hash = (hash * 397) ^ Row.GetHashCode();
                    return (hash * 397) ^ DataColumn;
                }
            }
        }

        public static void Attach(GridControl grid)
        {
            if (grid == null || !Grids.Add(grid))
            {
                return;
            }

            EnsureSubscribed();

            try
            {
                grid.CustomizeCellGDIObjects += OnCustomizeCell;
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Could not hook cell painting.", ex);
                Grids.Remove(grid);
                return;
            }

            Rebuild();
        }

        public static void Detach(GridControl grid)
        {
            if (grid == null || !Grids.Remove(grid))
            {
                return;
            }

            Gutters.Remove(grid);

            try
            {
                grid.CustomizeCellGDIObjects -= OnCustomizeCell;
            }
            catch (Exception)
            {
                // the grid is going away anyway
            }
        }

        public static void DetachAll()
        {
            foreach (var grid in new List<GridControl>(Grids))
            {
                Detach(grid);
            }

            Changed.Clear();
            Unparsed.Clear();
        }

        /// <summary>Re-picks the tints for the current VS theme and repaints.</summary>
        public static void SetDark(bool dark)
        {
            if (_changedBrush != null && _dark == dark)
            {
                return;
            }

            _dark = dark;

            _changedBrush?.Dispose();
            _unparsedBrush?.Dispose();

            // Amber for "you changed this", red for "this did not parse as the column's
            // type". Both are backgrounds behind the grid's own text colour, so they stay
            // deliberately muted.
            _changedBrush = new SolidBrush(dark ? Color.FromArgb(0x4A, 0x3B, 0x1E) : Color.FromArgb(0xFF, 0xF0, 0xB3));
            _unparsedBrush = new SolidBrush(dark ? Color.FromArgb(0x5A, 0x2A, 0x2A) : Color.FromArgb(0xF8, 0xD0, 0xD4));

            InvalidateAll();
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }

            PendingEdits.Changed += (s, e) => Rebuild();
            _subscribed = true;
        }

        /// <summary>
        /// Rebuilds the lookup sets and repaints. Called only when the pending changes
        /// change, never per painted cell.
        /// </summary>
        private static void Rebuild()
        {
            Changed.Clear();
            Unparsed.Clear();

            // A grid can pick up a new result set between rebuilds, so re-measure.
            Gutters.Clear();

            foreach (var grid in Grids)
            {
                foreach (var edit in PendingEdits.For(grid))
                {
                    var key = new CellKey(grid, edit.Row, edit.DataColumn);
                    Changed.Add(key);

                    if (!edit.Parsed)
                    {
                        Unparsed.Add(key);
                    }
                }
            }

            InvalidateAll();
        }

        private static void InvalidateAll()
        {
            foreach (var grid in Grids)
            {
                try
                {
                    if (!grid.IsDisposed && grid.IsHandleCreated)
                    {
                        grid.Invalidate();
                    }
                }
                catch (Exception)
                {
                    // a grid mid-teardown is not worth reporting
                }
            }
        }

        private static void OnCustomizeCell(object sender, CustomizeCellGDIObjectsEventArgs e)
        {
            // Runs for every painted cell: keep it allocation-free and never throw, or a
            // mistake here turns into a paint storm inside SSMS.
            try
            {
                if (Changed.Count == 0 || !(sender is GridControl grid))
                {
                    return;
                }

                int gutter = GutterFor(grid);
                var key = new CellKey(grid, e.RowIndex, e.ColumnIndex - gutter);

                if (Unparsed.Contains(key))
                {
                    e.BKBrush = _unparsedBrush;
                }
                else if (Changed.Contains(key))
                {
                    e.BKBrush = _changedBrush;
                }
            }
            catch (Exception)
            {
                // Swallow: this is a paint callback.
            }
        }

        /// <summary>
        /// The row-number gutter width, cached per grid. It is fixed once a result set has
        /// loaded, and recomputing it per painted cell would mean a reflection call per
        /// cell.
        /// </summary>
        private static int GutterFor(GridControl grid)
        {
            if (Gutters.TryGetValue(grid, out int gutter))
            {
                return gutter;
            }

            // Only cache once the storage is actually there. Grids are discovered before
            // their result set is attached, and caching the 0 we would compute then would
            // shift every lookup by one column for the life of the grid.
            try
            {
                var storage = grid.GridStorage;
                if (storage == null)
                {
                    return 0;
                }

                gutter = GridIndexMap.For(grid, ResultSetAccessor.For(storage), storage).Gutter;
            }
            catch (Exception)
            {
                return 0;
            }

            Gutters[grid] = gutter;
            return gutter;
        }
    }
}
