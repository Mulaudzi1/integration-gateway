namespace IntegrationGateway.Domain;

public static class RetryPolicy
{
    /// <summary>Exponential backoff: base * 2^(attempt-1), capped. Attempt is 1-based.</summary>
    public static TimeSpan Delay(int attempt, int baseDelaySeconds, int maxDelaySeconds)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var seconds = baseDelaySeconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, maxDelaySeconds));
    }
}
