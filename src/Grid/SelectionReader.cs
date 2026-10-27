using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Microsoft.SqlServer.Management.UI.Grid;
using SqlCellInspector.Format;

namespace SqlCellInspector.Grid
{
    /// <summary>
    /// Translates between the grid's column indices (row-number gutter at 0, what
    /// IGridStorage is addressed by) and the result set's own 0-based data indices (what
    /// the metadata calls are addressed by). Getting these two confused reads the cell one
    /// to the left, so every caller goes through here.
    /// </summary>
    internal readonly struct GridIndexMap
    {
        private GridIndexMap(int gutter, int dataColumnCount)
        {
            Gutter = gutter;
            DataColumnCount = dataColumnCount;
        }

        public int Gutter { get; }

        /// <summary>-1 when the result set did not report a column count.</summary>
        public int DataColumnCount { get; }

        public static GridIndexMap For(GridControl grid, ResultSetAccessor accessor, object storage)
        {
            int gridColumns = -1;
            try
            {
                gridColumns = grid.ColumnsNumber;
            }
            catch (Exception)
            {
                // leave unknown
            }

            int? dataColumns = accessor?.GetColumnCount(storage);
            int gutter = (dataColumns.HasValue && gridColumns > dataColumns.Value)
                ? gridColumns - dataColumns.Value
                : 0;

            return new GridIndexMap(gutter, dataColumns ?? -1);
        }

        /// <summary>Maps a UI column (as displayed, possibly reordered) to the grid/storage index.</summary>
        public static int ToGridColumn(GridControl grid, int uiColumn)
        {
            try
            {
                return grid.GetStorageColumnIndexByUIIndex(uiColumn);
            }
            catch (Exception)
            {
                return uiColumn;
            }
        }

        public int ToDataColumn(int gridColumn) => gridColumn - Gutter;

        public int ToGridColumnFromData(int dataColumn) => dataColumn + Gutter;

        public bool IsDataColumn(int gridColumn)
        {
            int data = ToDataColumn(gridColumn);
            return data >= 0 && (DataColumnCount < 0 || data < DataColumnCount);
        }
    }

    /// <summary>
    /// Reads every column's scripting metadata once per result set. The schema row is the
    /// expensive part, and a selection needs it for all columns, not just the current one.
    /// </summary>
    internal static class ColumnCatalog
    {
        public static List<ScriptColumn> Read(ResultSetAccessor accessor, object storage, GridIndexMap map)
        {
            var columns = new List<ScriptColumn>();
            if (accessor == null || map.DataColumnCount < 0)
            {
                return columns;
            }

            for (int data = 0; data < map.DataColumnCount; data++)
            {
                string serverType = accessor.GetServerTypeName(storage, data);
                var clrType = accessor.GetFieldType(storage, data);
                var schema = accessor.GetSchemaRow(storage, data);

                var column = new ScriptColumn
                {
                    Name = accessor.GetColumnName(storage, data) ?? ("Column" + (data + 1)),
                    ServerTypeName = serverType,
                    Category = SqlTypeCatalog.Classify(serverType, clrType),
                    IsKey = Flag(schema, "IsKey"),
                    IsIdentity = Flag(schema, "IsIdentity") || Flag(schema, "IsAutoIncrement"),
                    IsComputed = Flag(schema, "IsExpression"),
                    IsReadOnly = Flag(schema, "IsReadOnly"),
                    AllowDbNull = Flag(schema, "AllowDBNull", fallback: true),
                    BaseSchema = Text(schema, "BaseSchemaName"),
                    BaseTable = Text(schema, "BaseTableName"),
                    BaseColumn = Text(schema, "BaseColumnName")
                };

                columns.Add(column);
            }

            return columns;
        }

