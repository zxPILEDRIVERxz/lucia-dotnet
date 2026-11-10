using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using lucia.Agents.Abstractions;
using lucia.HomeAssistant.Models;
using lucia.HomeAssistant.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Parsed WeatherAPI.com settings. Mirrors the Tater weather_api integration
/// (https://github.com/TaterTotterson/Tater_Integrations, integrations/weather_api.py).
/// </summary>
public sealed class WeatherApiOptions
{
    public string ApiKey { get; init; } = "";
    public string DefaultLocation { get; init; } = "";
    public bool UseHomeLocation { get; init; } = true;
    public int DefaultDays { get; init; } = 3;
    public bool MetricUnits { get; init; }
    public bool IncludeAqi { get; init; } = true;
    public bool IncludePollen { get; init; } = true;
    public bool IncludeAlerts { get; init; } = true;
    public int ShowHourlyPeek { get; init; } = 6;
    public int MaxResponseChars { get; init; } = 650;
    public int TimeoutSeconds { get; init; } = 12;
}

/// <summary>
/// The home location read from Home Assistant's system configuration
/// (the user's onboarding coordinates) plus a human-readable display name.
/// </summary>
public sealed record WeatherHomeLocation(double Latitude, double Longitude, string DisplayName);

/// <summary>
/// Decides which location a weather lookup targets when the user doesn't name one:
/// a place named in the question wins, then the configured default override,
/// then the automatic Home Assistant home coordinates (when enabled and usable).
/// Coordinates are unambiguous to WeatherAPI's geocoder; bare postal codes are not.
/// </summary>
public static class WeatherQueryResolver
{
    public static string? Resolve(string? requestedLocation, string? defaultOverride, bool useHomeLocation, WeatherHomeLocation? home)
    {
        if (!string.IsNullOrWhiteSpace(requestedLocation))
            return requestedLocation.Trim();

        if (!string.IsNullOrWhiteSpace(defaultOverride))
            return defaultOverride.Trim();

        if (useHomeLocation && IsUsable(home))
            return Coordinates(home!);

        return null;
    }

    public static bool IsUsable(WeatherHomeLocation? home) =>
        home is not null && (home.Latitude != 0 || home.Longitude != 0);

    public static string Coordinates(WeatherHomeLocation home) =>
        $"{home.Latitude:0.######}, {home.Longitude:0.######}";
}

/// <summary>
/// Normalizes WeatherAPI.com forecast.json payloads into one canonical shape before
/// deserialization. The service has served two shapes over time: the classic
/// <c>current_condition[]</c> / <c>forecast[]</c> layout and the current spec's
/// <c>current</c> object / <c>forecast.forecastday[]</c> layout with top-level
/// <c>alerts.alert[]</c>. This accepts both so the plugin keeps working either way.
/// </summary>
public static class WeatherResponseNormalizer
{
    public static string Normalize(string raw)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }

        if (node is not JsonObject root)
            return raw;

        // forecast: object { forecastday: [...] } -> [...] (clone: node already has a parent)
        if (root["forecast"] is JsonObject fore && fore["forecastday"] is JsonArray days)
            root["forecast"] = days.DeepClone();

        // current_condition missing but a `current` object is present -> wrap in list
        if (root["current_condition"] is null && root["current"] is JsonObject cur)
            root["current_condition"] = new JsonArray(cur.DeepClone());

        NormalizeAlerts(root);

        if (root["forecast"] is JsonArray forecast)
        {
            foreach (var dayNode in forecast)
            {
                if (dayNode is not JsonObject day)
                    continue;
                HoistDayTemps(day);
                ConvertPollenObject(day);
                NormalizeAlerts(day);
                NormalizeHours(day);
            }
        }

        return root.ToJsonString();
    }

    /// <summary>Moves max/min temps up to the day entry when they only exist in the `day` sub-object.</summary>
    private static void HoistDayTemps(JsonObject day)
    {
        if (day["day"] is not JsonObject sub)
            return;
        foreach (var key in new[] { "maxtemp_c", "maxtemp_f", "mintemp_c", "mintemp_f" })
        {
            if (day[key] is null && sub[key] is JsonNode node)
                day[key] = node.DeepClone();
        }
    }

    /// <summary>Converts a pollen object ({ "Birch": 102, ... }) into the list shape ([{name, count}]).</summary>
    private static void ConvertPollenObject(JsonObject day)
    {
        if (day["pollen"] is not JsonObject pollenObj)
            return;
        var list = new JsonArray();
        foreach (var (name, value) in pollenObj)
        {
            if (value is null)
                continue;
            if (value is JsonValue jv && jv.TryGetValue<double>(out var count) && count == 0)
                continue;
            list.Add(new JsonObject { ["name"] = name, ["count"] = value.DeepClone() });
        }
        day["pollen"] = list;
    }

    /// <summary>Unwraps alerts containers ({ event: [...] } or { alert: [...] }) into a bare array.</summary>
    private static void NormalizeAlerts(JsonObject scope)
    {
        if (scope["alerts"] is not JsonObject alertsObj)
            return;
        var list = alertsObj["event"] as JsonArray ?? alertsObj["alert"] as JsonArray;
        if (list is not null)
            scope["alerts"] = list.DeepClone();
    }

    /// <summary>Converts numeric hour `time` values (0/300/600...) to their string form.</summary>
    private static void NormalizeHours(JsonObject day)
    {
        if (day["hour"] is not JsonArray hours)
            return;
        foreach (var hourNode in hours)
        {
            if (hourNode is not JsonObject hour)
                continue;
            if (hour["time"] is JsonValue tv && tv.TryGetValue<double>(out var t))
                hour["time"] = ((long)t).ToString();
        }
    }
}

