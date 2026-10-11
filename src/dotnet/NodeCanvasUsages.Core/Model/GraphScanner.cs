using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NodeCanvasUsages.Core.Json;
using NodeCanvasUsages.Core.Yaml;

namespace NodeCanvasUsages.Core.Model;

/// <summary>Finds every <c>_baseInfo</c> member reference in the NodeCanvas graphs of one Unity YAML file.</summary>
public static class GraphScanner
{
    public const string Marker = "_baseInfo";

    public static List<GraphUsage> Scan(string text, string path, Action<Exception>? onError = null)
    {
        var result = new List<GraphUsage>();
        if (text.IndexOf(Marker, StringComparison.Ordinal) < 0)
            return result;

        var file = new UnityYamlFile(text);
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var slot in file.Slots)
        {
            try
            {
                var span = file.ReadScalar(slot);
                if (span.Value.IndexOf(Marker, StringComparison.Ordinal) < 0)
                    continue;
                var root = FsJson.Parse(span.Value) as JsonObject;
                if (root == null)
                    continue;
                var graphName = slot.IsBound ? $"{stem}:{slot.OwnerLabel}" : stem;
                new Walker(result, graphName, GraphKindOf(root.GetString("type")), slot.OwnerLabel, root,
                    p => p < span.Map.Length ? span.Map[p] : span.Map.Length > 0 ? span.Map[span.Map.Length - 1] : 0)
                    .WalkGraph();
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                onError?.Invoke(new FormatException($"{path}: {slot.GraphKey}: {e.Message}", e));
            }
        }

