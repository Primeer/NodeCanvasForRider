using System;

namespace NodeCanvasUsages.Core.Model;

public enum GraphKind
{
    Unknown,
    BehaviourTree,
    StateMachine,
    DialogueTree,
}

public enum UsageKind
{
    /// <summary>Method invoked by an action (<c>ExecuteFunction</c>, <c>ImplementedAction</c>, ...).</summary>
    Call,

    /// <summary>Method or property read by a condition (<c>CheckFunction</c>, <c>CheckProperty</c>).</summary>
    Check,

    /// <summary>Property getter read into a variable (<c>GetProperty</c>).</summary>
    Get,

    /// <summary>Property setter (<c>SetProperty</c>).</summary>
    Set,

    /// <summary>Field read or written (<c>GetField</c>, <c>SetField</c>, ...).</summary>
    Field,
}

/// <summary>One reference from a NodeCanvas graph to a C# member, as stored in <c>_baseInfo</c>.</summary>
public sealed class GraphUsage : IEquatable<GraphUsage>
{
    public GraphUsage(string graphName, GraphKind graphKind, string? boundOwner, int nodeIndex, string ownerLabel,
                      string nodeTitle, UsageKind kind, string typeName, string memberName, string? returnType,
                      string? paramsInfo, string? agent, string taskType, bool isWrite, string? missingType, int offset)
    {
        GraphName = graphName;
        GraphKind = graphKind;
        BoundOwner = boundOwner;
        NodeIndex = nodeIndex;
        OwnerLabel = ownerLabel;
        NodeTitle = nodeTitle;
        Kind = kind;
        TypeName = typeName;
        MemberName = memberName;
        ReturnType = returnType;
        ParamsInfo = paramsInfo;
        Agent = agent;
        TaskType = taskType;
        IsWrite = isWrite;
        MissingType = missingType;
        Offset = offset;
    }

    /// <summary>Asset file name without extension; for bound graphs <c>Prefab:Owner</c>.</summary>
    public string GraphName { get; }

    public GraphKind GraphKind { get; }

    /// <summary>GameObject name (or document anchor) for graphs bound to a GraphOwner; <c>null</c> for graph assets.</summary>
    public string? BoundOwner { get; }

    public bool IsBound => BoundOwner != null;

    /// <summary>Index in <c>nodes[]</c>; for a connection, the index of its source node; -1 if outside nodes/connections.</summary>
    public int NodeIndex { get; }

    /// <summary><c>#5</c> for a node, <c>#1-&gt;#0</c> for a connection, <c>graph</c> otherwise.</summary>
    public string OwnerLabel { get; }

    /// <summary><c>Sequencer "Fight"</c>: short node type plus its <c>_name</c>, if any.</summary>
    public string NodeTitle { get; }

    public UsageKind Kind { get; }

    /// <summary>CLR full name of the type the member was accessed on (nested types use <c>+</c>).</summary>
    public string TypeName { get; }

    /// <summary>Second part of <c>_baseInfo</c> as written (<c>get_Name</c> for property getters).</summary>
    public string MemberName { get; }

    public string? ReturnType { get; }

    /// <summary><c>_paramsInfo</c> (parameter CLR names joined with <c>|</c>), <c>null</c> for fields.</summary>
    public string? ParamsInfo { get; }

    /// <summary>Name of the blackboard variable used as the call target (<c>overrideAgent._name</c>).</summary>
    public string? Agent { get; }

    /// <summary>Full <c>$type</c> of the task that holds the reference.</summary>
    public string TaskType { get; }

    /// <summary>The member is assigned (property setter, <c>SetField</c>).</summary>
    public bool IsWrite { get; }

    /// <summary><c>_missingType</c> when found inside the <c>_recoveryState</c> of a Missing* wrapper.</summary>
    public string? MissingType { get; }

    /// <summary>Character offset in the file (start of the <c>_baseInfo</c> value, or of the enclosing string).</summary>
    public int Offset { get; }

    /// <summary>Member name without <c>get_</c>/<c>set_</c>.</summary>
    public string PlainMemberName => StripAccessorPrefix(MemberName);

    public bool IsPropertyAccessor => MemberName.StartsWith("get_", StringComparison.Ordinal) ||
                                      MemberName.StartsWith("set_", StringComparison.Ordinal);

    public string Presentation => $"{GraphName} {OwnerLabel} — {NodeTitle}";

    public static string StripAccessorPrefix(string member) =>
        member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal)
            ? member.Substring(4)
            : member;

    public bool Equals(GraphUsage? other) =>
        other != null && GraphName == other.GraphName && OwnerLabel == other.OwnerLabel &&
        TypeName == other.TypeName && MemberName == other.MemberName && ParamsInfo == other.ParamsInfo &&
        Offset == other.Offset && Kind == other.Kind && MissingType == other.MissingType;

    public override bool Equals(object? obj) => Equals(obj as GraphUsage);

    public override int GetHashCode()
    {
        unchecked
        {
            var h = GraphName.GetHashCode();
            h = h * 397 ^ OwnerLabel.GetHashCode();
            h = h * 397 ^ MemberName.GetHashCode();
            return h * 397 ^ Offset;
        }
    }

    public override string ToString() =>
        $"{Presentation} [{Kind}] {TypeName}|{MemberName}({ParamsInfo}){(Agent != null ? " @" + Agent : "")}" +
        (MissingType != null ? $" missing:{MissingType}" : "");
}
