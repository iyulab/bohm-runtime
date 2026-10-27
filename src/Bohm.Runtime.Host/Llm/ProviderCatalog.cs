namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The providers the person may choose from: the built-in ones and the ones added (<see cref="ProviderStore"/>),
/// one per id. A built-in provider whose traffic is sent to a gateway instead
/// (<see cref="RuntimeHostOptions.LlmEndpoints"/>) is listed with the gateway's address.
/// </summary>
internal sealed class ProviderCatalog(RuntimeHostOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private IReadOnlyList<ProviderDeclaration>? _all;
    private IReadOnlyList<ProviderDeclaration> _added = [];

    /// <summary>Every provider to choose from, built-in ones first.</summary>
    public IReadOnlyList<ProviderDeclaration> All
    {
        get
        {
            lock (_lock)
            {
                if (_all is { } kept) return kept;
                _added = ProviderStore.Read(options.DataRoot);
                return _all = Merged();
            }
        }
    }

    /// <summary>The provider <paramref name="id"/>, or <see langword="null"/>.</summary>
    public ProviderDeclaration? ById(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Adds <paramref name="provider"/> (as one the person added), or replaces the one added under its id.
    /// A provider of the same id that an administrator listed stands over it.
    /// </summary>
    public async Task AddAsync(ProviderDeclaration provider, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = All;
            var added = _added.Where(p => p.Id != provider.Id).Append(provider with { Origin = ProviderOrigin.Person }).Take(ProviderStore.MaxProviders).ToList();
            await ProviderStore.WriteAsync(options.DataRoot, added, cancellationToken).ConfigureAwait(false);
            lock (_lock) (_added, _all) = (added, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the provider the person added under <paramref name="id"/>; <see langword="false"/> when there is none (a built-in provider stays).</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = All;
            var added = _added.Where(p => p.Id != id).ToList();
            if (added.Count == _added.Count) return false;
            await ProviderStore.WriteAsync(options.DataRoot, added, cancellationToken).ConfigureAwait(false);
            lock (_lock) (_added, _all) = (added, null);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<ProviderDeclaration> Merged() =>
        ProviderStore.Merge(ProviderDeclaration.BuiltIn.Select(Redirected), _added);

    /// <summary>
    /// A built-in provider at the gateway its traffic is sent to, when one is set: the gateway's address
    /// in place of the provider's own host, keeping the path the provider's API sits under (OpenAI's <c>v1/</c>).
    /// </summary>
    private ProviderDeclaration Redirected(ProviderDeclaration builtIn) =>
        options.LlmEndpoints?.GetValueOrDefault(builtIn.BaseUrl.Host) is { } gateway
            ? builtIn with { BaseUrl = new Uri(gateway, builtIn.BaseUrl.AbsolutePath.TrimStart('/')) }
            : builtIn;

    public void Dispose() => _gate.Dispose();
}
