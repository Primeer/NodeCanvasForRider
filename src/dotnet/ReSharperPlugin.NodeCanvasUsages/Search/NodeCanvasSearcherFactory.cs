using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI;
using JetBrains.ReSharper.Psi.Impl.Search.SearchDomain;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using JetBrains.Util.Logging;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;

namespace ReSharperPlugin.NodeCanvasUsages.Search;

/// <summary>
/// Adds graph references to Find Usages of C# methods, properties and fields. Asset files are searched through
/// the Unity plugin's YAML PSI files; the results come from <see cref="NodeCanvasGraphCache"/>, as
/// resharper-unity's <c>UnityYamlUsageSearchFactory</c> does with its own asset caches.
/// </summary>
[PsiSharedComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasSearcherFactory : DomainSpecificSearcherFactoryBase
{
    // The Unity plugin's trigram index for YAML assets contains only this marker, so it is the "word" every asset has
    private static readonly ILogger ourLogger = Logger.GetLogger<NodeCanvasSearcherFactory>();

    private const string AssetReferenceIdentifier = "#ASSET_REFERENCE#";

    private readonly SearchDomainFactory mySearchDomainFactory;

    public NodeCanvasSearcherFactory(SearchDomainFactory searchDomainFactory) =>
        mySearchDomainFactory = searchDomainFactory;

    public override bool IsCompatibleWithLanguage(PsiLanguageType languageType) =>
        languageType.Name == NodeCanvasGraphCache.UnityYamlLanguageName;

    public override IDomainSpecificSearcher? CreateReferenceSearcher(IDeclaredElementsSet elements,
                                                                    ReferenceSearcherParameters referenceSearcherParameters)
    {
        var solution = elements.FirstOrDefault()?.GetSolution();
        if (solution == null)
            return null;
        var resolver = solution.GetComponent<NodeCanvasUsageResolver>();
        var cache = solution.GetComponent<NodeCanvasGraphCache>();
        var includeBound = cache.IncludeBoundGraphs;

        var originals = referenceSearcherParameters.OriginalElements?
                            .SelectNotNull(e => e.GetValidDeclaredElement()).ToList()
                        ?? elements.ToList();
        var byFile = new Dictionary<IPsiSourceFile, List<(IDeclaredElement Element, GraphUsage Usage)>>();
        foreach (var element in originals)
        {
            if (!IsInteresting(element, cache))
                continue;
            foreach (var entry in resolver.FindUsages(element, includeBound))
            {
                if (!byFile.TryGetValue(entry.SourceFile, out var list))
                    byFile[entry.SourceFile] = list = new List<(IDeclaredElement, GraphUsage)>();
                list.Add((element, entry.Usage));
            }
        }

        if (byFile.Count == 0)
            return null;
        ourLogger.Verbose("NodeCanvas Find Usages: {0} reference(s) in {1} file(s) for {2}",
            byFile.Sum(f => f.Value.Count), byFile.Count, string.Join(", ", originals.Select(e => e.ShortName)));
        return new NodeCanvasReferenceSearcher(byFile);
    }

    public override IEnumerable<string> GetAllPossibleWordsInFile(IDeclaredElement element) =>
        IsInteresting(element, element.GetSolution().GetComponent<NodeCanvasGraphCache>())
            ? new[] { AssetReferenceIdentifier }
            : EmptyList<string>.Instance;

    public override ISearchDomain GetDeclaredElementSearchDomain(IDeclaredElement declaredElement)
    {
        var solution = declaredElement.GetSolution();
        var cache = solution.GetComponent<NodeCanvasGraphCache>();
        if (!IsInteresting(declaredElement, cache))
            return EmptySearchDomain.Instance;

        var member = NodeCanvasUsageResolver.Normalize(declaredElement)!;
        var files = cache.GetByMemberName(member.ShortName).Select(e => e.SourceFile).Distinct().ToList();
        return files.Count == 0 ? EmptySearchDomain.Instance : mySearchDomainFactory.CreateSearchDomain(files);
    }

    private static bool IsInteresting(IDeclaredElement element, NodeCanvasGraphCache cache)
    {
        var member = NodeCanvasUsageResolver.Normalize(element);
        return member != null && cache.IsEnabled && cache.HasMemberName(member.ShortName);
    }
}

public class NodeCanvasReferenceSearcher : IDomainSpecificSearcher
{
    private readonly Dictionary<IPsiSourceFile, List<(IDeclaredElement Element, GraphUsage Usage)>> myByFile;

    public NodeCanvasReferenceSearcher(Dictionary<IPsiSourceFile, List<(IDeclaredElement, GraphUsage)>> byFile) =>
        myByFile = byFile;

    public bool ProcessProjectItem<TResult>(IPsiSourceFile sourceFile, IFindResultConsumer<TResult> consumer)
    {
        if (!myByFile.TryGetValue(sourceFile, out var usages))
            return false;
        foreach (var (element, usage) in usages)
        {
            if (consumer.Accept(new NodeCanvasFindResult(sourceFile, element, usage)) == FindExecution.Stop)
                return true;
        }

        return false;
    }

    public bool ProcessElement<TResult>(ITreeNode element, IFindResultConsumer<TResult> consumer)
    {
        var sourceFile = element.GetSourceFile();
        return sourceFile != null && ProcessProjectItem(sourceFile, consumer);
    }
}
