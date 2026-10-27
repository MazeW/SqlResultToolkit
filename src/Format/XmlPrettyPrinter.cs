using System;
using System.IO;
using System.Text;
using System.Xml;

namespace SqlCellInspector.Format
{
    /// <summary>
    /// Re-indents XML and emits colour tokens. Uses <see cref="XmlReader"/> for parsing so
    /// namespaces, CDATA, entities and processing instructions behave correctly, but writes
    /// the output by hand so each piece can carry its own token kind.
    /// </summary>
    internal static class XmlPrettyPrinter
    {
        public static bool LooksLikeXml(string text)
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

            if (i >= text.Length || text[i] != '<')
            {
                return false;
            }

            int j = text.Length - 1;
            while (j > i && char.IsWhiteSpace(text[j]))
            {
                j--;
            }

            return text[j] == '>';
        }

        public static FormattedValue Format(string xml, int indentWidth = 2)
        {
            var w = new TokenWriter();

            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,   // FOR XML returns fragments
                IgnoreWhitespace = true,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                DtdProcessing = DtdProcessing.Prohibit,         // no external entity resolution
                XmlResolver = null,
                CheckCharacters = false
            };

            try
            {
                using (var stringReader = new StringReader(xml))
                using (var reader = XmlReader.Create(stringReader, settings))
                {
                    int depth = 0;
                    bool lastWasText = false;
                    bool atStart = true;

                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.XmlDeclaration:
                                WriteNewLineIfNeeded(w, ref atStart);
                                w.Write(TokenKind.Declaration, "<?" + reader.Name + " " + reader.Value + "?>");
                                break;

                            case XmlNodeType.ProcessingInstruction:
                                WriteNewLineIfNeeded(w, ref atStart);
                                w.Indent(depth, indentWidth);
                                w.Write(TokenKind.Declaration, "<?" + reader.Name + " " + reader.Value + "?>");
                                break;

                            case XmlNodeType.Comment:
                                WriteNewLineIfNeeded(w, ref atStart);
                                w.Indent(depth, indentWidth);
                                w.Write(TokenKind.Comment, "<!--" + reader.Value + "-->");
                                break;

                            case XmlNodeType.Element:
                                WriteNewLineIfNeeded(w, ref atStart);
                                w.Indent(depth, indentWidth);
                                w.Write(TokenKind.TagDelimiter, "<");
                                w.Write(TokenKind.TagName, reader.Name);
                                WriteAttributes(w, reader);

                                if (reader.IsEmptyElement)
                                {
                                    w.Write(TokenKind.TagDelimiter, " />");
                                }
                                else
                                {
                                    w.Write(TokenKind.TagDelimiter, ">");
                                    depth++;
                                }

                                lastWasText = false;
                                break;

                            case XmlNodeType.EndElement:
                                depth--;
                                if (!lastWasText)
                                {
                                    w.NewLine();
                                    w.Indent(depth, indentWidth);
                                }

                                w.Write(TokenKind.TagDelimiter, "</");
                                w.Write(TokenKind.TagName, reader.Name);
                                w.Write(TokenKind.TagDelimiter, ">");
                                lastWasText = false;
                                break;

                            case XmlNodeType.Text:
                            case XmlNodeType.SignificantWhitespace:
                                w.Write(TokenKind.Plain, Escape(reader.Value));
                                lastWasText = true;
                                break;

                            case XmlNodeType.CDATA:
                                w.Write(TokenKind.CData, "<![CDATA[" + reader.Value + "]]>");
                                lastWasText = true;
                                break;

                            case XmlNodeType.EntityReference:
                                w.Write(TokenKind.Keyword, "&" + reader.Name + ";");
                                lastWasText = true;
                                break;
                        }
                    }
                }

                return w.ToFormatted("xml");
            }
            catch (XmlException ex)
            {
                // Not well-formed. Show the raw value and say where the parser objected:
                // for a cell this often means SSMS only holds a prefix of the document.
                return FormattedValue.PlainText(
                    xml,
                    "xml",
                    $"Not well-formed XML (line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}). Showing the raw value.");
            }
            catch (Exception ex)
            {
                return FormattedValue.PlainText(xml, "xml", "Could not parse as XML: " + ex.Message);
            }
        }

        private static void WriteNewLineIfNeeded(TokenWriter w, ref bool atStart)
        {
            if (atStart)
            {
                atStart = false;
                return;
            }

            w.NewLine();
        }

        private static void WriteAttributes(TokenWriter w, XmlReader reader)
        {
            if (!reader.HasAttributes)
            {
                return;
            }

            for (int i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                w.Write(" ");
                w.Write(TokenKind.AttributeName, reader.Name);
                w.Write(TokenKind.Punctuation, "=");
                w.Write(TokenKind.AttributeValue, "\"" + Escape(reader.Value) + "\"");
            }

            reader.MoveToElement();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (value.IndexOfAny(new[] { '<', '>', '&' }) < 0)
            {
                return value;
            }

            var sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        public static void Measure(string xml, out int elements, out int attributes, out int maxDepth)
        {
            elements = attributes = maxDepth = 0;

            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CheckCharacters = false
            };

            try
            {
                using (var stringReader = new StringReader(xml))
                using (var reader = XmlReader.Create(stringReader, settings))
                {
                    int depth = 0;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            elements++;
                            attributes += reader.AttributeCount;
                            if (!reader.IsEmptyElement)
                            {
                                depth++;
                                if (depth > maxDepth) maxDepth = depth;
                            }
                            else if (depth + 1 > maxDepth)
                            {
                                maxDepth = depth + 1;
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            depth--;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Counts stay at whatever we managed; the caller treats 0 as "unknown".
            }
        }
    }
}
