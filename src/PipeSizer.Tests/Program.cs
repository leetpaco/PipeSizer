// Hand-checks ported from the validated Dynamo Python graphs (PipeSizer_*.py).
// Exit code 0 = all pass. Any failure throws with a message naming the check.
using System.Xml.Linq;
using PipeSizer.Sizing;

static void Eq<T>(T actual, T expected, string name)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"FAIL {name}: expected {expected}, got {actual}");
}

static void Check(bool cond, string name)
{
    if (!cond)
        throw new Exception($"FAIL {name}");
}

static void Near(double actual, double expected, double tol, string name)
{
    if (Math.Abs(actual - expected) > tol)
        throw new Exception($"FAIL {name}: expected {expected} ± {tol}, got {actual}");
}

// --- Gas: IFGC Table 402.4(2) ---
Eq(GasSizing.CapacityAt(1.0, 100), 195.0, "gas anchor 1in @100ft = 195 CFH");
Eq(GasSizing.CapacityAt(2.0, 100), 1160.0, "gas anchor 2in @100ft = 1160 CFH");
Eq(GasSizing.CapacityAt(0.5, 100), 50.0, "gas anchor 1/2in @100ft = 50 CFH");
Near(GasSizing.CapacityAt(0.75, 45)!.Value, 160.5, 0.01, "gas interp 3/4in @45ft = 160.5 CFH");
Near(GasSizing.CapacityAt(0.5, 45)!.Value, 76.5, 0.01, "gas interp 1/2in @45ft = 76.5 CFH");
Eq(GasSizing.PickSize(150, 45), 0.75, "gas 150 CFH @45ft -> 3/4in");
Eq(GasSizing.PickSize(40, 45), 0.5, "gas 40 CFH @45ft -> 1/2in");
Eq(GasSizing.PickSize(150, 100), 1.0, "gas 150 CFH @100ft -> 1in");
Eq(GasSizing.PickSize(200000, 100), null, "gas 200000 CFH @100ft -> oversize (null)");
Eq(GasSizing.CapacityAt(0.5, 2000), null, "gas 1/2in @2000ft = NA (null)");
Eq(GasSizing.CapacityAt(0.75, 2000), 20.0, "gas 3/4in @2000ft = 20 CFH");
Eq(GasSizing.CapacityAt(1.0, 2001), null, "gas >2000ft = outside table (null)");
Eq(GasSizing.PickSize(5, 2000), 0.75, "gas 5 CFH @2000ft -> 3/4in (1/2in NA)");
Console.WriteLine("gas: PASS");

// --- Gas expansion: propane, 2 psi, CSST, PE (IFGC Tables 402.4(5), (15), (18), (21), (22), (27), (28), (32), (33), (35), (36)) ---
var ng2Met = new GasConfig(GasFuel.NaturalGas, GasPressure.TwoPsi, GasMaterial.Metallic);
var ngLowCsst = new GasConfig(GasFuel.NaturalGas, GasPressure.Low, GasMaterial.Csst);
var ng2Csst = new GasConfig(GasFuel.NaturalGas, GasPressure.TwoPsi, GasMaterial.Csst);
var ngLowPe = new GasConfig(GasFuel.NaturalGas, GasPressure.Low, GasMaterial.Pe);
var ng2Pe = new GasConfig(GasFuel.NaturalGas, GasPressure.TwoPsi, GasMaterial.Pe);
var lpLowMet = new GasConfig(GasFuel.Propane, GasPressure.Low, GasMaterial.Metallic);
var lp2Met = new GasConfig(GasFuel.Propane, GasPressure.TwoPsi, GasMaterial.Metallic);
var lpLowCsst = new GasConfig(GasFuel.Propane, GasPressure.Low, GasMaterial.Csst);
var lp2Csst = new GasConfig(GasFuel.Propane, GasPressure.TwoPsi, GasMaterial.Csst);
var lpLowPe = new GasConfig(GasFuel.Propane, GasPressure.Low, GasMaterial.Pe);
var lp2Pe = new GasConfig(GasFuel.Propane, GasPressure.TwoPsi, GasMaterial.Pe);

