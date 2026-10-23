using System.Collections.Concurrent;
using System.Text.Json.Serialization;

/// <summary>
/// The 50 US states and, per state, the cities from the free countriesnow.space API (the same source the
/// registration page offers). Cities are cached per state for a day. When the service can't be reached the
/// answer is "unknown", so a registration isn't refused because a third party is down.
/// </summary>
public sealed class UsCityDirectory(HttpClient http, ILogger<UsCityDirectory> logger)
{
    /// <summary>The service has no Washington, D.C., so neither does the form.</summary>
    public static readonly string[] States =
    [
        "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware", "Florida",
        "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky", "Louisiana", "Maine",
        "Maryland", "Massachusetts", "Michigan", "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada",
        "New Hampshire", "New Jersey", "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma",
        "Oregon", "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah",
        "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming",
    ];

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, string[] Cities)> Cache = new();

    /// <summary>The state's canonical name, or null if it isn't one of the 50 (case-insensitive).</summary>
    public static string? FindState(string state) =>
        States.FirstOrDefault(name => string.Equals(name, state.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The city's canonical spelling if the state lists it, <c>""</c> if the list loaded but doesn't have it, or null
    /// if the list couldn't be loaded.
    /// </summary>
    public async Task<string?> FindCityAsync(string state, string city, CancellationToken cancellationToken)
    {
        var cities = await GetCitiesAsync(state, cancellationToken);
        if (cities is null)
        {
            return null;
        }

        return cities.FirstOrDefault(name => string.Equals(name, city.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private async Task<string[]?> GetCitiesAsync(string state, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(state, out var cached) && cached.Fetched > DateTimeOffset.UtcNow.AddDays(-1))
        {
            return cached.Cities;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var url = $"https://countriesnow.space/api/v0.1/countries/state/cities/q?country=United%20States&state={Uri.EscapeDataString(state)}";
            var response = await http.GetFromJsonAsync<CitiesResponse>(url, timeout.Token);
            if (response is null || response.Error || response.Data is null || response.Data.Length == 0)
            {
                logger.LogWarning("The city service returned no cities for {State}.", state);
                return null;
            }

            Cache[state] = (DateTimeOffset.UtcNow, response.Data);
            return response.Data;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not load the cities of {State}; the city was not checked.", state);
            return null;
        }
    }

    private sealed record CitiesResponse(
        [property: JsonPropertyName("error")] bool Error,
        [property: JsonPropertyName("data")] string[]? Data);
}
