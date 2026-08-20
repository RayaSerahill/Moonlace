using System.Collections.Generic;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Moonlace.Core.Models;

namespace Moonlace.App.ViewModels;

/// <summary>
/// A collapsible category row in the item browser: a main category ("Gear",
/// "Accessories", "Body") or a subcategory under it ("Feet", "Rings",
/// "Faces"). Collapsed by default; searching overrides the collapse state
/// without touching it.
/// </summary>
public partial class CategoryNode : ViewModelBase
{
    public required string Label { get; init; }

    /// <summary>0 = main category, 1 = subcategory.</summary>
    public required int Level { get; init; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Subcategories (main categories only).</summary>
    public List<CategoryNode> Children { get; } = [];

    /// <summary>Item rows (subcategories only), stable instances for the list's whole lifetime.</summary>
    public List<ItemNode> Items { get; } = [];

    public int TotalItems => Items.Count + Children.Sum(c => c.TotalItems);

    public string CountLabel => TotalItems.ToString("N0");

    public Thickness Indent => new(Level * 16, 0, 0, 0);
}

/// <summary>A selectable item row under a subcategory.</summary>
public sealed class ItemNode(EquipmentItem item)
{
    public EquipmentItem Item { get; } = item;

    public string Name => Item.Name;

    public string SlotLabel => Item.Slot switch
    {
        EquipSlot.MainHand => "Weapon",
        EquipSlot.OffHand => "Off-hand",
        EquipSlot.Head => "Head",
        EquipSlot.Body => "Body",
        EquipSlot.Hands => "Hands",
        EquipSlot.Legs => "Legs",
        EquipSlot.Feet => "Feet",
        EquipSlot.Ears => "Earring",
        EquipSlot.Neck => "Necklace",
        EquipSlot.Wrists => "Bracelet",
        EquipSlot.RightRing or EquipSlot.LeftRing => "Ring",
        EquipSlot.Face => "Face",
        EquipSlot.Tail => "Tail",
        EquipSlot.HumanBody => "Body model",
        EquipSlot.Hair => "Hair",
        _ => Item.Slot.ToString(),
    };
}
