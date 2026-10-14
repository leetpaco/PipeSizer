# PipeSizer — Live Pipe-Sizing Check for Revit 2025

A check-only Revit add-in (C#). It watches the model as you draw and flags pipes
whose size fails the sizing rules — it never modifies the model.

Source: https://github.com/leetpaco/PipeSizer — updates are automatic (see
[Auto-update](#auto-update)).

## Install

1. Copy these files into the Revit 2025 add-ins folder:
   - `PipeSizer.dll`
   - `PipeSizer.Sizing.dll`
   - `PipeSizer.addin`
   - (`PipeSizer.pdb` / `PipeSizer.Sizing.pdb` are optional debug symbols; harmless to copy.)

   The folder is: `%APPDATA%\Autodesk\Revit\Addins\2025\`
   (typically `C:\Users\<you>\AppData\Roaming\Autodesk\Revit\Addins\2025\`)
2. Restart Revit.
3. Open the pane with the **PipeSizer** ribbon tab → **Pipe Sizer** button.
   (The pane docks on the right; drag it wherever you like.) The ribbon also
   has a **Check for Updates** button that runs the update check without
   closing Revit — the new version still only loads after a restart.
   If the ribbon button ever fails to appear, the pane opens automatically
   on startup and the reason is shown in the panel notice; startup details
   are logged to `%TEMP%\PipeSizer-startup.log`.

## Auto-update

Installed copies check GitHub for a new release on Revit startup and on
**Check for updates** in the panel. After that, updates are automatic —
no more hand-copying DLLs.

How it works: Revit locks the loaded DLLs while it runs, so the add-in
cannot overwrite itself mid-session. Instead, on startup (before the sizing
code loads) it downloads the new release, verifies each file's SHA256
against the release's `version.json`, writes the new main DLL as
`PipeSizer.<version>.dll`, overwrites `PipeSizer.Sizing.dll` in place, and
rewrites the `.addin` manifest to point at the versioned DLL. The panel
then shows "Update vX.Y.Z downloaded — restart Revit to apply." A
**Check for updates** click does the same check but only stages the files;
the next startup applies them. No network (or any error) silently skips
the check — startup is never blocked. Stale versioned DLLs are cleaned up
on startup.

The panel header always shows the running version
("PipeSizer — v1.1.2 — live pipe sizing check"), so you can see at a glance
which build is loaded. When a startup update is staged, Revit also shows a
"PipeSizer Update" dialog once on first idle ("Update vX.Y.Z downloaded —
restart Revit to apply") — OK button only, no restart button, since closing
Revit programmatically risks unsaved work. A **Check for updates** click
shows its outcome as a dialog too, so the result is visible even if the
pane gets closed.

## Cutting a release

1. Bump `<Version>` (and `<AssemblyVersion>`) in the three `.csproj` files.
2. Commit and push to `main`.
3. Tag the release and push the tag:
   ```
   git tag v1.2.0
   git push origin v1.2.0
   ```
   The `release` GitHub Actions workflow builds Release, computes SHA256
   hashes, writes `version.json`, and creates the GitHub Release with
   `PipeSizer.dll`, `PipeSizer.Sizing.dll`, `PipeSizer.addin`, and
   `version.json`. Installed copies pick it up on their next launch.

## Usage

- The panel re-checks automatically ~1.5 s after you change the model, or click
  **Refresh** any time. Auto re-checks only run when a change touches pipes,
  equipment/fixtures/fittings, systems, or pipe types — moving a wall or
  editing text does not re-trigger a full check.
- **Results** list every pipe that fails a check: ElementId, system, category,
  flow, current size, required size, check type, actual value vs limit, status.
  **Click a row** to select that pipe in Revit; **double-click** to select +
  zoom to it.
- **Pause live checking** stops automatic re-checks (manual Refresh still works).
- **Show all pipes** includes the OK rows too. No-flow pipes always show as
  Info rows ("No flow — skipped").
- **Systems** lists every MEP system in the model and the category it is
  checked as. Click a system, then use the category dropdown to override it
  (e.g. mark a misnamed system as Gas). Overrides last for the session.
- **Settings** — hydronic/domestic material, velocity + friction limits,
  gas design length, and gas fuel / pressure / material. Changing any setting
  re-checks immediately.

## Methods (same as the Dynamo graphs / web app)

- **Gas:** IFGC Tables 402.4(2), (5), (15), (18), (21), (22), (27), (28),
  (32), (33), (35), (36), longest-length method. One design length
  (default 100 ft) applies to every gas pipe. Flow CFH = Revit Flow × 127132.8.
  The panel's **gas fuel / pressure / material** dropdowns pick the table:

  | Fuel | Pressure | Material | IFGC table | Conditions |
  | --- | --- | --- | --- | --- |
  | Natural gas | Low (< 2 psi) | Metallic (Sch 40) | 402.4(2) | 0.5 in. w.c. drop, SG 0.60, CFH |
  | Natural gas | 2 psi | Metallic (Sch 40) | 402.4(5) | 1.0 psi drop, SG 0.60, CFH |
  | Natural gas | Low (< 2 psi) | CSST | 402.4(15) | 0.5 in. w.c. drop, SG 0.60, CFH, EHD |
  | Natural gas | 2 psi | CSST | 402.4(18) | 1.0 psi drop, SG 0.60, CFH, EHD |
  | Natural gas | Low (< 2 psi) | PE pipe | 402.4(21) | 0.5 in. w.c. drop, SG 0.60, CFH |
  | Natural gas | 2 psi | PE pipe | 402.4(22) | 1.0 psi drop, SG 0.60, CFH |
  | Propane | Low (11 in. w.c.) | Metallic (Sch 40) | 402.4(28) | 0.5 in. w.c. drop, SG 1.50, MBH |
  | Propane | 2 psi | Metallic (Sch 40) | 402.4(27) | 1.0 psi drop, SG 1.50, MBH |
  | Propane | Low (11 in. w.c.) | CSST | 402.4(32) | 0.5 in. w.c. drop, SG 1.50, MBH, EHD |
  | Propane | 2 psi | CSST | 402.4(33) | 1.0 psi drop, SG 1.50, MBH, EHD |
  | Propane | Low (11 in. w.c.) | PE pipe | 402.4(35) | 0.5 in. w.c. drop, SG 1.50, MBH |
  | Propane | 2 psi | PE pipe | 402.4(36) | 1.0 psi drop, SG 1.50, MBH |

  Source for all tables: ICC-published 2021 IFGC Chapter 4
  (https://codes.iccsafe.org/content/IFGC2021P2/chapter-4-gas-piping-installations).
  Table values were transcribed from that source and validated (row/column
  counts, monotonicity, anchor values) — never from memory.
- **Propane units:** propane tables are in MBH, so Revit CFH is converted
  with an assumed propane heating value of **2,500 Btu/ft³**
  (MBH = CFH × 2.5, standard NFPA 54 convention). This is a tool assumption,
  not a value printed in IFGC Chapter 4.
- **CSST:** sized by **EHD** (Equivalent Hydraulic Diameter). The IFGC CSST
  tables are generic EHD tables, not manufacturer-specific. EHD has no fixed
  mapping from nominal size (it varies by manufacturer), so the panel does
  NOT pass/fail the Revit nominal diameter against EHD — it reports the
  **required EHD** as an Info row; verify the installed tube's listed EHD
  meets or exceeds it. CSST tables include losses for four 90° bends and
  two end fittings; runs with more bends/fittings need L = 1.3n ft of
  extra tubing length added to the design length.
- **PE pipe:** tables cover nominal OD 1/2 in. through 4 in. (SDR 9 for
  1/2 in., SDR 10 for 1-1/4 in., SDR 11 otherwise; actual IDs 0.660–3.682).
  A PE pipe larger than 4 in. is treated as at least the 4 in. capacity
  (it can only carry more). PE tubing (CTS) tables are NOT included — PE
  pipe and PE tubing are different products.
- **2 psi systems:** the 2 psi tables size the run between the service
  regulator and the line pressure regulator and do NOT include the
  pressure drop across the line regulator. The CSST 2 psi tables carry an
  explicit warning: do not use Table 402.4(18) if regulator loss exceeds
  3/4 psi (natural gas), or Table 402.4(33) if it exceeds 1/2 psi
  (propane, based on 13 in. w.c. outlet). Table capacities can also exceed
  a selected regulator's max — check the regulator separately.
- **Loads above the table's largest size** show as Oversize.
- **Hydronic:** Darcy-Weisbach + Swamee-Jain at 180 °F water, smallest size
  meeting BOTH velocity and friction limits. Straight-pipe friction only.
- **Domestic:** Hazen-Williams + velocity, smallest size meeting BOTH limits.
  Uses Revit's computed pipe Flow (already reflects fixture-unit diversity).

System classification: gas is detected by system name (Revit has no gas system
classification). Domestic/hydronic come from the system type's classification
(`MEPSystemType.SystemClassification` via the system's type element), with
name keywords as a fallback for custom-named systems. Pipes in systems
classified as anything else (sanitary, vent, fire, …) are ignored.

## Limits

- Check-only: no apply-sizes, no view coloring, no glycol.
- Hydronic water temperature is fixed at 180 °F (no UI input).
- No system-level residual-pressure check (use the web app for the critical run).
- Category overrides and settings are session-only (not saved between Revit runs).
- The checker reads Revit's computed pipe Flow: a system must be connected and
  calculated or its pipes show as "No flow — skipped".
- A pipe whose current size is not in the selected material's table shows an
  Info row ("cannot evaluate") instead of a pass/fail.
- Gas tables NOT included (out of scope for the fuel/pressure/material matrix
  above): other pressure drops (0.3 / 3.0 / 6.0 in. w.c.), 3 psi and 5 psi
  systems, semirigid copper tubing tables, PE tubing (CTS) tables, and any
  manufacturer-specific CSST tables (only the generic IFGC EHD tables are
  used). If a future fuel/pressure/material combo has no table, its pipes
  show "No IFGC table for this configuration" instead of being sized.
- Known published-table anomaly: as printed in the 2021 IFGC, Table
  402.4(36)'s 400 ft / 3 in. cell reads 12,000 MBH — lower than the 450 ft
  value (18,700). It is transcribed as printed, not "corrected"; near
  400 ft the 3 in. PE propane 2 psi results are conservative. Do not
  change it without an ICC errata.

## Troubleshooting

- **Pane doesn't appear:** make sure all files are in `Addins\2025\` (not a
  subfolder) and that Revit was restarted after copying. The `.addin` file
  must sit next to `PipeSizer.dll`.
- **Panel shows an error:** the status line at the bottom shows the message.
  "No flow — skipped" on every pipe means the system isn't calculated yet
  (check connections / system calculations).
- **Wrong category for a system:** use the Systems list override (see Usage).

## Rebuild from source

Requires .NET 8 SDK. From `src/`:

```
dotnet build -c Release
```

Output DLLs land in `src/PipeSizer/bin/Release/net8.0-windows/` and
`src/PipeSizer.Sizing/bin/Release/net8.0/`. Copy `PipeSizer.dll`,
`PipeSizer.Sizing.dll` (+ PDBs) next to `PipeSizer.addin`.

Or open `src/PipeSizer.sln`-equivalent by opening the `PipeSizer` project in
Visual Studio 2022 and building Release. The Revit API references come from
the `Nice3point.Revit.Api.RevitAPI[UI]` NuGet packages (Revit 2025, v25.4.60)
and are compile-only — Revit supplies the real DLLs at runtime.

## Layout

- `PipeSizer.addin` — manifest (AddInId, FullClassName `PipeSizer.App`).
- `PipeSizer.dll` / `PipeSizer.Sizing.dll` — compiled add-in.
- `src/PipeSizer.Sizing/` — pure sizing math, zero Revit references
  (`Sizing/GasTable.cs` is the generated IFGC Table 402.4(2);
  `Sizing/GasTables.cs` is the generated Tables 402.4(5), (15), (18), (21),
  (22), (27), (28), (32), (33), (35), (36) — neither is hand-typed;
  `Sizing/GasConfig.cs` holds the fuel/pressure/material selection).
- `src/PipeSizer/` — Revit glue: `App.cs` (startup, events, ExternalEvent),
  `Checker.cs` (model → rows), `PipeSizerPanel.cs` (code-only WPF panel),
  `Settings.cs`, `PipeRow.cs`, `Updater.cs` (GitHub self-update, BCL-only).
- `src/PipeSizer.Tests/` — console harness asserting the sizing hand-checks
  (`dotnet run` in that folder).
