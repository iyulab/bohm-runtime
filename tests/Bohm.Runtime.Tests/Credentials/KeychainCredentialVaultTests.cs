using Bohm.Runtime.Host;
using Bohm.Runtime.Host.Credentials;

namespace Bohm.Runtime.Tests.Credentials;

// The macOS login keychain. Each test uses a service of its own, made and read by this test process, so the keychain
// asks nobody anything and no entry of the person's is touched.
public sealed class KeychainCredentialVaultTests
{
    [Fact]
    public void A_secret_round_trips_through_the_macos_keychain_and_can_be_removed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The macOS keychain exists only on macOS.");
            return;
        }

        var vault = new KeychainCredentialVault("BohmTest-" + Guid.NewGuid().ToString("n"));
        try
        {
            Assert.Null(vault.Read("llm/openai"));

            vault.Write("llm/openai", "sk-테스트-🔑");
            Assert.Equal("sk-테스트-🔑", vault.Read("llm/openai"));

            vault.Write("llm/openai", "replaced");
            Assert.Equal("replaced", vault.Read("llm/openai"));
        }
        finally
        {
            vault.Delete("llm/openai");
        }

        Assert.Null(vault.Read("llm/openai"));
        vault.Delete("llm/openai"); // Deleting what is absent is not an error.
    }

    [Fact]
    public void A_signed_in_session_round_trips_whole_and_two_vault_services_stay_apart()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The macOS keychain exists only on macOS.");
            return;
        }

        static string Jwt(int claims) => string.Join('.', Convert.ToBase64String(new byte[64]), Convert.ToBase64String(new byte[claims]), Convert.ToBase64String(new byte[256]));
        var session = System.Text.Json.JsonSerializer.Serialize(new { id_token = Jwt(900), access_token = Jwt(700), refresh_token = Guid.NewGuid().ToString("n"), expires_at = 1792412751 });

        var mine = new KeychainCredentialVault("BohmTest-" + Guid.NewGuid().ToString("n"));
        var other = new KeychainCredentialVault("BohmTest-" + Guid.NewGuid().ToString("n"));
        try
        {
            mine.Write("cloud/session", session);
            Assert.Equal(session, mine.Read("cloud/session"));
            Assert.Null(other.Read("cloud/session")); // a verification run's prefix is a space of its own
        }
        finally
        {
            mine.Delete("cloud/session");
        }

        Assert.Null(mine.Read("cloud/session"));
    }

    [Theory]
    [InlineData(true, "X5ZLX4XYCL", "/Applications/Bohm.app/Contents/Resources/runtime/Bohm.Runtime.Host", true)]
    [InlineData(false, "X5ZLX4XYCL", "/Applications/Bohm.app/Contents/Resources/runtime/Bohm.Runtime.Host", false)] // a development build
    [InlineData(true, null, "/Applications/Bohm.app/Contents/Resources/runtime/Bohm.Runtime.Host", false)] // signed ad hoc, or not at all
    [InlineData(true, "UBF8T346G9", "/usr/local/share/dotnet/dotnet", false)] // started as `dotnet Bohm.Runtime.Host.dll`: the dotnet host's identity
    [InlineData(true, "X5ZLX4XYCL", null, true)]
    public void Keys_go_to_the_keychain_only_from_a_release_build_signed_by_a_team_and_not_run_by_the_dotnet_host(bool release, string? team, string? processPath, bool expected) =>
        Assert.Equal(expected, RuntimeHost.KeepsKeysInKeychain(release, team, processPath));
}

