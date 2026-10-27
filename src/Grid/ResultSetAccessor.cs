using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SqlResultToolkit.Grid
{
    /// <summary>
    /// Late-bound access to the object behind <see cref="GridControl.GridStorage"/>.
    ///
    /// For query results that object is SQLEditors' <c>QEResultSet</c>, which is internal,
    /// so the typed members are reached by name. Everything here degrades: if a member is
    /// missing the accessor reports "unavailable" rather than throwing, and the panel falls
    /// back to the public <see cref="IGridStorage"/> surface, which always works.
    /// </summary>
    internal sealed class ResultSetAccessor
    {
        private static readonly Dictionary<Type, ResultSetAccessor> Cache = new Dictionary<Type, ResultSetAccessor>();

        private readonly MethodInfo _getCellData;         // object GetCellData(long, int)
        private readonly MethodInfo _getServerTypeName;   // string GetServerDataTypeName(int)
        private readonly MethodInfo _getFormattedType;    // string GetFormattedDataTypeName(int)
        private readonly MethodInfo _getFieldType;        // Type GetFieldType(int)
        private readonly MethodInfo _getSchemaRow;        // DataRow GetSchemaRow(int)
        private readonly MethodInfo _isXmlColumn;         // bool IsXMLColumn(int)
        private readonly MethodInfo _isJsonColumn;        // bool IsJsonColumn(int)
        private readonly PropertyInfo _numberOfDataColumns;
        private readonly PropertyInfo _totalNumberOfRows;
        private readonly PropertyInfo _columnNames;

        private ResultSetAccessor(Type type)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            _getCellData = type.GetMethod("GetCellData", Flags, null, new[] { typeof(long), typeof(int) }, null);
            _getServerTypeName = type.GetMethod("GetServerDataTypeName", Flags, null, new[] { typeof(int) }, null);
            _getFormattedType = type.GetMethod("GetFormattedDataTypeName", Flags, null, new[] { typeof(int) }, null);
            _getFieldType = type.GetMethod("GetFieldType", Flags, null, new[] { typeof(int) }, null);
            _getSchemaRow = type.GetMethod("GetSchemaRow", Flags, null, new[] { typeof(int) }, null);
            _isXmlColumn = type.GetMethod("IsXMLColumn", Flags, null, new[] { typeof(int) }, null);
            _isJsonColumn = type.GetMethod("IsJsonColumn", Flags, null, new[] { typeof(int) }, null);
            _numberOfDataColumns = type.GetProperty("NumberOfDataColumns", Flags);
            _totalNumberOfRows = type.GetProperty("TotalNumberOfRows", Flags);
            _columnNames = type.GetProperty("ColumnNames", Flags);
        }

        public static ResultSetAccessor For(object storage)
        {
            if (storage == null)
            {
                return null;
            }

            var type = storage.GetType();
            lock (Cache)
            {
                if (!Cache.TryGetValue(type, out var accessor))
                {
                    accessor = new ResultSetAccessor(type);
                    Cache[type] = accessor;
                }

                return accessor;
            }
        }

        public bool HasTypedValue => _getCellData != null;

        public object GetCellData(object storage, long row, int column, out bool available)
        {
            available = false;
            if (_getCellData == null)
            {
                return null;
            }

            try
            {
                var value = _getCellData.Invoke(storage, new object[] { row, column });
                available = true;
                return value is DBNull ? null : value;
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("GetCellData failed.", Unwrap(ex));
                return null;
            }
        }

        public string GetServerTypeName(object storage, int column) =>
            Call<string>(_getServerTypeName, storage, new object[] { column });

        public string GetFormattedTypeName(object storage, int column) =>
            Call<string>(_getFormattedType, storage, new object[] { column });

        public Type GetFieldType(object storage, int column) =>
            Call<Type>(_getFieldType, storage, new object[] { column });

        public DataRow GetSchemaRow(object storage, int column) =>
            Call<DataRow>(_getSchemaRow, storage, new object[] { column });

        public bool? IsXmlColumn(object storage, int column) =>
            Call<bool>(_isXmlColumn, storage, new object[] { column });

        public bool? IsJsonColumn(object storage, int column) =>
            Call<bool>(_isJsonColumn, storage, new object[] { column });

        public int? GetColumnCount(object storage) =>
            GetProperty<int>(_numberOfDataColumns, storage);

        public long? GetTotalRows(object storage) =>
            GetProperty<long>(_totalNumberOfRows, storage);

        public string GetColumnName(object storage, int column)
        {
            if (_columnNames == null)
            {
                return null;
            }

            try
            {
                if (_columnNames.GetValue(storage) is IList names && column >= 0 && column < names.Count)
                {
                    return names[column] as string;
                }
            }
            catch (Exception)
            {
                // fall through
            }

            return null;
        }

        private static T Call<T>(MethodInfo method, object target, object[] args)
        {
            if (method == null)
            {
                return default;
            }

            try
            {
                var result = method.Invoke(target, args);
                return result is T typed ? typed : default;
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static T? GetProperty<T>(PropertyInfo property, object target) where T : struct
        {
            if (property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(target) is T typed ? typed : (T?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Exception Unwrap(Exception ex) =>
            ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
    }

    /// <summary>
    /// Turns a (grid, row, column) position into a <see cref="CellSnapshot"/>.
    /// </summary>
    internal static class CellReader
    {
        public static CellSnapshot Read(GridControl grid, long row, int uiColumn)
        {
            var storage = grid.GridStorage;
            if (storage == null)
            {
                return null;
            }

            var accessor = ResultSetAccessor.For(storage);

            // GridIndexMap owns the two column index spaces (see its remarks). Dragging a
            // column header reorders the display, so the UI index must go through
            // GetStorageColumnIndexByUIIndex before either space is derived from it.
            var map = GridIndexMap.For(grid, accessor, storage);
            int gridColumn = GridIndexMap.ToGridColumn(grid, uiColumn);
            int dataColumn = map.ToDataColumn(gridColumn);

            var cell = new CellSnapshot
            {
                SourceGrid = grid,
                Row = row,
                UiColumn = uiColumn,
                StorageColumn = dataColumn,
                ColumnCount = map.DataColumnCount,
                TotalRows = accessor?.GetTotalRows(storage) ?? SafeRowCount(storage)
            };

            if (!map.IsDataColumn(gridColumn))
            {
                // The row-number gutter, not a data cell.
                cell.StorageColumn = -1;
                return cell;
            }

            // Prefer the result set's own column name over the grid header. Both are
            // normally identical, but the result-set name is indexed by dataColumn, the
            // same index the type and value come from, so preferring it guarantees the
            // name, type and value on screen all describe the same column even if the
            // header turns out to be indexed differently after a reorder.
            string resultSetName = accessor?.GetColumnName(storage, dataColumn);
            string headerName = ReadHeader(grid, uiColumn);

            cell.ColumnName = !string.IsNullOrEmpty(resultSetName)
                ? resultSetName
                : headerName ?? ("Column " + (dataColumn + 1));

            if (!string.IsNullOrEmpty(headerName) &&
                !string.IsNullOrEmpty(resultSetName) &&
                !string.Equals(headerName, resultSetName, StringComparison.Ordinal))
            {
                cell.GridHeaderName = headerName;
            }

            // Make sure the row is actually paged in before asking for its value.
            try
            {
                storage.EnsureRowsInBuf(row, row);
            }
            catch (Exception)
            {
                // Some storages page eagerly and do not need this.
            }

            if (accessor != null)
            {
                cell.ServerTypeName = accessor.GetServerTypeName(storage, dataColumn);
                cell.FormattedTypeName = accessor.GetFormattedTypeName(storage, dataColumn);
                cell.FieldType = accessor.GetFieldType(storage, dataColumn);
                cell.IsXmlColumn = accessor.IsXmlColumn(storage, dataColumn) ?? false;
                cell.IsJsonColumn = accessor.IsJsonColumn(storage, dataColumn) ?? false;
                cell.SchemaRow = accessor.GetSchemaRow(storage, dataColumn);

                cell.Value = accessor.GetCellData(storage, row, gridColumn, out bool available);
                cell.ValueUnavailable = !available;

                // Deliberately not IsCellDataNull: which of the two index spaces it takes
                // is not established, and once GetCellData has answered, a null (DBNull is
                // mapped to null on the way out) is already a decisive NULL test.
                cell.IsNull = available && cell.Value == null;
            }
            else
            {
                cell.ValueUnavailable = true;
            }

            try
            {
                cell.Text = storage.GetCellDataAsString(row, gridColumn);
            }
            catch (Exception)
            {
                cell.Text = null;
            }

            if (!cell.IsNull && cell.Value == null && cell.ValueUnavailable)
            {
                // Only the string form is real. SSMS renders NULL as the literal "NULL".
                cell.IsNull = string.Equals(cell.Text, "NULL", StringComparison.Ordinal);
            }

            cell.MayBeTruncated = LooksTruncated(cell);
            return cell;
        }

        private static string ReadHeader(GridControl grid, int uiColumn)
        {
            try
            {
                grid.GetHeaderInfo(uiColumn, out string text, out System.Drawing.Bitmap _);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }


        private static long SafeRowCount(IGridStorage storage)
        {
            try
            {
                return storage.NumRows();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// SSMS caps how much character data it keeps per cell (Tools &gt; Options &gt;
        /// Query Results &gt; SQL Server &gt; Results to Grid). When a value lands exactly on
        /// one of those caps and the column is wider than the cap, what we hold is almost
        /// certainly a prefix. Flagged, never silently presented as the whole value.
        /// </summary>
        private static bool LooksTruncated(CellSnapshot cell)
        {
            int length = (cell.Value as string)?.Length ?? cell.Text?.Length ?? 0;
            if (length == 0)
            {
                return false;
            }

            // Defaults and the values the options page offers.
            int[] caps = { 43679, 65535, 2097152, 1048576, 524288, 262144, 8192, 256 };
            foreach (var cap in caps)
            {
                if (length == cap)
                {
                    cell.MaxCharsSetting = cap;
                    return true;
                }
            }

            return false;
        }
    }
}
