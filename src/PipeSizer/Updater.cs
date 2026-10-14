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
/// </summary>
public sealed record UpdateOutcome(bool UpdateStaged, string? Version, string? Notice, string? Error)
{
    public static UpdateOutcome Staged(string version) =>
        new(true, version, $"Update v{version} downloaded — restart Revit to apply.", null);

    public static UpdateOutcome AlreadyCurrent(string version) =>
        new(false, version, $"PipeSizer is up to date (v{version}).", null);

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
///  - On-demand ("Check for updates" button): Sizing IS already loaded, so the
///    update is only downloaded + verified + staged in update-staging/; the next
///    startup applies it before anything loads Sizing.
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

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

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

    /// <summary>TaskDialog text for an update-check outcome: the staged notice when
    /// an update was staged, the error on failure, "You're on the latest version
    /// (vX)." when already current, else the notice. Pure logic, so the panel and
    /// the startup dialog share one wording.</summary>
    public static string DialogText(UpdateOutcome outcome)
    {
        if (outcome.UpdateStaged)
            return outcome.Notice ?? $"Update v{outcome.Version} downloaded — restart Revit to apply.";
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

    // ----- On-demand path (Sizing already loaded: stage only) -----

    /// <summary>On-demand check: download + verify + stage a newer release in
    /// update-staging/. Does NOT touch the manifest or the loaded Sizing DLL.
    /// Returns Failed with a message on error (the caller shows it).</summary>
    public static UpdateOutcome CheckAndStageForRestart()
    {
        try
        {
            string? folder = GetAddinsFolder();
            if (string.IsNullOrEmpty(folder))
                return UpdateOutcome.Failed("Could not locate the Addins folder.");

            var release = GetLatestReleaseAsync().GetAwaiter().GetResult();
            if (release is null)
                return UpdateOutcome.Failed("Could not reach GitHub.");

            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            if (CompareVersions(release.TagVersion, current) <= 0)
                return UpdateOutcome.AlreadyCurrent(ParseVersionFromTag(release.Tag) ?? current);

            var bundle = DownloadVerifiedBundleAsync(release).GetAwaiter().GetResult();
            if (bundle is null)
                return UpdateOutcome.Failed("Could not verify the download (SHA256 mismatch or missing files).");

            string staging = Path.Combine(folder, StagingFolderName);
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, $"PipeSizer.{release.TagVersion}.dll"), bundle.MainDll);
            File.WriteAllBytes(Path.Combine(staging, SizingAssemblyFile), bundle.SizingDll);
            File.WriteAllText(Path.Combine(staging, VersionFileName), bundle.VersionJsonText);
            return UpdateOutcome.Staged(release.TagVersion);
        }
        catch (Exception ex)
        {
            return UpdateOutcome.Failed(ex.Message);
        }
    }

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
    /// (loaded assembly, or a just-applied staged update).</summary>
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

            string versionedName = $"PipeSizer.{release.TagVersion}.dll";
            string versionedPath = Path.Combine(folder, versionedName);
            string loaded = Assembly.GetExecutingAssembly().Location;
            if (IsSameFile(versionedPath, loaded))
                return UpdateOutcome.Noop(); // never overwrite the loaded file

            File.WriteAllBytes(versionedPath, bundle.MainDll);
            File.WriteAllBytes(Path.Combine(folder, SizingAssemblyFile), bundle.SizingDll);
            RewriteManifest(manifestPath, versionedName);
            return UpdateOutcome.Staged(release.TagVersion);
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
