using System;
using System.Collections.Generic;

namespace SqlResultToolkit.Format
{
    internal enum SqlTypeCategory
    {
        Unknown,
        AnsiText,
        UnicodeText,
        Binary,
        RowVersion,
        Integer,
        Decimal,
        Money,
        Float,
        Bit,
        Date,
        Time,
        DateTime,
        DateTimeOffset,
        Guid,
        Xml,
        Json,
        Vector,
        Variant,
        Spatial,
        HierarchyId,
        ClrUdt
    }

    /// <summary>
    /// Classifies a SQL Server column by its server type name, which is what the panel
    /// dispatches formatting on. Covers every type a SELECT can return, including the
    /// built-in CLR types and the types added in SQL Server 2022/2025.
    /// </summary>
    internal static class SqlTypeCatalog
    {
        private static readonly Dictionary<string, SqlTypeCategory> Map =
            new Dictionary<string, SqlTypeCategory>(StringComparer.OrdinalIgnoreCase)
            {
                // Exact numerics
                { "bigint", SqlTypeCategory.Integer },
                { "int", SqlTypeCategory.Integer },
                { "smallint", SqlTypeCategory.Integer },
                { "tinyint", SqlTypeCategory.Integer },
                { "bit", SqlTypeCategory.Bit },
                { "decimal", SqlTypeCategory.Decimal },
                { "numeric", SqlTypeCategory.Decimal },
                { "money", SqlTypeCategory.Money },
                { "smallmoney", SqlTypeCategory.Money },

                // Approximate numerics
                { "float", SqlTypeCategory.Float },
                { "real", SqlTypeCategory.Float },

                // Date and time
                { "date", SqlTypeCategory.Date },
                { "time", SqlTypeCategory.Time },
                { "datetime", SqlTypeCategory.DateTime },
                { "datetime2", SqlTypeCategory.DateTime },
                { "smalldatetime", SqlTypeCategory.DateTime },
                { "datetimeoffset", SqlTypeCategory.DateTimeOffset },

                // Character strings
                { "char", SqlTypeCategory.AnsiText },
                { "varchar", SqlTypeCategory.AnsiText },
                { "text", SqlTypeCategory.AnsiText },
                { "nchar", SqlTypeCategory.UnicodeText },
                { "nvarchar", SqlTypeCategory.UnicodeText },
                { "ntext", SqlTypeCategory.UnicodeText },
                { "sysname", SqlTypeCategory.UnicodeText },

                // Binary strings
                { "binary", SqlTypeCategory.Binary },
                { "varbinary", SqlTypeCategory.Binary },
                { "image", SqlTypeCategory.Binary },
                { "timestamp", SqlTypeCategory.RowVersion },
                { "rowversion", SqlTypeCategory.RowVersion },

                // Other
                { "uniqueidentifier", SqlTypeCategory.Guid },
                { "xml", SqlTypeCategory.Xml },
                { "json", SqlTypeCategory.Json },
                { "vector", SqlTypeCategory.Vector },
                { "sql_variant", SqlTypeCategory.Variant },

                // Built-in CLR types
                { "hierarchyid", SqlTypeCategory.HierarchyId },
                { "geometry", SqlTypeCategory.Spatial },
                { "geography", SqlTypeCategory.Spatial },
            };

        public static SqlTypeCategory Classify(string serverTypeName, Type clrType)
        {
            if (!string.IsNullOrEmpty(serverTypeName))
            {
                // "sys.geography", "varbinary(max)" and the like all reduce to a bare name.
                string name = Normalize(serverTypeName);
                if (Map.TryGetValue(name, out var category))
                {
                    return category;
                }
            }

            return FromClrType(clrType);
        }

        private static string Normalize(string typeName)
        {
            string name = typeName.Trim();

            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1)
            {
                name = name.Substring(dot + 1);
            }

            int paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name.Substring(0, paren);
            }

