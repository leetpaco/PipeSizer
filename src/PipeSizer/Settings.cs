using PipeSizer.Sizing;

namespace PipeSizer;

/// <summary>Which sizing method a system is checked with.</summary>
public enum PipeCategory
{
    Gas,
    Hydronic,
    Domestic,
    Ignore,
}

/// <summary>Panel settings (kept in memory for the session). Changing a setting re-checks.</summary>
public sealed class PipeSizerSettings
{
    public double HydronicMaxVelocityFps { get; set; } = 4;
    public double HydronicMaxFrictionPer100Ft { get; set; } = 4;
    public double DomesticMaxVelocityFps { get; set; } = 8;
    public double DomesticMaxFrictionPer100Ft { get; set; } = 8;
    public double GasDesignLengthFt { get; set; } = 100;
    public GasFuel GasFuel { get; set; } = GasFuel.NaturalGas;
    public GasPressure GasPressure { get; set; } = GasPressure.Low;
    public GasMaterial GasMaterial { get; set; } = GasMaterial.Metallic;

    /// <summary>The gas table selection implied by the three gas dropdowns.</summary>
    public GasConfig GasConfig => new(GasFuel, GasPressure, GasMaterial);
    public string HydronicMaterial { get; set; } = Materials.CopperTypeL;
    public string DomesticMaterial { get; set; } = Materials.CopperTypeL;
    public bool ShowAll { get; set; } = false;

    /// <summary>Automatic background update check (at most once per 30 min while
    /// Revit runs). Default ON. The manual ribbon/panel buttons always work
    /// regardless of this setting. Session-only, like all settings.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>Per-system category overrides (keyed by system name, case-insensitive).</summary>
    public Dictionary<string, PipeCategory> CategoryOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Manual flow overrides, session-only (keyed by ElementId value). Value is in the
    /// category's native unit: GPM for Hydronic/Domestic, CFH for natural gas, MBH for
    /// propane. Never written to the Revit model (check-only) and never persisted.
    /// </summary>
    public Dictionary<long, double> FlowOverrides { get; } = new();

    /// <summary>Water temperature for hydronic checks. Fixed at 180 F in v1 (no UI input).</summary>
    public const double HydronicWaterTempF = 180;
}
