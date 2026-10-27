using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SqlCellInspector.Grid;

namespace SqlCellInspector.Format
{
    internal enum ViewMode
    {
        Auto,
        Text,
        Json,
        Xml,
        Hex,
        Image,
        Base64
    }

    /// <summary>The fully prepared contents of the panel for one cell.</summary>
    internal sealed class RenderedCell
    {
        public CellSnapshot Cell { get; set; }

        public SqlTypeCategory Category { get; set; }

        public ViewMode EffectiveMode { get; set; }

        public FormattedValue Formatted { get; set; }

        public IReadOnlyList<Fact> Facts { get; set; }

        /// <summary>Raw bytes when the value is binary (or decodable to bytes), else null.</summary>
        public byte[] Bytes { get; set; }

        /// <summary>Sniffed content type for binary values, e.g. "image/png".</summary>
        public string ContentType { get; set; }

        /// <summary>The modes that make sense for this value, for the view picker.</summary>
        public IReadOnlyList<ViewMode> AvailableModes { get; set; }

        public string SuggestedExtension { get; set; }
    }

    internal static class CellRenderer
    {
        /// <summary>
        /// Above this many characters the panel stops tokenising and shows plain monospace
        /// text. Colouring a multi-megabyte document costs more than it is worth, and WPF
        /// gets unhappy about hundreds of thousands of Runs.
        /// </summary>
        public const int ColourizeLimit = 512 * 1024;

        public static RenderedCell Render(CellSnapshot cell, ViewMode requested)
        {
            var category = SqlTypeCatalog.Classify(cell.ServerTypeName, cell.FieldType);
            var bytes = ExtractBytes(cell);
            string text = ExtractText(cell, category);
            string contentType = bytes != null ? HexDump.DetectContentType(bytes) : null;

            var result = new RenderedCell
            {
                Cell = cell,
                Category = category,
                Bytes = bytes,
                ContentType = contentType
            };

            var modes = new List<ViewMode> { ViewMode.Auto };

            if (text != null)
            {
                modes.Add(ViewMode.Text);
                if (category == SqlTypeCategory.Json || JsonPrettyPrinter.LooksLikeJson(text))
                {
                    modes.Add(ViewMode.Json);
                }

                if (category == SqlTypeCategory.Xml || XmlPrettyPrinter.LooksLikeXml(text))
                {
                    modes.Add(ViewMode.Xml);
                }
            }

            if (bytes != null && !modes.Contains(ViewMode.Text))
            {
                modes.Add(ViewMode.Text);
            }

            // Hex and Base64 apply to anything we can get bytes for, and for a character
            // column that means its UTF-8 encoding. Being able to drop a text value into
            // the hex view is how you find the stray BOM, NBSP or trailing CR.
            if (bytes != null || text != null)
            {
                modes.Add(ViewMode.Hex);
                modes.Add(ViewMode.Base64);
            }

            if (HexDump.IsImage(contentType))
            {
                modes.Add(ViewMode.Image);
            }

            result.AvailableModes = modes;

            var mode = requested;
            if (mode != ViewMode.Auto && !modes.Contains(mode))
            {
                mode = ViewMode.Auto;
            }

            if (mode == ViewMode.Auto)
            {
                mode = PickAutoMode(category, text, bytes, contentType);
            }

            result.EffectiveMode = mode;
            result.Formatted = Build(mode, cell, category, text, bytes);
            result.Facts = ValueDescriber.Describe(cell, category, bytes, text);
            result.SuggestedExtension = SqlTypeCatalog.SuggestExtension(category, contentType);

            return result;
        }

        private static ViewMode PickAutoMode(SqlTypeCategory category, string text, byte[] bytes, string contentType)
        {
            if (HexDump.IsImage(contentType))
            {
                return ViewMode.Image;
            }

            switch (category)
            {
                case SqlTypeCategory.Json:
                    return ViewMode.Json;
                case SqlTypeCategory.Xml:
                    return ViewMode.Xml;
                case SqlTypeCategory.Binary:
                case SqlTypeCategory.RowVersion:
                    return ViewMode.Hex;
            }

            if (text != null)
            {
                if (JsonPrettyPrinter.LooksLikeJson(text))
                {
                    return ViewMode.Json;
                }

                if (XmlPrettyPrinter.LooksLikeXml(text))
                {
                    return ViewMode.Xml;
                }
            }

            return bytes != null && text == null ? ViewMode.Hex : ViewMode.Text;
        }

