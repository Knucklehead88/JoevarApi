using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

/// <summary>The monday.com item holding a customer's SMS thread, and the customer's E.164 phone number.</summary>
public sealed record MondayConversation(string BoardId, string ItemId, string PhoneNumber);

/// <summary>A board named "&lt;customer&gt;- Customer" and the item holding the customer's phone number.</summary>
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
/// Logs SMS conversations to monday.com. Customers are found by the Phone column on boards whose name ends
/// with <see cref="MondayOptions.CustomerBoardSuffix"/>; unknown numbers get an item on the inbox board.
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

    private readonly MondayOptions settings = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.ApiToken) &&
        !string.IsNullOrWhiteSpace(settings.BoardId) &&
        !string.IsNullOrWhiteSpace(settings.PhoneColumnId);

    /// <summary>Finds the customer's thread by phone, falling back to an inbox item for unknown numbers.</summary>
    public async Task<MondayConversation> GetOrCreateConversationAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var normalizedPhone = NormalizePhoneNumber(phoneNumber) ?? phoneNumber;
        var customer = await FindCustomerAsync(c => c.PhoneNumber == normalizedPhone, cancellationToken);
        if (customer is not null)
        {
            return new MondayConversation(customer.BoardId, customer.ItemId, customer.PhoneNumber);
        }

        var inboxItemId =
            await FindItemIdAsync(settings.BoardId!, settings.PhoneColumnId!, phoneNumber, cancellationToken) ??
            await CreateItemAsync(
                settings.BoardId!,
                phoneNumber,
                new Dictionary<string, string> { [settings.PhoneColumnId!] = phoneNumber },
                cancellationToken);
        return new MondayConversation(settings.BoardId!, inboxItemId, normalizedPhone);
    }

    /// <summary>
    /// Finds the customer thread an update was posted from: an inbox item, any item on a customer board,
    /// or a ticket whose group name matches a customer board.
    /// </summary>
    public async Task<MondayConversation?> FindConversationForItemAsync(
        string boardId,
        string itemId,
        CancellationToken cancellationToken)
    {
        if (boardId == settings.BoardId)
        {
            var phone = NormalizePhoneNumber(await GetColumnTextAsync(itemId, settings.PhoneColumnId!, cancellationToken));
            return phone is null ? null : new MondayConversation(boardId, itemId, phone);
        }

        CustomerBoard? customer;
        if (boardId == settings.TicketsBoardId)
        {
            var groupTitle = await GetGroupTitleAsync(itemId, cancellationToken);
            if (groupTitle is null)
            {
                return null;
            }

            var customerName = NormalizeName(groupTitle);
            customer = await FindCustomerAsync(c => NormalizeName(c.CustomerName) == customerName, cancellationToken);
        }
        else
        {
            customer = await FindCustomerAsync(c => c.BoardId == boardId, cancellationToken);
        }

        return customer is null ? null : new MondayConversation(customer.BoardId, customer.ItemId, customer.PhoneNumber);
    }

    /// <summary>Reloads the customer boards and registers the reply webhook on every board we text from.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var customers = await GetCustomersAsync(forceRefresh: true, cancellationToken);

        var boardIds = customers.Select(customer => customer.BoardId).Append(settings.BoardId!);
        if (!string.IsNullOrWhiteSpace(settings.TicketsBoardId))
        {
            boardIds = boardIds.Append(settings.TicketsBoardId);
        }

        foreach (var boardId in boardIds.Distinct())
        {
            try
            {
                await EnsureWebhookAsync(boardId, "create_update", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to register the Monday.com webhook on board {BoardId}.", boardId);
            }
        }
    }

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

    private static string NormalizeName(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();

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
            logger.LogInformation("Loaded {Count} Monday.com customer boards.", cache.Customers.Count);
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
        const string boardsQuery = "query ($page: Int!) { boards(limit: 100, page: $page, state: active) { id name } }";

        var customerBoards = new List<(string Id, string CustomerName)>();
        for (var page = 1; ; page++)
        {
            var data = await ExecuteAsync(boardsQuery, new { page }, cancellationToken);
            var boards = data.GetProperty("boards");
            foreach (var board in boards.EnumerateArray())
            {
                var name = board.GetProperty("name").GetString()?.Trim() ?? string.Empty;
                if (name.EndsWith(settings.CustomerBoardSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    customerBoards.Add((board.GetProperty("id").GetString()!, name[..^settings.CustomerBoardSuffix.Length].Trim()));
                }
            }

            if (boards.GetArrayLength() < pageSize)
            {
                break;
            }
        }

        const string phonesQuery = """
            query ($boardIds: [ID!]) {
              boards(ids: $boardIds) {
                id
                items_page(limit: 25) { items { id column_values(types: [phone]) { text } } }
              }
            }
            """;
        var names = customerBoards.ToDictionary(board => board.Id, board => board.CustomerName);
        var customers = new List<CustomerBoard>();
        foreach (var batch in customerBoards.Chunk(10))
        {
            var data = await ExecuteAsync(phonesQuery, new { boardIds = batch.Select(board => board.Id).ToArray() }, cancellationToken);
            foreach (var board in data.GetProperty("boards").EnumerateArray())
            {
                var boardId = board.GetProperty("id").GetString()!;
                var match = board.GetProperty("items_page").GetProperty("items").EnumerateArray()
                    .SelectMany(item => item.GetProperty("column_values").EnumerateArray()
                        .Select(column => (ItemId: item.GetProperty("id").GetString()!, Phone: NormalizePhoneNumber(column.GetProperty("text").GetString()))))
                    .FirstOrDefault(entry => entry.Phone is not null);

                if (match.Phone is null)
                {
                    logger.LogWarning("Customer board {BoardId} has no phone number.", boardId);
                    continue;
                }

                customers.Add(new CustomerBoard(boardId, names[boardId], match.ItemId, match.Phone));
            }
        }

        return customers;
    }

    private async Task<string?> FindItemIdAsync(
        string boardId,
        string columnId,
        string value,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ($boardId: ID!, $columnId: String!, $value: String!) {
              items_page_by_column_values(limit: 1, board_id: $boardId, columns: [{ column_id: $columnId, column_values: [$value] }]) {
                items { id }
              }
            }
            """;
        var data = await ExecuteAsync(query, new { boardId, columnId, value }, cancellationToken);
        var items = data.GetProperty("items_page_by_column_values").GetProperty("items");
        return items.GetArrayLength() > 0 ? items[0].GetProperty("id").GetString() : null;
    }

    private async Task<string> CreateItemAsync(
        string boardId,
        string itemName,
        Dictionary<string, string>? columnValues,
        CancellationToken cancellationToken)
    {
        const string query = """
            mutation ($boardId: ID!, $itemName: String!, $columnValues: JSON) {
              create_item(board_id: $boardId, item_name: $itemName, column_values: $columnValues) { id }
            }
            """;
        var data = await ExecuteAsync(
            query,
            new
            {
                boardId,
                itemName,
                columnValues = columnValues is null ? null : JsonSerializer.Serialize(columnValues)
            },
            cancellationToken);
        return data.GetProperty("create_item").GetProperty("id").GetString()!;
    }

    private async Task<string?> GetColumnTextAsync(string itemId, string columnId, CancellationToken cancellationToken)
    {
        const string query = """
            query ($itemIds: [ID!], $columnIds: [String!]) {
              items(ids: $itemIds) { column_values(ids: $columnIds) { text } }
            }
            """;
        var data = await ExecuteAsync(
            query,
            new { itemIds = new[] { itemId }, columnIds = new[] { columnId } },
            cancellationToken);
        var items = data.GetProperty("items");
        if (items.GetArrayLength() == 0)
        {
            return null;
        }

        var columns = items[0].GetProperty("column_values");
        var text = columns.GetArrayLength() > 0 ? columns[0].GetProperty("text").GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task<string?> GetGroupTitleAsync(string itemId, CancellationToken cancellationToken)
    {
        const string query = "query ($itemIds: [ID!]) { items(ids: $itemIds) { group { title } } }";
        var data = await ExecuteAsync(query, new { itemIds = new[] { itemId } }, cancellationToken);
        var items = data.GetProperty("items");
        return items.GetArrayLength() > 0 ? items[0].GetProperty("group").GetProperty("title").GetString() : null;
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

    /// <summary>Inbox board for numbers that don't match any customer board.</summary>
    public string? BoardId { get; init; }

    /// <summary>ID of the inbox board's Text column holding the sender's E.164 phone number.</summary>
    public string? PhoneColumnId { get; init; }

    /// <summary>Boards whose name ends with this are customer boards; the rest of the name is the customer name.</summary>
    public string CustomerBoardSuffix { get; init; } = "- Customer";

    /// <summary>Trade Tickets board; a ticket's group name is matched to the customer name.</summary>
    public string? TicketsBoardId { get; init; }

    /// <summary>Public URL of /api/monday/webhook, used to register webhooks on boards.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Shared secret expected in the monday webhook URL (?token=...).</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Only updates starting with this prefix are texted to the customer; everything else stays an internal note.</summary>
    public string ReplyPrefix { get; init; } = "/sms";
}
