using Bohm.Runtime.Credentials;

namespace Bohm.Runtime.Tests.Credentials;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public void A_secret_round_trips_through_the_windows_credential_manager_and_can_be_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows Credential Manager exists only on Windows.");
            return;
        }

        // A unique prefix keeps the test away from any real entry.
        var vault = new WindowsCredentialVault("BohmTest-" + Guid.NewGuid().ToString("n"));
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
}
