namespace PipeSizer.Sizing;

/// <summary>Shared constants and the mean-velocity formula used by both water methods.</summary>
public static class FlowMath
{
    public const double GravityFtS2 = 32.174;
    public const double CfsPerGpm = 1.0 / 448.831;
    public const double CstToFt2S = 1.07639e-5; // 1 centistoke = 1.07639e-5 ft^2/s

    /// <summary>Mean velocity (ft/s) for a flow in GPM through an inside diameter in inches.</summary>
    public static double VelocityFps(double gpm, double idIn)
    {
        double dFt = idIn / 12.0;
        double areaFt2 = Math.PI / 4.0 * dFt * dFt;
        return gpm * CfsPerGpm / areaFt2;
    }
}

/// <summary>Water kinematic viscosity vs temperature (standard textbook values, linearly interpolated).</summary>
internal static class WaterProperties
{
    private static readonly (double TempF, double Cst)[] ViscosityCst =
    {
        (32, 1.78), (40, 1.55), (50, 1.30), (60, 1.12), (70, 0.98),
        (80, 0.86), (90, 0.76), (100, 0.69), (110, 0.62), (120, 0.56),
        (130, 0.51), (140, 0.47), (150, 0.43), (160, 0.40), (170, 0.37),
        (180, 0.35), (190, 0.32), (200, 0.30), (210, 0.28),
    };

    /// <summary>Water kinematic viscosity (ft^2/s) at a temperature in deg F.</summary>
    public static double NuFt2s(double tempF)
    {
        double cst;
        if (tempF <= ViscosityCst[0].TempF)
            cst = ViscosityCst[0].Cst;
        else if (tempF >= ViscosityCst[^1].TempF)
            cst = ViscosityCst[^1].Cst;
        else
        {
            cst = ViscosityCst[^1].Cst;
            for (int i = 0; i < ViscosityCst.Length - 1; i++)
            {
                var (t0, v0) = ViscosityCst[i];
                var (t1, v1) = ViscosityCst[i + 1];
                if (t0 <= tempF && tempF <= t1)
                {
                    cst = v0 + (v1 - v0) * (tempF - t0) / (t1 - t0);
                    break;
                }
            }
        }
        return cst * FlowMath.CstToFt2S;
    }
}
