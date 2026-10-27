using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using SqlCellInspector.Format;
using SqlCellInspector.Grid;

namespace SqlCellInspector.Tests
{
    /// <summary>
    /// Exercises the formatting and description code outside SSMS. These are the parts with
    /// real logic in them; the grid plumbing can only be tested against a running SSMS.
    /// Compiled by test\run-tests.ps1 straight from the same sources as the extension.
    /// </summary>
    internal static partial class FormatterTests
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            JsonTests();
            XmlTests();
            HexTests();
            TypeCatalogTests();
            DescriberTests();
            RendererTests();
            SqlLiteralTests();
            ScriptGeneratorTests();
            TableFinderTests();
            ValueListTests();
            DiagnosticsTests();

            Console.WriteLine();
            Console.WriteLine($"passed {_passed}, failed {_failed}");
            return _failed == 0 ? 0 : 1;
        }

        // ---- JSON --------------------------------------------------------------------

        private static void JsonTests()
        {
            Section("JSON");

            Check("detects an object", JsonPrettyPrinter.LooksLikeJson("  {\"a\":1}  "));
            Check("detects an array", JsonPrettyPrinter.LooksLikeJson("[1,2]"));
            Check("rejects a bare word", !JsonPrettyPrinter.LooksLikeJson("hello"));
            Check("rejects an unclosed object", !JsonPrettyPrinter.LooksLikeJson("{\"a\":1"));
            Check("rejects empty", !JsonPrettyPrinter.LooksLikeJson(""));

            var r = JsonPrettyPrinter.Format("{\"name\":\"Ada\",\"age\":36,\"ok\":true,\"tags\":[1,2,3],\"meta\":null}");
            Equal("pretty-prints nested content", string.Join("|", r.Text.Split('\n')),
                "{|  \"name\": \"Ada\",|  \"age\": 36,|  \"ok\": true,|  \"tags\": [1, 2, 3],|  \"meta\": null|}");

            Check("names keys as PropertyName", r.Tokens.Any(t => t.Kind == TokenKind.PropertyName && t.Text == "\"name\""));
            Check("marks strings", r.Tokens.Any(t => t.Kind == TokenKind.String && t.Text == "\"Ada\""));
            Check("marks numbers", r.Tokens.Any(t => t.Kind == TokenKind.Number && t.Text == "36"));
            Check("marks keywords", r.Tokens.Any(t => t.Kind == TokenKind.Keyword && t.Text == "true"));
            Check("marks null", r.Tokens.Any(t => t.Kind == TokenKind.Null && t.Text == "null"));
            Check("no error tokens on valid input", !r.Tokens.Any(t => t.Kind == TokenKind.Error));

            var nested = JsonPrettyPrinter.Format("{\"a\":{\"b\":[{\"c\":1}]}}");
            Equal("indents nested structures", nested.Text,
                "{\n  \"a\": {\n    \"b\": [\n      {\n        \"c\": 1\n      }\n    ]\n  }\n}");

            var escapes = JsonPrettyPrinter.Format("{\"s\":\"he said \\\"hi\\\" \\\\ \\u00e9\"}");
            Check("keeps escapes intact", escapes.Text.Contains("\\\"hi\\\"") && escapes.Text.Contains("\\u00e9"));
            Check("no error on escapes", !escapes.Tokens.Any(t => t.Kind == TokenKind.Error));

            var broken = JsonPrettyPrinter.Format("{\"a\":1,}");
            Check("flags malformed JSON", broken.Tokens.Any(t => t.Kind == TokenKind.Error));
            Check("explains malformed JSON", broken.Note != null);

            var trailing = JsonPrettyPrinter.Format("{\"a\":1} oops");
            Check("flags trailing junk", trailing.Tokens.Any(t => t.Kind == TokenKind.Error));

            var longArray = JsonPrettyPrinter.Format("[\"" + new string('x', 80) + "\"]");
            Check("breaks out a long array", longArray.Text.Contains("\n"));

            var emptyObj = JsonPrettyPrinter.Format("{}");
            Equal("keeps an empty object inline", emptyObj.Text, "{}");

            var emptyArr = JsonPrettyPrinter.Format("[]");
            Equal("keeps an empty array inline", emptyArr.Text, "[]");

            var exponent = JsonPrettyPrinter.Format("{\"n\":-1.25e-7}");
            Check("parses exponent notation", !exponent.Tokens.Any(t => t.Kind == TokenKind.Error));

            JsonPrettyPrinter.Measure("{\"a\":1,\"b\":{\"c\":[1,2]}}", out int objects, out int arrays, out int properties, out int depth);
            Equal("counts objects", objects, 2);
            Equal("counts arrays", arrays, 1);
            Equal("counts properties", properties, 3);
            Equal("measures depth", depth, 3);

            JsonPrettyPrinter.Measure("{\"a:b\":\"c,d\"}", out _, out _, out int tricky, out _);
            Equal("ignores punctuation inside strings", tricky, 1);
        }

        // ---- XML ---------------------------------------------------------------------

        private static void XmlTests()
        {
            Section("XML");

            Check("detects a document", XmlPrettyPrinter.LooksLikeXml(" <a><b/></a> "));
            Check("rejects plain text", !XmlPrettyPrinter.LooksLikeXml("not xml"));

            var r = XmlPrettyPrinter.Format("<root id=\"7\"><child name=\"a\">text</child><empty/></root>");
            Equal("indents elements", r.Text,
                "<root id=\"7\">\n  <child name=\"a\">text</child>\n  <empty />\n</root>");

            Check("marks tag names", r.Tokens.Any(t => t.Kind == TokenKind.TagName && t.Text == "root"));
            Check("marks attribute names", r.Tokens.Any(t => t.Kind == TokenKind.AttributeName && t.Text == "id"));
            Check("marks attribute values", r.Tokens.Any(t => t.Kind == TokenKind.AttributeValue && t.Text == "\"7\""));

            var fragment = XmlPrettyPrinter.Format("<a>1</a><a>2</a>");
            Equal("handles FOR XML fragments", fragment.Text, "<a>1</a>\n<a>2</a>");

            var cdata = XmlPrettyPrinter.Format("<a><![CDATA[<raw & stuff>]]></a>");
            Check("preserves CDATA", cdata.Text.Contains("<![CDATA[<raw & stuff>]]>"));
            Check("marks CDATA", cdata.Tokens.Any(t => t.Kind == TokenKind.CData));

            var comment = XmlPrettyPrinter.Format("<a><!-- note --><b/></a>");
            Check("keeps comments", comment.Tokens.Any(t => t.Kind == TokenKind.Comment));

            var escaped = XmlPrettyPrinter.Format("<a>5 &lt; 6 &amp; 7</a>");
            Check("re-escapes text", escaped.Text.Contains("5 &lt; 6 &amp; 7"));

            var ns = XmlPrettyPrinter.Format("<a xmlns:p=\"urn:x\"><p:b/></a>");
            Check("keeps namespace declarations", ns.Text.Contains("xmlns:p=\"urn:x\""));

            var broken = XmlPrettyPrinter.Format("<a><b></a>");
            Check("falls back for malformed XML", broken.Tokens == null);
            Check("explains malformed XML", broken.Note != null && broken.Note.Contains("well-formed"));
            Equal("shows the raw value when malformed", broken.Text, "<a><b></a>");

            // A DTD must not be processed: an external entity in a cell value must never
            // cause a file read or a network fetch.
            var dtd = XmlPrettyPrinter.Format("<!DOCTYPE d [<!ENTITY e SYSTEM \"file:///C:/Windows/win.ini\">]><d>&e;</d>");
            Check("refuses to process a DTD", dtd.Tokens == null && dtd.Note != null);
            Check("leaks nothing from the entity", !dtd.Text.Contains("[fonts]"));

            XmlPrettyPrinter.Measure("<a x=\"1\"><b/><c><d/></c></a>", out int elements, out int attributes, out int depth);
            Equal("counts elements", elements, 4);
            Equal("counts attributes", attributes, 1);
            Equal("measures depth", depth, 3);
        }

        // ---- hex ---------------------------------------------------------------------

        private static void HexTests()
        {
            Section("Hex");

            var bytes = Encoding.ASCII.GetBytes("Hello, world!");
            var dump = HexDump.Format(bytes);
            // 13 bytes: eight, the mid-row gap, five more, then three blank byte columns.
            Equal("lays out one row", dump.Text,
                "00000000  48 65 6c 6c 6f 2c 20 77  6f 72 6c 64 21           |Hello, world!   |");

            var two = HexDump.Format(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
            Equal("starts a second row at 0x10", two.Text.Split('\n')[1].Substring(0, 8), "00000010");

            Equal("renders a T-SQL literal", HexDump.ToSqlLiteral(new byte[] { 0x01, 0xAB, 0xFF }), "0x01ABFF");
            Equal("renders NULL", HexDump.ToSqlLiteral(null), "NULL");
            Check("caps the literal", HexDump.ToSqlLiteral(new byte[64], 8).Contains("+56 bytes"));

            var capped = HexDump.Format(new byte[64], 16);
            Check("caps the dump", capped.Truncated);
            Check("says how much is hidden", capped.Text.Contains("48 more bytes"));

            Equal("sniffs PNG", HexDump.DetectContentType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "image/png");
            Equal("sniffs JPEG", HexDump.DetectContentType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }), "image/jpeg");
            Equal("sniffs GIF", HexDump.DetectContentType(Encoding.ASCII.GetBytes("GIF89a")), "image/gif");
            Equal("sniffs PDF", HexDump.DetectContentType(Encoding.ASCII.GetBytes("%PDF-1.7")), "application/pdf");
            Equal("sniffs gzip", HexDump.DetectContentType(new byte[] { 0x1F, 0x8B, 0x08, 0x00 }), "application/gzip");
            Check("sniffs nothing from noise", HexDump.DetectContentType(new byte[] { 1, 2, 3, 4 }) == null);
            Check("recognises image types", HexDump.IsImage("image/png") && !HexDump.IsImage("application/pdf"));
        }

        // ---- type catalog ------------------------------------------------------------

        private static void TypeCatalogTests()
        {
            Section("Type catalog");

            Equal("nvarchar", SqlTypeCatalog.Classify("nvarchar", null), SqlTypeCategory.UnicodeText);
            Equal("varchar", SqlTypeCatalog.Classify("varchar", null), SqlTypeCategory.AnsiText);
            Equal("varbinary(max)", SqlTypeCatalog.Classify("varbinary(max)", null), SqlTypeCategory.Binary);
            Equal("timestamp", SqlTypeCatalog.Classify("timestamp", null), SqlTypeCategory.RowVersion);
            Equal("bigint", SqlTypeCatalog.Classify("bigint", null), SqlTypeCategory.Integer);
            Equal("decimal(18,4)", SqlTypeCatalog.Classify("decimal(18,4)", null), SqlTypeCategory.Decimal);
            Equal("money", SqlTypeCatalog.Classify("money", null), SqlTypeCategory.Money);
            Equal("float", SqlTypeCatalog.Classify("float", null), SqlTypeCategory.Float);
            Equal("bit", SqlTypeCatalog.Classify("bit", null), SqlTypeCategory.Bit);
            Equal("date", SqlTypeCatalog.Classify("date", null), SqlTypeCategory.Date);
            Equal("time", SqlTypeCatalog.Classify("time", null), SqlTypeCategory.Time);
            Equal("datetime2", SqlTypeCatalog.Classify("datetime2", null), SqlTypeCategory.DateTime);
            Equal("datetimeoffset", SqlTypeCatalog.Classify("datetimeoffset", null), SqlTypeCategory.DateTimeOffset);
            Equal("uniqueidentifier", SqlTypeCatalog.Classify("uniqueidentifier", null), SqlTypeCategory.Guid);
            Equal("xml", SqlTypeCatalog.Classify("xml", null), SqlTypeCategory.Xml);
            Equal("json", SqlTypeCatalog.Classify("json", null), SqlTypeCategory.Json);
            Equal("vector", SqlTypeCatalog.Classify("vector", null), SqlTypeCategory.Vector);
            Equal("sql_variant", SqlTypeCatalog.Classify("sql_variant", null), SqlTypeCategory.Variant);
            Equal("sysname", SqlTypeCatalog.Classify("sysname", null), SqlTypeCategory.UnicodeText);
            Equal("hierarchyid", SqlTypeCatalog.Classify("hierarchyid", null), SqlTypeCategory.HierarchyId);
            Equal("geography", SqlTypeCatalog.Classify("geography", null), SqlTypeCategory.Spatial);
            Equal("sys.geometry", SqlTypeCatalog.Classify("sys.geometry", null), SqlTypeCategory.Spatial);
            Equal("NVARCHAR is case-insensitive", SqlTypeCatalog.Classify("NVARCHAR", null), SqlTypeCategory.UnicodeText);
            Equal("[int] strips brackets", SqlTypeCatalog.Classify("[int]", null), SqlTypeCategory.Integer);

            Equal("falls back to the CLR type", SqlTypeCatalog.Classify(null, typeof(byte[])), SqlTypeCategory.Binary);
            Equal("falls back for Guid", SqlTypeCatalog.Classify(null, typeof(Guid)), SqlTypeCategory.Guid);
            Equal("falls back for DateTimeOffset", SqlTypeCatalog.Classify(null, typeof(DateTimeOffset)), SqlTypeCategory.DateTimeOffset);
            Equal("unknown with nothing to go on", SqlTypeCatalog.Classify(null, null), SqlTypeCategory.Unknown);

            Equal("suggests .json", SqlTypeCatalog.SuggestExtension(SqlTypeCategory.Json, null), ".json");
            Equal("suggests .xml", SqlTypeCatalog.SuggestExtension(SqlTypeCategory.Xml, null), ".xml");
            Equal("suggests .bin", SqlTypeCatalog.SuggestExtension(SqlTypeCategory.Binary, null), ".bin");
            Equal("sniff beats category", SqlTypeCatalog.SuggestExtension(SqlTypeCategory.Binary, "image/png"), ".png");
        }

        // ---- describer ---------------------------------------------------------------

        private static void DescriberTests()
        {
            Section("Value describer");

            var guid = Guid.Parse("8f14e45f-ceea-467a-9c2b-1f2d3e4a5b6c");
            var facts = Describe("uniqueidentifier", guid);
            Check("gives the canonical GUID", Has(facts, "Canonical (D)", "8F14E45F-CEEA-467A-9C2B-1F2D3E4A5B6C"));
            Check("gives the compact GUID", Has(facts, "Compact (N)", "8F14E45FCEEA467A9C2B1F2D3E4A5B6C"));
            Check("reads the RFC 4122 version", Value(facts, "RFC 4122").StartsWith("version 4"));

            var dto = new DateTimeOffset(2026, 10, 25, 14, 30, 15, TimeSpan.FromHours(3));
            facts = Describe("datetimeoffset", dto);
            Check("round-trips the offset", Value(facts, "Round-trip (o)").StartsWith("2026-10-25T14:30:15"));
            Check("converts to UTC", Value(facts, "UTC").StartsWith("2026-10-25T11:30:15"));
            Equal("states the offset", Value(facts, "Offset"), "03:00:00");

            facts = Describe("datetime2", new DateTime(2026, 2, 29 - 1, 1, 2, 3));
            Check("names the weekday", Value(facts, "Day").Length > 0);
            Check("reports ticks", Value(facts, "Ticks").Length > 0);

            facts = Describe("int", 1761400000);
            Equal("exact integer", Value(facts, "Value"), "1761400000");
            Equal("hex integer", Value(facts, "Hex"), "0x68FCD4C0");
            Check("spots a Unix timestamp", Value(facts, "As Unix seconds").StartsWith("2025-10-25"));

            facts = Describe("decimal", 12345.6700m);
            Equal("keeps trailing zeros", Value(facts, "Value"), "12345.6700");
            Equal("reports the stored scale", Value(facts, "Decimal places held"), "4");

            facts = Describe("float", 0.1 + 0.2);
            Equal("round-trips a double", Value(facts, "Round-trip value"), "0.30000000000000004");
            Check("shows the IEEE bits", Value(facts, "IEEE 754 bits").StartsWith("0x"));

            facts = Describe("bit", true);
            Equal("renders a bit", Value(facts, "Value"), "1 (true)");

            facts = Describe("varbinary", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            Check("reports binary length", Value(facts, "Length").StartsWith("8 bytes"));
            Equal("names the sniffed type", Value(facts, "Detected content"), "image/png");
            Check("hashes the bytes", Value(facts, "SHA-256").Length == 64);

            facts = Describe("timestamp", new byte[] { 0, 0, 0, 0, 0, 0, 0x27, 0x10 });
            Equal("decodes rowversion big-endian", Value(facts, "As unsigned integer"), "10,000");

            facts = Describe("nvarchar", "line one\r\nline two\t end ");
            Equal("counts characters", Value(facts, "Characters"), "24");
            Equal("counts lines", Value(facts, "Lines"), "2");
            Check("notes CR, LF and tab", Value(facts, "Whitespace / control").Contains("CR x1"));
            Check("notes trailing whitespace", Value(facts, "Padding").Contains("trailing"));

            facts = Describe("nvarchar", "{\"a\":1}");
            Equal("sniffs embedded JSON", Value(facts, "Content sniff"), "parses as JSON");
            Check("measures embedded JSON", Value(facts, "JSON shape").Contains("1 object(s)"));

            facts = Describe("nvarchar", "<a><b/></a>");
            Equal("sniffs embedded XML", Value(facts, "Content sniff"), "looks like XML");

            facts = Describe("nvarchar", Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.7 and some padding here")));
            Equal("sniffs Base64", Value(facts, "Content sniff"), "looks like Base64");
            Equal("sniffs what Base64 decodes to", Value(facts, "Decoded content"), "application/pdf");

            facts = Describe("nvarchar", "caf\u00e9 \ud83d\ude00");
            Check("counts non-ASCII", Value(facts, "Non-ASCII characters").StartsWith("3"));
            Check("notes astral code points", Value(facts, "Non-ASCII characters").Contains("astral"));

            // NULL short-circuits to a single fact plus the schema block.
            var nullCell = new CellSnapshot { IsNull = true, ColumnName = "c", ServerTypeName = "int", Row = 0 };
            facts = ValueDescriber.Describe(nullCell, SqlTypeCategory.Integer, null, null);
            Equal("describes NULL", Value(facts, "Value"), "NULL");

            // Schema facts come from the provider's schema row.
            var table = new DataTable();
            table.Columns.Add("ColumnSize", typeof(int));
            table.Columns.Add("NumericPrecision", typeof(int));
            table.Columns.Add("NumericScale", typeof(int));
            table.Columns.Add("AllowDBNull", typeof(bool));
            table.Columns.Add("BaseTableName", typeof(string));
            var schemaRow = table.NewRow();
            schemaRow["ColumnSize"] = 4000;
            schemaRow["NumericPrecision"] = 18;
            schemaRow["NumericScale"] = 4;
            schemaRow["AllowDBNull"] = true;
            schemaRow["BaseTableName"] = "Orders";
            table.Rows.Add(schemaRow);

            var cell = new CellSnapshot
            {
                ColumnName = "Total",
                ServerTypeName = "decimal",
                FormattedTypeName = "decimal(18,4)",
                Value = 1.5m,
                Row = 41,
                TotalRows = 100,
                StorageColumn = 2,
                SchemaRow = schemaRow
            };

            facts = ValueDescriber.Describe(cell, SqlTypeCategory.Decimal, null, null);
            Equal("surfaces column size", Value(facts, "Column size"), "4000");
            Equal("surfaces precision", Value(facts, "Precision"), "18");
            Equal("surfaces scale", Value(facts, "Scale"), "4");
            Equal("surfaces the base table", Value(facts, "Base table"), "Orders");
            Equal("reports the position", Value(facts, "Position"), "row 42 of 100");
            Equal("reports the declared type", Value(facts, "Declared type"), "decimal(18,4)");
        }

        // ---- renderer ----------------------------------------------------------------

        private static void RendererTests()
        {
            Section("Renderer");

            var json = CellRenderer.Render(Cell("json", "{\"a\":1}", "{\"a\":1}"), ViewMode.Auto);
            Equal("auto-picks JSON for a json column", json.EffectiveMode, ViewMode.Json);
            Equal("pretty-prints it", json.Formatted.Text, "{\n  \"a\": 1\n}");

            var sniffed = CellRenderer.Render(Cell("nvarchar", "[1,2]", "[1,2]"), ViewMode.Auto);
            Equal("auto-picks JSON by sniffing", sniffed.EffectiveMode, ViewMode.Json);

            var xml = CellRenderer.Render(Cell("xml", "<a/>", "<a/>"), ViewMode.Auto);
            Equal("auto-picks XML", xml.EffectiveMode, ViewMode.Xml);

            var text = CellRenderer.Render(Cell("nvarchar", "just text", "just text"), ViewMode.Auto);
            Equal("auto-picks text", text.EffectiveMode, ViewMode.Text);
            Equal("passes text through", text.Formatted.Text, "just text");

            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
            var image = CellRenderer.Render(Cell("varbinary", png, "0x89504E47..."), ViewMode.Auto);
            Equal("auto-picks the image view for a PNG", image.EffectiveMode, ViewMode.Image);
            Equal("sniffs the content type", image.ContentType, "image/png");
            Check("offers the hex view too", image.AvailableModes.Contains(ViewMode.Hex));
            Equal("suggests .png", image.SuggestedExtension, ".png");

            var binary = CellRenderer.Render(Cell("varbinary", new byte[] { 1, 2, 3 }, "0x010203"), ViewMode.Auto);
            Equal("auto-picks hex for plain binary", binary.EffectiveMode, ViewMode.Hex);
            Check("does not offer a text view for binary", !binary.AvailableModes.Contains(ViewMode.Json));

            var forcedHex = CellRenderer.Render(Cell("nvarchar", "AB", "AB"), ViewMode.Hex);
            Check("honours a forced hex view", forcedHex.Formatted.Text.StartsWith("00000000  41 42"));

            var unavailable = CellRenderer.Render(Cell("nvarchar", "x", "x"), ViewMode.Image);
            Equal("falls back when a mode does not apply", unavailable.EffectiveMode, ViewMode.Text);

            var nullCell = new CellSnapshot { IsNull = true, ColumnName = "c", ServerTypeName = "int", StorageColumn = 0 };
            var rendered = CellRenderer.Render(nullCell, ViewMode.Auto);
            Equal("renders NULL as NULL", rendered.Formatted.Text, "NULL");
            Check("distinguishes NULL from empty", rendered.Formatted.Note.Contains("not an empty string"));

            var gutter = new CellSnapshot { StorageColumn = -1, Row = 7, ColumnName = null };
            var gutterRendered = CellRenderer.Render(gutter, ViewMode.Auto);
            Check("explains the row-number gutter", gutterRendered.Formatted.Note.Contains("row-number gutter"));

            var big = new string('x', CellRenderer.ColourizeLimit + 1);
            var bigJson = CellRenderer.Render(Cell("json", "[\"" + big + "\"]", null), ViewMode.Json);
            Check("stops colouring very large values", bigJson.Formatted.Tokens == null);
            Check("says why it stopped", bigJson.Formatted.Note.Contains("plain text"));

            var base64 = CellRenderer.Render(Cell("varbinary", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, "0xDEADBEEF"), ViewMode.Base64);
            Equal("encodes Base64", base64.Formatted.Text, "3q2+7w==");
        }

        // ---- helpers -----------------------------------------------------------------

        private static CellSnapshot Cell(string serverType, object value, string text) => new CellSnapshot
        {
            ColumnName = "col",
            ServerTypeName = serverType,
            FormattedTypeName = serverType,
            Value = value,
            Text = text,
            Row = 0,
            StorageColumn = 0,
            ColumnCount = 1,
            TotalRows = 1
        };

        private static IReadOnlyList<Fact> Describe(string serverType, object value)
        {
            var cell = Cell(serverType, value, Convert.ToString(value));
            var category = SqlTypeCatalog.Classify(serverType, value?.GetType());
            byte[] bytes = value as byte[];
            string text = value as string;
            return ValueDescriber.Describe(cell, category, bytes, text);
        }

        private static string Value(IReadOnlyList<Fact> facts, string label) =>
            facts.FirstOrDefault(f => f.Label == label)?.Value ?? string.Empty;

        private static bool Has(IReadOnlyList<Fact> facts, string label, string value) =>
            Value(facts, label) == value;

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name + " ==");
        }

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

        private static void Equal<T>(string what, T actual, T expected)
        {
            if (Equals(actual, expected))
            {
                _passed++;
                Console.WriteLine("  ok    " + what);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + what);
                Console.WriteLine("        expected: " + Show(expected));
                Console.WriteLine("        actual:   " + Show(actual));
            }
        }

        private static string Show(object value) =>
            value == null ? "(null)" : Convert.ToString(value).Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
