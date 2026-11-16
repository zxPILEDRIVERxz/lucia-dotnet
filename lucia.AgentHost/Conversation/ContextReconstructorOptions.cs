namespace lucia.AgentHost.Conversation;

/// <summary>
/// Configuration options for context prompt rendering.
/// Bind to the "ContextReconstructor" configuration section.
/// </summary>
public sealed class ContextReconstructorOptions
{
    public const string SectionName = "ContextReconstructor";

    /// <summary>IANA time zone id used to render the request timestamp as user-local time.</summary>
    public string TimeZone { get; set; } = "America/New_York";
}
