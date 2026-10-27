using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SqlResultToolkit.Format
{
    internal enum ScriptKind
    {
        Insert,
        Update,
        Delete,
        Select,
        InList,
        ValueList
    }

    /// <summary>One column of a result set, with the schema facts scripting needs.</summary>
    internal sealed class ScriptColumn
    {
        public string Name { get; set; }

        public string ServerTypeName { get; set; }

        public SqlTypeCategory Category { get; set; }

        public bool IsKey { get; set; }

        public bool IsIdentity { get; set; }

        /// <summary>The provider's IsExpression flag: a computed column the server owns.</summary>
        public bool IsComputed { get; set; }

        public bool IsReadOnly { get; set; }

        public bool AllowDbNull { get; set; } = true;

        public string BaseSchema { get; set; }

        public string BaseTable { get; set; }

        public string BaseColumn { get; set; }

        /// <summary>
        /// True when the provider reported no base table for this column. On its own that
        /// means little: a plain SELECT reports no base table for ANY column, so this is
        /// only disqualifying when the user has not named a target table either.
        /// </summary>
        public bool HasNoBaseTable => string.IsNullOrEmpty(BaseTable);

        /// <summary>The name to use in the script: the underlying column, not an alias.</summary>
        public string TargetName => string.IsNullOrEmpty(BaseColumn) ? Name : BaseColumn;

        /// <summary>A column the server will not accept a value for.</summary>
        public bool IsGenerated => IsIdentity || IsComputed || IsReadOnly;
    }

    internal sealed class ScriptRow
    {
        public long GridRow { get; set; }

        /// <summary>Parallel to <see cref="ScriptSelection.Columns"/>.</summary>
        public object[] Values { get; set; }
    }

    internal sealed class ScriptSelection
    {
        /// <summary>Every column that was read: the selected ones plus any key column.</summary>
        public IReadOnlyList<ScriptColumn> Columns { get; set; } = new List<ScriptColumn>();

        /// <summary>Indexes into <see cref="Columns"/> that the user actually selected.</summary>
        public IReadOnlyList<int> SelectedColumns { get; set; } = new List<int>();

        public IReadOnlyList<ScriptRow> Rows { get; set; } = new List<ScriptRow>();

        /// <summary>Set when the selection was larger than the row cap.</summary>
        public int RowsOmitted { get; set; }

        public bool ValuesIncomplete { get; set; }

        /// <summary>
        /// Table to script against, as typed by the user ("dbo.Orders", "[dbo].[Orders]"
        /// or "Orders"). This matters more than it looks: a plain SELECT returns no base
        /// table in its metadata, so for most query windows this is the only way to name
        /// the target.
        /// </summary>
        public string TableOverride { get; set; }

        /// <summary>Key column names supplied by the user, for the same reason.</summary>
        public IReadOnlyList<string> KeyColumnOverride { get; set; }

        /// <summary>
        /// New values the user staged in the Value tab, as row -&gt; (column name -&gt;
        /// literal). When this has entries an UPDATE is built from them instead of from
        /// the cells' current values, which is the point: retype several cells, then get
        /// one script that carries all of the changes.
        /// </summary>
        public IReadOnlyDictionary<long, Dictionary<string, string>> StagedEdits { get; set; }

        public bool HasStagedEdits => StagedEdits != null && StagedEdits.Count > 0;

        /// <summary>True when the provider itself reported a base table.</summary>
        public bool HasProviderTable => Columns.Any(c => !c.HasNoBaseTable);

        /// <summary>True when the provider itself reported key columns.</summary>
        public bool HasProviderKeys => Columns.Any(c => c.IsKey && !c.HasNoBaseTable);
    }

    internal static class ScriptGenerator
    {
        public const int DefaultRowLimit = 1000;

        /// <summary>Everything the statement builders need, worked out once.</summary>
        private sealed class Context
        {
            public string Table;
            public List<ScriptColumn> Keys = new List<ScriptColumn>();

            /// <summary>True when Keys is "every column", not a real key.</summary>
            public bool KeysAreFallback;

            /// <summary>True when we know which table to write to, from metadata or the user.</summary>
            public bool HaveTarget;

            /// <summary>
            /// Every selected column goes into the script. A column with no base table is
            /// not excluded: when the target is unknown the statement is emitted against
            /// the [<table>] placeholder with a warning, which shows the user the shape and
            /// what to fix. Refusing to emit anything would just be a dead end.
            /// </summary>
            public bool Scriptable(ScriptColumn c) => true;

            /// <summary>Columns the server will accept a value for.</summary>
            public bool Writable(ScriptColumn c) => !c.IsGenerated;
        }

        public static string Generate(ScriptSelection selection, ScriptKind kind)
        {
            if (selection == null || selection.Rows.Count == 0 || selection.Columns.Count == 0)
            {
                return "-- Nothing selected. Select one or more cells in the results grid.";
            }

            var warnings = new List<string>();
            var context = Prepare(selection, warnings);
            var body = new StringBuilder();

            switch (kind)
            {
                case ScriptKind.Insert: Insert(selection, context, body, warnings); break;
                case ScriptKind.Update: Update(selection, context, body, warnings); break;
                case ScriptKind.Delete: Delete(selection, context, body); break;
                case ScriptKind.Select: Select(selection, context, body); break;
                case ScriptKind.InList: InList(selection, body, warnings); break;
                case ScriptKind.ValueList: ValueList(selection, body, warnings); break;
            }

            if (selection.RowsOmitted > 0)
            {
                warnings.Add($"Only the first {selection.Rows.Count:N0} selected rows are scripted; {selection.RowsOmitted:N0} more were omitted.");
            }

            if (selection.ValuesIncomplete)
            {
                warnings.Add("At least one value could not be read and was scripted as NULL.");
            }

            return Compose(kind, selection, warnings, body.ToString());
        }

        private static Context Prepare(ScriptSelection s, List<string> warnings)
        {
            var context = new Context();

            // ---- target table
            if (!string.IsNullOrWhiteSpace(s.TableOverride))
            {
                context.Table = SqlLiteral.QualifiedNameFromUserInput(s.TableOverride);
                context.HaveTarget = true;
            }
            else
            {
                var tables = s.Columns
                    .Where(c => !c.HasNoBaseTable)
                    .Select(c => new { c.BaseSchema, c.BaseTable })
                    .Distinct()
                    .ToList();

                if (tables.Count == 0)
                {
                    warnings.Add("No base table was reported. A plain SELECT does not return base-table metadata, so "
                                 + "this is normal; type the table name in the Table box, or replace the placeholder below.");
                    context.Table = "[<table>]";
                    context.HaveTarget = false;
                }
                else
                {
                    if (tables.Count > 1)
                    {
                        warnings.Add("The selection spans more than one base table (" +
                                     string.Join(", ", tables.Select(t => t.BaseTable)) +
                                     "); scripting against the first.");
                    }

                    context.Table = SqlLiteral.QualifiedName(tables[0].BaseSchema, tables[0].BaseTable);
                    context.HaveTarget = true;
                }
            }

            // ---- key columns
            if (s.KeyColumnOverride != null && s.KeyColumnOverride.Count > 0)
            {
                var missing = new List<string>();
                foreach (var name in s.KeyColumnOverride)
                {
                    var match = s.Columns.FirstOrDefault(c =>
                        string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.TargetName, name, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        context.Keys.Add(match);
                    }
                    else
                    {
                        missing.Add(name);
                    }
                }

                if (missing.Count > 0)
                {
                    warnings.Add("These key columns are not in the result set and were ignored: " +
                                 string.Join(", ", missing) + ".");
                }
            }

            if (context.Keys.Count == 0)
            {
                context.Keys = s.Columns.Where(c => c.IsKey && !c.HasNoBaseTable).ToList();
            }

            if (context.Keys.Count == 0)
            {
                // Without a key the only honest predicate is every column we have, and
                // even that can match more than one row. Say so loudly rather than quietly
                // producing a statement that could hit the wrong rows.
                warnings.Add("NO KEY COLUMN was reported, so the WHERE clause below matches on every column that was "
                             + "read. It is NOT guaranteed to affect exactly one row. Put the key column(s) in the Key "
                             + "box, or check the statement before running it.");

                context.Keys = s.Columns.Where(c => !c.IsComputed).ToList();
                context.KeysAreFallback = true;
            }

            return context;
        }

        // ---- statements ---------------------------------------------------------------

        private static void Insert(ScriptSelection s, Context ctx, StringBuilder sb, List<string> warnings)
        {
            var selected = s.SelectedColumns.Select(i => s.Columns[i]).Where(ctx.Scriptable).ToList();
            var skipped = selected.Where(c => c.IsGenerated).ToList();
            var columns = selected.Where(c => !c.IsGenerated).ToList();

            if (skipped.Count > 0)
            {
                warnings.Add("Left out of the column list because the server assigns them: " +
                             string.Join(", ", skipped.Select(c => c.TargetName)) + ".");
            }

            if (columns.Count == 0)
            {
                sb.AppendLine("-- No insertable columns in the selection.");
                return;
            }

            string columnList = string.Join(", ", columns.Select(c => SqlLiteral.QuoteIdentifier(c.TargetName)));

            foreach (var row in s.Rows)
            {
                var values = columns.Select(c => Literal(s, row, c));
                sb.Append("INSERT INTO ").Append(ctx.Table).Append(" (").Append(columnList).AppendLine(")");
                sb.Append("VALUES (").Append(string.Join(", ", values)).AppendLine(");");
            }
        }

        /// <summary>
        /// UPDATE built from staged edits: one statement per row that has them, setting
        /// only the columns that were retyped, keyed as usual. Rows and columns that were
        /// not edited are left alone entirely.
        /// </summary>
        private static void UpdateFromStagedEdits(ScriptSelection s, Context ctx, StringBuilder sb, List<string> warnings)
        {
            int statements = 0;
            var missingRows = new List<long>();

            foreach (var pair in s.StagedEdits.OrderBy(p => p.Key))
            {
                var row = s.Rows.FirstOrDefault(r => r.GridRow == pair.Key);
                if (row == null)
                {
                    missingRows.Add(pair.Key);
                    continue;
                }

                var sets = new List<string>();
                foreach (var edit in pair.Value)
                {
                    var column = s.Columns.FirstOrDefault(c =>
                        string.Equals(c.Name, edit.Key, StringComparison.OrdinalIgnoreCase));

                    string target = column != null ? column.TargetName : edit.Key;

                    if (column != null && column.IsGenerated)
                    {
                        warnings.Add($"[{target}] is identity, computed, or read-only; the server will reject a write to it.");
                    }

                    sets.Add(SqlLiteral.QuoteIdentifier(target) + " = " + edit.Value);
                }

                if (sets.Count == 0)
                {
                    continue;
                }

                sb.Append("UPDATE ").AppendLine(ctx.Table);
                sb.Append("   SET ").AppendLine(string.Join(",\n       ", sets));
                sb.Append(" WHERE ").AppendLine(Predicate(s, row, ctx.Keys) + ";");
                statements++;
            }

            if (missingRows.Count > 0)
            {
                warnings.Add("Edits were staged for row(s) " + string.Join(", ", missingRows.Select(r => (r + 1).ToString())) +
                             ", whose key values could not be read; those rows were skipped.");
            }

            if (statements == 0)
            {
                sb.AppendLine("-- No staged edit could be turned into a statement.");
            }
        }

        private static void Update(ScriptSelection s, Context ctx, StringBuilder sb, List<string> warnings)
        {
            if (s.HasStagedEdits)
            {
                UpdateFromStagedEdits(s, ctx, sb, warnings);
                return;
            }

            // A real key is excluded from SET (updating it would move the row out from
            // under its own predicate). The all-columns fallback is NOT excluded: there
            // the predicate is just "the row that currently looks like this", and setting
            // a column that also appears in the WHERE is exactly the intent.
            var sets = s.SelectedColumns
                .Select(i => s.Columns[i])
                .Where(ctx.Writable)
                .Where(c => ctx.KeysAreFallback || !ctx.Keys.Contains(c))
                .ToList();

            if (sets.Count == 0)
            {
                sb.AppendLine("-- Nothing to SET: every selected column is a key, computed, or read-only.");
                return;
            }

            foreach (var row in s.Rows)
            {
                sb.Append("UPDATE ").AppendLine(ctx.Table);
                sb.Append("   SET ").AppendLine(string.Join(",\n       ",
                    sets.Select(c => SqlLiteral.QuoteIdentifier(c.TargetName) + " = " + Literal(s, row, c))));
                sb.Append(" WHERE ").AppendLine(Predicate(s, row, ctx.Keys) + ";");
            }
        }

        private static void Delete(ScriptSelection s, Context ctx, StringBuilder sb)
        {
            foreach (var row in s.Rows)
            {
                sb.Append("DELETE FROM ").AppendLine(ctx.Table);
                sb.Append(" WHERE ").AppendLine(Predicate(s, row, ctx.Keys) + ";");
            }
        }

        private static void Select(ScriptSelection s, Context ctx, StringBuilder sb)
        {
            var columns = s.SelectedColumns.Select(i => s.Columns[i]).Where(ctx.Scriptable).ToList();
            string columnList = columns.Count == 0
                ? "*"
                : string.Join(", ", columns.Select(c => SqlLiteral.QuoteIdentifier(c.TargetName)));

            foreach (var row in s.Rows)
            {
                sb.Append("SELECT ").Append(columnList).Append(" FROM ").Append(ctx.Table)
                  .Append(" WHERE ").AppendLine(Predicate(s, row, ctx.Keys) + ";");
            }
        }

        /// <summary>
        /// The selected column's values as an IN list, which is what you actually want
        /// when you have picked a column of ids to paste into another query.
        /// </summary>
        private static void InList(ScriptSelection s, StringBuilder sb, List<string> warnings)
        {
            var columns = s.SelectedColumns.Select(i => s.Columns[i]).ToList();
            if (columns.Count > 1)
            {
                warnings.Add("An IN list needs exactly one column selected; using the first.");
            }

            if (columns.Count == 0)
            {
                sb.AppendLine("-- Select a single column.");
                return;
            }

            var column = columns[0];
            var literals = new List<string>();
            bool sawNull = false;

            foreach (var row in s.Rows)
            {
                string literal = Literal(s, row, column);
                if (literal == SqlLiteral.Null)
                {
                    sawNull = true;
                    continue;
                }

                literals.Add(literal);
            }

            if (sawNull)
            {
                warnings.Add("NULLs were left out: IN never matches NULL. Add \"OR " +
                             SqlLiteral.QuoteIdentifier(column.TargetName) + " IS NULL\" if you need them.");
            }

            sb.Append(SqlLiteral.QuoteIdentifier(column.TargetName)).AppendLine(" IN (");
            sb.Append("    ").AppendLine(string.Join(",\n    ", Chunk(literals, 10)));
            sb.AppendLine(")");
        }

        /// <summary>
        /// Just the values, as ('a', 'b', 'c') — no column name, no IN. This is the form
        /// you want when pasting into an existing IN (...), a VALUES clause, or another
        /// tool. Literals are formatted from the column's real type, so an int column
        /// stays unquoted.
        /// </summary>
        private static void ValueList(ScriptSelection s, StringBuilder sb, List<string> warnings)
        {
            var columns = s.SelectedColumns.Select(i => s.Columns[i]).ToList();
            if (columns.Count == 0)
            {
                sb.AppendLine("-- Select a column.");
                return;
            }

            if (columns.Count > 1)
            {
                warnings.Add("A value list takes one column; using the first (" + columns[0].Name + ").");
            }

            var column = columns[0];
            var literals = new List<string>();
            int nulls = 0;

            foreach (var row in s.Rows)
            {
                string literal = Literal(s, row, column);
                if (literal == SqlLiteral.Null)
                {
                    nulls++;
                    continue;
                }

                if (!literals.Contains(literal, StringComparer.Ordinal))
                {
                    literals.Add(literal);
                }
            }

            if (nulls > 0)
            {
                warnings.Add($"{nulls:N0} NULL(s) left out: NULL cannot appear in a value list used with IN.");
            }

            if (literals.Count == 0)
            {
                sb.AppendLine("-- Nothing to list.");
                return;
            }

            sb.Append('(').Append(string.Join(",\n    ", Chunk(literals, 10))).Append(')').AppendLine();
        }

        /// <summary>
        /// A single-cell UPDATE built from a value the user edited in the panel. The new
        /// value arrives already formatted as a literal, because the panel converts the
        /// typed text using the column's declared type.
        /// </summary>
        public static string UpdateSingleValue(
            ScriptSelection selection,
            ScriptColumn column,
            ScriptRow row,
            string newValueLiteral)
        {
            if (selection == null || column == null || row == null)
            {
                return "-- No cell selected.";
            }

            var warnings = new List<string>();
            var ctx = Prepare(selection, warnings);

            if (column.HasNoBaseTable && !ctx.HaveTarget)
            {
                warnings.Add("This column has no base table and no table was given, so the target below is a placeholder.");
            }

            if (column.IsGenerated)
            {
                warnings.Add("This column is identity, computed, or read-only; the server will reject writes to it.");
            }

            var sb = new StringBuilder();
            sb.Append("UPDATE ").AppendLine(ctx.Table);
            sb.Append("   SET ").Append(SqlLiteral.QuoteIdentifier(column.TargetName)).Append(" = ").AppendLine(newValueLiteral);
            sb.Append(" WHERE ").AppendLine(Predicate(selection, row, ctx.Keys) + ";");

            return Compose(ScriptKind.Update, selection, warnings, sb.ToString());
        }

        // ---- shared pieces --------------------------------------------------------------

        private static string Predicate(ScriptSelection s, ScriptRow row, List<ScriptColumn> keys)
        {
            if (keys.Count == 0)
            {
                return "1 = 0 /* no columns available to match on */";
            }

            var parts = new List<string>();
            foreach (var key in keys)
            {
                string literal = Literal(s, row, key);
                parts.Add(literal == SqlLiteral.Null
                    ? SqlLiteral.QuoteIdentifier(key.TargetName) + " IS NULL"
                    : SqlLiteral.QuoteIdentifier(key.TargetName) + " = " + literal);
            }

            return string.Join("\n   AND ", parts);
        }

        private static string Literal(ScriptSelection s, ScriptRow row, ScriptColumn column)
        {
            int index = IndexOf(s, column);
            if (index < 0 || row.Values == null || index >= row.Values.Length)
            {
                return SqlLiteral.Null;
            }

            return SqlLiteral.Format(row.Values[index], column.ServerTypeName, column.Category);
        }

        private static int IndexOf(ScriptSelection s, ScriptColumn column)
        {
            for (int i = 0; i < s.Columns.Count; i++)
            {
                if (ReferenceEquals(s.Columns[i], column))
                {
                    return i;
                }
            }

            return -1;
        }

        private static IEnumerable<string> Chunk(List<string> items, int perLine)
        {
            for (int i = 0; i < items.Count; i += perLine)
            {
                yield return string.Join(", ", items.Skip(i).Take(perLine));
            }
        }

        private static string Compose(ScriptKind kind, ScriptSelection s, List<string> warnings, string body)
        {
            var sb = new StringBuilder();

            if (kind == ScriptKind.Update && s.HasStagedEdits)
            {
                int cells = s.StagedEdits.Sum(p => p.Value.Count);
                sb.Append("-- UPDATE generated from ").Append(cells.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(cells == 1 ? " staged edit across " : " staged edits across ")
                  .Append(s.StagedEdits.Count.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(s.StagedEdits.Count == 1 ? " row" : " rows")
                  .AppendLine(". Review before running; nothing has been executed.");
            }
            else
            {
                sb.Append("-- ").Append(kind.ToString().ToUpperInvariant())
                  .Append(" generated from ").Append(s.Rows.Count.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(s.Rows.Count == 1 ? " selected row" : " selected rows")
                  .AppendLine(". Review before running; nothing has been executed.");
            }

            foreach (var warning in warnings)
            {
                sb.Append("-- WARNING: ").AppendLine(Wrap(warning));
            }

            sb.AppendLine();
            sb.Append(body);
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        /// <summary>Keeps a long warning readable by continuing it on further comment lines.</summary>
        private static string Wrap(string text, int width = 96)
        {
            if (text.Length <= width)
            {
                return text;
            }

            var sb = new StringBuilder();
            int lineLength = 0;
            foreach (var word in text.Split(' '))
            {
                if (lineLength > 0 && lineLength + word.Length + 1 > width)
                {
                    sb.AppendLine().Append("--          ");
                    lineLength = 0;
                }
                else if (lineLength > 0)
                {
                    sb.Append(' ');
                    lineLength++;
                }

                sb.Append(word);
                lineLength += word.Length;
            }

            return sb.ToString();
        }
    }
}