            return name.Trim().Trim('[', ']');
        }

        private static SqlTypeCategory FromClrType(Type clrType)
        {
            if (clrType == null)
            {
                return SqlTypeCategory.Unknown;
            }

            if (clrType == typeof(string)) return SqlTypeCategory.UnicodeText;
            if (clrType == typeof(byte[])) return SqlTypeCategory.Binary;
            if (clrType == typeof(bool)) return SqlTypeCategory.Bit;
            if (clrType == typeof(byte) || clrType == typeof(short) || clrType == typeof(int) || clrType == typeof(long))
                return SqlTypeCategory.Integer;
            if (clrType == typeof(decimal)) return SqlTypeCategory.Decimal;
            if (clrType == typeof(float) || clrType == typeof(double)) return SqlTypeCategory.Float;
            if (clrType == typeof(DateTime)) return SqlTypeCategory.DateTime;
            if (clrType == typeof(DateTimeOffset)) return SqlTypeCategory.DateTimeOffset;
            if (clrType == typeof(TimeSpan)) return SqlTypeCategory.Time;
            if (clrType == typeof(Guid)) return SqlTypeCategory.Guid;
            if (clrType == typeof(object)) return SqlTypeCategory.Variant;

            // Microsoft.SqlServer.Types.SqlGeography / SqlGeometry / SqlHierarchyId arrive
            // as UDTs; match on name so we do not need a reference to that assembly.
            switch (clrType.Name)
            {
                case "SqlGeography":
                case "SqlGeometry":
                    return SqlTypeCategory.Spatial;
                case "SqlHierarchyId":
                    return SqlTypeCategory.HierarchyId;
                case "SqlXml":
                    return SqlTypeCategory.Xml;
                case "SqlJson":
                    return SqlTypeCategory.Json;
                case "SqlVector`1":
                    return SqlTypeCategory.Vector;
            }

            return clrType.IsValueType || clrType.IsClass ? SqlTypeCategory.ClrUdt : SqlTypeCategory.Unknown;
        }

        /// <summary>A short human label for the header chip.</summary>
        public static string Describe(SqlTypeCategory category)
        {
            switch (category)
            {
                case SqlTypeCategory.AnsiText: return "character data";
                case SqlTypeCategory.UnicodeText: return "Unicode character data";
                case SqlTypeCategory.Binary: return "binary data";
                case SqlTypeCategory.RowVersion: return "row version";
                case SqlTypeCategory.Integer: return "exact integer";
                case SqlTypeCategory.Decimal: return "exact numeric";
                case SqlTypeCategory.Money: return "currency";
                case SqlTypeCategory.Float: return "approximate numeric";
                case SqlTypeCategory.Bit: return "bit";
                case SqlTypeCategory.Date: return "date";
                case SqlTypeCategory.Time: return "time of day";
                case SqlTypeCategory.DateTime: return "date and time";
                case SqlTypeCategory.DateTimeOffset: return "date, time and offset";
                case SqlTypeCategory.Guid: return "GUID";
                case SqlTypeCategory.Xml: return "XML";
                case SqlTypeCategory.Json: return "JSON";
                case SqlTypeCategory.Vector: return "vector";
                case SqlTypeCategory.Variant: return "sql_variant";
                case SqlTypeCategory.Spatial: return "spatial";
                case SqlTypeCategory.HierarchyId: return "hierarchyid";
                case SqlTypeCategory.ClrUdt: return "CLR user-defined type";
                default: return "unknown type";
            }
        }

        /// <summary>File extension to offer in Save As for this category.</summary>
        public static string SuggestExtension(SqlTypeCategory category, string detectedContentType)
        {
            if (detectedContentType != null)
            {
                switch (detectedContentType)
                {
                    case "image/png": return ".png";
                    case "image/jpeg": return ".jpg";
                    case "image/gif": return ".gif";
                    case "image/bmp": return ".bmp";
                    case "image/webp": return ".webp";
                    case "image/x-icon": return ".ico";
                    case "application/pdf": return ".pdf";
                    case "application/gzip": return ".gz";
                }

                if (detectedContentType.StartsWith("application/zip", StringComparison.Ordinal))
                {
                    return ".zip";
                }
            }

            switch (category)
            {
                case SqlTypeCategory.Json: return ".json";
                case SqlTypeCategory.Xml: return ".xml";
                case SqlTypeCategory.Binary:
                case SqlTypeCategory.RowVersion: return ".bin";
                default: return ".txt";
            }
        }
    }
}
