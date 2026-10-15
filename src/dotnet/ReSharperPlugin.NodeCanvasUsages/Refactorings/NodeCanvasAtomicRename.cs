using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.Application.Progress;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.Diagnostics;
using JetBrains.DocumentModel;
using JetBrains.IDE;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Specific.Rename;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Pointers;
using JetBrains.ReSharper.Refactorings.Rename;
using JetBrains.Util;
using JetBrains.Util.Logging;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Search;

namespace ReSharperPlugin.NodeCanvasUsages.Refactorings;

/// <summary>
/// Adds graph updates to Rider's Rename of methods, properties, fields, types and namespaces, the way
/// resharper-unity's <c>UnityAssetAtomicRenameFactory</c> does for UnityEvent handlers.
/// </summary>
[ShellFeaturePart]
public class NodeCanvasAtomicRenameFactory : IAtomicRenameFactory
{
    public bool IsApplicable(IDeclaredElement declaredElement)
    {
        var solution = declaredElement.GetSolution();
        var cache = solution.TryGetComponent<NodeCanvasGraphCache>();
        if (cache == null || !cache.IsEnabled)
            return false;

        switch (declaredElement)
        {
            case ITypeElement:
            case INamespace:
                return cache.GraphFiles().Count > 0;
            default:
                var member = NodeCanvasUsageResolver.Normalize(declaredElement);
                if (member == null || !cache.HasMemberName(member.ShortName))
                    return false;
                // while assets are still being indexed the count is not final; let PrepareToRename decide
                return solution.GetComponent<DeferredCacheController>().IsProcessingFiles() ||
                       solution.GetComponent<NodeCanvasUsageResolver>().CountUsages(member, includeBound: true) > 0;
        }
    }

    public RenameAvailabilityCheckResult CheckRenameAvailability(IDeclaredElement element) =>
        RenameAvailabilityCheckResult.CanBeRenamed;

    public IEnumerable<AtomicRenameBase> CreateAtomicRenames(IDeclaredElement declaredElement, string newName,
                                                             bool doNotAddBindingConflicts) =>
        new[] { new NodeCanvasAtomicRename(declaredElement.GetSolution(), declaredElement, newName) };
}

/// <summary>
/// Rewrites graph scalars through decode → replace → encode (Unity's folding), so the asset is byte-for-byte what
/// Unity would write; each changed scalar becomes one text occurrence of the refactoring, narrowed to the part that
/// differs. They are applied in the refactoring's transaction (one undo) and listed in its preview.
/// <para>
/// One refactoring may rename several elements whose references share a scalar (a hierarchy, overloads, a property
/// with its field). Re-encoding a scalar changes its folding, so per-element edits of it would overlap; instead the
/// renames of one refactoring share a <see cref="Batch"/> that composes their rewrites into one edit per scalar.
/// ReSharper calls every <c>PrepareToRename</c> before the first <c>Rename</c>.
/// </para>
/// </summary>
public class NodeCanvasAtomicRename : AtomicRenameBase
{
    private static readonly ILogger ourLogger = Logger.GetLogger<NodeCanvasAtomicRename>();
    private static readonly ConditionalWeakTable<IRenameRefactoring, Batch> ourBatches = new();

    private readonly ISolution mySolution;
    private readonly IDeclaredElementPointer<IDeclaredElement> myPointer;

    public NodeCanvasAtomicRename(ISolution solution, IDeclaredElement declaredElement, string newName)
    {
        mySolution = solution;
        myPointer = declaredElement.CreateElementPointer();
        OldName = declaredElement.ShortName;
        NewName = newName;
    }

    public override string NewName { get; }
    public override string OldName { get; }

    public override IDeclaredElement NewDeclaredElement =>
        myPointer.FindDeclaredElement().NotNull("myPointer.FindDeclaredElement() != null");

    public override IDeclaredElement PrimaryDeclaredElement =>
        myPointer.FindDeclaredElement().NotNull("myPointer.FindDeclaredElement() != null");

    public override IList<IDeclaredElement>? SecondaryDeclaredElements => null;

