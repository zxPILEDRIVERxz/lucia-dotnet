using Microsoft.Extensions.Options;
using lucia.AgentHost.Conversation;
using lucia.AgentHost.Conversation.Models;

namespace lucia.Tests.Conversation;

public sealed class ContextReconstructorTests
{
    private readonly ContextReconstructor _reconstructor = new();

    [Fact]
    public void Reconstruct_WithFullContext_BuildsExpectedPrompt()
    {
        // Arrange — 2024-03-15T10:30Z is 06:30 EDT (America/New_York, DST active)
        var request = new ConversationRequest
        {
            Text = "Turn on the lights",
            Context = new ConversationContext
            {
                Timestamp = new DateTimeOffset(2024, 3, 15, 10, 30, 0, TimeSpan.Zero),
                ConversationId = "conv-123",
                DeviceId = "device-1",
                DeviceArea = "Living Room",
                DeviceType = "voice_assistant",
                Location = "Home"
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert — timestamp rendered in the user's local zone with explicit offset
        Assert.Contains("2024-03-15 06:30:00-04:00", result);
        Assert.Contains("Friday", result);
        Assert.Contains("\"Home\"", result);
        Assert.Contains("device-1", result);
        Assert.Contains("Living Room", result);
        Assert.Contains("voice_assistant", result);
        Assert.Contains("User: Turn on the lights", result);
    }

    [Fact]
    public void Reconstruct_NovemberUtcTimestamp_RendersEasternStandardTime()
    {
        // Arrange — live failure case: 2026-11-16T13:45:55Z must be 08:45 EST, not 13:45 "local"
        var request = new ConversationRequest
        {
            Text = "What time is it?",
            Context = new ConversationContext
            {
                Timestamp = new DateTimeOffset(2026, 11, 16, 13, 45, 55, TimeSpan.Zero)
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert
        Assert.Contains("2026-11-16 08:45:55-05:00", result);
        Assert.Contains("Monday", result);
    }

    [Fact]
    public void Reconstruct_UtcAndLocalDayDiffer_DayOfWeekFollowsLocalDate()
    {
        // Arrange — 2026-11-01T03:30Z is still Sunday in UTC but Saturday 23:30 EDT locally
        var request = new ConversationRequest
        {
            Text = "Test",
            Context = new ConversationContext
            {
                Timestamp = new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero)
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert — local date part drives both the date and the day name
        Assert.Contains("2026-10-31 23:30:00-04:00", result);
        Assert.Contains("Saturday", result);
    }

    [Fact]
    public void Reconstruct_InvalidTimeZone_FallsBackToUtc()
    {
        // Arrange
        var reconstructor = new ContextReconstructor(
            options: Options.Create(new ContextReconstructorOptions { TimeZone = "Not/AZone" }));
        var request = new ConversationRequest
        {
            Text = "Test",
            Context = new ConversationContext
            {
                Timestamp = new DateTimeOffset(2026, 11, 16, 13, 45, 55, TimeSpan.Zero)
            }
        };

        // Act
        var result = reconstructor.Reconstruct(request);

        // Assert — no crash; value rendered unambiguously as UTC
        Assert.Contains("2026-11-16 13:45:55+00:00", result);
    }

    [Fact]
    public void Reconstruct_WithNullFields_UsesDefaults()
    {
        // Arrange
        var request = new ConversationRequest
        {
            Text = "Hello",
            Context = new ConversationContext
            {
                Timestamp = DateTimeOffset.UtcNow,
                DeviceId = null,
                DeviceArea = null,
                DeviceType = null,
                Location = null
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert — defaults applied for null fields
        Assert.Contains("\"unknown\"", result);       // deviceId default
        Assert.Contains("\"Home\"", result);           // location default
        Assert.Contains("voice_assistant", result);    // deviceType default
    }

    [Fact]
    public void Reconstruct_WithPromptOverride_UsesOverride()
    {
        // Arrange
        var customTemplate = "Custom prompt for {deviceId} at {timestamp}";
        var request = new ConversationRequest
        {
            Text = "test",
            PromptOverride = customTemplate,
            Context = new ConversationContext
            {
                Timestamp = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero),
                DeviceId = "my-device"
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert — uses custom template, not default
        Assert.StartsWith("Custom prompt for my-device", result);
        Assert.DoesNotContain("HOME ASSISTANT CONTEXT", result);
    }

    [Fact]
    public void Reconstruct_OutputContainsUserText()
    {
        // Arrange
        var request = new ConversationRequest
        {
            Text = "What is the temperature?",
            Context = new ConversationContext
            {
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        // Act
        var result = _reconstructor.Reconstruct(request);

        // Assert
        Assert.Contains("User: What is the temperature?", result);
    }
}
