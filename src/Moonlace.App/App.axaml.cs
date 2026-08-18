using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.App.ViewModels;
using Moonlace.App.Views;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Settings;
using Moonlace.GameData;
using Moonlace.GameData.Items;
using Moonlace.GameData.Resolution;

namespace Moonlace.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();

            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Moonlace starting on {OS} ({Arch})",
                RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture);

            var mainViewModel = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };

            desktop.MainWindow.Opened += async (_, _) =>
            {
                try
                {
                    await mainViewModel.StartAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Startup failed");
                }
            };

            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });

        // Core
        services.AddSingleton<ISettingsService, SettingsService>();

        // Session (non-destructive editing workspace)
        services.AddSingleton<Moonlace.Core.Session.ISessionService, Moonlace.Core.Session.SessionService>();

        // Penumbra live-edit link (writes into a linked mod folder, never the game)
        services.AddSingleton<Moonlace.Core.Penumbra.IPenumbraLinkService, Moonlace.Core.Penumbra.PenumbraLinkService>();

        // Game data (Lumina stays behind these)
        services.AddSingleton<LuminaGameDataService>();
        services.AddSingleton<IGameDataService>(sp => sp.GetRequiredService<LuminaGameDataService>());
        services.AddSingleton<IItemRepository, ItemRepository>();
        services.AddSingleton<AssetPathResolver>();
        services.AddSingleton<EffectiveAssetProvider>();
        services.AddSingleton<TextureDecoder>();
        services.AddSingleton<IRenderModelLoader, RenderModelBuilder>();
        services.AddSingleton<Moonlace.GameData.Editing.ItemEditingService>();
        services.AddSingleton<Moonlace.GameData.Upgrade.DawntrailModUpgrader>();

        // App
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<PenumbraViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton<BrowserViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
