using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SqlCellInspector.Format;
using DrawingColor = System.Drawing.Color;

namespace SqlCellInspector.UI
{
    /// <summary>
    /// Colours for the panel, taken from the live VS/SSMS theme.
    ///
    /// Chrome colours come straight from <see cref="EnvironmentColors"/>, so the panel
    /// matches whatever theme SSMS is in. Syntax colours are picked from one of two
    /// hand-tuned palettes, chosen by the measured luminance of the tool-window
    /// background rather than by theme name, which means a custom or high-contrast theme
    /// still gets readable text.
    /// </summary>
    internal sealed class Theme
    {
        private Theme(bool dark)
        {
            IsDark = dark;

            Background = Brush(EnvironmentColors.ToolWindowBackgroundColorKey, dark ? 0x1E1E1E : 0xFFFFFF);
            Foreground = Brush(EnvironmentColors.ToolWindowTextColorKey, dark ? 0xDCDCDC : 0x1E1E1E);
            Border = Brush(EnvironmentColors.ToolWindowBorderColorKey, dark ? 0x3F3F46 : 0xCCCEDB);
            ChromeBackground = Brush(EnvironmentColors.CommandBarGradientBeginColorKey, dark ? 0x252526 : 0xF5F5F5);
            SubtleForeground = Blend(Foreground.Color, Background.Color, 0.45);
            SelectionBackground = Brush(EnvironmentColors.SystemHighlightColorKey, dark ? 0x264F78 : 0xADD6FF);

            Syntax = dark ? DarkSyntax() : LightSyntax();
            Accent = dark ? Freeze(0x4FC1FF) : Freeze(0x0070C1);
            Warning = dark ? Freeze(0xD7BA7D) : Freeze(0x9A6700);
            Danger = dark ? Freeze(0xF48771) : Freeze(0xA31515);
        }

        public bool IsDark { get; }

        public SolidColorBrush Background { get; }

        public SolidColorBrush Foreground { get; }

        public SolidColorBrush Border { get; }

        public SolidColorBrush ChromeBackground { get; }

        public SolidColorBrush SubtleForeground { get; }

        public SolidColorBrush SelectionBackground { get; }

        public SolidColorBrush Accent { get; }

        public SolidColorBrush Warning { get; }

        public SolidColorBrush Danger { get; }

        private Dictionary<TokenKind, SolidColorBrush> Syntax { get; }

        public SolidColorBrush ForToken(TokenKind kind) =>
            Syntax.TryGetValue(kind, out var brush) ? brush : Foreground;

        public static Theme Current()
        {
            var background = Lookup(EnvironmentColors.ToolWindowBackgroundColorKey);
            bool dark = background.HasValue ? Luminance(background.Value) < 0.5 : false;
            return new Theme(dark);
        }

        private Dictionary<TokenKind, SolidColorBrush> DarkSyntax() => new Dictionary<TokenKind, SolidColorBrush>
        {
            { TokenKind.Plain,          Foreground },
            { TokenKind.Punctuation,    Freeze(0x9C9C9C) },
            { TokenKind.PropertyName,   Freeze(0x9CDCFE) },
            { TokenKind.String,         Freeze(0xCE9178) },
            { TokenKind.Number,         Freeze(0xB5CEA8) },
            { TokenKind.Keyword,        Freeze(0x569CD6) },
            { TokenKind.Null,           Freeze(0xC586C0) },
            { TokenKind.Comment,        Freeze(0x6A9955) },
            { TokenKind.TagDelimiter,   Freeze(0x808080) },
            { TokenKind.TagName,        Freeze(0x569CD6) },
            { TokenKind.AttributeName,  Freeze(0x9CDCFE) },
            { TokenKind.AttributeValue, Freeze(0xCE9178) },
            { TokenKind.CData,          Freeze(0xD7BA7D) },
            { TokenKind.Declaration,    Freeze(0x808080) },
            { TokenKind.Offset,         Freeze(0x6A9955) },
            { TokenKind.HexByte,        Freeze(0xDCDCAA) },
            { TokenKind.HexByteAlt,     Freeze(0xB5A77A) },
            { TokenKind.Ascii,          Freeze(0x9CDCFE) },
            { TokenKind.Error,          Freeze(0xF48771) },
            { TokenKind.Dim,            Freeze(0x5A5A5A) },
        };

        private Dictionary<TokenKind, SolidColorBrush> LightSyntax() => new Dictionary<TokenKind, SolidColorBrush>
        {
            { TokenKind.Plain,          Foreground },
            { TokenKind.Punctuation,    Freeze(0x5A5A5A) },
            { TokenKind.PropertyName,   Freeze(0x0451A5) },
            { TokenKind.String,         Freeze(0xA31515) },
            { TokenKind.Number,         Freeze(0x098658) },
            { TokenKind.Keyword,        Freeze(0x0000FF) },
            { TokenKind.Null,           Freeze(0xAF00DB) },
            { TokenKind.Comment,        Freeze(0x008000) },
            { TokenKind.TagDelimiter,   Freeze(0x800000) },
            { TokenKind.TagName,        Freeze(0x800000) },
            { TokenKind.AttributeName,  Freeze(0xFF0000) },
            { TokenKind.AttributeValue, Freeze(0x0451A5) },
            { TokenKind.CData,          Freeze(0x795E26) },
            { TokenKind.Declaration,    Freeze(0x808080) },
            { TokenKind.Offset,         Freeze(0x008000) },
            { TokenKind.HexByte,        Freeze(0x1F377F) },
            { TokenKind.HexByteAlt,     Freeze(0x4A5E99) },
            { TokenKind.Ascii,          Freeze(0x0451A5) },
            { TokenKind.Error,          Freeze(0xA31515) },
            { TokenKind.Dim,            Freeze(0xAAAAAA) },
        };

        private static SolidColorBrush Brush(ThemeResourceKey key, int fallbackRgb)
        {
            var color = Lookup(key);
            return color.HasValue ? Freeze(color.Value) : Freeze(fallbackRgb);
        }

        private static DrawingColor? Lookup(ThemeResourceKey key)
        {
            try
            {
                if (!ThreadHelper.CheckAccess())
                {
                    return null;
                }

                return VSColorTheme.GetThemedColor(key);
            }
            catch (Exception)
            {
                // A theme service that is not ready yet is not worth failing over.
                return null;
            }
        }

        private static SolidColorBrush Freeze(DrawingColor color) =>
            Freeze(Color.FromArgb(color.A, color.R, color.G, color.B));

        private static SolidColorBrush Freeze(int rgb) =>
            Freeze(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

        private static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Blend(Color a, Color b, double towardsB)
        {
            byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * towardsB);
            return Freeze(Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B)));
        }

        private static double Luminance(DrawingColor c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }
}
