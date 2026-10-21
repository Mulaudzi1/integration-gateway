using System.Security.Cryptography;
using System.Text;

namespace IntegrationGateway.Domain;

public static class Signatures
{
    /// <summary>Returns "sha256=&lt;hex&gt;" HMAC of the body (GitHub-style webhook signature).</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static bool Verify(string secret, string body, string? signature) =>
        signature is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, body)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
}
