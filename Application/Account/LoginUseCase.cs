using System.Security.Cryptography;
using System.Text;

namespace ICOGenerator.Application.Account;

/// <summary>
/// Validates the single shared app login against <c>Auth:Username</c> / <c>Auth:Password</c>.
/// Username defaults to "admin" when unset; the password is a required secret — set via
/// <c>Auth__Password</c> env var or user-secrets, never commit it (same stance as the ApiKey
/// encryption key). Returns true only on an exact, fixed-time match.
/// </summary>
public class LoginUseCase
{
    private const string DefaultUsername = "admin";

    private readonly IConfiguration _configuration;
    private readonly ILogger<LoginUseCase> _logger;

    public LoginUseCase(IConfiguration configuration, ILogger<LoginUseCase> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool Execute(string? username, string? password)
    {
        var expectedUser = _configuration["Auth:Username"];
        if (string.IsNullOrWhiteSpace(expectedUser))
            expectedUser = DefaultUsername;

        var expectedPassword = _configuration["Auth:Password"];

        // No password configured => no valid credentials exist; reject every attempt rather
        // than letting a blank value act as a wildcard that would leave the app wide open.
        if (string.IsNullOrEmpty(expectedPassword))
        {
            _logger.LogWarning(
                "Auth:Password is not configured; all logins are rejected. Set it via the " +
                "environment variable Auth__Password or user-secrets.");
            return false;
        }

        // Compare SHA-256 digests with a fixed-time equality so neither the username nor the
        // password can be inferred from response timing or input length.
        var userOk = FixedTimeEquals(username, expectedUser);
        var passOk = FixedTimeEquals(password, expectedPassword);
        return userOk && passOk;
    }

    private static bool FixedTimeEquals(string? actual, string? expected)
    {
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual ?? string.Empty));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
