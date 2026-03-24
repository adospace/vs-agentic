namespace VsAgentic.Services.Models;

public class SessionEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = "New Session";
    public int Ordinal { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }

    // Token usage — persisted so cost survives IDE restarts
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalCacheCreationTokens { get; set; }
    public int TotalCacheReadTokens { get; set; }
    public string? LastModelId { get; set; }
}
