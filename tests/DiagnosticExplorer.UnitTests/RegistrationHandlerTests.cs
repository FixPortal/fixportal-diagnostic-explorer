using AwesomeAssertions;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     RegistrationHandler fails fast at construction when an API key is configured
///     against a cleartext hub URL, so the shared secret is never transmitted over
///     http/ws. Deleting the guard or widening IsSecureUrl must turn these tests red.
///     (DE-4)
/// </summary>
public class RegistrationHandlerTests
{
    /// <summary>
    ///     A non-empty API key with an http:// or ws:// URL must be rejected with
    ///     ArgumentException — the key would otherwise leak in cleartext on the
    ///     negotiate and WebSocket-upgrade requests. (DE-4)
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5000/diagnostics")]
    [InlineData("ws://localhost:5000/diagnostics")]
    public void Ctor_CleartextUrlWithApiKey_ThrowsArgumentException(string url)
    {
        var act = () => new RegistrationHandler(url, new Registration(), "secret-api-key");

        act.Should().Throw<ArgumentException>().WithParameterName("url");
    }

    /// <summary>
    ///     The guard must not over-reject: TLS transports (https/wss) accept an API
    ///     key, and a cleartext URL without a key is fine since there is no secret
    ///     to leak. (DE-4)
    /// </summary>
    [Theory]
    [InlineData("https://localhost:5001/diagnostics", "secret-api-key")]
    [InlineData("wss://localhost:5001/diagnostics", "secret-api-key")]
    [InlineData("http://localhost:5000/diagnostics", null)]
    public void Ctor_SecureUrlOrNoApiKey_DoesNotThrow(string url, string? apiKey)
    {
        var act = () => new RegistrationHandler(url, new Registration(), apiKey);

        act.Should().NotThrow();
    }
}
