using System;
using System.Collections.Generic;
using System.Linq;
using SqlCellInspector.Format;

namespace SqlCellInspector.Tests
{
    /// <summary>
    /// Covers the two pure modules behind the script features: turning a value into a
    /// T-SQL literal, and turning a grid selection into a statement. Neither needs a grid
    /// or a server, which is exactly why the risky logic lives in them.
    /// </summary>
    internal static partial class FormatterTests
    {
        private static void SqlLiteralTests()
        {
            Section("SQL literals");

            Equal("null", SqlLiteral.Format(null, "int", SqlTypeCategory.Integer), "NULL");
            Equal("DBNull", SqlLiteral.Format(DBNull.Value, "int", SqlTypeCategory.Integer), "NULL");

            Equal("varchar doubles the quote", SqlLiteral.Format("O'Brien", "varchar", SqlTypeCategory.AnsiText), "'O''Brien'");
            Equal("nvarchar gets the N prefix", SqlLiteral.Format("café", "nvarchar", SqlTypeCategory.UnicodeText), "N'café'");
            Equal("empty string is not NULL", SqlLiteral.Format("", "varchar", SqlTypeCategory.AnsiText), "''");
            Equal("newlines survive", SqlLiteral.Format("a\nb", "varchar", SqlTypeCategory.AnsiText), "'a\nb'");

            Equal("varbinary", SqlLiteral.Format(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, "varbinary", SqlTypeCategory.Binary), "0xDEADBEEF");
            Equal("empty varbinary", SqlLiteral.Format(new byte[0], "varbinary", SqlTypeCategory.Binary), "0x");
            Equal("rowversion", SqlLiteral.Format(new byte[] { 0, 0, 0, 0, 0, 0, 0x27, 0x10 }, "timestamp", SqlTypeCategory.RowVersion), "0x0000000000002710");

            Equal("int", SqlLiteral.Format(42, "int", SqlTypeCategory.Integer), "42");
            Equal("bigint at the boundary", SqlLiteral.Format(long.MinValue, "bigint", SqlTypeCategory.Integer), "-9223372036854775808");
            Equal("tinyint", SqlLiteral.Format((byte)255, "tinyint", SqlTypeCategory.Integer), "255");

            Equal("decimal keeps its scale", SqlLiteral.Format(12345.6700m, "decimal", SqlTypeCategory.Decimal), "12345.6700");
            Equal("money", SqlLiteral.Format(19.99m, "money", SqlTypeCategory.Money), "19.99");

            Equal("float round-trips", SqlLiteral.Format(0.1 + 0.2, "float", SqlTypeCategory.Float), "0.30000000000000004");
            Check("NaN is refused", SqlLiteral.Format(double.NaN, "float", SqlTypeCategory.Float).Contains("not representable"));
            Check("Infinity is refused", SqlLiteral.Format(double.PositiveInfinity, "float", SqlTypeCategory.Float).Contains("not representable"));

            Equal("bit true", SqlLiteral.Format(true, "bit", SqlTypeCategory.Bit), "1");
            Equal("bit false", SqlLiteral.Format(false, "bit", SqlTypeCategory.Bit), "0");

            var moment = new DateTime(2026, 10, 25, 14, 30, 15, 123);
            Equal("date", SqlLiteral.Format(moment, "date", SqlTypeCategory.Date), "'2026-10-25'");
            Equal("datetime keeps 3 digits", SqlLiteral.Format(moment, "datetime", SqlTypeCategory.DateTime), "'2026-10-25T14:30:15.123'");
            Equal("datetime2 keeps 7 digits", SqlLiteral.Format(moment, "datetime2", SqlTypeCategory.DateTime), "'2026-10-25T14:30:15.1230000'");
            Equal("smalldatetime drops seconds", SqlLiteral.Format(moment, "smalldatetime", SqlTypeCategory.DateTime), "'2026-10-25T14:30:00'");
            Equal("unknown date type gets full precision", SqlLiteral.Format(moment, null, SqlTypeCategory.DateTime), "'2026-10-25T14:30:15.1230000'");

            var dto = new DateTimeOffset(2026, 10, 25, 14, 30, 15, TimeSpan.FromHours(3));
            Equal("datetimeoffset", SqlLiteral.Format(dto, "datetimeoffset", SqlTypeCategory.DateTimeOffset), "'2026-10-25T14:30:15.0000000+03:00'");

            Equal("time", SqlLiteral.Format(new TimeSpan(0, 23, 59, 59), "time", SqlTypeCategory.Time), "'23:59:59.0000000'");

            Equal("guid", SqlLiteral.Format(Guid.Parse("8f14e45f-ceea-467a-9c2b-1f2d3e4a5b6c"), "uniqueidentifier", SqlTypeCategory.Guid),
                "'8F14E45F-CEEA-467A-9C2B-1F2D3E4A5B6C'");

            Equal("spatial uses its serialised bytes",
                SqlLiteral.Format(new byte[] { 0xE6, 0x10, 0x00, 0x00 }, "geography", SqlTypeCategory.Spatial), "0xE6100000");
            Equal("hierarchyid as text", SqlLiteral.Format("/1/2/", "hierarchyid", SqlTypeCategory.HierarchyId), "'/1/2/'");
            Equal("sql_variant re-dispatches on the CLR type", SqlLiteral.Format(7, "sql_variant", SqlTypeCategory.Variant), "7");

            Equal("identifier", SqlLiteral.QuoteIdentifier("Orders"), "[Orders]");
            Equal("identifier escapes a bracket", SqlLiteral.QuoteIdentifier("we]rd"), "[we]]rd]");
            Equal("qualified name", SqlLiteral.QualifiedName("dbo", "Orders"), "[dbo].[Orders]");
            Equal("qualified name without a schema", SqlLiteral.QualifiedName(null, "Orders"), "[Orders]");

            Equal("typed table name, bare", SqlLiteral.QualifiedNameFromUserInput("Orders"), "[Orders]");
            Equal("typed table name, dotted", SqlLiteral.QualifiedNameFromUserInput("dbo.Orders"), "[dbo].[Orders]");
            Equal("typed table name, already bracketed", SqlLiteral.QualifiedNameFromUserInput("[dbo].[Orders]"), "[dbo].[Orders]");
            Equal("a dot inside brackets is part of the name", SqlLiteral.QualifiedNameFromUserInput("[My.Schema].[T]"), "[My.Schema].[T]");
            Equal("blank table name", SqlLiteral.QualifiedNameFromUserInput("  "), "[<table>]");

            bool ok;
            Equal("typed int", SqlLiteral.FromUserText("42", SqlTypeCategory.Integer, "int", out ok), "42");
            Check("typed int parsed", ok);
            SqlLiteral.FromUserText("abc", SqlTypeCategory.Integer, "int", out ok);
            Check("a bad int is reported, not hidden", !ok);
            Equal("typed text is escaped, not executed",
                SqlLiteral.FromUserText("O'Brien", SqlTypeCategory.UnicodeText, "nvarchar", out ok), "N'O''Brien'");
            Equal("typed bit as a word", SqlLiteral.FromUserText("true", SqlTypeCategory.Bit, "bit", out ok), "1");
            Equal("typed guid", SqlLiteral.FromUserText("8f14e45f-ceea-467a-9c2b-1f2d3e4a5b6c", SqlTypeCategory.Guid, "uniqueidentifier", out ok),
                "'8F14E45F-CEEA-467A-9C2B-1F2D3E4A5B6C'");
            Equal("typed hex", SqlLiteral.FromUserText("0xDEAD", SqlTypeCategory.Binary, "varbinary", out ok), "0xDEAD");
            Equal("typed date", SqlLiteral.FromUserText("2026-10-25", SqlTypeCategory.Date, "date", out ok), "'2026-10-25'");

            // An injection attempt through the edit box must come back as data.
            string hostile = SqlLiteral.FromUserText("x'; DROP TABLE Orders; --", SqlTypeCategory.UnicodeText, "nvarchar", out ok);
            Equal("hostile text stays a single literal", hostile, "N'x''; DROP TABLE Orders; --'");
        }

