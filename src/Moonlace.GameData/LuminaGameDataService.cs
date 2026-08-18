using Lumina;
using Lumina.Data;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;

namespace Moonlace.GameData;

/// <summary>
/// Owns the Lumina <see cref="Lumina.GameData"/> instance. No Lumina type
/// leaks out of Moonlace.GameData; other services in this assembly reach the
/// instance through <see cref="Lumina"/>.
/// </summary>
public sealed class LuminaGameDataService : IGameDataService
{
    private readonly ILogger<LuminaGameDataService> _logger;
    private Lumina.GameData? _lumina;

    public LuminaGameDataService(ILogger<LuminaGameDataService> logger)
    {
        _logger = logger;
    }

    public bool IsInitialized => _lumina is not null;

    internal Lumina.GameData Lumina =>
        _lumina ?? throw new InvalidOperationException("Game data has not been initialized.");

    public Task InitializeAsync(string gameDirectory, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sqpack = Path.Combine(gameDirectory, "sqpack");
            _logger.LogInformation("Initializing Lumina against {SqPack}", sqpack);

            var options = new LuminaOptions
            {
                DefaultExcelLanguage = Language.English,
                PanicOnSheetChecksumMismatch = false,
            };

            var lumina = new Lumina.GameData(sqpack, options);
            _logger.LogInformation("Lumina initialized; {Count} repositories found", lumina.Repositories.Count);
            var old = Interlocked.Exchange(ref _lumina, lumina);
            old?.Dispose();
        }, ct);
    }

    public void Dispose()
    {
        _lumina?.Dispose();
        _lumina = null;
    }
}
