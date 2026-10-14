namespace PipeSizer.Sizing;

/// <summary>
/// Gas sizing: longest-length method, IFGC Tables 402.4(2), (5), (15), (18), (21), (22),
/// (27), (28), (32), (33), (35), (36) — see GasTables.cs for the table data and
/// GasConfig for the fuel × pressure × material selection.
///
/// All table values are in the table's native units: CFH for natural gas tables,
/// MBH (thousands of Btu per hour) for propane tables. Callers convert Revit flows
/// (CFH) to MBH for propane via CfhToPropaneMbh.
///
/// CSST sizes are EHD flow designations, not nominal inches: CapacityAt / PickSize
/// take and return EHD numbers for CSST configs. There is no nominal-to-EHD mapping
/// (EHD depends on the manufacturer), so callers must not compare a Revit nominal
/// diameter against an EHD — they report the required EHD instead.
/// </summary>
public static class GasSizing
{
    /// <summary>
    /// Propane heating value used to convert Revit CFH to the MBH the propane tables
    /// use: 2,500 Btu per cubic foot of propane vapor (standard NFPA 54 convention).
    /// This is an assumption of this tool, not a value printed in IFGC Chapter 4.
    /// MBH = CFH × 2500 / 1000 = CFH × 2.5.
    /// </summary>
    public const double PropaneBtuPerCubicFoot = 2500;

    public static double CfhToPropaneMbh(double cfh) => cfh * PropaneBtuPerCubicFoot / 1000.0;

    /// <summary>
    /// IFGC Table 402.4(2) as a GasTableDef, built from the existing GasTableData
    /// (Schedule 40 metallic, natural gas, inlet &lt; 2 psi, 0.5 in. w.c. drop, SG 0.60).
    /// Kept separate from the generated GasTables.cs so the v1 table is untouched.
    /// </summary>
    private static readonly GasTableDef Table402_4_02 = new(
        "402.4(2)",
        "Schedule 40 Metallic Pipe",
        "Natural",
        "Less than 2 psi",
        "0.5 in. w.c.",
        0.60,
        "CFH",
        "",
        "Nominal (inch)",
        GasTableData.SizesIn,
        GasTableData.LengthsFt,
        GasTableData.Table,
        "");

    /// <summary>
    /// The IFGC table for a fuel × pressure × material config, or null if no table
    /// covers that config. All 12 configs in the current fuel/pressure/material matrix
    /// have a table; null is defensive (future configs / unmapped combos).
    /// </summary>
    public static GasTableDef? TryGetTable(GasConfig config) => config switch
    {
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.Low, Material: GasMaterial.Metallic } => Table402_4_02,
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Metallic } => GasTables.Table402_4_05,
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.Low, Material: GasMaterial.Csst } => GasTables.Table402_4_15,
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Csst } => GasTables.Table402_4_18,
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.Low, Material: GasMaterial.Pe } => GasTables.Table402_4_21,
        { Fuel: GasFuel.NaturalGas, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Pe } => GasTables.Table402_4_22,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.Low, Material: GasMaterial.Metallic } => GasTables.Table402_4_28,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Metallic } => GasTables.Table402_4_27,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.Low, Material: GasMaterial.Csst } => GasTables.Table402_4_32,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Csst } => GasTables.Table402_4_33,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.Low, Material: GasMaterial.Pe } => GasTables.Table402_4_35,
        { Fuel: GasFuel.Propane, Pressure: GasPressure.TwoPsi, Material: GasMaterial.Pe } => GasTables.Table402_4_36,
        _ => null,
    };

    /// <summary>True if the size (nominal inches, or EHD for CSST) is a column in the config's table.</summary>
    public static bool IsSizeInTable(GasConfig config, double size)
    {
        var table = TryGetTable(config);
        return table is not null && Array.IndexOf(table.Sizes, size) >= 0;
    }

    /// <summary>
    /// Table capacity for a size at a design length, in the table's native units.
    /// Linear interpolation between bracketing table lengths. Lengths below the first
    /// table length use the first row (conservative). Lengths above the last table
    /// length return null (outside the table). Returns null for NA cells and for
    /// sizes that are not columns of the table.
    /// </summary>
    public static double? CapacityAt(GasConfig config, double size, double lengthFt)
    {
        var table = TryGetTable(config);
        if (table is null)
            return null;
        int idx = Array.IndexOf(table.Sizes, size);
        if (idx < 0)
            return null;
        if (lengthFt <= table.LengthsFt[0])
            return table.Table[0][idx];
        if (lengthFt > table.LengthsFt[^1])
            return null;
        for (int i = 0; i < table.LengthsFt.Length - 1; i++)
        {
            int lo = table.LengthsFt[i];
            int hi = table.LengthsFt[i + 1];
            if (lo <= lengthFt && lengthFt <= hi)
            {
                if (lengthFt == lo)
                    return table.Table[i][idx];
                if (lengthFt == hi)
                    return table.Table[i + 1][idx];
                double? capLo = table.Table[i][idx];
                double? capHi = table.Table[i + 1][idx];
                if (capLo is null || capHi is null)
                    return capLo ?? capHi;
                double frac = (lengthFt - lo) / (double)(hi - lo);
                return capLo.Value + (capHi.Value - capLo.Value) * frac;
            }
        }
        return null;
    }

    /// <summary>
    /// Smallest table size whose capacity covers the load (in the table's native units),
    /// or null if no table size covers it (oversize / outside the table).
    /// </summary>
    public static double? PickSize(GasConfig config, double load, double lengthFt)
    {
        var table = TryGetTable(config);
        if (table is null)
            return null;
        foreach (double size in table.Sizes)
        {
            double? cap = CapacityAt(config, size, lengthFt);
            if (cap is not null && cap.Value >= load)
                return size;
        }
        return null;
    }

    // ----- Original v1 overloads: IFGC Table 402.4(2), unchanged behavior -----

    /// <summary>
    /// Table capacity (CFH) for a size at a design length, IFGC Table 402.4(2).
    /// Linear interpolation between bracketing table lengths. Lengths below 10 ft use the
    /// 10 ft row (conservative). Lengths above 2,000 ft return null (outside the table).
    /// Returns null for the NA cell (1/2 in. at 2,000 ft).
    /// </summary>
    public static double? CapacityAt(double sizeIn, double lengthFt) =>
        CapacityAt(GasConfig.Default, sizeIn, lengthFt);

    /// <summary>Smallest table size whose capacity covers the load, or null.</summary>
    public static double? PickSize(double cfh, double lengthFt) =>
        PickSize(GasConfig.Default, cfh, lengthFt);
}
