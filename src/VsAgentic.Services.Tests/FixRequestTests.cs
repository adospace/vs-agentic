using System.Linq;
using VsAgentic.Services.IdeContext;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The text "Fix in VsAgentic Chat" writes into the input box, and the places
/// it attaches. The user sees and sends the text as it is.
/// </summary>
public class FixRequestTests
{
    private const string Foo = @"C:\src\App\Foo.cs";
    private const string Bar = @"C:\src\App\Bar.cs";

    private static Problem Error(string code, string text, string? path = Foo, int? line = 12, string? project = "App")
        => new(ProblemSeverity.Error, code, text, path, line, project);

    private static Problem Warning(string code, string text, string? path = Foo, int? line = 12)
        => new(ProblemSeverity.Warning, code, text, path, line, "App");

    [Fact]
    public void OneErrorLeavesItsPlaceToTheAttachment()
    {
        var prompt = FixRequest.BuildPrompt([Error("CS0246", "The type or namespace name 'Foo' could not be found")]);

        Assert.Equal("Fix this error:\nCS0246: The type or namespace name 'Foo' could not be found", prompt);
    }

    [Fact]
    public void OneWarningIsCalledAWarning()
    {
        Assert.StartsWith("Fix this warning:\n", FixRequest.BuildPrompt([Warning("CS0168", "Unused")]));
    }

    [Fact]
    public void AMessageIsCalledAProblem()
    {
        var prompt = FixRequest.BuildPrompt([new Problem(ProblemSeverity.Message, "IDE0090", "Simplify 'new'", Foo, 3, null)]);

        Assert.StartsWith("Fix this problem:\n", prompt);
    }

    [Fact]
    public void AProblemWithoutADocumentNamesItsProject()
    {
        var prompt = FixRequest.BuildPrompt([Error("NU1101", "Unable to find package Foo", path: null, line: null)]);

        Assert.Equal("Fix this error:\nNU1101 in project App: Unable to find package Foo", prompt);
        Assert.Empty(FixRequest.Locations([Error("NU1101", "x", path: null, line: null)]));
    }

    [Fact]
    public void AFileWithoutALineIsNamedInTheText()
    {
        var prompt = FixRequest.BuildPrompt([Error("CS8019", "Unnecessary using", line: null)]);

        Assert.Equal($"Fix this error:\nCS8019 in {Foo}: Unnecessary using", prompt);
    }

    [Fact]
    public void SeveralProblemsAreListedWithTheirPlaces()
    {
        var prompt = FixRequest.BuildPrompt(
        [
            Error("CS0246", "Type not found"),
            Error("CS1503", "Cannot convert", Bar, 40),
        ]);

        Assert.Equal(
            "Fix these errors:\n" +
            "- CS0246 in Foo.cs, line 12: Type not found\n" +
            "- CS1503 in Bar.cs, line 40: Cannot convert",
            prompt);
    }

    [Fact]
    public void MixedSeveritiesAreProblems()
    {
        Assert.StartsWith("Fix these problems:\n", FixRequest.BuildPrompt([Error("CS0246", "a"), Warning("CS0168", "b")]));
    }

    [Fact]
    public void ALongListNamesHowManyMoreThereAre()
    {
        var problems = Enumerable.Range(1, FixRequest.MaxProblems + 3)
            .Select(i => Error("CS0246", $"Type {i} not found", line: i))
            .ToList();

        var prompt = FixRequest.BuildPrompt(problems)!;

        Assert.Equal(FixRequest.MaxProblems + 2, prompt.Split('\n').Length);
        Assert.EndsWith("\n- … and 3 more in the Error List.", prompt);
    }

    [Fact]
    public void NothingToFixGivesNoPrompt()
    {
        Assert.Null(FixRequest.BuildPrompt([]));
    }

    [Fact]
    public void TheSameProblemFromBuildAndLiveAnalysisIsOne()
    {
        var problems = FixRequest.Distinct(
        [
            Error("CS0246", "Type not found"),
            Error("CS0246", "Type not found", path: Foo.ToLowerInvariant()),
            Error("CS0246", "Type not found", line: 13),
            Error("CS0246", "   "),
        ]);

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void ProblemsOnOneLineShareOneAttachment()
    {
        var locations = FixRequest.Locations(
        [
            Error("CS0246", "Type not found"),
            Error("CS1503", "Cannot convert"),
            Error("CS1503", "Cannot convert", Bar, 40),
        ]);

        Assert.Equal(new[] { new FixLocation(Foo, 12, true), new FixLocation(Bar, 40, true) }, locations.ToArray());
    }

    [Fact]
    public void ALineKnownOnlyFromTheBuildIsNotCurrent()
    {
        var locations = FixRequest.Locations(
        [
            new Problem(ProblemSeverity.Error, "CS0246", "Type not found", Foo, 12, "App", isFromBuild: true),
            new Problem(ProblemSeverity.Error, "CS1503", "Cannot convert", Bar, 40, "App", isFromBuild: true),
            Error("CS0168", "Unused", Bar, 40),
        ]);

        // Foo:12 only from the build; Bar:40 also from live analysis, which follows the edits.
        Assert.Equal(new[] { new FixLocation(Foo, 12, false), new FixLocation(Bar, 40, true) }, locations.ToArray());
    }

    [Fact]
    public void TheCopyFromLiveAnalysisIsKept()
    {
        var problems = FixRequest.Distinct(
        [
            new Problem(ProblemSeverity.Error, "CS0246", "Type not found", Foo, 12, "App", isFromBuild: true),
            Error("CS0246", "Type not found"),
        ]);

        Assert.False(Assert.Single(problems).IsFromBuild);
        Assert.True(Assert.Single(FixRequest.Locations(problems)).LineIsCurrent);
    }

    [Fact]
    public void ATextWithAngleBracketsIsACodeSpan()
    {
        // Rendered as Markdown, "<string>" would be taken for an HTML tag and dropped.
        Assert.Equal(
            "Fix this error:\nCS1503: `Argument 1: cannot convert from 'List<string>' to 'string'`",
            FixRequest.BuildPrompt([Error("CS1503", "Argument 1: cannot convert from 'List<string>' to 'string'")]));
        Assert.Equal(
            "Fix this error:\nCS0000: ``Use `x<y>` here``",
            FixRequest.BuildPrompt([Error("CS0000", "Use `x<y>` here")]));
        Assert.Equal(
            "Fix this error:\nCS0000: `` `T<U>` ``",
            FixRequest.BuildPrompt([Error("CS0000", "`T<U>`")]));
    }

    [Fact]
    public void AttachmentsStopAtTheLimit()
    {
        var problems = Enumerable.Range(1, FixRequest.MaxLocations + 5)
            .Select(i => Error("CS0246", $"Type {i} not found", line: i))
            .ToList();

        Assert.Equal(FixRequest.MaxLocations, FixRequest.Locations(problems).Count);
    }

    [Fact]
    public void ALineBelowOneIsNoLine()
    {
        var problem = Error("CS0246", "x", line: 0);

        Assert.Null(problem.Line);
    }
}
