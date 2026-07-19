namespace FileServerMonitor.Core;

public static class AgentPollingSchedule
{
    public static TimeSpan CalculateDelay(
        TimeSpan interval,
        TimeSpan elapsed,
        TimeSpan? minimumYield = null)
    {
        var safeInterval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : interval;
        var safeElapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        var safeMinimumYield = minimumYield is null || minimumYield <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(100)
            : minimumYield.Value;
        var remaining = safeInterval - safeElapsed;
        return remaining > safeMinimumYield ? remaining : safeMinimumYield;
    }
}