        private static FormattedValue Build(ViewMode mode, CellSnapshot cell, SqlTypeCategory category, string text, byte[] bytes)
        {
            if (cell.IsNull)
            {
                var w = new TokenWriter();
                w.Write(TokenKind.Null, "NULL");
                return w.ToFormatted("text", note: "The column holds SQL NULL, not an empty string.");
            }

            if (cell.StorageColumn < 0)
            {
                return FormattedValue.PlainText(
                    $"Row {cell.Row + 1:N0} selected.",
                    "text",
                    "That is the row-number gutter. Click a data cell to inspect its value.");
            }

            switch (mode)
            {
                case ViewMode.Json:
                    return text == null
                        ? FormattedValue.PlainText(string.Empty, "json", "No string form of this value is available.")
                        : (text.Length > ColourizeLimit
                            ? FormattedValue.PlainText(text, "json", LargeValueNote(text.Length))
                            : JsonPrettyPrinter.Format(text));

                case ViewMode.Xml:
                    return text == null
                        ? FormattedValue.PlainText(string.Empty, "xml", "No string form of this value is available.")
                        : (text.Length > ColourizeLimit
                            ? FormattedValue.PlainText(text, "xml", LargeValueNote(text.Length))
                            : XmlPrettyPrinter.Format(text));

                case ViewMode.Hex:
                    return bytes != null
                        ? HexDump.Format(bytes)
                        : HexDump.Format(Encoding.UTF8.GetBytes(text ?? string.Empty));

                case ViewMode.Base64:
                    byte[] source = bytes ?? Encoding.UTF8.GetBytes(text ?? string.Empty);
                    return FormattedValue.PlainText(
                        Convert.ToBase64String(source, Base64FormattingOptions.InsertLineBreaks),
                        "base64",
                        bytes == null ? "Base64 of the UTF-8 encoding of the text." : null);

                case ViewMode.Image:
                    return FormattedValue.PlainText(string.Empty, "image");

                default:
                    if (text != null)
                    {
                        return FormattedValue.PlainText(text, "text",
                            text.Length > ColourizeLimit ? LargeValueNote(text.Length) : null);
                    }

                    if (bytes != null)
                    {
                        return HexDump.Format(bytes);
                    }

                    return FormattedValue.PlainText(
                        DescribeOpaqueValue(cell, category),
                        "text",
                        "No string or binary form of this value is available; see Details.");
            }
        }

        private static string LargeValueNote(int length) =>
            $"{length:N0} characters — shown as plain text because syntax colouring is capped at {ColourizeLimit:N0}.";

        private static string DescribeOpaqueValue(CellSnapshot cell, SqlTypeCategory category)
        {
            if (cell.Value == null)
            {
                return "(value unavailable)";
            }

            try
            {
                return Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? cell.Value.GetType().FullName;
            }
            catch (Exception)
            {
                return cell.Value.GetType().FullName;
            }
        }

        /// <summary>
        /// Gets the bytes behind the value. Covers byte[] directly, SqlBinary/SqlBytes and
        /// the UDT types (geography, geometry, hierarchyid) which all expose their
        /// serialised form one way or another.
        /// </summary>
        private static byte[] ExtractBytes(CellSnapshot cell)
        {
            var value = cell.Value;
            if (value == null)
            {
                return null;
            }

            if (value is byte[] direct)
            {
                return direct;
            }

            // SqlBinary / SqlBytes / SqlGeography&co: Value, Buffer or ToArray().
            foreach (var name in new[] { "Value", "Buffer" })
            {
                try
                {
                    var property = value.GetType().GetProperty(name);
                    if (property?.GetValue(value) is byte[] fromProperty)
                    {
                        return fromProperty;
                    }
                }
                catch (Exception)
                {
                    // keep trying
                }
            }

            try
            {
                var toArray = value.GetType().GetMethod("ToArray", Type.EmptyTypes);
                if (toArray?.Invoke(value, null) is byte[] fromMethod)
                {
                    return fromMethod;
                }
            }
            catch (Exception)
            {
                // not byte-backed
            }

            // IBinarySerialize / INullable UDTs: Serialize into a BinaryWriter.
            if (cell.FieldType != null && value.GetType().GetInterface("Microsoft.SqlServer.Server.IBinarySerialize") != null)
            {
                try
                {
                    using (var stream = new System.IO.MemoryStream())
                    using (var writer = new System.IO.BinaryWriter(stream))
                    {
                        var serialize = value.GetType().GetMethod("Write", new[] { typeof(System.IO.BinaryWriter) });
                        if (serialize != null)
                        {
                            serialize.Invoke(value, new object[] { writer });
                            writer.Flush();
                            return stream.ToArray();
                        }
                    }
                }
                catch (Exception)
                {
                    // optional
                }
            }

            return null;
        }

        private static string ExtractText(CellSnapshot cell, SqlTypeCategory category)
        {
            if (cell.Value is string s)
            {
                return s;
            }

            // SqlString / SqlXml / SqlChars.
            if (cell.Value != null && !(cell.Value is byte[]))
            {
                foreach (var name in new[] { "Value" })
                {
                    try
                    {
                        var property = cell.Value.GetType().GetProperty(name);
                        if (property?.GetValue(cell.Value) is string fromProperty)
                        {
                            return fromProperty;
                        }
                    }
                    catch (Exception)
                    {
                        // keep trying
                    }
                }
            }

            // For the binary categories the grid's string form is just "0x..."; the hex
            // view is strictly better, so do not offer it as text.
            if (category == SqlTypeCategory.Binary || category == SqlTypeCategory.RowVersion)
            {
                return null;
            }

            return cell.Text;
        }

        public static string ModeLabel(ViewMode mode)
        {
            switch (mode)
            {
                case ViewMode.Auto: return "Auto";
                case ViewMode.Text: return "Text";
                case ViewMode.Json: return "JSON";
                case ViewMode.Xml: return "XML";
                case ViewMode.Hex: return "Hex";
                case ViewMode.Image: return "Image";
                case ViewMode.Base64: return "Base64";
                default: return mode.ToString();
            }
        }
    }
}
