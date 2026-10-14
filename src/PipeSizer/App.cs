using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace PipeSizer;

/// <summary>
/// Add-in entry point. Registers the dockable pane + ribbon button, then stays out of the way:
/// model changes only record changed element ids; the Idling handler debounces them and
/// raises one ExternalEvent, whose handler does the actual check + select/zoom on the
/// Revit thread. The add-in never modifies the model (check-only).
///
/// Perf notes: auto re-checks only run when a change actually touches something that
/// can affect pipe sizing (pipes, equipment/fixtures/fittings, systems, pipe types)
/// — moving a wall or editing text does not re-trigger a full check. Changes are
/// debounced (~1.5 s after the last change) and coalesced, and a check never
/// overlaps another one.
/// </summary>
public class App : IExternalApplication
{
    public static readonly Guid PaneGuid = new("5876342f-20d0-4042-8775-59b9ba125872");
    public static DockablePaneId PaneId { get; } = new(PaneGuid);

    private static PipeSizerPanel? _panel;
    private static ExternalEvent? _checkEvent;
    private static readonly object Sync = new();
    private static bool _dirty;
    private static DateTime _lastChange = DateTime.MinValue;
    private static bool _manualCheckRequested;
    private static bool _autoCheckPending;
    private static bool _checkRunning;
    private static bool _paused;
    private static ElementId? _pendingSelect;
    private static bool _pendingSelectZoom;
    private static readonly HashSet<ElementId> _changedAdded = new();
    private static readonly HashSet<ElementId> _changedModified = new();
    private static readonly HashSet<ElementId> _changedDeleted = new();

    /// <summary>Debounce: wait this long after the last model change before re-checking.</summary>
    private const double DebounceMs = 1500;

    internal static PipeSizerPanel? Panel => _panel;

    public Result OnStartup(UIControlledApplication app)
    {
        // Auto-update check runs FIRST, before any Sizing type is touched: the
        // updater may overwrite PipeSizer.Sizing.dll, which is only safe while
        // it is not yet loaded. Never let the check block startup.
        string? updateNotice = null;
        try
        {
            var outcome = Updater.RunStartupUpdate();
            updateNotice = outcome.Notice;
        }
        catch { /* never block startup on update */ }

        _panel = new PipeSizerPanel();
        if (updateNotice is not null)
            _panel.ShowUpdateNotice(updateNotice);
        app.RegisterDockablePane(PaneId, "PipeSizer", _panel);

        // Ribbon button is convenience only: if it fails (e.g. a panel-name
        // collision), the add-in still loads and the pane still registers.
        try
        {
            var ribbon = app.CreateRibbonPanel("PipeSizer", "PipeSizer");
            var buttonData = new PushButtonData(
                "ShowPipeSizer",
                "Pipe\nSizer",
                Assembly.GetExecutingAssembly().Location,
                typeof(ShowPaneCommand).FullName!);
            ribbon.AddItem(buttonData);
        }
        catch
        {
            // Pane remains usable via Revit's dockable-pane handling.
        }

        _checkEvent = ExternalEvent.Create(new CheckEventHandler());
        app.ControlledApplication.DocumentChanged += OnDocumentChanged;
        app.Idling += OnIdling;
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        try { app.ControlledApplication.DocumentChanged -= OnDocumentChanged; } catch { /* already gone */ }
        try { app.Idling -= OnIdling; } catch { /* already gone */ }
        return Result.Succeeded;
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Only record ids here: never touch the model or the UI from this handler.
        // While paused, ignore changes entirely (no stale accumulation).
        lock (Sync)
        {
            if (_paused)
                return;
            foreach (var id in e.GetAddedElementIds())
                _changedAdded.Add(id);
            foreach (var id in e.GetModifiedElementIds())
                _changedModified.Add(id);
            foreach (var id in e.GetDeletedElementIds())
                _changedDeleted.Add(id);
            _dirty = true;
            _lastChange = DateTime.UtcNow;
        }
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        bool raise = false;
        lock (Sync)
        {
            if (_checkRunning)
                return; // a check is already executing; Idling will fire again after it
            if (_pendingSelect is not null)
            {
                raise = true; // retry a select whose first raise never executed
            }
            else if (!_paused && _dirty &&
                     (DateTime.UtcNow - _lastChange).TotalMilliseconds >= DebounceMs)
            {
                // Debounce + coalesce: only promote to a pending auto-check once
                // the model has been quiet for the debounce interval.
                _dirty = false;
                _autoCheckPending = true;
                raise = true;
            }
        }
        if (raise)
            _checkEvent?.Raise();
    }

    /// <summary>Called by the panel (any thread) to ask for a fresh check.
    /// Manual checks bypass the pause flag and the change filter.</summary>
    public static void RequestCheck()
    {
        lock (Sync) { _manualCheckRequested = true; }
        _checkEvent?.Raise();
    }

    /// <summary>Called by the panel when the user clicks a result row.
    /// Single-click selects without zoom; double-click selects + zooms.</summary>
    public static void RequestSelect(ElementId id, bool zoom)
    {
        lock (Sync)
        {
            _pendingSelect = id;
            _pendingSelectZoom = zoom;
        }
        _checkEvent?.Raise();
    }

