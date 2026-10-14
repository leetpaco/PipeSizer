using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipeSizer.Sizing;

namespace PipeSizer;

/// <summary>One MEP system found in the model, with its auto category and effective (post-override) category.</summary>
public sealed class SystemInfo
{
    public required string Name { get; init; }
    public required PipeCategory AutoCategory { get; init; }
    public required PipeCategory EffectiveCategory { get; init; }

    /// <summary>What the Systems list shows.</summary>
    public string Display => $"{Name} ({EffectiveCategory})";
}

/// <summary>Everything one check produces.</summary>
public sealed class CheckResult
{
    public required List<PipeRow> Rows { get; init; }
    public required List<SystemInfo> Systems { get; init; }
    public required string Summary { get; init; }
}

/// <summary>
/// Reads every pipe in the active document and checks it against the panel settings.
/// Read-only: this never modifies the model. Ported from the validated Dynamo graphs
/// (PipeSizer_*.py) — same methods, same thresholds, same unit conversions.
/// </summary>
public static class Checker
{
    public const double M3sToGpm = 15850.3; // Revit stores flow internally in m3/s
    public const double M3sToCfh = 127132.8;

    public static CheckResult Check(Document doc, PipeSizerSettings settings)
    {
        var rows = new List<PipeRow>();
        var systems = new Dictionary<string, SystemInfo>(StringComparer.OrdinalIgnoreCase);
        int nPipes = 0, nViolations = 0, nNoFlow = 0, nOversize = 0, nIgnored = 0, nOk = 0, nInfo = 0;

        // Hoisted out of the per-pipe loop: these are the same for every pipe,
        // so resolving them per pipe was pure overhead on big models.
        var gasConfig = settings.GasConfig;
        var gasTable = GasSizing.TryGetTable(gasConfig);
        var (hydMatName, _) = Materials.Resolve(settings.HydronicMaterial);
        var hydMat = Materials.Get(hydMatName);
        var (domMatName, _) = Materials.Resolve(settings.DomesticMaterial);
        var domMat = Materials.Get(domMatName);
        // TypeId -> TypeId-derived classification. The name-keyword fallback is
        // NOT cached here (it depends on the system name, not the type).
        var typeCache = new Dictionary<ElementId, PipeCategory>();

        var pipes = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_PipeCurves)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var el in pipes)
        {
            if (el is not Pipe pipe)
                continue;
            nPipes++;

            var mepSystem = pipe.MEPSystem;
            string sysName = mepSystem?.Name ?? "(no system)";

            PipeCategory category;
            if (mepSystem is null)
            {
                category = PipeCategory.Ignore;
                nIgnored++;
                continue;
            }
            if (!systems.TryGetValue(sysName, out var sysInfo))
            {
                var auto = Classify(doc, mepSystem, sysName, typeCache);
                var effective = settings.CategoryOverrides.TryGetValue(sysName, out var ov) ? ov : auto;
                sysInfo = new SystemInfo { Name = sysName, AutoCategory = auto, EffectiveCategory = effective };
                systems[sysName] = sysInfo;
            }
            category = sysInfo.EffectiveCategory;
            if (category == PipeCategory.Ignore)
            {
                nIgnored++;
                continue;
            }

            double? flowM3s = GetParam(pipe, BuiltInParameter.RBS_PIPE_FLOW_PARAM);
            if (flowM3s is null)
            {
                rows.Add(Info(pipe, sysName, category, "No Flow parameter — is this a pipe in a calculated system?"));
                nInfo++;
                continue;
            }
            double? diaFt = GetParam(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            // Round: Revit stores diameter in feet, so e.g. 1-1/4" comes back as 1.2500000000000002 in.
            double? curIn = diaFt is null ? null : Math.Round(diaFt.Value * 12.0, 3);

            switch (category)
            {
                case PipeCategory.Gas:
                {
                    double cfh = flowM3s.Value * M3sToCfh;
                    if (cfh < 0.5)
                    {
                        rows.Add(Info(pipe, sysName, category, "No flow — skipped"));
                        nNoFlow++;
                        break;
                    }
                    if (gasTable is null)
                    {
                        rows.Add(Info(pipe, sysName, category,
                            $"No IFGC table for this configuration ({gasConfig.Describe()}) — cannot evaluate."));
                        nInfo++;
                        break;
                    }
                    // Table-native load: CFH for natural gas, MBH for propane
                    // (propane: 2,500 Btu/ft3, see GasSizing.PropaneBtuPerCubicFoot).
                    string units = gasTable.Units;
                    double load = gasConfig.Fuel == GasFuel.Propane ? GasSizing.CfhToPropaneMbh(cfh) : cfh;
                    string flowText = $"{load:0} {units}";
                    double designLength = settings.GasDesignLengthFt;
                    double maxSize = gasTable.Sizes[^1];
                    string maxSizeLabel = gasConfig.IsCsst ? $"EHD {maxSize:0}" : SizeLabels.Format(maxSize);
                    double? maxCap = GasSizing.CapacityAt(gasConfig, maxSize, designLength);

                    if (gasConfig.IsCsst)
                    {
                        // CSST is sized by EHD, which has no fixed mapping from the Revit
                        // nominal diameter (EHD depends on the manufacturer). Report the
                        // required EHD; do not pass/fail the nominal size against it.
                        double? requiredEhd = GasSizing.PickSize(gasConfig, load, designLength);
                        string curSize = curIn is null ? "?" : SizeLabels.Format(curIn.Value);
                        if (requiredEhd is null)
                        {
                            rows.Add(new PipeRow
                            {
                                ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                                System = sysName, Category = "Gas",
                                Flow = flowText, CurrentSize = curSize, RequiredSize = "Oversize",
                                Check = "Capacity (EHD)", Actual = flowText,
                                Limit = maxCap is null ? "outside table" : $"{maxSizeLabel} cap {maxCap:0} {units}",
                                Status = "Oversize",
                                Note = $"Load exceeds largest CSST table size ({maxSizeLabel}) — split the run or size manually.",
                            });
                            nOversize++;
                        }
                        else
                        {
                            double? reqCap = GasSizing.CapacityAt(gasConfig, requiredEhd.Value, designLength);
                            string note = "CSST is sized by EHD (Equivalent Hydraulic Diameter), which has no fixed mapping " +
                                "from nominal size — verify the installed tube's listed EHD meets or exceeds the required EHD. " +
                                "Table includes 4 bends + 2 fittings; add L = 1.3n ft for extra bends/fittings.";
                            if (gasConfig.Pressure == GasPressure.TwoPsi)
                                note += gasConfig.Fuel == GasFuel.NaturalGas
                                    ? " 2 psi table excludes line-regulator drop — do not use if regulator loss exceeds 3/4 psi."
                                    : " 2 psi table excludes line-regulator drop — do not use if regulator loss exceeds 1/2 psi (based on 13 in. w.c. outlet).";
                            rows.Add(new PipeRow
                            {
                                ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                                System = sysName, Category = "Gas",
                                Flow = flowText, CurrentSize = curSize,
                                RequiredSize = $"EHD {requiredEhd:0}",
                                Check = "Capacity (EHD)", Actual = flowText,
                                Limit = reqCap is null ? "—" : $"EHD {requiredEhd:0} cap {reqCap:0} {units}",
                                Status = "Info",
                                Note = note,
                            });
                            nInfo++;
                        }
                        break;
                    }

                    // Metallic / PE: table sizes are nominal inches, so pass/fail works.
                    string curSizeIn = curIn is null ? "?" : SizeLabels.Format(curIn.Value);
                    double? curCap;
                    if (curIn is null)
                    {
                        curCap = null;
                    }
                    else if (!GasSizing.IsSizeInTable(gasConfig, curIn.Value))
                    {
                        if (curIn.Value > maxSize)
                        {
                            // Bigger than the largest table size: at least the max-size capacity.
                            curCap = maxCap;
                        }
                        else
                        {
                            rows.Add(Info(pipe, sysName, category,
                                $"Current size {curSizeIn} is not a column of IFGC Table {gasTable.TableNumber} — cannot evaluate."));
                            nInfo++;
                            break;
                        }
                    }
                    else
                    {
                        curCap = GasSizing.CapacityAt(gasConfig, curIn.Value, designLength);
                    }
                    double? required = GasSizing.PickSize(gasConfig, load, designLength);
                    if (curCap is null || curCap.Value < load)
                    {
                        if (required is null)
                        {
                            rows.Add(new PipeRow
                            {
                                ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                                System = sysName, Category = "Gas",
                                Flow = flowText, CurrentSize = curSizeIn, RequiredSize = "Oversize",
                                Check = "Capacity", Actual = flowText,
                                Limit = maxCap is null ? "outside table" : $"{maxSizeLabel} cap {maxCap:0} {units}",
                                Status = "Oversize",
                                Note = $"Load exceeds {maxSizeLabel} table capacity — split the run or size manually.",
                            });
                            nOversize++;
                        }
                        else
                        {
                            rows.Add(new PipeRow
                            {
                                ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                                System = sysName, Category = "Gas",
                                Flow = flowText, CurrentSize = curSizeIn,
                                RequiredSize = SizeLabels.Format(required.Value),
                                Check = "Capacity", Actual = flowText,
                                Limit = curCap is null ? "no capacity" : $"cap {curCap:0} {units}",
                                Status = "Violation",
                            });
                            nViolations++;
                        }
                    }
                    else
                    {
                        rows.Add(Ok(pipe, sysName, "Gas", flowText, curSizeIn, "Capacity",
                            flowText, $"cap {curCap:0} {units}"));
                        nOk++;
                    }
                    break;
                }
                case PipeCategory.Hydronic:
                {
                    double gpm = flowM3s.Value * M3sToGpm;
                    if (gpm < 0.05)
                    {
                        rows.Add(Info(pipe, sysName, category, "No flow — skipped"));
                        nNoFlow++;
                        break;
                    }
                    // Materials resolved once per check (hoisted above the loop).
                    var material = hydMatName;
                    var mat = hydMat;
                    if (curIn is null || !mat.InsideDiametersIn.TryGetValue(curIn.Value, out double idCur))
                    {
                        rows.Add(Info(pipe, sysName, category,
                            $"Current size {(curIn is null ? "?" : SizeLabels.Format(curIn.Value))} not in {material} table — cannot evaluate."));
                        nInfo++;
                        break;
                    }
                    double velCur = FlowMath.VelocityFps(gpm, idCur);
                    double hCur = HydronicSizing.FrictionPer100Ft(gpm, idCur, mat.RoughnessFt, PipeSizerSettings.HydronicWaterTempF);
                    double vmax = settings.HydronicMaxVelocityFps;
                    double hmax = settings.HydronicMaxFrictionPer100Ft;
                    bool velFail = velCur > vmax;
                    bool hFail = hCur > hmax;
                    var (pick, _, _) = HydronicSizing.PickSize(gpm, material, PipeSizerSettings.HydronicWaterTempF, vmax, hmax);
                    if (!velFail && !hFail)
                    {
                        rows.Add(Ok(pipe, sysName, "Hydronic", $"{gpm:0.0} GPM", SizeLabels.Format(curIn.Value),
                            "Velocity + Friction", $"V {velCur:0.00} ft/s, h {hCur:0.00} ft/100 ft",
                            $"V ≤ {vmax:0.##}, h ≤ {hmax:0.##}"));
                        nOk++;
                    }
                    else
                    {
                        string check = velFail && hFail ? "Velocity + Friction" : velFail ? "Velocity" : "Friction";
                        rows.Add(new PipeRow
                        {
                            ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                            System = sysName, Category = "Hydronic",
                            Flow = $"{gpm:0.0} GPM", CurrentSize = SizeLabels.Format(curIn.Value),
                            RequiredSize = pick is null ? "None" : SizeLabels.Format(pick.Value),
                            Check = check,
                            Actual = $"V {velCur:0.00} ft/s, h {hCur:0.00} ft/100 ft",
                            Limit = $"V ≤ {vmax:0.##}, h ≤ {hmax:0.##}",
                            Status = pick is null ? "No size meets limits" : "Violation",
                            Note = pick is null ? "No size meets both limits — check flow / limits." : "",
                        });
                        nViolations++;
                    }
                    break;
                }
                case PipeCategory.Domestic:
                {
                    double gpm = flowM3s.Value * M3sToGpm;
                    if (gpm < 0.05)
                    {
                        rows.Add(Info(pipe, sysName, category, "No flow — skipped"));
                        nNoFlow++;
                        break;
                    }
                    // Materials resolved once per check (hoisted above the loop).
                    var material = domMatName;
                    var mat = domMat;
                    if (curIn is null || !mat.InsideDiametersIn.TryGetValue(curIn.Value, out double idCur))
                    {
                        rows.Add(Info(pipe, sysName, category,
                            $"Current size {(curIn is null ? "?" : SizeLabels.Format(curIn.Value))} not in {material} table — cannot evaluate."));
                        nInfo++;
                        break;
                    }
                    double velCur = FlowMath.VelocityFps(gpm, idCur);
                    double hCur = DomesticSizing.HazenWilliamsPer100Ft(gpm, idCur, mat.HazenWilliamsC);
                    double vmax = settings.DomesticMaxVelocityFps;
                    double hmax = settings.DomesticMaxFrictionPer100Ft;
                    bool velFail = velCur > vmax;
                    bool hFail = hCur > hmax;
                    var (pick, _, _) = DomesticSizing.PickSize(gpm, material, vmax, hmax);
                    if (!velFail && !hFail)
                    {
                        rows.Add(Ok(pipe, sysName, "Domestic", $"{gpm:0.0} GPM", SizeLabels.Format(curIn.Value),
                            "Velocity + Friction", $"V {velCur:0.00} ft/s, h {hCur:0.00} ft/100 ft",
                            $"V ≤ {vmax:0.##}, h ≤ {hmax:0.##}"));
                        nOk++;
                    }
                    else
                    {
                        string check = velFail && hFail ? "Velocity + Friction" : velFail ? "Velocity" : "Friction";
                        rows.Add(new PipeRow
                        {
                            ElementId = pipe.Id, ElementIdValue = pipe.Id.Value,
                            System = sysName, Category = "Domestic",
                            Flow = $"{gpm:0.0} GPM", CurrentSize = SizeLabels.Format(curIn.Value),
                            RequiredSize = pick is null ? "None" : SizeLabels.Format(pick.Value),
                            Check = check,
                            Actual = $"V {velCur:0.00} ft/s, h {hCur:0.00} ft/100 ft",
                            Limit = $"V ≤ {vmax:0.##}, h ≤ {hmax:0.##}",
                            Status = pick is null ? "No size meets limits" : "Violation",
                            Note = pick is null ? "No size meets both limits — check flow / limits." : "",
                        });
                        nViolations++;
                    }
                    break;
                }
            }
        }

        string summary = $"{nPipes} pipes read · {nViolations} violation(s) · {nOversize} oversize · " +
                         $"{nNoFlow} no-flow · {nInfo} info · {nOk} OK · {nIgnored} ignored · {DateTime.Now:HH:mm:ss}";
        return new CheckResult
        {
            Rows = rows,
            Systems = systems.Values.OrderBy(s => s.Name).ToList(),
            Summary = summary,
        };
    }

    private static double? GetParam(Element el, BuiltInParameter bip)
    {
        try
        {
            var p = el.get_Parameter(bip);
            return p?.AsDouble();
        }
        catch
        {
            return null;
        }
    }

    private static PipeRow Info(Pipe pipe, string sysName, PipeCategory category, string note) => new()
    {
        ElementId = pipe.Id,
        ElementIdValue = pipe.Id.Value,
        System = sysName,
        Category = category.ToString(),
        Flow = "—",
        CurrentSize = "—",
        RequiredSize = "—",
        Check = "Info",
        Actual = "—",
        Limit = "—",
        Status = "Info",
        Note = note,
    };

    private static PipeRow Ok(Pipe pipe, string sysName, string category, string flow, string curSize,
        string check, string actual, string limit) => new()
    {
        ElementId = pipe.Id,
        ElementIdValue = pipe.Id.Value,
        System = sysName,
        Category = category,
        Flow = flow,
        CurrentSize = curSize,
        RequiredSize = curSize,
        Check = check,
        Actual = actual,
        Limit = limit,
        Status = "OK",
    };

    /// <summary>
    /// Auto-classify a system. Gas has no Revit system classification, so it is detected
    /// by name only. Domestic/hydronic come from the system type's classification first,
    /// with name keywords as a fallback for custom-named systems.
    /// <paramref name="typeCache"/> memoizes the TypeId-derived classification for the
    /// check, so doc.GetElement(typeId) runs at most once per system type. The
    /// name-keyword fallback is NOT cached (it depends on the system name).
    /// </summary>
    public static PipeCategory Classify(Document doc, MEPSystem system, string systemName,
        Dictionary<ElementId, PipeCategory> typeCache)
    {
        string lower = systemName.ToLowerInvariant();
        if (lower.Contains("gas"))
            return PipeCategory.Gas;

        PipeCategory byType = PipeCategory.Ignore;
        try
        {
            var typeId = system.GetTypeId();
            if (typeId.Value > 0 && typeCache.TryGetValue(typeId, out var cached))
            {
                byType = cached;
            }
            else if (typeId.Value > 0 && doc.GetElement(typeId) is MEPSystemType sysType)
            {
                byType = sysType.SystemClassification switch
                {
                    MEPSystemClassification.DomesticColdWater => PipeCategory.Domestic,
                    MEPSystemClassification.DomesticHotWater => PipeCategory.Domestic,
                    MEPSystemClassification.Recirculation => PipeCategory.Domestic,
                    MEPSystemClassification.SupplyHydronic => PipeCategory.Hydronic,
                    MEPSystemClassification.ReturnHydronic => PipeCategory.Hydronic,
                    _ => PipeCategory.Ignore,
                };
                typeCache[typeId] = byType;
            }
        }
        catch
        {
            // Fall through to name keywords.
        }

        if (byType != PipeCategory.Ignore)
            return byType;

        if (lower.Contains("domestic") || lower.Contains("cold water") ||
            lower.Contains("hot water") || lower.Contains("potable"))
            return PipeCategory.Domestic;
        if (lower.Contains("hydronic") || lower.Contains("heating") ||
            lower.Contains("chilled") || lower.Contains("condenser"))
            return PipeCategory.Hydronic;
        return PipeCategory.Ignore;
    }
}
