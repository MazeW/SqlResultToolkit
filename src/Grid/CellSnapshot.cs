using System;

namespace SqlResultToolkit.Grid
{
    /// <summary>
    /// Everything the panel knows about one cell, captured on the UI thread at the moment
    /// the selection moved. Deliberately a value snapshot: the grid's storage can be
    /// disposed or re-paged underneath us at any time.
    /// </summary>
    internal sealed class CellSnapshot
    {
        public long Row { get; set; }

        /// <summary>Column index as the grid shows it (0 is SSMS's row-number gutter).</summary>
        public int UiColumn { get; set; }

        /// <summary>Column index in the result set, after reorder/gutter mapping.</summary>
        public int StorageColumn { get; set; }

        public string ColumnName { get; set; }

        /// <summary>e.g. "nvarchar", "varbinary", "datetimeoffset". Null if unavailable.</summary>
        public string ServerTypeName { get; set; }

        /// <summary>e.g. "nvarchar(max)", "decimal(18,4)". Null if unavailable.</summary>
        public string FormattedTypeName { get; set; }

        /// <summary>CLR type the provider reports for the column, if available.</summary>
        public Type FieldType { get; set; }

        public bool IsNull { get; set; }

        public bool IsXmlColumn { get; set; }

        public bool IsJsonColumn { get; set; }

        /// <summary>The CLR value, when the internal typed accessor was reachable.</summary>
        public object Value { get; set; }

        /// <summary>True when <see cref="Value"/> could not be obtained and only <see cref="Text"/> is real.</summary>
        public bool ValueUnavailable { get; set; }

        /// <summary>The grid's own string form of the cell. Always populated when possible.</summary>
        public string Text { get; set; }

        /// <summary>
        /// The provider's schema row for this column (ColumnSize, NumericPrecision,
        /// NumericScale, AllowDBNull, ProviderSpecificDataType, ...). Null if unavailable.
        /// </summary>
        public System.Data.DataRow SchemaRow { get; set; }

        /// <summary>
        /// The column name as the grid header shows it, when it differs from the result
        /// set's own name. Null when they agree or the header is unavailable.
        /// </summary>
        public string GridHeaderName { get; set; }

        /// <summary>1-based position among the result-set grids stacked in this tab; 0 if unknown.</summary>
        public int ResultSetIndex { get; set; }

        /// <summary>How many result-set grids are stacked in this tab; 0 if unknown.</summary>
        public int ResultSetCount { get; set; }

        /// <summary>
        /// The grid this cell came from. Held so the script and record views can read the
        /// rest of the selection on demand; UI thread only, and always re-checked for
        /// disposal before use.
        /// </summary>
        public Microsoft.SqlServer.Management.UI.Grid.GridControl SourceGrid { get; set; }

        public long TotalRows { get; set; } = -1;

        public int ColumnCount { get; set; } = -1;

        /// <summary>
        /// True when the text looks like it hit SSMS's retrieval cap, so what we show is
        /// all SSMS has in memory, not all the server sent.
        /// </summary>
        public bool MayBeTruncated { get; set; }

        public int? MaxCharsSetting { get; set; }

        public string TypeLabel =>
            !string.IsNullOrEmpty(FormattedTypeName) ? FormattedTypeName :
            !string.IsNullOrEmpty(ServerTypeName) ? ServerTypeName :
            FieldType != null ? FieldType.Name : "unknown";
    }
}