    /// <summary>Pause / resume automatic (DocumentChanged-driven) checks.
    /// Manual Refresh still works while paused.</summary>
    public static void SetPaused(bool paused)
    {
        lock (Sync)
        {
            _paused = paused;
            if (paused)
            {
                // Drop pending auto work so resuming starts clean.
                _dirty = false;
                _autoCheckPending = false;
                _changedAdded.Clear();
                _changedModified.Clear();
                _changedDeleted.Clear();
            }
        }
    }

    /// <summary>On-demand update check from the panel button. Runs off the UI thread;
    /// stages (never applies in place) because Sizing is already loaded.</summary>
    public static void CheckForUpdatesOnDemand()
    {
        Task.Run(() =>
        {
            UpdateOutcome outcome;
            try
            {
                outcome = Updater.CheckAndStageForRestart();
            }
            catch (Exception ex)
            {
                outcome = UpdateOutcome.Failed(ex.Message);
            }
            try
            {
                Panel?.Dispatcher.Invoke(() =>
                    Panel.ShowUpdateNotice(outcome.Notice ?? outcome.Error ?? "No update available."));
            }
            catch { /* panel gone */ }
        });
    }

    /// <summary>
    /// Atomically takes the pending work for the event handler. Called on the Revit thread.
    /// The changed-id sets are only consumed when an auto-check is being taken, so a
    /// select-only run never discards changes that a later auto-check still needs.
    /// Also marks a check as running; <see cref="EndCheck"/> clears it.
    /// </summary>
    internal static (bool ManualCheck, bool AutoCheck, ElementId? SelectId, bool SelectZoom,
        HashSet<ElementId> Added, HashSet<ElementId> Modified, HashSet<ElementId> Deleted) TakeWork()
    {
        lock (Sync)
        {
            bool manual = _manualCheckRequested;
            bool auto = _autoCheckPending;
            var selectId = _pendingSelect;
            bool zoom = _pendingSelectZoom;
            HashSet<ElementId> added, modified, deleted;
            if (auto)
            {
                added = new HashSet<ElementId>(_changedAdded);
                modified = new HashSet<ElementId>(_changedModified);
                deleted = new HashSet<ElementId>(_changedDeleted);
                _changedAdded.Clear();
                _changedModified.Clear();
                _changedDeleted.Clear();
            }
            else
            {
                added = new HashSet<ElementId>();
                modified = new HashSet<ElementId>();
                deleted = new HashSet<ElementId>();
            }
            _manualCheckRequested = false;
            _autoCheckPending = false;
            _pendingSelect = null;
            _pendingSelectZoom = false;
            _checkRunning = true;
            return (manual, auto, selectId, zoom, added, modified, deleted);
        }
    }

    internal static void EndCheck()
    {
        lock (Sync) { _checkRunning = false; }
    }
}

/// <summary>Runs on the Revit thread: applies a pending select/zoom, then runs a check if asked.</summary>
public class CheckEventHandler : IExternalEventHandler
{
    public void Execute(UIApplication app)
    {
        try
        {
            var (manual, auto, selectId, selectZoom, added, modified, deleted) = App.TakeWork();
            var uidoc = app.ActiveUIDocument;

            if (selectId is not null && uidoc is not null)
            {
                try
                {
                    uidoc.Selection.SetElementIds(new[] { selectId });
                    if (selectZoom)
                        uidoc.ShowElements(selectId);
                }
                catch
                {
                    // Selecting is best-effort (view may be closed, element deleted, etc.).
                }
            }

            bool runCheck = false;
            if (uidoc?.Document is not null && App.Panel is not null)
            {
                if (manual)
                {
                    runCheck = true;
                }
                else if (auto)
                {
                    // Auto-checks only run when the change touched something
                    // that can affect pipe sizing. Everything else skips.
                    runCheck = AnyRelevantChange(uidoc.Document, added, modified, deleted);
                }
            }

            if (runCheck && uidoc?.Document is not null && App.Panel is not null)
            {
                var result = Checker.Check(uidoc.Document, App.Panel.Settings);
                App.Panel.UpdateResults(result);
            }
        }
        catch (Exception ex)
        {
            App.Panel?.ShowError(ex.Message);
        }
        finally
        {
            App.EndCheck();
        }
    }

    /// <summary>
    /// True when the change set includes anything that can affect pipe sizing:
    /// a pipe, equipment/fixtures/fittings (FamilyInstance), a system, or a pipe type.
    /// Deleted elements are gone so their type is unknowable — any deletion counts
    /// as relevant (conservative; deletions are rare).
    /// </summary>
    private static bool AnyRelevantChange(Document doc, HashSet<ElementId> added,
        HashSet<ElementId> modified, HashSet<ElementId> deleted)
    {
        if (deleted.Count > 0)
            return true;
        foreach (var id in added)
        {
            if (IsCheckRelevant(doc.GetElement(id)))
                return true;
        }
        foreach (var id in modified)
        {
            if (IsCheckRelevant(doc.GetElement(id)))
                return true;
        }
        return false;
    }

    private static bool IsCheckRelevant(Element? el) =>
        el is Pipe
        || el is FamilyInstance
        || el is MEPSystem
        || el is PipeType;

    public string GetName() => "PipeSizer Check";
}

/// <summary>Ribbon button: toggles the dockable pane.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ShowPaneCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var pane = commandData.Application.GetDockablePane(App.PaneId);
        if (pane.IsShown())
            pane.Hide();
        else
            pane.Show();
        return Result.Succeeded;
    }
}
