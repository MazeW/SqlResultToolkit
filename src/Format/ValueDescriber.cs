using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SqlResultToolkit.Grid;

namespace SqlResultToolkit.Format
{
    internal sealed class Fact
    {
        public Fact(string label, string value, bool monospace = false)
        {
            Label = label;
            Value = value;
            Monospace = monospace;
        }

        public string Label { get; }

        public string Value { get; }

        public bool Monospace { get; }
    }

    /// <summary>
    /// Builds the "Details" list: the exact-value breakdown for the cell's type. This is
    /// the part that answers "what is actually in there", as opposed to the formatted view,
    /// which answers "what does it say".
    /// </summary>
    internal static class ValueDescriber
    {
        public static IReadOnlyList<Fact> Describe(CellSnapshot cell, SqlTypeCategory category, byte[] bytes, string text)
        {
            var facts = new List<Fact>();

            if (cell.IsNull)
            {
                facts.Add(new Fact("Value", "NULL"));
                AddSchemaFacts(facts, cell);
                return facts;
            }

            switch (category)
            {
                case SqlTypeCategory.AnsiText:
                case SqlTypeCategory.UnicodeText:
                case SqlTypeCategory.Json:
                case SqlTypeCategory.Xml:
                case SqlTypeCategory.Vector:
                    AddTextFacts(facts, text, category);
                    break;

                case SqlTypeCategory.Binary:
                    AddBinaryFacts(facts, bytes);
                    break;

                case SqlTypeCategory.RowVersion:
                    AddRowVersionFacts(facts, bytes);
                    break;

                case SqlTypeCategory.Integer:
                    AddIntegerFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Decimal:
                case SqlTypeCategory.Money:
                    AddDecimalFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Float:
                    AddFloatFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Bit:
                    AddBitFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Date:
                case SqlTypeCategory.DateTime:
                    AddDateTimeFacts(facts, cell.Value, text, category);
                    break;

                case SqlTypeCategory.DateTimeOffset:
                    AddDateTimeOffsetFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Time:
                    AddTimeFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Guid:
                    AddGuidFacts(facts, cell.Value, text);
                    break;

                case SqlTypeCategory.Spatial:
                    AddSpatialFacts(facts, cell.Value, bytes, text);
                    break;

                case SqlTypeCategory.HierarchyId:
                    AddHierarchyIdFacts(facts, cell.Value, bytes, text);
                    break;

                case SqlTypeCategory.Variant:
                    AddVariantFacts(facts, cell.Value, text);
                    break;

                default:
                    if (bytes != null)
                    {
                        AddBinaryFacts(facts, bytes);
                    }
                    else
                    {
                        AddTextFacts(facts, text, category);
                    }

                    if (cell.Value != null)
                    {
                        facts.Add(new Fact("CLR type", cell.Value.GetType().FullName));
                    }

                    break;
            }

            AddSchemaFacts(facts, cell);
            return facts;
        }

        // ---- text -------------------------------------------------------------------

