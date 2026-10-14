namespace PipeSizer.Sizing;

/// <summary>Per-material pipe data. Inside diameters (inches) are keyed by nominal size.</summary>
public sealed class MaterialData
{
    public required Dictionary<double, double> InsideDiametersIn { get; init; }
    public required double RoughnessFt { get; init; }
    public required double HazenWilliamsC { get; init; }
}

public static class Materials
{
    public const string CopperTypeL = "Copper Type L";
    public const string SteelSch40 = "Steel Sch 40";
    public const string Pex = "PEX";

    public static readonly IReadOnlyList<string> Names = new[]
    {
        CopperTypeL, SteelSch40, Pex,
    };

    private static readonly Dictionary<string, MaterialData> Data = new()
    {
        [CopperTypeL] = new MaterialData
        {
            InsideDiametersIn = new Dictionary<double, double>
            {
                [0.5] = 0.545, [0.75] = 0.785, [1.0] = 1.025, [1.25] = 1.265,
                [1.5] = 1.505, [2.0] = 1.985, [2.5] = 2.465, [3.0] = 2.945,
                [4.0] = 3.905,
            },
            RoughnessFt = 0.000005,
            HazenWilliamsC = 140,
        },
        [SteelSch40] = new MaterialData
        {
            InsideDiametersIn = new Dictionary<double, double>
            {
                [0.5] = 0.622, [0.75] = 0.824, [1.0] = 1.049, [1.25] = 1.380,
                [1.5] = 1.610, [2.0] = 2.067, [2.5] = 2.469, [3.0] = 3.068,
                [4.0] = 4.026, [5.0] = 5.047, [6.0] = 6.065, [8.0] = 7.981,
            },
            RoughnessFt = 0.00015,
            HazenWilliamsC = 120,
        },
        [Pex] = new MaterialData
        {
            InsideDiametersIn = new Dictionary<double, double>
            {
                [0.5] = 0.485, [0.75] = 0.677, [1.0] = 0.863, [1.25] = 1.063,
                [1.5] = 1.244, [2.0] = 1.629,
            },
            RoughnessFt = 0.000005,
            HazenWilliamsC = 150,
        },
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copper"] = CopperTypeL,
        ["copper type l"] = CopperTypeL,
        ["type l"] = CopperTypeL,
        ["cu"] = CopperTypeL,
        ["steel"] = SteelSch40,
        ["steel sch 40"] = SteelSch40,
        ["sch 40"] = SteelSch40,
        ["sch40"] = SteelSch40,
        ["pex"] = Pex,
    };

    /// <summary>Material input string -&gt; canonical material name. Unknown input defaults to Copper Type L.</summary>
    public static (string Material, bool Recognized) Resolve(string? materialIn)
    {
        if (materialIn is null)
            return (CopperTypeL, false);
        var key = materialIn.Trim();
        foreach (var name in Data.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return (name, true);
        }
        if (Aliases.TryGetValue(key, out var aliased))
            return (aliased, true);
        return (CopperTypeL, false);
    }

    public static MaterialData Get(string material) => Data[material];
}
