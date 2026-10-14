namespace PipeSizer.Sizing;

/// <summary>Fuel gas carried by a gas piping system.</summary>
public enum GasFuel
{
    NaturalGas,
    Propane,
}

/// <summary>
/// Pressure class of a gas piping system. "Low" is a sub-2 psi system (appliance
/// pressure, e.g. 11 in. w.c. propane or &lt; 2 psi natural gas). "TwoPsi" is a 2 psi
/// system that drops to appliance pressure through a line pressure regulator.
/// </summary>
public enum GasPressure
{
    Low,
    TwoPsi,
}

/// <summary>Piping material for a gas system.</summary>
public enum GasMaterial
{
    /// <summary>Schedule 40 metallic pipe (IFGC Tables 402.4(2), (5), (27), (28)).</summary>
    Metallic,
    /// <summary>Corrugated stainless steel tubing, sized by EHD (Tables 402.4(15), (18), (32), (33)).</summary>
    Csst,
    /// <summary>Polyethylene plastic pipe (Tables 402.4(21), (22), (35), (36)).</summary>
    Pe,
}

/// <summary>
/// Which gas sizing table applies to a system: fuel × pressure × material.
/// Defaults (NaturalGas / Low / Metallic) reproduce the original v1 behavior
/// (IFGC Table 402.4(2)).
/// </summary>
public sealed record GasConfig(GasFuel Fuel, GasPressure Pressure, GasMaterial Material)
{
    public static readonly GasConfig Default = new(GasFuel.NaturalGas, GasPressure.Low, GasMaterial.Metallic);

    /// <summary>True for CSST: sizes are EHD flow designations, not nominal inches.</summary>
    public bool IsCsst => Material == GasMaterial.Csst;

    /// <summary>Short label for panel messages, e.g. "Propane / 2 psi / CSST".</summary>
    public string Describe() =>
        (Fuel == GasFuel.NaturalGas ? "Natural gas" : "Propane") + " / " +
        (Pressure == GasPressure.Low ? "Low (< 2 psi)" : "2 psi") + " / " +
        (Material == GasMaterial.Metallic ? "Metallic" : Material == GasMaterial.Csst ? "CSST" : "PE pipe");
}
