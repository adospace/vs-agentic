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
            var localVersion = ReadInstalledVersion();
            Log($"CheckAsync: localVersion = {localVersion?.ToString() ?? "<null>"}");
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
    /// Reads the version from extension.vsixmanifest (deployed next to this assembly).
    /// The VSIX manifest is the source of truth for the published version — the assembly
    /// version is independent and typically left at 1.0.0.0. Falls back to the assembly
    /// version if the manifest can't be located or parsed.
    /// </summary>
    private static Version? ReadInstalledVersion()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(UpdateChecker).Assembly.Location);
            Log($"ReadInstalledVersion: asmDir = {asmDir ?? "<null>"}");

            // VS may keep stale extension directories after an update, and the running
            // assembly can still be loaded from an old directory.  Scan ALL sibling
            // directories under the parent Extensions folder for manifests that match
            // our extension Id, and return the highest version found.
            if (!string.IsNullOrEmpty(asmDir))
            {
                var extensionsRoot = Path.GetDirectoryName(asmDir);
                if (!string.IsNullOrEmpty(extensionsRoot))
                {
                    Version? best = null;
                    foreach (var dir in Directory.EnumerateDirectories(extensionsRoot))
                    {
                        var candidate = Path.Combine(dir, "extension.vsixmanifest");
                        if (!File.Exists(candidate)) continue;
                        var version = TryReadVersionFromManifest(candidate, ExtensionId);
                        if (version is not null && (best is null || version > best))
                            best = version;
                    }

                    if (best is not null)
                    {
                        Log($"ReadInstalledVersion: best version across Extensions = {best}");
                        return best;
                    }
                }

                // Fallback: read just the manifest next to the running assembly.
                var manifestPath = Path.Combine(asmDir, "extension.vsixmanifest");
                Log($"ReadInstalledVersion: fallback manifestPath = {manifestPath}, exists = {File.Exists(manifestPath)}");
                if (File.Exists(manifestPath))
                {
                    var v = TryReadVersionFromManifest(manifestPath, extensionId: null);
                    if (v is not null) return v;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ReadInstalledVersion: exception {ex}");
            Debug.WriteLine($"VsAgentic UpdateChecker: failed to read vsixmanifest: {ex.Message}");
        }

        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
        Log($"ReadInstalledVersion: falling back to assembly version = {asmVersion?.ToString() ?? "<null>"}");
        return asmVersion;
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

        if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) return;

        if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))
            || hostObj is not IVsInfoBarHost host)
        {
            return;
        }

        if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;

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
