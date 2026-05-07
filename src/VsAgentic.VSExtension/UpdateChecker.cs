using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsAgentic.VSExtension;

/// <summary>
/// Queries the VS Marketplace for the latest published version of this extension and,
/// if it's newer than the locally installed assembly, surfaces an InfoBar in the VS main
/// window prompting the user to update. This works around the slow built-in auto-update
/// cadence so users find out about new releases on the next IDE start instead of days later.
/// </summary>
internal sealed class UpdateChecker : IVsInfoBarUIEvents
{
    // Marketplace identifier (publisher.extensionName) — matches publishManifest.json
    private const string MarketplaceItemName = "adospace.VsAgentic";
    private const string MarketplaceListingUrl = "https://marketplace.visualstudio.com/items?itemName=" + MarketplaceItemName;

    // The Identity Id from source.extension.vsixmanifest — used to match our extension
    // across multiple install directories that VS may leave behind during updates.
    private const string ExtensionId = "VsAgentic.VSExtension.c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f";
    private const string ExtensionQueryUrl = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AsyncPackage _package;
    private uint _eventCookie;

    public UpdateChecker(AsyncPackage package) => _package = package;

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VsAgentic", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"updatechecker-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        Log("CheckAsync: start");
        try
        {
            var (running, bestOnDisk) = ReadVersions();
            Log($"CheckAsync: running = {running?.ToString() ?? "<null>"}, bestOnDisk = {bestOnDisk?.ToString() ?? "<null>"}");

            // A newer version of the extension exists on disk (in a sibling Extensions
            // folder with the same Identity Id), but VS picked an older folder at
            // process start and is loading those assemblies instead. The user must
            // fully restart VS — closing all devenv.exe instances — for the cleanup
            // sweep to remove the stale folder and load the new one. This is a
            // separate problem from "marketplace has a newer release," so surface it
            // first and skip the marketplace check until it's resolved.
            if (running is not null && bestOnDisk is not null && running < bestOnDisk)
            {
                Log($"CheckAsync: stale install detected ({running} loaded, {bestOnDisk} on disk)");
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                ShowStaleInstallInfoBar(running, bestOnDisk);
                return;
            }

            // Use bestOnDisk for the marketplace comparison: if anything newer is
            // actually installed (even unloaded), we shouldn't pester the user about
            // an "available update" they already have on disk.
            var localVersion = bestOnDisk ?? running;
            if (localVersion is null) return;

            var latest = await FetchLatestVersionAsync(ct).ConfigureAwait(false);
            Log($"CheckAsync: marketplace latest = {latest?.ToString() ?? "<null>"}");
            if (latest is null) return;

            if (latest > localVersion)
            {
                Log($"CheckAsync: newer version available, showing InfoBar ({localVersion} -> {latest})");
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                ShowInfoBar(localVersion, latest);
            }
            else
            {
                Log($"CheckAsync: up to date ({localVersion} >= {latest})");
            }
        }
        catch (OperationCanceledException)
        {
            Log("CheckAsync: cancelled");
        }
        catch (Exception ex)
        {
            Log($"CheckAsync: exception {ex}");
            Debug.WriteLine($"VsAgentic UpdateChecker: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns two versions: the one currently loaded (manifest next to the running
    /// assembly) and the highest version found across all sibling extension folders
    /// that share our Identity Id. When VS leaves a stale folder behind after an
    /// update, the two diverge — the older one keeps loading because its files were
    /// locked at cleanup time.
    /// </summary>
    private static (Version? running, Version? bestOnDisk) ReadVersions()
    {
        Version? running = null;
        Version? best = null;

        try
        {
            var asmDir = Path.GetDirectoryName(typeof(UpdateChecker).Assembly.Location);
            Log($"ReadVersions: asmDir = {asmDir ?? "<null>"}");
            if (string.IsNullOrEmpty(asmDir)) return (null, null);

            var ownManifest = Path.Combine(asmDir, "extension.vsixmanifest");
            if (File.Exists(ownManifest))
            {
                running = TryReadVersionFromManifest(ownManifest, extensionId: null);
                Log($"ReadVersions: running (own manifest) = {running?.ToString() ?? "<null>"}");
            }

            var extensionsRoot = Path.GetDirectoryName(asmDir);
            if (!string.IsNullOrEmpty(extensionsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(extensionsRoot))
                {
                    var candidate = Path.Combine(dir, "extension.vsixmanifest");
                    if (!File.Exists(candidate)) continue;
                    var v = TryReadVersionFromManifest(candidate, ExtensionId);
                    if (v is not null && (best is null || v > best))
                        best = v;
                }
                Log($"ReadVersions: bestOnDisk = {best?.ToString() ?? "<null>"}");
            }
        }
        catch (Exception ex)
        {
            Log($"ReadVersions: exception {ex}");
            Debug.WriteLine($"VsAgentic UpdateChecker: failed to read vsixmanifest: {ex.Message}");
        }

        if (running is null)
        {
            // Last-resort fallback. The assembly version doesn't track the VSIX version
            // (typically left at 1.0.0.0), so this is mostly to keep the marketplace
            // check alive when the manifest can't be read at all.
            running = Assembly.GetExecutingAssembly().GetName().Version;
            Log($"ReadVersions: fallback running = assembly version = {running?.ToString() ?? "<null>"}");
        }

        return (running, best);
    }

    /// <summary>
    /// Reads the version from an extension.vsixmanifest file.  When <paramref name="extensionId"/>
    /// is supplied, the manifest is only considered a match if its Identity Id equals the value.
    /// </summary>
    private static Version? TryReadVersionFromManifest(string path, string? extensionId)
    {
        try
        {
            var doc = XDocument.Load(path);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var identity = doc.Root?
                .Element(ns + "Metadata")?
                .Element(ns + "Identity");
            if (identity is null) return null;

            if (extensionId is not null)
            {
                var id = identity.Attribute("Id")?.Value;
                if (!string.Equals(id, extensionId, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            var versionAttr = identity.Attribute("Version");
            if (versionAttr is not null && Version.TryParse(versionAttr.Value, out var v))
            {
                Log($"TryReadVersionFromManifest: {path} -> {v}");
                return v;
            }
        }
        catch (Exception ex)
        {
            Log($"TryReadVersionFromManifest: {path} -> exception {ex.Message}");
        }

        return null;
    }

    private static async Task<Version?> FetchLatestVersionAsync(CancellationToken ct)
    {
        // Marketplace ExtensionQuery REST API.
        // filterType 7 = ExtensionName ("publisher.name"), flags 914 includes IncludeVersions.
        var body = "{\"filters\":[{\"criteria\":[{\"filterType\":7,\"value\":\""
                   + MarketplaceItemName
                   + "\"}],\"pageNumber\":1,\"pageSize\":1,\"sortBy\":0,\"sortOrder\":0}],\"flags\":914}";

        using var req = new HttpRequestMessage(HttpMethod.Post, ExtensionQueryUrl);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Accept.ParseAdd("application/json;api-version=3.0-preview.1");

        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        // The response shape is:
        //   {"results":[{"extensions":[{..., "versions":[{"version":"1.2.3", ...}, ...]}]}]}
        // We only need the first version string — a regex avoids pulling in a JSON parser.
        var match = Regex.Match(json, "\"versions\"\\s*:\\s*\\[\\s*\\{\\s*\"version\"\\s*:\\s*\"([^\"]+)\"");
        if (!match.Success) return null;

        return Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    private void ShowInfoBar(Version current, Version latest)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetInfoBarHost(out var host, out var factory)) return;

        var model = new InfoBarModel(
            textSpans: new[]
            {
                new InfoBarTextSpan($"VsAgentic {latest} is available (you have {current}). "),
            },
            actionItems: new[]
            {
                new InfoBarHyperlink("View on Marketplace", "open"),
                new InfoBarHyperlink("Dismiss", "dismiss"),
            },
            image: default,
            isCloseButtonVisible: true);

        var element = factory.CreateInfoBar(model);
        element.Advise(this, out _eventCookie);
        host.AddInfoBar(element);
    }

    private void ShowStaleInstallInfoBar(Version running, Version onDisk)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetInfoBarHost(out var host, out var factory)) return;

        // No persistent dismissal. The user can close the banner for this session
        // via the X button, but on the next VS launch the check runs again and the
        // banner reappears as long as the stale folder is still on disk. That's the
        // whole point — silent stale-loads are exactly the failure mode we're
        // guarding against.
        var model = new InfoBarModel(
            textSpans: new[]
            {
                new InfoBarTextSpan(
                    $"VsAgentic {onDisk} is installed but Visual Studio is currently running {running}. "
                    + "Close all Visual Studio windows and reopen to load the latest version."),
            },
            image: KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);

        var element = factory.CreateInfoBar(model);
        element.Advise(this, out _eventCookie);
        host.AddInfoBar(element);
    }

    private static bool TryGetInfoBarHost(out IVsInfoBarHost host, out IVsInfoBarUIFactory factory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        host = null!;
        factory = null!;

        if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) return false;

        if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))
            || hostObj is not IVsInfoBarHost h)
        {
            return false;
        }

        if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory f) return false;

        host = h;
        factory = f;
        return true;
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_eventCookie != 0)
        {
            infoBarUIElement.Unadvise(_eventCookie);
            _eventCookie = 0;
        }
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (actionItem.ActionContext is string ctx && ctx == "open")
        {
            try
            {
                Process.Start(new ProcessStartInfo(MarketplaceListingUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VsAgentic UpdateChecker: failed to open marketplace URL: {ex.Message}");
            }
        }

        infoBarUIElement.Close();
    }
}
