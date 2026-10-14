using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace PipeSizer;

/// <summary>
/// Result of an update check. <see cref="Notice"/> is user-facing text (shown in the
/// panel); <see cref="Error"/> is only surfaced for an on-demand check the user asked for.
/// Startup checks fail silently (Noop) so a missing network never blocks Revit.
///
/// Two "ready" shapes: <see cref="Applied"/> / <see cref="AlreadyApplied"/> mean the
/// update is fully applied (versioned main DLL on disk, Sizing overwritten, manifest
/// rewritten) — one restart loads it. <see cref="Staged"/> / <see cref="AlreadyStaged"/>
/// mean the files are only downloaded to update-staging/ (the fallback when the
/// Sizing DLL could not be overwritten while Revit runs) — two restarts are needed
/// (next launch applies the staged files, the one after loads them).
/// </summary>
public sealed record UpdateOutcome(bool UpdateStaged, string? Version, string? Notice, string? Error)
{
    /// <summary>Files only staged (fallback): two restarts to load.</summary>
    public static UpdateOutcome Staged(string version) =>
        new(true, version, $"Update v{version} downloaded — restart Revit twice to apply.", null);

    /// <summary>Fully applied mid-session: one restart loads it.</summary>
    public static UpdateOutcome Applied(string version) =>
        new(true, version, $"Update v{version} ready — restart Revit once to apply.", null);

    public static UpdateOutcome AlreadyCurrent(string version) =>
        new(false, version, $"PipeSizer is up to date (v{version}).", null);

    /// <summary>An update is already downloaded to update-staging/ and waiting
    /// (previous on-demand check fell back to staging). Two restarts to load.</summary>
    public static UpdateOutcome AlreadyStaged(string version) =>
        new(true, version, $"Update v{version} already downloaded — restart Revit twice to apply.", null);

    /// <summary>An update is already fully applied (manifest points at the newer
    /// versioned DLL). One restart loads it.</summary>
    public static UpdateOutcome AlreadyApplied(string version) =>
        new(true, version, $"Update v{version} already ready — restart Revit once to apply.", null);

    public static UpdateOutcome Noop() => new(false, null, null, null);

    public static UpdateOutcome Failed(string error) =>
        new(false, null, null, "Update check failed: " + error);
}

/// <summary>
/// Self-update against GitHub Releases (public repo leetpaco/PipeSizer).
///
/// BCL ONLY — this class must never reference PipeSizer.Sizing types (or anything that
/// does), because the startup check runs before the Sizing DLL is loaded and may
/// overwrite it. Revit locks the loaded main DLL, so updates work like this:
///  - Startup (before the panel is created, so Sizing is not yet loaded): apply any
///    staged update, clean up stale versioned files, then check GitHub. If a newer
///    release exists, download both DLLs, verify SHA256 against version.json, write
///    the new main DLL as PipeSizer.&lt;version&gt;.dll (never the loaded file),
///    overwrite PipeSizer.Sizing.dll in place, and rewrite the .addin manifest's
///    &lt;Assembly&gt; to the versioned file. The new main DLL loads next launch.
///  - On-demand ("Check for updates" button) and the automatic background check:
///    Sizing IS already loaded, so the update is downloaded + verified + FULLY
///    APPLIED in place (same three writes as startup: versioned main DLL, Sizing,
///    manifest). On .NET 8 the runtime does not lock loaded assembly files, so the
///    running session keeps using its in-memory copy and the new files load on the
///    next launch — one restart. If overwriting Sizing throws, the update falls
///    back to staging-only in update-staging/ (the next startup applies it, so
///    that path needs two restarts) and the manifest is left pointing at the
///    previously working version.
/// </summary>
public static class Updater
{
    public const string RepoOwner = "leetpaco";
    public const string RepoName = "PipeSizer";
    public const string MainAssemblyFile = "PipeSizer.dll";
    public const string SizingAssemblyFile = "PipeSizer.Sizing.dll";
    public const string ManifestFile = "PipeSizer.addin";
    public const string StagingFolderName = "update-staging";
    public const string VersionFileName = "version.json";