public sealed class WeatherForecastResponse
{
    [JsonPropertyName("location")]
    public WeatherLocationDto? Location { get; set; }

    [JsonPropertyName("current_condition")]
    public List<WeatherCurrentConditionDto> CurrentCondition { get; set; } = [];

    [JsonPropertyName("forecast")]
    public List<WeatherForecastDayDto> Forecast { get; set; } = [];

    [JsonPropertyName("alerts")]
    public List<WeatherAlertDto>? Alerts { get; set; }
}

public sealed class WeatherLocationDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("localtime")]
    public string? Localtime { get; set; }
}

public sealed class WeatherCurrentConditionDto
{
    [JsonPropertyName("temp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempF { get; set; }

    [JsonPropertyName("temp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempC { get; set; }

    [JsonPropertyName("feelslike_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? FeelslikeF { get; set; }

    [JsonPropertyName("feelslike_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? FeelslikeC { get; set; }

    [JsonPropertyName("humidity")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Humidity { get; set; }

    [JsonPropertyName("wind_mph")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? WindMph { get; set; }

    [JsonPropertyName("wind_kmph")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? WindKmph { get; set; }

    [JsonPropertyName("wind_dir_16_point")]
    public string? WindDir16 { get; set; }

    [JsonPropertyName("wind_dir")]
    public string? WindDir { get; set; }

    [JsonPropertyName("weatherDesc")]
    public List<WeatherDescriptionDto>? WeatherDesc { get; set; }

    [JsonPropertyName("condition")]
    public WeatherConditionDto? Condition { get; set; }

    /// <summary>Condition text from either the classic weatherDesc list or the condition object.</summary>
    public string? ConditionText()
    {
        var fromList = WeatherDesc is { Count: > 0 } d ? d[0].Value : null;
        return string.IsNullOrWhiteSpace(fromList) ? Condition?.Text : fromList;
    }

    /// <summary>Wind direction from either the classic or current field name.</summary>
    public string? WindDirection =>
        string.IsNullOrWhiteSpace(WindDir16) ? WindDir : WindDir16;
}

public sealed class WeatherDescriptionDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class WeatherConditionDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class WeatherForecastDayDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("maxtemp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MaxTempF { get; set; }

    [JsonPropertyName("mintemp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MinTempF { get; set; }

    [JsonPropertyName("maxtemp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MaxTempC { get; set; }

    [JsonPropertyName("mintemp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MinTempC { get; set; }

    [JsonPropertyName("day")]
    public WeatherDaySummaryDto? Day { get; set; }

    [JsonPropertyName("aqi")]
    public WeatherAqiDto? Aqi { get; set; }

    [JsonPropertyName("alerts")]
    public List<WeatherAlertDto>? Alerts { get; set; }

    [JsonPropertyName("pollen")]
    public List<WeatherPollenDto>? Pollen { get; set; }

    [JsonPropertyName("hour")]
    public List<WeatherHourDto>? Hour { get; set; }

    /// <summary>Day-level rain chance, falling back to the max hourly value.</summary>
    public double? DailyChanceOfRain()
    {
        if (Day?.DailyChanceOfRain is not null)
            return Day.DailyChanceOfRain;
        double? max = null;
        foreach (var h in Hour ?? [])
        {
            var chance = h.ChanceOfRain();
            if (chance is not null && (max is null || chance > max))
                max = chance;
        }
        return max;
    }
}

public sealed class WeatherDaySummaryDto
{
    [JsonPropertyName("daily_chance_of_rain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? DailyChanceOfRain { get; set; }
}

public sealed class WeatherAqiDto
{
    [JsonPropertyName("avgAqi")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? AvgAqi { get; set; }
}

public sealed class WeatherAlertDto
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("expires")]
    public string? Expires { get; set; }

    /// <summary>Human-readable alert line, e.g. "Severe Thunderstorm Warning until 9 PM CDT".</summary>
    public string DisplayText()
    {
        var name = string.IsNullOrWhiteSpace(Event) ? (Headline ?? "") : Event;
        return string.IsNullOrWhiteSpace(Expires) ? name : $"{name} until {Expires}";
    }
}

public sealed class WeatherPollenDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Count { get; set; }
}

public sealed class WeatherHourDto
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("temp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempF { get; set; }

    [JsonPropertyName("temp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempC { get; set; }

    [JsonPropertyName("chanceofrain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? ChanceOfRainLegacy { get; set; }

    [JsonPropertyName("chance_of_rain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? ChanceOfRainNew { get; set; }

    [JsonPropertyName("condition")]
    public WeatherConditionDto? Condition { get; set; }

    /// <summary>Rain chance from either the classic or current field name.</summary>
    public double? ChanceOfRain() => ChanceOfRainNew ?? ChanceOfRainLegacy;

    /// <summary>Condition text from either field of the condition object.</summary>
    public string? ConditionText() => Condition?.Text ?? Condition?.Value;
}

public sealed class WeatherApiSkill : IWeatherSkill, IPluginStatusProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeatherApiOptions _options;
    private readonly ILogger _logger;
    private readonly IHomeAssistantClient? _homeAssistant;

    private static readonly ActivitySource ActivitySource = new("Lucia.Skills.Weather.WeatherApi", "1.0.0");
    private static readonly Meter Meter = new("Lucia.Skills.Weather.WeatherApi", "1.0.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("weather.weatherapi.requests", "{count}", "Number of WeatherAPI.com forecast requests.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("weather.weatherapi.failures", "{count}", "Number of failed WeatherAPI.com requests.");
    private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("weather.weatherapi.duration", "ms", "Duration of WeatherAPI.com forecast operations.");

    private static readonly JsonSerializerOptions DeserializationOptions = new() { PropertyNameCaseInsensitive = true };

    private WeatherHomeLocation? _home;
    private string? _statusLine;
    private string _apiNote = "";

    public string? StatusLine => _statusLine;

    public WeatherApiSkill(IHttpClientFactory httpClientFactory, WeatherApiOptions options, ILogger logger, IHomeAssistantClient? homeAssistant = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _homeAssistant = homeAssistant;
    }

    public IList<AITool> GetTools() => [AIFunctionFactory.Create(GetWeatherAsync, new AIFunctionFactoryOptions { Name = "get_weather" })];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_options.UseHomeLocation)
            await TryLoadHomeLocationAsync(cancellationToken).ConfigureAwait(false);
        else
            _statusLine = "Automatic home location is disabled — DefaultLocation is used when no place is named.";

        _logger.LogInformation("WeatherAPI skill initialized (default location: {Location}, units: {Units}).",
            string.IsNullOrWhiteSpace(_options.DefaultLocation) ? "<not set>" : _options.DefaultLocation,
            _options.MetricUnits ? "metric" : "us");
    }

    /// <summary>
    /// Reads the home coordinates from Home Assistant's system configuration and resolves a
    /// human-readable place name with one lightweight forecast call (which also verifies the API key).
    /// Every failure mode is non-fatal and reported via <see cref="StatusLine"/> on the dashboard.
    /// </summary>
    private async Task TryLoadHomeLocationAsync(CancellationToken cancellationToken)
    {
        if (_homeAssistant is null)
        {
            _statusLine = "No Home Assistant connection available — automatic home location skipped.";
            return;
        }

        ConfigResponse config;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            config = await _homeAssistant.GetConfigAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Home Assistant home location; weather will use the configured default location.");
            _statusLine = "Home Assistant is unreachable — home location not detected. Set a default location manually if needed.";
            return;
        }

        if (config.Latitude == 0 && config.Longitude == 0)
        {
            _statusLine = "Home Assistant has no home coordinates set — set a default location manually.";
            return;
        }

        var raw = new WeatherHomeLocation(config.Latitude, config.Longitude, config.LocationName);
        var display = await TryResolveDisplayNameAsync(raw).ConfigureAwait(false);
        _home = new WeatherHomeLocation(raw.Latitude, raw.Longitude, display);

        var note = string.IsNullOrEmpty(_apiNote) ? "" : $" — {_apiNote}";
        var overrideNote = string.IsNullOrWhiteSpace(_options.DefaultLocation) ? "" : " (not used while DefaultLocation is set)";
        _statusLine = $"Home location (from Home Assistant): {display}{overrideNote}{note}";
        _logger.LogInformation("WeatherAPI home location resolved from Home Assistant: {Display} ({Lat}, {Lon}).", display, raw.Latitude, raw.Longitude);
    }

    /// <summary>Resolves a place name for the home coordinates; falls back to the coordinate string on any failure.</summary>
    private async Task<string> TryResolveDisplayNameAsync(WeatherHomeLocation home)
    {
        var coords = WeatherQueryResolver.Coordinates(home);
        try
        {
            var query = new StringBuilder()
                .Append("q=").Append(Uri.EscapeDataString(coords))
                .Append("&days=1&units=us")
                .Append("&key=").Append(Uri.EscapeDataString(_options.ApiKey));

            var client = _httpClientFactory.CreateClient("WeatherApi");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await client.GetAsync($"https://api.weatherapi.com/v1/forecast.json?{query}", cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                _logger.LogWarning("WeatherAPI key check failed at startup: HTTP {StatusCode} — {Body}", (int)response.StatusCode, Truncate(body, 200));
                _apiNote = "WeatherAPI call failed: " + ExtractErrorMessage(body);
                return coords;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<WeatherForecastResponse>(WeatherResponseNormalizer.Normalize(json), DeserializationOptions);
            var loc = result?.Location;
            if (loc is null)
                return coords;

            var parts = new List<string> { loc.Name, loc.Region, loc.Country };
            var display = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray());
            return display.Length > 0 ? display : coords;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve a display name for the home location; using coordinates.");
            return coords;
        }
    }

    [Description("Get current weather conditions and a multi-day forecast from WeatherAPI.com. Use for questions like 'what's the weather', 'will it rain tomorrow', or 'forecast this weekend'. Returns compact text starting with the place name, then current conditions, any alerts, daily high/low, and optionally the next few hours. Always state which place the result is for — use the place name from the first line of the result — instead of assuming it is the user's home.")]
    public async Task<string> GetWeatherAsync(
        [Description("Optional location — city name, 'lat,lon' coordinates, or postal code (prefer a city name; bare postal codes can geocode to a different country). When omitted, falls back to the configured default location, then the automatic home location.")] string? location = null,
        [Description("Optional number of forecast days (1-14). Defaults to the configured value.")] int? days = null,
        CancellationToken cancellationToken = default)
    {
        var q = WeatherQueryResolver.Resolve(location, _options.DefaultLocation, _options.UseHomeLocation, _home);
        if (q is null)
            return "No weather location is available — no place was named and no default or home location is configured. Ask the user for a city name, and note that a default can be set in the Lucia dashboard under Plugins > WeatherAPI.com.";

        var forecastDays = Math.Clamp(days ?? _options.DefaultDays, 1, 14);

        Requests.Add(1);
        var start = Stopwatch.GetTimestamp();
        using var activity = ActivitySource.StartActivity();
        activity?.SetTag("weather.location", q);
        activity?.SetTag("weather.days", forecastDays);

        try
        {
            var query = new StringBuilder()
                .Append("q=").Append(Uri.EscapeDataString(q))
                .Append("&days=").Append(forecastDays)
                .Append("&units=").Append(_options.MetricUnits ? "metric" : "us")
                .Append("&aqi=").Append(_options.IncludeAqi ? "yes" : "no")
                .Append("&alerts=").Append(_options.IncludeAlerts ? "yes" : "no")
                .Append("&pollen=").Append(_options.IncludePollen ? "yes" : "no")
                .Append("&key=").Append(Uri.EscapeDataString(_options.ApiKey));

            var client = _httpClientFactory.CreateClient("WeatherApi");
            var response = await client.GetAsync($"https://api.weatherapi.com/v1/forecast.json?{query}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Failures.Add(1);
                _logger.LogWarning("WeatherAPI returned {StatusCode} for '{Location}': {Body}", (int)response.StatusCode, q, Truncate(errorBody, 300));
                return $"Weather lookup failed: WeatherAPI.com returned HTTP {(int)response.StatusCode}. {ExtractErrorMessage(errorBody)}";
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // WeatherAPI is inconsistent about casing: current_condition uses temp_C/temp_F
            // while forecast entries use temp_c/temp_f, so match property names case-insensitively.
            var result = JsonSerializer.Deserialize<WeatherForecastResponse>(WeatherResponseNormalizer.Normalize(json), DeserializationOptions);
            if (result is null)
            {
                Failures.Add(1);
                return "Weather lookup failed: empty response from WeatherAPI.com.";
            }

            var ms = RecordDuration(start);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogDebug("WeatherAPI forecast for '{Location}' completed in {Ms}ms.", q, ms);
            return RenderForecast(result, forecastDays);
        }
        catch (Exception ex)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "Weather lookup failed for location: {Location}.", q);
            return $"Weather lookup failed: {ex.Message}";
        }
    }

    private string RenderForecast(WeatherForecastResponse r, int requestedDays)
    {
        var loc = r.Location;
        var place = JoinParts(", ", loc?.Name, loc?.Region);
        if (place.Length == 0 && loc is not null)
            place = loc.Country;
        if (place.Length == 0)
            place = "<unknown location>";

        var core = new StringBuilder(place);
        var cur = r.CurrentCondition.Count > 0 ? r.CurrentCondition[0] : null;
        if (cur is not null)
        {
            var condition = (cur.ConditionText() ?? "").ToLowerInvariant();
            var conditionSuffix = condition.Length > 0 ? " " + condition : "";
            if (_options.MetricUnits)
                core.Append($" — now {Fmt(cur.TempC)}°C{conditionSuffix}, feels like {Fmt(cur.FeelslikeC)}°, humidity {Fmt(cur.Humidity)}%, wind {JoinParts(" ", cur.WindDirection, $"{Fmt(cur.WindKmph)} km/h")}");
            else
                core.Append($" — now {Fmt(cur.TempF)}°F{conditionSuffix}, feels like {Fmt(cur.FeelslikeF)}°, humidity {Fmt(cur.Humidity)}%, wind {JoinParts(" ", cur.WindDirection, $"{Fmt(cur.WindMph)} mph")}");
        }

        if (_options.IncludeAlerts)
        {
            var alerts = (r.Alerts ?? []).Cast<WeatherAlertDto>()
                .Concat(r.Forecast.SelectMany(d => d.Alerts ?? []))
                .Where(a => !string.IsNullOrWhiteSpace(a.Event) || !string.IsNullOrWhiteSpace(a.Headline))
                .DistinctBy(a => a.DisplayText())
                .Take(2);
            if (alerts.Any())
                core.Append('\n').Append("Alerts: ").Append(string.Join("; ", alerts.Select(a => a.DisplayText())));
        }

        var daily = new List<string>();
        foreach (var (day, i) in r.Forecast.Take(requestedDays).Select((d, i) => (d, i)))
        {
            var hi = _options.MetricUnits ? day.MaxTempC : day.MaxTempF;
            var lo = _options.MetricUnits ? day.MinTempC : day.MinTempF;
            if (hi is null || lo is null)
                continue;

            var unit = _options.MetricUnits ? "°C" : "°F";
            var label = i == 0 ? "Today" : i == 1 ? "Tomorrow" : ShortDate(day.Date);
            var line = $"{label}: high {Fmt(hi)}{unit} / low {Fmt(lo)}{unit}";
            var rainChance = day.DailyChanceOfRain();
            if (rainChance is not null && rainChance > 0)
                line += $", rain up to {(int)Math.Round(rainChance.Value)}%";
            daily.Add(line);
        }
        if (daily.Count > 0)
            core.Append('\n').Append(string.Join(" · ", daily));

        var hourly = BuildHourlyPeek(r);
        var extras = BuildExtras(r.Forecast.Count > 0 ? r.Forecast[0] : null);

        string Combine(params string[] parts) => string.Join("\n", parts.Where(p => p.Length > 0).ToArray());

        var full = Combine(core.ToString(), hourly, extras);
        if (full.Length <= _options.MaxResponseChars)
            return full;

        var noHourly = Combine(core.ToString(), extras);
        if (noHourly.Length <= _options.MaxResponseChars)
            return noHourly;

        var coreText = core.ToString();
        if (coreText.Length <= _options.MaxResponseChars)
            return coreText;

        return Truncate(coreText, _options.MaxResponseChars);
    }

    private string BuildHourlyPeek(WeatherForecastResponse r)
    {
        if (_options.ShowHourlyPeek <= 0 || r.Forecast.Count == 0 || r.Forecast[0].Hour is not { Count: > 0 })
            return "";

        // The hourly list starts at local midnight in 3-hour blocks, so the current block index
        // is simply the current local hour divided by 3.
        var startIdx = 0;
        if (DateTime.TryParse(r.Location?.Localtime, out var now))
            startIdx = Math.Clamp(now.Hour / 3, 0, 7);

        var entries = new List<string>();
        for (var dayIdx = 0; dayIdx < r.Forecast.Count && entries.Count < _options.ShowHourlyPeek; dayIdx++)
        {
            var hours = r.Forecast[dayIdx].Hour ?? [];
            var from = dayIdx == 0 ? startIdx : 0;
            for (var i = from; i < hours.Count && entries.Count < _options.ShowHourlyPeek; i++)
            {
                var h = hours[i];
                if (string.IsNullOrWhiteSpace(h.Time) || (_options.MetricUnits ? h.TempC : h.TempF) is null)
                    continue;

                var temp = _options.MetricUnits ? h.TempC!.Value : h.TempF!.Value;
                var unit = _options.MetricUnits ? "°C" : "°F";
                var condition = (h.ConditionText() ?? "").ToLowerInvariant();
                entries.Add($"{FormatHour(h.Time)} {Fmt(temp)}{unit} {condition}".Trim());
            }
        }

        return entries.Count > 0 ? "Next hours: " + string.Join(" · ", entries) : "";
    }

    private static string BuildExtras(WeatherForecastDayDto? today)
    {
        var parts = new List<string>();
        var aqi = today?.Aqi?.AvgAqi;
        if (aqi is not null && aqi > 0)
            parts.Add($"AQI {(long)Math.Round(aqi.Value)}");
        if (today?.Pollen is { Count: > 0 } pollen)
        {
            var entries = pollen.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Take(3).Select(p => $"{p.Name} {Fmt(p.Count)}".Trim());
            if (entries.Any())
                parts.Add("Pollen: " + string.Join(", ", entries));
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    /// <summary>Formats a temperature-like number without a trailing .0 (voice friendly).</summary>
    private static string Fmt(double? value)
    {
        if (value is null)
            return "";
        return value.Value % 1 == 0 ? ((long)value.Value).ToString() : value.Value.ToString("0.#");
    }

    private static string FormatHour(string time)
    {
        if (int.TryParse(time, out var n))
        {
            var h = n / 100;
            var m = n % 100;
            var suffix = h >= 12 ? "PM" : "AM";
            var displayH = h % 12 == 0 ? 12 : h % 12;
            return m == 0 ? $"{displayH} {suffix}" : $"{displayH}:{m:00} {suffix}";
        }

        // Tolerate ISO-style timestamps ("2026-11-09T15:00:00Z") from newer response shapes.
        if (DateTime.TryParse(time, out var dt))
            return $"{(dt.Hour % 12 == 0 ? 12 : dt.Hour % 12)}:{dt.Minute:00} {(dt.Hour >= 12 ? "PM" : "AM")}";

        return time;
    }

    private static string ShortDate(string iso)
    {
        if (DateTime.TryParse(iso, out var d))
            return d.ToString("MMM d");
        return iso;
    }

    private static string JoinParts(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray());

    private static string Truncate(string value, int maxChars)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars].TrimEnd() + "…";
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg) &&
                msg.GetString() is { Length: > 0 } message)
                return $"WeatherAPI says: {message}";
        }
        catch (JsonException)
        {
            // Fall through to raw-body truncation below.
        }

        return Truncate(body, 200);
    }

    private static double RecordDuration(long start)
    {
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        DurationMs.Record(ms);
        return ms;
    }
}

public sealed class WeatherApiPlugin : ILuciaPlugin, IPluginStatusProvider
{
    private IServiceProvider? _services;

    public string PluginId => "weather-api";

    public string? ConfigSection => "WeatherApi";
    public string? ConfigDescription => "WeatherAPI.com connection settings. Restart Lucia after changing these so the weather tool picks them up.";
    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new("ApiKey", "string", "WeatherAPI.com API key (free at https://www.weatherapi.com/my-account/apikey)", "", IsSensitive: true),
        new("DefaultLocation", "string", "Optional override for the default weather location — city, postal code, or lat,lon. Wins over the automatic home location; leave empty to use your Home Assistant home.", ""),
        new("UseHomeLocation", "string", "Use the Home Assistant home location as the default when no place is named ('true' or 'false')", "true"),
        new("DefaultDays", "string", "Default number of forecast days (1-14)", "3"),
        new("Units", "string", "Units: 'us' or 'metric'", "us"),
        new("IncludeAqi", "string", "Include air quality index: 'true' or 'false'", "true"),
        new("IncludePollen", "string", "Include pollen counts: 'true' or 'false'", "true"),
        new("IncludeAlerts", "string", "Include weather alerts: 'true' or 'false'", "true"),
        new("ShowHourlyPeek", "string", "How many of the next hours to include (0 = off)", "6"),
        new("MaxResponseChars", "string", "Maximum characters returned to the agent (120-4000)", "650"),
    ];

    /// <summary>Live status for the dashboard: where default weather lookups target, and any startup issues.</summary>
    public string? StatusLine => (_services?.GetService<IWeatherSkill>() as WeatherApiSkill)?.StatusLine
        ?? "No API key configured — the weather tool is inactive.";

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var apiKey = (config["WeatherApi:ApiKey"] ?? config["WEATHERAPI_KEY"] ?? "").Trim();
        if (apiKey.Length == 0)
            return;

        var options = new WeatherApiOptions
        {
            ApiKey = apiKey,
            DefaultLocation = (config["WeatherApi:DefaultLocation"] ?? "").Trim(),
            UseHomeLocation = Bool(config["WeatherApi:UseHomeLocation"], true),
            DefaultDays = BoundedInt(config["WeatherApi:DefaultDays"], fallback: 3, min: 1, max: 14),
            MetricUnits = (config["WeatherApi:Units"] ?? "us").Trim().ToLowerInvariant() == "metric",
            IncludeAqi = Bool(config["WeatherApi:IncludeAqi"], true),
            IncludePollen = Bool(config["WeatherApi:IncludePollen"], true),
            IncludeAlerts = Bool(config["WeatherApi:IncludeAlerts"], true),
            ShowHourlyPeek = BoundedInt(config["WeatherApi:ShowHourlyPeek"], fallback: 6, min: 0, max: 48),
            MaxResponseChars = BoundedInt(config["WeatherApi:MaxResponseChars"], fallback: 650, min: 120, max: 4000),
        };

        builder.Services.AddHttpClient("WeatherApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BoundedInt(config["WeatherApi:TimeoutSeconds"], fallback: 12, min: 2, max: 60));
            client.DefaultRequestHeaders.Add("User-Agent", "Lucia/WeatherApiPlugin");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        builder.Services.AddSingleton<IWeatherSkill>(sp =>
            new WeatherApiSkill(
                sp.GetRequiredService<IHttpClientFactory>(),
                options,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<WeatherApiSkill>(),
                sp.GetService<IHomeAssistantClient>()));
    }

    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _services = services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("WeatherApiPlugin");
        if (services.GetService<IWeatherSkill>() is WeatherApiSkill)
            logger.LogInformation("WeatherAPI plugin active — weather tool registered.");
        else
            logger.LogInformation("WeatherAPI plugin loaded but no API key configured — no weather tool registered.");
        return Task.CompletedTask;
    }

    private static int BoundedInt(string? value, int fallback, int min, int max)
    {
        var parsed = int.TryParse(value?.Trim(), out var v) ? v : fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static bool Bool(string? value, bool fallback)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "y": case "on": case "enabled": return true;
            case "0": case "false": case "no": case "n": case "off": case "disabled": return false;
            default: return fallback;
        }
    }
}

new WeatherApiPlugin()