    public override void PrepareToRename(IRenameRefactoring executer, IProgressIndicator pi,
                                         bool hasConflictsWithDeclarations, IRefactoringDriver driver)
    {
        var batch = ourBatches.GetValue(executer, _ => new Batch());
        if (batch.Applied)
            batch.Reset(); // the refactoring runs again
        batch.Plans.RemoveAll(p => p.Owner == this);

        var element = myPointer.FindDeclaredElement();
        if (element == null)
            return;
        var plan = CreatePlan(element);
        if (plan == null)
            return;
        batch.Plans.Add(plan);

        // only this plan's files change; recompute them with every plan of the refactoring that touches them
        pi.Start(plan.Files.Count);
        foreach (var file in plan.Files)
        {
            Interruption.Current.CheckAndThrow();
            using var _ = pi.CreateSubProgress(1);
            var rewrite = GraphRewriter.ComposeJson(batch.Plans.Where(p => p.Files.Contains(file)).Select(p => p.RewriteJson));
            batch.Occurrences[file] = ComputeOccurrences(file, rewrite);
        }

        ourLogger.Verbose("NodeCanvas rename {0} -> {1}: {2} graph scalar(s) in {3} file(s)", OldName, NewName,
            plan.Files.Sum(f => batch.Occurrences[f].Count), plan.Files.Count(f => batch.Occurrences[f].Count > 0));
    }

    private List<GraphTextOccurrence> ComputeOccurrences(IPsiSourceFile file, Func<string, string?> rewrite)
    {
        var result = new List<GraphTextOccurrence>();
        var document = file.Document;
        var text = document.GetText();
        List<TextEdit> edits;
        try
        {
            edits = GraphRewriter.ComputeJsonEdits(text, rewrite);
        }
        catch (FormatException e)
        {
            ourLogger.Warn(e, $"NodeCanvas rename: cannot rewrite {file.GetLocation()}");
            return result;
        }

        foreach (var edit in edits)
        {
            var trimmed = edit.Trim(text);
            result.Add(new GraphTextOccurrence(mySolution, file, document, new TextRange(trimmed.Start, trimmed.End),
                text.Substring(trimmed.Start, trimmed.End - trimmed.Start), trimmed.NewText));
        }

        return result;
    }

    public override void Rename(IRenameRefactoring executer, IProgressIndicator pi, bool hasConflictsWithDeclarations,
                                IRefactoringDriver driver, PreviousAtomicRenames previousAtomicRenames)
    {
        // the first rename of the refactoring adds the occurrences of all of them
        if (!ourBatches.TryGetValue(executer, out var batch) || batch.Applied)
            return;
        batch.Applied = true;
        var occurrences = batch.Occurrences.Values.SelectMany(o => o).ToList();
        if (occurrences.Count == 0)
            return;
        var workflow = (executer.Workflow as RenameWorkflowBase).NotNull("workflow != null");
        foreach (var occurrence in occurrences)
            workflow.DataModel.AddExtraTextOccurrence(occurrence);

        // code and graphs must reach Unity together (see GraphDocumentSaver)
        mySolution.GetComponent<GraphDocumentSaver>().RequestSaveAll("rename");
    }

    private sealed class Plan
    {
        public Plan(NodeCanvasAtomicRename owner, IReadOnlyList<IPsiSourceFile> files, Func<string, string?> rewriteJson)
        {
            Owner = owner;
            Files = new HashSet<IPsiSourceFile>(files);
            RewriteJson = rewriteJson;
        }

        public NodeCanvasAtomicRename Owner { get; }
        public HashSet<IPsiSourceFile> Files { get; }
        public Func<string, string?> RewriteJson { get; }
    }

    /// <summary>The graph rewrites of all NodeCanvas renames of one refactoring.</summary>
    private sealed class Batch
    {
        public List<Plan> Plans { get; } = new();
        public Dictionary<IPsiSourceFile, List<GraphTextOccurrence>> Occurrences { get; } = new();
        public bool Applied { get; set; }

        public void Reset()
        {
            Plans.Clear();
            Occurrences.Clear();
            Applied = false;
        }
    }

