using Sky17.Application;

namespace Sky17.Web.Infrastructure;

/// <summary>
/// Runs the dispatcher every few seconds: expires unanswered offers (30 s), offers searching orders to the
/// next nearest free driver, auto-cancels orders the store never accepted, and alerts admins.
/// </summary>
public sealed class DispatchWorker(IDispatchService dispatch, ILogger<DispatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var r = await dispatch.RunCycleAsync(stoppingToken);
                if (r.OffersSent + r.OffersExpired + r.OrdersAutoCancelled + r.Alerts > 0)
                    logger.LogInformation("Dispatch: sent {Sent}, expired {Expired}, auto-cancelled {Cancelled}, alerts {Alerts}",
                        r.OffersSent, r.OffersExpired, r.OrdersAutoCancelled, r.Alerts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one failing cycle stop dispatching.
                logger.LogError(ex, "Dispatch cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
