using PipeSizer.Sizing;

namespace PipeSizer;

/// <summary>
/// Manual flow overrides: the user can type a flow for a pipe in the panel
/// instead of relying on Revit's computed flow (whose system calculation is
/// bulky). Overrides are session-only, keyed by ElementId value, never written
/// to the model, and stored in the panel settings in the category's native unit:
/// GPM for Hydronic/Domestic, CFH for natural gas, MBH for propane.
/// All members are pure (no Revit types) so the console test harness can cover them.
/// </summary>
public static class FlowOverrides
{
    public const double M3sToGpm = 15850.3; // Revit stores flow internally in m3/s
    public const double M3sToCfh = 127132.8;

    /// <summary>Validates a typed flow entry: a finite positive number. Rejects empty, non-numeric, zero/negative, NaN, and Infinity.</summary>
    public static bool TryParseFlow(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!double.TryParse(text.Trim(), out double v))
            return false;
        if (!(v > 0) || !double.IsFinite(v))
            return false;
        value = v;
        return true;
    }

    /// <summary>The unit label for a category: GPM for Hydronic/Domestic, CFH for natural gas, MBH for propane.</summary>
    public static string UnitLabel(PipeCategory category, GasFuel fuel) =>
        category == PipeCategory.Gas && fuel == GasFuel.Propane ? "MBH"
        : category == PipeCategory.Gas ? "CFH"
        : "GPM";

    /// <summary>
    /// Resolves the effective flow for a pipe in the category's native unit.
    /// A valid manual override always wins over Revit's computed flow (and rescues
    /// pipes Revit never calculated a flow for). With no override, Revit's m3/s
    /// flow is converted (CFH for gas, converted to MBH for propane; GPM for
    /// hydronic/domestic). Returns null when there is no usable flow (ignored
    /// category, or no override and no Revit flow). An invalid override
    /// (non-positive / non-finite) falls through to Revit's flow.
    /// </summary>
    public static double? Resolve(double? revitFlowM3s, double? overrideNative,
        PipeCategory category, GasConfig gasConfig)
    {
        if (category == PipeCategory.Ignore)
            return null;
        if (overrideNative is { } ov && ov > 0 && double.IsFinite(ov))
            return ov;
        if (revitFlowM3s is null)
            return null;
        if (category == PipeCategory.Gas)
        {
            double cfh = revitFlowM3s.Value * M3sToCfh;
            return gasConfig.Fuel == GasFuel.Propane ? GasSizing.CfhToPropaneMbh(cfh) : cfh;
        }
        return revitFlowM3s.Value * M3sToGpm;
    }
}