        // ---- script generation ---------------------------------------------------------

        private static ScriptColumn Col(string name, string type, SqlTypeCategory category,
            bool key = false, bool identity = false, string table = "Orders", string schema = "dbo")
        {
            return new ScriptColumn
            {
                Name = name,
                BaseColumn = name,
                ServerTypeName = type,
                Category = category,
                IsKey = key,
                IsIdentity = identity,
                BaseSchema = schema,
                BaseTable = table
            };
        }

        private static ScriptSelection Sel(List<ScriptColumn> columns, params object[][] rows)
        {
            return new ScriptSelection
            {
                Columns = columns,
                SelectedColumns = Enumerable.Range(0, columns.Count).ToList(),
                Rows = rows.Select((r, i) => new ScriptRow { GridRow = i, Values = r }).ToList()
            };
        }

        private static void ScriptGeneratorTests()
        {
            Section("Script generation");

            var columns = new List<ScriptColumn>
            {
                Col("OrderId", "int", SqlTypeCategory.Integer, key: true, identity: true),
                Col("Customer", "nvarchar", SqlTypeCategory.UnicodeText),
                Col("Total", "decimal", SqlTypeCategory.Decimal)
            };

            var selection = Sel(columns,
                new object[] { 1, "Ada", 10.50m },
                new object[] { 2, null, 20m });

            string insert = ScriptGenerator.Generate(selection, ScriptKind.Insert);
            Check("insert names the table", insert.Contains("INSERT INTO [dbo].[Orders]"));
            Check("insert leaves out the identity column", !insert.Contains("[OrderId]"));
            Check("insert says what it left out", insert.Contains("WARNING") && insert.Contains("OrderId"));
            Check("insert lists the columns", insert.Contains("([Customer], [Total])"));
            Check("insert quotes text", insert.Contains("N'Ada'"));
            Check("insert writes NULL unquoted", insert.Contains("VALUES (NULL, 20)"));

            string update = ScriptGenerator.Generate(selection, ScriptKind.Update);
            Check("update names the table", update.Contains("UPDATE [dbo].[Orders]"));
            Check("update sets the non-key columns", update.Contains("[Customer] = N'Ada'"));
            Check("update never sets the key", !update.Contains("SET [OrderId]"));
            Check("update matches on the key", update.Contains("WHERE [OrderId] = 1"));

            string delete = ScriptGenerator.Generate(selection, ScriptKind.Delete);
            Check("delete names the table", delete.Contains("DELETE FROM [dbo].[Orders]"));
            Check("delete matches on the key", delete.Contains("WHERE [OrderId] = 2"));

            string select = ScriptGenerator.Generate(selection, ScriptKind.Select);
            Check("select round-trips the row",
                select.Contains("SELECT [OrderId], [Customer], [Total] FROM [dbo].[Orders] WHERE [OrderId] = 1"));

            // No key reported: the predicate falls back to every column, loudly.
            var keyless = Sel(new List<ScriptColumn>
            {
                Col("A", "int", SqlTypeCategory.Integer),
                Col("B", "nvarchar", SqlTypeCategory.UnicodeText)
            }, new object[] { 1, null });

            string keylessUpdate = ScriptGenerator.Generate(keyless, ScriptKind.Update);
            Check("a missing key is called out", keylessUpdate.Contains("NO KEY COLUMN"));
            Check("the fallback predicate uses every column", keylessUpdate.Contains("[A] = 1"));
            Check("NULL in a predicate becomes IS NULL", keylessUpdate.Contains("[B] IS NULL"));

            keyless.TableOverride = "sales.[My.Orders]";
            Check("a typed table name wins",
                ScriptGenerator.Generate(keyless, ScriptKind.Delete).Contains("DELETE FROM [sales].[My.Orders]"));

            keyless.KeyColumnOverride = new List<string> { "A" };
            string keyed = ScriptGenerator.Generate(keyless, ScriptKind.Delete);
            Check("a typed key wins", keyed.Contains("WHERE [A] = 1"));
            Check("a typed key silences the warning", !keyed.Contains("NO KEY COLUMN"));

            keyless.KeyColumnOverride = new List<string> { "Nope" };
            Check("an unknown key column is reported",
                ScriptGenerator.Generate(keyless, ScriptKind.Delete).Contains("not in the result set"));

            var expressions = Sel(new List<ScriptColumn>
            {
                Col("Computed", "int", SqlTypeCategory.Integer, table: null, schema: null)
            }, new object[] { 5 });
            string noTable = ScriptGenerator.Generate(expressions, ScriptKind.Update);
            Check("a missing base table is explained", noTable.Contains("does not return base-table metadata"));
            Check("a missing base table leaves a placeholder", noTable.Contains("[<table>]"));

            var mixed = Sel(new List<ScriptColumn>
            {
                Col("A", "int", SqlTypeCategory.Integer, key: true, table: "Orders"),
                Col("B", "int", SqlTypeCategory.Integer, table: "Customers")
            }, new object[] { 1, 2 });
            Check("columns from two tables are reported",
                ScriptGenerator.Generate(mixed, ScriptKind.Update).Contains("more than one base table"));

            var ids = new ScriptSelection
            {
                Columns = new List<ScriptColumn> { Col("OrderId", "int", SqlTypeCategory.Integer) },
                SelectedColumns = new List<int> { 0 },
                Rows = new List<ScriptRow>
                {
                    new ScriptRow { GridRow = 0, Values = new object[] { 1 } },
                    new ScriptRow { GridRow = 1, Values = new object[] { 2 } },
                    new ScriptRow { GridRow = 2, Values = new object[] { null } }
                }
            };
            string inList = ScriptGenerator.Generate(ids, ScriptKind.InList);
            Check("an IN list carries the values", inList.Contains("[OrderId] IN (") && inList.Contains("1, 2"));
            Check("an IN list drops NULL and says so", inList.Contains("NULLs were left out"));

            string single = ScriptGenerator.UpdateSingleValue(selection, columns[1], selection.Rows[0], "N'Grace'");
            Check("a single-cell update sets one column", single.Contains("SET [Customer] = N'Grace'"));
            Check("a single-cell update keys on that row", single.Contains("WHERE [OrderId] = 1"));

            string ontoIdentity = ScriptGenerator.UpdateSingleValue(selection, columns[0], selection.Rows[0], "99");
            Check("writing to an identity column is flagged", ontoIdentity.Contains("identity, computed, or read-only"));

            selection.RowsOmitted = 7;
            selection.ValuesIncomplete = true;
            string capped = ScriptGenerator.Generate(selection, ScriptKind.Insert);
            Check("omitted rows are reported", capped.Contains("7 more were omitted"));
            Check("unreadable values are reported", capped.Contains("could not be read"));

            Check("an empty selection says so",
                ScriptGenerator.Generate(new ScriptSelection(), ScriptKind.Insert).Contains("Nothing selected"));

            StagedEditTests();
        }

