using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NodeCanvasUsages.Core.Yaml;

/// <summary>One serialized graph inside a Unity YAML file.</summary>
public sealed class GraphSlot
{
    public const string AssetGraphKey = "_serializedGraph";
    public const string BoundGraphKey = "_boundGraphSerialization";

    internal GraphSlot(string graphKey, int docStart, int docEnd, string? owner, string? anchor)
    {
        GraphKey = graphKey;
        DocStart = docStart;
        DocEnd = docEnd;
        Owner = owner;
        Anchor = anchor;
    }

    public string GraphKey { get; }
    public int DocStart { get; }
    public int DocEnd { get; }

    /// <summary>GameObject name of the owner component (bound graphs only); disambiguated with <c>#anchor</c>.</summary>
    public string? Owner { get; internal set; }

    /// <summary>fileID of the YAML document that holds the graph.</summary>
    public string? Anchor { get; }

    public bool IsBound => GraphKey == BoundGraphKey;

    /// <summary><c>Owner</c> or the document anchor.</summary>
    public string? OwnerLabel => IsBound ? Owner ?? Anchor : null;
}

/// <summary>
/// Line-level view of a Unity YAML file (<c>.asset</c>, <c>.prefab</c>, <c>.unity</c>) that locates serialized
/// NodeCanvas graphs and replaces their scalar without touching any other byte of the file.
/// </summary>
public sealed class UnityYamlFile
{
    private static readonly Regex ourDocumentHeader = new(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ourFileId = new(@"fileID: (-?\d+)", RegexOptions.Compiled);

    private readonly string myText;
    private List<GraphSlot>? mySlots;

    public UnityYamlFile(string text)
    {
        myText = text;
        Newline = text.IndexOf("\r\n", 0, Math.Min(text.Length, 4096), StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        Lines = text.Split(new[] { Newline }, StringSplitOptions.None);
        var offsets = new int[Lines.Length];
        var offset = 0;
        for (var i = 0; i < Lines.Length; i++)
        {
            offsets[i] = offset;
            offset += Lines[i].Length + Newline.Length;
        }

        LineOffsets = offsets;
    }

    public string Newline { get; }
    public string[] Lines { get; }
    public int[] LineOffsets { get; }
    public string Text => myText;

    public IReadOnlyList<GraphSlot> Slots => mySlots ??= FindSlots();

    private List<(int Start, int End, string Class, string FileId)> Documents()
    {
        var starts = new List<(int Line, string Class, string FileId)>();
        for (var i = 0; i < Lines.Length; i++)
        {
            if (!Lines[i].StartsWith("--- !u!", StringComparison.Ordinal))
                continue;
            var m = ourDocumentHeader.Match(Lines[i]);
            if (m.Success)
                starts.Add((i, m.Groups[1].Value, m.Groups[2].Value));
        }

        if (starts.Count == 0)
            return new List<(int, int, string, string)> { (0, Lines.Length, "", "") };

        var docs = new List<(int, int, string, string)>(starts.Count);
        for (var n = 0; n < starts.Count; n++)
        {
            var end = n + 1 < starts.Count ? starts[n + 1].Line : Lines.Length;
            docs.Add((starts[n].Line, end, starts[n].Class, starts[n].FileId));
        }

        return docs;
    }

    private string? ValueOf(int start, int end, string key)
    {
        for (var i = start; i < end; i++)
        {
            var s = Lines[i].TrimStart(' ');
            if (s.StartsWith(key + ":", StringComparison.Ordinal))
                return s.Substring(key.Length + 1).Trim();
        }

        return null;
    }

    private List<GraphSlot> FindSlots()
    {
        var result = new List<GraphSlot>();
        var docs = Documents();
        var gameObjectNames = new Dictionary<string, string>();
        foreach (var (s, e, cls, fid) in docs)
        {
            if (cls != "1")
                continue;
            var name = ValueOf(s, e, "m_Name");
            if (name != null)
                gameObjectNames[fid] = name;
        }

        foreach (var (s, e, _, fid) in docs)
        {
            foreach (var key in new[] { GraphSlot.AssetGraphKey, GraphSlot.BoundGraphKey })
            {
                var span = YamlScalar.Find(Lines, key, s, e);
                if (span == null || span.Style == ScalarStyle.Empty)
                    continue;

                string? owner = null;
                if (key == GraphSlot.BoundGraphKey)
                {
                    var go = ValueOf(s, e, "m_GameObject");
                    var m = go != null ? ourFileId.Match(go) : Match.Empty;
                    if (m.Success)
                        gameObjectNames.TryGetValue(m.Groups[1].Value, out owner);
                }

                result.Add(new GraphSlot(key, s, e, owner, fid.Length > 0 ? fid : null));
            }
        }

        var duplicates = result.Where(x => x.Owner != null).GroupBy(x => x.Owner).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToList();
        foreach (var slot in result)
        {
            if (slot.Owner != null && duplicates.Contains(slot.Owner))
                slot.Owner = $"{slot.Owner}#{slot.Anchor}";
        }

        return result;
    }

    public ScalarSpan ReadScalar(GraphSlot slot) =>
        YamlScalar.Find(Lines, slot.GraphKey, slot.DocStart, slot.DocEnd, LineOffsets)
        ?? throw new FormatException($"{slot.GraphKey} not found");

    /// <summary><c>_graphSource._version</c> (or <c>_boundGraphSource._version</c>), if present.</summary>
    public string? Version(GraphSlot slot)
    {
        var sourceKey = slot.IsBound ? "_boundGraphSource" : "_graphSource";
        for (var i = slot.DocStart; i < slot.DocEnd; i++)
        {
            if (!Lines[i].TrimStart(' ').StartsWith(sourceKey + ":", StringComparison.Ordinal))
                continue;
            return ValueOf(i + 1, Math.Min(i + 8, slot.DocEnd), "_version");
        }

        return null;
    }

    /// <summary>Absolute character range of the scalar lines (without the trailing newline).</summary>
    public (int Start, int End) Range(ScalarSpan span)
    {
        var start = LineOffsets[span.StartLine];
        var last = span.EndLine - 1;
        return (start, LineOffsets[last] + Lines[last].Length);
    }

    /// <summary>Text that replaces <see cref="Range"/> of <paramref name="span"/> when the graph JSON becomes <paramref name="json"/>.</summary>
    public string EncodeScalar(ScalarSpan span, string json) =>
        string.Join(Newline, YamlScalar.Encode(span.Key, span.KeyIndent, json));
}
