namespace FileServerMonitor.Core;

public sealed record AgentCycleMetrics(
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    long DurationMs,
    int SecurityEventsRead,
    int UsnEventsRead,
    int CorrelatedEvents,
    int SentEvents,
    int QueuedEvents,
    string? Error);

public sealed record AgentOperationalHealthInput(
    string Status,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastSuccessfulSendUtc,
    int PendingQueueEvents,
    AgentCycleMetrics? LastCycle,
    DateTimeOffset NowUtc,
    int StaleAfterMinutes,
    int BacklogWarningThreshold,
    int SendLagWarningMinutes);

public sealed record AgentOperationalHealthResult(
    string Level,
    long? LastHeartbeatAgeSeconds,
    long? LastSuccessfulSendAgeSeconds,
    bool HasError,
    string Reason);

public static class AgentOperationalHealth
{
    public static AgentOperationalHealthResult Evaluate(AgentOperationalHealthInput input)
    {
        var heartbeatAge = GetAgeSeconds(input.NowUtc, input.LastHeartbeatUtc);
        var sendAge = GetAgeSeconds(input.NowUtc, input.LastSuccessfulSendUtc);
        var staleAfterMinutes = Math.Max(1, input.StaleAfterMinutes);
        var backlogWarningThreshold = Math.Max(1, input.BacklogWarningThreshold);
        var sendLagWarningMinutes = Math.Max(1, input.SendLagWarningMinutes);
        var lastCycleAge = GetAgeSeconds(input.NowUtc, input.LastCycle?.FinishedUtc);
        var hasRecentSuccessfulCycle = input.LastCycle?.Error is null
            && input.LastCycle is not null
            && lastCycleAge is not null
            && lastCycleAge <= staleAfterMinutes * 60L
            && input.LastCycle.QueuedEvents == 0;
        var hasError = !string.IsNullOrWhiteSpace(input.LastCycle?.Error)
            || input.Status.Equals("degraded", StringComparison.OrdinalIgnoreCase);

        if (input.LastHeartbeatUtc is null)
        {
            return new AgentOperationalHealthResult(
                Level: "critical",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: hasError,
                Reason: "Agente sem heartbeat registrado.");
        }

        if (heartbeatAge is not null && heartbeatAge > staleAfterMinutes * 60L)
        {
            return new AgentOperationalHealthResult(
                Level: "critical",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: hasError,
                Reason: $"Sem heartbeat ha {heartbeatAge.Value / 60} minuto(s).");
        }

        if (!input.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
            && !input.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentOperationalHealthResult(
                Level: "attention",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: hasError,
                Reason: $"Status reportado: {input.Status}.");
        }

        if (input.PendingQueueEvents >= backlogWarningThreshold)
        {
            return new AgentOperationalHealthResult(
                Level: "critical",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: hasError,
                Reason: $"Fila local com {input.PendingQueueEvents} evento(s), acima do limite.");
        }

        if (input.PendingQueueEvents > 0)
        {
            return new AgentOperationalHealthResult(
                Level: "attention",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: hasError,
                Reason: $"Fila local com {input.PendingQueueEvents} evento(s) pendente(s).");
        }

        if (hasError)
        {
            return new AgentOperationalHealthResult(
                Level: "attention",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: true,
                Reason: input.LastCycle?.Error ?? "Ultimo ciclo reportou erro.");
        }

        if (sendAge is not null && sendAge > sendLagWarningMinutes * 60L && !hasRecentSuccessfulCycle)
        {
            return new AgentOperationalHealthResult(
                Level: "attention",
                LastHeartbeatAgeSeconds: heartbeatAge,
                LastSuccessfulSendAgeSeconds: sendAge,
                HasError: false,
                Reason: $"Ultimo envio com sucesso ha {sendAge.Value / 60} minuto(s).");
        }

        return new AgentOperationalHealthResult(
            Level: "ok",
            LastHeartbeatAgeSeconds: heartbeatAge,
            LastSuccessfulSendAgeSeconds: sendAge,
            HasError: false,
            Reason: hasRecentSuccessfulCycle && input.LastCycle?.SentEvents == 0
                ? "Coleta recente sem eventos novos para enviar."
                : "Coleta e envio sem sinais de atraso.");
    }

    private static long? GetAgeSeconds(DateTimeOffset now, DateTimeOffset? value)
    {
        return value is null
            ? null
            : Math.Max(0, (long)now.Subtract(value.Value).TotalSeconds);
    }
}
