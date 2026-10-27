using System;
using System.Text;

namespace SqlResultToolkit.Format
{
    internal static class HexDump
    {
        public const int DefaultLimit = 1024 * 1024;   // 1 MiB of bytes == 64K dump lines

        /// <summary>
        /// Classic 16-bytes-per-line dump: offset, hex bytes in two groups of eight, then
        /// the printable rendering. Alternating byte columns get their own token kind so the
        /// panel can shade them and keep long rows readable.
        /// </summary>
        public static FormattedValue Format(byte[] bytes, int limit = DefaultLimit)
        {
            if (bytes == null)
            {
                return FormattedValue.PlainText(string.Empty, "hex");
            }

            int count = Math.Min(bytes.Length, limit);
            bool truncated = count < bytes.Length;

            var w = new TokenWriter();
            var ascii = new StringBuilder(16);

            for (int offset = 0; offset < count; offset += 16)
            {
                if (offset > 0)
                {
                    w.NewLine();
                }

                w.Write(TokenKind.Offset, offset.ToString("x8"));
                w.Write(TokenKind.Dim, "  ");

                ascii.Clear();
                for (int i = 0; i < 16; i++)
                {
                    int index = offset + i;

                    if (i == 8)
                    {
                        w.Write(TokenKind.Dim, " ");
                    }

                    if (index < count)
                    {
                        byte b = bytes[index];
                        w.Write((i % 2 == 0) ? TokenKind.HexByte : TokenKind.HexByteAlt, b.ToString("x2"));
                        w.Write(TokenKind.Dim, " ");
                        ascii.Append(b >= 0x20 && b <= 0x7e ? (char)b : '.');
                    }
                    else
                    {
                        w.Write(TokenKind.Dim, "   ");
                        ascii.Append(' ');
                    }
                }

                w.Write(TokenKind.Dim, " |");
                w.Write(TokenKind.Ascii, ascii.ToString());
                w.Write(TokenKind.Dim, "|");
            }

            if (truncated)
            {
                w.NewLine();
                w.NewLine();
                w.Write(TokenKind.Error, $"... {bytes.Length - count:N0} more bytes not shown (dump capped at {limit:N0} bytes)");
            }

            return w.ToFormatted(
                "hex",
                truncated,
                truncated ? $"Showing the first {count:N0} of {bytes.Length:N0} bytes." : null);
        }

        /// <summary>T-SQL literal form, e.g. 0x48656C6C6F. Capped so the panel stays usable.</summary>
        public static string ToSqlLiteral(byte[] bytes, int limit = 4096)
        {
            if (bytes == null)
            {
                return "NULL";
            }

            int count = Math.Min(bytes.Length, limit);
            var sb = new StringBuilder(2 + count * 2 + 24);
            sb.Append("0x");
            for (int i = 0; i < count; i++)
            {
                sb.Append(bytes[i].ToString("X2"));
            }

            if (count < bytes.Length)
            {
                sb.Append("... (+").Append((bytes.Length - count).ToString("N0")).Append(" bytes)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Identifies common payloads from their leading bytes, so a varbinary column tells
        /// you it is holding a PNG rather than just showing you 89 50 4e 47.
        /// </summary>
        public static string DetectContentType(byte[] b)
        {
            if (b == null || b.Length < 4)
            {
                return null;
            }

            if (Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
            if (Starts(b, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
            if (Starts(b, 0x47, 0x49, 0x46, 0x38)) return "image/gif";
            if (Starts(b, 0x42, 0x4D)) return "image/bmp";
            if (Starts(b, 0x00, 0x00, 0x01, 0x00)) return "image/x-icon";
            if (b.Length > 11 && Starts(b, 0x52, 0x49, 0x46, 0x46) && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
            if (Starts(b, 0x25, 0x50, 0x44, 0x46)) return "application/pdf";
            if (Starts(b, 0x50, 0x4B, 0x03, 0x04)) return "application/zip (or docx/xlsx/pptx)";
            if (Starts(b, 0x1F, 0x8B)) return "application/gzip";
            if (Starts(b, 0xD0, 0xCF, 0x11, 0xE0)) return "application/x-ole-storage (legacy Office)";
            if (Starts(b, 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65)) return "application/vnd.sqlite3";
            if (Starts(b, 0x4D, 0x5A)) return "application/x-msdownload (PE)";
            if (Starts(b, 0x7F, 0x45, 0x4C, 0x46)) return "application/x-elf";
            if (Starts(b, 0xEF, 0xBB, 0xBF)) return "text/plain; charset=utf-8 (BOM)";
            if (Starts(b, 0xFF, 0xFE)) return "text/plain; charset=utf-16le (BOM)";
            if (Starts(b, 0xFE, 0xFF)) return "text/plain; charset=utf-16be (BOM)";

            return null;
        }

        public static bool IsImage(string contentType) =>
            contentType != null && contentType.StartsWith("image/", StringComparison.Ordinal);

        private static bool Starts(byte[] b, params byte[] prefix)
        {
            if (b.Length < prefix.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                if (b[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
