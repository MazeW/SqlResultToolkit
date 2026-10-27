using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SqlResultToolkit.UI
{
    /// <summary>
    /// Reads the text of the query window the user is looking at.
    ///
    /// Needed only to guess a table name: SQL Server returns no base-table metadata for a
    /// plain SELECT, so the batch the user wrote is the only other place the name exists.
    /// Nothing is modified, and every failure path returns null so the caller just falls
    /// back to an empty Table box.
    /// </summary>
    internal static class EditorText
    {
        /// <summary>The text currently selected in the active query window, or null.</summary>
        public static string GetSelectedText()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var view = GetActiveView();
                if (view == null)
                {
                    return null;
                }

                return ErrorHandler.Failed(view.GetSelectedText(out string text)) ? null : text;
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Could not read the editor selection.", ex);
                return null;
            }
        }

        /// <summary>
        /// Replaces the current selection. This edits the user's document, so it is only
        /// ever called from an explicit command, and it lands as one undo unit: Ctrl+Z puts
        /// the original text back.
        /// </summary>
        public static bool ReplaceSelection(string replacement)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var view = GetActiveView();
                if (view == null || replacement == null)
                {
                    return false;
                }

                if (ErrorHandler.Failed(view.GetSelection(out int startLine, out int startColumn, out int endLine, out int endColumn)))
                {
                    return false;
                }

                // GetSelection reports the anchor first, which is after the end when the
                // user dragged upwards or backwards.
                if (startLine > endLine || (startLine == endLine && startColumn > endColumn))
                {
                    int line = startLine, column = startColumn;
                    startLine = endLine; startColumn = endColumn;
                    endLine = line; endColumn = column;
                }

                if (ErrorHandler.Failed(view.GetBuffer(out IVsTextLines lines)) || lines == null)
                {
                    return false;
                }

                IntPtr text = IntPtr.Zero;
                try
                {
                    text = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(replacement);
                    int result = lines.ReplaceLines(
                        startLine, startColumn, endLine, endColumn,
                        text, replacement.Length,
                        new TextSpan[1]);

                    return ErrorHandler.Succeeded(result);
                }
                finally
                {
                    if (text != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(text);
                    }
                }
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Could not replace the editor selection.", ex);
                return false;
            }
        }

        private static IVsTextView GetActiveView()
        {
            var manager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            if (manager == null)
            {
                return null;
            }

            return ErrorHandler.Failed(manager.GetActiveView(0, null, out IVsTextView view)) ? null : view;
        }

        public static string GetActiveDocumentText()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // fMustHaveFocus = 0: the user may have clicked into our panel, which
                // takes focus away from the editor without changing which document they
                // are working in.
                var view = GetActiveView();
                if (view == null)
                {
                    return null;
                }

                if (ErrorHandler.Failed(view.GetBuffer(out IVsTextLines lines)) || lines == null)
                {
                    return null;
                }

                if (ErrorHandler.Failed(lines.GetLastLineIndex(out int lastLine, out int lastIndex)))
                {
                    return null;
                }

                return ErrorHandler.Failed(lines.GetLineText(0, 0, lastLine, lastIndex, out string text))
                    ? null
                    : text;
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Could not read the active document's text.", ex);
                return null;
            }
        }
    }
}