    // Internal (not private) so the test harness — which compiles Updater.cs
    // directly — can pin the timeout values.
    internal static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    // ----- Pure helpers (unit-tested) -----

    /// <summary>Numeric SemVer compare: -1, 0, +1. Leading 'v' ignored; missing segments
    /// count as 0 (so 1.1.0 == 1.1.0.0); a pre-release suffix sorts below the release.</summary>
    public static int CompareVersions(string a, string b)
    {
        var (aSegs, aPre) = ParseVersion(a);
        var (bSegs, bPre) = ParseVersion(b);
        int len = Math.Max(aSegs.Length, bSegs.Length);
        for (int i = 0; i < len; i++)
        {
            int av = i < aSegs.Length ? aSegs[i] : 0;
            int bv = i < bSegs.Length ? bSegs[i] : 0;
            if (av != bv)
                return av.CompareTo(bv);
        }
        // Segments equal: release beats pre-release.
        if (aPre == bPre)
            return 0;
        return aPre ? -1 : 1;
    }

    /// <summary>Strips a leading 'v' from a tag ("v1.1.0" -> "1.1.0"). Null/empty stays null.</summary>
    public static string? ParseVersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        tag = tag.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            tag = tag[1..];
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>Display form of an AssemblyVersion: "1.1.2" for 1.1.2.0 (trailing
    /// .0 revision dropped), "1.1.2.3" when the revision is nonzero. Null -> "unknown".
    /// Used for the panel header, read at runtime — never hardcoded.</summary>
    public static string DisplayVersion(Version? version)
    {
        if (version is null)
            return "unknown";
        if (version.Build < 0)
            return $"{version.Major}.{version.Minor}";
        string text = $"{version.Major}.{version.Minor}.{version.Build}";
        return version.Revision > 0 ? text + "." + version.Revision : text;
    }

    /// <summary>How often the automatic background check runs (at most once per
    /// interval, gated on elapsed time in the Idling path).</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>True when an automatic background update check is due: the last
    /// check was at least <paramref name="interval"/> ago. A last-check time in
    /// the future (clock skew) is never due. Pure, unit-tested.</summary>
    public static bool IsAutoCheckDue(DateTimeOffset lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval) =>
        nowUtc >= lastCheckUtc && (nowUtc - lastCheckUtc) >= interval;

    /// <summary>Notify-dedup for the background check: returns true (and records the
    /// version) the first time a version is seen, false afterwards — so the
    /// "update ready" popup shows ONCE per version per session. Null/empty
    /// versions never notify. Pure, unit-tested.</summary>
    public static bool ShouldNotifyForVersion(HashSet<string> notifiedVersions, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        return notifiedVersions.Add(version);
    }

    /// <summary>TaskDialog text for an update-check outcome: the staged notice when
    /// an update was staged, the error on failure, "You're on the latest version
    /// (vX)." when already current, else the notice. Pure logic, so the panel and
    /// the startup dialog share one wording.</summary>
    public static string DialogText(UpdateOutcome outcome)
    {
        if (outcome.UpdateStaged)
            return outcome.Notice ?? $"Update v{outcome.Version} ready — restart Revit once to apply.";
        if (outcome.Error is not null)
            return outcome.Error;
        if (outcome.Version is not null)
            return $"You're on the latest version (v{outcome.Version}).";
        return outcome.Notice ?? "No update available.";
    }

    public static string ComputeSha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool VerifySha256(byte[] data, string? expectedHex) =>
        !string.IsNullOrWhiteSpace(expectedHex) &&
        string.Equals(ComputeSha256Hex(data), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when both paths name the same file (full path, case-insensitive —
    /// the target is Windows). Used to refuse writing over the loaded assembly.</summary>
    public static bool IsSameFile(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Rewrites the manifest's &lt;Assembly&gt; element to a new file name.
    /// Returns the previous value. Throws if the manifest has no Assembly element.</summary>
    public static string RewriteManifest(string manifestPath, string newAssemblyFileName)
    {
        var doc = XDocument.Load(manifestPath);
        var el = doc.Descendants("Assembly").FirstOrDefault()
            ?? throw new InvalidOperationException("Manifest has no <Assembly> element: " + manifestPath);
        string old = el.Value;
        el.Value = newAssemblyFileName;
        doc.Save(manifestPath);
        return old;
    }

    /// <summary>Deletes stale PipeSizer.&lt;version&gt;.dll files in <paramref name="folder"/>,
    /// except the currently loaded assembly, the Sizing DLL, and the assembly the
    /// manifest currently points at (a staged update's target — deleting it would
    /// break the next launch). Also deletes the unversioned PipeSizer.dll when the
    /// manifest no longer points at it. Never throws.</summary>
    public static void CleanupStale(string folder, string manifestAssemblyFileName)
    {
        try
        {
            string loaded = Assembly.GetExecutingAssembly().Location;
            foreach (var file in Directory.EnumerateFiles(folder, "PipeSizer.*.dll"))
            {
                try
                {
                    string name = Path.GetFileName(file);
                    if (IsSameFile(file, loaded))
                        continue;
                    if (string.Equals(name, SizingAssemblyFile, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(name, manifestAssemblyFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Delete(file);
                }
                catch { /* best effort */ }
            }
            if (!string.Equals(manifestAssemblyFileName, MainAssemblyFile, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string unversioned = Path.Combine(folder, MainAssemblyFile);
                    if (File.Exists(unversioned) && !IsSameFile(unversioned, loaded))
                        File.Delete(unversioned);
                }
                catch { /* best effort */ }
            }
        }
        catch { /* cleanup must never fail startup */ }
    }

    // ----- Startup path (Sizing not yet loaded) -----

    /// <summary>Full startup flow: apply staged update, clean up, check GitHub, apply a
    /// newer release in place. Never throws; failures silently return Noop.</summary>
    public static UpdateOutcome RunStartupUpdate()
    {
        try
        {
            string? folder = GetAddinsFolder();
            if (string.IsNullOrEmpty(folder))
                return UpdateOutcome.Noop();

            string manifestPath = Path.Combine(folder, ManifestFile);
            string manifestAssembly = ReadManifestAssembly(manifestPath) ?? MainAssemblyFile;

            // A previous session's on-demand check may have staged an update.
            var staged = ApplyStagedUpdate(folder);
            string effectiveVersion = staged.Applied && staged.Version is not null
                ? staged.Version
                : Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            if (staged.Applied)
                manifestAssembly = staged.ManifestAssembly ?? manifestAssembly;

            CleanupStale(folder, manifestAssembly);

            var outcome = CheckGitHubAndApply(folder, manifestPath, effectiveVersion);
            return outcome;
        }
        catch
        {
            return UpdateOutcome.Noop();
        }
    }

    // ----- On-demand path (Sizing already loaded: fully apply in place) -----

    /// <summary>On-demand check (manual button or automatic background check):
    /// download + verify + FULLY APPLY a newer release in place — versioned main
    /// DLL, Sizing overwrite, manifest rewrite — so one restart loads it. The
    /// running session keeps using its in-memory copies (.NET 8 does not lock
    /// loaded assembly files). If overwriting Sizing throws, falls back to
    /// staging-only in update-staging/ (two restarts to load) and leaves the
    /// manifest pointing at the previously working version — never a
    /// half-applied state, never a silent failure. If an update is already
    /// applied or staged, reports it instead of re-downloading. Returns Failed
    /// with a message on error (the caller shows it).</summary>
    public static UpdateOutcome CheckAndApplyForRestart()
    {
        try
        {
            string? folder = GetAddinsFolder();
            if (string.IsNullOrEmpty(folder))
                return UpdateOutcome.Failed("Could not locate the Addins folder.");

            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            string manifestPath = Path.Combine(folder, ManifestFile);

            var release = GetLatestReleaseAsync().GetAwaiter().GetResult();
            if (release is null)
            {
                // Offline: a pending update is still worth reporting.
                var pendingOffline = GetPendingUpdate(folder, current);
                return pendingOffline.Version is not null
                    ? (pendingOffline.FullyApplied
                        ? UpdateOutcome.AlreadyApplied(pendingOffline.Version)
                        : UpdateOutcome.AlreadyStaged(pendingOffline.Version))
                    : UpdateOutcome.Failed("Could not reach GitHub.");
            }

            // Already applied or staged, at least as new as the latest release:
            // report it, never re-download.
            var pending = GetPendingUpdate(folder, current);
            if (pending.Version is not null && CompareVersions(pending.Version, release.TagVersion) >= 0)
                return pending.FullyApplied
                    ? UpdateOutcome.AlreadyApplied(pending.Version)
                    : UpdateOutcome.AlreadyStaged(pending.Version);

            if (CompareVersions(release.TagVersion, current) <= 0)
                return UpdateOutcome.AlreadyCurrent(ParseVersionFromTag(release.Tag) ?? current);

            var bundle = DownloadVerifiedBundleAsync(release).GetAwaiter().GetResult();
            if (bundle is null)
                return UpdateOutcome.Failed("Could not verify the download (SHA256 mismatch or missing files).");

            var applied = TryApplyBundle(folder, manifestPath, release.TagVersion, bundle);
            if (applied == ApplyResult.Applied)
                return UpdateOutcome.Applied(release.TagVersion);
            if (applied == ApplyResult.SizingWriteFailed)
            {
                // Sizing could not be overwritten while Revit runs: fall back to
                // staging-only (the next startup applies it). The manifest was
                // left pointing at the previously working version.
                StageBundle(folder, release.TagVersion, bundle);
                return UpdateOutcome.Staged(release.TagVersion);
            }
            return UpdateOutcome.Failed("Could not apply the update (file write failed).");
        }
        catch (Exception ex)
        {
            return UpdateOutcome.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Result of <see cref="TryApplyBundle"/>.</summary>
    private enum ApplyResult
    {
        Applied,
        /// <summary>The versioned main DLL was written but Sizing could not be
        /// overwritten (e.g. locked by another process). Caller falls back to
        /// staging-only; the manifest was NOT touched.</summary>
        SizingWriteFailed,
        Failed,
    }

    /// <summary>Applies a verified bundle in place, in order: versioned main DLL
    /// (new file — never the loaded one), Sizing overwrite, manifest rewrite.
    /// On any failure the manifest is left pointing at the previously working
    /// version; a partially written versioned DLL is removed, and a Sizing
    /// overwrite that was followed by a manifest failure is rolled back from
    /// the bytes read before overwriting. Never throws.</summary>
    private static ApplyResult TryApplyBundle(string folder, string manifestPath, string version, VerifiedBundle bundle)
    {
        string versionedName = $"PipeSizer.{version}.dll";
        string versionedPath = Path.Combine(folder, versionedName);
        string sizingPath = Path.Combine(folder, SizingAssemblyFile);
        byte[]? oldSizing = null;
        bool wroteVersioned = false;
        try
        {
            string loaded = Assembly.GetExecutingAssembly().Location;
            if (IsSameFile(versionedPath, loaded))
                return ApplyResult.Failed; // never overwrite the loaded file

            File.WriteAllBytes(versionedPath, bundle.MainDll);
            wroteVersioned = true;

            try { if (File.Exists(sizingPath)) oldSizing = File.ReadAllBytes(sizingPath); } catch { /* rollback best effort */ }
            try
            {
                File.WriteAllBytes(sizingPath, bundle.SizingDll);
            }
            catch
            {
                // Sizing could not be overwritten: remove the orphan versioned DLL
                // and report so the caller can fall back to staging-only.
                try { if (wroteVersioned) File.Delete(versionedPath); } catch { /* best effort */ }
                return ApplyResult.SizingWriteFailed;
            }

            try
            {
                RewriteManifest(manifestPath, versionedName);
            }
            catch
            {
                // Manifest rewrite failed: roll Sizing back to the previous bytes
                // so the old manifest target + old Sizing stay a working pair.
                try { if (oldSizing is not null) File.WriteAllBytes(sizingPath, oldSizing); } catch { /* best effort */ }
                try { if (wroteVersioned) File.Delete(versionedPath); } catch { /* best effort */ }
                return ApplyResult.Failed;
            }
            return ApplyResult.Applied;
        }
        catch
        {
            try { if (wroteVersioned) File.Delete(versionedPath); } catch { /* best effort */ }
            return ApplyResult.Failed;
        }
    }

    /// <summary>Writes a verified bundle to update-staging/ (fallback path).
    /// Never throws.</summary>
    private static void StageBundle(string folder, string version, VerifiedBundle bundle)
    {
        try
        {
            string staging = Path.Combine(folder, StagingFolderName);
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, $"PipeSizer.{version}.dll"), bundle.MainDll);
            File.WriteAllBytes(Path.Combine(staging, SizingAssemblyFile), bundle.SizingDll);
            File.WriteAllText(Path.Combine(staging, VersionFileName), bundle.VersionJsonText);
        }
        catch { /* staging is best effort; the caller already reports Staged */ }
    }

    /// <summary>An already-downloaded update: its version, and whether it is fully
    /// applied (manifest already points at the newer versioned DLL — one restart
    /// loads it) or only staged in update-staging/ (two restarts). When both
    /// exist, the newer version wins; a tie goes to the fully-applied state.
    /// Never throws; corrupt/partial staging is ignored.</summary>
    public static (string? Version, bool FullyApplied) GetPendingUpdate(string folder, string currentVersion)
    {
        try
        {
            // (a) staging dir from a previous on-demand fallback.
            string? stagedVersion = null;
            string staging = Path.Combine(folder, StagingFolderName);
            string versionJsonPath = Path.Combine(staging, VersionFileName);
            if (File.Exists(versionJsonPath))
            {
                var manifest = ParseVersionJson(File.ReadAllText(versionJsonPath));
                if (manifest is not null)
                {
                    string stagedMain = Path.Combine(staging, $"PipeSizer.{manifest.Version}.dll");
                    string stagedSizing = Path.Combine(staging, SizingAssemblyFile);
                    if (File.Exists(stagedMain) && File.Exists(stagedSizing)
                        && VerifySha256(File.ReadAllBytes(stagedMain), manifest.MainSha256)
                        && VerifySha256(File.ReadAllBytes(stagedSizing), manifest.SizingSha256)
                        && CompareVersions(manifest.Version, currentVersion) > 0)
                        stagedVersion = manifest.Version;
                }
            }
            // (b) manifest already points at a newer versioned DLL (fully applied:
            // startup check, or a previous on-demand check applied it in place).
            string? appliedVersion = null;
            string? manifestAssembly = ReadManifestAssembly(Path.Combine(folder, ManifestFile));
            if (manifestAssembly is not null
                && manifestAssembly.StartsWith("PipeSizer.", StringComparison.OrdinalIgnoreCase)
                && manifestAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifestAssembly, MainAssemblyFile, StringComparison.OrdinalIgnoreCase))
            {
                string ver = manifestAssembly["PipeSizer.".Length..^".dll".Length];
                if (ver.Length > 0 && CompareVersions(ver, currentVersion) > 0
                    && File.Exists(Path.Combine(folder, manifestAssembly)))
                    appliedVersion = ver;
            }
            if (appliedVersion is not null
                && (stagedVersion is null || CompareVersions(appliedVersion, stagedVersion) >= 0))
                return (appliedVersion, true);
            if (stagedVersion is not null)
                return (stagedVersion, false);
            return (null, false);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>Version of an update that is already downloaded and waiting for a
    /// restart, or null. Delegates to <see cref="GetPendingUpdate"/> (the
    /// FullyApplied distinction is in the caller's outcome, not the version).</summary>
    public static string? GetPendingUpdateVersion(string folder, string currentVersion) =>
        GetPendingUpdate(folder, currentVersion).Version;

    // ----- Staged-update apply (runs at startup, before Sizing loads) -----

    /// <summary>Applies a previously staged update: verifies SHAs against the staged
    /// version.json, copies Sizing into place, writes the versioned main DLL, rewrites
    /// the manifest, deletes staging. Returns whether anything was applied.</summary>
    public static (bool Applied, string? Version, string? ManifestAssembly) ApplyStagedUpdate(string folder)
    {
        string staging = Path.Combine(folder, StagingFolderName);
        string manifestPath = Path.Combine(folder, ManifestFile);
        try
        {
            if (!Directory.Exists(staging))
                return (false, null, null);
            string versionJsonPath = Path.Combine(staging, VersionFileName);
            if (!File.Exists(versionJsonPath))
            {
                Directory.Delete(staging, true);
                return (false, null, null);
            }
            var manifest = ParseVersionJson(File.ReadAllText(versionJsonPath));
            if (manifest is null)
            {
                Directory.Delete(staging, true);
                return (false, null, null);
            }
            // Staged main DLL is named PipeSizer.<version>.dll.
            string stagedMain = Path.Combine(staging, $"PipeSizer.{manifest.Version}.dll");
            string stagedSizing = Path.Combine(staging, SizingAssemblyFile);
            if (!File.Exists(stagedMain) || !File.Exists(stagedSizing))
            {
                Directory.Delete(staging, true);
                return (false, null, null);
            }
            byte[] mainBytes = File.ReadAllBytes(stagedMain);
            byte[] sizingBytes = File.ReadAllBytes(stagedSizing);
            if (!VerifySha256(mainBytes, manifest.MainSha256) ||
                !VerifySha256(sizingBytes, manifest.SizingSha256))
            {
                Directory.Delete(staging, true);
                return (false, null, null);
            }
            string versionedName = $"PipeSizer.{manifest.Version}.dll";
            string versionedPath = Path.Combine(folder, versionedName);
            string loaded = Assembly.GetExecutingAssembly().Location;
            if (IsSameFile(versionedPath, loaded))
            {
                Directory.Delete(staging, true);
                return (false, null, null);
            }
            File.WriteAllBytes(versionedPath, mainBytes);
            File.WriteAllBytes(Path.Combine(folder, SizingAssemblyFile), sizingBytes);
            RewriteManifest(manifestPath, versionedName);
            Directory.Delete(staging, true);
            return (true, manifest.Version, versionedName);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
            return (false, null, null);
        }
    }

    // ----- GitHub -----

    private static string? GetAddinsFolder()
    {
        try
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadManifestAssembly(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                return null;
            var doc = XDocument.Load(manifestPath);
            return doc.Descendants("Assembly").FirstOrDefault()?.Value;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ReleaseInfo(string Tag, string TagVersion, List<(string Name, string Url)> Assets);

    private sealed record VersionManifest(string Version, string? MainSha256, string? SizingSha256);

    private sealed record VerifiedBundle(byte[] MainDll, byte[] SizingDll, string VersionJsonText);

    private static async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using var http = new HttpClient { Timeout = ApiTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PipeSizer-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.GetAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest").ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl))
            return null;
        string tag = tagEl.GetString() ?? "";
        string? tagVersion = ParseVersionFromTag(tag);
        if (tagVersion is null)
            return null;
        var assets = new List<(string, string)>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Length > 0 && url.Length > 0)
                    assets.Add((name, url));
            }
        }
        return new ReleaseInfo(tag, tagVersion, assets);
    }

    private static VersionManifest? ParseVersionJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var vEl))
                return null;
            string version = vEl.GetString() ?? "";
            string? mainSha = null, sizingSha = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                if (assets.TryGetProperty(MainAssemblyFile, out var m) &&
                    m.TryGetProperty("sha256", out var ms))
                    mainSha = ms.GetString();
                if (assets.TryGetProperty(SizingAssemblyFile, out var s) &&
                    s.TryGetProperty("sha256", out var ss))
                    sizingSha = ss.GetString();
            }
            if (version.Length == 0 || mainSha is null || sizingSha is null)
                return null;
            return new VersionManifest(version, mainSha, sizingSha);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<VerifiedBundle?> DownloadVerifiedBundleAsync(ReleaseInfo release)
    {
        // version.json first: its SHAs are what the DLL downloads are verified against.
        string versionJsonUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{release.Tag}/{VersionFileName}";
        string? versionJsonText = await TryDownloadTextAsync(versionJsonUrl).ConfigureAwait(false);
        if (versionJsonText is null)
        {
            // Fallback: the version.json asset URL from the API listing.
            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, VersionFileName, StringComparison.OrdinalIgnoreCase));
            if (asset.Url.Length > 0)
                versionJsonText = await TryDownloadTextAsync(asset.Url).ConfigureAwait(false);
        }
        if (versionJsonText is null)
            return null;
        var manifest = ParseVersionJson(versionJsonText);
        if (manifest is null)
            return null;

        string mainUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{release.Tag}/{MainAssemblyFile}";
        string sizingUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{release.Tag}/{SizingAssemblyFile}";
        byte[]? mainBytes = await TryDownloadBytesAsync(mainUrl).ConfigureAwait(false);
        byte[]? sizingBytes = await TryDownloadBytesAsync(sizingUrl).ConfigureAwait(false);
        if (mainBytes is null || sizingBytes is null)
            return null;
        if (!VerifySha256(mainBytes, manifest.MainSha256) ||
            !VerifySha256(sizingBytes, manifest.SizingSha256))
            return null;
        return new VerifiedBundle(mainBytes, sizingBytes, versionJsonText);
    }

    private static async Task<string?> TryDownloadTextAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PipeSizer-Updater");
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryDownloadBytesAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PipeSizer-Updater");
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Startup-only: download + verify + apply in place (Sizing not yet loaded).
    /// <paramref name="currentVersion"/> is the effective version already installed
    /// (loaded assembly, or a just-applied staged update). Returns Applied when a
    /// newer release was fully applied, Noop otherwise (already current, offline,
    /// or any failure — startup never reports update errors).</summary>
    private static UpdateOutcome CheckGitHubAndApply(string folder, string manifestPath, string currentVersion)
    {
        try
        {
            var release = GetLatestReleaseAsync().GetAwaiter().GetResult();
            if (release is null)
                return UpdateOutcome.Noop();
            if (CompareVersions(release.TagVersion, currentVersion) <= 0)
                return UpdateOutcome.Noop();

            var bundle = DownloadVerifiedBundleAsync(release).GetAwaiter().GetResult();
            if (bundle is null)
                return UpdateOutcome.Noop();

            // The on-demand path may already have fully applied this exact version
            // (manifest already points at it): skip re-applying, do not re-download,
            // do not loop. The version check above already covers it when the
            // loaded assembly IS the applied one; this covers the manifest-ahead
            // case (manifest points at a newer DLL than the loaded assembly,
            // e.g. a previous session applied it but Revit loaded the old one).
            var pending = GetPendingUpdate(folder, currentVersion);
            if (pending.Version is not null && pending.FullyApplied
                && CompareVersions(pending.Version, release.TagVersion) >= 0)
                return UpdateOutcome.Noop();

            return TryApplyBundle(folder, manifestPath, release.TagVersion, bundle) == ApplyResult.Applied
                ? UpdateOutcome.Applied(release.TagVersion)
                : UpdateOutcome.Noop();
        }
        catch
        {
            return UpdateOutcome.Noop();
        }
    }

    private static (int[] Segments, bool PreRelease) ParseVersion(string version)
    {
        string v = (version ?? "").Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        bool pre = false;
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            pre = true;
            v = v[..dash];
        }
        int plus = v.IndexOf('+');
        if (plus >= 0)
            v = v[..plus];
        var segs = new List<int>();
        foreach (var part in v.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int num = 0, i = 0;
            while (i < part.Length && char.IsDigit(part[i]))
            {
                num = num * 10 + (part[i] - '0');
                i++;
            }
            segs.Add(num);
        }
        if (segs.Count == 0)
            segs.Add(0);
        return (segs.ToArray(), pre);
    }
}
