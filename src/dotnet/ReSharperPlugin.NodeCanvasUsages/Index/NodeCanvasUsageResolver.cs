using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Resolve;
using NodeCanvasUsages.Core.Model;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>
/// Maps graph references to C# members the way NodeCanvas' reflection does: the type is looked up by its CLR name
/// (the type the member was accessed on, so a base class member used through a derived type still resolves), the
/// member by name (<c>get_X</c>/<c>set_X</c> → property <c>X</c>), and overloads by <c>_paramsInfo</c>.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasUsageResolver
{
    private static readonly Regex ourAssemblyQualifier =
        new(@",\s*[^\[\],]+,\s*Version=[^,\]]*(,\s*Culture=[^,\]]*)?(,\s*PublicKeyToken=[^,\]]*)?", RegexOptions.Compiled);

    private readonly ISolution mySolution;
    private readonly IGraphUsageIndex myCache;

    public NodeCanvasUsageResolver(ISolution solution, IGraphUsageIndex cache)
    {
        mySolution = solution;
        myCache = cache;
    }

    /// <summary>Property for accessors; method, property or field otherwise; <c>null</c> for anything else.</summary>
    public static ITypeMember? Normalize(IDeclaredElement? element) => element switch
    {
        IAccessor accessor => accessor.OwnerMember as IProperty,
        IMethod method => method,
        IProperty property => property,
        IField field => field,
        _ => null,
    };

    /// <summary>Graph references that resolve to <paramref name="element"/>. Requires a read lock.</summary>
    public IReadOnlyList<GraphUsageEntry> FindUsages(IDeclaredElement element, bool includeBound)
    {
        var member = Normalize(element);
        if (member?.GetContainingType() == null || !myCache.IsEnabled)
            return Array.Empty<GraphUsageEntry>();

        var candidates = myCache.GetByMemberName(member.ShortName);
        if (candidates.Count == 0)
            return candidates;

        var scope = SymbolScope();
        var types = new Dictionary<string, ITypeElement?>(StringComparer.Ordinal);
        var result = new List<GraphUsageEntry>();
        foreach (var entry in candidates)
        {
            if (!includeBound && entry.Usage.IsBound)
                continue;
            if (!types.TryGetValue(entry.Usage.TypeName, out var type))
                types[entry.Usage.TypeName] = type = scope.GetTypeElementByCLRName(entry.Usage.TypeName);
            if (type != null && Equals(ResolveMember(type, entry.Usage), member))
                result.Add(entry);
        }

        return result;
    }

    public int CountUsages(IDeclaredElement element, bool includeBound) => FindUsages(element, includeBound).Count;

    /// <summary>The member a reference points to, or <c>null</c> when it no longer resolves. Requires a read lock.</summary>
    public ITypeMember? Resolve(GraphUsage usage)
    {
        var type = SymbolScope().GetTypeElementByCLRName(usage.TypeName);
        return type == null ? null : ResolveMember(type, usage);
    }

    public ITypeElement? ResolveType(GraphUsage usage) => SymbolScope().GetTypeElementByCLRName(usage.TypeName);

    /// <summary>A type with this CLR name exists in the solution or its libraries. Requires a read lock.</summary>
    public bool TypeExists(string clrName) => SymbolScope().GetTypeElementByCLRName(clrName) != null;

    private ISymbolScope SymbolScope() =>
        mySolution.GetPsiServices().Symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true);

    public static ITypeMember? ResolveMember(ITypeElement type, GraphUsage usage)
    {
        var name = usage.PlainMemberName;
        foreach (var (t, substitution) in SelfAndSuperTypes(type))
        {
            if (!t.HasMemberWithName(name, ignoreCase: false))
                continue;
            foreach (var m in t.GetMembers())
            {
                if (m.ShortName != name)
                    continue;
                switch (usage.Kind)
                {
                    case UsageKind.Field when m is IField:
                        return m;
                    case UsageKind.Field:
                        continue;
                }

                if (usage.IsPropertyAccessor)
                {
                    if (m is IProperty { Parameters.Count: 0 })
                        return m;
                }
                else if (m is IMethod method && ParametersMatch(method, usage.ParamsInfo, substitution))
                {
                    return m;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The type and its supertypes, each with the substitution of its type parameters as seen from
    /// <paramref name="type"/>: reflection on <c>Bot : Singleton&lt;Bot&gt;</c> writes <c>Demo.Bot</c>, not <c>T</c>.
    /// </summary>
    private static IEnumerable<(ITypeElement Type, ISubstitution Substitution)> SelfAndSuperTypes(ITypeElement type)
    {
        var visited = new HashSet<ITypeElement>();
        var queue = new Queue<(ITypeElement, ISubstitution)>();
        queue.Enqueue((type, EmptySubstitution.INSTANCE));
        while (queue.Count > 0)
        {
            var (t, substitution) = queue.Dequeue();
            if (!visited.Add(t))
                continue;
            yield return (t, substitution);
            // base class first, so the nearest declaration wins as in Type.GetMethod
            var supers = new List<(ITypeElement, ISubstitution)>();
            foreach (var super in t.GetSuperTypes())
            {
                var resolved = (substitution.Apply(super) as IDeclaredType ?? super).Resolve();
                if (resolved.DeclaredElement is ITypeElement element)
                    supers.Add((element, resolved.Substitution));
            }

            foreach (var super in supers.OrderBy(s => s.Item1 is IInterface ? 1 : 0))
                queue.Enqueue(super);
        }
    }

    /// <param name="substitution">Type arguments of the type the method is reached through (see <see cref="SelfAndSuperTypes"/>).</param>
    public static bool ParametersMatch(IMethod method, string? paramsInfo, ISubstitution? substitution = null)
    {
        var expected = string.IsNullOrEmpty(paramsInfo) ? Array.Empty<string>() : paramsInfo!.Split('|');
        var parameters = method.Parameters;
        if (parameters.Count != expected.Length)
            return false;
        for (var i = 0; i < expected.Length; i++)
        {
            var parameterType = parameters[i].Type;
            var actual = ClrName(substitution?.Apply(parameterType) ?? parameterType);
            if (parameters[i].Kind is ParameterKind.REFERENCE or ParameterKind.OUTPUT or ParameterKind.READONLY_REFERENCE)
                actual += "&";
            if (actual != StripAssemblies(expected[i]))
                return false;
        }

        return true;
    }

    /// <summary><c>System.Collections.Generic.List`1[[Ns.Pawn, Asm, Version=…]]</c> → <c>System.Collections.Generic.List`1[[Ns.Pawn]]</c>.</summary>
    public static string StripAssemblies(string clrName) => ourAssemblyQualifier.Replace(clrName.Trim(), "");

    /// <summary>The CLR name of a type as <c>System.Type.FullName</c> spells it, without assembly qualifiers.</summary>
    public static string ClrName(IType type)
    {
        switch (type)
        {
            case IArrayType array:
                return ClrName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
            case IPointerType pointer:
                return ClrName(pointer.ElementType) + "*";
            case IDeclaredType declared:
            {
                var resolve = declared.Resolve();
                if (resolve.DeclaredElement is ITypeParameter typeParameter)
                    return typeParameter.ShortName;
                var element = declared.GetTypeElement();
                if (element == null)
                    return declared.GetClrName().FullName;
                var sb = new StringBuilder(element.GetClrName().FullName);
                var typeParameters = AllTypeParameters(element);
                if (typeParameters.Count > 0)
                {
                    sb.Append('[');
                    for (var i = 0; i < typeParameters.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        sb.Append('[').Append(ClrName(resolve.Substitution[typeParameters[i]])).Append(']');
                    }

                    sb.Append(']');
                }

                return sb.ToString();
            }
            default:
                return type.GetPresentableName(JetBrains.ReSharper.Psi.CSharp.CSharpLanguage.Instance!);
        }
    }

    private static List<ITypeParameter> AllTypeParameters(ITypeElement element)
    {
        var chain = new List<ITypeElement>();
        for (var t = element; t != null; t = t.GetContainingType())
            chain.Insert(0, t);
        return chain.SelectMany(t => t.TypeParameters).ToList();
    }
}