// Config -> table mapping (all 12 combos)
Eq(GasSizing.TryGetTable(GasConfig.Default)?.TableNumber, "402.4(2)", "gas table default -> 402.4(2)");
Eq(GasSizing.TryGetTable(ng2Met)?.TableNumber, "402.4(5)", "gas table NG 2psi metallic -> 402.4(5)");
Eq(GasSizing.TryGetTable(ngLowCsst)?.TableNumber, "402.4(15)", "gas table NG low CSST -> 402.4(15)");
Eq(GasSizing.TryGetTable(ng2Csst)?.TableNumber, "402.4(18)", "gas table NG 2psi CSST -> 402.4(18)");
Eq(GasSizing.TryGetTable(ngLowPe)?.TableNumber, "402.4(21)", "gas table NG low PE -> 402.4(21)");
Eq(GasSizing.TryGetTable(ng2Pe)?.TableNumber, "402.4(22)", "gas table NG 2psi PE -> 402.4(22)");
Eq(GasSizing.TryGetTable(lpLowMet)?.TableNumber, "402.4(28)", "gas table propane low metallic -> 402.4(28)");
Eq(GasSizing.TryGetTable(lp2Met)?.TableNumber, "402.4(27)", "gas table propane 2psi metallic -> 402.4(27)");
Eq(GasSizing.TryGetTable(lpLowCsst)?.TableNumber, "402.4(32)", "gas table propane low CSST -> 402.4(32)");
Eq(GasSizing.TryGetTable(lp2Csst)?.TableNumber, "402.4(33)", "gas table propane 2psi CSST -> 402.4(33)");
Eq(GasSizing.TryGetTable(lpLowPe)?.TableNumber, "402.4(35)", "gas table propane low PE -> 402.4(35)");
Eq(GasSizing.TryGetTable(lp2Pe)?.TableNumber, "402.4(36)", "gas table propane 2psi PE -> 402.4(36)");

// Table units: CFH for natural gas, MBH for propane
Eq(GasSizing.TryGetTable(ng2Met)?.Units, "CFH", "gas units NG 2psi metallic = CFH");
Eq(GasSizing.TryGetTable(lpLowMet)?.Units, "MBH", "gas units propane low metallic = MBH");
Eq(GasSizing.TryGetTable(lp2Pe)?.Units, "MBH", "gas units propane 2psi PE = MBH");

// Propane CFH -> MBH conversion (2,500 Btu/ft3 assumption)
Eq(GasSizing.PropaneBtuPerCubicFoot, 2500.0, "propane 2500 Btu/ft3");
Near(GasSizing.CfhToPropaneMbh(100), 250.0, 0.001, "propane 100 CFH = 250 MBH");

// One anchor value per new table, verified against the published IFGC table
Eq(GasSizing.CapacityAt(ng2Met, 1.0, 100), 1710.0, "gas anchor (5) 1in @100ft = 1710 CFH");
Eq(GasSizing.CapacityAt(ng2Met, 0.5, 100), 462.0, "gas anchor (5) 1/2in @100ft = 462 CFH");
Eq(GasSizing.CapacityAt(ngLowCsst, 13, 5), 46.0, "gas anchor (15) EHD13 @5ft = 46 CFH");
Eq(GasSizing.CapacityAt(ng2Csst, 13, 10), 270.0, "gas anchor (18) EHD13 @10ft = 270 CFH");
Eq(GasSizing.CapacityAt(ngLowPe, 1.0, 100), 209.0, "gas anchor (21) 1in @100ft = 209 CFH");
Eq(GasSizing.CapacityAt(ng2Pe, 1.0, 100), 1930.0, "gas anchor (22) 1in @100ft = 1930 CFH");
Eq(GasSizing.CapacityAt(lpLowMet, 1.0, 100), 330.0, "gas anchor (28) 1in @100ft = 330 MBH");
Eq(GasSizing.CapacityAt(lpLowMet, 0.5, 100), 84.0, "gas anchor (28) 1/2in @100ft = 84 MBH");
Eq(GasSizing.CapacityAt(lp2Met, 1.0, 100), 3030.0, "gas anchor (27) 1in @100ft = 3030 MBH");
Eq(GasSizing.CapacityAt(lpLowCsst, 13, 5), 72.0, "gas anchor (32) EHD13 @5ft = 72 MBH");
Eq(GasSizing.CapacityAt(lp2Csst, 13, 10), 426.0, "gas anchor (33) EHD13 @10ft = 426 MBH");
Eq(GasSizing.CapacityAt(lpLowPe, 1.0, 100), 353.0, "gas anchor (35) 1in @100ft = 353 MBH");
Eq(GasSizing.CapacityAt(lp2Pe, 1.0, 100), 3250.0, "gas anchor (36) 1in @100ft = 3250 MBH");
Eq(GasSizing.CapacityAt(lp2Pe, 4.0, 10), 284000.0, "gas anchor (36) 4in @10ft = 284000 MBH");

