using System;
using System.Collections.Generic;
using System.Linq;
using SqlCellInspector.Format;

namespace SqlCellInspector.Tests
{
    /// <summary>
    /// Turning selected editor text, and selected grid cells, into a SQL list. The text
    /// path has to cope with whatever people actually paste, so that is where most of
    /// these live.
    /// </summary>
    internal static partial class FormatterTests
    {
        private static void ValueListTests()
        {
            Section("Value lists from editor text");

            Equal("one value per line",
                ValueListBuilder.Build("alpha\nbeta\ngamma"), "('alpha', 'beta', 'gamma')");
            Equal("carriage returns too",
                ValueListBuilder.Build("alpha\r\nbeta"), "('alpha', 'beta')");
            Equal("comma separated",
                ValueListBuilder.Build("alpha, beta, gamma"), "('alpha', 'beta', 'gamma')");
            Equal("tab separated, as pasted from a spreadsheet",
                ValueListBuilder.Build("alpha\tbeta"), "('alpha', 'beta')");
            Equal("blank lines are dropped",
                ValueListBuilder.Build("alpha\n\n  \nbeta\n"), "('alpha', 'beta')");
            Equal("surrounding whitespace is trimmed",
                ValueListBuilder.Build("  alpha  \n\tbeta "), "('alpha', 'beta')");

            // All numeric: leave unquoted, so an int column is not forced into a conversion.
            Equal("numbers are not quoted", ValueListBuilder.Build("1\n2\n3"), "(1, 2, 3)");
            Equal("negatives and decimals count as numbers",
                ValueListBuilder.Build("-1\n2.5\n0"), "(-1, 2.5, 0)");
            Equal("one non-number quotes them all",
                ValueListBuilder.Build("1\n2\nx"), "('1', '2', 'x')");
            Equal("values that merely start with digits are quoted",
                ValueListBuilder.Build("1a\n2b"), "('1a', '2b')");

            // Punctuation people leave behind.
            Equal("already quoted", ValueListBuilder.Build("'alpha'\n'beta'"), "('alpha', 'beta')");
            Equal("already an N-prefixed literal", ValueListBuilder.Build("N'alpha'\nN'beta'"), "('alpha', 'beta')");
            Equal("double quoted", ValueListBuilder.Build("\"alpha\"\n\"beta\""), "('alpha', 'beta')");
            Equal("bracketed", ValueListBuilder.Build("[alpha]\n[beta]"), "('alpha', 'beta')");
            Equal("trailing commas", ValueListBuilder.Build("alpha,\nbeta,"), "('alpha', 'beta')");
            Equal("trailing semicolon", ValueListBuilder.Build("alpha;\nbeta;"), "('alpha', 'beta')");
            Equal("a pasted list is re-listed",
                ValueListBuilder.Build("('alpha', 'beta')"), "('alpha', 'beta')");

            // Escaping is the thing that must not go wrong.
            Equal("an apostrophe is doubled",
                ValueListBuilder.Build("O'Brien"), "('O''Brien')");
            Equal("an escaped apostrophe survives a round trip",
                ValueListBuilder.Build("'O''Brien'"), "('O''Brien')");
            Equal("a quote-and-comment injection stays one literal",
                ValueListBuilder.Build("x'); DROP TABLE Orders; --"), "('x''); DROP TABLE Orders; --')");

            // Options.
            Equal("unicode prefix on request",
                ValueListBuilder.Build("alpha", new ValueListBuilder.Options { Unicode = true }), "(N'alpha')");
            Equal("without parentheses",
                ValueListBuilder.Build("a\nb", new ValueListBuilder.Options { Parenthesise = false }), "'a', 'b'");
            Check("long lists wrap",
                ValueListBuilder.Build(string.Join("\n", Enumerable.Range(1, 25).Select(i => "v" + i))).Contains("\n    "));

            Equal("empty input", ValueListBuilder.Build(""), string.Empty);
            Equal("whitespace only", ValueListBuilder.Build("   \n  "), string.Empty);
            Equal("null input", ValueListBuilder.Build(null), string.Empty);
            Equal("counts values", ValueListBuilder.Count("a\nb\nc"), 3);

            // ---- the same thing from selected grid cells -----------------------------

            Section("Value lists from grid cells");

            var text = new List<ScriptColumn> { Col("Code", "nvarchar", SqlTypeCategory.UnicodeText) };
            var selection = Sel(text,
                new object[] { "A-1" },
                new object[] { "O'Brien" },
                new object[] { null });

            string list = ScriptGenerator.Generate(selection, ScriptKind.ValueList);
            Check("uses the column's real type", list.Contains("N'A-1'"));
            Check("escapes correctly", list.Contains("N'O''Brien'"));
            Check("drops NULL with a reason", list.Contains("1 NULL(s) left out"));
            Check("has no column name or IN", !list.Contains("IN (") && !list.Contains("[Code]"));

            var ids = Sel(new List<ScriptColumn> { Col("Id", "int", SqlTypeCategory.Integer) },
                new object[] { 1 },
                new object[] { 2 },
                new object[] { 2 });

            string idList = ScriptGenerator.Generate(ids, ScriptKind.ValueList);
            Check("an int column stays unquoted", idList.Contains("(1, 2)"));
            Check("duplicates are collapsed", !idList.Contains("2, 2"));

            var twoColumns = Sel(new List<ScriptColumn>
            {
                Col("A", "int", SqlTypeCategory.Integer),
                Col("B", "int", SqlTypeCategory.Integer)
            }, new object[] { 1, 2 });
            Check("more than one column is reported",
                ScriptGenerator.Generate(twoColumns, ScriptKind.ValueList).Contains("takes one column"));
        }
    }
}
