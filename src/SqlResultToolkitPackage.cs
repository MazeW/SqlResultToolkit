using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SqlResultToolkit
{
    /// <summary>
    /// Package for the SQL Result Toolkit tool window.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ResultToolkitWindow),
        Style = VsDockStyle.Linked,
        Orientation = ToolWindowOrientation.Right,
        MultiInstances = false,
        Transient = false)]
    [Guid(PackageGuidString)]
    public sealed class SqlResultToolkitPackage : AsyncPackage
    {
        public const string PackageGuidString = "79da0b49-76bd-41ff-91b6-97d0fea27706";
        public static readonly Guid CommandSet = new Guid("ca2d1c5c-c7f8-434d-9bab-ae97a8f0291a");
        public const int CmdIdShowResultToolkit = 0x0100;
        public const int CmdIdQuoteSelectionAsList = 0x0102;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                commands.AddCommand(new MenuCommand(
                    (s, e) => ShowWindow(),
                    new CommandID(CommandSet, CmdIdShowResultToolkit)));

                var quote = new OleMenuCommand(
                    (s, e) => QuoteSelectionAsList(),
                    new CommandID(CommandSet, CmdIdQuoteSelectionAsList));

                // Only offer it when there is actually a selection to convert.
                quote.BeforeQueryStatus += (s, e) =>
                {
                    if (s is OleMenuCommand command)
                    {
                        command.Enabled = !string.IsNullOrWhiteSpace(UI.EditorText.GetSelectedText());
                    }
                };

                commands.AddCommand(quote);
            }
        }

        /// <summary>
        /// Replaces the selected lines in the query editor with ('a', 'b', 'c').
        ///
        /// This edits the document, which is why it is a command the user invokes rather
        /// than anything automatic. It lands as a single undo unit, so Ctrl+Z puts the
        /// original text back, and the result also goes on the clipboard in case the
        /// replacement is not what was wanted.
        /// </summary>
        private void QuoteSelectionAsList()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                string selection = UI.EditorText.GetSelectedText();
                if (string.IsNullOrWhiteSpace(selection))
                {
                    return;
                }

                string list = Format.ValueListBuilder.Build(selection);
                if (string.IsNullOrEmpty(list))
                {
                    return;
                }

                try
                {
                    System.Windows.Clipboard.SetText(list);
                }
                catch (Exception)
                {
                    // The clipboard is a convenience here, not the point.
                }

                UI.EditorText.ReplaceSelection(list);
            }
            catch (Exception ex)
            {
                Log.Error("Quoting the selection failed.", ex);
            }
        }

        private void ShowWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var pane = FindToolWindow(typeof(ResultToolkitWindow), 0, create: true) as ToolWindowPane;
                if (pane?.Frame is IVsWindowFrame frame)
                {
                    ErrorHandler.ThrowOnFailure(frame.Show());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not show the Result Toolkit window.", ex);
            }
        }

        /// <summary>
        /// Writes to the SSMS activity log. Everything in this extension reaches SSMS
        /// through reflection or event handlers on someone else's controls, so failures
        /// are logged and swallowed rather than allowed to tear down the IDE.
        /// </summary>
        internal static class Log
        {
            private const string Source = "SqlResultToolkit";

            public static void Error(string message, Exception ex = null)
            {
                // Record first: the activity log is only written when SSMS was started
                // with /log, so on its own it loses almost every failure.
                Diagnostics.Record(message, ex);
                Write(__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR, message, ex);
            }

            public static void Info(string message)
            {
                Write(__ACTIVITYLOG_ENTRYTYPE.ALE_INFORMATION, message, null);
            }

            private static void Write(__ACTIVITYLOG_ENTRYTYPE kind, string message, Exception ex)
            {
                try
                {
                    if (ex != null)
                    {
                        message = message + Environment.NewLine + ex;
                    }

                    if (ThreadHelper.CheckAccess())
                    {
                        var log = Package.GetGlobalService(typeof(SVsActivityLog)) as IVsActivityLog;
                        log?.LogEntry((uint)kind, Source, message);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[" + Source + "] " + message);
                    }
                }
                catch
                {
                    // Logging must never be the thing that fails.
                }
            }
        }
    }
}
