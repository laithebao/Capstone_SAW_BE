using Xunit.Abstractions;
using Xunit.Sdk;

namespace SAW.Test;

// Tag the existing opt-in attributes so profiles select tests rather than silently skipping them.
public sealed class SqlTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo attribute) =>
        [new("RequiresSql", "true")];
}

public sealed class BrowserTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo attribute) =>
        [new("RequiresSql", "true"), new("RequiresBrowser", "true")];
}

public sealed class ExternalTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo attribute) =>
        [new("RequiresSql", "true"), new("RequiresCloudinary", "true")];
}