// Interpolation
Near(GasSizing.CapacityAt(ng2Met, 0.75, 45)!.Value, 1440.0, 0.01, "gas interp (5) 3/4in @45ft = 1440 CFH");
Near(GasSizing.CapacityAt(lpLowMet, 0.5, 45)!.Value, 129.5, 0.01, "gas interp (28) 1/2in @45ft = 129.5 MBH");
Near(GasSizing.CapacityAt(ngLowCsst, 13, 7.5)!.Value, 39.0, 0.01, "gas interp (15) EHD13 @7.5ft = 39 CFH");
Near(GasSizing.CapacityAt(ngLowPe, 1.0, 45)!.Value, 323.5, 0.01, "gas interp (21) 1in @45ft = 323.5 CFH");

// Out of range / edges
Eq(GasSizing.CapacityAt(ngLowPe, 1.0, 501), null, "gas (21) >500ft = outside table (null)");
Eq(GasSizing.CapacityAt(ngLowCsst, 13, 301), null, "gas (15) >300ft = outside table (null)");
Eq(GasSizing.CapacityAt(ngLowCsst, 13, 3), 46.0, "gas (15) <5ft uses 5ft row = 46 CFH");
Eq(GasSizing.CapacityAt(ng2Csst, 13, 501), null, "gas (18) >500ft = outside table (null)");
Eq(GasSizing.PickSize(ngLowPe, 100000, 100), null, "gas (21) 100000 CFH @100ft -> oversize (null)");

// PickSize per config (table-native units)
Eq(GasSizing.PickSize(lpLowMet, 330, 100), 1.0, "gas (28) 330 MBH @100ft -> 1in");
Eq(GasSizing.PickSize(lpLowMet, 331, 100), 1.25, "gas (28) 331 MBH @100ft -> 1-1/4in");
Eq(GasSizing.PickSize(ng2Met, 1710, 100), 1.0, "gas (5) 1710 CFH @100ft -> 1in");
Eq(GasSizing.PickSize(lp2Met, 3030, 100), 1.0, "gas (27) 3030 MBH @100ft -> 1in");
Eq(GasSizing.PickSize(lp2Pe, 3250, 100), 1.0, "gas (36) 3250 MBH @100ft -> 1in");
Eq(GasSizing.PickSize(ngLowCsst, 46, 5), 13.0, "gas (15) 46 CFH @5ft -> EHD 13");
Eq(GasSizing.PickSize(ngLowCsst, 5000, 5), null, "gas (15) 5000 CFH @5ft -> oversize (null)");
Eq(GasSizing.PickSize(ng2Csst, 270, 10), 13.0, "gas (18) 270 CFH @10ft -> EHD 13");

// Size-in-table checks (PE has no 2-1/2 in. column; CSST sizes are EHD, not inches)
Eq(GasSizing.IsSizeInTable(ngLowPe, 2.5), false, "gas (21) 2-1/2in not a PE column");
Eq(GasSizing.IsSizeInTable(ngLowPe, 2.0), true, "gas (21) 2in is a PE column");
Eq(GasSizing.IsSizeInTable(ngLowCsst, 23), true, "gas (15) EHD 23 is a CSST column");
Eq(GasSizing.IsSizeInTable(ngLowCsst, 0.75), false, "gas (15) 3/4in is not a CSST column");

// KNOWN PUBLISHED-TABLE ANOMALY (402.4(36)): as printed in the 2021 IFGC, the 400 ft /
// 3 in. cell reads 12,000 MBH — lower than the 450 ft value (18,700). Transcribed as
// printed; these checks pin that behavior so a future "fix" is a deliberate decision.
Eq(GasSizing.CapacityAt(lp2Pe, 3.0, 400), 12000.0, "gas anomaly (36) 3in @400ft = 12000 MBH as printed");
Eq(GasSizing.CapacityAt(lp2Pe, 3.0, 450), 18700.0, "gas anomaly (36) 3in @450ft = 18700 MBH");
Console.WriteLine("gas expansion: PASS");

