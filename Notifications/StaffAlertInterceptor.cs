using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Notifies staff devices when a new kitchen booking, contractor registration or customer is saved, wherever in the
/// code it happens (website forms, phone or Google sign-up). The alert is queued only once the save succeeded.
/// </summary>
public sealed class StaffAlertInterceptor(PushQueue queue) : SaveChangesInterceptor
{
    // Alerts found while saving, per context, until the save completes.
    private readonly ConditionalWeakTable<DbContext, List<PushPayload>> pending = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null)
        {
            pending.Remove(eventData.Context);
        }

        base.SaveChangesFailed(eventData);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var alerts = context.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity switch
            {
                KitchenBooking booking => new PushPayload(
                    "New kitchen design request",
                    $"{booking.Name}: {booking.AppointmentDate:ddd, MMM d} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}",
                    "/calendar",
                    "new-booking"),
                ContractorApplication application => new PushPayload(
                    "New contractor registration",
                    $"{application.FullName}, {application.City}, {application.State}",
                    "/dashboard",
                    "new-contractor"),
                Customer customer => new PushPayload("New customer sign-up", customer.Name, "/dashboard", "new-customer"),
                _ => null
            })
            .OfType<PushPayload>()
            .ToList();
        if (alerts.Count > 0)
        {
            pending.AddOrUpdate(context, alerts);
        }
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var alerts))
        {
            return;
        }

        pending.Remove(context);
        foreach (var alert in alerts)
        {
            queue.Enqueue(new PushAudience.Staff(), alert);
        }
    }
}
