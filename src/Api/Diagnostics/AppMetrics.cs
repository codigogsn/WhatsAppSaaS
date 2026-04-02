using System.Threading;

namespace WhatsAppSaaS.Api.Diagnostics;

/// <summary>
/// Lightweight application metrics with Prometheus-compatible text export.
/// Thread-safe counters tracked via Interlocked for direct value reading.
/// </summary>
public static class AppMetrics
{
    private static long _messagesProcessed;
    private static long _messagesFailed;
    private static long _webhookEnqueueFailures;
    private static long _processingDurationSum; // stored as ticks for precision
    private static long _processingDurationCount;

    public static void RecordProcessed(double durationMs)
    {
        Interlocked.Increment(ref _messagesProcessed);
        Interlocked.Add(ref _processingDurationSum, (long)(durationMs * 1000)); // microseconds
        Interlocked.Increment(ref _processingDurationCount);
    }

    public static void RecordFailed() => Interlocked.Increment(ref _messagesFailed);
    public static void RecordEnqueueFailure() => Interlocked.Increment(ref _webhookEnqueueFailures);

    public static string RenderPrometheus(long queuePending)
    {
        var processed = Interlocked.Read(ref _messagesProcessed);
        var failed = Interlocked.Read(ref _messagesFailed);
        var enqueueFailures = Interlocked.Read(ref _webhookEnqueueFailures);
        var durationCount = Interlocked.Read(ref _processingDurationCount);
        var durationSumUs = Interlocked.Read(ref _processingDurationSum);
        var avgMs = durationCount > 0 ? (durationSumUs / 1000.0) / durationCount : 0;

        return $"""
            # HELP whatsappsaas_queue_pending Pending messages in webhook queue
            # TYPE whatsappsaas_queue_pending gauge
            whatsappsaas_queue_pending {queuePending}
            # HELP whatsappsaas_messages_processed_total Total messages processed successfully
            # TYPE whatsappsaas_messages_processed_total counter
            whatsappsaas_messages_processed_total {processed}
            # HELP whatsappsaas_messages_failed_total Total messages that failed processing
            # TYPE whatsappsaas_messages_failed_total counter
            whatsappsaas_messages_failed_total {failed}
            # HELP whatsappsaas_webhook_enqueue_failures_total Total webhook enqueue failures
            # TYPE whatsappsaas_webhook_enqueue_failures_total counter
            whatsappsaas_webhook_enqueue_failures_total {enqueueFailures}
            # HELP whatsappsaas_message_processing_duration_ms Average message processing duration
            # TYPE whatsappsaas_message_processing_duration_ms gauge
            whatsappsaas_message_processing_duration_ms {avgMs:F1}
            # HELP whatsappsaas_message_processing_count Total messages measured for duration
            # TYPE whatsappsaas_message_processing_count counter
            whatsappsaas_message_processing_count {durationCount}
            """;
    }
}
