using System.IO;
using System.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.VSExtension.Editor;

/// <summary>
/// Opens Claude's file changes in the Visual Studio diff viewer.
///
/// Both sides are written to temporary files, also when one of them is the
/// file on disk: the comparison shows the file as it was at that moment, and
/// a change typed into it must not look like an edit of the real file.
/// </summary>
internal static class FileDiffViewer
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "VsAgentic", "diff");

    // Diffs from an earlier session of Visual Studio, or ones still open in
    // this one, are left alone for this long.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public static void Open(FileDiffContent diff)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService service)
            return;

        DeleteOld();

        var name = Path.GetFileName(diff.FilePath);
        if (string.IsNullOrEmpty(name)) name = "file";
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        var left = Write(Path.Combine(folder, "before", name), diff.Before);
        var right = Write(Path.Combine(folder, "after", name), diff.After);

        var suffix = diff.ChangedPartsOnly ? " (changed parts only)" : "";
        var options = (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary
                             | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);

        service.OpenComparisonWindow2(
            left,
            right,
            $"{name}: {diff.BeforeLabel} vs. {diff.AfterLabel}{suffix}",
            diff.FilePath,
            $"{diff.BeforeLabel}: {diff.FilePath}{suffix}",
            $"{diff.AfterLabel}: {diff.FilePath}{suffix}",
            $"{name}: {diff.BeforeLabel} vs. {diff.AfterLabel}{suffix}",
            null,
            options);
    }

    /// <summary>
    /// Writes one side with Windows line endings, so that neither side of the
    /// comparison differs from the other in line endings alone.
    /// </summary>
    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        return path;
    }

    private static void DeleteOld()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in new DirectoryInfo(Root).GetDirectories())
            {
                if (DateTime.UtcNow - dir.CreationTimeUtc < KeepFor) continue;
                try { dir.Delete(recursive: true); }
                catch (IOException) { /* still open in a diff window */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
