using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Best-effort, zero-cost lookup of the model the CLI will use, for display in
/// the input bar before the first message has been sent.
///
/// The CLI only reports its resolved model in the <c>system/init</c> event, and
/// that event is emitted when the first user message arrives — not at process
/// start (verified: a stream-json process left idle emits nothing). Every way of
/// forcing an early init costs a real API call, including <c>--max-budget-usd</c>,
/// which aborts only *after* the first request. So to show the model at window
/// load we re-implement the CLI's settings precedence over the same files it
/// reads.
///
/// This is a preview, not the truth: <see cref="ClaudeCliChatService.ModelChanged"/>
/// overwrites it with the authoritative value as soon as the real init arrives.
/// Anything this can't determine returns null, and the caller shows nothing —
/// never a guess.
/// </summary>
public static class ClaudeModelResolver
{
    /// <summary>
    /// Resolves the model a *previously started* session actually ran on, by
    /// reading the CLI's own transcript for that session id.
    ///
    /// This matters because <c>--resume</c> keeps a session on the model it was
    /// created with — verified: a session pinned to Haiku resumed as Haiku while
    /// the configured default was Opus. So for a restored session the configured
    /// default (<see cref="ResolveConfiguredModel"/>) is the wrong answer.
    ///
    /// Returns the model of the *last* assistant entry, so a mid-session model
    /// switch is respected, or null if the transcript is missing or has no
    /// assistant turns yet.
    /// </summary>
    public static string? ResolveSessionModel(string? sessionId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(userProfile)) return null;

            var projectsRoot = Path.Combine(userProfile, ".claude", "projects");
            if (!Directory.Exists(projectsRoot)) return null;

            // Transcripts live under a per-working-directory folder whose name is
            // an encoded form of the path ("E:\Lingos" -> "E--Lingos"). Rather
            // than reproduce that encoding — undocumented and liable to change —
            // just find the file by session id.
            var file = FirstOrDefault(Directory.EnumerateFiles(
                projectsRoot, sessionId!.Trim() + ".jsonl", SearchOption.AllDirectories));
            if (file is null)
            {
                logger?.LogDebug("[ModelResolver] No transcript found for session {SessionId}", sessionId);
                return null;
            }

            string? lastModel = null;
            // Streamed, with a cheap substring prefilter so we only pay for JSON
            // parsing on assistant turns — transcripts reach many megabytes.
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0) continue;
                if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") continue;
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("model", out var m) || m.ValueKind != JsonValueKind.String) continue;

                    var value = m.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) lastModel = value!.Trim();
                }
                catch (JsonException)
                {
                    // A torn final line (the CLI may be mid-write) — skip it.
                }
            }

            logger?.LogDebug("[ModelResolver] Session {SessionId} last ran on {Model}", sessionId, lastModel);
            return lastModel;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[ModelResolver] Could not read transcript for session {SessionId}", sessionId);
            return null;
        }
    }

    private static string? FirstOrDefault(IEnumerable<string> items)
    {
        foreach (var item in items) return item;
        return null;
    }

    /// <summary>
    /// Resolves the configured model id/alias, or null when it can't be
    /// determined (no setting anywhere, meaning the account default applies).
    ///
    /// This is only the right answer for a *new* session — see
    /// <see cref="ResolveSessionModel"/> for restored ones.
    /// Precedence follows Claude Code's documented order, highest first:
    /// managed policy → env → project-local → project → user settings.
    /// We never pass <c>--model</c>, so the CLI-flag tier is skipped.
    /// </summary>
    public static string? ResolveConfiguredModel(string? workingDirectory, ILogger? logger = null)
    {
        foreach (var path in CandidateSettingsPaths(workingDirectory))
        {
            // The env var tier sits between managed policy and project settings;
            // CandidateSettingsPaths yields a null marker at that position.
            if (path is null)
            {
                var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    logger?.LogDebug("[ModelResolver] Model from ANTHROPIC_MODEL: {Model}", fromEnv);
                    return Interpret(fromEnv);
                }
                continue;
            }

            var model = ReadModelSetting(path, logger);
            if (model is not null)
            {
                logger?.LogDebug("[ModelResolver] Model from {Path}: {Model}", path, model);
                // A hit at this tier wins outright — including the literal
                // "default", which the CLI's model picker writes to mean "use the
                // account default". That must stop the search rather than fall
                // through to a lower-precedence file that names a real model.
                return Interpret(model);
            }
        }

        logger?.LogDebug("[ModelResolver] No configured model found; account default applies");
        return null;
    }

    private static IEnumerable<string?> CandidateSettingsPaths(string? workingDirectory)
    {
        // 1. Enterprise managed policy — overrides everything.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            yield return Path.Combine(programData, "ClaudeCode", "managed-settings.json");

        // 2. Environment variable (signalled by the null marker).
        yield return null;

        // 3/4. Project settings, searched from the working directory upwards so a
        // solution nested below the repo root still picks up the repo's .claude.
        // Local overrides the shared file at each level, matching the CLI.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            DirectoryInfo? dir = null;
            try { dir = new DirectoryInfo(workingDirectory!); } catch { }

            while (dir is not null && dir.Exists)
            {
                yield return Path.Combine(dir.FullName, ".claude", "settings.local.json");
                yield return Path.Combine(dir.FullName, ".claude", "settings.json");
                dir = dir.Parent;
            }
        }

        // 5. User-level settings.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            yield return Path.Combine(userProfile, ".claude", "settings.json");
    }

    private static string? ReadModelSetting(string path, ILogger? logger)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // Tolerate comments / trailing commas — hand-edited settings files
            // often have them and the CLI accepts them.
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("model", out var m)) return null;
            if (m.ValueKind != JsonValueKind.String) return null;

            var raw = m.GetString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw!.Trim();
        }
        catch (Exception ex)
        {
            // A malformed or unreadable settings file must never break the chat
            // window — fall through to the next candidate.
            logger?.LogDebug(ex, "[ModelResolver] Could not read {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Maps a winning setting value to what we should display. "default" means
    /// "whatever the account default is" — we can't know that without an API
    /// call, so report nothing rather than showing the literal word.
    /// </summary>
    private static string? Interpret(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var trimmed = model!.Trim();
        return trimmed.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }
}
