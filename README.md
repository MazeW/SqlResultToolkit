# SQL Cell Inspector

A dockable side panel for SSMS 22 that shows the **complete** value of the selected
result-grid cell, formatted and syntax-coloured for its SQL Server type.

Built against SSMS **22.11.12324.101** (x64).

## What it does

The panel has three tabs: **Value**, **Row** and **Script**.

### Script — INSERT / UPDATE / DELETE / SELECT / IN list

Select cells in the grid and the Script tab writes the statement for them. **Nothing is
ever executed**: the script is produced for you to read, copy, or open in a query window.

- **INSERT** leaves out identity, computed and read-only columns, and says which.
- **UPDATE** sets the selected columns and matches on the key. A real key is excluded from
  `SET`; when there is no key, the predicate falls back to every column that was read and
  `SET` keeps the selected columns, because "the row that currently looks like this" is
  then the only thing to match on.
- **DELETE** and **SELECT** use the same predicate.
- **IN list** turns a selected column into `[Col] IN (1, 2, 3)`, dropping NULLs with a note
  that `IN` never matches them.
- NULL becomes `IS NULL` in a predicate, never `= NULL`.

**Table and Key boxes.** A plain `SELECT` returns no base-table or key metadata, so for most
query windows the provider reports neither. Type them once (`dbo.Orders`, `OrderId`) and they
stick for the session; they are pre-filled when the provider does report them. A key typed
here is fetched even when it is not selected and not on screen. Every assumption the
generator had to make appears as a `-- WARNING:` comment at the top of the script, including
a loud one when there is no key and the statement may therefore hit more than one row.

### Editing cells and scripting all of them at once

Press **Edit** on the Value tab, retype the value, and press **Stage edit**. Do that for as
many cells as you like, across as many rows and columns as you like. The Script tab then
turns *all* of the staged edits into one UPDATE script: one statement per row, setting only
the columns that were actually retyped.

```sql
UPDATE [dbo].[CellInspectorDemo]
   SET [Name] = N'O''Brien, Ada',
       [Amount] = 42.5
 WHERE [Id] = 1;
UPDATE [dbo].[CellInspectorDemo]
   SET [Name] = N'Grace M. Hopper'
 WHERE [Id] = 2;
```

Typed text is converted using the column's declared type, so `O'Brien` in an `nvarchar`
column becomes `N'O''Brien'` and text that does not parse as the column's type is flagged
rather than silently quoted. A **NULL** checkbox stages NULL instead.

The pending changes are always visible, never hidden state:

- the Script tab lists them as `row 1 · Name   ~~Ada Lovelace~~ → O'Brien, Ada`, each with its
  own × to drop just that one, and **Discard all** to drop the lot;
- the tab itself reads **Script (3)** so the count is visible from the other tabs;
- the cell's header carries a `pending change → …` badge, and **Remove change** drops it.

Nothing is ever written to the database, and pending changes are dropped when the grid goes
away — which is what re-running the query does.

**The Table box fills itself.** SQL Server reports no base table for a plain SELECT, so the
table name is read out of the query text you wrote and the hint says so, because it is a
guess. Moving to another result set re-reads it (the second SELECT in a batch is usually a
different table); a name you typed yourself is never overwritten.

**The script box is editable.** Tweak a WHERE clause, comment a row out, whatever —
**Copy** and **New query** use what is in the box. Moving the grid selection will not
overwrite your changes; it offers **Regenerate** instead. Changing the kind, table or key
*does* rebuild it, and Ctrl+Z brings your version back.

### Making a list of values

Two ways, depending on where the values are.

**From text in the query editor.** Select a column of values — one per line, comma
separated, or tab separated as pasted out of a spreadsheet — and press
**Ctrl+Alt+Q, Ctrl+Alt+L** (also **Edit → Quote Selection as SQL List**). The selection is
replaced with a SQL list:

```sql
SELECT * FROM dbo.Orders WHERE Name IN
('Ada Lovelace', 'Grace Hopper', 'O''Brien', 'Katherine')
```

