using System;
using System.Collections.Generic;
using System.Linq;
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
using NodeCanvasUsages.Core.Json;
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
/// </summary>
public class NodeCanvasAtomicRename : AtomicRenameBase
{
    private static readonly ILogger ourLogger = Logger.GetLogger<NodeCanvasAtomicRename>();

    private readonly ISolution mySolution;
    private readonly IDeclaredElementPointer<IDeclaredElement> myPointer;
    private readonly List<GraphTextOccurrence> myOccurrences = new();

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
        myOccurrences.Clear();
        var element = myPointer.FindDeclaredElement();
        if (element == null)
            return;

        var plan = CreatePlan(element);
        if (plan == null)
            return;

        pi.Start(plan.Value.Files.Count);
        foreach (var file in plan.Value.Files)
        {
            Interruption.Current.CheckAndThrow();
            using var _ = pi.CreateSubProgress(1);
            var document = file.Document;
            var text = document.GetText();
            List<TextEdit> edits;
            try
            {
                edits = GraphRewriter.ComputeJsonEdits(text, plan.Value.RewriteJson);
            }
            catch (FormatException e)
            {
                ourLogger.Warn(e, $"NodeCanvas rename: cannot rewrite {file.GetLocation()}");
                continue;
            }

            foreach (var edit in edits)
            {
                var trimmed = edit.Trim(text);
                myOccurrences.Add(new GraphTextOccurrence(mySolution, file, document,
                    new TextRange(trimmed.Start, trimmed.End), text.Substring(trimmed.Start, trimmed.End - trimmed.Start),
                    trimmed.NewText));
            }
        }

        ourLogger.Verbose("NodeCanvas rename {0} -> {1}: {2} graph scalar(s) in {3} file(s)", OldName, NewName,
            myOccurrences.Count, myOccurrences.Select(o => o.SourceFile).Distinct().Count());
    }

    public override void Rename(IRenameRefactoring executer, IProgressIndicator pi, bool hasConflictsWithDeclarations,
                                IRefactoringDriver driver, PreviousAtomicRenames previousAtomicRenames)
    {
        if (myOccurrences.Count == 0)
            return;
        var workflow = (executer.Workflow as RenameWorkflowBase).NotNull("workflow != null");
        foreach (var occurrence in myOccurrences)
            workflow.DataModel.AddExtraTextOccurrence(occurrence);

        // code and graphs must reach Unity together (see GraphDocumentSaver)
        mySolution.GetComponent<GraphDocumentSaver>().RequestSaveAll("rename");
    }

    private (IReadOnlyList<IPsiSourceFile> Files, Func<string, string?> RewriteJson)? CreatePlan(IDeclaredElement element)
    {
        var cache = mySolution.GetComponent<NodeCanvasGraphCache>();
        switch (element)
        {
            case ITypeElement type:
            {
                var oldName = type.GetClrName().FullName;
                var newName = RenamedClrName(oldName, NewName);
                return (FilesContaining(cache, oldName), WholeStrings(GraphRewriter.RenameType(oldName, newName)));
            }
            case INamespace ns:
            {
                var oldName = ns.QualifiedName;
                var dot = oldName.LastIndexOf('.');
                var newName = dot < 0 ? NewName : oldName.Substring(0, dot + 1) + NewName;
                return (FilesContaining(cache, oldName), WholeStrings(GraphRewriter.RenameNamespace(oldName, newName)));
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
                return (usages.Select(u => u.SourceFile).Distinct().ToList(),
                    json => GraphRewriter.RenameReferences(json,
                        r => targets.Contains((r.TypeName, r.MemberName, r.ParamsInfo ?? "")), newName));
            }
        }
    }

    internal static Func<string, string?> WholeStrings(Func<string, string> rewrite) =>
        json => FsJson.RewriteStrings(json, rewrite);

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

    public bool Included
    {
        get => true;
        set { }
    }

    public string OldName { get; }
    public TextRange OldNameRange => myRangeMarker.Range;
}