    private Plan? CreatePlan(IDeclaredElement element)
    {
        var cache = mySolution.GetComponent<NodeCanvasGraphCache>();
        switch (element)
        {
            case ITypeElement type:
            {
                var oldName = type.GetClrName().FullName;
                var newName = RenamedClrName(oldName, NewName);
                return new Plan(this, FilesContaining(cache, oldName), TypeNames(GraphRewriter.RenameType(oldName, newName)));
            }
            case INamespace ns:
            {
                var oldName = ns.QualifiedName;
                var dot = oldName.LastIndexOf('.');
                var newName = dot < 0 ? NewName : oldName.Substring(0, dot + 1) + NewName;
                return new Plan(this, FilesContaining(cache, oldName), TypeNames(GraphRewriter.RenameNamespace(oldName, newName)));
            }
            default:
            {
                var member = NodeCanvasUsageResolver.Normalize(element);
                if (member == null)
                    return null;
                // the graphs follow the code whatever the "include bound graphs" display setting says
                var usages = mySolution.GetComponent<NodeCanvasUsageResolver>().FindUsages(member, includeBound: true);
                if (usages.Count == 0)
                    return null;
                var targets = new HashSet<(string, string, string)>(usages.Select(u =>
                    (u.Usage.TypeName, u.Usage.MemberName, u.Usage.ParamsInfo ?? "")));
                var newName = NewName;
                return new Plan(this, usages.Select(u => u.SourceFile).Distinct().ToList(),
                    json => GraphRewriter.RenameReferences(json,
                        r => targets.Contains((r.TypeName, r.MemberName, r.ParamsInfo ?? "")), newName));
            }
        }
    }

    /// <summary>A JSON rewrite of the graphs' type names only (never node names, text or assembly names).</summary>
    internal static Func<string, string?> TypeNames(Func<string, string> rewrite) =>
        json => GraphRewriter.RewriteTypeNames(json, rewrite);

    internal static IReadOnlyList<IPsiSourceFile> FilesContaining(NodeCanvasGraphCache cache, string name) =>
        cache.GraphFiles().Where(f => f.Document.GetText().IndexOf(name, StringComparison.Ordinal) >= 0).ToList();

    /// <summary><c>Ns.Outer+Old`1</c> renamed to <c>New</c> → <c>Ns.Outer+New`1</c>.</summary>
    public static string RenamedClrName(string clrName, string newShortName)
    {
        var start = clrName.LastIndexOfAny(new[] { '.', '+' }) + 1;
        var tick = clrName.IndexOf('`', start);
        var suffix = tick < 0 ? "" : clrName.Substring(tick);
        return clrName.Substring(0, start) + newShortName + suffix;
    }
}

/// <summary>One changed graph scalar, as a text occurrence of the rename.</summary>
public sealed class GraphTextOccurrence : ITextOccurrenceRenameMarker
{
    private readonly ISolution mySolution;
    private readonly IDocument myDocument;
    private readonly RangeMarker myRangeMarker;

    public GraphTextOccurrence(ISolution solution, IPsiSourceFile sourceFile, IDocument document, TextRange range,
                               string oldText, string newText)
    {
        mySolution = solution;
        SourceFile = sourceFile;
        myDocument = document;
        myRangeMarker = new RangeMarker(document, range);
        OldName = oldText;
        NewName = newText;
        PresentationOptions = OccurrencePresentationOptions.DefaultOptions;
    }

    public IPsiSourceFile SourceFile { get; }

    public bool Navigate(ISolution solution, PopupWindowContextSource windowContext, bool transferFocus,
                         TabOptions tabOptions = TabOptions.Default)
    {
        var navigation = solution.TryGetComponent<IGraphUsageNavigation>();
        navigation?.Navigate(SourceFile, myRangeMarker.Range.StartOffset, transferFocus);
        return navigation != null;
    }

    public ISolution GetSolution() => mySolution;
    public string DumpToString() => $"{SourceFile.GetLocation().Name} {OldNameRange}: {OldName} -> {NewName}";
    public OccurrenceType OccurrenceType => OccurrenceType.TextualOccurrence;
    public bool IsValid => myRangeMarker.IsValid && SourceFile.IsValid();
    public OccurrencePresentationOptions PresentationOptions { get; set; }
    public IDocument GetDocument() => myDocument;

    public string NewName { get; set; }

    // unchecking the asset in the preview leaves it as it is
    public bool Included { get; set; } = true;

    public string OldName { get; }
    public TextRange OldNameRange => myRangeMarker.Range;
}
