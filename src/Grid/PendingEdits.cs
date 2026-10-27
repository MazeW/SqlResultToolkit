using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SqlCellInspector.Grid
{
    /// <summary>One cell the user has given a new value to, not yet written anywhere.</summary>
    internal sealed class PendingEdit
    {
        public GridControl Grid { get; set; }

        public long Row { get; set; }

        /// <summary>0-based result-set column index.</summary>
        public int DataColumn { get; set; }

        public string ColumnName { get; set; }

        /// <summary>The new value, already formatted as a T-SQL literal.</summary>
        public string Literal { get; set; }

        /// <summary>What the user typed, for showing back to them.</summary>
        public string Display { get; set; }

        /// <summary>The value before the change, so the list can show old → new.</summary>
        public string OldDisplay { get; set; }

        /// <summary>False when the typed text did not parse as the column's declared type.</summary>
        public bool Parsed { get; set; } = true;
    }

    /// <summary>
    /// The staged edits: cells the user has retyped so that one script can carry all of
    /// them. Deliberately explicit rather than implicit — the Script tab always shows how
    /// many are staged and lets them be cleared, because invisible state that silently
    /// changes a generated UPDATE would be a trap.
    ///
    /// Nothing here touches the database. Edits live for the SSMS session and are dropped
    /// when the grid they belong to goes away, which is what re-running a query does.
    /// </summary>
    internal static class PendingEdits
    {
        private static readonly List<PendingEdit> Edits = new List<PendingEdit>();

        public static event EventHandler Changed;

        public static void Set(PendingEdit edit)
        {
            if (edit?.Grid == null)
            {
                return;
            }

            Prune();
            Edits.RemoveAll(e => Same(e, edit.Grid, edit.Row, edit.DataColumn));
            Edits.Add(edit);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static PendingEdit Get(GridControl grid, long row, int dataColumn)
        {
            Prune();
            return Edits.FirstOrDefault(e => Same(e, grid, row, dataColumn));
        }

        public static void Remove(GridControl grid, long row, int dataColumn)
        {
            Prune();
            if (Edits.RemoveAll(e => Same(e, grid, row, dataColumn)) > 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        public static IReadOnlyList<PendingEdit> For(GridControl grid)
        {
            Prune();
            return Edits
                .Where(e => ReferenceEquals(e.Grid, grid))
                .OrderBy(e => e.Row)
                .ThenBy(e => e.DataColumn)
                .ToList();
        }

        public static int CountFor(GridControl grid) => For(grid).Count;

        public static void Clear(GridControl grid)
        {
            Prune();
            if (Edits.RemoveAll(e => ReferenceEquals(e.Grid, grid)) > 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void ClearAll()
        {
            if (Edits.Count == 0)
            {
                return;
            }

            Edits.Clear();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Drops edits whose grid has been disposed, e.g. after re-running a query.</summary>
        private static void Prune()
        {
            Edits.RemoveAll(e =>
            {
                try
                {
                    return e.Grid == null || e.Grid.IsDisposed;
                }
                catch (Exception)
                {
                    return true;
                }
            });
        }

        private static bool Same(PendingEdit e, GridControl grid, long row, int dataColumn) =>
            ReferenceEquals(e.Grid, grid) && e.Row == row && e.DataColumn == dataColumn;
    }
}