// --- Hydronic: Darcy-Weisbach + Swamee-Jain, Copper Type L, 180 F ---
{
    var (size, vel, h) = HydronicSizing.PickSize(24, Materials.CopperTypeL, 180, 4, 4);
    Eq(size, 2.0, "hydronic 24 GPM copper -> 2in");
    Near(vel, 2.49, 0.02, "hydronic 24 GPM copper V = 2.49 ft/s");
    Near(h, 1.03, 0.02, "hydronic 24 GPM copper h = 1.03 ft/100ft");
}
{
    var (size, _, _) = HydronicSizing.PickSize(5, Materials.CopperTypeL, 180, 4, 4);
    Eq(size, 1.0, "hydronic 5 GPM copper -> 1in");
}
{
    var (size, _, _) = HydronicSizing.PickSize(24, Materials.SteelSch40, 180, 4, 4);
    Eq(size, 1.5, "hydronic 24 GPM steel -> 1-1/2in");
}
Console.WriteLine("hydronic: PASS");

// --- Domestic: Hazen-Williams + velocity, Copper Type L ---
{
    var (size, vel, h) = DomesticSizing.PickSize(37.5, Materials.CopperTypeL, 8, 8);
    Eq(size, 1.5, "domestic 37.5 GPM copper -> 1-1/2in");
    Near(vel, 6.76, 0.02, "domestic 37.5 GPM copper V = 6.76 ft/s");
    Near(h, 5.40, 0.02, "domestic 37.5 GPM copper h = 5.40 ft/100ft");
}
{
    var (size, _, _) = DomesticSizing.PickSize(5, Materials.CopperTypeL, 8, 8);
    Eq(size, 0.75, "domestic 5 GPM copper -> 3/4in");
}
{
    var (size, _, _) = DomesticSizing.PickSize(37.5, Materials.Pex, 8, 8);
    Eq(size, 2.0, "domestic 37.5 GPM PEX -> 2in");
}
Console.WriteLine("domestic: PASS");

// --- Shared ---
Eq(SizeLabels.Format(0.75), "3/4\"", "size label 0.75");
Eq(SizeLabels.Format(1.5), "1-1/2\"", "size label 1.5");
Eq(Materials.Resolve("copper").Material, Materials.CopperTypeL, "material alias copper");
Eq(Materials.Resolve("nonsense").Material, Materials.CopperTypeL, "material default copper");
Console.WriteLine("shared: PASS");
Console.WriteLine("ALL SIZING TESTS PASSED");

// --- Updater: version compare / parse, SHA256, manifest rewrite, guards, staging ---
Check(PipeSizer.Updater.CompareVersions("1.1.0", "1.1.0") == 0, "updater version equal");
Check(PipeSizer.Updater.CompareVersions("v1.2.0", "1.1.0") > 0, "updater version v-prefix newer");
Check(PipeSizer.Updater.CompareVersions("1.1.0", "1.1.0.0") == 0, "updater version 1.1.0 == 1.1.0.0");
Check(PipeSizer.Updater.CompareVersions("1.10.0", "1.9.0") > 0, "updater version numeric not lexicographic");
Check(PipeSizer.Updater.CompareVersions("2.0.0", "1.99.0") > 0, "updater version major wins");
Check(PipeSizer.Updater.CompareVersions("1.0.0", "1.0.1") < 0, "updater version patch older");
Check(PipeSizer.Updater.CompareVersions("1.0.0-alpha", "1.0.0") < 0, "updater version prerelease < release");
Check(PipeSizer.Updater.CompareVersions("1.0.0", "1.0.0-alpha") > 0, "updater version release > prerelease");
Eq(PipeSizer.Updater.ParseVersionFromTag("v1.1.0"), "1.1.0", "updater tag v-prefix stripped");
Eq(PipeSizer.Updater.ParseVersionFromTag("1.1.0"), "1.1.0", "updater tag no prefix");
Eq(PipeSizer.Updater.ParseVersionFromTag(null), null, "updater tag null");
Eq(PipeSizer.Updater.ParseVersionFromTag(""), null, "updater tag empty");

Eq(PipeSizer.Updater.DisplayVersion(new Version(1, 1, 2, 0)), "1.1.2", "updater display version drops .0 revision");
Eq(PipeSizer.Updater.DisplayVersion(new Version(1, 1, 2, 3)), "1.1.2.3", "updater display version keeps nonzero revision");
Eq(PipeSizer.Updater.DisplayVersion(new Version(2, 0, 0, 0)), "2.0.0", "updater display version 2.0.0");
Eq(PipeSizer.Updater.DisplayVersion(null), "unknown", "updater display version null");

