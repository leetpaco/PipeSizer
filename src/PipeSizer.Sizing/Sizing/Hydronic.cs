namespace PipeSizer.Sizing;

/// <summary>
/// Hydronic sizing: Darcy-Weisbach + Swamee-Jain, smallest size meeting BOTH
/// velocity &lt;= max velocity AND friction &lt;= max friction per 100 ft of straight pipe.
/// Water only. Straight-pipe friction only (no fitting losses / pump head).
/// </summary>
public static class HydronicSizing
{
    /// <summary>Darcy-Weisbach head loss (ft) per 100 ft of straight pipe.</summary>
    public static double FrictionPer100Ft(double gpm, double idIn, double roughnessFt, double tempF)
    {
        double dFt = idIn / 12.0;
        double vel = FlowMath.VelocityFps(gpm, idIn);
        double nu = WaterProperties.NuFt2s(tempF);
        double re = nu > 0 ? vel * dFt / nu : 0.0;
        if (re <= 0)
            return 0.0;
        double f;
        if (re < 2300)
        {
            f = 64.0 / re; // laminar
        }
        else
        {
            double term = roughnessFt / (3.7 * dFt) + 5.74 / Math.Pow(re, 0.9);
            f = 0.25 / Math.Pow(Math.Log10(term), 2); // Swamee-Jain
        }
        return f * (100.0 / dFt) * (vel * vel / (2.0 * FlowMath.GravityFtS2));
    }

    /// <summary>
    /// Smallest size meeting BOTH limits. Returns (size, velocity, friction) at the pick;
    /// if no size meets both limits, size is null and velocity/friction are for the largest evaluated size.
    /// </summary>
    public static (double? SizeIn, double VelocityFps, double FrictionPer100Ft) PickSize(
        double gpm, string material, double tempF, double maxVelocityFps, double maxFrictionPer100Ft)
    {
        var mat = Materials.Get(material);
        double lastV = 0, lastH = 0;
        foreach (double sizeIn in mat.InsideDiametersIn.Keys.OrderBy(s => s))
        {
            double idIn = mat.InsideDiametersIn[sizeIn];
            double vel = FlowMath.VelocityFps(gpm, idIn);
            double h = FrictionPer100Ft(gpm, idIn, mat.RoughnessFt, tempF);
            lastV = vel;
            lastH = h;
            if (vel <= maxVelocityFps && h <= maxFrictionPer100Ft)
                return (sizeIn, vel, h);
        }
        return (null, lastV, lastH);
    }
}
