# Backlog

Things asked for but not built yet, newest first.

## Tint edited cells in the grid — built, proven, does not work in SSMS

> "Yes, do the cell tinting — highlight edited cells in the grid."
> — 2026-10-26

**Status: written and disabled.** `src\Grid\CellHighlighter.cs` is complete and correct; the
call to `CellHighlighter.Attach` in `GridMonitor.Visit` is commented out, so nothing runs.

**What is proven.** `test\run-tint-test.ps1` builds the highlighter against the *real*
`Microsoft.SqlServer.GridControl.dll`, paints a grid to a bitmap and samples pixels. Seven
assertions pass: the edited cell takes the amber tint, its neighbours are untouched (so an
override does not leak onto the next cell), an unparsed value takes the red tint, removing
the change restores the cell, and the dark palette is used in dark mode.

**What is proven about SSMS, from its own IL.**

- `GridControl.GetCellGDIObjects` seeds `TextBrush` (il 130) and `BKBrush` (il 144) *before*
  raising `CustomizeCellGDIObjects`, and reads them back (il 175/189). So handlers may
  override just the cells they care about.
- `GridControl.PaintOneCell` calls `GetCellGDIObjects` (il 171), then `GetCellFont`, then
  `DoCellPainting` (il 199). Nothing re-assigns the brushes in between.
- `GridResultsGrid.GetCellGDIObjects` seeds its own two brushes and *then* calls base
  (il 26), so base — and therefore the event — has the last word.
- `GridResultsTabPage.AddGridContainer` itself calls `add_CustomizeCellGDIObjects`, so the
  event demonstrably fires for results grids.

Every link in that chain says this should work. It does not: with a change staged and the
selection moved off the cell, the cell paints normally in SSMS.

**Where to pick it up.** The remaining unknown is whether the handler is invoked at all
inside SSMS. The next step is instrumentation rather than more static analysis — have
`OnCustomizeCell` append a line to a temp file for its first few calls, install, click once,
and read it. That answers in one cycle whether the problem is attachment (the handler never
runs, so look at `CellHighlighter.Attach` and the `Changed` set) or painting (it runs and
sets the brush, but something later discards it).

## ~~Turn selected rows into an array literal~~ — done in 1.5.1

Both halves shipped, after the clarification that "in editor" meant selected text:

- **Editor text** → `Edit → Quote Selection as SQL List` (Ctrl+Alt+Q, Ctrl+Alt+L) replaces
  the selection with `('a', 'b', 'c')`. `ValueListBuilder` handles newline/comma/tab input,
  strips quotes, brackets and trailing commas, doubles apostrophes, and leaves an all-numeric
  list unquoted.
- **Grid cells** → the Script tab's **Values** kind, type-aware and de-duplicated.

Worth recording for next time: **SSMS's query editor does not use `IDM_VS_CTXT_CODEWIN`.**
A command placed in that group compiles, binds and is reachable by keyboard, but never
appears in the editor's right-click menu — the menu SSMS draws there comes from SQLEditors'
own command table. Finding that menu's GUID means parsing the `.ctmenu` resource out of
SQLEditors.dll. The command lives on the Edit menu instead.

## Bigger UX direction (DBeaver-like)

> "generally i want similar functionality of dbeaver kind of, but i like the idea of just
> generating a script instead of auto inserting it"
> — 2026-10-26

DBeaver's model is: edit cells inline in the grid, changed cells are highlighted in place,
then Save (or "Generate SQL"). We cannot edit SSMS's grid inline — it is custom-drawn and
read-only, and fighting it would be fragile. What we *can* copy is the mental model:

- **Edit from the Row tab**, not just the Value tab. The Row tab already lists every column
  of the current row; making each value editable there, with a per-row "Add changes"
  action, would be much closer to filling in a record than clicking cell by cell.
- **A dedicated Changes tab** instead of the bar on the Script tab, once the list grows:
  grouped by row, with old → new, per-item undo, and the script underneath.
- **Show which cells are edited.** `GridControl` raises `CustomizeCellGDIObjects`, which is
  how SSMS itself colours cells, so a pending change could tint the actual grid cell the way
  DBeaver does. This is the single biggest win for "I can see what I changed" and is
  genuinely feasible — it is a public event on a public type.
- **Revert all / revert row** as first-class actions.