Eq(PipeSizer.Updater.DialogText(PipeSizer.UpdateOutcome.Staged("1.1.2")), "Update v1.1.2 downloaded — restart Revit to apply.", "updater dialog staged");
Eq(PipeSizer.Updater.DialogText(PipeSizer.UpdateOutcome.AlreadyCurrent("1.1.1")), "You're on the latest version (v1.1.1).", "updater dialog already current");
Eq(PipeSizer.Updater.DialogText(PipeSizer.UpdateOutcome.Failed("boom")), "Update check failed: boom", "updater dialog failed");
Eq(PipeSizer.Updater.DialogText(PipeSizer.UpdateOutcome.Noop()), "No update available.", "updater dialog noop");

byte[] abc = System.Text.Encoding.UTF8.GetBytes("abc");
Eq(PipeSizer.Updater.ComputeSha256Hex(abc), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "updater sha256 abc");
Check(PipeSizer.Updater.VerifySha256(abc, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), "updater sha verify match");
Check(!PipeSizer.Updater.VerifySha256(abc, "0000000000000000000000000000000000000000000000000000000000000000"), "updater sha verify mismatch");
Check(!PipeSizer.Updater.VerifySha256(abc, null), "updater sha verify null");

string selfPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
Check(PipeSizer.Updater.IsSameFile(selfPath, selfPath), "updater same file identical");
Check(PipeSizer.Updater.IsSameFile("/tmp/ABC.dll", "/tmp/abc.dll"), "updater same file case-insensitive");
Check(!PipeSizer.Updater.IsSameFile("/tmp/A.dll", "/tmp/B.dll"), "updater different files");

// Manifest rewrite on a temp .addin: Assembly replaced, rest intact.
string tmpManifest = Path.Combine(Path.GetTempPath(), "PipeSizer-test-" + Guid.NewGuid().ToString("N") + ".addin");
File.WriteAllText(tmpManifest, """
    <?xml version="1.0" encoding="utf-8"?>
    <RevitAddIns>
      <AddIn Type="Application">
        <Name>PipeSizer</Name>
        <Assembly>PipeSizer.dll</Assembly>
        <AddInId>535616f7-caa7-4755-bdc5-c66058691afb</AddInId>
        <FullClassName>PipeSizer.App</FullClassName>
        <VendorId>PipeSizer</VendorId>
        <VendorDescription>Test.</VendorDescription>
      </AddIn>
    </RevitAddIns>
    """);
Eq(PipeSizer.Updater.RewriteManifest(tmpManifest, "PipeSizer.2.0.0.dll"), "PipeSizer.dll", "updater manifest rewrite returns old");
var mdoc = XDocument.Load(tmpManifest);
Eq(mdoc.Descendants("Assembly").First().Value, "PipeSizer.2.0.0.dll", "updater manifest assembly replaced");
Eq(mdoc.Descendants("AddInId").First().Value, "535616f7-caa7-4755-bdc5-c66058691afb", "updater manifest rest intact");
Eq(mdoc.Descendants("FullClassName").First().Value, "PipeSizer.App", "updater manifest class intact");
File.Delete(tmpManifest);

