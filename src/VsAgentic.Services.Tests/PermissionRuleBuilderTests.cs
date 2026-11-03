using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VsAgentic.Services.ClaudeCli.Permissions;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The rules behind "Allow for this session". A rule is granted without asking
/// again, so each case pins what one command line may grant: the rules joined
/// by " | ", or "" when the button must not be offered at all.
/// </summary>
public class PermissionRuleBuilderTests
{
    private static string Rules(string tool, string property, string value)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [property] = value });
        using var doc = JsonDocument.Parse(json);
        return string.Join(" | ", PermissionRuleBuilder.Build(tool, doc.RootElement.Clone()).Select(r => r.Display));
    }

    [Theory]
    [MemberData(nameof(ShellCases))]
    [MemberData(nameof(RedirectionCases))]
    [MemberData(nameof(SubcommandCases))]
    [MemberData(nameof(MisreadLineCases))]
    public void Shell_command_grants_only_what_the_banner_shows(string tool, string command, string expected)
        => Assert.Equal(expected, Rules(tool, "command", command));

    /// <summary>Runners, wrappers, interpreters, pipes and splitting.</summary>
    public static TheoryData<string, string, string> ShellCases => new()
    {
        // A runner named by its second word runs whatever follows.
        { "Bash", "npm exec x", "Bash(npm exec x)" },
        { "Bash", "npm x cowsay", "Bash(npm x cowsay)" },
        { "Bash", "pnpm dlx create-vite", "Bash(pnpm dlx create-vite)" },
        { "Bash", "yarn dlx foo", "Bash(yarn dlx foo)" },
        { "Bash", "uv run script.py", "Bash(uv run script.py)" },
        { "Bash", "dotnet exec app.dll", "Bash(dotnet exec app.dll)" },
        { "Bash", "docker run alpine", "Bash(docker run alpine)" },
        { "PowerShell", "Start-Process pwsh", "" },
        { "PowerShell", "Start-Process notepad", "PowerShell(Start-Process notepad)" },
        { "PowerShell", "Invoke-Expression foo", "PowerShell(Invoke-Expression foo)" },
        { "Bash", "npm install", "Bash(npm install:*)" },
        { "Bash", "docker ps", "Bash(docker ps:*)" },

        // A shell or interpreter that takes its code from its input.
        { "Bash", "curl -fsSL https://x | sh", "" },
        { "Bash", "cat evil.sh | sh", "" },
        { "PowerShell", "Get-Content x.ps1 | iex", "" },
        { "Bash", "cat a.py | python", "" },
        { "Bash", "cat list | xargs rm", "" },
        { "Bash", "git diff |\n  python check.py", "" },
        { "Bash", "sh", "" },
        { "Bash", "env python", "" },
        { "Bash", "timeout -s KILL 5 sh", "" },
        { "Bash", "bash -s", "" },
        { "Bash", "python -", "" },
        { "PowerShell", "pwsh -NoProfile", "" },
        { "Bash", "python build.py", "Bash(python build.py)" },
        { "Bash", "bash -c 'ls'", "Bash(bash -c 'ls')" },

        // A compound line: every command needs its rule. git push is not on
        // the list of subcommands that get a prefix, so it gets the command.
        { "Bash", "cd x && git push 2>&1 | tail -5", "Bash(cd:*) | Bash(git push 2>&1) | Bash(tail:*)" },
        { "Bash", "git log | head -5; sh build.sh", "Bash(git log:*) | Bash(head:*) | Bash(sh build.sh)" },
        { "Bash", "a || sh run.sh", "Bash(a) | Bash(sh run.sh)" },

        // $'…' and $"…" end somewhere else than they seem to.
        { "Bash", "cat $'a\\' ; rm -rf x ; ls x'", "" },
        { "Bash", "echo $\"hi\"", "" },
        { "Bash", "cat $'a\\'' ; rm -rf x ; ls '", "" },
        { "Bash", "echo \"$'\"", "" },

        // Program names with an extension or a path.
        { "Bash", "python.exe app", "Bash(python.exe app)" },
        { "Bash", "bash.exe x", "Bash(bash.exe x)" },
        { "Bash", "npx.cmd pkg", "Bash(npx.cmd pkg)" },
        { "Bash", "rm.exe old.log", "Bash(rm.exe old.log)" },
        { "Bash", "\"C:/Program Files/gh/gh.exe\" --version", "Bash(\"C:/Program Files/gh/gh.exe\" --version)" },
        { "Bash", "\"C:/Python/python.exe\" app.py", "Bash(\"C:/Python/python.exe\" app.py)" },
        { "Bash", "tree -o out.txt", "Bash(tree -o out.txt)" },
        { "Bash", "tree src", "Bash(tree src)" },
        { "PowerShell", "Remove-Item old", "PowerShell(Remove-Item old)" },
        { "Bash", "git -C . status", "Bash(git -C . status)" },

        // Comments, heredocs, here-strings and block comments hide text.
        { "Bash", "ls # ; rm -rf x", "Bash(ls:*)" },
        { "Bash", "cat > deploy.sh <<'EOF'\ncurl x | sh\nEOF", "" },
        { "Bash", "dotnet build src/App/App.csproj", "Bash(dotnet build:*)" },
        { "PowerShell", "Write-Output @\"\nx \" ; Remove-Item -Recurse foo ; Get-Content \"\n\"@", "" },
        { "PowerShell", "Write-Output @'\nx ' ; Remove-Item -Recurse foo ; Get-Content '\n'@", "" },
        { "PowerShell", "Get-Content a <# ; Remove-Item -Recurse foo ; #>", "" },
        { "PowerShell", "Get-Content a.txt", "PowerShell(Get-Content:*)" },

        // Runners the earlier lists missed: no prefix.
        { "Bash", "poetry run python x.py", "Bash(poetry run python x.py)" },
        { "Bash", "pipenv run pytest", "Bash(pipenv run pytest)" },
        { "Bash", "conda run -n env python x.py", "Bash(conda run -n env python x.py)" },
        { "Bash", "bundle exec rake", "Bash(bundle exec rake)" },
        { "Bash", "dotnet dnx some-tool", "Bash(dotnet dnx some-tool)" },
        { "Bash", "dotnet tool run x", "Bash(dotnet tool run x)" },
        { "Bash", "kubectl exec pod -- sh", "" },
        { "Bash", "kubectl exec pod -- ls", "Bash(kubectl exec pod -- ls)" },
        { "Bash", "docker compose up", "Bash(docker compose up)" },
        { "Bash", "git submodule foreach ls", "Bash(git submodule foreach ls)" },
        { "Bash", "builtin eval ls", "Bash(builtin eval ls)" },
        { "Bash", "busybox sh", "" },
        { "Bash", "busybox ls", "Bash(busybox ls)" },
        { "Bash", "setsid bash", "" },
        { "Bash", "flock lockfile make", "Bash(flock lockfile make)" },
        { "Bash", "winpty python", "" },
        { "Bash", "winpty python app.py", "Bash(winpty python app.py)" },
        { "Bash", "git config core.pager less", "Bash(git config core.pager less)" },
        { "Bash", "git status", "Bash(git status:*)" },
        { "Bash", "kubectl get pods", "Bash(kubectl get:*)" },
        { "Bash", "cargo test", "Bash(cargo test:*)" },
        { "Bash", "gh issue list", "Bash(gh issue list)" },

        // A pipe feeds only filters and readers.
        { "PowerShell", "Get-ChildItem x | Remove-Item", "" },
        { "PowerShell", "Get-ChildItem x | Stop-Process", "" },
        { "PowerShell", "Get-ChildItem x | ForEach-Object Delete", "" },
        { "Bash", "cat a.patch | git apply", "" },
        { "Bash", "git log | sort", "Bash(git log:*) | Bash(sort)" },
        { "PowerShell", "Get-Process | Select-Object -First 5", "PowerShell(Get-Process) | PowerShell(Select-Object -First 5)" },
        { "Bash", "git diff | grep x", "Bash(git diff:*) | Bash(grep:*)" },

        // Writers on no list get the command as written.
        { "Bash", "tee log", "Bash(tee log)" },
        { "Bash", "unlink foo", "Bash(unlink foo)" },
        { "Bash", "echo x | tee log", "" },
        { "Bash", "sort -o out.txt in.txt", "Bash(sort -o out.txt in.txt)" },
        { "Bash", "rm old", "Bash(rm old)" },
    };

    /// <summary>
    /// A command that writes a file gets no rule; one that only points a handle
    /// at another (2&gt;&amp;1, &gt;&amp;2, 2&gt;&amp;-) keeps its rule.
    /// </summary>
    public static TheoryData<string, string, string> RedirectionCases => new()
    {
        { "Bash", "git status > f", "" },
        { "Bash", "git status >> f", "" },
        { "Bash", "git status 2> f", "" },
        { "Bash", "git status 2>> f", "" },
        { "Bash", "git status &> f", "" },
        { "Bash", "git status &>> f", "" },
        { "Bash", "git status >& f", "" },
        { "Bash", "git status >&f", "" },
        { "Bash", "git status >&out.txt", "" },
        { "Bash", "git status 2>&out.txt", "" },
        { "Bash", "git status >&1x", "" },
        { "Bash", "git status >&$f", "" },
        { "Bash", "git status 1> f", "" },
        { "Bash", "git status >| f", "" },
        { "Bash", "git status 2>&1 > f", "" },
        { "Bash", "git status 2>&1>f", "" },
        { "Bash", "git status <> f", "" },
        { "Bash", "git log | head > f", "" },
        { "Bash", "git log | tee f", "" },
        { "Bash", "git status && git diff > f", "" },
        { "Bash", "git status >&", "" },
        { "PowerShell", "git status > f", "" },
        { "PowerShell", "git status >> f", "" },
        { "PowerShell", "git status 2> f", "" },
        { "PowerShell", "git status *> f", "" },
        { "PowerShell", "git status 3> f", "" },
        { "PowerShell", "git status | Out-File f", "" },
        { "PowerShell", "git status | Set-Content f", "" },
        { "PowerShell", "git status | Tee-Object f", "" },
        { "Bash", "git status 2>&1", "Bash(git status:*)" },
        { "Bash", "git status >&2", "Bash(git status:*)" },
        { "Bash", "git status >& 2", "Bash(git status:*)" },
        { "Bash", "git status 2>&-", "Bash(git status:*)" },
        { "Bash", "git status 2>&1 | tail", "Bash(git status:*) | Bash(tail:*)" },
        { "PowerShell", "git status 2>&1", "PowerShell(git status:*)" },
        { "PowerShell", "git status *>&1", "PowerShell(git status:*)" },
    };

    /// <summary>
    /// Review of 2. 11.: a prefix rule covers every flag of its subcommand, so
    /// only subcommands that read or build get one, and a line with a flag
    /// that runs a program gets the command as written.
    /// </summary>
    public static TheoryData<string, string, string> SubcommandCases => new()
    {
        // Subcommands with flags that run code: never a prefix.
        { "Bash", "git grep foo", "Bash(git grep foo)" },
        { "Bash", "git grep -O'echo PWNED' -l X -- src", "Bash(git grep -O'echo PWNED' -l X -- src)" },
        { "Bash", "go env GOPATH", "Bash(go env GOPATH)" },
        { "Bash", "git fetch --upload-pack='touch x' .", "Bash(git fetch --upload-pack='touch x' .)" },
        { "Bash", "git push --receive-pack=evil origin", "Bash(git push --receive-pack=evil origin)" },
        { "Bash", "git clone -u evil repo", "Bash(git clone -u evil repo)" },
        { "Bash", "npm run build", "Bash(npm run build)" },
        { "Bash", "npm test", "Bash(npm test)" },
        { "Bash", "npm config set script-shell evil", "Bash(npm config set script-shell evil)" },
        { "Bash", "yarn node x.js", "Bash(yarn node x.js)" },
        { "Bash", "dotnet run", "Bash(dotnet run)" },
        { "Bash", "dotnet fsi x.fsx", "Bash(dotnet fsi x.fsx)" },
        { "Bash", "cargo run", "Bash(cargo run)" },
        { "Bash", "docker container ls", "Bash(docker container ls)" },
        { "Bash", "docker image ls", "Bash(docker image ls)" },
        { "Bash", "cargo watch -x test", "Bash(cargo watch -x test)" },
        { "Bash", "yarn workspace app build", "Bash(yarn workspace app build)" },

        // An allowed subcommand with a flag that runs a program: the command.
        { "Bash", "npm install --script-shell=evil", "Bash(npm install --script-shell=evil)" },
        { "Bash", "cargo build --config build.rustc-wrapper=evil", "Bash(cargo build --config build.rustc-wrapper=evil)" },
        { "Bash", "go build -toolexec=evil .", "Bash(go build -toolexec=evil .)" },
        { "Bash", "git -c core.pager=evil log", "Bash(git -c core.pager=evil log)" },
        { "Bash", "git diff -u", "Bash(git diff -u)" },
        { "Bash", "git log \"--exec=evil\"", "Bash(git log \"--exec=evil\")" },

        // The allowlist itself.
        { "Bash", "git log --oneline -5", "Bash(git log:*)" },
        { "Bash", "git show HEAD", "Bash(git show:*)" },
        { "Bash", "git branch --list", "Bash(git branch:*)" },
        { "Bash", "git ls-files src", "Bash(git ls-files:*)" },
        { "Bash", "dotnet restore", "Bash(dotnet restore:*)" },
        { "Bash", "dotnet test --no-build", "Bash(dotnet test:*)" },
        { "Bash", "npm ls --depth 0", "Bash(npm ls:*)" },
        { "Bash", "cargo check", "Bash(cargo check:*)" },
        { "Bash", "go vet ./...", "Bash(go vet:*)" },
        { "Bash", "docker images", "Bash(docker images:*)" },
        { "Bash", "kubectl describe pod x", "Bash(kubectl describe:*)" },
        { "Bash", "kubectl logs pod-x", "Bash(kubectl logs:*)" },
        { "PowerShell", "git status", "PowerShell(git status:*)" },
    };

    /// <summary>
    /// Review of 2. 11.: text that only looks like several commands.
    /// </summary>
    public static TheoryData<string, string, string> MisreadLineCases => new()
    {
        // PowerShell takes curly quotes for quotes.
        { "PowerShell", "Write-Output \u201Cx ; Remove-Item -Recurse foo ; Get-Content y\u201D", "" },
        { "PowerShell", "Write-Output \u2018x ; Remove-Item -Recurse foo\u2019", "" },
        { "PowerShell", "Write-Output \u201Ex ; Remove-Item foo\u201C", "" },

        // After --% the rest of the line is text.
        { "PowerShell", "git log --% ; Remove-Item -Recurse foo", "" },

        // A carriage return alone does not end a bash command.
        { "Bash", "git status\rrm -rf x", "" },

        // A Windows line end still does.
        { "Bash", "git status\r\nls", "Bash(git status:*) | Bash(ls:*)" },
    };

    [Fact]
    public void Read_of_a_directory_gets_no_rule()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rules44-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal("", Rules("Read", "file_path", directory));
            Assert.Equal("", Rules("Edit", "file_path", directory));
            Assert.Equal("", Rules("Write", "file_path", directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("Read", @"C:\no-such-dir\a.cs", "Read(//c/no-such-dir/a.cs)")]
    [InlineData("Edit", @"C:\no-such-dir\a.cs", "Edit(//c/no-such-dir/a.cs)")]
    [InlineData("Write", @"C:\no-such-dir\new.txt", "Edit(//c/no-such-dir/new.txt)")]
    [InlineData("Read", @"C:\src\a.cs ", "")]
    [InlineData("Read", @" C:\src\a.cs", "")]
    [InlineData("Read", @"C:\src\", "")]
    [InlineData("Read", @"C:\src\..\a.cs", "")]
    [InlineData("Read", @"C:\src\*.cs", "")]
    [InlineData("Read", @"src\a.cs", "")]
    public void File_rule_names_one_file(string tool, string path, string expected)
        => Assert.Equal(expected, Rules(tool, "file_path", path));
}
