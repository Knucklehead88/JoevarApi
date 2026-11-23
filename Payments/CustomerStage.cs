/// <summary>
/// A step on the customers' progress bar in the staff app: Paid Deposit, Received Blueprint, Installed Cabinets,
/// Installed Countertop, and any the office adds. <see cref="Key"/> marks the stages the app completes by itself.
/// </summary>
public sealed class CustomerStage
{
    /// <summary>The stage a paid deposit completes.</summary>
    public const string DepositKey = "deposit";

    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Order on the progress bar, from 0.</summary>
    public int Position { get; set; }

    /// <summary>Set on stages the app completes itself (<see cref="DepositKey"/>); they can be renamed but not deleted.</summary>
    public string? Key { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A customer reached a stage: when, and who marked it (or "Stripe" for a paid deposit).</summary>
public sealed class CustomerStageCompletion
{
    public Guid CustomerId { get; set; }

    public Guid StageId { get; set; }

    public CustomerStage Stage { get; set; } = null!;

    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CompletedBy { get; set; }
}

/// <summary>Settings the office changes in the staff app, stored by key (e.g. the kitchen visit deposit).</summary>
public sealed class SiteSetting
{
    public const string KitchenDepositCents = "KitchenDepositCents";

    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? UpdatedBy { get; set; }
}