// CleanupStale: stale versioned + superseded unversioned deleted; Sizing and the
// manifest's target kept.
string tmpClean = Path.Combine(Path.GetTempPath(), "PipeSizer-clean-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpClean);
File.WriteAllBytes(Path.Combine(tmpClean, "PipeSizer.1.0.0.dll"), new byte[] { 1 });
File.WriteAllBytes(Path.Combine(tmpClean, "PipeSizer.dll"), new byte[] { 2 });
File.WriteAllBytes(Path.Combine(tmpClean, "PipeSizer.Sizing.dll"), new byte[] { 3 });
File.WriteAllBytes(Path.Combine(tmpClean, "PipeSizer.1.1.0.dll"), new byte[] { 4 });
PipeSizer.Updater.CleanupStale(tmpClean, "PipeSizer.1.1.0.dll");
Check(!File.Exists(Path.Combine(tmpClean, "PipeSizer.1.0.0.dll")), "updater cleanup deletes stale versioned");
Check(!File.Exists(Path.Combine(tmpClean, "PipeSizer.dll")), "updater cleanup deletes superseded unversioned");
Check(File.Exists(Path.Combine(tmpClean, "PipeSizer.Sizing.dll")), "updater cleanup keeps sizing");
Check(File.Exists(Path.Combine(tmpClean, "PipeSizer.1.1.0.dll")), "updater cleanup keeps manifest target");
Directory.Delete(tmpClean, true);

// ApplyStagedUpdate: happy path.
string tmpStage = Path.Combine(Path.GetTempPath(), "PipeSizer-stage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpStage);
byte[] fakeMain = System.Text.Encoding.UTF8.GetBytes("fake-main-9.9.9");
byte[] fakeSizing = System.Text.Encoding.UTF8.GetBytes("fake-sizing-9.9.9");
string mainSha = PipeSizer.Updater.ComputeSha256Hex(fakeMain);
string sizingSha = PipeSizer.Updater.ComputeSha256Hex(fakeSizing);
string staging = Path.Combine(tmpStage, "update-staging");
Directory.CreateDirectory(staging);
File.WriteAllText(Path.Combine(staging, "version.json"),
    $"{{\"version\": \"9.9.9\", \"assets\": {{\"PipeSizer.dll\": {{\"url\": \"\", \"sha256\": \"{mainSha}\"}}, \"PipeSizer.Sizing.dll\": {{\"url\": \"\", \"sha256\": \"{sizingSha}\"}}}}}}");
File.WriteAllBytes(Path.Combine(staging, "PipeSizer.9.9.9.dll"), fakeMain);
File.WriteAllBytes(Path.Combine(staging, "PipeSizer.Sizing.dll"), fakeSizing);
File.WriteAllText(Path.Combine(tmpStage, "PipeSizer.addin"),
    "<?xml version=\"1.0\" encoding=\"utf-8\"?><RevitAddIns><AddIn Type=\"Application\"><Name>PipeSizer</Name><Assembly>PipeSizer.dll</Assembly><AddInId>x</AddInId><FullClassName>PipeSizer.App</FullClassName><VendorId>PipeSizer</VendorId></AddIn></RevitAddIns>");
var applied = PipeSizer.Updater.ApplyStagedUpdate(tmpStage);
Check(applied.Applied, "updater staged apply applied");
Eq(applied.Version, "9.9.9", "updater staged apply version");
Eq(applied.ManifestAssembly, "PipeSizer.9.9.9.dll", "updater staged apply manifest assembly");
Check(File.Exists(Path.Combine(tmpStage, "PipeSizer.9.9.9.dll")), "updater staged main written");
Eq(File.ReadAllText(Path.Combine(tmpStage, "PipeSizer.Sizing.dll")), "fake-sizing-9.9.9", "updater staged sizing overwritten");
Eq(XDocument.Load(Path.Combine(tmpStage, "PipeSizer.addin")).Descendants("Assembly").First().Value, "PipeSizer.9.9.9.dll", "updater staged manifest rewritten");
Check(!Directory.Exists(staging), "updater staged dir removed");
Directory.Delete(tmpStage, true);

// ApplyStagedUpdate: SHA mismatch -> nothing applied, staging deleted.
string tmpBad = Path.Combine(Path.GetTempPath(), "PipeSizer-bad-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpBad);
string stagingBad = Path.Combine(tmpBad, "update-staging");
Directory.CreateDirectory(stagingBad);
File.WriteAllText(Path.Combine(stagingBad, "version.json"),
    "{\"version\": \"9.9.9\", \"assets\": {\"PipeSizer.dll\": {\"url\": \"\", \"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"}, \"PipeSizer.Sizing.dll\": {\"url\": \"\", \"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"}}}");
File.WriteAllBytes(Path.Combine(stagingBad, "PipeSizer.9.9.9.dll"), fakeMain);
File.WriteAllBytes(Path.Combine(stagingBad, "PipeSizer.Sizing.dll"), fakeSizing);
File.WriteAllText(Path.Combine(tmpBad, "PipeSizer.addin"),
    "<?xml version=\"1.0\" encoding=\"utf-8\"?><RevitAddIns><AddIn Type=\"Application\"><Name>PipeSizer</Name><Assembly>PipeSizer.dll</Assembly><AddInId>x</AddInId><FullClassName>PipeSizer.App</FullClassName><VendorId>PipeSizer</VendorId></AddIn></RevitAddIns>");
var bad = PipeSizer.Updater.ApplyStagedUpdate(tmpBad);
Check(!bad.Applied, "updater staged sha mismatch not applied");
Check(!Directory.Exists(stagingBad), "updater staged sha mismatch staging deleted");
Check(!File.Exists(Path.Combine(tmpBad, "PipeSizer.9.9.9.dll")), "updater staged sha mismatch no main written");
Eq(XDocument.Load(Path.Combine(tmpBad, "PipeSizer.addin")).Descendants("Assembly").First().Value, "PipeSizer.dll", "updater staged sha mismatch manifest untouched");
Directory.Delete(tmpBad, true);

Console.WriteLine("updater: PASS");
Console.WriteLine("ALL TESTS PASSED");
