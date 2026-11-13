using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using lucia.Agents.Abstractions;
using lucia.HomeAssistant.Models;
using lucia.HomeAssistant.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class HomeAssistantCalendarToolProvider : IAgentToolProvider
{
    private readonly IHomeAssistantClient _ha;
    private readonly string[] _configuredCalendars;
    private readonly ILogger _logger;

    private static readonly ActivitySource ActivitySource = new("Lucia.Plugins.HomeAssistantCalendar", "1.0.0");
    private static readonly Meter Meter = new("Lucia.Plugins.HomeAssistantCalendar", "1.0.0");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("calendar.operations", "{count}", "Number of calendar tool operations.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("calendar.failures", "{count}", "Number of failed calendar tool operations.");

    public HomeAssistantCalendarToolProvider(IHomeAssistantClient ha, IConfiguration config, ILogger logger)
    {
        _ha = ha;
        _logger = logger;
        _configuredCalendars = (config["HomeAssistantCalendar:Calendars"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string Name => "Home Assistant Calendar";

    public string Description => "List, create, update, and delete events on your Home Assistant calendars.";

    public string? InstructionHint =>
        "Call list_calendar_events first to get event uids, then pass a uid (and the calendar name when more than one calendar is in scope) to update or delete an event. " +
        "Deleting or updating a recurring event by uid affects the whole series — confirm with the user before doing that.";

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ListCalendarEventsAsync, new AIFunctionFactoryOptions { Name = "list_calendar_events" }),
        AIFunctionFactory.Create(CreateCalendarEventAsync, new AIFunctionFactoryOptions { Name = "create_calendar_event" }),
        AIFunctionFactory.Create(UpdateCalendarEventAsync, new AIFunctionFactoryOptions { Name = "update_calendar_event" }),
        AIFunctionFactory.Create(DeleteCalendarEventAsync, new AIFunctionFactoryOptions { Name = "delete_calendar_event" }),
    ];

    [Description("List upcoming events from your Home Assistant calendars. Returns markdown grouped by calendar; each event line includes its uid, which update_calendar_event and delete_calendar_event use to find the event.")]
    public async Task<string> ListCalendarEventsAsync(
        [Description("How many days ahead to look, starting today (default 7).")] int days_ahead = 7,
        [Description("Optional calendar entity id or friendly name to list only that calendar.")] string? calendar = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(1);
        using var activity = ActivitySource.StartActivity("list_calendar_events");
        try
        {
            var (scope, error) = await ResolveScopeAsync(calendar, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                return error;
            if (scope.Length == 0)
                return "No Home Assistant calendars are available.";

            var windowStart = DateTime.Today;
            var windowEnd = windowStart.AddDays(days_ahead);
            var sb = new StringBuilder();
            foreach (var cal in scope)
            {
                var events = await _ha.GetCalendarEventsAsync(
                    cal.EntityId, ToIso(windowStart), ToIso(windowEnd), cancellationToken).ConfigureAwait(false);

                sb.AppendLine($"### {cal.Name} ({cal.EntityId})");
                sb.AppendLine();
                if (events.Length == 0)
                {
                    sb.AppendLine("No upcoming events.");
                    sb.AppendLine();
                    continue;
                }

                foreach (var evt in events)
                    sb.AppendLine(FormatEventLine(evt));
                sb.AppendLine();
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "list_calendar_events failed.");
            return FailedMessage;
        }
    }

    [Description("Create a new event on one of your Home Assistant calendars. Times are ISO 8601, for example 2026-11-25T14:00:00+01:00; use a plain date like 2026-12-01 for all-day events (both start and end must be the same form).")]
    public async Task<string> CreateCalendarEventAsync(
        [Description("The event title.")] string summary,
        [Description("Start as ISO 8601 datetime (e.g. 2026-11-25T14:00:00+01:00) or all-day date (e.g. 2026-12-01).")] string start,
        [Description("End as ISO 8601 datetime or all-day date — same form as start.")] string end,
        [Description("Calendar entity id or friendly name. Required when more than one calendar is in scope.")] string? calendar = null,
        [Description("Optional event notes.")] string? description = null,
        [Description("Optional event location.")] string? location = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(1);
        using var activity = ActivitySource.StartActivity("create_calendar_event");
        try
        {
            var (target, error) = await ResolveSingleCalendarAsync(calendar, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                return error;

            var startValue = ParseCalendarTime(start);
            var endValue = ParseCalendarTime(end);
            await _ha.CreateCalendarEventAsync(
                target.EntityId, summary, startValue, endValue, description, location, cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return $"Created event \"{summary}\" on {target.Name} ({target.EntityId}).";
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "create_calendar_event got an unparseable time value.");
            return FailedDateMessage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "create_calendar_event failed.");
            return FailedMessage;
        }
    }

    [Description("Update an existing event on one of your Home Assistant calendars. The uid comes from list_calendar_events. All-day events use plain dates like 2026-12-01 for both start and end.")]
    public async Task<string> UpdateCalendarEventAsync(
        [Description("The uid of the event to update, as returned by list_calendar_events.")] string uid,
        [Description("The new event title.")] string summary,
        [Description("New start as ISO 8601 datetime or all-day date.")] string start,
        [Description("New end as ISO 8601 datetime or all-day date — same form as start.")] string end,
        [Description("Calendar entity id or friendly name. Required when more than one calendar is in scope.")] string? calendar = null,
        [Description("Optional new notes for the event.")] string? description = null,
        [Description("Optional new location for the event.")] string? location = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(1);
        using var activity = ActivitySource.StartActivity("update_calendar_event");
        try
        {
            var (target, error) = await ResolveSingleCalendarAsync(calendar, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                return error;

            var startValue = ParseCalendarTime(start);
            var endValue = ParseCalendarTime(end);
            await _ha.UpdateCalendarEventAsync(
                target.EntityId, uid, summary, startValue, endValue, description, location, cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return $"Updated event {uid} on {target.Name} ({target.EntityId}).";
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "update_calendar_event got an unparseable time value.");
            return FailedDateMessage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "update_calendar_event failed.");
            return FailedMessage;
        }
    }

    [Description("Delete an event from one of your Home Assistant calendars. The uid comes from list_calendar_events. Deleting a recurring event removes the whole series, not just one occurrence.")]
    public async Task<string> DeleteCalendarEventAsync(
        [Description("The uid of the event to delete, as returned by list_calendar_events.")] string uid,
        [Description("Calendar entity id or friendly name. Required when more than one calendar is in scope.")] string? calendar = null,
        CancellationToken cancellationToken = default)
    {
        Operations.Add(1);
        using var activity = ActivitySource.StartActivity("delete_calendar_event");
        try
        {
            var (target, error) = await ResolveSingleCalendarAsync(calendar, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                return error;

            await _ha.DeleteCalendarEventAsync(target.EntityId, uid, cancellationToken).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
            return $"Deleted event {uid} from {target.Name} ({target.EntityId}).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "delete_calendar_event failed.");
            return FailedMessage;
        }
    }

    // ── Calendar scope resolution ───────────────────────────────────

    /// <summary>
    /// All calendars in scope: every HA calendar unless the plugin's Calendars
    /// setting restricts them (by entity id or friendly name). Returns an error
    /// string when a configured entry matches nothing.
    /// </summary>
    private async Task<(CalendarEntity[] Calendars, string? Error)> ResolveScopeAsync(string? calendar, CancellationToken cancellationToken)
    {
        var all = await _ha.GetCalendarsAsync(cancellationToken).ConfigureAwait(false);

        var inScope = all;
        if (_configuredCalendars.Length > 0)
        {
            var known = all.SelectMany(c => new[] { c.EntityId, c.Name }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = _configuredCalendars.Where(e => !known.Contains(e)).Distinct().ToArray();
            if (unknown.Length > 0)
                return ([], $"Unknown calendar '{string.Join("', '", unknown)}' in plugin config. Available calendars: {DescribeCalendars(all)}.");

            inScope = all.Where(c => _configuredCalendars.Any(name =>
                string.Equals(c.EntityId, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        }

        if (calendar is null)
            return (inScope, null);

        var match = inScope.FirstOrDefault(c =>
            string.Equals(c.EntityId, calendar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, calendar, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? ([], $"Calendar '{calendar}' not found. Available calendars: {DescribeCalendars(all)}.")
            : ([match], null);
    }

    /// <summary>
    /// The single calendar a write operation targets: the explicit calendar when
    /// given, otherwise the only in-scope calendar. Errors when ambiguous or missing.
    /// </summary>
    private async Task<(CalendarEntity Target, string? Error)> ResolveSingleCalendarAsync(string? calendar, CancellationToken cancellationToken)
    {
        var (scope, error) = await ResolveScopeAsync(calendar, cancellationToken).ConfigureAwait(false);
        if (error is not null)
            return (default!, error);

        if (scope.Length == 0)
            return (default!, "No Home Assistant calendars are available.");
        if (scope.Length > 1 && calendar is null)
            return (default!, $"Multiple calendars are available — set the 'calendar' argument to one of: {DescribeCalendars(scope)}.");

        return (scope[0], null);
    }

    // ── Formatting & parsing helpers ────────────────────────────────

    private static string DescribeCalendars(CalendarEntity[] calendars) =>
        string.Join(", ", calendars.Select(c => $"{c.Name} ({c.EntityId})"));

    private static string FormatEventLine(CalendarEvent evt)
    {
        var when = evt.Start.DateTime is not null
            ? $"{ParseWhen(evt.Start.DateTime!):yyyy-MM-dd HH:mm}-{ParseWhen(evt.End.DateTime!):yyyy-MM-dd HH:mm}"
            : $"All day {evt.Start.Date}-{evt.End.Date}";

        var uid = string.IsNullOrEmpty(evt.Uid) ? "" : $" (uid: {evt.Uid})";
        var line = $"- {when} — {evt.Summary}{uid}";

        if (!string.IsNullOrEmpty(evt.Description))
            line += $"\n  Description: {evt.Description}";
        if (!string.IsNullOrEmpty(evt.Location))
            line += $"\n  Location: {evt.Location}";
        if (!string.IsNullOrEmpty(evt.Rrule))
            line += $"\n  Recurring: {evt.Rrule}";
        return line;
    }

    private static DateTimeOffset ParseWhen(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string ToIso(DateTime localValue) => localValue.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static CalendarDateTime ParseCalendarTime(string value)
    {
        var trimmed = value.Trim();
        if (Regex.IsMatch(trimmed, @"^\d{4}-\d{2}-\d{2}$"))
            return new CalendarDateTime { Date = trimmed };
        var parsed = DateTimeOffset.Parse(trimmed, CultureInfo.InvariantCulture);
        return new CalendarDateTime { DateTime = parsed.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) };
    }

    private const string FailedMessage =
        "Calendar operation failed: Home Assistant did not complete the request. Check that Home Assistant is running and reachable.";

    private const string FailedDateMessage =
        "Calendar operation failed: could not understand the date or time value. Use ISO 8601, e.g. 2026-11-25T14:00:00+01:00, or an all-day date like 2026-12-01.";
}

public sealed class HomeAssistantCalendarPlugin : ILuciaPlugin, IPluginStatusProvider
{
    private string? _statusLine;

    public string PluginId => "home-assistant-calendar";

    public string? ConfigSection => "HomeAssistantCalendar";

    public string? ConfigDescription => "Home Assistant calendar selection for the calendar plugin.";

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new("Calendars", "string", "Comma-separated calendar entity ids or friendly names to include. Leave blank to include every calendar on your Home Assistant.", ""),
    ];

    public string? StatusLine => _statusLine;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var haBaseUrl = (builder.Configuration["HomeAssistant:BaseUrl"] ?? "").Trim();
        if (haBaseUrl.Length == 0)
            return; // Inert until Lucia's Home Assistant connection is configured.

        builder.Services.AddSingleton<IAgentToolProvider>(sp => new HomeAssistantCalendarToolProvider(
            sp.GetRequiredService<IHomeAssistantClient>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("HomeAssistantCalendarPlugin")));
    }

    public async Task OnSystemReadyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var config = services.GetRequiredService<IConfiguration>();
        if (string.IsNullOrWhiteSpace(config["HomeAssistant:BaseUrl"]))
        {
            _statusLine = "Home Assistant is not configured.";
            return;
        }

        try
        {
            var ha = services.GetRequiredService<IHomeAssistantClient>();
            var calendars = await ha.GetCalendarsAsync(cancellationToken).ConfigureAwait(false);
            _statusLine = calendars.Length == 0
                ? "No calendars found on Home Assistant."
                : $"{calendars.Length} calendar(s): {string.Join(", ", calendars.Select(c => c.Name))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statusLine = "Home Assistant is not reachable.";
            services.GetRequiredService<ILoggerFactory>().CreateLogger("HomeAssistantCalendarPlugin")
                .LogDebug(ex, "Could not read calendars for the dashboard status line.");
        }
    }
}

new HomeAssistantCalendarPlugin()
