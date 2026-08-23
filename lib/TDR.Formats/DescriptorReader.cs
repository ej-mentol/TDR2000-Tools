using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace TDR.PakLib.Formats
{
    /// <summary>
    /// Reusable text tokenizer and reader for TDR2000 structured descriptor files.
    /// Handles comments (// and #), quote preservation, and invariant numeric parsing.
    /// </summary>
    public static class DescriptorReader
    {
        public static string StripComments(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;

            int slashIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (slashIdx >= 0) line = line[..slashIdx];

            int hashIdx = line.IndexOf('#', StringComparison.Ordinal);
            if (hashIdx >= 0) line = line[..hashIdx];

            return line.Trim();
        }

        public static List<string> GetCleanLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string clean = StripComments(line);
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    result.Add(clean);
                }
            }

            return result;
        }

        public static List<string> TokenizeLine(string line)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return tokens;

            string clean = StripComments(line);
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < clean.Length; i++)
            {
                char c = clean[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && (char.IsWhiteSpace(c) || c == ',' || c == '|'))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        public static bool TryParseFloat(string token, out float value)
        {
            return float.TryParse(token.Trim('\"', '\''), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseInt(string token, out int value)
        {
            return int.TryParse(token.Trim('\"', '\''), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseVector3(IList<string> tokens, int startIndex, out Vector3 value)
        {
            value = Vector3.Zero;
            if (tokens == null || startIndex + 2 >= tokens.Count) return false;

            if (TryParseFloat(tokens[startIndex], out float x) &&
                TryParseFloat(tokens[startIndex + 1], out float y) &&
                TryParseFloat(tokens[startIndex + 2], out float z))
            {
                value = new Vector3(x, y, z);
                return true;
            }

            return false;
        }
    }
}
