using System.Collections.Generic;
using JetBrains.Serialization;
using JetBrains.Util.PersistentMap;
using NodeCanvasUsages.Core.Model;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>Per-file payload of <see cref="NodeCanvasGraphCache"/>: every member and type reference found in the file.</summary>
public sealed class NodeCanvasFileData
{
    // bump when the stored layout or the scanner's output changes
    public const int FormatVersion = 5;

    /// <summary>Not a graph container.</summary>
    public static readonly NodeCanvasFileData Empty = new(new List<GraphUsage>(), new List<string>(), isGraphFile: false);

    public static readonly IUnsafeMarshaller<NodeCanvasFileData> Marshaller =
        new UniversalMarshaller<NodeCanvasFileData>(Read, Write);

    public NodeCanvasFileData(IReadOnlyList<GraphUsage> usages, IReadOnlyList<string> typeNames, bool isGraphFile)
    {
        Usages = usages;
        TypeNames = typeNames;
        IsGraphFile = isGraphFile;
    }

    public IReadOnlyList<GraphUsage> Usages { get; }

    /// <summary>CLR names of every type the graphs refer to (see <see cref="GraphScanner.ScanTypeNames"/>).</summary>
    public IReadOnlyList<string> TypeNames { get; }

    /// <summary>The file holds at least one serialized graph (it may still have no member references).</summary>
    public bool IsGraphFile { get; }

    private static void Write(UnsafeWriter writer, NodeCanvasFileData value)
    {
        writer.Write(value.IsGraphFile);
        writer.Write(value.Usages.Count);
        foreach (var u in value.Usages)
        {
            writer.Write(u.GraphName);
            writer.Write((int)u.GraphKind);
            writer.Write(u.BoundOwner);
            writer.Write(u.NodeIndex);
            writer.Write(u.OwnerLabel);
            writer.Write(u.NodeTitle);
            writer.Write((int)u.Kind);
            writer.Write(u.TypeName);
            writer.Write(u.MemberName);
            writer.Write(u.ReturnType);
            writer.Write(u.ParamsInfo);
            writer.Write(u.Agent);
            writer.Write(u.TaskType);
            writer.Write(u.IsWrite);
            writer.Write(u.MissingType);
            writer.Write(u.Offset);
        }

        writer.Write(value.TypeNames.Count);
        foreach (var name in value.TypeNames)
            writer.Write(name);
    }

    private static NodeCanvasFileData Read(UnsafeReader reader)
    {
        var isGraphFile = reader.ReadBoolean();
        var count = reader.ReadInt32();
        var usages = new List<GraphUsage>(count);
        for (var i = 0; i < count; i++)
        {
            usages.Add(new GraphUsage(
                graphName: reader.ReadString()!,
                graphKind: (GraphKind)reader.ReadInt32(),
                boundOwner: reader.ReadString(),
                nodeIndex: reader.ReadInt32(),
                ownerLabel: reader.ReadString()!,
                nodeTitle: reader.ReadString()!,
                kind: (UsageKind)reader.ReadInt32(),
                typeName: reader.ReadString()!,
                memberName: reader.ReadString()!,
                returnType: reader.ReadString(),
                paramsInfo: reader.ReadString(),
                agent: reader.ReadString(),
                taskType: reader.ReadString()!,
                isWrite: reader.ReadBoolean(),
                missingType: reader.ReadString(),
                offset: reader.ReadInt32()));
        }

        var typeCount = reader.ReadInt32();
        var typeNames = new List<string>(typeCount);
        for (var i = 0; i < typeCount; i++)
            typeNames.Add(reader.ReadString()!);

        return new NodeCanvasFileData(usages, typeNames, isGraphFile);
    }
}