        /// <summary>
        /// The staged-edit path: retype several cells across several rows, then get one
        /// UPDATE script carrying all of them. Only the edited columns are SET; untouched
        /// columns and rows are left alone.
        /// </summary>
        private static void StagedEditTests()
        {
            Section("Staged edits");

            var columns = new List<ScriptColumn>
            {
                Col("OrderId", "int", SqlTypeCategory.Integer, key: true, identity: true),
                Col("Customer", "nvarchar", SqlTypeCategory.UnicodeText),
                Col("Total", "decimal", SqlTypeCategory.Decimal)
            };

            var selection = Sel(columns,
                new object[] { 1, "Ada", 10.50m },
                new object[] { 2, "Grace", 20m },
                new object[] { 3, "Katherine", 30m });

            // Two cells on row 1, one on row 2; row 3 untouched.
            selection.StagedEdits = new Dictionary<long, Dictionary<string, string>>
            {
                [0] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Customer"] = "N'O''Brien, Ada'",
                    ["Total"] = "99.9900"
                },
                [1] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Total"] = "NULL"
                }
            };

            string script = ScriptGenerator.Generate(selection, ScriptKind.Update);

            Check("the header counts cells and rows", script.Contains("3 staged edits across 2 rows"));
            Check("row 1 sets both edited columns",
                script.Contains("[Customer] = N'O''Brien, Ada'") && script.Contains("[Total] = 99.9900"));
            Check("row 1 is keyed", script.Contains("WHERE [OrderId] = 1"));
            Check("row 2 sets only its edited column", script.Contains("[Total] = NULL"));
            Check("row 2 is keyed", script.Contains("WHERE [OrderId] = 2"));
            Check("an untouched row produces nothing", !script.Contains("WHERE [OrderId] = 3"));
            Check("an untouched column is not SET", !script.Contains("[Customer] = N'Grace'"));
            Equal("one statement per edited row", script.Split(new[] { "UPDATE [dbo].[Orders]" }, StringSplitOptions.None).Length - 1, 2);

