using System.IO;
using System.Reflection;

namespace VsAgentic.VSExtension
{
    /// <summary>
    /// Permanent fix for the VS dependency-version-drift trap.
    ///
    /// Visual Studio ships its own copies of Microsoft.Extensions.*,
    /// System.Text.Json, Microsoft.Bcl.AsyncInterfaces, etc. in
    /// Common7\IDE\PublicAssemblies\ and PrivateAssemblies\. Microsoft bumps
    /// the patch level on every VS release, and .NET Framework's strict
    /// strong-name binder rejects an exact-version reference against any
    /// other patch — so a VSIX built against 10.0.0.5 fails to load on a
    /// VS that ships 10.0.0.4.
    ///
    /// Previously we worked around this with a process-wide pkgdef
    /// (VsAgentic.VSExtension.BindingRedirects.pkgdef) that registered
    /// $RootKey$\RuntimeConfiguration redirects with codeBase entries pointing
    /// into our PackageFolder. Two problems with that approach:
    ///   1. The redirects were global to devenv.exe, so they hijacked
    ///      resolution for every other extension. In particular the
    ///      Microsoft.Extensions.AI.* redirects pointed at a file we
    ///      never shipped, breaking GitHub Copilot with a
    ///      FileNotFoundException whenever Copilot's responder asked for
    ///      Microsoft.Extensions.AI.Abstractions.
    ///   2. We had to ship our own copies of every BCL shim, layering
    ///      10.0.5 on top of whatever VS bundled. Version-drift bugs
    ///      compounded over time.
    ///
    /// This handler subscribes to AppDomain.AssemblyResolve at module load
    /// time (before any of our types are touched). It only fires when the
    /// default binder fails, only for an explicit allow-list, and probes
    /// VS's PublicAssemblies/PrivateAssemblies for whatever version is on
    /// disk. The CLR accepts the substitution regardless of version
    /// mismatch. Result: no global redirects, no shipped BCL DLLs, no
    /// cross-extension contamination.
    ///
    /// Any new VS-shipped dependency must be added to AllowList below,
    /// otherwise the handler skips it and the original binding failure
    /// surfaces.
    /// </summary>
    internal static class AssemblyResolveBootstrap
    {
        private static int _initialized;

        private static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Primitives",
            "Microsoft.Bcl.AsyncInterfaces",
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "System.Threading.Channels",
            "System.Diagnostics.DiagnosticSource",
            "System.Memory",
            "System.Buffers",
            "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Threading.Tasks.Extensions",
            "System.IO.Pipelines",
        };

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Initialize()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
            catch
            {
                // Never throw from a module initializer — that would prevent
                // the assembly itself from loading, defeating the whole point.
            }
        }

        private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var simpleName = requested.Name;
                if (string.IsNullOrEmpty(simpleName) || !AllowList.Contains(simpleName!))
                    return null;

                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
                string parentDir;
                try { parentDir = Path.GetFullPath(Path.Combine(baseDir, "..")); }
                catch { parentDir = string.Empty; }

                var probeDirs = new[]
                {
                    baseDir,
                    Path.Combine(baseDir, "PublicAssemblies"),
                    Path.Combine(baseDir, "PrivateAssemblies"),
                    string.IsNullOrEmpty(parentDir) ? string.Empty : Path.Combine(parentDir, "PublicAssemblies"),
                    string.IsNullOrEmpty(parentDir) ? string.Empty : Path.Combine(parentDir, "PrivateAssemblies"),
                };

                foreach (var dir in probeDirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;

                    string candidate;
                    try { candidate = Path.Combine(dir, simpleName + ".dll"); }
                    catch { continue; }

                    if (!File.Exists(candidate)) continue;

                    try
                    {
                        var asm = Assembly.LoadFrom(candidate);
                        Log($"resolved {simpleName} (requested {requested.Version}) -> {candidate} (loaded {asm.GetName().Version})");
                        return asm;
                    }
                    catch (Exception ex)
                    {
                        Log($"LoadFrom failed for {candidate}: {ex.Message}");
                    }
                }

                Log($"no candidate found for {simpleName} (requested {requested.Version} by {args.RequestingAssembly?.GetName().Name ?? "?"})");
            }
            catch (Exception ex)
            {
                try { Log($"resolver crashed: {ex}"); } catch { }
            }
            return null;
        }

        private static void Log(string message)
        {
            // Write to %LOCALAPPDATA%\VsAgentic\resolver.log. Separate file
            // from the main Serilog sink because the resolver runs at
            // module-load time, well before VsAgenticPackage wires up
            // logging. Kept small and self-contained on purpose.
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VsAgentic");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "resolver.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [resolver] {message}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
            catch
            {
                // Logging must never crash the resolver.
            }
        }
    }
}

// Polyfill: net472 does not ship ModuleInitializerAttribute. The C# compiler
// (LangVersion 9+) recognises any class with this exact full name regardless
// of the assembly it lives in, so declaring it here is enough to enable
// [ModuleInitializer] on net472 builds.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
