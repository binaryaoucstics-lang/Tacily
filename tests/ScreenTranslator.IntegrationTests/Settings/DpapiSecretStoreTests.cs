using System.Text;
using ScreenTranslator.App.Services.Settings;

namespace ScreenTranslator.IntegrationTests.Settings;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ScreenTranslator.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Secret_Is_Encrypted_At_Rest_And_Can_Be_Deleted()
    {
        const string secret = "deepseek-test-secret";
        var store = new DpapiSecretStore(_directory);

        await store.SetAsync("deepseek-api-key", secret);

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(_directory, "deepseek-api-key.bin"));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
        Assert.Equal(secret, await store.GetAsync("deepseek-api-key"));

        await store.DeleteAsync("deepseek-api-key");
        Assert.Null(await store.GetAsync("deepseek-api-key"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
