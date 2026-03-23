namespace VsAgentic.Services.Models;

public class SessionEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = "New Session";
    public int Ordinal { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
}
