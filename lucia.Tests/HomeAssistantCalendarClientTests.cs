using System.Text;
using System.Text.Json;
using lucia.HomeAssistant.Configuration;
using lucia.HomeAssistant.Models;
using lucia.HomeAssistant.Services;
using lucia.Tests.Helpers;
using lucia.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace lucia.Tests;

/// <summary>
/// Covers the calendar read model extension (uid/rrule/status) and the
/// calendar write paths: create via the calendar.create_event REST service,
/// delete/update via the WebSocket API.
/// </summary>
public class HomeAssistantCalendarClientTests
{
    // ── Event wire format ───────────────────────────────────────────

    [Fact]
    public void CalendarEvent_DeserializesFullWireFormat()
    {
        const string Json = """
        [
            {
                "summary": "Team standup",
                "start": {"dateTime": "2026-11-14T09:00:00+01:00"},
                "end": {"dateTime": "2026-11-14T09:30:00+01:00"},
                "description": "Daily sync",
                "location": "Kitchen",
                "uid": "evt-123",
                "rrule": "FREQ=DAILY",
                "status": "confirmed"
            }
        ]
        """;

        var events = JsonSerializer.Deserialize<CalendarEvent[]>(Json);

        Assert.NotNull(events);
        var evt = events![0];
        Assert.Equal("Team standup", evt.Summary);
        Assert.Equal("2026-11-14T09:00:00+01:00", evt.Start.DateTime);
        Assert.Null(evt.Start.Date);
        Assert.Equal("Daily sync", evt.Description);
        Assert.Equal("Kitchen", evt.Location);
        Assert.Equal("evt-123", evt.Uid);
        Assert.Equal("FREQ=DAILY", evt.Rrule);
        Assert.Equal("confirmed", evt.Status);
    }

    [Fact]
    public void CalendarEvent_MissingOptionalFields_AreNull()
    {
        const string Json = """
        [
            {
                "summary": "Birthday",
                "start": {"date": "2026-12-01"},
                "end": {"date": "2026-12-02"}
            }
        ]
        """;

        var events = JsonSerializer.Deserialize<CalendarEvent[]>(Json);

        var evt = events![0];
        Assert.Equal("2026-12-01", evt.Start.Date);
        Assert.Null(evt.Start.DateTime);
        Assert.Null(evt.Uid);
        Assert.Null(evt.Rrule);
        Assert.Null(evt.Status);
    }

    // ── Create (REST service) ───────────────────────────────────────

    [Fact]
    public async Task CreateCalendarEvent_Timed_UsesDateTimeServiceKeys()
    {
        List<(string Method, string Path, string Body)> requests = [];
        var client = CreateClient(request =>
        {
            requests.Add((request.Method.ToString(), request.RequestUri!.PathAndQuery, ReadBody(request)));
            return JsonResponse("[]");
        });

        await client.CreateCalendarEventAsync(
            "calendar.local",
            "Dentist",
            new CalendarDateTime { DateTime = "2026-11-25T14:00:00+00:00" },
            new CalendarDateTime { DateTime = "2026-11-25T15:00:00+00:00" },
            description: "bring insurance card",
            location: "Dental clinic");

        var (method, path, body) = requests.Single();
        Assert.Equal("POST", method);
        Assert.Equal("/api/services/calendar/create_event", path);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("calendar.local", root.GetProperty("entity_id").GetString());
        Assert.Equal("Dentist", root.GetProperty("summary").GetString());
        Assert.Equal("2026-11-25T14:00:00+00:00", root.GetProperty("start_datetime").GetString());
        Assert.Equal("2026-11-25T15:00:00+00:00", root.GetProperty("end_datetime").GetString());
        Assert.Equal("bring insurance card", root.GetProperty("description").GetString());
        Assert.Equal("Dental clinic", root.GetProperty("location").GetString());
        Assert.False(root.TryGetProperty("start_date", out _));
        Assert.False(root.TryGetProperty("end_date", out _));
    }

    [Fact]
    public async Task CreateCalendarEvent_AllDay_UsesDateServiceKeys()
    {
        List<(string Method, string Path, string Body)> requests = [];
        var client = CreateClient(request =>
        {
            requests.Add((request.Method.ToString(), request.RequestUri!.PathAndQuery, ReadBody(request)));
            return JsonResponse("[]");
        });

        await client.CreateCalendarEventAsync(
            "calendar.local",
            "Vacation",
            new CalendarDateTime { Date = "2026-12-01" },
            new CalendarDateTime { Date = "2026-12-08" });

        var (_, _, body) = requests.Single();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("2026-12-01", root.GetProperty("start_date").GetString());
        Assert.Equal("2026-12-08", root.GetProperty("end_date").GetString());
        Assert.False(root.TryGetProperty("start_datetime", out _));
        Assert.False(root.TryGetProperty("end_datetime", out _));
        // Optional fields are omitted entirely when null.
        Assert.False(root.TryGetProperty("description", out _));
        Assert.False(root.TryGetProperty("location", out _));
    }

