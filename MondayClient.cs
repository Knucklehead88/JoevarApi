using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>A customer board and the item holding the customer's phone number (E.164).</summary>
public sealed record CustomerBoard(string BoardId, string CustomerName, string ItemId, string PhoneNumber);

/// <summary>Customer board lookup and webhook registration state shared across requests.</summary>
public sealed class MondayCustomerCache
{
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public IReadOnlyList<CustomerBoard> Customers { get; set; } = [];

    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.MinValue;

    public ConcurrentDictionary<string, byte> WebhookBoards { get; } = new();
}

/// <summary>
/// Logs SMS conversations to monday.com. A customer board is any board with a phone number in the
/// <see cref="MondayOptions.PhoneColumnId"/> column; messages go as updates on that item.
/// </summary>
public sealed class MondayClient(
    HttpClient httpClient,
    MondayCustomerCache cache,
    IOptions<MondayOptions> options,
    ILogger<MondayClient> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    // A forced refresh (unknown phone or board) is skipped if the cache is younger than this, so spam can't hammer monday.
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(30);

    // "Norman Winkfield- Customer" -> "Norman Winkfield"
    private static readonly Regex CustomerSuffixRegex = new(@"\s*-?\s*Customer\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MondayOptions settings = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.ApiToken) &&
        !string.IsNullOrWhiteSpace(settings.PhoneColumnId);

    /// <summary>Finds the customer board whose phone number matches, or null for an unknown number.</summary>
    public async Task<CustomerBoard?> FindCustomerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var normalizedPhone = NormalizePhoneNumber(phoneNumber);
        if (normalizedPhone is null)
        {
            return null;
        }

        var cached = await FindCustomerAsync(customer => customer.PhoneNumber == normalizedPhone, cancellationToken);
        if (cached is null)
        {
            return null;
        }

        // Confirm against the live board, in case the number was changed since the cache was loaded.
        var current = await FindCustomerByBoardAsync(cached.BoardId, cancellationToken);
        return current?.PhoneNumber == normalizedPhone ? current : null;
    }

    /// <summary>
    /// Finds the customer board an update was posted on. The phone is read live, not from the cache,
    /// so a number changed in the Phone column is used right away.
    /// </summary>
    public async Task<CustomerBoard?> FindCustomerByBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        var allowedBoardIds = AllowedBoardIds;
        if (allowedBoardIds.Count > 0 && !allowedBoardIds.Contains(boardId))
        {
            return null;
        }

        var customer = (await LoadBoardsAsync([boardId], cancellationToken)).FirstOrDefault();

        // Keep the cache in step, so inbound texts from the new number match and the old number no longer does.
        cache.Customers = cache.Customers
            .Where(cached => cached.BoardId != boardId)
            .Concat(customer is null ? [] : [customer])
            .ToList();
        return customer;
    }

    /// <summary>Reloads the customer boards and registers the reply webhook on each of them.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var customers = await GetCustomersAsync(forceRefresh: true, cancellationToken);
        foreach (var boardId in customers.Select(customer => customer.BoardId).Distinct())
        {
            try
            {
                await EnsureWebhookAsync(boardId, "create_update", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Usually a permission problem (USER_UNAUTHORIZED); one line per board is enough, no stack trace.
                logger.LogWarning("Could not register the Monday.com webhook on board {BoardId}: {Error}", boardId, exception.Message);
            }
        }
    }

    /// <summary>Posts an update on the item, or a reply to an existing update. An optional author name is shown in bold.</summary>
    public async Task CreateUpdateAsync(
        string itemId,
        string text,
        string? parentUpdateId,
        CancellationToken cancellationToken,
        string? authorName = null)
    {
        const string query = """
            mutation ($itemId: ID!, $body: String!, $parentId: ID) {
              create_update(item_id: $itemId, body: $body, parent_id: $parentId) { id }
            }
            """;
        var body = WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
        if (!string.IsNullOrWhiteSpace(authorName))
        {
            body = $"<strong>{WebUtility.HtmlEncode(authorName)}:</strong> {body}";
        }

        await ExecuteAsync(query, new { itemId, body, parentId = parentUpdateId }, cancellationToken);
    }

    /// <summary>Normalizes a phone number to E.164; 10-digit numbers are treated as US numbers.</summary>
    public static string? NormalizePhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        var digits = new string(phoneNumber.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10)
        {
            digits = "1" + digits;
        }

        return digits.Length >= 8 ? "+" + digits : null;
    }

    private async Task<CustomerBoard?> FindCustomerAsync(Func<CustomerBoard, bool> predicate, CancellationToken cancellationToken)
    {
        var customer = (await GetCustomersAsync(forceRefresh: false, cancellationToken)).FirstOrDefault(predicate);
        if (customer is not null)
        {
            return customer;
        }

        // The customer board may be new since the last load.
        await RefreshAsync(cancellationToken);
        return cache.Customers.FirstOrDefault(predicate);
    }

    private async Task<IReadOnlyList<CustomerBoard>> GetCustomersAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var age = DateTimeOffset.UtcNow - cache.LoadedAt;
        if (age < (forceRefresh ? MinimumRefreshInterval : CacheLifetime))
        {
            return cache.Customers;
        }

        await cache.RefreshLock.WaitAsync(cancellationToken);
        try
        {
            age = DateTimeOffset.UtcNow - cache.LoadedAt;
            if (age < (forceRefresh ? MinimumRefreshInterval : CacheLifetime))
            {
                return cache.Customers;
            }

            cache.Customers = await LoadCustomersAsync(cancellationToken);
            cache.LoadedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Loaded {Count} Monday.com customer boards: {Customers}.",
                cache.Customers.Count,
                string.Join(", ", cache.Customers.Select(customer => customer.CustomerName)));
            return cache.Customers;
        }
        finally
        {
            cache.RefreshLock.Release();
        }
    }

    private HashSet<string> AllowedBoardIds =>
        (settings.BoardIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

    private async Task<IReadOnlyList<CustomerBoard>> LoadCustomersAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const string boardsQuery = "query ($page: Int!) { boards(limit: 100, page: $page, state: active) { id type } }";

        // When BoardIds is set, only those boards are used (e.g. a test board); otherwise every board is scanned.
        var allowedBoardIds = AllowedBoardIds;
        var boardIds = new List<string>();
        for (var page = 1; ; page++)
        {
            var data = await ExecuteAsync(boardsQuery, new { page }, cancellationToken);
            var boards = data.GetProperty("boards");
            foreach (var board in boards.EnumerateArray())
            {
                // Skip subitem boards and docs; they never hold the customer's phone.
                var boardId = board.GetProperty("id").GetString()!;
                if (board.GetProperty("type").GetString() == "board" &&
                    (allowedBoardIds.Count == 0 || allowedBoardIds.Contains(boardId)))
                {
                    boardIds.Add(boardId);
                }
            }

            if (boards.GetArrayLength() < pageSize)
            {
                break;
            }
        }

        var customers = new List<CustomerBoard>();
        foreach (var batch in boardIds.Chunk(10))
        {
            customers.AddRange(await LoadBoardsAsync(batch, cancellationToken));
        }

        return customers;
    }

    /// <summary>Reads the current phone number and name of the given boards; boards without a phone are left out.</summary>
    private async Task<IReadOnlyList<CustomerBoard>> LoadBoardsAsync(IReadOnlyList<string> boardIds, CancellationToken cancellationToken)
    {
        // Boards duplicated from the same template keep the same column IDs, so one Phone column ID covers all customers.
        const string query = """
            query ($boardIds: [ID!], $columnIds: [String!]) {
              boards(ids: $boardIds) {
                id
                name
                items_page(limit: 25) { items { id column_values(ids: $columnIds) { text } } }
              }
            }
            """;
        var data = await ExecuteAsync(
            query,
            new { boardIds, columnIds = new[] { settings.PhoneColumnId } },
            cancellationToken);

        var customers = new List<CustomerBoard>();
        foreach (var board in data.GetProperty("boards").EnumerateArray())
        {
            var match = board.GetProperty("items_page").GetProperty("items").EnumerateArray()
                .SelectMany(item => item.GetProperty("column_values").EnumerateArray()
                    .Select(column => (ItemId: item.GetProperty("id").GetString()!, Phone: NormalizePhoneNumber(column.GetProperty("text").GetString()))))
                .FirstOrDefault(entry => entry.Phone is not null);

            if (match.Phone is not null)
            {
                var customerName = CustomerSuffixRegex.Replace(board.GetProperty("name").GetString()?.Trim() ?? string.Empty, string.Empty);
                customers.Add(new CustomerBoard(board.GetProperty("id").GetString()!, customerName, match.ItemId, match.Phone));
            }
        }

        return customers;
    }

    private async Task EnsureWebhookAsync(string boardId, string eventName, CancellationToken cancellationToken)
    {
        var cacheKey = $"{boardId}:{eventName}";
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl) ||
            string.IsNullOrWhiteSpace(settings.WebhookSecret) ||
            cache.WebhookBoards.ContainsKey(cacheKey))
        {
            return;
        }

        const string listQuery = "query ($boardId: ID!) { webhooks(board_id: $boardId) { event } }";
        var existing = await ExecuteAsync(listQuery, new { boardId }, cancellationToken);
        var hasWebhook = existing.GetProperty("webhooks").EnumerateArray()
            .Any(webhook => webhook.GetProperty("event").GetString() == eventName);

        if (!hasWebhook)
        {
            const string createQuery = """
                mutation ($boardId: ID!, $url: String!, $event: WebhookEventType!) {
                  create_webhook(board_id: $boardId, url: $url, event: $event) { id }
                }
                """;
            var url = $"{settings.WebhookUrl}?token={Uri.EscapeDataString(settings.WebhookSecret)}";
            await ExecuteAsync(createQuery, new { boardId, url, @event = eventName }, cancellationToken);
            logger.LogInformation("Registered the Monday.com {Event} webhook on board {BoardId}.", eventName, boardId);
        }

        cache.WebhookBoards.TryAdd(cacheKey, 0);
    }

    private async Task<JsonElement> ExecuteAsync(string query, object variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.monday.com/v2")
        {
            Content = JsonContent.Create(new { query, variables })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new HttpRequestException($"Monday.com returned a GraphQL error: {errors}");
        }

        return document.RootElement.GetProperty("data").Clone();
    }
}

/// <summary>Reloads customer boards and registers webhooks on startup and every 10 minutes.</summary>
public sealed class MondayRefreshService(IServiceProvider services, ILogger<MondayRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            var monday = services.GetRequiredService<MondayClient>();
            if (!monday.IsConfigured)
            {
                return;
            }

            try
            {
                await monday.RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to refresh Monday.com customer boards.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class MondayOptions
{
    public const string SectionName = "Monday";

    public string? ApiToken { get; init; }

    /// <summary>ID of the column holding the customer's phone number; the same on every customer board.</summary>
    public string? PhoneColumnId { get; init; }

    /// <summary>Optional comma-separated board IDs to limit SMS logging to; empty means every board with a phone.</summary>
    public string? BoardIds { get; init; }

    /// <summary>Public URL of /api/monday/webhook, used to register webhooks on boards.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Shared secret expected in the monday webhook URL (?token=...).</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Only updates starting with this prefix are texted to the customer; everything else stays an internal note.</summary>
    public string ReplyPrefix { get; init; } = "/sms";
}
