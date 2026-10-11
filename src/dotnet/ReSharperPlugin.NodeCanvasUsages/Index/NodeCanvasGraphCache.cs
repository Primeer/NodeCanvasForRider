using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.DataFlow;
using JetBrains.DocumentManagers;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.Util;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Settings;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>A graph reference together with the asset that contains it.</summary>
public readonly struct GraphUsageEntry
{
    public GraphUsageEntry(IPsiSourceFile sourceFile, GraphUsage usage)
    {
        SourceFile = sourceFile;
        Usage = usage;
    }

    public IPsiSourceFile SourceFile { get; }
    public GraphUsage Usage { get; }
}

/// <summary>Read side of the graph usage index.</summary>
public interface IGraphUsageIndex
{
    bool IsEnabled { get; }

    /// <summary>References whose member name (without <c>get_</c>/<c>set_</c>) is <paramref name="plainMemberName"/>.</summary>
    IReadOnlyList<GraphUsageEntry> GetByMemberName(string plainMemberName);
}

/// <summary>
/// Background index of C# member references in NodeCanvas graphs, built per asset file by the platform's deferred
/// cache machinery (the same one resharper-unity uses for its YAML index). Data is persisted between sessions and
/// rebuilt only for files whose timestamp changed; edits in open documents are picked up as well.
/// Asset files come from the Unity plugin's external files module; this cache is separate from its YAML index.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasGraphCache : DeferredCacheBase<NodeCanvasFileData>, IGraphUsageIndex
{
    private static readonly HashSet<string> ourExtensions = new(StringComparer.OrdinalIgnoreCase) { "asset", "prefab", "unity" };

    private readonly NodeCanvasDetector myDetector;
    private readonly DocumentToProjectFileMappingStorage myDocuments;
    private readonly IProperty<bool> myEnabled;
    private readonly IProperty<bool> myIncludeBound;

    private readonly object myLock = new();
    private readonly Dictionary<IPsiSourceFile, NodeCanvasFileData> myFiles = new();
    private readonly Dictionary<string, List<GraphUsageEntry>> myByMember = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GraphUsageEntry>> myByType = new(StringComparer.Ordinal);
    // GraphScanner.ClrShortName → full CLR names of the types the graphs refer to → files
    private readonly Dictionary<string, Dictionary<string, List<IPsiSourceFile>>> myTypeNamesByShortName =
        new(StringComparer.Ordinal);
    private int myVersion;

    public NodeCanvasGraphCache(Lifetime lifetime, IPersistentIndexManager persistentIndexManager,
                                NodeCanvasDetector detector, DocumentToProjectFileMappingStorage documents,
                                ISettingsStore settingsStore, ISolution solution, ILogger logger)
        : base(lifetime, persistentIndexManager, NodeCanvasFileData.Marshaller, logger)
    {
        myDetector = detector;
        myDocuments = documents;
        var settings = settingsStore.BindToContextLive(lifetime, ContextRange.Smart(solution.ToDataContext()));
        myEnabled = settings.GetValueProperty(lifetime, (NodeCanvasSettings s) => s.Enabled);
        myIncludeBound = settings.GetValueProperty(lifetime, (NodeCanvasSettings s) => s.IncludeBoundGraphs);
    }

    /// <summary>Persisted data from an older plugin build is discarded when this changes.</summary>
    protected override string Version => NodeCanvasFileData.FormatVersion.ToString();

    /// <summary>Raised after the index content changed (on the thread that merged the data).</summary>
    public event Action? Changed;

    /// <summary>Increments on every change; lets consumers cache derived data.</summary>
    public int ModificationCount => myVersion;

    public bool IsEnabled => myEnabled.Value;

    /// <summary>Solution-level value of <see cref="NodeCanvasSettings.IncludeBoundGraphs"/>.</summary>
    public bool IncludeBoundGraphs => myIncludeBound.Value;

    /// <summary>Name of the PSI language the Unity plugin assigns to asset files (<c>UnityYamlLanguage.Name</c>).</summary>
    public const string UnityYamlLanguageName = "UnityYaml";

    /// <summary>
    /// A Unity YAML asset as the Unity plugin exposes it (its external files module, like its own asset index uses).
    /// The same file can also be an item of a C# project; that copy is ignored so references are not counted twice.
    /// </summary>
    public static bool IsAssetFile(IPsiSourceFile sourceFile) =>
        sourceFile is not IPsiProjectFile &&
        ourExtensions.Contains(sourceFile.GetLocation().ExtensionNoDot) &&
        sourceFile.PrimaryPsiLanguage.Name == UnityYamlLanguageName;

    public override bool IsApplicable(IPsiSourceFile sourceFile) =>
        myEnabled.Value && IsAssetFile(sourceFile) && myDetector.IsNodeCanvasProject;

    public override object Build(IPsiSourceFile sourceFile)
    {
        var location = sourceFile.GetLocation();
        var document = myDocuments.TryGetDocumentByPath(location);
        string text;
        if (document != null)
        {
            text = document.GetText();
        }
        else
        {
            var path = location.FullPath;
            if (!File.Exists(path) || !MarkerScanner.ContainsFile(path))
                return NodeCanvasFileData.Empty;
            text = File.ReadAllText(path, new UTF8Encoding(false));
        }

        if (!MarkerScanner.ContainsGraph(text))
            return NodeCanvasFileData.Empty;

        var usages = GraphScanner.Scan(text, location.Name, e => Logger.Warn(e.Message));
        var typeNames = GraphScanner.ScanTypeNames(text, location.Name);
        Logger.Verbose("NodeCanvas: scanned {0}: {1} reference(s), {2} type(s)", location.Name, usages.Count,
            typeNames.Count);
        return new NodeCanvasFileData(usages, typeNames, isGraphFile: true);
    }

    protected override void MergeData(IPsiSourceFile sourceFile, NodeCanvasFileData data)
    {
        lock (myLock)
        {
            RemoveUnderLock(sourceFile);
            if (data.IsGraphFile)
            {
                myFiles[sourceFile] = data;
                foreach (var usage in data.Usages)
                {
                    var entry = new GraphUsageEntry(sourceFile, usage);
                    Add(myByMember, usage.PlainMemberName, entry);
                    Add(myByType, usage.TypeName, entry);
                }

                foreach (var name in data.TypeNames)
                {
                    var shortName = GraphScanner.ClrShortName(name);
                    if (!myTypeNamesByShortName.TryGetValue(shortName, out var names))
                        myTypeNamesByShortName[shortName] = names = new Dictionary<string, List<IPsiSourceFile>>(StringComparer.Ordinal);
                    if (!names.TryGetValue(name, out var files))
                        names[name] = files = new List<IPsiSourceFile>();
                    files.Add(sourceFile);
                }
            }

            myVersion++;
        }

        if (data.Usages.Count > 0)
            Logger.Verbose($"NodeCanvas: {data.Usages.Count} member reference(s) in {sourceFile.GetLocation().Name}");
        Changed?.Invoke();
    }

    protected override void DropData(IPsiSourceFile sourceFile, NodeCanvasFileData data)
    {
        lock (myLock)
        {
            RemoveUnderLock(sourceFile);
            myVersion++;
        }

        Changed?.Invoke();
    }

    protected override void InvalidateData()
    {
        lock (myLock)
        {
            myFiles.Clear();
            myByMember.Clear();
            myByType.Clear();
            myTypeNamesByShortName.Clear();
            myVersion++;
        }

        Changed?.Invoke();
    }

    private static void Add(Dictionary<string, List<GraphUsageEntry>> index, string key, GraphUsageEntry entry)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = new List<GraphUsageEntry>();
        list.Add(entry);
    }

    private static void Remove(Dictionary<string, List<GraphUsageEntry>> index, IEnumerable<string> keys,
                               IPsiSourceFile sourceFile)
    {
        foreach (var key in keys.Distinct())
        {
            if (!index.TryGetValue(key, out var list))
                continue;
            list.RemoveAll(e => Equals(e.SourceFile, sourceFile));
            if (list.Count == 0)
                index.Remove(key);
        }
    }

    private void RemoveUnderLock(IPsiSourceFile sourceFile)
    {
        if (!myFiles.TryGetValue(sourceFile, out var old))
            return;
        myFiles.Remove(sourceFile);
        Remove(myByMember, old.Usages.Select(u => u.PlainMemberName), sourceFile);
        Remove(myByType, old.Usages.Select(u => u.TypeName), sourceFile);
        foreach (var name in old.TypeNames)
        {
            var shortName = GraphScanner.ClrShortName(name);
            if (!myTypeNamesByShortName.TryGetValue(shortName, out var names) || !names.TryGetValue(name, out var files))
                continue;
            files.Remove(sourceFile);
            if (files.Count == 0)
                names.Remove(name);
            if (names.Count == 0)
                myTypeNamesByShortName.Remove(shortName);
        }
    }

    /// <summary>References whose <c>_baseInfo</c> names the type <paramref name="clrTypeName"/>.</summary>
    public IReadOnlyList<GraphUsageEntry> GetByTypeName(string clrTypeName)
    {
        lock (myLock)
        {
            return myByType.TryGetValue(clrTypeName, out var list)
                ? list.Where(e => e.SourceFile.IsValid()).ToList()
                : (IReadOnlyList<GraphUsageEntry>)Array.Empty<GraphUsageEntry>();
        }
    }

    /// <summary>
    /// Full CLR names with the short name <paramref name="clrShortName"/> (<see cref="GraphScanner.ClrShortName"/>)
    /// that some graph refers to, with the files that do.
    /// </summary>
    public IReadOnlyList<(string TypeName, IReadOnlyList<IPsiSourceFile> Files)> GetTypeNamesByShortName(string clrShortName)
    {
        lock (myLock)
        {
            if (!myTypeNamesByShortName.TryGetValue(clrShortName, out var names))
                return Array.Empty<(string, IReadOnlyList<IPsiSourceFile>)>();
            return names
                .Select(n => (n.Key, (IReadOnlyList<IPsiSourceFile>)n.Value.Where(f => f.IsValid()).ToList()))
                .Where(n => n.Item2.Count > 0)
                .ToList();
        }
    }

    public IReadOnlyList<GraphUsageEntry> GetByMemberName(string plainMemberName)
    {
        lock (myLock)
        {
            return myByMember.TryGetValue(plainMemberName, out var list)
                ? list.Where(e => e.SourceFile.IsValid()).ToList()
                : (IReadOnlyList<GraphUsageEntry>)Array.Empty<GraphUsageEntry>();
        }
    }

    public bool HasMemberName(string plainMemberName)
    {
        lock (myLock)
            return myByMember.ContainsKey(plainMemberName);
    }

    public IReadOnlyList<GraphUsageEntry> AllUsages()
    {
        lock (myLock)
            return myFiles.SelectMany(f => f.Value.Usages.Select(u => new GraphUsageEntry(f.Key, u))).ToList();
    }

    /// <summary>Every file that holds a serialized graph.</summary>
    public IReadOnlyList<IPsiSourceFile> GraphFiles()
    {
        lock (myLock)
            return myFiles.Keys.Where(f => f.IsValid()).ToList();
    }
}
