using System;
using System.Threading.Tasks;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// The instruction file several coding agents share, <c>AGENTS.md</c> in the
/// solution folder. Visual Studio's Copilot reads it, and so do other agents.
/// A chat service implements this when its agent can miss that file and can be
/// made to read it. The UI asks whether the service has it.
/// </summary>
public interface ISharedInstructions
{
    /// <summary>
    /// Raised at most once per conversation, when the folder has a shared
    /// instruction file that the agent did not load.
    /// </summary>
    event Action<SharedInstructionsNotLoaded>? SharedInstructionsNotLoaded;

    /// <summary>
    /// Makes the agent read the file from the next message on. Returns the
    /// path of the file that was changed or created for it.
    /// </summary>
    /// <exception cref="System.IO.IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    Task<string> LoadSharedInstructionsAsync(string path);
}

/// <summary>A shared instruction file the agent did not load.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="CannotLoad">
/// Why <see cref="ISharedInstructions.LoadSharedInstructionsAsync"/> cannot
/// help, in words for the user (the agent's own instruction file is in an
/// encoding the agent cannot read, say); null when it can.
/// </param>
public sealed record SharedInstructionsNotLoaded(string Path, string? CannotLoad);
