using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.DocumentManagers.Transactions;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.TextControl;
using JetBrains.Util;
using JetBrains.Util.Logging;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Refactorings;

namespace ReSharperPlugin.NodeCanvasUsages.Daemon;

[RegisterConfigurableSeverity(SeverityId, null, HighlightingGroupIds.CodeSmell, Title, Description, Severity.WARNING)]
[ConfigurableSeverityHighlighting(SeverityId, CSharpLanguage.Name, OverlapResolve = OverlapResolveKind.NONE)]
public class MovedGraphTypeWarning : IHighlighting
{
    public const string SeverityId = "NodeCanvasMovedGraphType";
    private const string Title = "NodeCanvas graph references this type by an old name";
    private const string Description =
        "A NodeCanvas graph refers to a type that no longer exists and has the same name as this one, for example " +
        "after Move to namespace or Move type to outer scope, which do not update graphs. The quick-fix rewrites the " +
        "graphs to this type's full name.";

    private readonly ITreeNode myNode;
    private readonly DocumentRange myRange;

    public MovedGraphTypeWarning(ITreeNode node, DocumentRange range, string message, string oldName, string newName)
    {
        myNode = node;
        myRange = range;
        ToolTip = message;
        OldName = oldName;
        NewName = newName;
    }

    /// <summary>The CLR name the graphs use.</summary>
    public string OldName { get; }

    /// <summary>The CLR name of the declared type.</summary>
    public string NewName { get; }

    public string ToolTip { get; }
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => myNode.IsValid();
    public DocumentRange CalculateRange() => myRange;
}

/// <summary>
/// Move to namespace and Move type to outer scope have no participant extension point (unlike Rename), so graphs
/// keep the old full name. This finds graph type names (<c>$type</c>, <c>_baseInfo</c>, generic arguments, ...) that
/// no longer resolve but share the short name (with generic arity) of a declared type, and offers to rewrite them.
/// </summary>
[ElementProblemAnalyzer(typeof(ICSharpTypeDeclaration))]
public class MovedGraphTypeAnalyzer : ElementProblemAnalyzer<ICSharpTypeDeclaration>
{
    private const int MaxLocations = 3;
    private readonly NodeCanvasGraphCache myCache;
    private readonly NodeCanvasUsageResolver myResolver;

    public MovedGraphTypeAnalyzer(NodeCanvasGraphCache cache, NodeCanvasUsageResolver resolver)
    {
        myCache = cache;
        myResolver = resolver;
    }

    protected override void Run(ICSharpTypeDeclaration element, ElementProblemAnalyzerData data,
                                IHighlightingConsumer consumer)
    {
        var type = element.DeclaredElement;
        if (type == null || !myCache.IsEnabled)
            return;
        // report a partial type once
        if (!Equals(type.GetDeclarations().FirstOrDefault(), element))
            return;

        var newName = type.GetClrName().FullName;
        foreach (var (oldName, files) in myCache.GetTypeNamesByShortName(GraphScanner.ClrShortName(newName)))
        {
            if (oldName == newName || myResolver.TypeExists(oldName))
                continue;
            var locations = files.Take(MaxLocations).Select(f => f.GetLocation().NameWithoutExtension).ToList();
            if (files.Count > MaxLocations)
                locations.Add($"and {files.Count - MaxLocations} more");
            var message = $"NodeCanvas graphs reference missing type {oldName}; if {type.ShortName} was moved, " +
                          $"update them to {newName}: {string.Join(", ", locations)}";
            consumer.AddHighlighting(new MovedGraphTypeWarning(element, element.GetNameDocumentRange(), message,
                oldName, newName));
        }
    }
}

/// <summary>
/// Rewrites the old full name to the declared one in every graph, the same way Rename of a type does (nested types
/// and generic arguments included), as one undoable command; untouched files stay byte-identical.
/// </summary>
[QuickFix]
public class UpdateMovedGraphTypeFix : QuickFixBase
{
    private static readonly ILogger ourLogger = Logger.GetLogger<UpdateMovedGraphTypeFix>();
    private readonly MovedGraphTypeWarning myWarning;

    public UpdateMovedGraphTypeFix(MovedGraphTypeWarning warning) => myWarning = warning;

    public override string Text => $"Update NodeCanvas graphs: {myWarning.OldName} → {myWarning.NewName}";

    public override bool IsAvailable(IUserDataHolder cache) => myWarning.IsValid();

    protected override Action<ITextControl>? ExecutePsiTransaction(ISolution solution, IProgressIndicator progress) =>
        null;

    // the assets are plain documents, edited under the document transaction of the action
    protected override Action<ITextControl>? ExecuteAfterPsiTransaction(ISolution solution,
                                                                        IProjectModelTransactionCookie cookie,
                                                                        IProgressIndicator progress)
    {
        var cache = solution.GetComponent<NodeCanvasGraphCache>();
        var rewrite = NodeCanvasAtomicRename.WholeStrings(GraphRewriter.RenameType(myWarning.OldName, myWarning.NewName));
        var changed = 0;
        foreach (var file in NodeCanvasAtomicRename.FilesContaining(cache, myWarning.OldName))
        {
            var document = file.Document;
            var text = document.GetText();
            List<TextEdit> edits;
            try
            {
                edits = GraphRewriter.ComputeJsonEdits(text, rewrite);
            }
            catch (FormatException e)
            {
                ourLogger.Warn($"NodeCanvas: cannot rewrite {file.GetLocation()}: {e.Message}");
                continue;
            }

            // from the end, so earlier offsets stay valid
            using (WriteLockCookie.Create())
            {
                foreach (var edit in edits.OrderByDescending(e => e.Start))
                {
                    var trimmed = edit.Trim(text);
                    document.ReplaceText(new TextRange(trimmed.Start, trimmed.End), trimmed.NewText);
                }
            }

            if (edits.Count > 0)
                changed++;
        }

        ourLogger.Verbose($"NodeCanvas: {myWarning.OldName} -> {myWarning.NewName} in {changed} file(s)");
        if (changed > 0)
            solution.GetComponent<GraphDocumentSaver>().RequestSaveAll("moved type");
        return null;
    }
}
