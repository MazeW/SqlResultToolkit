using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlResultToolkit.Format
{
    /// <summary>
    /// Guesses which table a result set came from by reading the query text.
    ///
    /// This exists because SQL Server does not tell us: a plain SELECT returns no
    /// base-table metadata, so the only other source is the batch the user wrote. That
    /// makes this a guess, and it is always presented as one — it pre-fills the Table box,
    /// which the user can overwrite.
    ///
    /// Comments and string literals are stripped first, so a table named in a comment or
    /// inside a quoted string is never picked up.
    /// </summary>
    internal static class SqlTableFinder
    {
        // schema-qualified, bracketed or bare: [db].[dbo].[T], dbo.T, T, #tmp, @var
        private const string Identifier = @"(?:\[[^\]]+\]|[A-Za-z_#@][\w$#@]*)";
        private const string QualifiedName = Identifier + @"(?:\s*\.\s*" + Identifier + @"){0,3}";

        private static readonly Regex FromOrJoin = new Regex(
            @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?<name>" + QualifiedName + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Every table named by a FROM / JOIN / UPDATE / INTO, in the order they appear.
        /// Duplicates are kept, because position is what maps a name to a result set.
        /// </summary>
        public static List<string> FindTables(string sql)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(sql))
            {
                return names;
            }

            string cleaned = StripCommentsAndStrings(sql);

            foreach (Match match in FromOrJoin.Matches(cleaned))
            {
                string name = Normalize(match.Groups["name"].Value);

                // A table-valued function or a derived table is not something we can
                // UPDATE, and "FROM (" never names a table at all.
                if (name == null || IsFollowedByOpenParen(cleaned, match.Index + match.Length))
                {
                    continue;
                }

                names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// The best guess for the Nth result set (1-based). Falls back to the only table
        /// in the batch when there is exactly one, which covers the common single-SELECT
        /// case even if the counting is off.
        /// </summary>
        public static string ForResultSet(string sql, int resultSetIndex)
        {
            var tables = FindTables(sql);
            if (tables.Count == 0)
            {
                return null;
            }

            var distinct = new List<string>();
            foreach (var table in tables)
            {
                if (!distinct.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    distinct.Add(table);
                }
            }

            if (distinct.Count == 1)
            {
                return distinct[0];
            }

            if (resultSetIndex >= 1 && resultSetIndex <= tables.Count)
            {
                return tables[resultSetIndex - 1];
            }

            // Several candidates and no way to choose: say nothing rather than guess wrong.
            return null;
        }

        private static string Normalize(string name)
        {
            string trimmed = Regex.Replace(name ?? string.Empty, @"\s*\.\s*", ".").Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            // A table variable or a derived-table alias is not a target we can script.
            if (trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                return null;
            }

            return trimmed;
        }

        private static bool IsFollowedByOpenParen(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index < text.Length && text[index] == '(';
        }

        /// <summary>
        /// Blanks out -- line comments, /* block comments */, 'literals' and "quoted
        /// identifiers", replacing them with spaces so every match offset stays valid.
        /// </summary>
        private static string StripCommentsAndStrings(string sql)
        {
            var sb = new StringBuilder(sql);
            int i = 0;

            while (i < sb.Length)
            {
                char c = sb[i];

                if (c == '-' && i + 1 < sb.Length && sb[i + 1] == '-')
                {
                    while (i < sb.Length && sb[i] != '\n')
                    {
                        sb[i++] = ' ';
                    }
                }
                else if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '*')
                {
                    int depth = 1;                 // T-SQL block comments nest
                    sb[i++] = ' ';
                    sb[i++] = ' ';

                    while (i < sb.Length && depth > 0)
                    {
                        if (i + 1 < sb.Length && sb[i] == '/' && sb[i + 1] == '*')
                        {
                            depth++;
                            sb[i++] = ' ';
                            sb[i++] = ' ';
                        }
                        else if (i + 1 < sb.Length && sb[i] == '*' && sb[i + 1] == '/')
                        {
                            depth--;
                            sb[i++] = ' ';
                            sb[i++] = ' ';
                        }
                        else
                        {
                            if (sb[i] != '\n')
                            {
                                sb[i] = ' ';
                            }

                            i++;
                        }
                    }
                }
                else if (c == '\'' || c == '"')
                {
                    char quote = c;
                    sb[i++] = ' ';

                    while (i < sb.Length)
                    {
                        if (sb[i] == quote)
                        {
                            // A doubled quote is an escaped one, not the end.
                            if (i + 1 < sb.Length && sb[i + 1] == quote)
                            {
                                sb[i++] = ' ';
                                sb[i++] = ' ';
                                continue;
                            }

                            sb[i++] = ' ';
                            break;
                        }

                        if (sb[i] != '\n')
                        {
                            sb[i] = ' ';
                        }

                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }

            return sb.ToString();
        }
    }
}
