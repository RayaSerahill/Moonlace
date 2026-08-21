using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Export;

namespace Moonlace.App.ViewModels;

/// <summary>
/// The editing tabs under the viewport: Model (GLTF round-trip), Material
/// (color-table editing), Texture (preview/import/export), plus the session
/// status strip (dirty state, discard, PMP export). All edits flow through
/// <see cref="ItemEditingService"/> into the session never into the game.
/// </summary>
public partial class EditorViewModel : ViewModelBase
{
    private readonly ItemEditingService _editing;
    private readonly ISessionService _session;
    private readonly Moonlace.Core.Penumbra.IPenumbraLinkService _link;
    private readonly TextureDecoder _textures;
    private readonly GameData.Resolution.AssetPathResolver _resolver;
    private readonly IFilePickerService _files;
    private readonly ILogger<EditorViewModel> _logger;

    private EquipmentItem? _item;
    private bool _settingVersions;
    private int _refreshSequence;

    [ObservableProperty]
    private bool _hasItem;

    [ObservableProperty]
    private string _modelPath = "";

    [ObservableProperty]
    private bool _modelModified;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _sessionDirty;

    [ObservableProperty]
    private string _sessionSummary = "";

    [ObservableProperty]
    private bool _isConfirmingDiscard;

    /// <summary>True while a Penumbra live-edit link is active: edits land in the mod folder, session commands hide.</summary>
    [ObservableProperty]
    private bool _isLiveLinked;

    // --- Model version (race variant) selector ---

