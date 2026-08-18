using Moonlace.Core.Models;
using Moonlace.Core.Settings;

namespace Moonlace.Core.Interfaces;

public interface ISettingsService
{
    MoonlaceSettings Load();

    void Save(MoonlaceSettings settings);
}

/// <summary>
/// Owns access to the FFXIV game data (SqPack). All FFXIV reading happens
/// behind this and the repositories/loaders that depend on it.
/// </summary>
public interface IGameDataService : IDisposable
{
    bool IsInitialized { get; }

    /// <summary>Initializes game data readers for the given game directory (the one containing "sqpack").</summary>
    Task InitializeAsync(string gameDirectory, CancellationToken ct = default);
}

public interface IItemRepository
{
    /// <summary>Loads the displayable equipment items. Requires an initialized <see cref="IGameDataService"/>.</summary>
    Task<IReadOnlyList<EquipmentItem>> GetEquipmentItemsAsync(CancellationToken ct = default);
}

/// <summary>
/// Turns a selected item into renderer-ready data: model geometry, materials
/// and decoded textures. This is the whole item→pixels pipeline up to (but
/// excluding) GPU upload.
/// </summary>
public interface IRenderModelLoader
{
    Task<RenderModel> LoadAsync(EquipmentItem item, CancellationToken ct = default);
}
