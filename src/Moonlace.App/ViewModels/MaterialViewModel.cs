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

    [ObservableProperty]
    private bool _modified;

    public ObservableCollection<ColorRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private ColorRowViewModel? _selectedRow;

    public ObservableCollection<TextureSlotViewModel> TextureSlots { get; } = [];

    public MaterialViewModel(EditorViewModel owner, EditableMaterial material)
    {
        _owner = owner;
        GamePath = material.GamePath;
        Name = material.Name;
        ShaderPack = material.ShaderPack;
        Modified = material.Modified;
        for (var i = 0; i < material.ColorTable.Length; i++)
            Rows.Add(new ColorRowViewModel(i, material.ColorTable[i]));
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

/// <summary>One mesh group in the Model tab with its selectable material assignment.</summary>
public partial class MeshAssignmentViewModel : ViewModelBase
{
    public int MeshIndex { get; }

    public string Label { get; }

    public System.Collections.Generic.IReadOnlyList<string> MaterialNames { get; }

    [ObservableProperty]
    private int _selectedMaterialIndex;

    public MeshAssignmentViewModel(EditableMesh mesh, System.Collections.Generic.IReadOnlyList<string> materialNames)
    {
        MeshIndex = mesh.Index;
        Label = $"Mesh {mesh.Index}  ·  {mesh.TriangleCount:N0} tris";
        MaterialNames = materialNames;
        _selectedMaterialIndex = mesh.MaterialIndex;
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

    public Avalonia.Media.Color Swatch => Avalonia.Media.Color.FromRgb(
        ToChannel(DiffuseR), ToChannel(DiffuseG), ToChannel(DiffuseB));

    private static byte ToChannel(float value) =>
        (byte)System.Math.Clamp(System.MathF.Round(System.MathF.Pow(System.Math.Clamp(value, 0f, 1f), 1f / 2.2f) * 255f), 0, 255);

    public ColorRowViewModel(int index, MaterialColorRow row)
    {
        Index = index;
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