    [Fact]
    public async Task CreateCalendarEvent_MixedDateTypes_ThrowsWithoutCalling()
    {
        List<HttpRequestMessage> requests = [];
        var client = CreateClient(request =>
        {
            requests.Add(request);
            return JsonResponse("[]");
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateCalendarEventAsync(
                "calendar.local",
                "Mixed",
                new CalendarDateTime { Date = "2026-12-01" },
                new CalendarDateTime { DateTime = "2026-12-08T10:00:00+00:00" }));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task CreateCalendarEvent_HttpError_Throws()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"message":"Service not found"}""")
        });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateCalendarEventAsync(
                "calendar.local",
                "Dentist",
                new CalendarDateTime { DateTime = "2026-11-25T14:00:00+00:00" },
                new CalendarDateTime { DateTime = "2026-11-25T15:00:00+00:00" }));
    }

    // ── Delete (WebSocket) ──────────────────────────────────────────

    [Fact]
    public async Task DeleteCalendarEvent_SendsAuthenticatedWsCommand()
    {
        await using var server = new HomeAssistantWebSocketServer();
        var client = CreateClient(baseUrl: server.BaseUrl, accessToken: "test-token", handler: _ => throw new InvalidOperationException("no REST expected"));

        await client.DeleteCalendarEventAsync("calendar.local", "evt-123");

        Assert.Equal("test-token", server.LastAuthToken);
        var command = JsonDocument.Parse(server.ReceivedCommands.Single()).RootElement;
        Assert.Equal("calendar/event/delete", command.GetProperty("type").GetString());
        Assert.Equal("calendar.local", command.GetProperty("entity_id").GetString());
        Assert.Equal("evt-123", command.GetProperty("uid").GetString());
    }

    [Fact]
    public async Task DeleteCalendarEvent_WsError_ThrowsWithServerMessage()
    {
        await using var server = new HomeAssistantWebSocketServer
        {
            CommandHandler = _ => (false, null, "UID not found")
        };
        var client = CreateClient(baseUrl: server.BaseUrl, accessToken: "test-token", handler: _ => throw new InvalidOperationException("no REST expected"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DeleteCalendarEventAsync("calendar.local", "missing-uid"));

        Assert.Contains("calendar/event/delete", ex.Message);
        Assert.Contains("UID not found", ex.Message);
    }

    // ── Update (WebSocket) ──────────────────────────────────────────

    [Fact]
    public async Task UpdateCalendarEvent_SendsFullEventDict()
    {
        await using var server = new HomeAssistantWebSocketServer();
        var client = CreateClient(baseUrl: server.BaseUrl, accessToken: "test-token", handler: _ => throw new InvalidOperationException("no REST expected"));

        await client.UpdateCalendarEventAsync(
            "calendar.local",
            "evt-123",
            "Dentist (moved)",
            new CalendarDateTime { DateTime = "2026-11-25T16:00:00+00:00" },
            new CalendarDateTime { DateTime = "2026-11-25T17:00:00+00:00" },
            description: "rescheduled");

        var command = JsonDocument.Parse(server.ReceivedCommands.Single()).RootElement;
        Assert.Equal("calendar/event/update", command.GetProperty("type").GetString());
        Assert.Equal("calendar.local", command.GetProperty("entity_id").GetString());
        Assert.Equal("evt-123", command.GetProperty("uid").GetString());

        var evt = command.GetProperty("event");
        Assert.Equal("Dentist (moved)", evt.GetProperty("summary").GetString());
        Assert.Equal("2026-11-25T16:00:00+00:00", evt.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("2026-11-25T17:00:00+00:00", evt.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.Equal("rescheduled", evt.GetProperty("description").GetString());
        Assert.False(evt.TryGetProperty("location", out _));
    }

    [Fact]
    public async Task UpdateCalendarEvent_AllDay_SendsDateForm()
    {
        await using var server = new HomeAssistantWebSocketServer();
        var client = CreateClient(baseUrl: server.BaseUrl, accessToken: "test-token", handler: _ => throw new InvalidOperationException("no REST expected"));

        await client.UpdateCalendarEventAsync(
            "calendar.local",
            "evt-123",
            "Vacation (extended)",
            new CalendarDateTime { Date = "2026-12-01" },
            new CalendarDateTime { Date = "2026-12-10" });

        var command = JsonDocument.Parse(server.ReceivedCommands.Single()).RootElement;
        var evt = command.GetProperty("event");
        Assert.Equal("2026-12-01", evt.GetProperty("start").GetProperty("date").GetString());
        Assert.Equal("2026-12-10", evt.GetProperty("end").GetProperty("date").GetString());
    }

    [Fact]
    public async Task UpdateCalendarEvent_WsError_ThrowsWithServerMessage()
    {
        await using var server = new HomeAssistantWebSocketServer
        {
            CommandHandler = _ => (false, null, "Calendar does not support event update")
        };
        var client = CreateClient(baseUrl: server.BaseUrl, accessToken: "test-token", handler: _ => throw new InvalidOperationException("no REST expected"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateCalendarEventAsync(
                "calendar.local",
                "evt-123",
                "Dentist (moved)",
                new CalendarDateTime { DateTime = "2026-11-25T16:00:00+00:00" },
                new CalendarDateTime { DateTime = "2026-11-25T17:00:00+00:00" }));

        Assert.Contains("calendar/event/update", ex.Message);
        Assert.Contains("Calendar does not support event update", ex.Message);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static HomeAssistantClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string baseUrl = "http://homeassistant.local",
        string accessToken = "token")
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(handler))
        {
            BaseAddress = new Uri(baseUrl)
        };

        var options = new TestOptionsMonitor<HomeAssistantOptions>(new HomeAssistantOptions
        {
            BaseUrl = baseUrl,
            AccessToken = accessToken
        });

        return new HomeAssistantClient(httpClient, NullLogger<HomeAssistantClient>.Instance, options);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string ReadBody(HttpRequestMessage request)
    {
        return request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
