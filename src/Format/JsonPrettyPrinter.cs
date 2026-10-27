using System;
using System.Globalization;
using System.Text;

namespace SqlResultToolkit.Format
{
    /// <summary>
    /// Re-indents JSON and emits colour tokens in one pass.
    ///
    /// Hand-written rather than delegating to a JSON library on purpose: the extension
    /// loads into SSMS's AppDomain, where pulling in another copy of Newtonsoft or
    /// System.Text.Json invites an assembly-version fight with the IDE. It also means
    /// malformed JSON still renders: the printer emits everything it understood, then an
    /// error token saying where it stopped.
    /// </summary>
    internal static class JsonPrettyPrinter
    {
        public static bool LooksLikeJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return false;
            }

            char first = text[i];
            if (first != '{' && first != '[')
            {
                return false;
            }

            int j = text.Length - 1;
            while (j > i && char.IsWhiteSpace(text[j]))
            {
                j--;
            }

            char last = text[j];
            return (first == '{' && last == '}') || (first == '[' && last == ']');
        }

        public static FormattedValue Format(string json, int indentWidth = 2)
        {
            var w = new TokenWriter();
            var p = new Parser(json, w, indentWidth);
            bool ok = p.Run();
            return w.ToFormatted("json", note: ok ? null : "Stopped at the first thing that is not valid JSON; the rest is shown raw.");
        }

        /// <summary>Counts keys and array elements for the details pane.</summary>
        public static void Measure(string json, out int objects, out int arrays, out int properties, out int maxDepth)
        {
            objects = arrays = properties = maxDepth = 0;
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            bool expectKey = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                        if (expectKey)
                        {
                            properties++;
                            expectKey = false;
                        }
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        objects++;
                        depth++;
                        expectKey = true;
                        if (depth > maxDepth) maxDepth = depth;
                        break;
                    case '[':
                        arrays++;
                        depth++;
                        expectKey = false;
                        if (depth > maxDepth) maxDepth = depth;
                        break;
                    case '}':
                    case ']':
                        depth--;
                        break;
                    case ',':
                        expectKey = true;
                        break;
                    case ':':
                        expectKey = false;
                        break;
                }
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private readonly TokenWriter _w;
            private readonly int _indent;
            private int _i;

            public Parser(string s, TokenWriter w, int indent)
            {
                _s = s ?? string.Empty;
                _w = w;
                _indent = indent;
            }

            public bool Run()
            {
                SkipWhitespace();
                bool ok = WriteValue(0);
                SkipWhitespace();

                if (_i < _s.Length)
                {
                    _w.NewLine();
                    _w.Write(TokenKind.Error, _s.Substring(_i));
                    return false;
                }

                return ok;
            }

            private void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }
            }

            private bool WriteValue(int depth)
            {
                if (_i >= _s.Length)
                {
                    return false;
                }

                switch (_s[_i])
                {
                    case '{': return WriteObject(depth);
                    case '[': return WriteArray(depth);
                    case '"': return WriteString(TokenKind.String);
                    case 't': return WriteLiteral("true", TokenKind.Keyword);
                    case 'f': return WriteLiteral("false", TokenKind.Keyword);
                    case 'n': return WriteLiteral("null", TokenKind.Null);
                    default: return WriteNumber();
                }
            }

            private bool WriteObject(int depth)
            {
                _w.Write(TokenKind.Punctuation, "{");
                _i++; // '{'
                SkipWhitespace();

                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    _w.Write(TokenKind.Punctuation, "}");
                    return true;
                }

                bool first = true;
                while (_i < _s.Length)
                {
                    if (!first)
                    {
                        _w.Write(TokenKind.Punctuation, ",");
                    }

                    first = false;
                    _w.NewLine();
                    _w.Indent(depth + 1, _indent);

                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != '"')
                    {
                        return Fail();
                    }

                    if (!WriteString(TokenKind.PropertyName))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':')
                    {
                        return Fail();
                    }

                    _i++;
                    _w.Write(TokenKind.Punctuation, ":");
                    _w.Write(" ");

                    SkipWhitespace();
                    if (!WriteValue(depth + 1))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (_i < _s.Length && _s[_i] == '}')
                    {
                        _i++;
                        _w.NewLine();
                        _w.Indent(depth, _indent);
                        _w.Write(TokenKind.Punctuation, "}");
                        return true;
                    }

                    return Fail();
                }

                return Fail();
            }

            private bool WriteArray(int depth)
            {
                _w.Write(TokenKind.Punctuation, "[");
                _i++; // '['
                SkipWhitespace();

                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    _w.Write(TokenKind.Punctuation, "]");
                    return true;
                }

                // Arrays of scalars stay on one line when short; that keeps things like
                // [1, 2, 3] readable instead of exploding into a column of numbers.
                if (TryWriteInlineScalarArray())
                {
                    return true;
                }

                bool first = true;
                while (_i < _s.Length)
                {
                    if (!first)
                    {
                        _w.Write(TokenKind.Punctuation, ",");
                    }

                    first = false;
                    _w.NewLine();
                    _w.Indent(depth + 1, _indent);

                    SkipWhitespace();
                    if (!WriteValue(depth + 1))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (_i < _s.Length && _s[_i] == ']')
                    {
                        _i++;
                        _w.NewLine();
                        _w.Indent(depth, _indent);
                        _w.Write(TokenKind.Punctuation, "]");
                        return true;
                    }

                    return Fail();
                }

                return Fail();
            }

            /// <summary>
            /// Looks ahead for a short array holding only scalars. Restores position and
            /// returns false if it does not qualify, so the caller can lay it out normally.
            /// </summary>
            private bool TryWriteInlineScalarArray()
            {
                const int MaxInlineChars = 72;

                int start = _i;
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                int end = -1;

                for (int k = _i; k < _s.Length; k++)
                {
                    char c = _s[k];

                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') inString = false;
                        continue;
                    }

                    if (c == '"') { inString = true; continue; }
                    if (c == '{' || c == '[') { depth++; continue; }
                    if (c == '}') { depth = -1; break; }   // malformed here; lay it out normally
                    if (c == ']')
                    {
                        if (depth == 0) { end = k; break; }
                        depth--;
                        continue;
                    }
                }

                if (end < 0 || depth != 0)
                {
                    _i = start;
                    return false;
                }

                string body = _s.Substring(start, end - start);
                if (body.Length > MaxInlineChars || body.IndexOf('{') >= 0 || body.IndexOf('[') >= 0 ||
                    body.IndexOf('\n') >= 0 || body.IndexOf('\r') >= 0)
                {
                    _i = start;
                    return false;
                }

                bool first = true;
                while (_i < end)
                {
                    SkipWhitespace();
                    if (_i >= end)
                    {
                        break;
                    }

                    if (!first)
                    {
                        _w.Write(TokenKind.Punctuation, ",");
                        _w.Write(" ");
                    }

                    first = false;

                    if (!WriteValue(0))
                    {
                        _i = start;
                        return false;
                    }

                    SkipWhitespace();
                    if (_i < end && _s[_i] == ',')
                    {
                        _i++;
                    }
                }

                _i = end + 1;
                _w.Write(TokenKind.Punctuation, "]");
                return true;
            }

            private bool WriteString(TokenKind kind)
            {
                int start = _i;
                _i++; // opening quote
                bool escaped = false;

                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        _i++;
                        _w.Write(kind, _s.Substring(start, _i - start));
                        return true;
                    }

                    _i++;
                }

                _w.Write(TokenKind.Error, _s.Substring(start));
                return false;
            }

            private bool WriteLiteral(string literal, TokenKind kind)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) == 0)
                {
                    _i += literal.Length;
                    _w.Write(kind, literal);
                    return true;
                }

                return Fail();
            }

            private bool WriteNumber()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+'))
                {
                    _i++;
                }

                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' ||
                                          ((_s[_i] == '-' || _s[_i] == '+') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))))
                {
                    _i++;
                }

                if (_i == start)
                {
                    return Fail();
                }

                string text = _s.Substring(start, _i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    _w.Write(TokenKind.Error, text);
                    return false;
                }

                _w.Write(TokenKind.Number, text);
                return true;
            }

            private bool Fail()
            {
                if (_i < _s.Length)
                {
                    _w.Write(TokenKind.Error, _s.Substring(_i));
                    _i = _s.Length;
                }

                return false;
            }
        }
    }
}
