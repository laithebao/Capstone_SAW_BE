namespace SAW.Infrastructure.AiChat.Rag;

public sealed class QdrantOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string CollectionAlias { get; init; } = "saw-knowledge-base";

    internal Uri? EndpointUri =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;

    internal bool IsConfigured =>
        EndpointUri is not null && !string.IsNullOrWhiteSpace(ApiKey) &&
        CollectionAlias is { Length: > 0 and <= 64 } &&
        CollectionAlias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