Apostrophes are doubled, values that already carry quotes, brackets or trailing commas are
cleaned up first, and a list of pure numbers is left unquoted — `(1, 2, 3)`, not
`('1','2','3')`, because quoting an int column forces a conversion on every row. The result
also goes on the clipboard, and the replacement is a single undo unit, so Ctrl+Z puts your
text back.

The command is on the **Edit** menu rather than the editor's right-click menu: SSMS's query
editor does not use the shell's standard code-window context menu, so an item placed there
never appears.

**From cells in the results grid.** The Script tab's **Values** kind turns the selected
column into the same thing, formatted from the column's real type and with duplicates
collapsed. (**IN list** is the same values with `[Column] IN (…)` around them.)

### Row — the whole record

Every column of the current row on one line each, with name, type and value, the current
column highlighted and key columns marked. **Copy row** gives `name = value` lines; **Copy
names** gives the column list. This is what makes a 40-column result set readable without
scrolling the grid sideways.

### Value — the cell itself

Click any cell in a query results grid and the panel shows:

- **A formatted view.** JSON is pretty-printed and coloured; XML is re-indented with tag,
  attribute and CDATA colouring; binary gets a hex dump with an ASCII gutter; PNG/JPEG/GIF/BMP/WebP/ICO
  payloads render as an image. A view picker lets you force Text, JSON, XML, Hex, Base64 or Image,
  and `Auto` tells you which it chose.
- **A details list**: the exact-value breakdown for the type (see the table below), plus the
  provider's schema facts for the column — size, precision, scale, nullability, base table
  and column, UDT assembly, XML schema collection.
- **Badges** for the things that change how you should read the value: `NULL`,
  `possibly truncated`, `string form only`, the sniffed content type, `view capped`.
- **Copy** (the formatted text) and **Save…** (text as UTF-8, binary byte-exact, with the
  file extension picked from the type or the sniffed content).
- **Pin** to freeze the panel on one value and stop following the selection.

### Per-type details

| Category | What you get |
|---|---|
| `char` `varchar` `text` `nchar` `nvarchar` `ntext` `sysname` | character count, UTF-8 and UTF-16 byte counts, line count, CR/LF/tab/control-character tallies, leading and trailing whitespace, non-ASCII and astral code-point counts, and a content sniff that spots JSON, XML or Base64 hiding in a text column (Base64 is decoded and sniffed again) |
| `json` | the above plus object, array, property and depth counts |
| `xml` | element, attribute and depth counts |
| `binary` `varbinary` `image` | length with a human-readable size, sniffed content type, `0x…` T-SQL literal, SHA-256, and a strict-UTF-8 decode when the bytes are really text |
| `timestamp` `rowversion` | `0x…` literal and the big-endian unsigned value |
| `bigint` `int` `smallint` `tinyint` | exact value, grouped, hex, binary, CLR type, and a Unix seconds/milliseconds reading when the magnitude looks like an epoch timestamp |
| `decimal` `numeric` `money` `smallmoney` | invariant exact value, grouped, and the scale actually stored (so `12345.6700` does not silently become `12345.67`) |
| `float` `real` | round-trip (`R`) and full-precision (`G17`/`G9`) forms, raw IEEE 754 bits, NaN/Infinity flag |
| `bit` | `1 (true)` / `0 (false)` |
| `date` `datetime` `datetime2` `smalldatetime` | ISO 8601, round-trip `o`, time of day, weekday, day of year, ISO week, ticks, `DateTimeKind`, and a relative "x ago" |
| `datetimeoffset` | round-trip `o`, UTC, the offset, your machine's local time, Unix seconds, UTC ticks, relative |
| `time` | `c` form, ISO 8601 duration, total seconds, ticks |
| `uniqueidentifier` | `D`/`N`/`B` forms, raw bytes, RFC 4122 version and variant, and whether it looks like `NEWID()` or `NEWSEQUENTIALID()` |
| `geometry` `geography` | WKT, SRID, geometry type, point and geometry counts, validity, area/length, lat/long — read off `Microsoft.SqlServer.Types` by reflection, so no hard dependency on that assembly |
| `hierarchyid` | path, depth, level, encoded length, `0x…` literal |
| `sql_variant` | the underlying CLR type and which category it behaves as |
| `vector` | treated as text (its JSON array form) |
| anything else, including CLR UDTs | bytes if it can get them, else `ToString()`, plus the CLR type name |

