using Autodesk.Revit.DB;

namespace PipeSizer;

/// <summary>One row in the violations grid. String properties are what the grid shows.</summary>
public sealed class PipeRow
{
    /// <summary>Revit element id (for select + zoom). Internal: the grid must not auto-generate a column for it.</summary>
    internal ElementId ElementId { get; init; } = null!;

    public long ElementIdValue { get; init; }
    public string System { get; init; } = "";
    public string Category { get; init; } = "";
    public string Flow { get; init; } = "";
    public string CurrentSize { get; init; } = "";
    public string RequiredSize { get; init; } = "";
    public string Check { get; init; } = "";
    public string Actual { get; init; } = "";
    public string Limit { get; init; } = "";
    public string Status { get; init; } = "";
    public string Note { get; init; } = "";
}
