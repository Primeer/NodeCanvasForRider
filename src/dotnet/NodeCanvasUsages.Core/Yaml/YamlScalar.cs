using System;
using System.Collections.Generic;
using System.Text;

namespace NodeCanvasUsages.Core.Yaml;

public enum ScalarStyle
{
    Empty,
    Plain,
    SingleQuoted,
    DoubleQuoted,
}

/// <summary>
/// A <c>key: value</c> scalar located in a list of lines. <see cref="Map"/> maps every character of the decoded
/// <see cref="Value"/> to its offset in the original text (for navigation); for double-quoted and plain scalars it
/// is approximate.
/// </summary>
public sealed class ScalarSpan
{
    public ScalarSpan(string key, int keyIndent, int startLine, int endLine, string value, ScalarStyle style, int[] map)
    {
        Key = key;
        KeyIndent = keyIndent;
        StartLine = startLine;
        EndLine = endLine;
        Value = value;
        Style = style;
        Map = map;
    }

    public string Key { get; }
    public int KeyIndent { get; }
    public int StartLine { get; }

    /// <summary>Exclusive.</summary>
    public int EndLine { get; }

    public string Value { get; }
    public ScalarStyle Style { get; }
    public int[] Map { get; }
}

/// <summary>
/// Codec for the single YAML scalar Unity writes for a serialized graph. Unity emits long strings as single-quoted
/// flow scalars folded the way libyaml does (<c>yaml_emitter_write_single_quoted</c>): when the column reaches
/// <see cref="BestWidth"/> a single space is replaced by a line break plus indentation.
/// </summary>
public static class YamlScalar
{
    public const int BestWidth = 80;

    /// <param name="lineOffsets">Offset of every line in the original text (may be null; then offsets are line-relative).</param>
    public static ScalarSpan? Find(IReadOnlyList<string> lines, string key, int start = 0, int? stop = null,
                                   IReadOnlyList<int>? lineOffsets = null)
    {
        var end = stop ?? lines.Count;
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            var indent = CountLeadingSpaces(line);
            if (string.CompareOrdinal(line, indent, key, 0, key.Length) != 0 || line.Length <= indent + key.Length ||
                line[indent + key.Length] != ':')
                continue;

            var restStart = indent + key.Length + 1;
            if (restStart < line.Length && line[restStart] != ' ')
                continue;
            if (restStart < line.Length)
                restStart++;

            var baseOffset = lineOffsets?[i] ?? 0;
            if (restStart >= line.Length)
                return new ScalarSpan(key, indent, i, i + 1, "", ScalarStyle.Empty, Array.Empty<int>());
            if (line[restStart] == '\'')
                return ReadSingle(lines, key, indent, i, restStart + 1, lineOffsets);
            if (line[restStart] == '"')
                return ReadDouble(lines, key, indent, i, restStart + 1, lineOffsets);

            var j = i + 1;
            var cont = new string(' ', indent + 1);
            while (j < end && lines[j].StartsWith(cont, StringComparison.Ordinal) &&
                   !lines[j].TrimStart(' ').StartsWith("- ", StringComparison.Ordinal))
                j++;
            var raw = new List<string> { line.Substring(restStart) };
            for (var k = i + 1; k < j; k++)
                raw.Add(lines[k].Trim());
            var plain = Fold(raw);
            return new ScalarSpan(key, indent, i, j, plain, ScalarStyle.Plain, Uniform(plain.Length, baseOffset + restStart));
        }