## How it hooks into SSMS

The results grid is still the original Windows Forms `GridControl` from
`Microsoft.SqlServer.GridControl.dll`, and the parts this needs are **public**:
`GridControl.GridStorage`, `GetCurrentCell`, `GetHeaderInfo`,
`GetStorageColumnIndexByUIIndex`, `ColumnsNumber` and the `SelectionChanged` event.

- **Finding grids** — `EnumWindows`/`EnumChildWindows` over this process, then
  `Control.FromHandle`. The grid is a real HWND-backed control, so this hands back the live
  instance without touching any internal SSMS type. Grids only exist after a batch returns
  a result set, so the monitor rescans about once a second.
- **Following the selection** — `SelectionChanged` for immediate response, plus a 250 ms
  poll of `GetCurrentCell` as a safety net for navigation paths that do not raise it.
  Repeat positions are de-duplicated, so the poll is nearly free.
- **Reading the value** — `IGridStorage.GetCellDataAsString` is public and always works.
  For the *typed* value the storage object is SQLEditors' internal `QEResultSet`, so
  `GetCellData`, `IsCellDataNull`, `GetServerDataTypeName`, `GetFormattedDataTypeName`,
  `GetFieldType`, `GetSchemaRow`, `IsXMLColumn` and `IsJsonColumn` are reached by name
  through cached reflection. Every one of those is optional: if a member is missing the
  panel degrades to the string form and says so with the `string form only` badge.
- **Two column index spaces.** `QEResultSet` is addressed by *two different* column
  indices, and conflating them is an off-by-one that silently shows you the cell to the
  left:
  - **Grid index** (row-number gutter at 0) for the cell *contents* —
    `GetCellDataAsString` and `GetCellData`. This is what the grid passes its storage, and
    what `GetStorageColumnIndexByUIIndex` returns; "storage index" in that method's name
    means this space.
  - **Data index** (0-based, gutter excluded) for the result-set *metadata* —
    `GetServerDataTypeName`, `GetFormattedDataTypeName`, `GetFieldType`, `GetSchemaRow`,
    `IsXMLColumn`, `IsJsonColumn`, `ColumnNames`.

  The offset between them is computed from `GridControl.ColumnsNumber` minus
  `QEResultSet.NumberOfDataColumns` rather than assumed to be 1. Clicking the gutter says
  so instead of showing you column 0 by accident.

  `IsCellDataNull` is deliberately not used: which space it takes is not established, and
  a `GetCellData` that returns null is already a decisive NULL test.
- **Theming** — chrome colours come from `EnvironmentColors`; the syntax palette is chosen
  from the measured luminance of the tool-window background, so custom and high-contrast
  themes stay readable. It re-renders on `VSColorTheme.ThemeChanged`.

Everything that crosses into SSMS is wrapped: a failure is recorded, surfaced, and left to
go no further. Problems appear in a bar at the bottom of the panel — count, the latest
message with its real exception type, and **Details** / **Copy** / **Dismiss**. They also go
to the activity log (`SqlCellInspector` source), but that is a fallback: SSMS does not write
an activity log at all unless it was started with `/log`, so on its own it loses nearly
every failure.

## Build

Needs MSBuild (Visual Studio Build Tools is enough — **no** VS SDK workload) and an
installed SSMS 22. `VSCT.exe`, `CreatePkgDef.exe` and the .NET Framework 4.7.2 reference
assemblies are **not** committed — they are ~176 MB of third-party binaries.
`tools
estore-tools.ps1` fetches them from nuget.org, and `build.ps1` calls it
automatically when they are missing, so a fresh clone still builds in one command.

```powershell
.\build.ps1
```

That compiles the command table, builds `SqlCellInspector.dll` against SSMS's own
assemblies, generates the pkgdef from the package attributes, and zips
`dist\SqlCellInspector.vsix`.

