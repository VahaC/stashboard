namespace Stashboard.Tests.Infrastructure;

/// <summary>Manually-controlled <see cref="TimeProvider"/> for deterministic tests.</summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public TestTimeProvider() : this(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    public void SetUtcNow(DateTimeOffset value) => _now = value;
}
