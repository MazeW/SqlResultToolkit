using System;
using System.Linq;
using SqlResultToolkit.Format;

namespace SqlResultToolkit.Tests
{
    /// <summary>
    /// The table-name guesser. This one reads text the user wrote, so the cases that
    /// matter are the ones where a naive regex would pick up something that is not a
    /// table: comments, string literals, derived tables and table variables.
    /// </summary>
    internal static partial class FormatterTests
    {
        private static void TableFinderTests()
        {
            Section("Table finder");

            Equal("simple FROM", SqlTableFinder.ForResultSet("SELECT * FROM Orders", 1), "Orders");
            Equal("schema qualified", SqlTableFinder.ForResultSet("SELECT * FROM dbo.Orders", 1), "dbo.Orders");
            Equal("bracketed", SqlTableFinder.ForResultSet("SELECT * FROM [dbo].[My Orders]", 1), "[dbo].[My Orders]");
            Equal("three-part name", SqlTableFinder.ForResultSet("SELECT * FROM db.dbo.Orders", 1), "db.dbo.Orders");
            Equal("case-insensitive keyword", SqlTableFinder.ForResultSet("select * from Orders", 1), "Orders");
            Equal("newlines and indenting", SqlTableFinder.ForResultSet("SELECT *\n  FROM\n     dbo.Orders\n ORDER BY Id", 1), "dbo.Orders");
            Equal("spaces around the dot", SqlTableFinder.ForResultSet("SELECT * FROM dbo . Orders", 1), "dbo.Orders");
            Equal("temp table", SqlTableFinder.ForResultSet("SELECT * FROM #tmp", 1), "#tmp");

            // The whole point of stripping first.
            Equal("a table named in a line comment is ignored",
                SqlTableFinder.ForResultSet("-- FROM Wrong\nSELECT * FROM Right", 1), "Right");
            Equal("a table named in a block comment is ignored",
                SqlTableFinder.ForResultSet("/* FROM Wrong */ SELECT * FROM Right", 1), "Right");
            Equal("nested block comments are handled",
                SqlTableFinder.ForResultSet("/* a /* FROM Wrong */ b */ SELECT * FROM Right", 1), "Right");
            Equal("a table named in a string is ignored",
                SqlTableFinder.ForResultSet("SELECT 'FROM Wrong' FROM Right", 1), "Right");
            Equal("an escaped quote does not end the string",
                SqlTableFinder.ForResultSet("SELECT 'it''s FROM Wrong' FROM Right", 1), "Right");

            // Things that are not tables.
            Check("a derived table is skipped",
                SqlTableFinder.ForResultSet("SELECT * FROM (SELECT 1 AS x) AS t", 1) == null);
            Check("a table-valued function is skipped",
                SqlTableFinder.ForResultSet("SELECT * FROM dbo.Fn(1)", 1) == null);
            Check("a table variable is skipped",
                SqlTableFinder.ForResultSet("SELECT * FROM @t", 1) == null);
            Check("no FROM at all", SqlTableFinder.ForResultSet("SELECT 1", 1) == null);
            Check("empty input", SqlTableFinder.ForResultSet("", 1) == null);
            Check("null input", SqlTableFinder.ForResultSet(null, 1) == null);

            // Several result sets: position picks the right one.
            const string twoBatches = "SELECT * FROM dbo.Orders;\nSELECT * FROM sys.databases;";
            Equal("first result set", SqlTableFinder.ForResultSet(twoBatches, 1), "dbo.Orders");
            Equal("second result set", SqlTableFinder.ForResultSet(twoBatches, 2), "sys.databases");

            // One table used twice is unambiguous whatever the index.
            const string selfJoin = "SELECT * FROM Orders a JOIN Orders b ON a.Id = b.ParentId";
            Equal("a self-join still has one answer", SqlTableFinder.ForResultSet(selfJoin, 1), "Orders");
            Equal("and the index does not matter", SqlTableFinder.ForResultSet(selfJoin, 9), "Orders");

            // Genuinely ambiguous: better to say nothing than to guess wrong.
            Check("a join of two tables with an out-of-range index gives up",
                SqlTableFinder.ForResultSet("SELECT * FROM A JOIN B ON A.Id = B.Id", 7) == null);
            Equal("but the index still picks one when it is in range",
                SqlTableFinder.ForResultSet("SELECT * FROM A JOIN B ON A.Id = B.Id", 2), "B");

            Equal("UPDATE names its target", SqlTableFinder.ForResultSet("UPDATE dbo.Orders SET x = 1", 1), "dbo.Orders");

            var tables = SqlTableFinder.FindTables("SELECT * FROM A JOIN B ON A.Id=B.Id JOIN C ON C.Id=B.Id");
            Equal("every table is listed in order", string.Join(",", tables), "A,B,C");
        }
    }
}
