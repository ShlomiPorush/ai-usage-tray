using costats.Application.SessionActivation;
using costats.Infrastructure.SessionActivation;
using Xunit;

namespace costats.Core.Tests.SessionActivation;

public sealed class JsonSessionActivationStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "costats-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task State_round_trips_and_leaves_no_temporary_file()
    {
        var resetAt = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        var store = new JsonSessionActivationStateStore(_root);
        var state = new Dictionary<string, SessionActivationCheckpoint>
        {
            ["claude:work"] = new()
            {
                ObservedResetAt = resetAt,
                ObservedActiveWindow = true,
                RequiresFutureObservation = true,
                Attempts = 2,
                NextAttemptAt = resetAt.AddMinutes(10),
                Completed = false,
                Succeeded = false
            }
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IsReliable);
        var checkpoint = Assert.Single(loaded.Checkpoints).Value;
        Assert.Equal(resetAt, checkpoint.ObservedResetAt);
        Assert.True(checkpoint.ObservedActiveWindow);
        Assert.True(checkpoint.RequiresFutureObservation);
        Assert.Equal(2, checkpoint.Attempts);
        Assert.Equal(resetAt.AddMinutes(10), checkpoint.NextAttemptAt);
        Assert.False(checkpoint.Completed);
        Assert.False(File.Exists(Path.Combine(_root, "costats", "session-activation-state.json.tmp")));
    }

    [Fact]
    public async Task Corrupt_state_fails_closed_as_empty()
    {
        var directory = Path.Combine(_root, "costats");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "session-activation-state.json"), "not json");

        var loaded = await new JsonSessionActivationStateStore(_root).LoadAsync(CancellationToken.None);

        Assert.Empty(loaded.Checkpoints);
        Assert.False(loaded.IsReliable);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
