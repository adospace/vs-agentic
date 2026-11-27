using System.Text.Json;
using VsAgentic.Services.Configuration;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The JSON passed to <c>--settings</c> for the Attribution option.
/// </summary>
public class CommitAttributionTests
{
    private static (string Commit, string Pr) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var a = doc.RootElement.GetProperty("attribution");
        return (a.GetProperty("commit").GetString()!, a.GetProperty("pr").GetString()!);
    }

    [Fact]
    public void CliSetting_sends_nothing() =>
        Assert.Null(CommitAttribution.CliSetting.ToSettingsJson("ignored", "ignored"));

    [Fact]
    public void Off_clears_both_lines() =>
        Assert.Equal(("", ""), Parse(CommitAttribution.Off.ToSettingsJson("ignored", "ignored")!));

    [Fact]
    public void Custom_keeps_the_texts_and_quotes_them() =>
        Assert.Equal(
            ("Co-Authored-By: \"Claude\" <noreply@anthropic.com>", ""),
            Parse(CommitAttribution.Custom.ToSettingsJson("  Co-Authored-By: \"Claude\" <noreply@anthropic.com> ", null)!));

    [Fact]
    public void Custom_turns_typed_backslash_n_into_a_line_break() =>
        Assert.Equal(
            ("Co-Authored-By: A\nCo-Authored-By: B", "x\ny"),
            Parse(CommitAttribution.Custom.ToSettingsJson("Co-Authored-By: A\\nCo-Authored-By: B", "x\r\ny")!));
}
