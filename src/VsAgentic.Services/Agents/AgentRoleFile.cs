using System;
using System.Collections.Generic;
using System.Linq;

namespace VsAgentic.Services.Agents;

/// <summary>
/// The YAML frontmatter of a Claude Code subagent file (<c>agents\*.md</c>):
/// the lines between the opening and closing <c>---</c>.
///
/// Only flat <c>key: value</c> lines are read, which is all a subagent file
/// uses (<c>name</c>, <c>description</c>, <c>model</c>, <c>effort</c>,
/// <c>tools</c>). <see cref="WithField"/> changes one line and leaves every
/// other byte of the file as it was, so a file the user wrote by hand keeps
/// its comments, order and prompt.
/// </summary>
public static class AgentRoleFile
{
    private const string Fence = "---";

    /// <summary>
    /// The fields of the frontmatter, keys in lower case. Empty when the file
    /// has no frontmatter. Quotes around a value are removed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = SplitLines(text);
        if (!TryFindFrontmatter(lines, out var end)) return fields;

        for (var i = 1; i < end; i++)
        {
            if (!TryParseField(lines[i], out var key, out var value)) continue;
            if (!fields.ContainsKey(key)) fields[key] = value;
        }
        return fields;
    }

    /// <summary>
    /// The file with <paramref name="key"/> set to <paramref name="value"/>.
    /// A null or empty value removes the line, which leaves the CLI's default.
    /// A missing key is added before the closing fence; a file without
    /// frontmatter gets one.
    /// </summary>
    public static string WithField(string text, string key, string? value)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var remove = string.IsNullOrWhiteSpace(value);
        var line = remove ? null : key + ": " + value!.Trim();

        if (!TryFindFrontmatter(lines, out var end))
        {
            if (remove) return text;
            return Fence + newline + line + newline + Fence + newline + text;
        }

        for (var i = 1; i < end; i++)
        {
            if (!TryParseField(lines[i], out var k, out _)) continue;
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (remove) lines.RemoveAt(i);
            else lines[i] = line!;
            return string.Join(newline, lines);
        }

        if (remove) return text;
        lines.Insert(end, line!);
        return string.Join(newline, lines);
    }

    /// <summary>
    /// A new subagent file. <paramref name="tools"/> null gives the agent
    /// every tool the session has.
    /// </summary>
    public static string Create(string name, string description, string? model, string? tools, string prompt)
    {
        var lines = new List<string> { Fence, "name: " + name, "description: " + description };
        if (!string.IsNullOrWhiteSpace(model)) lines.Add("model: " + model);
        if (!string.IsNullOrWhiteSpace(tools)) lines.Add("tools: " + tools);
        lines.Add(Fence);
        lines.Add("");
        lines.Add(prompt.Trim());
        lines.Add("");
        return string.Join("\n", lines);
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // The first line must be the fence (a BOM before it is allowed); the end
    // is the index of the next fence line.
    private static bool TryFindFrontmatter(IReadOnlyList<string> lines, out int end)
    {
        end = -1;
        if (lines.Count == 0 || lines[0].TrimStart('﻿').TrimEnd() != Fence) return false;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd() == Fence) { end = i; return true; }
        }
        return false;
    }

    private static bool TryParseField(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') return false;
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;
        key = line.Substring(0, colon).Trim();
        value = line.Substring(colon + 1).Trim();
        if (value.Length >= 2 && (value[0] == '"' && value[value.Length - 1] == '"' || value[0] == '\'' && value[value.Length - 1] == '\''))
            value = value.Substring(1, value.Length - 2);
        return key.Length > 0;
    }
}
