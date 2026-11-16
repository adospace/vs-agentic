using VsAgentic.Services.ClaudeCli;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The step mark for a call denied under <c>--permission-prompts none</c>. The
/// texts are the tool results CLI 2.1.325 sent.
/// </summary>
public class PromptDenialTests
{
    [Fact]
    public void Denied_call_is_recognised()
    {
        const string text = "Permission for this tool use was denied. It requires approval, and this session has no approval surface — nobody can answer a permission prompt here — so it was denied automatically. The action was NOT performed; do not claim it succeeded, and do not retry it: this action, and anything else that requires approval, will be denied the same way for the rest of this session. Tell the user what was blocked and why you needed it, then continue with the parts of the task that do not require approval. What required approval: Claude requested permissions to write to C:\\w\\a.txt, but you haven't granted it yet.";
        Assert.True(PromptDenial.IsDenial(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Exit code 128\nfatal: not a git repository (or any of the parent directories): .git")]
    [InlineData("Permission for this action was denied by the Claude Code auto mode classifier. Reason: x. If you have other tasks")]
    [InlineData("The user doesn't want to proceed with this tool use. The tool use was rejected.")]
    public void Other_errors_are_not(string? text) => Assert.False(PromptDenial.IsDenial(text));
}
