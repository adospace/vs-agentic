namespace VsAgentic.Services.Configuration;

/// <summary>
/// Controls how the AI model is selected for each request.
/// </summary>
public enum ModelMode
{
    /// <summary>Uses Haiku to classify task complexity and routes to the appropriate model.</summary>
    Auto,

    /// <summary>Always uses Haiku — fast, lightweight responses.</summary>
    Simple,

    /// <summary>Always uses Sonnet — balanced reasoning and speed.</summary>
    Moderate,

    /// <summary>Always uses Opus — maximum reasoning capability.</summary>
    Complex
}