        return result;
    }

    /// <summary>Keys whose string values hold CLR type names (as FullSerializer and NodeCanvas write them).</summary>
    private static readonly HashSet<string> ourTypeKeys = new(StringComparer.Ordinal)
        { "$type", "_type", "_missingType", Marker, "_paramsInfo" };

    private static readonly char[] ourTypeSeparators = { '[', ']', ',', '|', '&', '*', ' ', '\t' };

    private static readonly Regex ourTypeToken =
        new(@"^[A-Za-z_]\w*(`\d+)?([.+][A-Za-z_]\w*(`\d+)?)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every CLR type name (without generic arguments or assembly; nested with <c>+</c>) that the graphs of one file
    /// refer to: <c>$type</c>, <c>_type</c>, <c>_missingType</c>, the type parts of <c>_baseInfo</c>,
    /// <c>_paramsInfo</c>, generic arguments of all of them, and the same inside <c>_recoveryState</c>.
    /// </summary>
    public static List<string> ScanTypeNames(string text, string path, Action<Exception>? onError = null)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var file = new UnityYamlFile(text);
        foreach (var slot in file.Slots)
        {
            try
            {
                CollectTypeNames(FsJson.Parse(file.ReadScalar(slot).Value), result);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                onError?.Invoke(new FormatException($"{path}: {slot.GraphKey}: {e.Message}", e));
            }
        }

        return new List<string>(result);
    }

    private static void CollectTypeNames(JsonNode node, ISet<string> result)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array.Items)
                    CollectTypeNames(item, result);
                break;
            case JsonObject obj:
                foreach (var m in obj.Members)
                {
                    if (m.Value is not JsonString value)
                    {
                        CollectTypeNames(m.Value, result);
                    }
                    else if (m.Key == "_recoveryState")
                    {
                        if (value.Value.IndexOf('{') < 0)
                            continue;
                        try
                        {
                            CollectTypeNames(FsJson.Parse(value.Value), result);
                        }
                        catch (FormatException)
                        {
                            // a damaged recovery state is not ours to report
                        }
                    }
                    else if (ourTypeKeys.Contains(m.Key))
                    {
                        var text = value.Value;
                        if (m.Key == Marker)
                        {
                            // Type|member|ReturnType: the member name is not a type
                            var parts = text.Split('|');
                            if (parts.Length >= 2)
                                parts[1] = "";
                            text = string.Join("|", parts);
                        }

                        foreach (var token in text.Split(ourTypeSeparators, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (ourTypeToken.IsMatch(token))
                                result.Add(token);
                        }
                    }
                }

                break;
        }
    }

    /// <summary><c>Ns.Outer+Inner`1</c> → <c>Inner`1</c>: the part a move to another namespace or scope keeps.</summary>
    public static string ClrShortName(string clrName) => clrName.Substring(clrName.LastIndexOfAny(new[] { '.', '+' }) + 1);

    public static GraphKind GraphKindOf(string? type)
    {
        if (type == null)
            return GraphKind.Unknown;
        if (type.EndsWith("BehaviourTree", StringComparison.Ordinal))
            return GraphKind.BehaviourTree;
        if (type.EndsWith("FSM", StringComparison.Ordinal))
            return GraphKind.StateMachine;
        if (type.EndsWith("DialogueTree", StringComparison.Ordinal))
            return GraphKind.DialogueTree;
        return GraphKind.Unknown;
    }

    /// <summary><c>NodeCanvas.Tasks.Actions.SetVariable`1[[...]]</c> → <c>SetVariable</c>.</summary>
    public static string ShortTypeName(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return "?";
        var t = type!;
        var cut = t.IndexOfAny(new[] { '`', '[', ',' });
        if (cut >= 0)
            t = t.Substring(0, cut);
        var dot = t.LastIndexOfAny(new[] { '.', '+' });
        return dot >= 0 ? t.Substring(dot + 1) : t;
    }

    private sealed class Walker
    {
        private readonly List<GraphUsage> myResult;
        private readonly string myGraphName;
        private readonly GraphKind myGraphKind;
        private readonly string? myBoundOwner;
        private readonly JsonObject myRoot;
        private readonly Func<int, int> myOffset;

        public Walker(List<GraphUsage> result, string graphName, GraphKind graphKind, string? boundOwner,
                      JsonObject root, Func<int, int> offset)
        {
            myResult = result;
            myGraphName = graphName;
            myGraphKind = graphKind;
            myBoundOwner = boundOwner;
            myRoot = root;
            myOffset = offset;
        }

        public void WalkGraph()
        {
            var nodes = myRoot["nodes"] as JsonArray;
            var connections = myRoot["connections"] as JsonArray;
            foreach (var member in myRoot.Members)
            {
                if (member.Value == nodes && nodes != null)
                {
                    for (var i = 0; i < nodes.Items.Count; i++)
                    {
                        if (nodes.Items[i] is JsonObject node)
                            Walk(node, new Owner(i, $"#{i}", NodeTitle(node)), new List<JsonObject>(), null, null);
                    }
                }
                else if (member.Value == connections && connections != null)
                {
                    foreach (var item in connections.Items)
                    {
                        if (item is JsonObject connection)
                            Walk(connection, ConnectionOwner(connection, nodes), new List<JsonObject>(), null, null);
                    }
                }
                else
                {
                    Walk(member.Value, new Owner(-1, "graph", ShortTypeName(myRoot.GetString("type"))),
                        new List<JsonObject>(), null, null);
                }
            }
        }

        private static string NodeTitle(JsonObject node)
        {
            var type = ShortTypeName(node.GetString("$type"));
            var name = node.GetString("_name");
            return string.IsNullOrEmpty(name) ? type : $"{type} \"{name}\"";
        }

        private static string? RefIndex(JsonObject connection, string key) =>
            (connection[key] as JsonObject)?.GetString("$ref");

        private static Owner ConnectionOwner(JsonObject connection, JsonArray? nodes)
        {
            var source = RefIndex(connection, "_sourceNode") ?? "?";
            var target = RefIndex(connection, "_targetNode") ?? "?";
            var index = int.TryParse(source, out var s) ? s : -1;
            var title = ShortTypeName(connection.GetString("$type"));
            if (nodes != null && index >= 0 && index < nodes.Items.Count && nodes.Items[index] is JsonObject src)
                title = $"{title} from {NodeTitle(src)}";
            return new Owner(index, $"#{source}->#{target}", title);
        }

        /// <param name="ancestors">Objects enclosing <paramref name="node"/>, innermost last.</param>
        private void Walk(JsonNode node, Owner owner, List<JsonObject> ancestors, string? missingType,
                          int? fixedOffset)
        {
            switch (node)
            {
                case JsonArray arr:
                    foreach (var item in arr.Items)
                        Walk(item, owner, ancestors, missingType, fixedOffset);
                    break;

                case JsonObject obj:
                    if (obj[Marker] is JsonString baseInfo)
                        Add(obj, baseInfo, owner, ancestors, missingType, fixedOffset);

                    if (obj["_recoveryState"] is JsonString recovery &&
                        recovery.Value.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                    {
                        JsonNode? inner = null;
                        try
                        {
                            inner = FsJson.Parse(recovery.Value);
                        }
                        catch (FormatException)
                        {
                            // a damaged recovery state is not ours to report
                        }

                        if (inner != null)
                        {
                            Walk(inner, owner, new List<JsonObject>(ancestors) { obj },
                                obj.GetString("_missingType") ?? missingType, fixedOffset ?? myOffset(recovery.Start));
                        }
                    }

                    ancestors.Add(obj);
                    foreach (var m in obj.Members)
                        Walk(m.Value, owner, ancestors, missingType, fixedOffset);
                    ancestors.RemoveAt(ancestors.Count - 1);
                    break;
            }
        }

        private void Add(JsonObject holder, JsonString baseInfo, Owner owner, List<JsonObject> ancestors,
                         string? missingType, int? fixedOffset)
        {
            var parts = baseInfo.Value.Split('|');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return;

            JsonObject? task = null;
            string? holderKey = null;
            for (var k = ancestors.Count - 1; k >= 0; k--)
            {
                var candidate = ancestors[k];
                if (holderKey == null)
                {
                    foreach (var m in candidate.Members)
                    {
                        if (m.Value == holder)
                            holderKey = m.Key;
                    }
                }

                if (candidate.GetString("$type") != null)
                {
                    task = candidate;
                    break;
                }
            }

            var taskType = task?.GetString("$type") ?? "";
            var shortTask = ShortTypeName(taskType);
            var member = parts[1];
            var isField = holderKey == "field" || holder["_paramsInfo"] == null && parts.Length == 2;

            UsageKind kind;
            bool isWrite;
            if (isField)
            {
                kind = UsageKind.Field;
                isWrite = shortTask.StartsWith("Set", StringComparison.Ordinal);
            }
            else if (member.StartsWith("set_", StringComparison.Ordinal))
            {
                kind = UsageKind.Set;
                isWrite = true;
            }
            else
            {
                kind = shortTask.IndexOf("Check", StringComparison.Ordinal) >= 0 ? UsageKind.Check
                    : member.StartsWith("get_", StringComparison.Ordinal) ? UsageKind.Get
                    : UsageKind.Call;
                isWrite = false;
            }

            var agent = (task?["overrideAgent"] as JsonObject)?.GetString("_name");
            myResult.Add(new GraphUsage(
                myGraphName, myGraphKind, myBoundOwner, owner.NodeIndex, owner.Label, owner.Title, kind,
                parts[0], member, parts.Length > 2 ? parts[2] : null,
                isField ? null : holder.GetString("_paramsInfo") ?? "",
                string.IsNullOrEmpty(agent) ? null : agent, taskType, isWrite, missingType,
                fixedOffset ?? myOffset(baseInfo.Start)));
        }

        private readonly struct Owner
        {
            public Owner(int nodeIndex, string label, string title)
            {
                NodeIndex = nodeIndex;
                Label = label;
                Title = title;
            }

            public int NodeIndex { get; }
            public string Label { get; }
            public string Title { get; }
        }
    }
}
