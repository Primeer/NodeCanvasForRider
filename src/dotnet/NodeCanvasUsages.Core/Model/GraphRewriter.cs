using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NodeCanvasUsages.Core.Json;
using NodeCanvasUsages.Core.Yaml;

namespace NodeCanvasUsages.Core.Model;

/// <summary>A parsed <c>_baseInfo</c> reference: type, member as written (<c>get_X</c>), <c>_paramsInfo</c>.</summary>
public readonly struct MemberRef
{
    public MemberRef(string typeName, string memberName, string? paramsInfo, bool isField)
    {
        TypeName = typeName;
        MemberName = memberName;
        ParamsInfo = paramsInfo;
        IsField = isField;
    }

    public string TypeName { get; }
    public string MemberName { get; }
    public string? ParamsInfo { get; }
    public bool IsField { get; }
    public string PlainMemberName => GraphUsage.StripAccessorPrefix(MemberName);
}

/// <summary>Replacement of <c>[Start, End)</c> in a file's text.</summary>
public readonly struct TextEdit
{
    public TextEdit(int start, int end, string newText)
    {
        Start = start;
        End = end;
        NewText = newText;
    }

    public int Start { get; }
    public int End { get; }
    public string NewText { get; }

    /// <summary>The same edit narrowed to the part that actually differs from <paramref name="text"/>.</summary>
    public TextEdit Trim(string text)
    {
        var oldLength = End - Start;
        var prefix = 0;
        while (prefix < oldLength && prefix < NewText.Length && text[Start + prefix] == NewText[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < oldLength - prefix && suffix < NewText.Length - prefix &&
               text[End - 1 - suffix] == NewText[NewText.Length - 1 - suffix])
            suffix++;
        return new TextEdit(Start + prefix, End - suffix, NewText.Substring(prefix, NewText.Length - prefix - suffix));
    }

    public override string ToString() => $"[{Start}, {End}) -> \"{NewText}\"";
}

/// <summary>
/// Project-wide renames inside serialized graphs. Every string value of the graph JSON is rewritten, including JSON
/// nested in <c>_recoveryState</c>; the YAML scalar is re-encoded with Unity's folding so the file matches what Unity
/// itself would write.
/// </summary>
public static class GraphRewriter
{
    private const string TypeNameChars = @"[\w.+`]";

    /// <summary>
    /// Rewrites <c>Type|Old|...</c>, <c>Type|get_Old|...</c>, <c>Type|set_Old|...</c> and field references
    /// <c>Type|Old</c> for every type in <paramref name="typeNames"/> (CLR full names, nested with <c>+</c>).
    /// </summary>
    public static Func<string, string> RenameMember(IEnumerable<string> typeNames, string oldName, string newName)
    {
        var names = typeNames.Distinct().OrderByDescending(t => t.Length).ToList();
        if (names.Count == 0 || oldName == newName)
            return s => s;

        var pattern = new Regex(
            $"(?<!{TypeNameChars})({string.Join("|", names.Select(Regex.Escape))})\\|(get_|set_)?{Regex.Escape(oldName)}(?=\\||\"|$)",
            RegexOptions.CultureInvariant);
        return s => s.IndexOf(oldName, StringComparison.Ordinal) < 0
            ? s
            : pattern.Replace(s, m => $"{m.Groups[1].Value}|{m.Groups[2].Value}{newName}");
    }

    /// <summary>
    /// Rewrites the full type name <paramref name="oldName"/> to <paramref name="newName"/> wherever it occurs as a
    /// whole name (also as the outer part of nested names and inside generic arguments). When
    /// <paramref name="assembly"/> is given, assembly-qualified references of the new name get that assembly.
    /// </summary>
    public static Func<string, string> RenameType(string oldName, string newName, string? assembly = null)
    {
        if (oldName == newName && assembly == null)
            return s => s;
        var pattern = new Regex($"(?<!{TypeNameChars}){Regex.Escape(oldName)}(?![\\w`.])", RegexOptions.CultureInvariant);
        var asmPattern = new Regex(
            $"(?<!{TypeNameChars}){Regex.Escape(newName)}((?:\\+[\\w`]+)*), ([\\w.\\-]+), Version=",
            RegexOptions.CultureInvariant);
        return s =>
        {
            if (s.IndexOf(oldName, StringComparison.Ordinal) < 0)
                return s;
            var result = pattern.Replace(s, newName);
            if (result != s && assembly != null)
                result = asmPattern.Replace(result, m => $"{newName}{m.Groups[1].Value}, {assembly}, Version=");
            return result;
        };
    }

    /// <summary>
    /// Rewrites the namespace prefix <paramref name="oldNamespace"/> of every full type name in it (including nested
    /// namespaces): <c>Demo.Game.Bot</c> → <c>Demo.Units.Bot</c> for <c>Demo.Game</c> → <c>Demo.Units</c>.
    /// </summary>
    public static Func<string, string> RenameNamespace(string oldNamespace, string newNamespace)
    {
        if (oldNamespace == newNamespace)
            return s => s;
        var pattern = new Regex($"(?<!{TypeNameChars}){Regex.Escape(oldNamespace)}(?=\\.[\\w`])", RegexOptions.CultureInvariant);
        return s => s.IndexOf(oldNamespace, StringComparison.Ordinal) < 0 ? s : pattern.Replace(s, newNamespace);
    }

    /// <summary>
    /// JSON-level rename of exactly the references accepted by <paramref name="match"/> (it receives the parsed
    /// <c>_baseInfo</c> parts and <c>_paramsInfo</c>), so that renaming one overload leaves the others alone.
    /// References inside <c>_recoveryState</c> are handled too. Returns the new JSON or <c>null</c>.
    /// </summary>
    public static string? RenameReferences(string json, Func<MemberRef, bool> match, string newMemberName)
    {
        var root = FsJson.Parse(json);
        var edits = new List<TextEdit>();
        CollectReferenceEdits(root, match, newMemberName, edits);
        return edits.Count == 0 ? null : Apply(json, edits);
    }

    private static void CollectReferenceEdits(JsonNode node, Func<MemberRef, bool> match, string newName, List<TextEdit> edits)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array.Items)
                    CollectReferenceEdits(item, match, newName, edits);
                break;
            case JsonObject obj:
                if (obj[GraphScanner.Marker] is JsonString baseInfo)
                {
                    var parts = baseInfo.Value.Split('|');
                    if (parts.Length >= 2)
                    {
                        var reference = new MemberRef(parts[0], parts[1], obj.GetString("_paramsInfo"), parts.Length == 2);
                        if (match(reference))
                        {
                            var member = parts[1];
                            parts[1] = member.StartsWith("get_", StringComparison.Ordinal) ||
                                       member.StartsWith("set_", StringComparison.Ordinal)
                                ? member.Substring(0, 4) + newName
                                : newName;
                            edits.Add(new TextEdit(baseInfo.Start, baseInfo.End, FsJson.Quote(string.Join("|", parts))));
                        }
                    }
                }

                foreach (var m in obj.Members)
                {
                    if (m.Key == "_recoveryState" && m.Value is JsonString recovery &&
                        recovery.Value.IndexOf(GraphScanner.Marker, StringComparison.Ordinal) >= 0)
                    {
                        string? inner = null;
                        try
                        {
                            inner = RenameReferences(recovery.Value, match, newName);
                        }
                        catch (FormatException)
                        {
                            // leave a damaged recovery state as it is
                        }

                        if (inner != null)
                            edits.Add(new TextEdit(recovery.Start, recovery.End, FsJson.Quote(inner)));
                    }
                    else
                    {
                        CollectReferenceEdits(m.Value, match, newName, edits);
                    }
                }

                break;
        }
    }

    /// <summary>Edits for a JSON-level rewrite (<paramref name="rewriteJson"/> returns <c>null</c> when nothing changes).</summary>
    public static List<TextEdit> ComputeJsonEdits(string text, Func<string, string?> rewriteJson,
                                                  Func<GraphSlot, bool>? slotFilter = null)
    {
        var edits = new List<TextEdit>();
        var file = new UnityYamlFile(text);
        foreach (var slot in file.Slots)
        {
            if (slotFilter != null && !slotFilter(slot))
                continue;
            var span = file.ReadScalar(slot);
            var json = rewriteJson(span.Value);
            if (json == null || json == span.Value)
                continue;
            var (start, end) = file.Range(span);
            edits.Add(new TextEdit(start, end, file.EncodeScalar(span, json)));
        }

        return edits;
    }

    /// <summary>Composes several rewrites (applied left to right).</summary>
    public static Func<string, string> Chain(IEnumerable<Func<string, string>> rewrites)
    {
        var list = rewrites.ToList();
        return s => list.Aggregate(s, (acc, f) => f(acc));
    }

    /// <summary>
    /// Edits (one per changed graph scalar, covering the whole scalar) that apply <paramref name="rewrite"/> to
    /// <paramref name="text"/>. Empty when no graph changes; untouched graphs produce no edit.
    /// </summary>
    public static List<TextEdit> ComputeEdits(string text, Func<string, string> rewrite,
                                              Func<GraphSlot, bool>? slotFilter = null) =>
        ComputeJsonEdits(text, json => FsJson.RewriteStrings(json, rewrite), slotFilter);

    /// <summary>The rewritten file text, or <c>null</c> when nothing changed.</summary>
    public static string? Rewrite(string text, Func<string, string> rewrite, Func<GraphSlot, bool>? slotFilter = null)
    {
        var edits = ComputeEdits(text, rewrite, slotFilter);
        return edits.Count == 0 ? null : Apply(text, edits);
    }

    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var sb = new StringBuilder(text.Length + 256);
        var pos = 0;
        foreach (var edit in edits.OrderBy(e => e.Start))
        {
            sb.Append(text, pos, edit.Start - pos).Append(edit.NewText);
            pos = edit.End;
        }

        return sb.Append(text, pos, text.Length - pos).ToString();
    }
}
