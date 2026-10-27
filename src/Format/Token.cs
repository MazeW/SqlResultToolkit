using System.Collections.Generic;
using System.Text;

namespace SqlCellInspector.Format
{
    internal enum TokenKind
    {
        Plain,
        Punctuation,
        PropertyName,
        String,
        Number,
        Keyword,
        Null,
        Comment,
        TagDelimiter,
        TagName,
        AttributeName,
        AttributeValue,
        CData,
        Declaration,
        Offset,
        HexByte,
        HexByteAlt,
        Ascii,
        Error,
        Dim
    }

    internal readonly struct Token
    {
        public Token(TokenKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public TokenKind Kind { get; }

        public string Text { get; }
    }

    /// <summary>
    /// Collects tokens and the plain text in one pass, so the panel can colour the output
    /// and Copy/Save can hand back exactly what is on screen.
    /// </summary>
    internal sealed class TokenWriter
    {
        private readonly List<Token> _tokens = new List<Token>();
        private readonly StringBuilder _text = new StringBuilder();

        public IReadOnlyList<Token> Tokens => _tokens;

        public int Length => _text.Length;

        public void Write(TokenKind kind, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _tokens.Add(new Token(kind, text));
            _text.Append(text);
        }

        public void Write(string text) => Write(TokenKind.Plain, text);

        public void NewLine() => Write(TokenKind.Plain, "\n");

        public void Indent(int depth, int width = 2)
        {
            if (depth > 0)
            {
                Write(TokenKind.Plain, new string(' ', depth * width));
            }
        }

        public string GetText() => _text.ToString();

        public FormattedValue ToFormatted(string language, bool truncated = false, string note = null) =>
            new FormattedValue(language, GetText(), _tokens, truncated, note);
    }

    internal sealed class FormattedValue
    {
        public FormattedValue(string language, string text, IReadOnlyList<Token> tokens, bool truncated = false, string note = null)
        {
            Language = language;
            Text = text ?? string.Empty;
            Tokens = tokens;
            Truncated = truncated;
            Note = note;
        }

        /// <summary>"json", "xml", "hex", "text", ... Used for the view label and save filter.</summary>
        public string Language { get; }

        public string Text { get; }

        /// <summary>Null when the content should be shown as plain monospace text.</summary>
        public IReadOnlyList<Token> Tokens { get; }

        public bool Truncated { get; }

        public string Note { get; }

        public static FormattedValue PlainText(string text, string language = "text", string note = null) =>
            new FormattedValue(language, text, null, false, note);
    }
}
