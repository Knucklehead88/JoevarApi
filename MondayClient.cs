using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>A customer board and the item holding the customer's phone number (E.164).</summary>
public sealed record CustomerBoard(string BoardId, string CustomerName, string ItemId, string PhoneNumber)
{
    /// <summary>How the customer is addressed in monday updates, e.g. "@TestCustomer".</summary>
    public string Handle => "@" + string.Concat(CustomerName.Where(character => !char.IsWhiteSpace(character)));
}

/// <summary>Customer board lookup, webhook registration and echo state shared across requests.</summary>
public sealed class MondayCustomerCache
{
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public IReadOnlyList<CustomerBoard> Customers { get; set; } = [];

    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.MinValue;

    public ConcurrentDictionary<string, byte> WebhookBoards { get; } = new();

    /// <summary>Customer messages this API just posted, so their webhook echo is not texted back to the customer.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> PostedCustomerMessages { get; } = new();
}

/// <summary>
/// Logs SMS conversations to monday.com. A customer board is any board (except ticket boards) with a phone number
/// in a column titled "Phone"; updates starting with the customer's @handle are texted to them.
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

    private static readonly TimeSpan EchoLifetime = TimeSpan.FromMinutes(5);

    // "Norman Winkfield- Customer" / "Norman Winkfield — Customer" / "Norman Winkfield —" -> "Norman Winkfield"
    private static readonly Regex CustomerSuffixRegex = new(@"\s*[-–—]+\s*(Customer)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MondayOptions settings = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.ApiToken);

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
        var customer = (await LoadBoardsAsync([boardId], cancellationToken)).FirstOrDefault();

        // Keep the cache in step, so inbound texts from the new number match and the old number no longer does.
        cache.Customers = cache.Customers
            .Where(cached => cached.BoardId != boardId)
            .Concat(customer is null ? [] : [customer])
            .ToList();
        return customer;
    }

    /// <summary>Reloads the customer boards and registers the update webhook on each of them.</summary>
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

    /// <summary>
    /// Posts a customer's text as "@Handle message": a reply to the last message sent to them, or a new update
    /// on their phone item if nothing was sent yet.
    /// </summary>
    public async Task PostCustomerMessageAsync(CustomerBoard customer, string message, CancellationToken cancellationToken)
    {
        var text = $"{customer.Handle} {message}";
        var lastSent = await FindLastSentMessageAsync(customer, cancellationToken);

        // Remember it before posting: monday may deliver the webhook before create_update returns.
        RememberPostedCustomerMessage(lastSent?.ItemId ?? customer.ItemId, text);
        await CreateUpdateAsync(lastSent?.ItemId ?? customer.ItemId, text, lastSent?.UpdateId, cancellationToken);
    }

    /// <summary>True when the update is a customer message this API posted itself.</summary>
    public bool IsPostedCustomerMessage(string itemId, string text) =>
        cache.PostedCustomerMessages.TryRemove(EchoKey(itemId, text), out var postedAt) &&
        DateTimeOffset.UtcNow - postedAt < EchoLifetime;

    /// <summary>Posts plain text as an update on the item, or as a reply to an existing update.</summary>
    public async Task CreateUpdateAsync(
        string itemId,
        string text,
        string? parentUpdateId,
        CancellationToken cancellationToken)
    {
        const string query = """
            mutation ($itemId: ID!, $body: String!, $parentId: ID) {
              create_update(item_id: $itemId, body: $body, parent_id: $parentId) { id }
            }
            """;
        var body = WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
        await ExecuteAsync(query, new { itemId, body, parentId = parentUpdateId }, cancellationToken);
    }

    /// <summary>
    /// If <paramref name="text"/> starts with the customer's @handle (spaces and case ignored, so "@Test Customer"
    /// matches "@TestCustomer"), returns the message after it.
    /// </summary>
    public static bool TryGetMessageForCustomer(string text, CustomerBoard customer, out string message)
    {
        message = string.Empty;
        text = text.Trim();
        if (!text.StartsWith('@'))
        {
            return false;
        }

        var handle = customer.Handle[1..];
        var position = 1;
        foreach (var expected in handle)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position >= text.Length || char.ToUpperInvariant(text[position]) != char.ToUpperInvariant(expected))
            {
                return false;
            }

            position++;
        }

        // The handle must end at a word boundary ("@TestCustomers" is not "@TestCustomer").
        if (position < text.Length && char.IsLetterOrDigit(text[position]))
        {
            return false;
        }

        message = text[position..].TrimStart(' ', ':', ',', '-').Trim();
        return message.Length > 0;
    }

    /// <summary>Normalizes a phone number to E.164; 10-digit numbers are treated as US numbers.</summary>
    public static string? NormalizePhoneNumber(string? phoneNumber) => PhoneNumbers.Normalize(phoneNumber);

    private void RememberPostedCustomerMessage(string itemId, string text)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in cache.PostedCustomerMessages.Where(entry => now - entry.Value > EchoLifetime))
        {
            cache.PostedCustomerMessages.TryRemove(expired.Key, out _);
        }

        cache.PostedCustomerMessages[EchoKey(itemId, text)] = now;
    }

    private static string EchoKey(string itemId, string text) =>
        $"{itemId}|{string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}";

    /// <summary>Finds the newest top-level update on the customer's board addressed to their @handle.</summary>
    private async Task<(string ItemId, string UpdateId)?> FindLastSentMessageAsync(CustomerBoard customer, CancellationToken cancellationToken)
    {
        const string query = "query ($boardIds: [ID!]) { boards(ids: $boardIds) { updates(limit: 50) { id item_id text_body created_at } } }";
        var data = await ExecuteAsync(query, new { boardIds = new[] { customer.BoardId } }, cancellationToken);
        var boards = data.GetProperty("boards");
        if (boards.GetArrayLength() == 0)
        {
            return null;
        }

        return boards[0].GetProperty("updates").EnumerateArray()
            .Where(update => TryGetMessageForCustomer(update.GetProperty("text_body").GetString() ?? string.Empty, customer, out _))
            .OrderByDescending(update => update.GetProperty("created_at").GetString(), StringComparer.Ordinal)
            .Select(update => ((string ItemId, string UpdateId)?)(update.GetProperty("item_id").GetString()!, update.GetProperty("id").GetString()!))
            .FirstOrDefault();
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
                string.Join(", ", cache.Customers.Select(customer => customer.Handle)));
            return cache.Customers;
        }
        finally
        {
            cache.RefreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<CustomerBoard>> LoadCustomersAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const string boardsQuery = "query ($page: Int!) { boards(limit: 100, page: $page, state: active) { id type } }";

        var boardIds = new List<string>();
        for (var page = 1; ; page++)
        {
            var data = await ExecuteAsync(boardsQuery, new { page }, cancellationToken);
            var boards = data.GetProperty("boards");

            // Skip subitem boards and docs; they never hold the customer's phone.
            boardIds.AddRange(boards.EnumerateArray()
                .Where(board => board.GetProperty("type").GetString() == "board")
                .Select(board => board.GetProperty("id").GetString()!));

            if (boards.GetArrayLength() < pageSize)
            {
                break;
            }
        }

        var customers = new List<CustomerBoard>();
        foreach (var batch in boardIds.Chunk(5))
        {
            customers.AddRange(await LoadBoardsAsync(batch, cancellationToken));
        }

        return customers;
    }

    /// <summary>
    /// Reads the current phone number and customer name of the given boards. Ticket boards and boards
    /// without a phone number are left out.
    /// </summary>
    private async Task<IReadOnlyList<CustomerBoard>> LoadBoardsAsync(IReadOnlyList<string> boardIds, CancellationToken cancellationToken)
    {
        const string query = """
            query ($boardIds: [ID!]) {
              boards(ids: $boardIds) {
                id
                name
                items_page(limit: 25) { items { id column_values { type text column { title } } } }
              }
            }
            """;
        var data = await ExecuteAsync(query, new { boardIds }, cancellationToken);

        var customers = new List<CustomerBoard>();
        foreach (var board in data.GetProperty("boards").EnumerateArray())
        {
            var boardName = board.GetProperty("name").GetString()?.Trim() ?? string.Empty;
            if (boardName.Contains("Ticket", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = board.GetProperty("items_page").GetProperty("items").EnumerateArray()
                .SelectMany(item => item.GetProperty("column_values").EnumerateArray()
                    .Where(IsPhoneColumn)
                    .Select(column => (ItemId: item.GetProperty("id").GetString()!, Phone: NormalizePhoneNumber(column.GetProperty("text").GetString()))))
                .FirstOrDefault(entry => entry.Phone is not null);

            if (match.Phone is not null)
            {
                var customerName = CustomerSuffixRegex.Replace(boardName, string.Empty);
                customers.Add(new CustomerBoard(board.GetProperty("id").GetString()!, customerName, match.ItemId, match.Phone));
            }
        }

        return customers;
    }

    // A monday Phone column, or any column titled "Phone".
    private static bool IsPhoneColumn(JsonElement columnValue) =>
        columnValue.GetProperty("type").GetString() == "phone" ||
        string.Equals(
            columnValue.GetProperty("column").GetProperty("title").GetString()?.Trim(),
            "Phone",
            StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Public URL of /api/monday/webhook, used to register webhooks on boards.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Shared secret expected in the monday webhook URL (?token=...).</summary>
    public string? WebhookSecret { get; init; }
}