        private static bool Flag(DataRow row, string column, bool fallback = false)
        {
            try
            {
                if (row == null || !row.Table.Columns.Contains(column))
                {
                    return fallback;
                }

                object value = row[column];
                return value is DBNull || value == null ? fallback : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string Text(DataRow row, string column)
        {
            try
            {
                if (row == null || !row.Table.Columns.Contains(column))
                {
                    return null;
                }

                object value = row[column];
                if (value == null || value is DBNull)
                {
                    return null;
                }

                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>One column of the current row, for the record view.</summary>
    internal sealed class RowField
    {
        public ScriptColumn Column { get; set; }

        public object Value { get; set; }

        public string Text { get; set; }

        public bool IsNull { get; set; }

        public bool IsCurrent { get; set; }
    }

    internal static class SelectionReader
    {
        /// <summary>
        /// Builds a scripting selection from whatever is selected in the grid. Key columns
        /// are read even when they are not part of the selection, because the WHERE clause
        /// needs them.
        /// </summary>
        /// <param name="extraKeyColumns">
        /// Column names the user typed in the Key box. These must be read even when they
        /// are not selected and the provider does not flag them as keys, which is the
        /// normal case: a plain SELECT reports no key at all, so without this the typed
        /// key would be reported as "not in the result set".
        /// </param>
        /// <param name="extraRows">
        /// Rows that must be read even when they are not selected, because the user staged
        /// an edit on them. Without this, staging an edit and then clicking elsewhere would
        /// lose the key values needed to build that row's WHERE clause.
        /// </param>
        public static ScriptSelection ReadSelection(
            GridControl grid,
            IReadOnlyList<string> extraKeyColumns = null,
            IReadOnlyList<long> extraRows = null,
            int rowLimit = ScriptGenerator.DefaultRowLimit)
        {
            var storage = SafeStorage(grid);
            if (storage == null)
            {
                return null;
            }

            var accessor = ResultSetAccessor.For(storage);
            var map = GridIndexMap.For(grid, accessor, storage);
            var catalog = ColumnCatalog.Read(accessor, storage, map);
            if (catalog.Count == 0)
            {
                return null;
            }

            CollectSelection(grid, map, out var dataColumns, out var rows, out int omitted, rowLimit);

            if (extraRows != null && extraRows.Count > 0)
            {
                foreach (long row in extraRows)
                {
                    if (row >= 0 && !rows.Contains(row))
                    {
                        rows.Add(row);
                    }
                }

                rows.Sort();
            }

            if (dataColumns.Count == 0 || rows.Count == 0)
            {
                return null;
            }

            // Columns we must read: the selected ones, plus any key column so the
            // predicate can be built even when the key is off-screen or unselected.
            var needed = new List<int>(dataColumns);
            for (int i = 0; i < catalog.Count; i++)
            {
                var column = catalog[i];
                bool isKey = column.IsKey || MatchesAny(column, extraKeyColumns);

                if (isKey && !needed.Contains(i))
                {
                    needed.Add(i);
                }
            }

            needed.Sort();

            var columns = needed.Select(i => catalog[i]).ToList();
            var selectedIndexes = dataColumns
                .Select(d => needed.IndexOf(d))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();

            bool incomplete = false;
            var scriptRows = new List<ScriptRow>(rows.Count);

            foreach (long row in rows)
            {
                try
                {
                    storage.EnsureRowsInBuf(row, row);
                }
                catch (Exception)
                {
                    // eager storages do not need it
                }

                var values = new object[needed.Count];
                for (int i = 0; i < needed.Count; i++)
                {
                    values[i] = accessor.GetCellData(storage, row, map.ToGridColumnFromData(needed[i]), out bool ok);
                    if (!ok)
                    {
                        incomplete = true;
                    }
                }

                scriptRows.Add(new ScriptRow { GridRow = row, Values = values });
            }

            return new ScriptSelection
            {
                Columns = columns,
                SelectedColumns = selectedIndexes,
                Rows = scriptRows,
                RowsOmitted = omitted,
                ValuesIncomplete = incomplete
            };
        }

        /// <summary>Reads every column of one row, for the record view.</summary>
        public static List<RowField> ReadRow(GridControl grid, long row, int currentDataColumn)
        {
            var fields = new List<RowField>();
            var storage = SafeStorage(grid);
            if (storage == null)
            {
                return fields;
            }

            var accessor = ResultSetAccessor.For(storage);
            var map = GridIndexMap.For(grid, accessor, storage);
            var catalog = ColumnCatalog.Read(accessor, storage, map);

            try
            {
                storage.EnsureRowsInBuf(row, row);
            }
            catch (Exception)
            {
                // eager storages do not need it
            }

            for (int data = 0; data < catalog.Count; data++)
            {
                int gridColumn = map.ToGridColumnFromData(data);
                object value = accessor?.GetCellData(storage, row, gridColumn, out bool _);

                string text;
                try
                {
                    text = storage.GetCellDataAsString(row, gridColumn);
                }
                catch (Exception)
                {
                    text = null;
                }

                fields.Add(new RowField
                {
                    Column = catalog[data],
                    Value = value,
                    Text = text,
                    IsNull = value == null,
                    IsCurrent = data == currentDataColumn
                });
            }

            return fields;
        }

        /// <summary>
        /// Flattens the selected blocks into a distinct, ordered set of data columns and
        /// rows. The grid hands back rectangles that can overlap, so both are de-duplicated.
        /// </summary>
        private static void CollectSelection(
            GridControl grid,
            GridIndexMap map,
            out List<int> dataColumns,
            out List<long> rows,
            out int omitted,
            int rowLimit)
        {
            var columnSet = new SortedSet<int>();
            var rowSet = new SortedSet<long>();
            omitted = 0;

            BlockOfCellsCollection blocks = null;
            try
            {
                blocks = grid.SelectedCells;
            }
            catch (Exception)
            {
                // fall through to the current cell
            }

            if (blocks != null)
            {
                for (int b = 0; b < blocks.Count; b++)
                {
                    var block = blocks[b];
                    if (block == null || block.IsEmpty)
                    {
                        continue;
                    }

                    for (int c = block.X; c < block.X + block.Width; c++)
                    {
                        if (map.IsDataColumn(c))
                        {
                            columnSet.Add(map.ToDataColumn(c));
                        }
                    }

                    for (long r = block.Y; r < block.Y + block.Height; r++)
                    {
                        rowSet.Add(r);
                    }
                }
            }

            if (columnSet.Count == 0 || rowSet.Count == 0)
            {
                // Nothing (or only the gutter) selected: fall back to the current cell.
                try
                {
                    grid.GetCurrentCell(out long row, out int uiColumn);
                    int gridColumn = GridIndexMap.ToGridColumn(grid, uiColumn);
                    if (row >= 0 && map.IsDataColumn(gridColumn))
                    {
                        rowSet.Add(row);
                        columnSet.Add(map.ToDataColumn(gridColumn));
                    }
                }
                catch (Exception)
                {
                    // nothing we can do
                }
            }

            dataColumns = columnSet.ToList();

            if (rowSet.Count > rowLimit)
            {
                omitted = rowSet.Count - rowLimit;
                rows = rowSet.Take(rowLimit).ToList();
            }
            else
            {
                rows = rowSet.ToList();
            }
        }

        private static bool MatchesAny(ScriptColumn column, IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(column.TargetName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IGridStorage SafeStorage(GridControl grid)
        {
            try
            {
                return grid?.GridStorage;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
