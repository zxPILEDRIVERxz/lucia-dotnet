using FakeItEasy;
using lucia.Agents.Abstractions;
using lucia.Agents.PluginFramework;
using lucia.HomeAssistant.Models;
using lucia.HomeAssistant.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace lucia.Tests.Plugins;

/// <summary>
/// Verifies the bundled home-assistant-calendar plugin by evaluating the real
/// plugins/home-assistant-calendar/plugin.cs script and exercising its tools
/// against a faked IHomeAssistantClient.
/// </summary>
public sealed class HomeAssistantCalendarPluginTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lucia-cal-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "lucia-dotnet.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repo root (lucia-dotnet.slnx).");
    }

    private static ILogger CreateLogger()
    {
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return factory.CreateLogger("HomeAssistantCalendarPluginTests");
    }

    /// <summary>
    /// Host harness: evaluates the real plugin script, runs its ConfigureServices,
    /// and builds a host with the faked IHomeAssistantClient.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness(IHomeAssistantClient client, string? haBaseUrl = "http://ha.local:8123", string? calendarsConfig = null)
        {
            Client = client;

            var pluginSource = Path.Combine(FindRepoRoot(), "plugins", "home-assistant-calendar");
            var tempDir = Path.Combine(Path.GetTempPath(), "lucia-cal-eval-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            foreach (var file in Directory.EnumerateFiles(pluginSource))
            {
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)));
            }

            Plugin = PluginScriptHost.EvaluatePluginAsync(tempDir, CreateLogger()).GetAwaiter().GetResult();
            Assert.NotNull(Plugin);

            var builder = Host.CreateApplicationBuilder();
            if (haBaseUrl is not null)
                builder.Configuration["HomeAssistant:BaseUrl"] = haBaseUrl;
            if (calendarsConfig is not null)
                builder.Configuration["HomeAssistantCalendar:Calendars"] = calendarsConfig;
            builder.Services.AddSingleton<IHomeAssistantClient>(client);

            Plugin!.ConfigureServices(builder);
            App = builder.Build();
        }

        public ILuciaPlugin? Plugin { get; }
        public IHost App { get; }
        public IHomeAssistantClient Client { get; }

        public IAgentToolProvider GetProvider() => App.Services.GetRequiredService<IAgentToolProvider>();

        public async Task<string> InvokeToolAsync(string toolName, IDictionary<string, object?>? arguments = null)
        {
            var tool = GetProvider().GetTools().Single(t => t.Name == toolName);
            var function = (AIFunction)tool;
            var result = await function.InvokeAsync(
                arguments is null ? null : new AIFunctionArguments(arguments));
            return result switch
            {
                string s => s,
                System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String => element.GetString()!,
                _ => throw new InvalidOperationException($"Unexpected tool result type: {result?.GetType().Name ?? "null"}"),
            };
        }

        public void Dispose()
        {
            App.StopAsync().GetAwaiter().GetResult();
            App.Dispose();
        }
    }

    private static IHomeAssistantClient CreateClientFake(
        CalendarEntity[] calendars,
        CalendarEvent[] eventsFor,
        out List<(string EntityId, string? Start, string? End)> eventCalls)
    {
        eventCalls = new List<(string EntityId, string? Start, string? End)>();
        var captured = eventCalls;
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns(calendars);
        A.CallTo(() => client.GetCalendarEventsAsync(
                A<string>._, A<string?>._, A<string?>._, A<CancellationToken>._))
            .Invokes((FakeItEasy.Core.IFakeObjectCall call) => captured.Add((
                call.GetArgument<string>(0)!,
                call.GetArgument<string?>(1),
                call.GetArgument<string?>(2))))
            .Returns(eventsFor);
        return client;
    }

    private static CalendarEntity[] TwoCalendars() =>
    [
        new() { EntityId = "calendar.local", Name = "My Calendar" },
        new() { EntityId = "calendar.work", Name = "Work" },
    ];

    private static CalendarEvent[] SampleEvents() =>
    [
        new()
        {
            Summary = "Team standup",
            Start = new CalendarDateTime { DateTime = "2026-11-14T09:00:00+01:00" },
            End = new CalendarDateTime { DateTime = "2026-11-14T09:30:00+01:00" },
            Description = "Daily sync",
            Location = "Kitchen",
            Uid = "evt-123",
            Rrule = "FREQ=DAILY",
        },
        new()
        {
            Summary = "Birthday",
            Start = new CalendarDateTime { Date = "2026-12-01" },
            End = new CalendarDateTime { Date = "2026-12-02" },
            Uid = "evt-456",
        },
    ];

    // ── Script evaluation & registration ────────────────────────────

    [Fact]
    public async Task RealScript_EvaluatesToCalendarPlugin_WithConfigSurface()
    {
        var client = A.Fake<IHomeAssistantClient>();
        using var harness = new Harness(client);

        Assert.Equal("home-assistant-calendar", harness.Plugin!.PluginId);
        Assert.Equal("HomeAssistantCalendar", harness.Plugin.ConfigSection);
        var property = harness.Plugin.ConfigProperties.Single();
        Assert.Equal("Calendars", property.Name);
        Assert.False(property.IsSensitive);
    }

    [Fact]
    public void RegistersAllFourTools_WhenHaConfigured()
    {
        var client = A.Fake<IHomeAssistantClient>();
        using var harness = new Harness(client);

        var provider = harness.GetProvider();
        Assert.Equal("Home Assistant Calendar", provider.Name);

        var names = provider.GetTools().Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            ["create_calendar_event", "delete_calendar_event", "list_calendar_events", "update_calendar_event"],
            names);
    }

    [Fact]
    public void RegistersNoToolProvider_WhenHaNotConfigured()
    {
        var client = A.Fake<IHomeAssistantClient>();
        using var harness = new Harness(client, haBaseUrl: null);

        Assert.Null(harness.App.Services.GetService<IAgentToolProvider>());
    }

    /// <summary>
    /// Regression: without routing examples the calendar capability was invisible to the
    /// router (catalog prints skill examples only), so calendar requests misrouted to
    /// timer-agent. The provider must publish example utterances for router visibility.
    /// </summary>
    [Fact]
    public void Provider_PublishesRoutingExamplesForRouterVisibility()
    {
        var client = A.Fake<IHomeAssistantClient>();
        using var harness = new Harness(client);

        var examples = harness.GetProvider().RoutingExamples;

        Assert.NotNull(examples);
        Assert.True(examples!.Count >= 4, $"expected at least 4 routing examples, got {examples.Count}");
        Assert.Contains(examples, e => e.Contains("calendar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(examples, e => e.Contains("event", StringComparison.OrdinalIgnoreCase));
    }

    // ── list_calendar_events ────────────────────────────────────────

    [Fact]
    public async Task ListEvents_RendersMarkdownGroupedByCalendar()
    {
        var client = CreateClientFake(TwoCalendars(), SampleEvents(), out _);
        using var harness = new Harness(client);

        var result = await harness.InvokeToolAsync("list_calendar_events");

        Assert.Contains("### My Calendar (calendar.local)", result);
        Assert.Contains("### Work (calendar.work)", result);
        Assert.Contains("Team standup (uid: evt-123)", result);
        Assert.Contains("Description: Daily sync", result);
        Assert.Contains("Location: Kitchen", result);
        Assert.Contains("Recurring: FREQ=DAILY", result);
        Assert.Contains("All day 2026-12-01-2026-12-02 — Birthday (uid: evt-456)", result);
    }

    [Fact]
    public async Task ListEvents_UsesRequestedWindow()
    {
        var client = CreateClientFake(TwoCalendars(), SampleEvents(), out var calls);
        using var harness = new Harness(client);

        await harness.InvokeToolAsync("list_calendar_events", new Dictionary<string, object?> { ["days_ahead"] = 14 });

        // Both calendars were queried with a matching window.
        Assert.Equal(2, calls.Count);
        foreach (var (_, start, end) in calls)
        {
            var s = DateTimeOffset.Parse(start!);
            var e = DateTimeOffset.Parse(end!);
            Assert.Equal(s.Date.AddDays(14), e.Date);
            Assert.Equal(DateTime.Today, s.Date);
        }
    }

    [Fact]
    public async Task ListEvents_FiltersToConfiguredCalendars_ByFriendlyName()
    {
        var client = CreateClientFake(TwoCalendars(), SampleEvents(), out var calls);
        using var harness = new Harness(client, calendarsConfig: "My Calendar");

        var result = await harness.InvokeToolAsync("list_calendar_events");

        Assert.Single(calls);
        Assert.Equal("calendar.local", calls[0].EntityId);
        Assert.Contains("### My Calendar (calendar.local)", result);
        Assert.DoesNotContain("### Work", result);
    }

    [Fact]
    public async Task ListEvents_UnknownConfiguredCalendar_ListsAvailable()
    {
        var client = CreateClientFake(TwoCalendars(), SampleEvents(), out _);
        using var harness = new Harness(client, calendarsConfig: "Nope");

        var result = await harness.InvokeToolAsync("list_calendar_events");

        Assert.Contains("Nope", result);
        Assert.Contains("My Calendar", result);
        Assert.Contains("Work", result);
    }

    // ── create_calendar_event ───────────────────────────────────────

    [Fact]
    public async Task CreateEvent_SingleCalendar_DefaultsToIt()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns([new CalendarEntity { EntityId = "calendar.local", Name = "My Calendar" }]);
        (string? entity, string? summary, CalendarDateTime? start, CalendarDateTime? end)? captured = null;
        A.CallTo(() => client.CreateCalendarEventAsync(
                A<string>._, A<string>._, A<CalendarDateTime>._, A<CalendarDateTime>._,
                A<string?>._, A<string?>._, A<CancellationToken>._))
            .Invokes((string entityId, string summary, CalendarDateTime start, CalendarDateTime end,
                      string? _, string? __, CancellationToken ___) =>
                captured = (entityId, summary, start, end));

        using var harness = new Harness(client);
        var result = await harness.InvokeToolAsync("create_calendar_event", new Dictionary<string, object?>
        {
            ["summary"] = "Dentist",
            ["start"] = "2026-11-25T14:00:00+01:00",
            ["end"] = "2026-11-25T15:00:00+01:00",
        });

        Assert.NotNull(captured);
        Assert.Equal("calendar.local", captured!.Value.entity);
        Assert.Equal("Dentist", captured.Value.summary);
        Assert.Equal("2026-11-25T14:00:00+01:00", captured.Value.start!.DateTime);
        Assert.Null(captured.Value.start.Date);
        Assert.Equal("2026-11-25T15:00:00+01:00", captured.Value.end!.DateTime);
        Assert.Contains("Dentist", result);
        Assert.Contains("calendar.local", result);
    }

    [Fact]
    public async Task CreateEvent_AllDay_UsesDateForm()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns([new CalendarEntity { EntityId = "calendar.local", Name = "My Calendar" }]);
        (CalendarDateTime? start, CalendarDateTime? end)? captured = null;
        A.CallTo(() => client.CreateCalendarEventAsync(
                A<string>._, A<string>._, A<CalendarDateTime>._, A<CalendarDateTime>._,
                A<string?>._, A<string?>._, A<CancellationToken>._))
            .Invokes((string _, string __, CalendarDateTime start, CalendarDateTime end,
                      string? ___, string? ____, CancellationToken _____) =>
                captured = (start, end));

        using var harness = new Harness(client);
        await harness.InvokeToolAsync("create_calendar_event", new Dictionary<string, object?>
        {
            ["summary"] = "Vacation",
            ["start"] = "2026-12-01",
            ["end"] = "2026-12-08",
        });

        Assert.NotNull(captured);
        Assert.Equal("2026-12-01", captured!.Value.start!.Date);
        Assert.Null(captured.Value.start.DateTime);
        Assert.Equal("2026-12-08", captured.Value.end!.Date);
    }

    [Fact]
    public async Task CreateEvent_MultipleCalendars_RequiresExplicitChoice()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns(TwoCalendars());

        using var harness = new Harness(client);
        var result = await harness.InvokeToolAsync("create_calendar_event", new Dictionary<string, object?>
        {
            ["summary"] = "Dentist",
            ["start"] = "2026-11-25T14:00:00+01:00",
            ["end"] = "2026-11-25T15:00:00+01:00",
        });

        Assert.Contains("calendar", result, StringComparison.OrdinalIgnoreCase);
        A.CallTo(() => client.CreateCalendarEventAsync(
                A<string>._, A<string>._, A<CalendarDateTime>._, A<CalendarDateTime>._,
                A<string?>._, A<string?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // ── update_calendar_event ───────────────────────────────────────

    [Fact]
    public async Task UpdateEvent_PassesFullEventDict()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns([new CalendarEntity { EntityId = "calendar.local", Name = "My Calendar" }]);
        (string? uid, string? summary, CalendarDateTime? start, CalendarDateTime? end, string? description)? captured = null;
        A.CallTo(() => client.UpdateCalendarEventAsync(
                A<string>._, A<string>._, A<string>._, A<CalendarDateTime>._, A<CalendarDateTime>._,
                A<string?>._, A<string?>._, A<CancellationToken>._))
            .Invokes((string _, string uid, string summary, CalendarDateTime start, CalendarDateTime end,
                      string? description, string? ___, CancellationToken _____) =>
                captured = (uid, summary, start, end, description));

        using var harness = new Harness(client);
        var result = await harness.InvokeToolAsync("update_calendar_event", new Dictionary<string, object?>
        {
            ["uid"] = "evt-123",
            ["summary"] = "Dentist (moved)",
            ["start"] = "2026-11-25T16:00:00+01:00",
            ["end"] = "2026-11-25T17:00:00+01:00",
            ["description"] = "rescheduled",
        });

        Assert.NotNull(captured);
        Assert.Equal("evt-123", captured!.Value.uid);
        Assert.Equal("Dentist (moved)", captured.Value.summary);
        Assert.Equal("2026-11-25T16:00:00+01:00", captured.Value.start!.DateTime);
        Assert.Equal("rescheduled", captured.Value.description);
        Assert.Contains("evt-123", result);
    }

    // ── delete_calendar_event ───────────────────────────────────────

    [Fact]
    public async Task DeleteEvent_CallsClientWithUid()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns([new CalendarEntity { EntityId = "calendar.local", Name = "My Calendar" }]);
        string? capturedEntity = null;
        string? capturedUid = null;
        A.CallTo(() => client.DeleteCalendarEventAsync(
                A<string>._, A<string>._, A<CancellationToken>._))
            .Invokes((string entityId, string uid, CancellationToken _) =>
            {
                capturedEntity = entityId;
                capturedUid = uid;
            });

        using var harness = new Harness(client);
        var result = await harness.InvokeToolAsync("delete_calendar_event", new Dictionary<string, object?>
        {
            ["uid"] = "evt-456",
        });

        Assert.Equal("calendar.local", capturedEntity);
        Assert.Equal("evt-456", capturedUid);
        Assert.Contains("evt-456", result);
    }

    // ── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task ToolErrors_ReturnFixedMessage_WithoutExceptionText()
    {
        const string SecretMarker = "SECRETY-EXN-TEXT";
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns([new CalendarEntity { EntityId = "calendar.local", Name = "My Calendar" }]);
        A.CallTo(() => client.DeleteCalendarEventAsync(
                A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException(SecretMarker));

        using var harness = new Harness(client);
        var result = await harness.InvokeToolAsync("delete_calendar_event", new Dictionary<string, object?>
        {
            ["uid"] = "evt-456",
        });

        Assert.DoesNotContain(SecretMarker, result);
        Assert.StartsWith("Calendar operation failed:", result);
    }

    // ── Dashboard status line ───────────────────────────────────────

    [Fact]
    public async Task OnSystemReady_SetsStatusLine_WithCalendarNames()
    {
        var client = A.Fake<IHomeAssistantClient>();
        A.CallTo(() => client.GetCalendarsAsync(A<CancellationToken>._))
            .Returns(TwoCalendars());
        using var harness = new Harness(client);

        await harness.Plugin!.OnSystemReadyAsync(harness.App.Services);

        var status = (harness.Plugin as IPluginStatusProvider)!.StatusLine;
        Assert.NotNull(status);
        Assert.Contains("My Calendar", status);
        Assert.Contains("Work", status);
    }

    [Fact]
    public async Task OnSystemReady_UnconfiguredHa_ShowsNotConfigured()
    {
        var client = A.Fake<IHomeAssistantClient>();
        using var harness = new Harness(client, haBaseUrl: null);

        await harness.Plugin!.OnSystemReadyAsync(harness.App.Services);

        Assert.Contains("not configured", (harness.Plugin as IPluginStatusProvider)!.StatusLine!);
    }
}
