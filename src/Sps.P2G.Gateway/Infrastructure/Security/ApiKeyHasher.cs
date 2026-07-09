using System.Security.Cryptography;
using System.Text;

namespace Sps.P2G.Gateway.Infrastructure.Security;

public interface IApiKeyHasher
{
    string GenerateApiKey();
    string Hash(string apiKey);
    bool Verify(string apiKey, string hash);
}

public sealed class Sha256ApiKeyHasher : IApiKeyHasher
{
    public string GenerateApiKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "p2g_" + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }

    public bool Verify(string apiKey, string hash)
    {
        var candidate = Hash(apiKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(hash));
    }
}
