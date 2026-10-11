using System.Collections.Immutable;
using System.Text.Json;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Settings;

public sealed class ApiNetworkTests
{
    [Fact]
    public void OldSettingsKeepSystemProxyAndMigrationPreservesIndependentRoutes()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"Version\":13}")!.Migrate(out _);
        Assert.Equal(ApiNetworkMode.SystemProxy, old.ApiNetwork.Mode);
        var a = new TranslationProviderPreset { Network = new() { Mode = ApiNetworkMode.Direct } };
        var b = new TranslationProviderPreset { Network = new() { Mode = ApiNetworkMode.ClashNode, ClashNode = "  Node B  " } };
        var original = new AppSettings { ApiNetwork = new() { Mode = ApiNetworkMode.Clash },
            TranslationProviderPresets = [a, b], SelectedTranslationPresetId = b.Id };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!.Migrate(out _);
        Assert.Equal(ApiNetworkMode.Clash, restored.ApiNetwork.Mode);
        Assert.Equal(ApiNetworkMode.Direct, restored.TranslationProviderPresets[0].Network.Mode);
        Assert.Equal("  Node B  ", TranslationProviderConfiguration.Network(restored).ClashNode);
    }

    [Fact]
    public void RouteChangeReconfiguresConnectionsWithoutInvalidatingTranslations()
    {
        var original = new AppSettings();
        var changed = original with { ApiNetwork = new() { Mode = ApiNetworkMode.ClashNode, ClashNode = "private-node" } };
        Assert.Equal(TranslationProviderConfiguration.TransportIdentity(original), TranslationProviderConfiguration.TransportIdentity(changed));
        Assert.NotEqual(TranslationProviderConfiguration.ConnectionIdentity(original), TranslationProviderConfiguration.ConnectionIdentity(changed));
    }

    [Theory]
    [InlineData("http://127.0.0.1:7897", true)]
    [InlineData("socks5://localhost:1080", true)]
    [InlineData("https://proxy.example:443", true)]
    [InlineData("file:///C:/private", false)]
    [InlineData("http://user:secret@127.0.0.1:7897", false)]
    [InlineData("http://127.0.0.1:7897?key=secret", false)]
    [InlineData("http://127.0.0.1:7897/path", false)]
    public void ProxyUrlValidationRejectsCredentialsAndNonProxySchemes(string url, bool valid)
    {
        var network = new ApiNetworkOptions { Mode = ApiNetworkMode.CustomProxy, ProxyUrl = url };
        if (valid) network.Validate(); else Assert.Throws<TranslationConfigurationException>(network.Validate);
    }

    [Fact]
    public void MissingNodeCannotSendAndMetadataDoesNotPrintPrivateRouting()
    {
        Assert.Throws<TranslationConfigurationException>(() => new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode }.Validate());
        var network = new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode, ClashNode = "private-node" };
        Assert.DoesNotContain("private-node", network.ToString());
    }
}
