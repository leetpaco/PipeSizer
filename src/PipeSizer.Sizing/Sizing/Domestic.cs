namespace PipeSizer.Sizing;

/// <summary>
/// Domestic-water sizing: Hazen-Williams + velocity, smallest size meeting BOTH
/// velocity &lt;= max velocity AND friction &lt;= max friction per 100 ft.
/// Per-pipe sizing only (no system-level residual-pressure check).
/// </summary>
public static class DomesticSizing
{
    /// <summary>
    /// Hazen-Williams head loss (ft) per 100 ft of pipe.
    /// English units: h_f = 4.52 * L * Q^1.85 / (C^1.85 * d^4.87), Q in GPM, d in inches, L in ft.
    /// </summary>
    public static double HazenWilliamsPer100Ft(double gpm, double idIn, double cFactor)
    {
        if (gpm <= 0)
            return 0.0;
        return 452.0 * Math.Pow(gpm, 1.85) / (Math.Pow(cFactor, 1.85) * Math.Pow(idIn, 4.87));
    }

    /// <summary>
    /// Smallest size meeting BOTH limits. Returns (size, velocity, friction) at the pick;
    /// if no size meets both limits, size is null and velocity/friction are for the largest evaluated size.
    /// </summary>
    public static (double? SizeIn, double VelocityFps, double FrictionPer100Ft) PickSize(
        double gpm, string material, double maxVelocityFps, double maxFrictionPer100Ft)
    {
        var mat = Materials.Get(material);
        double lastV = 0, lastH = 0;
        foreach (double sizeIn in mat.InsideDiametersIn.Keys.OrderBy(s => s))
        {
            double idIn = mat.InsideDiametersIn[sizeIn];
            double vel = FlowMath.VelocityFps(gpm, idIn);
            double h = HazenWilliamsPer100Ft(gpm, idIn, mat.HazenWilliamsC);
            lastV = vel;
            lastH = h;
            if (vel <= maxVelocityFps && h <= maxFrictionPer100Ft)
                return (sizeIn, vel, h);
        }
        return (null, lastV, lastH);
    }
}
