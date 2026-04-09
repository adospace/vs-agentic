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
            if (!string.IsNullOrEmpty(asmDir))
            {
                var manifestPath = Path.Combine(asmDir, "extension.vsixmanifest");
                Log($"ReadInstalledVersion: manifestPath = {manifestPath}, exists = {File.Exists(manifestPath)}");
                if (File.Exists(manifestPath))
                {
                    var doc = XDocument.Load(manifestPath);
                    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    Log($"ReadInstalledVersion: root = {doc.Root?.Name.LocalName}, ns = {ns.NamespaceName}");
                    var identity = doc.Root?
                        .Element(ns + "Metadata")?
                        .Element(ns + "Identity");
                    var versionAttr = identity?.Attribute("Version");
                    Log($"ReadInstalledVersion: identity found = {identity is not null}, version attr = {versionAttr?.Value ?? "<null>"}");
                    if (versionAttr is not null && Version.TryParse(versionAttr.Value, out var v))
                        return v;
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
