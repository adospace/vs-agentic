using System.Text.Json;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// The line Claude adds at the end of a commit message ("Co-Authored-By: …")
/// and of a pull request description ("Generated with Claude Code").
///
/// The CLI reads this from the <c>attribution</c> setting and tells the model
/// in its instructions; an empty string drops the line. A value other than
/// <see cref="CliSetting"/> is passed with <c>--settings</c>, which ranks above
/// the user's and the project's settings files, so it also wins over a
/// repository's committed <c>.claude/settings.json</c>. Measured on 2.1.332:
/// a <c>-p</c> session takes both strings from <c>--settings</c>.
/// </summary>
public enum CommitAttribution
{
    /// <summary>
    /// Send nothing: the <c>attribution</c> setting from the user's or the
    /// project's Claude Code settings applies, and the CLI default without one.
    /// </summary>
    CliSetting,

    /// <summary>No attribution line in commit messages or pull request descriptions.</summary>
    Off,

    /// <summary>
    /// The texts from <see cref="VsAgenticOptions.CommitAttributionText"/> and
    /// <see cref="VsAgenticOptions.PullRequestAttributionText"/>; an empty
    /// text drops that line.
    /// </summary>
    Custom,
}

public static class CommitAttributionExtensions
{
    /// <summary>
    /// The JSON for <c>--settings</c>, or <c>null</c> to send no flag.
    /// </summary>
    public static string? ToSettingsJson(this CommitAttribution attribution, string? commitText, string? pullRequestText)
    {
        string commit, pr;
        switch (attribution)
        {
            case CommitAttribution.Off:
                commit = pr = "";
                break;
            case CommitAttribution.Custom:
                commit = Normalize(commitText);
                pr = Normalize(pullRequestText);
                break;
            default:
                return null;
        }

        return JsonSerializer.Serialize(new { attribution = new { commit, pr } });
    }

    // The property grid edits one line, but a value pasted from elsewhere can
    // carry line breaks. "\n" typed in the grid stands for one, so a trailer
    // block such as "Co-Authored-By: A\nCo-Authored-By: B" can be entered.
    private static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace("\\n", "\n").Trim();
}
