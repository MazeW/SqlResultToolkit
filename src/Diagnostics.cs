using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SqlResultToolkit
{
    /// <summary>
    /// Keeps the problems this extension has hit, so they can be shown in the panel.
    ///
    /// Everything used to go only to the SSMS activity log, which is not written at all
    /// unless SSMS was started with /log. That meant any failure was invisible: the panel
    /// would quietly show nothing and there was no way to find out why. These entries are
    /// surfaced in the panel itself and can be copied in one click.
    /// </summary>
    internal static class Diagnostics
    {
        private const int Capacity = 100;

        private static readonly object Gate = new object();
        private static readonly LinkedList<Entry> Entries = new LinkedList<Entry>();

        public sealed class Entry
        {
            public DateTime When { get; set; }

            public string Message { get; set; }

            public string Detail { get; set; }

            public override string ToString() =>
                When.ToString("HH:mm:ss") + "  " + Message +
                (string.IsNullOrEmpty(Detail) ? string.Empty : Environment.NewLine + Detail);
        }

        /// <summary>Raised whenever an entry is added or the list is cleared.</summary>
        public static event EventHandler Changed;

        public static int Count
        {
            get { lock (Gate) { return Entries.Count; } }
        }

        public static Entry Latest
        {
            get { lock (Gate) { return Entries.Last?.Value; } }
        }

        public static IReadOnlyList<Entry> Recent()
        {
            lock (Gate)
            {
                return Entries.Reverse().ToList();
            }
        }

        public static void Record(string message, Exception ex)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            var entry = new Entry
            {
                When = DateTime.Now,
                Message = message,
                Detail = Describe(ex)
            };

            lock (Gate)
            {
                Entries.AddLast(entry);
                while (Entries.Count > Capacity)
                {
                    Entries.RemoveFirst();
                }
            }

            Raise();
        }

        public static void Clear()
        {
            lock (Gate)
            {
                if (Entries.Count == 0)
                {
                    return;
                }

                Entries.Clear();
            }

            Raise();
        }

        /// <summary>Everything recorded, oldest first, for the clipboard.</summary>
        public static string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SQL Result Toolkit — recorded problems");
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            lock (Gate)
            {
                if (Entries.Count == 0)
                {
                    sb.AppendLine("(none)");
                }

                foreach (var entry in Entries)
                {
                    sb.AppendLine(entry.ToString());
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Unwraps the reflection wrapper so the message names the real failure rather
        /// than "Exception has been thrown by the target of an invocation".
        /// </summary>
        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return null;
            }

            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return ex.GetType().Name + ": " + ex.Message;
        }

        private static void Raise()
        {
            try
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A listener blowing up must not take the logger with it.
            }
        }
    }
}
