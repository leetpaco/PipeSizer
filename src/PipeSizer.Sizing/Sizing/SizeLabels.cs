namespace PipeSizer.Sizing;

/// <summary>Nominal pipe size (inches) -&gt; display string, e.g. 0.75 -&gt; 3/4".</summary>
public static class SizeLabels
{
    private static readonly Dictionary<double, string> Labels = new()
    {
        [0.5] = "1/2\"",
        [0.75] = "3/4\"",
        [1.0] = "1\"",
        [1.25] = "1-1/4\"",
        [1.5] = "1-1/2\"",
        [2.0] = "2\"",
        [2.5] = "2-1/2\"",
        [3.0] = "3\"",
        [4.0] = "4\"",
        [5.0] = "5\"",
        [6.0] = "6\"",
        [8.0] = "8\"",
        [10.0] = "10\"",
        [12.0] = "12\"",
    };

    public static string Format(double sizeIn) =>
        Labels.TryGetValue(sizeIn, out var label) ? label : $"{sizeIn:0.00}\"";
}