        return null;
    }

    private static int CountLeadingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ')
            n++;
        return n;
    }

    private static int[] Uniform(int length, int offset)
    {
        var map = new int[length];
        for (var k = 0; k < length; k++)
            map[k] = offset;
        return map;
    }

    private static ScalarSpan ReadSingle(IReadOnlyList<string> lines, string key, int indent, int i, int bodyStart,
                                         IReadOnlyList<int>? lineOffsets)
    {
        var sb = new StringBuilder();
        var map = new List<int>();
        var pendingBreaks = 0;
        var first = true;
        var j = i;
        while (true)
        {
            var line = lines[j];
            var from = j == i ? bodyStart : CountLeadingSpaces(line);
            var lineOffset = lineOffsets?[j] ?? 0;
            var close = ClosingQuote(line, from);
            var to = close ?? TrimEndSpaces(line, from);

            if (first)
            {
                first = false;
            }
            else if (to == from && close == null)
            {
                // an empty continuation line is a line break; flushed before the next text (or the closing quote)
                pendingBreaks++;
                j = NextLine(lines, key, j);
                continue;
            }
            else if (pendingBreaks > 0)
            {
                for (var b = 0; b < pendingBreaks; b++)
                {
                    sb.Append('\n');
                    map.Add(lineOffset - 1);
                }

                pendingBreaks = 0;
            }
            else
            {
                sb.Append(' ');
                map.Add(lineOffset - 1);
            }

            for (var k = from; k < to; k++)
            {
                sb.Append(line[k]);
                map.Add(lineOffset + k);
                if (line[k] == '\'')
                    k++; // '' -> '
            }

            if (close != null)
                break;
            j = NextLine(lines, key, j);
        }

        return new ScalarSpan(key, indent, i, j + 1, sb.ToString(), ScalarStyle.SingleQuoted, map.ToArray());
    }

    /// <summary>End of <paramref name="line"/> without trailing blanks, which YAML drops before a line fold.</summary>
    private static int TrimEndSpaces(string line, int from)
    {
        var to = line.Length;
        while (to > from && (line[to - 1] == ' ' || line[to - 1] == '\t'))
            to--;
        return to;
    }

    private static int NextLine(IReadOnlyList<string> lines, string key, int j)
    {
        if (j + 1 >= lines.Count)
            throw new FormatException($"unterminated single-quoted scalar for '{key}'");
        return j + 1;
    }

    private static int? ClosingQuote(string text, int from)
    {
        for (var k = from; k < text.Length; k++)
        {
            if (text[k] != '\'')
                continue;
            if (k + 1 < text.Length && text[k + 1] == '\'')
            {
                k++;
                continue;
            }

            return k;
        }

        return null;
    }

    private static ScalarSpan ReadDouble(IReadOnlyList<string> lines, string key, int indent, int i, int bodyStart,
                                         IReadOnlyList<int>? lineOffsets)
    {
        var raw = new List<string>();
        var j = i;
        while (true)
        {
            var line = lines[j];
            var text = j == i ? line.Substring(bodyStart) : line.TrimStart(' ');
            var end = ClosingDouble(text);
            if (end != null)
            {
                raw.Add(text.Substring(0, end.Value));
                break;
            }

            raw.Add(text);
            j++;
            if (j >= lines.Count)
                throw new FormatException($"unterminated double-quoted scalar for '{key}'");
        }

        // YAML flow folding: a single line break is a space, n empty lines are n breaks, and an escaped line end
        // ("\" before it) is dropped (empty lines after it still count); blanks around a fold are not content
        var joined = new StringBuilder();
        var pendingBreaks = 0;
        var escapedBreak = false;
        for (var k = 0; k < raw.Count; k++)
        {
            var part = raw[k];
            var last = k == raw.Count - 1;
            if (!last)
                part = TrimEndUnescaped(part);
            if (k == 0)
            {
                joined.Append(part);
            }
            else if (part.Length == 0 && !last)
            {
                pendingBreaks++;
                continue;
            }
            else
            {
                if (pendingBreaks > 0)
                    joined.Append('\n', pendingBreaks);
                else if (!escapedBreak)
                    joined.Append(' ');
                joined.Append(part);
            }

            pendingBreaks = 0;
            escapedBreak = !last && EndsWithEscape(joined);
            if (escapedBreak)
                joined.Length--;
        }

        var value = Unescape(joined.ToString());
        var offset = (lineOffsets?[i] ?? 0) + bodyStart;
        return new ScalarSpan(key, indent, i, j + 1, value, ScalarStyle.DoubleQuoted, Uniform(value.Length, offset));
    }

    /// <summary>An odd number of trailing backslashes: the last one escapes the line break.</summary>
    private static bool EndsWithEscape(StringBuilder sb)
    {
        var n = 0;
        while (n < sb.Length && sb[sb.Length - 1 - n] == '\\')
            n++;
        return n % 2 == 1;
    }

    /// <summary>Drops trailing blanks unless the last one is escaped (<c>\ </c>, <c>\t</c> as a raw tab).</summary>
    private static string TrimEndUnescaped(string s)
    {
        var to = s.Length;
        while (to > 0 && (s[to - 1] == ' ' || s[to - 1] == '\t'))
        {
            var backslashes = 0;
            while (to - 2 - backslashes >= 0 && s[to - 2 - backslashes] == '\\')
                backslashes++;
            if (backslashes % 2 == 1)
                break;
            to--;
        }

        return s.Substring(0, to);
    }

    private static int? ClosingDouble(string text)
    {
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '\\')
            {
                k++;
                continue;
            }

            if (text[k] == '"')
                return k;
        }

        return null;
    }

    /// <summary>YAML 1.1 double-quoted escapes; a malformed escape is a <see cref="FormatException"/>.</summary>
    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var k = 0; k < s.Length; k++)
        {
            var ch = s[k];
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (k + 1 >= s.Length)
                throw new FormatException("dangling escape in double-quoted scalar");
            var next = s[k + 1];
            switch (next)
            {
                case 'x':
                    sb.Append((char)Hex(s, k + 2, 2));
                    k += 3;
                    continue;
                case 'u':
                    sb.Append((char)Hex(s, k + 2, 4));
                    k += 5;
                    continue;
                case 'U':
                    var code = Hex(s, k + 2, 8);
                    if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
                        throw new FormatException($"bad escape \\U{s.Substring(k + 2, 8)}");
                    sb.Append(char.ConvertFromUtf32(code));
                    k += 9;
                    continue;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't':
                case '\t': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\u001b'); break;
                case ' ': sb.Append(' '); break;
                case '"': sb.Append('"'); break;
                case '/': sb.Append('/'); break;
                case '\\': sb.Append('\\'); break;
                case 'N': sb.Append('\u0085'); break;
                case '_': sb.Append('\u00a0'); break;
                case 'L': sb.Append('\u2028'); break;
                case 'P': sb.Append('\u2029'); break;
                default: throw new FormatException($"bad escape \\{next} in double-quoted scalar");
            }

            k++;
        }

        return sb.ToString();
    }

    private static int Hex(string s, int start, int length)
    {
        if (start + length > s.Length)
            throw new FormatException("truncated escape in double-quoted scalar");
        var value = 0;
        for (var k = start; k < start + length; k++)
        {
            var digit = s[k] switch
            {
                >= '0' and <= '9' => s[k] - '0',
                >= 'a' and <= 'f' => s[k] - 'a' + 10,
                >= 'A' and <= 'F' => s[k] - 'A' + 10,
                _ => throw new FormatException($"bad hex digit '{s[k]}' in double-quoted scalar"),
            };
            value = value * 16 + digit;
        }

        return value;
    }

    private static string Fold(List<string> raw)
    {
        var sb = new StringBuilder(raw[0]);
        var pendingBreaks = 0;
        for (var k = 1; k < raw.Count; k++)
        {
            if (raw[k].Length == 0)
            {
                pendingBreaks++;
                continue;
            }

            if (pendingBreaks > 0)
                sb.Append('\n', pendingBreaks);
            else
                sb.Append(' ');
            pendingBreaks = 0;
            sb.Append(raw[k]);
        }

        sb.Append('\n', pendingBreaks);
        return sb.ToString();
    }

    private static string KeyPrefix(int indent, string key) => new string(' ', indent) + key + ":";

    /// <summary>Unity-style single-quoted folding when it decodes back exactly, else a double-quoted line.</summary>
    public static List<string> Encode(string key, int keyIndent, string value)
    {
        var lines = EncodeSingle(key, keyIndent, value);
        var span = Find(lines, key);
        if (span != null && span.Value == value && span.EndLine == lines.Count)
            return lines;
        return EncodeDouble(key, keyIndent, value);
    }

    /// <summary>Single-line double-quoted scalar; exact for any string.</summary>
    public static List<string> EncodeDouble(string key, int keyIndent, string value)
    {
        var sb = new StringBuilder(KeyPrefix(keyIndent, key)).Append(" \"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\x").Append(((int)ch).ToString("x2"));
                    else
                        sb.Append(ch);
                    break;
            }
        }

        sb.Append('"');
        return new List<string> { sb.ToString() };
    }

    /// <summary>Emit <c>key: 'value'</c> folded like Unity/libyaml (<c>yaml_emitter_write_single_quoted</c>).</summary>
    public static List<string> EncodeSingle(string key, int keyIndent, string value)
    {
        var indent = keyIndent + 2;
        var lines = new List<string>();
        var cur = new StringBuilder(KeyPrefix(keyIndent, key)).Append(" '");
        var column = cur.Length;
        var breaks = false;
        var n = value.Length;
        for (var k = 0; k < n; k++)
        {
            var ch = value[k];
            if (ch == ' ')
            {
                if (column >= BestWidth && k != 0 && k != n - 1 && value[k + 1] != ' ' && value[k - 1] != ' ')
                {
                    lines.Add(cur.ToString());
                    cur.Clear().Append(' ', indent);
                    column = indent;
                }
                else
                {
                    cur.Append(ch);
                    column++;
                }
            }
            else if (ch == '\n')
            {
                if (!breaks)
                {
                    lines.Add(cur.ToString());
                    cur.Clear();
                }

                lines.Add(cur.ToString());
                cur.Clear();
                column = 0;
                breaks = true;
            }
            else
            {
                if (breaks)
                {
                    cur.Clear().Append(' ', indent);
                    column = indent;
                }

                if (ch == '\'')
                {
                    cur.Append("''");
                    column += 2;
                }
                else
                {
                    cur.Append(ch);
                    column++;
                }

                breaks = false;
            }
        }

        if (breaks)
            cur.Clear().Append(' ', indent);
        cur.Append('\'');
        lines.Add(cur.ToString());
        return lines;
    }
}
