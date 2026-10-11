using System.Collections.Generic;
using JetBrains.Application.Parts;
using JetBrains.Application.UI.Controls.JetPopupMenu;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.DocumentManagers;
using JetBrains.IDE;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Feature.Services.Occurrences.OccurrenceInformation;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Pointers;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.UI.RichText;
using JetBrains.Util;
using JetBrains.Util.Media;
using NodeCanvasUsages.Core.Model;

namespace ReSharperPlugin.NodeCanvasUsages.Search;

/// <summary>A graph reference found by <see cref="NodeCanvasReferenceSearcher"/>.</summary>
public sealed class NodeCanvasFindResult : FindResult
{
    public NodeCanvasFindResult(IPsiSourceFile sourceFile, IDeclaredElement element, GraphUsage usage)
    {
        SourceFile = sourceFile;
        DeclaredElementPointer = element.CreateElementPointer();
        Usage = usage;
    }

    public IPsiSourceFile SourceFile { get; }
    public IDeclaredElementPointer<IDeclaredElement> DeclaredElementPointer { get; }
    public GraphUsage Usage { get; }

    public override bool Equals(object? obj) =>
        obj is NodeCanvasFindResult other && Equals(SourceFile, other.SourceFile) && Usage.Equals(other.Usage);

    public override int GetHashCode() => SourceFile.GetHashCode() * 397 ^ Usage.GetHashCode();
}

/// <summary>Find Usages entry for a graph reference: <c>Bot_BT #5 — ActionNode "Shoot"</c>.</summary>
public sealed class NodeCanvasOccurrence : IOccurrence
{
    public NodeCanvasOccurrence(IPsiSourceFile sourceFile, IDeclaredElementPointer<IDeclaredElement> element,
                                GraphUsage usage)
    {
        SourceFile = sourceFile;
        DeclaredElementPointer = element;
        Usage = usage;
        PresentationOptions = OccurrencePresentationOptions.DefaultOptions;
    }

    public IPsiSourceFile SourceFile { get; }
    public IDeclaredElementPointer<IDeclaredElement> DeclaredElementPointer { get; }
    public GraphUsage Usage { get; }

    public bool Navigate(ISolution solution, PopupWindowContextSource windowContext, bool transferFocus,
                         TabOptions tabOptions = TabOptions.Default)
    {
        var navigation = solution.TryGetComponent<IGraphUsageNavigation>();
        navigation?.Navigate(SourceFile, Usage.Offset, transferFocus);
        return navigation != null;
    }

    public ISolution GetSolution() => SourceFile.GetSolution();

    public string DumpToString() => $"{SourceFile.GetLocation().Name}: {Usage}";

    public OccurrenceType OccurrenceType => OccurrenceType.Occurrence;
    public bool IsValid => SourceFile.IsValid();
    public OccurrencePresentationOptions PresentationOptions { get; set; }

    public override string ToString() => Usage.Presentation;
}

/// <summary>Opens a file at an offset; implemented on the Rider side.</summary>
public interface IGraphUsageNavigation
{
    void Navigate(IPsiSourceFile sourceFile, int offset, bool activate);
}

public static class NodeCanvasOccurrenceKinds
{
    public static readonly OccurrenceKind Graphs = OccurrenceKind.CreateSemantic("NodeCanvas graphs");
}

[OccurrenceProvider(Priority = 10)]
public class NodeCanvasOccurrenceProvider : IOccurrenceProvider
{
    public IOccurrence? MakeOccurrence(FindResult findResult) =>
        findResult is NodeCanvasFindResult r ? new NodeCanvasOccurrence(r.SourceFile, r.DeclaredElementPointer, r.Usage) : null;
}

[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasOccurrenceKindProvider : IOccurrenceKindProvider
{
    public ICollection<OccurrenceKind> GetOccurrenceKinds(IOccurrence occurrence) =>
        occurrence is NodeCanvasOccurrence ? new[] { NodeCanvasOccurrenceKinds.Graphs } : EmptyList<OccurrenceKind>.Instance;

    public IEnumerable<OccurrenceKind> GetAllPossibleOccurrenceKinds() => new[] { NodeCanvasOccurrenceKinds.Graphs };
}

[OccurrencePresenter(Priority = 10.0)]
public class NodeCanvasOccurrencePresenter : IOccurrencePresenter
{
    public bool IsApplicable(IOccurrence occurrence) => occurrence is NodeCanvasOccurrence;

    public bool Present(IMenuItemDescriptor descriptor, IOccurrence occurrence,
                        OccurrencePresentationOptions occurrencePresentationOptions)
    {
        if (occurrence is not NodeCanvasOccurrence o)
            return false;
        var usage = o.Usage;
        var text = new RichText(usage.Presentation);
        var detail = $"  {usage.Kind.ToString().ToLowerInvariant()} {usage.TypeName.Substring(usage.TypeName.LastIndexOfAny(new[] { '.', '+' }) + 1)}.{usage.MemberName}";
        if (usage.Agent != null)
            detail += $" @{usage.Agent}";
        if (usage.MissingType != null)
            detail += $" (missing {GraphScanner.ShortTypeName(usage.MissingType)})";
        text.Append(detail, TextStyle.FromForeColor(JetRgbaColors.DarkGray));
        descriptor.Text = text;
        descriptor.ShortcutText = new RichText(o.SourceFile.GetLocation().Name, TextStyle.FromForeColor(JetRgbaColors.DarkGray));
        return true;
    }
}

[SolutionFeaturePart(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasOccurrenceInformationProvider : IOccurrenceInformationProvider2
{
    public IDeclaredElementEnvoy? GetTypeMember(IOccurrence occurrence) => null;
    public IDeclaredElementEnvoy? GetTypeElement(IOccurrence occurrence) => null;
    public IDeclaredElementEnvoy? GetNamespace(IOccurrence occurrence) => null;
    public OccurrenceMergeContext GetMergeContext(IOccurrence occurrence) => new(occurrence);
    // Rider shows no preview for usages in Unity YAML assets (the Unity plugin's own asset usages have none either)
    public TextRange GetTextRange(IOccurrence occurrence) => TextRange.InvalidRange;

    /// <summary>
    /// Assets are not project files, but Rider maps them to a misc-files project item. Without it the usages count as
    /// out of the "Solution" scope in Show Usages (as resharper-unity's <c>UnityAssetOccurenceInfoProvider</c> notes).
    /// </summary>
    public ProjectModelElementEnvoy? GetProjectModelElementEnvoy(IOccurrence occurrence)
    {
        if (occurrence is not NodeCanvasOccurrence o || !o.SourceFile.IsValid())
            return null;
        var mapping = o.SourceFile.GetSolution().GetComponent<DocumentToProjectFileMappingStorage>();
        var projectFile = mapping.TryGetProjectFile(o.SourceFile.Document);
        return projectFile != null ? ProjectModelElementEnvoy.Create(projectFile) : null;
    }

    public SourceFilePtr GetSourceFilePtr(IOccurrence occurrence) =>
        (occurrence as NodeCanvasOccurrence)?.SourceFile.Ptr() ?? SourceFilePtr.Fake;

    public bool IsApplicable(IOccurrence occurrence) => occurrence is NodeCanvasOccurrence;

    public void SetTabOptions(TabOptions tabOptions, IOccurrence occurrence)
    {
    }
}
