using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Parsing;

namespace Moonlace.App.ViewModels;

/// <summary>One material in the Material tab: identity plus editable color-table rows.</summary>
public partial class MaterialViewModel : ViewModelBase
{
    private readonly EditorViewModel _owner;

    public string GamePath { get; }

    public string Name { get; }

    public string ShaderPack { get; }

    public string DisplayName => System.IO.Path.GetFileName(GamePath);

    public bool HasColorTable => Rows.Count > 0;

    /// <summary>character.shpk shows a hint about fields 3/7/11, which it needs set or it renders black.</summary>
    public bool IsCharacterShader => ShaderPack == "character.shpk";

    [ObservableProperty]
    private bool _modified;

    public ObservableCollection<ColorRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private ColorRowViewModel? _selectedRow;

    public ObservableCollection<TextureSlotViewModel> TextureSlots { get; } = [];

    /// <summary>Shader packs offered in the dropdown; any other name can be typed.</summary>
    public System.Collections.Generic.IReadOnlyList<string> KnownShaders => MaterialEdits.KnownShaders;

    /// <summary>The shader pack to switch to, bound to the editable dropdown.</summary>
    [ObservableProperty]
    private string _shaderText;

    /// <summary>False when no texture is bound as diffuse; the tab then offers to add one.</summary>
    public bool HasDiffuseSlot { get; }

    public bool CanAddDiffuseSlot => !HasDiffuseSlot;

    /// <summary>Path for the diffuse texture to add, prefilled next to the normal map.</summary>
    [ObservableProperty]
    private string _newDiffusePath;

    public MaterialViewModel(EditorViewModel owner, EditableMaterial material)
    {
        _owner = owner;
        GamePath = material.GamePath;
        Name = material.Name;
        ShaderPack = material.ShaderPack;
        _shaderText = material.ShaderPack;
        HasDiffuseSlot = material.HasDiffuseSlot;
        _newDiffusePath = material.SuggestedDiffusePath;
        Modified = material.Modified;
        for (var i = 0; i < material.ColorTable.Length; i++)
            Rows.Add(new ColorRowViewModel(i, material.ColorTable[i], material.ColorTable.Length == 32));
        SelectedRow = Rows.FirstOrDefault();
        for (var i = 0; i < material.Textures.Count; i++)
            TextureSlots.Add(new TextureSlotViewModel(i, material.Textures[i]));
    }

    public MaterialColorRow[] BuildRows() => Rows.Select(r => r.ToRow()).ToArray();

    public string[] BuildTexturePaths() => TextureSlots.Select(s => s.Path.Trim()).ToArray();

    [RelayCommand]
    private Task ApplyAsync() => _owner.ApplyMaterialAsync(this);

    [RelayCommand]
    private Task ApplyTexturesAsync() => _owner.ApplyMaterialTexturesAsync(this);

    [RelayCommand]
    private Task ApplyShaderAsync() => _owner.ApplyMaterialShaderAsync(this);

    [RelayCommand]
    private Task AddDiffuseSlotAsync() => _owner.AddDiffuseSlotAsync(this);
}

/// <summary>One texture slot of a material; the path is editable and re-pointable at any game texture.</summary>
public partial class TextureSlotViewModel : ViewModelBase
{
    public int Index { get; }

    public string Role { get; }

    [ObservableProperty]
    private string _path;

    public TextureSlotViewModel(int index, EditableTexture texture)
    {
        Index = index;
        Role = texture.Role;
        _path = texture.GamePath;
    }
}

/// <summary>
/// One mesh group in the Model tab with its material assignment. The
/// dropdown offers the model's materials, but the name is free text: any
/// material path can be typed (e.g. a third-party body mod's
/// "/mt_c0201b0001_bibo.mtrl") and is written into the model as-is.
/// </summary>
public partial class MeshAssignmentViewModel : ViewModelBase
{
    public int MeshIndex { get; }

    public string Label { get; }

    public System.Collections.Generic.IReadOnlyList<string> MaterialNames { get; }

    /// <summary>The material this mesh will use; bound to the editable dropdown's text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMaterialIndex), nameof(IsCustomMaterial))]
    private string _materialName;

