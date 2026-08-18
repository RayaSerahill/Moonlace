using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Moonlace.App.Services;

/// <summary>Abstraction over the native folder picker so ViewModels stay UI-free.</summary>
public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string title);
}

public sealed class FolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
            return null;

        var provider = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (provider is null)
            return null;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
