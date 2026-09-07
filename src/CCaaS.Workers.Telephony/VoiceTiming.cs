using System.Diagnostics;
using System.Text.Json;

namespace CCaaS.Workers.Telephony;

// Correlate a turn by its recording/event ID, never by caller phone number.
internal sealed class VoiceTiming : IDisposable
{
    internal static readonly AsyncLocal<(string Call, string Turn)?> Current = new();
    private readonly ILogger _logger;
    private readonly object _call;
    private readonly object _turn;
    private readonly string _stage;
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly long _tick = Stopwatch.GetTimestamp();
    private bool _disposed;
    private string _outcome = "incomplete";
    public VoiceTiming(ILogger logger, object call, object turn, string stage)
    {
        _logger = logger; _call = call; _turn = turn; _stage = stage;
        Write("started", null);
    }
    public void Complete() => _outcome = "success";
    private void Write(string outcome, double? duration) => _logger.LogInformation("VOICE_TIMING {Timing}",
        JsonSerializer.Serialize(new { schema_version = 1, pipeline_version = "batch-v1",
            call_id = _call, turn_id = _turn, turn_no = (int?)null, stage = _stage,
            started_at = _start, ended_at = duration.HasValue ? DateTime.UtcNow : (DateTime?)null,
            duration_ms = duration, concurrency = (int?)null, outcome }));
    public void Dispose() { if (_disposed) return; _disposed = true; Write(_outcome, Stopwatch.GetElapsedTime(_tick).TotalMilliseconds); }
    public static async Task<T> Run<T>(ILogger logger, object call, object turn, string stage, Func<Task<T>> action)
    {
        using var timing = new VoiceTiming(logger, call, turn, stage);
        var prior = Current.Value; Current.Value = (call.ToString()!, turn.ToString()!);
        try { var result = await action(); timing.Complete(); return result; }
        finally { Current.Value = prior; }
    }
    public static async Task Run(ILogger logger, object call, object turn, string stage, Func<Task> action)
    {
        using var timing = new VoiceTiming(logger, call, turn, stage);
        await action(); timing.Complete();
    }
}
