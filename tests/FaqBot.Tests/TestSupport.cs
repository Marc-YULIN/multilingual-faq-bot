using FaqBot.Core;

namespace FaqBot.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        now += duration;
        foreach (var timer in timers.ToArray()) timer.Fire(now);
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        timers.Add(timer);
        return timer;
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private DateTimeOffset due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
        private TimeSpan interval = period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (disposed) return false;
            due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
            interval = period;
            return true;
        }
        public void Fire(DateTimeOffset current)
        {
            if (disposed || due > current) return;
            due = interval == Timeout.InfiniteTimeSpan || interval == TimeSpan.Zero ? DateTimeOffset.MaxValue : current + interval;
            callback(state);
        }
        public void Dispose() { disposed = true; owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
internal sealed class MemoryRepository : IFaqRepository, IChatLogWriter
{
    public SiteSetting? Site { get; set; } = new("demo", true, true, "en", []);
    public long Version { get; set; } = 1;
    public FaqItem[] Items { get; set; } = [TestData.Item("refund", "refund payment parcel")];
    public List<ChatLogEntry> Logs { get; } = [];
    public Task<FaqItem[]> GetAllAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Items); }
    public Task<SiteSetting?> GetSiteAsync(string code, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Site); }
    public Task<long> GetVersionAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Version); }
    public Task WriteAsync(ChatLogEntry entry, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Logs.Add(entry); return Task.CompletedTask; }
}
internal sealed class FixedIndexes(FaqEngine engine) : IFaqIndexProvider
{
    public int Calls { get; private set; }
    public Task<FaqEngine> GetAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(engine); }
}
internal sealed class StubLlm : ILlmClient
{
    public int Calls { get; private set; }
    public LlmRequest? LastRequest { get; private set; }
    public Func<CancellationToken, Task<LlmResponse>> Complete { get; set; } = _ => Task.FromResult(new LlmResponse("Grounded answer", false));
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) { Calls++; LastRequest = request; return Complete(cancellationToken); }
}
internal static class TestData
{
    public static FaqItem Item(string id, string question, bool enabled = true) => new(id, "support", enabled,
        SupportedLocales.All.ToDictionary(locale => locale, _ => new FaqTranslation(question, "A verified answer.", [], []), StringComparer.Ordinal));
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FaqBot.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }
}
