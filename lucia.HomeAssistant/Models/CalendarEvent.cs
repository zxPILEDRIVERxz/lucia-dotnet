using System.Text.Json.Serialization;

namespace lucia.HomeAssistant.Models;

/// <summary>
/// Calendar event with start/end times, plus the identity, recurrence, and
/// status fields returned by current Home Assistant API versions.
/// </summary>
public sealed record CalendarEvent
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("start")]
    public required CalendarDateTime Start { get; init; }

    [JsonPropertyName("end")]
    public required CalendarDateTime End { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    /// <summary>Stable identifier used to update or delete the event.</summary>
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    /// <summary>Recurrence rule (i.e. "FREQ=DAILY") when the event repeats, else null.</summary>
    [JsonPropertyName("rrule")]
    public string? Rrule { get; init; }

    /// <summary>Event status such as "confirmed", "tentative", or "cancelled" when reported.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