Point it at a different SSMS with `-SsmsIde`.

## Sharing it

The VSIX is unsigned, and that is fine for handing to colleagues. The VSIX Installer shows:

```
Install
  SQL Cell Inspector
  Digital Signature: None
Select the product(s) you want to install the extension to:
  [x] SQL Server Management Studio 22
```

No warning, no block — just a factual line, and the Install button works. (Verified on
2026-10-27 by uninstalling and running the installer interactively.) The one case where
this stops working is an organisation that enforces signed extensions, which is a policy
setting rather than anything about the package.

If signing ever becomes necessary, `Microsoft.VSSDK.Vsixsigntool` on nuget.org provides
`VsixSignTool.exe`; the certificate is the hard part, not the tooling. A self-signed
certificate does **not** help here: every recipient would have to add it to their trusted
roots, which is a worse thing to ask of someone than installing an unsigned extension.
[SignPath Foundation](https://signpath.org/) issues free certificates to open-source
projects and is the realistic free route if it is ever needed.

## Install

```powershell
.\build.ps1 -Install
```

or run SSMS's own installer directly:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" .\dist\SqlCellInspector.vsix
```

Then restart SSMS and open it from **View → Other Windows → SQL Cell Inspector**, or press
**Ctrl+Alt+Q, Ctrl+Alt+V**. Dock it on the right next to the results grid.

To remove it: **Tools → Extensions and Updates** (or `VSIXInstaller.exe /uninstall:SqlCellInspector.79da0b49-76bd-41ff-91b6-97d0fea27706`).

## Test

```powershell
.\test\run-tests.ps1
```

323 assertions over the JSON and XML printers, the hex dump and content sniffing, the type
catalog, the value describer and renderer, T-SQL literal formatting for every type, every branch of the script generator, the staged-edit path, the table-name guesser, value-list building, and the problem recorder. They compile the same source files the extension uses
and depend on nothing from Visual Studio, so they run without SSMS.

### End-to-end

```powershell
.\test\setup-demo-db.ps1        # dbo.CellInspectorDemo in LocalDB "test"; -Drop removes it
. .\test\ui-helpers.ps1         # Start-SsmsWith, Send-Keys, Click-Screen, Save-Screen
Start-SsmsWith -ScriptPath .\test\demo.sql
```

`demo.sql` returns two result sets over a table with one column per interesting type. The
helpers drive SSMS from outside (SendKeys and screen coordinates, since the grid is
custom-drawn) and screenshot it, which is how the grid layer was verified.

The grid plumbing — `GridMonitor`, `SelectionReader`, `ResultSetAccessor`, the views — is
**not** covered by those tests: it only has meaning against a live SSMS with a real result
set. It was driven end to end in SSMS 22.11.12324.101 against the fixture above,
confirming: grid discovery and selection following; the two column index spaces (including
the first data column); result-set numbering across two stacked grids, with the right
per-grid column count; type detection and formatting for int, nvarchar-holding-JSON, xml,
varbinary, decimal, float, bit, datetime2, datetimeoffset, date, time, uniqueidentifier and
rowversion; the Row tab; INSERT and UPDATE over a two-row six-column selection; the Table
and Key boxes; and staging three edits across two rows and two columns, which produced one
UPDATE per row with row 1's two columns collapsed into a single statement.

**Still untested:** reordered columns (SSMS enables reordering — `ResultSetAndGridContainer`
sets `ColumnsReorderableByDefault = true`, confirmed from its IL — but dragging a header was
not exercised), DELETE and IN list against a live grid, Results to Text, result sets in
separate tabs rather than stacked, and the row cap on very large selections.

## Known limits

- **Generated statements are never executed, and never validated against the server.** They
  are text. Read them — especially a DELETE, and especially when the no-key warning is
  present — before running them.
- **A plain SELECT reports no base table and no key.** That is SQL Server's behaviour, not a
  gap in the panel: base-table metadata only comes back when the batch asks for key info.
  The Table and Key boxes exist for exactly this; without them the script carries a
  `[<table>]` placeholder and a predicate over every column.
- Scripting reads at most 1,000 selected rows and says how many it left out.
- The script generator targets one table. A selection spanning a join is scripted against
  the first base table, with a warning.

- **SSMS truncates before the panel ever sees the value.** Tools → Options → Query Results →
  SQL Server → Results to Grid caps non-XML character data (43,679 characters by default)
  and XML separately. The panel can only show what SSMS retrieved. When a length lands
  exactly on one of those caps you get a `possibly truncated` badge — raise the cap and
  re-run the query to see the rest. The panel does not go back to the server itself.
- The syntax-colouring path stops at 512,000 characters and falls back to plain monospace
  text; the hex dump caps at 1 MiB. Both say so in the panel.
- Spatial, `hierarchyid` and other UDT details depend on `Microsoft.SqlServer.Types` being
  loaded in the SSMS process; where it is not, you still get the bytes and the SRID.
- `Microsoft.VisualStudio.Interop` consolidated the old per-version interop assemblies in
  the VS 17.x/18 shell, so the references are not embedded interop types. That makes the
  assembly shell-version-sensitive: rebuild against a different SSMS major version.
- Reading the typed value relies on `QEResultSet` member *names*. A future SSMS that renames
  them degrades the panel to the string form rather than breaking it, but the richer details
  would need the names updated.

## Layout

```
build.ps1                       build + package (+ -Install)
src/
  SqlCellInspectorPackage.cs    AsyncPackage, commands, logging wrapper
  Diagnostics.cs                recorded problems, shown in the panel
  CellInspectorWindow.cs        ToolWindowPane; owns the monitor while open
  SqlCellInspector.vsct         menu command and key binding
  VSPackage.resx                embeds the compiled .cto as Menus.ctmenu
  source.extension.vsixmanifest targets Microsoft.VisualStudio.Ssms [22.0,)
  Grid/
    GridMonitor.cs              finds grids, follows the selection, numbers result sets
    CellReader / ResultSetAccessor.cs  reads one cell; late-bound QEResultSet access
    SelectionReader.cs          GridIndexMap, column catalog, selection and row reads
    PendingEdits.cs             staged cell edits, scoped to a grid and the session
    CellSnapshot.cs             the captured value + metadata
  Format/
    Token.cs                    token kinds and the writer
    JsonPrettyPrinter.cs        re-indent + colour + shape measurement
    XmlPrettyPrinter.cs         re-indent + colour + shape measurement (DTD disabled)
    HexDump.cs                  hex dump, T-SQL literal, content sniffing
    SqlTypeCatalog.cs           server type name -> category
    ValueDescriber.cs           the per-type details list
    CellRenderer.cs             view-mode selection and dispatch
    SqlLiteral.cs               CLR value -> T-SQL literal; identifier quoting
    ScriptGenerator.cs          INSERT / UPDATE / DELETE / SELECT / IN list
    SqlTableFinder.cs           guesses the table from the query text
    ValueListBuilder.cs         selected text -> ('a', 'b', 'c')
  UI/
    Theme.cs                    VS theme colours + syntax palettes
    EditorText.cs               reads and replaces the query window's text/selection
    CellInspectorControl.cs     the panel shell + Value tab (WPF in code, no XAML)
    RowView.cs                  the Row tab
    ScriptView.cs               the Script tab
test/
  FormatterTests.cs             value/format assertions
  ScriptTests.cs                literal + script generator assertions
  TableFinderTests.cs           table-name guessing
  ValueListTests.cs             value lists from text and from grid cells
  DiagnosticsTests.cs           the problem recorder (323 in total)
  TintHarness.cs                pixel-samples cell tinting against a real GridControl
  run-tint-test.ps1             compile + run that harness
  run-tests.ps1                 compile + run
  setup-demo-db.ps1             creates/drops the LocalDB fixture
  demo.sql                      two result sets over every interesting type
  ui-helpers.ps1                drive SSMS from outside and screenshot it
tools/                          vendored VSCT, CreatePkgDef, net472 ref assemblies
dist/SqlCellInspector.vsix      the build output
```
