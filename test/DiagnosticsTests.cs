using System;
using System.Linq;
using System.Reflection;

namespace SqlCellInspector.Tests
{
    /// <summary>
    /// The problem recorder behind the panel's problems bar.
    /// </summary>
    internal static partial class FormatterTests
    {
        private static void DiagnosticsTests()
        {
            Section("Diagnostics");

            Diagnostics.Clear();
            Equal("starts empty", Diagnostics.Count, 0);
            Check("nothing is latest", Diagnostics.Latest == null);

            int raised = 0;
            EventHandler handler = (s, e) => raised++;
            Diagnostics.Changed += handler;

            Diagnostics.Record("first thing broke", null);
            Equal("records an entry", Diagnostics.Count, 1);
            Equal("raises Changed", raised, 1);
            Equal("latest is the newest", Diagnostics.Latest.Message, "first thing broke");

            Diagnostics.Record("second thing broke", new InvalidOperationException("nope"));
            Equal("latest moves on", Diagnostics.Latest.Message, "second thing broke");
            Equal("keeps the exception type and message", Diagnostics.Latest.Detail, "InvalidOperationException: nope");

            // The reflection wrapper would otherwise hide the real failure behind
            // "Exception has been thrown by the target of an invocation."
            var wrapped = new TargetInvocationException(new FormatException("bad literal"));
            Diagnostics.Record("reflected call failed", wrapped);
            Equal("unwraps TargetInvocationException", Diagnostics.Latest.Detail, "FormatException: bad literal");

            Check("newest first for display", Diagnostics.Recent().First().Message == "reflected call failed");
            Equal("recent returns everything", Diagnostics.Recent().Count, 3);

            string text = Diagnostics.ToText();
            Check("copy text names the extension", text.Contains("SQL Cell Inspector"));
            Check("copy text includes every entry",
                text.Contains("first thing broke") && text.Contains("second thing broke") && text.Contains("reflected call failed"));

            Diagnostics.Record(null, null);
            Equal("a null message records nothing", Diagnostics.Count, 3);

            // The ring is bounded, so a repeating failure cannot grow without limit.
            for (int i = 0; i < 200; i++)
            {
                Diagnostics.Record("flood " + i, null);
            }

            Equal("the list is capped", Diagnostics.Count, 100);
            Equal("and keeps the newest", Diagnostics.Latest.Message, "flood 199");

            raised = 0;
            Diagnostics.Clear();
            Equal("clear empties it", Diagnostics.Count, 0);
            Equal("clear raises Changed", raised, 1);

            Diagnostics.Clear();
            Equal("clearing an empty list raises nothing more", raised, 1);
            Check("copy text of an empty list says so", Diagnostics.ToText().Contains("(none)"));

            Diagnostics.Changed -= handler;
        }
    }
}
