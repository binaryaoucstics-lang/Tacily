using System.IO;
using System.Security.Cryptography;
using System.Text;
using ScreenTranslator.Core.Abstractions;

namespace ScreenTranslator.App.Services.Settings;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _secretDirectory;

    public DpapiSecretStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlobalAITranslator",
            "secrets"))
    {
    }

    public DpapiSecretStore(string secretDirectory)
    {
        _secretDirectory = secretDirectory;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var clearText = ProtectedData.Unprotect(
                encrypted,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearText);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException("无法读取已保存的 API Key。", exception);
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(_secretDirectory);

        try
        {
            var clearText = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(
                clearText,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(GetSecretPath(key), encrypted, cancellationToken);
            CryptographicOperations.ZeroMemory(clearText);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException("无法安全保存 API Key。", exception);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSecretPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetSecretPath(string key)
    {
        if (key.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("密钥名称只能包含字母、数字、连字符和下划线。", nameof(key));
        }

        return Path.Combine(_secretDirectory, $"{key}.bin");
    }
}
