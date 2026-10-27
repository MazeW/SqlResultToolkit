using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SqlCellInspector.Format
{
    /// <summary>
    /// Renders a CLR value as a T-SQL literal.
    ///
    /// Date and time values are written in the ISO 8601 "T" form, which SQL Server reads
    /// the same way regardless of SET LANGUAGE or SET DATEFORMAT. Precision is matched to
    /// the declared type: handing seven fractional digits to a <c>datetime</c> column is a
    /// conversion error, so that type gets three.
    ///
    /// Everything here is pure, which is the point: this is the part of script generation
    /// that has to be right, and it is covered by tests that need no grid and no server.
    /// </summary>
    internal static class SqlLiteral
    {
        public const string Null = "NULL";

        public static string Format(object value, string serverTypeName, SqlTypeCategory category)
        {
            if (value == null || value is DBNull)
            {
                return Null;
            }

            value = Unwrap(value);
            if (value == null)
            {
                return Null;
            }

            switch (category)
            {
                case SqlTypeCategory.AnsiText:
                    return Quote(AsString(value), unicode: false);

                case SqlTypeCategory.UnicodeText:
                case SqlTypeCategory.Xml:
                case SqlTypeCategory.Json:
                case SqlTypeCategory.Vector:
                    return Quote(AsString(value), unicode: true);

                case SqlTypeCategory.Binary:
                case SqlTypeCategory.RowVersion:
                    return value is byte[] bytes ? Hex(bytes) : Quote(AsString(value), unicode: true);

                case SqlTypeCategory.Integer:
                    return FormatInteger(value);

                case SqlTypeCategory.Decimal:
                case SqlTypeCategory.Money:
                    return value is decimal d
                        ? d.ToString(CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture);

                case SqlTypeCategory.Float:
                    return FormatFloat(value);

                case SqlTypeCategory.Bit:
                    return FormatBit(value);

                case SqlTypeCategory.Date:
                    return value is DateTime date
                        ? "'" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'"
                        : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.DateTime:
                    return FormatDateTime(value, serverTypeName);

                case SqlTypeCategory.DateTimeOffset:
                    return value is DateTimeOffset dto
                        ? "'" + dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'"
                        : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.Time:
                    return value is TimeSpan ts
                        ? "'" + FormatTimeSpan(ts) + "'"
                        : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.Guid:
                    return value is Guid guid
                        ? "'" + guid.ToString("D").ToUpperInvariant() + "'"
                        : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.Spatial:
                    // geometry and geography both convert implicitly from their serialised
                    // varbinary form, which round-trips exactly. WKT would lose Z and M.
                    return value is byte[] udt ? Hex(udt) : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.HierarchyId:
                    return value is byte[] hid ? Hex(hid) : Quote(AsString(value), unicode: false);

                case SqlTypeCategory.Variant:
                    // Re-dispatch on what it actually holds.
                    var inner = SqlTypeCatalog.Classify(null, value.GetType());
                    return inner == SqlTypeCategory.Variant || inner == SqlTypeCategory.Unknown
                        ? Quote(AsString(value), unicode: true)
                        : Format(value, null, inner);

                default:
                    if (value is byte[] raw)
                    {
                        return Hex(raw);
                    }

                    return Quote(AsString(value), unicode: true);
            }
        }

        /// <summary>Bracket-quotes an identifier, escaping a literal ']' by doubling it.</summary>
        public static string QuoteIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "[]";
            }

            return "[" + name.Replace("]", "]]") + "]";
        }

        /// <summary>
        /// Builds the most qualified table name the schema row supports. The catalog is
        /// left out even when known: scripts are usually run against the database they
        /// came from, and a three-part name breaks the moment it is run elsewhere.
        /// </summary>
        public static string QualifiedName(string schema, string table)
        {
            if (string.IsNullOrEmpty(table))
            {
                return null;
            }

            return string.IsNullOrEmpty(schema)
                ? QuoteIdentifier(table)
                : QuoteIdentifier(schema) + "." + QuoteIdentifier(table);
        }

        /// <summary>
        /// Turns text the user typed into a literal of the column's type.
        ///
        /// The text is read as a *value*, not as SQL: typing <c>O'Brien</c> into an
        /// nvarchar column yields <c>N'O''Brien'</c>, not a syntax error. When the text
        /// cannot be read as the column's type, <paramref name="parsed"/> comes back false
        /// and the caller is expected to say so rather than emit a statement that will
        /// fail on the server.
        /// </summary>
        public static string FromUserText(string text, SqlTypeCategory category, string serverTypeName, out bool parsed)
        {
            parsed = true;
            if (text == null)
            {
                return Null;
            }

            string trimmed = text.Trim();
            var invariant = CultureInfo.InvariantCulture;

            switch (category)
            {
                case SqlTypeCategory.Integer:
                    if (long.TryParse(trimmed, NumberStyles.Integer, invariant, out long i)) return i.ToString(invariant);
                    break;

                case SqlTypeCategory.Decimal:
                case SqlTypeCategory.Money:
                    if (decimal.TryParse(trimmed, NumberStyles.Number, invariant, out decimal m)) return m.ToString(invariant);
                    break;

                case SqlTypeCategory.Float:
                    if (double.TryParse(trimmed, NumberStyles.Float, invariant, out double f)) return f.ToString("R", invariant);
                    break;

                case SqlTypeCategory.Bit:
                    if (trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
                    if (trimmed == "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
                    break;

                case SqlTypeCategory.Guid:
                    if (Guid.TryParse(trimmed, out Guid g)) return "'" + g.ToString("D").ToUpperInvariant() + "'";
                    break;

                case SqlTypeCategory.Date:
                    if (DateTime.TryParse(trimmed, invariant, DateTimeStyles.None, out DateTime dateOnly))
                        return Format(dateOnly.Date, serverTypeName, SqlTypeCategory.Date);
                    break;

                case SqlTypeCategory.DateTime:
                    if (DateTime.TryParse(trimmed, invariant, DateTimeStyles.None, out DateTime dt))
                        return Format(dt, serverTypeName, SqlTypeCategory.DateTime);
                    break;

                case SqlTypeCategory.DateTimeOffset:
                    if (DateTimeOffset.TryParse(trimmed, invariant, DateTimeStyles.None, out DateTimeOffset dto))
                        return Format(dto, serverTypeName, SqlTypeCategory.DateTimeOffset);
                    break;

                case SqlTypeCategory.Time:
                    if (TimeSpan.TryParse(trimmed, invariant, out TimeSpan ts))
                        return Format(ts, serverTypeName, SqlTypeCategory.Time);
                    break;

                case SqlTypeCategory.Binary:
                case SqlTypeCategory.RowVersion:
                case SqlTypeCategory.Spatial:
                    if (TryParseHex(trimmed, out byte[] bytes)) return Hex(bytes);
                    break;

                case SqlTypeCategory.AnsiText:
                    return Quote(text, unicode: false);

                default:
                    return Quote(text, unicode: true);
            }

            // Did not parse as the declared type. Hand back a quoted string so the shape
            // of the statement is still right, and let the caller flag it.
            parsed = false;
            return Quote(text, unicode: true);
        }

        private static bool TryParseHex(string text, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string body = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text;
            body = body.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (body.Length == 0 || body.Length % 2 != 0)
            {
                return false;
            }

            var result = new byte[body.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(body.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                {
                    return false;
                }
            }

            bytes = result;
            return true;
        }

        /// <summary>
        /// Turns what the user typed in the Table box into a safely quoted name. Accepts
        /// "Orders", "dbo.Orders", "[dbo].[Orders]" and "My.Schema"-style names that are
        /// already bracketed, and re-quotes anything that is not.
        /// </summary>
        public static string QualifiedNameFromUserInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "[<table>]";
            }

            var parts = SplitQualifiedName(input.Trim());
            return parts.Count == 1
                ? QuoteIdentifier(parts[0])
                : string.Join(".", parts.Select(QuoteIdentifier));
        }

        /// <summary>
        /// Splits a possibly-bracketed dotted name into its parts. A '.' inside brackets
        /// belongs to the identifier, so "[My.Schema].[T]" is two parts, not three.
        /// </summary>
        private static List<string> SplitQualifiedName(string input)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inBrackets = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '[' && !inBrackets)
                {
                    inBrackets = true;
                }
                else if (c == ']' && inBrackets)
                {
                    if (i + 1 < input.Length && input[i + 1] == ']')
                    {
                        current.Append(']');
                        i++;
                    }
                    else
                    {
                        inBrackets = false;
                    }
                }
                else if (c == '.' && !inBrackets)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            parts.Add(current.ToString().Trim());
            parts.RemoveAll(string.IsNullOrEmpty);

            if (parts.Count == 0)
            {
                parts.Add("<table>");
            }

            return parts;
        }

        // ---- pieces ------------------------------------------------------------------

        private static string Quote(string text, bool unicode)
        {
            if (text == null)
            {
                return Null;
            }

            return (unicode ? "N'" : "'") + text.Replace("'", "''") + "'";
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(2 + bytes.Length * 2);
            sb.Append("0x");
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static string FormatInteger(object value)
        {
            switch (value)
            {
                case byte b: return b.ToString(CultureInfo.InvariantCulture);
                case sbyte sb: return sb.ToString(CultureInfo.InvariantCulture);
                case short s: return s.ToString(CultureInfo.InvariantCulture);
                case ushort us: return us.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case uint ui: return ui.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case ulong ul: return ul.ToString(CultureInfo.InvariantCulture);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static string FormatFloat(object value)
        {
            if (value is double dbl)
            {
                if (double.IsNaN(dbl) || double.IsInfinity(dbl))
                {
                    // Neither is representable in a float column; say so rather than
                    // emitting something that will fail at run time.
                    return "/* " + dbl.ToString(CultureInfo.InvariantCulture) + " is not representable */ NULL";
                }

                return dbl.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value is float flt)
            {
                if (float.IsNaN(flt) || float.IsInfinity(flt))
                {
                    return "/* " + flt.ToString(CultureInfo.InvariantCulture) + " is not representable */ NULL";
                }

                return flt.ToString("R", CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string FormatBit(object value)
        {
            if (value is bool b)
            {
                return b ? "1" : "0";
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase)) return "1";
            if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase)) return "0";
            return text;
        }

        private static string FormatDateTime(object value, string serverTypeName)
        {
            if (!(value is DateTime dt))
            {
                return Quote(AsString(value), unicode: false);
            }

            // datetime holds 1/300 s and rejects a 7-digit fraction; smalldatetime holds
            // whole minutes. datetime2 (and an unknown type) gets full precision.
            string normalized = Normalize(serverTypeName);
            string format;
            switch (normalized)
            {
                case "smalldatetime":
                    format = "yyyy-MM-ddTHH:mm:00";
                    break;
                case "datetime":
                    format = "yyyy-MM-ddTHH:mm:ss.fff";
                    break;
                default:
                    format = "yyyy-MM-ddTHH:mm:ss.fffffff";
                    break;
            }

            return "'" + dt.ToString(format, CultureInfo.InvariantCulture) + "'";
        }

        /// <summary>
        /// A <c>time</c> value can legitimately reach 23:59:59.9999999 but never a day
        /// component, so the day part is folded into the hours rather than dropped.
        /// </summary>
        private static string FormatTimeSpan(TimeSpan ts)
        {
            int hours = (int)ts.TotalHours;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:0000000}",
                hours,
                ts.Minutes,
                ts.Seconds,
                ts.Ticks % TimeSpan.TicksPerSecond);
        }

        private static string Normalize(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return string.Empty;
            }

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

            return name.Trim().Trim('[', ']').ToLowerInvariant();
        }

        private static string AsString(object value) =>
            value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

        /// <summary>
        /// Unwraps a System.Data.SqlTypes value (SqlInt32, SqlString, SqlBinary, ...) to
        /// the CLR value it carries, mapping a null one to CLR null.
        /// </summary>
        private static object Unwrap(object value)
        {
            var type = value.GetType();
            if (!type.FullName?.StartsWith("System.Data.SqlTypes.", StringComparison.Ordinal) ?? true)
            {
                return value;
            }

            try
            {
                var isNull = type.GetProperty("IsNull");
                if (isNull?.GetValue(value) is bool b && b)
                {
                    return null;
                }

                var inner = type.GetProperty("Value");
                return inner != null ? inner.GetValue(value) : value;
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
