namespace VsAgentic.Services.Abstractions;

/// <summary>
/// Makes the saved state of a new chat that continues another chat's
/// conversation from where it is now. The original chat is not changed: what
/// either chat says afterwards stays out of the other one.
/// </summary>
/// <remarks>
/// The state is the one <see cref="IChatService.SerializeHistory"/> writes,
/// so only the agent that wrote it can read it. The session store copies the
/// transcript; this turns the agent's part into that of the new chat.
/// </remarks>
public interface IConversationForker
{
    /// <summary>
    /// Returns the state for the new chat, or null when
    /// <paramref name="serializedHistory"/> holds no conversation to continue,
    /// as in a chat that has not had an answer yet.
    /// </summary>
    string? CreateForkHistory(string? serializedHistory);
}