            // Editing a column the server owns must be called out.
            selection.StagedEdits = new Dictionary<long, Dictionary<string, string>>
            {
                [0] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OrderId"] = "42" }
            };
            Check("staging onto an identity column warns",
                ScriptGenerator.Generate(selection, ScriptKind.Update).Contains("identity, computed, or read-only"));

            // A staged row whose key values were never read cannot be scripted.
            selection.StagedEdits = new Dictionary<long, Dictionary<string, string>>
            {
                [99] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Customer"] = "N'X'" }
            };
            string orphan = ScriptGenerator.Generate(selection, ScriptKind.Update);
            Check("an unreadable staged row is reported", orphan.Contains("could not be read"));
            Check("and nothing is emitted for it", orphan.Contains("No staged edit could be turned into a statement"));

            // Staged edits must not leak into the other statement kinds.
            selection.StagedEdits = new Dictionary<long, Dictionary<string, string>>
            {
                [0] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Customer"] = "N'Edited'" }
            };
            Check("DELETE ignores staged edits",
                !ScriptGenerator.Generate(selection, ScriptKind.Delete).Contains("N'Edited'"));
            Check("INSERT ignores staged edits",
                !ScriptGenerator.Generate(selection, ScriptKind.Insert).Contains("N'Edited'"));
        }
    }
}
