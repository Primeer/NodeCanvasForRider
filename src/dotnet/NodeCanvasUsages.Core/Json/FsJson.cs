using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NodeCanvasUsages.Core.Json;

public abstract class JsonNode
{
    /// <summary>Range of the node in the source text, <c>[Start, End)</c>.</summary>
    public int Start { get; internal set; }

    public int End { get; internal set; }
}

public sealed class JsonObject : JsonNode
{
    public List<KeyValuePair<string, JsonNode>> Members { get; } = new();

    public JsonNode? this[string key]
    {
        get
        {
            foreach (var m in Members)
            {
                if (m.Key == key)
                    return m.Value;
            }

            return null;
        }
    }

    public string? GetString(string key) => (this[key] as JsonString)?.Value;
}

public sealed class JsonArray : JsonNode
{
    public List<JsonNode> Items { get; } = new();
}

public sealed class JsonString : JsonNode
{
    public JsonString(string value) => Value = value;
    public string Value { get; }
}

/// <summary>Number, <c>true</c>, <c>false</c> or <c>null</c>, kept as written.</summary>
public sealed class JsonLiteral : JsonNode
{
    public JsonLiteral(string raw) => Raw = raw;
    public string Raw { get; }
}

/// <summary>
/// JSON reader for FullSerializer output that keeps source ranges, so that a rewrite can replace individual string
/// literals and leave every other byte (number formatting, key order) as it was.
/// </summary>
public static class FsJson
{
    public static JsonNode Parse(string text)
    {
        var parser = new Parser(text);
        parser.SkipWhitespace();
        var node = parser.ParseValue();
        parser.SkipWhitespace();
        if (parser.Position != text.Length)
            throw new FormatException($"unexpected trailing data at {parser.Position}");
        return node;
    }

    /// <summary>Quotes and escapes a string the way FullSerializer does.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\a': sb.Append("\\a"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Applies <paramref name="rewrite"/> to every string value (not keys) and returns the new text, or
    /// <c>null</c> when nothing changed. Unchanged parts of <paramref name="text"/> are copied verbatim.
    /// </summary>
    public static string? RewriteStrings(string text, Func<string, string> rewrite)
    {
        var root = Parse(text);
        var edits = new List<(int Start, int End, string Text)>();
        Collect(root, rewrite, edits);
        if (edits.Count == 0)
            return null;

        var sb = new StringBuilder(text.Length + 64);
        var pos = 0;
        foreach (var (start, end, replacement) in edits)
        {
            sb.Append(text, pos, start - pos).Append(replacement);
            pos = end;
        }

        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    private static void Collect(JsonNode node, Func<string, string> rewrite, List<(int, int, string)> edits)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var m in obj.Members)
                    Collect(m.Value, rewrite, edits);
                break;
            case JsonArray arr:
                foreach (var item in arr.Items)
                    Collect(item, rewrite, edits);
                break;
            case JsonString str:
                var changed = rewrite(str.Value);
                if (changed != str.Value)
                    edits.Add((str.Start, str.End, Quote(changed)));
                break;
        }
    }

    private sealed class Parser
    {
        private readonly string myText;

        public Parser(string text) => myText = text;

        public int Position { get; private set; }

        public void SkipWhitespace()
        {
            while (Position < myText.Length && char.IsWhiteSpace(myText[Position]))
                Position++;
        }

        private FormatException Error(string message) => new($"{message} at {Position}");

        public JsonNode ParseValue()
        {
            if (Position >= myText.Length)
                throw Error("unexpected end of JSON");

            var start = Position;
            JsonNode node;
            switch (myText[Position])
            {
                case '{':
                    node = ParseObject();
                    break;
                case '[':
                    node = ParseArray();
                    break;
                case '"':
                    node = new JsonString(ParseString());
                    break;
                default:
                    while (Position < myText.Length && "{}[],: \t\r\n\"".IndexOf(myText[Position]) < 0)
                        Position++;
                    if (Position == start)
                        throw Error("unexpected character");
                    node = new JsonLiteral(myText.Substring(start, Position - start));
                    break;
            }

            node.Start = start;
            node.End = Position;
            return node;
        }

        private JsonObject ParseObject()
        {
            var obj = new JsonObject();
            Position++;
            SkipWhitespace();
            if (Peek() == '}')
            {
                Position++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                    throw Error("expected object key");
                var key = ParseString();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                obj.Members.Add(new KeyValuePair<string, JsonNode>(key, ParseValue()));
                SkipWhitespace();
                if (Peek() == ',')
                {
                    Position++;
                    continue;
                }

                Expect('}');
                return obj;
            }
        }

        private JsonArray ParseArray()
        {
            var arr = new JsonArray();
            Position++;
            SkipWhitespace();
            if (Peek() == ']')
            {
                Position++;
                return arr;
            }

            while (true)
            {
                SkipWhitespace();
                arr.Items.Add(ParseValue());
                SkipWhitespace();
                if (Peek() == ',')
                {
                    Position++;
                    continue;
                }

                Expect(']');
                return arr;
            }
        }

        private char Peek() => Position < myText.Length ? myText[Position] : '\0';

        private void Expect(char ch)
        {
            if (Peek() != ch)
                throw Error($"expected '{ch}'");
            Position++;
        }

        private string ParseString()
        {
            Position++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (Position >= myText.Length)
                    throw Error("unterminated string");
                var ch = myText[Position++];
                if (ch == '"')
                    return sb.ToString();
                if (ch != '\\')
                {
                    sb.Append(ch);
                    continue;
                }

                if (Position >= myText.Length)
                    throw Error("unterminated escape");
                var esc = myText[Position++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    // fsJsonPrinter.EscapeString writes these two as well
                    case 'a': sb.Append('\a'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (Position + 4 > myText.Length)
                            throw Error("bad unicode escape");
                        sb.Append((char)int.Parse(myText.Substring(Position, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        Position += 4;
                        break;
                    default:
                        throw Error($"bad escape '\\{esc}'");
                }
            }
        }
    }
}
