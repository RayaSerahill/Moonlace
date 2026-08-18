using Moonlace.App.ViewModels;
using Moonlace.Core.Penumbra;

namespace Moonlace.Core.Tests;

/// <summary>The option-picker view models turn UI state back into option index selections.</summary>
public sealed class PenumbraViewModelTests
{
    private static PenumbraGroup MakeGroup(PenumbraGroupType type, params string[] options) => new()
    {
        Name = "Group",
        Type = type,
        Priority = 0,
        DefaultSettings = 0,
        Options = options
            .Select(name => new PenumbraOption
            {
                Name = name,
                Priority = 0,
                Files = new Dictionary<string, string>(),
            })
            .ToArray(),
    };

    [Fact]
    public void SingleGroupBuildsTheDropdownChoice()
    {
        var vm = new PenumbraGroupViewModel(MakeGroup(PenumbraGroupType.Single, "A", "B", "C"), [1]);

        Assert.True(vm.IsSingle);
        Assert.Equal("B", vm.SelectedOption!.Name);
        Assert.Equal([1], vm.BuildSelection());

        vm.SelectedOption = vm.Options[2];
        Assert.Equal([2], vm.BuildSelection());
    }

    [Fact]
    public void SingleGroupDefaultsToTheFirstOptionWhenNothingIsSelected()
    {
        var vm = new PenumbraGroupViewModel(MakeGroup(PenumbraGroupType.Single, "A", "B"), []);
        Assert.Equal([0], vm.BuildSelection());
    }

    [Fact]
    public void MultiGroupBuildsTheCheckedSet()
    {
        var vm = new PenumbraGroupViewModel(MakeGroup(PenumbraGroupType.Multi, "A", "B", "C"), [0, 2]);

        Assert.True(vm.IsMulti);
        Assert.Equal([0, 2], vm.BuildSelection());

        vm.Options[2].IsSelected = false;
        vm.Options[1].IsSelected = true;
        Assert.Equal([0, 1], vm.BuildSelection());
    }
}