        private static void AddTextFacts(List<Fact> facts, string text, SqlTypeCategory category)
        {
            if (text == null)
            {
                facts.Add(new Fact("Value", "(no string form available)"));
                return;
            }

            facts.Add(new Fact("Characters", text.Length.ToString("N0")));

            int utf8 = Encoding.UTF8.GetByteCount(text);
            facts.Add(new Fact("Bytes (UTF-8)", utf8.ToString("N0")));
            facts.Add(new Fact("Bytes (UTF-16)", (text.Length * 2).ToString("N0")));

            int lines = 1;
            int cr = 0, lf = 0, tab = 0, control = 0, nonAscii = 0, surrogates = 0;
            foreach (char c in text)
            {
                if (c == '\r') { cr++; }
                else if (c == '\n') { lf++; lines++; }
                else if (c == '\t') { tab++; }
                else if (char.IsControl(c)) { control++; }

                if (c > 0x7f) { nonAscii++; }
                if (char.IsSurrogate(c)) { surrogates++; }
            }

            facts.Add(new Fact("Lines", lines.ToString("N0")));

            var oddities = new List<string>();
            if (cr > 0) oddities.Add($"CR x{cr}");
            if (lf > 0) oddities.Add($"LF x{lf}");
            if (tab > 0) oddities.Add($"tab x{tab}");
            if (control > 0) oddities.Add($"other control x{control}");
            if (oddities.Count > 0)
            {
                facts.Add(new Fact("Whitespace / control", string.Join(", ", oddities)));
            }

            if (nonAscii > 0)
            {
                facts.Add(new Fact("Non-ASCII characters", nonAscii.ToString("N0") +
                    (surrogates > 0 ? $" (including {surrogates / 2:N0} astral code point(s))" : string.Empty)));
            }

            if (text.Length > 0)
            {
                if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]))
                {
                    facts.Add(new Fact("Padding", DescribePadding(text)));
                }
            }

            if (category == SqlTypeCategory.Json)
            {
                AddJsonShape(facts, text);
            }
            else if (category == SqlTypeCategory.Xml)
            {
                AddXmlShape(facts, text);
            }
            else
            {
                // A character column very often carries JSON or XML anyway.
                if (JsonPrettyPrinter.LooksLikeJson(text))
                {
                    facts.Add(new Fact("Content sniff", "parses as JSON"));
                    AddJsonShape(facts, text);
                }
                else if (XmlPrettyPrinter.LooksLikeXml(text))
                {
                    facts.Add(new Fact("Content sniff", "looks like XML"));
                    AddXmlShape(facts, text);
                }
                else if (LooksLikeBase64(text))
                {
                    facts.Add(new Fact("Content sniff", "looks like Base64"));
                    try
                    {
                        byte[] decoded = Convert.FromBase64String(text.Trim());
                        facts.Add(new Fact("Decodes to", decoded.Length.ToString("N0") + " bytes"));
                        string sniff = HexDump.DetectContentType(decoded);
                        if (sniff != null)
                        {
                            facts.Add(new Fact("Decoded content", sniff));
                        }
                    }
                    catch (FormatException)
                    {
                        // sniff was wrong; nothing to add
                    }
                }
            }
        }

        private static string DescribePadding(string text)
        {
            int lead = 0;
            while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;

            int trail = 0;
            while (trail < text.Length - lead && char.IsWhiteSpace(text[text.Length - 1 - trail])) trail++;

            var parts = new List<string>();
            if (lead > 0) parts.Add($"{lead} leading");
            if (trail > 0) parts.Add($"{trail} trailing");
            return string.Join(", ", parts) + " whitespace character(s)";
        }

        private static void AddJsonShape(List<Fact> facts, string text)
        {
            JsonPrettyPrinter.Measure(text, out int objects, out int arrays, out int properties, out int depth);
            facts.Add(new Fact("JSON shape",
                $"{objects:N0} object(s), {arrays:N0} array(s), {properties:N0} property name(s), depth {depth}"));
        }

        private static void AddXmlShape(List<Fact> facts, string text)
        {
            XmlPrettyPrinter.Measure(text, out int elements, out int attributes, out int depth);
            if (elements > 0)
            {
                facts.Add(new Fact("XML shape", $"{elements:N0} element(s), {attributes:N0} attribute(s), depth {depth}"));
            }
        }

        private static bool LooksLikeBase64(string text)
        {
            string t = text.Trim();
            if (t.Length < 16 || t.Length % 4 != 0)
            {
                return false;
            }

            int padding = 0;
            for (int i = t.Length - 1; i >= 0 && t[i] == '='; i--)
            {
                padding++;
            }

            if (padding > 2)
            {
                return false;
            }

            for (int i = 0; i < t.Length - padding; i++)
            {
                char c = t[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        // ---- binary -----------------------------------------------------------------

        private static void AddBinaryFacts(List<Fact> facts, byte[] bytes)
        {
            if (bytes == null)
            {
                facts.Add(new Fact("Value", "(binary value not available)"));
                return;
            }

            facts.Add(new Fact("Length", bytes.Length.ToString("N0") + " bytes" + FormatByteSize(bytes.Length)));

            string sniff = HexDump.DetectContentType(bytes);
            if (sniff != null)
            {
                facts.Add(new Fact("Detected content", sniff));
            }

            facts.Add(new Fact("T-SQL literal", HexDump.ToSqlLiteral(bytes, 64), monospace: true));

            try
            {
                using (var sha = SHA256.Create())
                {
                    facts.Add(new Fact("SHA-256", BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant(), monospace: true));
                }
            }
            catch (Exception)
            {
                // hashing is a nicety, not a requirement
            }

            // A varbinary very often holds text.
            string asText = TryDecodeText(bytes);
            if (asText != null)
            {
                facts.Add(new Fact("Decodes as text", Shorten(asText, 120)));
            }
        }

        private static string TryDecodeText(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > 1024 * 1024)
            {
                return null;
            }

            try
            {
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                string s = strict.GetString(bytes);
                foreach (char c in s)
                {
                    if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                    {
                        return null;
                    }
                }

                return s;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddRowVersionFacts(List<Fact> facts, byte[] bytes)
        {
            if (bytes == null)
            {
                return;
            }

            facts.Add(new Fact("Length", bytes.Length + " bytes"));
            facts.Add(new Fact("T-SQL literal", HexDump.ToSqlLiteral(bytes), monospace: true));

            if (bytes.Length == 8)
            {
                // rowversion is stored big-endian.
                ulong value = 0;
                foreach (byte b in bytes)
                {
                    value = (value << 8) | b;
                }

                facts.Add(new Fact("As unsigned integer", value.ToString("N0")));
            }
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes < 1024)
            {
                return string.Empty;
            }

            string[] units = { "KiB", "MiB", "GiB" };
            double value = bytes;
            int unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $" ({value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]})";
        }

        // ---- numbers ----------------------------------------------------------------

        private static void AddIntegerFacts(List<Fact> facts, object value, string text)
        {
            long? asLong = null;

            switch (value)
            {
                case byte b: asLong = b; break;
                case sbyte sb: asLong = sb; break;
                case short s: asLong = s; break;
                case ushort us: asLong = us; break;
                case int i: asLong = i; break;
                case uint ui: asLong = ui; break;
                case long l: asLong = l; break;
                default:
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                    {
                        asLong = parsed;
                    }

                    break;
            }

            if (!asLong.HasValue)
            {
                facts.Add(new Fact("Value", text ?? "(unavailable)"));
                return;
            }

            long v = asLong.Value;
            facts.Add(new Fact("Value", v.ToString(CultureInfo.InvariantCulture), monospace: true));
            facts.Add(new Fact("Grouped", v.ToString("N0", CultureInfo.InvariantCulture)));
            facts.Add(new Fact("Hex", "0x" + v.ToString("X"), monospace: true));
            facts.Add(new Fact("Binary", Convert.ToString(v, 2), monospace: true));

            if (value != null)
            {
                facts.Add(new Fact("CLR type", value.GetType().Name));
            }

            // Integers in a database are very often epoch timestamps.
            if (v > 946_684_800L && v < 4_102_444_800L)
            {
                facts.Add(new Fact("As Unix seconds", DateTimeOffset.FromUnixTimeSeconds(v).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "Z"));
            }
            else if (v > 946_684_800_000L && v < 4_102_444_800_000L)
            {
                facts.Add(new Fact("As Unix milliseconds", DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") + "Z"));
            }
        }

        private static void AddDecimalFacts(List<Fact> facts, object value, string text)
        {
            decimal? d = value as decimal?;
            if (!d.HasValue && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
            {
                d = parsed;
            }

            if (!d.HasValue)
            {
                facts.Add(new Fact("Value", text ?? "(unavailable)"));
                return;
            }

            facts.Add(new Fact("Value", d.Value.ToString(CultureInfo.InvariantCulture), monospace: true));
            facts.Add(new Fact("Grouped", d.Value.ToString("N" + CountDecimals(d.Value), CultureInfo.InvariantCulture)));

            int[] bits = decimal.GetBits(d.Value);
            int scale = (bits[3] >> 16) & 0x7F;
            facts.Add(new Fact("Decimal places held", scale.ToString()));
        }

        private static int CountDecimals(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            return (bits[3] >> 16) & 0x7F;
        }

        private static void AddFloatFacts(List<Fact> facts, object value, string text)
        {
            if (value is double dbl)
            {
                facts.Add(new Fact("Round-trip value", dbl.ToString("R", CultureInfo.InvariantCulture), monospace: true));
                facts.Add(new Fact("Fixed (17 sig. digits)", dbl.ToString("G17", CultureInfo.InvariantCulture), monospace: true));
                facts.Add(new Fact("IEEE 754 bits", "0x" + BitConverter.DoubleToInt64Bits(dbl).ToString("X16"), monospace: true));
                if (double.IsNaN(dbl) || double.IsInfinity(dbl))
                {
                    facts.Add(new Fact("Special", double.IsNaN(dbl) ? "NaN" : "Infinity"));
                }
            }
            else if (value is float flt)
            {
                facts.Add(new Fact("Round-trip value", flt.ToString("R", CultureInfo.InvariantCulture), monospace: true));
                facts.Add(new Fact("Fixed (9 sig. digits)", flt.ToString("G9", CultureInfo.InvariantCulture), monospace: true));
                facts.Add(new Fact("IEEE 754 bits", "0x" + BitConverter.ToUInt32(BitConverter.GetBytes(flt), 0).ToString("X8"), monospace: true));
            }
            else
            {
                facts.Add(new Fact("Value", text ?? "(unavailable)"));
            }

            facts.Add(new Fact("Note", "float and real are approximate; the grid's rendering may round."));
        }

        private static void AddBitFacts(List<Fact> facts, object value, string text)
        {
            if (value is bool b)
            {
                facts.Add(new Fact("Value", b ? "1 (true)" : "0 (false)"));
            }
            else
            {
                facts.Add(new Fact("Value", text ?? "(unavailable)"));
            }
        }

        // ---- date and time ----------------------------------------------------------

        private static void AddDateTimeFacts(List<Fact> facts, object value, string text, SqlTypeCategory category)
        {
            if (!(value is DateTime dt))
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                {
                    facts.Add(new Fact("Value", text ?? "(unavailable)"));
                    return;
                }
            }

            if (category == SqlTypeCategory.Date)
            {
                facts.Add(new Fact("ISO 8601", dt.ToString("yyyy-MM-dd"), monospace: true));
            }
            else
            {
                facts.Add(new Fact("ISO 8601", dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), monospace: true));
                facts.Add(new Fact("Round-trip (o)", dt.ToString("o", CultureInfo.InvariantCulture), monospace: true));
                facts.Add(new Fact("Time of day", dt.TimeOfDay.ToString()));
            }

            facts.Add(new Fact("Day", dt.DayOfWeek + ", day " + dt.DayOfYear + " of " + dt.Year));
            facts.Add(new Fact("ISO week", IsoWeekOfYear(dt).ToString()));
            facts.Add(new Fact("Ticks", dt.Ticks.ToString("N0")));
            facts.Add(new Fact("DateTimeKind", dt.Kind.ToString()));

            var now = DateTime.Now;
            facts.Add(new Fact("Relative to now", DescribeInterval(now - dt)));
        }

        private static void AddDateTimeOffsetFacts(List<Fact> facts, object value, string text)
        {
            if (!(value is DateTimeOffset dto))
            {
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                {
                    facts.Add(new Fact("Value", text ?? "(unavailable)"));
                    return;
                }
            }

            facts.Add(new Fact("Round-trip (o)", dto.ToString("o", CultureInfo.InvariantCulture), monospace: true));
            facts.Add(new Fact("UTC", dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z", monospace: true));
            facts.Add(new Fact("Offset", dto.Offset.ToString()));
            facts.Add(new Fact("In this machine's local time", dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz")));
            facts.Add(new Fact("Unix seconds", dto.ToUnixTimeSeconds().ToString("N0")));
            facts.Add(new Fact("UTC ticks", dto.UtcTicks.ToString("N0")));
            facts.Add(new Fact("Relative to now", DescribeInterval(DateTimeOffset.Now - dto)));
        }

        private static void AddTimeFacts(List<Fact> facts, object value, string text)
        {
            if (!(value is TimeSpan ts))
            {
                if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out ts))
                {
                    facts.Add(new Fact("Value", text ?? "(unavailable)"));
                    return;
                }
            }

            facts.Add(new Fact("Value", ts.ToString("c"), monospace: true));
            facts.Add(new Fact("ISO 8601 duration", $"PT{ts.Hours}H{ts.Minutes}M{ts.Seconds + ts.Milliseconds / 1000.0:0.#######}S"));
            facts.Add(new Fact("Total seconds", ts.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture)));
            facts.Add(new Fact("Ticks", ts.Ticks.ToString("N0")));
        }

        /// <summary>
        /// ISO-8601 week number. Hand-rolled because System.Globalization.ISOWeek only
        /// exists from .NET Core 3.0 and this assembly targets .NET Framework 4.7.2.
        /// </summary>
        private static int IsoWeekOfYear(DateTime date)
        {
            var calendar = CultureInfo.InvariantCulture.Calendar;
            var day = calendar.GetDayOfWeek(date);

            // Thursday in the same ISO week decides which year the week belongs to.
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
            {
                date = date.AddDays(3);
            }

            return calendar.GetWeekOfYear(date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }

        private static string DescribeInterval(TimeSpan delta)
        {
            bool past = delta.Ticks >= 0;
            var d = delta.Duration();

            string magnitude;
            if (d.TotalSeconds < 60) magnitude = $"{d.TotalSeconds:0.#} second(s)";
            else if (d.TotalMinutes < 60) magnitude = $"{d.TotalMinutes:0.#} minute(s)";
            else if (d.TotalHours < 48) magnitude = $"{d.TotalHours:0.#} hour(s)";
            else if (d.TotalDays < 365) magnitude = $"{d.TotalDays:0.#} day(s)";
            else magnitude = $"{d.TotalDays / 365.2425:0.##} year(s)";

            return magnitude + (past ? " ago" : " from now");
        }

        // ---- guid, spatial, hierarchyid, variant -----------------------------------

        private static void AddGuidFacts(List<Fact> facts, object value, string text)
        {
            if (!(value is Guid guid))
            {
                if (!Guid.TryParse(text, out guid))
                {
                    facts.Add(new Fact("Value", text ?? "(unavailable)"));
                    return;
                }
            }

            facts.Add(new Fact("Canonical (D)", guid.ToString("D").ToUpperInvariant(), monospace: true));
            facts.Add(new Fact("Compact (N)", guid.ToString("N").ToUpperInvariant(), monospace: true));
            facts.Add(new Fact("Braced (B)", guid.ToString("B").ToUpperInvariant(), monospace: true));
            facts.Add(new Fact("Bytes (little-endian first fields)", BitConverter.ToString(guid.ToByteArray()).Replace("-", " ").ToLowerInvariant(), monospace: true));

            byte[] b = guid.ToByteArray();
            int version = (b[7] >> 4) & 0x0F;
            int variant = (b[8] >> 6) & 0x03;
            facts.Add(new Fact("RFC 4122", $"version {version}, variant {variant}"));

            if (version == 4)
            {
                facts.Add(new Fact("Generation", "random (version 4) — NEWID() produces these"));
            }
            else if (version == 1)
            {
                facts.Add(new Fact("Generation", "time-based (version 1) — NEWSEQUENTIALID() is a variation on this"));
            }
        }

        private static void AddSpatialFacts(List<Fact> facts, object value, byte[] bytes, string text)
        {
            // SqlGeography / SqlGeometry live in Microsoft.SqlServer.Types, which SSMS
            // loads but this extension does not reference. Reach their members by name.
            if (value != null)
            {
                var type = value.GetType();
                facts.Add(new Fact("CLR type", type.FullName));

                string wkt = TryToString(value);
                if (wkt != null)
                {
                    facts.Add(new Fact("Well-known text", Shorten(wkt, 400), monospace: true));
                }

                AddReflectedProperty(facts, value, "STSrid", "SRID");
                AddReflectedMethodResult(facts, value, "STGeometryType", "Geometry type");
                AddReflectedMethodResult(facts, value, "STNumPoints", "Points");
                AddReflectedMethodResult(facts, value, "STNumGeometries", "Geometries");
                AddReflectedMethodResult(facts, value, "STIsValid", "Valid");
                AddReflectedMethodResult(facts, value, "STArea", "Area");
                AddReflectedMethodResult(facts, value, "STLength", "Length");
                AddReflectedProperty(facts, value, "Lat", "Latitude");
                AddReflectedProperty(facts, value, "Long", "Longitude");
            }
            else if (!string.IsNullOrEmpty(text))
            {
                facts.Add(new Fact("Value", Shorten(text, 400), monospace: true));
            }

            if (bytes != null && bytes.Length >= 4)
            {
                facts.Add(new Fact("SRID (from UDT bytes)", BitConverter.ToInt32(bytes, 0).ToString()));
                facts.Add(new Fact("UDT length", bytes.Length.ToString("N0") + " bytes"));
            }
        }

        private static void AddHierarchyIdFacts(List<Fact> facts, object value, byte[] bytes, string text)
        {
            string path = value != null ? TryToString(value) : text;
            if (!string.IsNullOrEmpty(path))
            {
                facts.Add(new Fact("Path", path, monospace: true));
                int depth = path.Count(c => c == '/') - 1;
                if (depth >= 0)
                {
                    facts.Add(new Fact("Depth", depth.ToString()));
                }
            }

            AddReflectedMethodResult(facts, value, "GetLevel", "Level");

            if (bytes != null)
            {
                facts.Add(new Fact("Encoded length", bytes.Length + " bytes"));
                facts.Add(new Fact("T-SQL literal", HexDump.ToSqlLiteral(bytes), monospace: true));
            }
        }

        private static void AddVariantFacts(List<Fact> facts, object value, string text)
        {
            if (value == null)
            {
                facts.Add(new Fact("Value", text ?? "(unavailable)"));
                return;
            }

            var type = value.GetType();
            facts.Add(new Fact("Underlying CLR type", type.FullName));
            facts.Add(new Fact("Value", Shorten(Convert.ToString(value, CultureInfo.InvariantCulture), 400)));

            var inner = SqlTypeCatalog.Classify(null, type);
            if (inner != SqlTypeCategory.Variant && inner != SqlTypeCategory.Unknown)
            {
                facts.Add(new Fact("Behaves as", SqlTypeCatalog.Describe(inner)));
            }
        }

        // ---- schema -----------------------------------------------------------------

        private static void AddSchemaFacts(List<Fact> facts, CellSnapshot cell)
        {
            facts.Add(new Fact("Column", $"{cell.ColumnName} (index {cell.StorageColumn})"));
            facts.Add(new Fact("Declared type", cell.TypeLabel));

            if (cell.FieldType != null)
            {
                facts.Add(new Fact("Provider CLR type", cell.FieldType.FullName));
            }

            facts.Add(new Fact("Position", cell.TotalRows >= 0
                ? $"row {cell.Row + 1:N0} of {cell.TotalRows:N0}"
                : $"row {cell.Row + 1:N0}"));

            var schema = cell.SchemaRow;
            if (schema == null)
            {
                return;
            }

            AddSchemaValue(facts, schema, "ColumnSize", "Column size");
            AddSchemaValue(facts, schema, "NumericPrecision", "Precision");
            AddSchemaValue(facts, schema, "NumericScale", "Scale");
            AddSchemaValue(facts, schema, "AllowDBNull", "Nullable");
            AddSchemaValue(facts, schema, "IsKey", "Key column");
            AddSchemaValue(facts, schema, "IsIdentity", "Identity");
            AddSchemaValue(facts, schema, "IsReadOnly", "Read only");
            AddSchemaValue(facts, schema, "IsUnique", "Unique");
            AddSchemaValue(facts, schema, "BaseTableName", "Base table");
            AddSchemaValue(facts, schema, "BaseColumnName", "Base column");
            AddSchemaValue(facts, schema, "BaseSchemaName", "Base schema");
            AddSchemaValue(facts, schema, "DataTypeName", "Provider type name");
            AddSchemaValue(facts, schema, "UdtAssemblyQualifiedName", "UDT assembly");
            AddSchemaValue(facts, schema, "XmlSchemaCollectionName", "XML schema collection");
        }

        private static void AddSchemaValue(List<Fact> facts, DataRow row, string column, string label)
        {
            try
            {
                if (!row.Table.Columns.Contains(column))
                {
                    return;
                }

                object value = row[column];
                if (value == null || value is DBNull)
                {
                    return;
                }

                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                facts.Add(new Fact(label, text));
            }
            catch (Exception)
            {
                // A provider that does not supply this column is not an error.
            }
        }

        // ---- reflection helpers -----------------------------------------------------

        private static string TryToString(object value)
        {
            try
            {
                return value?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddReflectedProperty(List<Fact> facts, object target, string propertyName, string label)
        {
            if (target == null)
            {
                return;
            }

            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                var value = property?.GetValue(target);
                string text = Stringify(value);
                if (text != null)
                {
                    facts.Add(new Fact(label, text));
                }
            }
            catch (Exception)
            {
                // optional detail
            }
        }

        private static void AddReflectedMethodResult(List<Fact> facts, object target, string methodName, string label)
        {
            if (target == null)
            {
                return;
            }

            try
            {
                var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var value = method?.Invoke(target, null);
                string text = Stringify(value);
                if (text != null)
                {
                    facts.Add(new Fact(label, text));
                }
            }
            catch (Exception)
            {
                // optional detail
            }
        }

        /// <summary>
        /// Renders a value that may be a SqlInt32/SqlDouble/SqlBoolean wrapper. Those expose
        /// IsNull and Value; a null one contributes nothing.
        /// </summary>
        private static string Stringify(object value)
        {
            if (value == null)
            {
                return null;
            }

            try
            {
                var isNull = value.GetType().GetProperty("IsNull", BindingFlags.Public | BindingFlags.Instance);
                if (isNull != null && isNull.GetValue(value) is bool b && b)
                {
                    return null;
                }
            }
            catch (Exception)
            {
                // not a Sql* wrapper
            }

            string text = TryToString(value);
            return string.IsNullOrWhiteSpace(text) || text == "Null" ? null : text;
        }

        private static string Shorten(string text, int max)
        {
            if (text == null)
            {
                return null;
            }

            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + " ... (+" + (text.Length - max).ToString("N0") + " chars)";
        }
    }
}
