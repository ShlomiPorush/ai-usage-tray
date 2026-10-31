using costats.Infrastructure.Antigravity;

namespace costats.Core.Tests.Antigravity;

/// <summary>Payloads and fakes shared by the Antigravity tests. No real agy is ever started.</summary>
internal static class AgyFixtures
{
    /// <summary>
    /// The shape agy 1.4.10 pipes to its statusline, sanitized: no email, and
    /// extra fields are dropped.
    /// </summary>
    public const string RealPayload = """
        {"cwd":"C:\\probe","session_id":"","agent_state":"idle","version":"1.4.10","plan_tier":"Google AI Plus",
         "model":{"id":"Gemini 3.8 Flash (High)"},
         "quota":{"3p-weekly":{"remaining_fraction":1,"reset_time":"2026-11-06T00:10:57Z","reset_in_seconds":604800},
                  "gemini-weekly":{"remaining_fraction":0,"reset_time":"2026-11-04T12:58:07Z","reset_in_seconds":478030}}}
        """;

    /// <summary>
    /// <see cref="RealPayload"/> with a placeholder email where agy puts the
    /// real one, for the tests that prove it never travels further.
    /// </summary>
    public static string RealPayloadWithPlaceholderEmail =>
        RealPayload.Replace("\"plan_tier\":", "\"email\":\"" + Email + "\",\"plan_tier\":", StringComparison.Ordinal);

    /// <summary>
    /// <see cref="RealPayload"/> without <c>plan_tier</c>: agy's first payloads
    /// with quota arrive before it reports the plan.
    /// </summary>
    public static string RealPayloadWithoutPlan =>
        RealPayload.Replace("\"plan_tier\":\"Google AI Plus\",", string.Empty, StringComparison.Ordinal);

    /// <summary>A payload with quota but no Gemini bucket.</summary>
    public const string ThirdPartyOnlyPayload = """
        {"agent_state":"idle","plan_tier":"Google AI Plus","email":"secret.person@example.com",
         "quota":{"3p-weekly":{"remaining_fraction":0.5,"reset_time":"2026-11-06T00:10:57Z","reset_in_seconds":300000}}}
        """;

    public const string Email = "secret.person@example.com";

    public static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static string NewBasePath(string name) =>
        Path.Combine(Path.GetTempPath(), "ai-usage-tray-agy-tests", name + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort clean-up; retries briefly while ended processes release their working folder.</summary>
    public static void DeleteQuietly(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeUptime(long start) : IUptimeClock
{
    public long Value { get; set; } = start;

    public long TickCount64 => Value;

    /// <summary>The boot counter; null simulates a machine where it cannot be read.</summary>
    public long? BootId { get; set; }

    public void Advance(TimeSpan by) => Value += (long)by.TotalMilliseconds;
}
