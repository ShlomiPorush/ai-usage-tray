using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Hands the usage report its current pricing table: the bundled snapshot,
/// the live catalog and the user's <c>pricing.json</c>, layered by
/// <see cref="ModelPricingLoader"/>.
/// </summary>
/// <remarks>
/// The table is rebuilt only when the cached catalog or the override file
/// changes on disk, so editing <c>pricing.json</c> takes effect on the next
/// report without a restart.
/// </remarks>
public sealed class ModelPricingProvider
{
    /// <summary>
    /// Longest a report waits for a due catalog download. A slower download
    /// finishes in the background and prices the next report.
    /// </summary>
    public static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(8);

    private readonly PricingCatalogUpdater? _updater;
    private readonly string _overridePath;
    private readonly object _gate = new();

    private ModelPricingTable? _table;
    private (DateTime Catalog, DateTime Override) _stamp;

    /// <summary>Creates a provider. A null <paramref name="updater"/> disables the live catalog.</summary>
    public ModelPricingProvider(PricingCatalogUpdater? updater, string? overridePath = null)
    {
        _updater = updater;
        _overridePath = overridePath ?? ModelPricingLoader.DefaultOverridePath();
    }

    /// <summary>
    /// Returns the current table, first giving a due catalog download up to
    /// <see cref="RefreshWait"/> to finish.
    /// </summary>
    public async Task<ModelPricingTable> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_updater is not null)
        {
            try
            {
                await _updater.RefreshIfStaleAsync().WaitAsync(RefreshWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Keep the current prices; the download carries on.
            }
        }

        var stamp = (
            _updater is null ? default : File.GetLastWriteTimeUtc(_updater.CachePath),
            File.GetLastWriteTimeUtc(_overridePath));

        lock (_gate)
        {
            if (_table is null || stamp != _stamp)
            {
                _table = ModelPricingLoader.Load(_overridePath, _updater?.ReadCached());
                _stamp = stamp;
            }

            return _table;
        }
    }
}