    /// <summary>Slot of <see cref="MaterialName"/> in the model's list, or -1 for a typed name the model does not have.</summary>
    public int SelectedMaterialIndex
    {
        get => IndexOf(MaterialName);
        set
        {
            if (value >= 0 && value < MaterialNames.Count)
                MaterialName = MaterialNames[value];
        }
    }

    /// <summary>True when the typed name is not one of the model's materials (shown as a hint).</summary>
    public bool IsCustomMaterial => !string.IsNullOrWhiteSpace(MaterialName) && IndexOf(MaterialName) < 0;

    public MeshAssignmentViewModel(EditableMesh mesh, System.Collections.Generic.IReadOnlyList<string> materialNames)
    {
        MeshIndex = mesh.Index;
        Label = $"Mesh {mesh.Index}  ·  {mesh.TriangleCount:N0} tris";
        MaterialNames = materialNames;
        _materialName = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < materialNames.Count
            ? materialNames[mesh.MaterialIndex]
            : "";
    }

    private int IndexOf(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        for (var i = 0; i < MaterialNames.Count; i++)
        {
            if (string.Equals(MaterialNames[i], trimmed, System.StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}

/// <summary>One editable color-table row. Values are floats (colors can exceed 1.0 in FFXIV tables).</summary>
public partial class ColorRowViewModel : ViewModelBase
{
    public int Index { get; }

    public string Label => $"Row {Index}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch))]
    private float _diffuseR;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch))]
    private float _diffuseG;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch))]
    private float _diffuseB;

    [ObservableProperty]
    private float _specularR;

    [ObservableProperty]
    private float _specularG;

    [ObservableProperty]
    private float _specularB;

    [ObservableProperty]
    private float _emissiveR;

    [ObservableProperty]
    private float _emissiveG;

    [ObservableProperty]
    private float _emissiveB;

    [ObservableProperty]
    private float _gloss;

    [ObservableProperty]
    private float _specularStrength;

    /// <summary>Field 11 (Dawntrail tables only); character.shpk wants 1 here.</summary>
    [ObservableProperty]
    private float _emissiveExtra;

    /// <summary>True for 32-row Dawntrail tables, which have field 11.</summary>
    public bool IsDawntrailRow { get; }

    public Avalonia.Media.Color Swatch => Avalonia.Media.Color.FromRgb(
        ToChannel(DiffuseR), ToChannel(DiffuseG), ToChannel(DiffuseB));

    private static byte ToChannel(float value) =>
        (byte)System.Math.Clamp(System.MathF.Round(System.MathF.Pow(System.Math.Clamp(value, 0f, 1f), 1f / 2.2f) * 255f), 0, 255);

    public ColorRowViewModel(int index, MaterialColorRow row, bool isDawntrailRow)
    {
        Index = index;
        IsDawntrailRow = isDawntrailRow;
        _emissiveExtra = row.EmissiveExtra;
        _diffuseR = row.Diffuse.X;
        _diffuseG = row.Diffuse.Y;
        _diffuseB = row.Diffuse.Z;
        _specularR = row.Specular.X;
        _specularG = row.Specular.Y;
        _specularB = row.Specular.Z;
        _emissiveR = row.Emissive.X;
        _emissiveG = row.Emissive.Y;
        _emissiveB = row.Emissive.Z;
        _gloss = row.Gloss;
        _specularStrength = row.SpecularStrength;
    }

    public MaterialColorRow ToRow() => new()
    {
        Diffuse = new Vector3(DiffuseR, DiffuseG, DiffuseB),
        Specular = new Vector3(SpecularR, SpecularG, SpecularB),
        Emissive = new Vector3(EmissiveR, EmissiveG, EmissiveB),
        Gloss = Gloss,
        SpecularStrength = SpecularStrength,
        EmissiveExtra = EmissiveExtra,
    };
}

/// <summary>One texture in the Texture tab.</summary>
public partial class TextureViewModel : ViewModelBase
{
    public string GamePath { get; }

    public string FileName => System.IO.Path.GetFileName(GamePath);

    public string Role { get; }

    public string Dimensions { get; }

    [ObservableProperty]
    private bool _modified;

    public TextureViewModel(EditableTexture texture)
    {
        GamePath = texture.GamePath;
        Role = texture.Role;
        Dimensions = texture.Width > 0 ? $"{texture.Width}×{texture.Height}" : "?";
        Modified = texture.Modified;
    }
}
