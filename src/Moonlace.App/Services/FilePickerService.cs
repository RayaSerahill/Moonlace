using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Moonlace.App.Services;

/// <summary>Native open/save dialogs behind an interface so ViewModels stay UI-free.</summary>
public interface IFilePickerService
{
    Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns);

    Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, IReadOnlyList<string> patterns);
}

public sealed class FilePickerService : IFilePickerService
{
    private static IStorageProvider? Provider =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? TopLevel.GetTopLevel(window)?.StorageProvider
            : null;

    public async Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        if (Provider is not { } provider)
            return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = [.. patterns] }],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, IReadOnlyList<string> patterns)
    {
        if (Provider is not { } provider)
            return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [.. patterns] }],
            DefaultExtension = patterns.Count > 0 ? patterns[0].TrimStart('*', '.') : null,
        });

        return file?.TryGetLocalPath();
    }
}