    public ObservableCollection<GameData.Resolution.RaceVariant> ModelVersions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewVersionHint))]
    private GameData.Resolution.RaceVariant? _selectedVersion;

    [ObservableProperty]
    private bool _hasVersions;

    [ObservableProperty]
    private bool _hasMultipleVersions;

    // --- New model version (race/gender combinations without one yet) ---

    public ObservableCollection<GameData.Resolution.RaceVariant> CreatableVersions { get; } = [];

    [ObservableProperty]
    private GameData.Resolution.RaceVariant? _selectedNewVersion;

    [ObservableProperty]
    private bool _hasCreatableVersions;

    [ObservableProperty]
    private bool _isNewVersionPanelOpen;

    /// <summary>Explains what creating a version will copy based on the selected source version.</summary>
    public string NewVersionHint => SelectedVersion is { } source
        ? $"Copies the {source.Label} version the new version gets its own model, materials and textures, editable without touching the other versions."
        : "";

    private bool _autoCreateVersionConsumed;

    // --- Model tab: mesh → material assignments ---

    public ObservableCollection<MeshAssignmentViewModel> MeshAssignments { get; } = [];

    [ObservableProperty]
    private bool _hasMultipleMaterials;

    // --- Material tab ---

    public ObservableCollection<MaterialViewModel> Materials { get; } = [];

    [ObservableProperty]
    private MaterialViewModel? _selectedMaterial;

    // --- Texture tab ---

    public ObservableCollection<TextureViewModel> Textures { get; } = [];

    [ObservableProperty]
    private TextureViewModel? _selectedTexture;

    [ObservableProperty]
    private Bitmap? _texturePreview;

    // --- PMP export ---

    [ObservableProperty]
    private bool _isPmpPanelOpen;

    [ObservableProperty]
    private string _pmpName = "Moonlace Export";

    [ObservableProperty]
    private string _pmpAuthor = "";

    [ObservableProperty]
    private string _pmpVersion = "1.0.0";

    [ObservableProperty]
    private string _pmpDescription = "";

    /// <summary>Raised when a session change requires the viewport model to reload.</summary>
    public event Action? SessionAssetsChanged;

    public EditorViewModel(
        ItemEditingService editing,
        ISessionService session,
        Moonlace.Core.Penumbra.IPenumbraLinkService link,
        TextureDecoder textures,
        GameData.Resolution.AssetPathResolver resolver,
        IFilePickerService files,
        ILogger<EditorViewModel> logger)
    {
        _editing = editing;
        _session = session;
        _link = link;
        _textures = textures;
        _resolver = resolver;
        _files = files;
        _logger = logger;

        _session.SessionChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSessionState);
        _link.LinkChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSessionState);
    }

    public async Task SetItemAsync(EquipmentItem? item)
    {
        await PrepareItemAsync(item);
        await RefreshTabsAsync();
    }

    /// <summary>
    /// Fast first phase of item selection: activates the session and resolves
    /// the model-version selector (setting the resolver's preferred race
    /// code). Callers must await this BEFORE loading the viewport model so
    /// the load resolves the selected version, not a stale or default one.
    /// </summary>
    public async Task PrepareItemAsync(EquipmentItem? item)
    {
        _item = item;
        _session.ActivateForItem(item);
        HasItem = item is not null;
        ErrorText = null;
        IsConfirmingDiscard = false;
        IsNewVersionPanelOpen = false;
        if (item is not null)
            PmpName = $"{item.Name} Edit";

        // Populate the model-version selector; default to the first variant
        // that exists (the resolver's own fallback choice). Dev/testing hook:
        // MOONLACE_AUTOVERSION pre-selects a version without UI automation.
        await LoadVersionsAsync(Environment.GetEnvironmentVariable("MOONLACE_AUTOVERSION"));

        // Dev/testing hook: create a model version right after the first item
        // selection settles (runs once; composes with MOONLACE_AUTOSELECT).
        if (!_autoCreateVersionConsumed && item is not null
            && Environment.GetEnvironmentVariable("MOONLACE_AUTOCREATEVERSION") is { Length: > 0 } autoCreate)
        {
            _autoCreateVersionConsumed = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AutoCreateVersionAsync(autoCreate));
        }
    }

    /// <summary>
    /// (Re)loads the model-version selector and the creatable-version list,
    /// keeping <paramref name="preferCode"/> selected when it still exists.
    /// The resolver's preferred race code is only reassigned at the end, so a
    /// concurrent viewport load never sees a transient null.
    /// </summary>
    private async Task LoadVersionsAsync(string? preferCode)
    {
        var item = _item;
        _settingVersions = true;
        try
        {
            ModelVersions.Clear();
            CreatableVersions.Clear();
            SelectedVersion = null;
            SelectedNewVersion = null;
            if (item is not null)
            {
                var (variants, missing) = await Task.Run(() =>
                    (_resolver.GetAvailableVariants(item), _resolver.GetMissingVariants(item)));
                foreach (var variant in variants)
                    ModelVersions.Add(variant);
                foreach (var variant in missing)
                    CreatableVersions.Add(variant);

                SelectedVersion = ModelVersions.FirstOrDefault(v => v.Code == preferCode)
                    ?? ModelVersions.FirstOrDefault();
                SelectedNewVersion = CreatableVersions.FirstOrDefault();
            }

            _resolver.PreferredRaceCode = SelectedVersion?.Code;
            HasVersions = ModelVersions.Count > 0;
            HasMultipleVersions = ModelVersions.Count > 1;
            HasCreatableVersions = CreatableVersions.Count > 0;
        }
        finally
        {
            _settingVersions = false;
        }
    }

    /// <summary>
    /// Second phase: (re)loads the tab contents for the current item and
    /// version. Also re-enumerates the model versions effective assets may
    /// have changed wholesale (link, revert, import) since the last load.
    /// </summary>
    public async Task RefreshTabsAsync()
    {
        await LoadVersionsAsync(SelectedVersion?.Code);
        await RefreshAsync();
    }

    partial void OnSelectedVersionChanged(GameData.Resolution.RaceVariant? value)
    {
        if (_settingVersions || value is null)
            return;

        _resolver.PreferredRaceCode = value.Code;
        _logger.LogInformation("Model version switched to c{Race} ({Label})", value.Code, value.Label);
        _ = SwitchVersionAsync();
    }

    private async Task SwitchVersionAsync()
    {
        await RefreshAsync();
        NotifyAssetsChanged();
    }

    // --- New model version ---

    [RelayCommand]
    private void OpenNewVersionPanel() => IsNewVersionPanelOpen = HasCreatableVersions;

    [RelayCommand]
    private void CloseNewVersionPanel() => IsNewVersionPanelOpen = false;

    /// <summary>
    /// Creates a model version for the chosen race/gender by copying the
    /// currently selected version, then selects the new version for editing.
    /// </summary>
    [RelayCommand]
    private async Task CreateVersionAsync()
    {
        if (_item is null || SelectedVersion is not { } source || SelectedNewVersion is not { } target)
            return;

        IsNewVersionPanelOpen = false;
        await RunOperationAsync($"Creating {target.Label} version…", async () =>
        {
            var files = await _editing.CreateModelVersionAsync(_item, source.Code, target.Code);
            _logger.LogInformation("Created model version c{Code} from c{Source} ({Files} files)",
                target.Code, source.Code, files);
            await LoadVersionsAsync(target.Code);
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    private async Task AutoCreateVersionAsync(string raceCode)
    {
        SelectedNewVersion = CreatableVersions.FirstOrDefault(v => v.Code == raceCode);
        if (SelectedNewVersion is null)
        {
            _logger.LogWarning("MOONLACE_AUTOCREATEVERSION: c{Code} is not creatable for this item", raceCode);
            return;
        }

        await CreateVersionAsync();
    }

    private async Task RefreshAsync()
    {
        // Refreshes can overlap (item selection + a live-link change firing
        // together); only the newest run may populate, or the lists get
        // double entries.
        var sequence = ++_refreshSequence;
        UpdateSessionState();
        var previousMaterial = SelectedMaterial?.GamePath;
        var previousTexture = SelectedTexture?.GamePath;
        Materials.Clear();
        Textures.Clear();
        MeshAssignments.Clear();
        SelectedMaterial = null;
        SelectedTexture = null;
        TexturePreview = null;
        ModelPath = "";
        ModelModified = false;
        HasMultipleMaterials = false;

        if (_item is null)
            return;

        try
        {
            var info = await _editing.GetItemInfoAsync(_item);
            if (sequence != _refreshSequence)
                return;

            ModelPath = info.ModelPath;
            ModelModified = info.ModelModified;

            HasMultipleMaterials = info.MaterialNames.Count > 1;
            foreach (var mesh in info.Meshes)
                MeshAssignments.Add(new MeshAssignmentViewModel(mesh, info.MaterialNames));

            foreach (var material in info.Materials)
                Materials.Add(new MaterialViewModel(this, material));
            foreach (var texture in info.Materials.SelectMany(m => m.Textures)
                         .DistinctBy(t => t.GamePath))
                Textures.Add(new TextureViewModel(texture));

            SelectedMaterial = Materials.FirstOrDefault(m => m.GamePath == previousMaterial) ?? Materials.FirstOrDefault();
            SelectedTexture = Textures.FirstOrDefault(t => t.GamePath == previousTexture) ?? Textures.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load editing info for item {Id}", _item.RowId);
            ErrorText = $"Could not inspect this item: {ex.Message}";
        }
    }

    private void UpdateSessionState()
    {
        IsLiveLinked = _link.IsLinked;
        if (_link.IsLinked)
        {
            // Session commands (discard, PMP export) do not apply while edits
            // go straight into the mod folder.
            SessionDirty = false;
            var changed = _link.ChangedFileCount;
            var summary = changed == 0
                ? $"Live editing “{_link.ModName}”"
                : $"Live editing “{_link.ModName}” {changed} file{(changed == 1 ? "" : "s")} changed";
            if (_link.EditTarget is { } target)
                summary += $" · edits → “{target.Option}”";
            SessionSummary = summary;
            return;
        }

        SessionDirty = _session.IsDirty;
        var entries = _session.Entries;
        SessionSummary = entries.Count == 0
            ? "No changes"
            : $"{entries.Count} modified asset{(entries.Count == 1 ? "" : "s")}";
    }

    partial void OnSelectedTextureChanged(TextureViewModel? value)
    {
        _ = LoadTexturePreviewAsync(value);
    }

    private async Task LoadTexturePreviewAsync(TextureViewModel? texture)
    {
        TexturePreview = null;
        if (texture is null)
            return;

        try
        {
            var decoded = await Task.Run(() => _textures.Decode(texture.GamePath));
            if (decoded is null || SelectedTexture != texture)
                return;
            TexturePreview = CreateBitmap(decoded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Texture preview failed for {Path}", texture.GamePath);
        }
    }

    private static unsafe Bitmap CreateBitmap(RenderTexture texture)
    {
        fixed (byte* pixels = texture.Rgba)
        {
            return new Bitmap(
                Avalonia.Platform.PixelFormat.Rgba8888,
                Avalonia.Platform.AlphaFormat.Unpremul,
                (nint)pixels,
                new Avalonia.PixelSize(texture.Width, texture.Height),
                new Avalonia.Vector(96, 96),
                texture.Width * 4);
        }
    }

    private async Task RunOperationAsync(string busyText, Func<Task> operation, string? successStatus = null)
    {
        IsBusy = true;
        BusyText = busyText;
        ErrorText = null;
        try
        {
            await operation();
            if (successStatus is not null)
                BusyText = successStatus;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Editing operation failed: {Operation}", busyText);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private void NotifyAssetsChanged() => SessionAssetsChanged?.Invoke();

    // --- Model tab commands ---

    [RelayCommand]
    private async Task ExportGltfAsync()
    {
        if (_item is null)
            return;
        var path = await _files.SaveFileAsync(
            "Export model as GLTF", SafeName(_item.Name) + ".glb", "Binary GLTF", ["*.glb"]);
        if (path is null)
            return;

        await RunOperationAsync("Exporting GLTF…", () => _editing.ExportModelAsync(_item, path));
    }

    [RelayCommand]
    private async Task ExportFbxAsync()
    {
        if (_item is null)
            return;
        var path = await _files.SaveFileAsync(
            "Export model as FBX", SafeName(_item.Name) + ".fbx", "FBX", ["*.fbx"]);
        if (path is null)
            return;

        await RunOperationAsync("Exporting FBX…", () => _editing.ExportModelAsync(_item, path));
    }

    [RelayCommand]
    private async Task ImportModelAsync()
    {
        if (_item is null)
            return;
        var path = await _files.OpenFileAsync("Import model", "Model files", ["*.glb", "*.gltf", "*.fbx"]);
        if (path is null)
            return;

        await RunOperationAsync("Importing model…", async () =>
        {
            await _editing.ImportModelAsync(_item, path);
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    [RelayCommand]
    private async Task ApplyMeshAssignmentsAsync()
    {
        if (_item is null)
            return;
        await RunOperationAsync("Reassigning materials…", async () =>
        {
            await _editing.SetMeshMaterialsAsync(_item, MeshAssignments.Select(m => m.SelectedMaterialIndex).ToArray());
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    // --- Material tab ---

    internal async Task ApplyMaterialAsync(MaterialViewModel material)
    {
        await RunOperationAsync("Applying material…", async () =>
        {
            await _editing.SetMaterialColorTableAsync(material.GamePath, material.BuildRows());
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    internal async Task ApplyMaterialTexturesAsync(MaterialViewModel material)
    {
        await RunOperationAsync("Reassigning textures…", async () =>
        {
            await _editing.SetMaterialTexturesAsync(material.GamePath, material.BuildTexturePaths());
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    // --- Texture tab commands ---

    [RelayCommand]
    private async Task ExportTextureAsync()
    {
        if (SelectedTexture is not { } texture)
            return;
        var path = await _files.SaveFileAsync(
            "Export texture as PNG", Path.GetFileNameWithoutExtension(texture.FileName) + ".png", "PNG image", ["*.png"]);
        if (path is null)
            return;

        await RunOperationAsync("Exporting texture…", () => _editing.ExportTexturePngAsync(texture.GamePath, path));
    }

    [RelayCommand]
    private async Task ImportTextureAsync()
    {
        if (_item is null || SelectedTexture is not { } texture)
            return;
        var path = await _files.OpenFileAsync("Import texture image", "Images", ["*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp"]);
        if (path is null)
            return;

        await RunOperationAsync("Importing texture…", async () =>
        {
            await _editing.ImportTextureAsync(texture.GamePath, path);
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    // --- Session commands ---

    [RelayCommand]
    private void RequestDiscard() => IsConfirmingDiscard = true;

    [RelayCommand]
    private void CancelDiscard() => IsConfirmingDiscard = false;

    [RelayCommand]
    private async Task ConfirmDiscardAsync()
    {
        IsConfirmingDiscard = false;
        await RunOperationAsync("Discarding changes…", async () =>
        {
            _session.DiscardActiveSession();
            // Created model versions vanish with the session re-enumerate.
            await LoadVersionsAsync(SelectedVersion?.Code);
            await RefreshAsync();
            NotifyAssetsChanged();
        });
    }

    [RelayCommand]
    private void OpenPmpPanel() => IsPmpPanelOpen = true;

    [RelayCommand]
    private void ClosePmpPanel() => IsPmpPanelOpen = false;

    [RelayCommand]
    private async Task ExportPmpAsync()
    {
        var path = await _files.SaveFileAsync(
            "Export session as PMP", SafeName(PmpName) + ".pmp", "Penumbra Mod Package", ["*.pmp"]);
        if (path is null)
            return;

        await RunOperationAsync("Exporting PMP…", async () =>
        {
            await _editing.ExportPmpAsync(new PmpMetadata
            {
                Name = PmpName,
                Author = PmpAuthor,
                Version = PmpVersion,
                Description = PmpDescription,
            }, path);
            IsPmpPanelOpen = false;
        });
    }

    private static string SafeName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "moonlace-export" : safe;
    }
}
