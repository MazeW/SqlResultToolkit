using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SqlCellInspector.Format
{
    /// <summary>
    /// Turns a block of text the user selected in the query editor into a SQL list:
    /// <c>('a', 'b', 'c')</c>.
    ///
    /// The input is whatever people actually paste — one value per line, a comma-separated
    /// row, a column copied out of Excel with tabs, values that already carry quotes or
    /// trailing commas. All of that reduces to the same list.
    ///
    /// When every value is a number the list is left unquoted, because
    /// <c>IN ('1','2')</c> against an int column forces a conversion on every row and can
    /// cost an index seek.
    /// </summary>
    internal static class ValueListBuilder
    {
        public sealed class Options
        {
            /// <summary>Prefix string literals with N. Off by default; the column type is unknown here.</summary>
            public bool Unicode { get; set; }

            /// <summary>Wrap in parentheses. Off when you are pasting into an existing list.</summary>
            public bool Parenthesise { get; set; } = true;

            /// <summary>Break into several lines once the list gets long.</summary>
            public int ValuesPerLine { get; set; } = 10;
        }

        public static string Build(string selectedText, Options options = null)
        {
            options = options ?? new Options();

            var values = Split(selectedText);
            if (values.Count == 0)
            {
                return string.Empty;
            }

            bool numeric = values.All(IsNumeric);
            var literals = values
                .Select(v => numeric ? v : Quote(v, options.Unicode))
                .ToList();

            var sb = new StringBuilder();
            if (options.Parenthesise)
            {
                sb.Append('(');
            }

            int perLine = Math.Max(1, options.ValuesPerLine);
            for (int i = 0; i < literals.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                    sb.Append(i % perLine == 0 ? "\n    " : " ");
                }

                sb.Append(literals[i]);
            }

            if (options.Parenthesise)
            {
                sb.Append(')');
            }

            return sb.ToString();
        }

        /// <summary>How many values the text would produce, for a status message.</summary>
        public static int Count(string selectedText) => Split(selectedText).Count;

        /// <summary>
        /// Splits on newlines, commas and tabs, then strips the punctuation people leave
        /// behind: surrounding quotes or brackets, and a trailing comma or semicolon.
        /// </summary>
        private static List<string> Split(string text)
        {
            var values = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return values;
            }

            foreach (var raw in text.Split(new[] { '\r', '\n', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = raw.Trim();

                // Trailing separators left over from a pasted column or list.
                value = value.TrimEnd(';', ',').Trim();

                // A value already quoted, bracketed, or wrapped in the parens of a list.
                value = value.Trim('(', ')').Trim();
                value = Unwrap(value, '\'', '\'');
                value = Unwrap(value, '"', '"');
                value = Unwrap(value, '[', ']');

                // "N'abc'" from a copied script.
                if (value.Length > 2 &&
                    (value[0] == 'N' || value[0] == 'n') &&
                    value[1] == '\'' &&
                    value[value.Length - 1] == '\'')
                {
                    value = Unescape(value.Substring(2, value.Length - 3));
                }

                if (value.Length > 0)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static string Unwrap(string value, char open, char close)
        {
            if (value.Length >= 2 && value[0] == open && value[value.Length - 1] == close)
            {
                string inner = value.Substring(1, value.Length - 2);
                return open == '\'' ? Unescape(inner) : inner;
            }

            return value;
        }

        private static string Unescape(string value) => value.Replace("''", "'");

        private static string Quote(string value, bool unicode) =>
            (unicode ? "N'" : "'") + value.Replace("'", "''") + "'";

        /// <summary>
        /// True for something SQL Server will read as a number without quoting. Deliberately
        /// strict: anything with a currency symbol, grouping or exponent stays a string.
        /// </summary>
        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
}
