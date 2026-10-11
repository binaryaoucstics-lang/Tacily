namespace ScreenTranslator.Core.Abstractions;

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
