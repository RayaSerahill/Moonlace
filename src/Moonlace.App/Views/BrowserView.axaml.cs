using Avalonia.Controls;
using Moonlace.App.ViewModels;

namespace Moonlace.App.Views;

public partial class BrowserView : UserControl
{
    private BrowserViewModel? _subscribed;

    public BrowserView()
    {
        InitializeComponent();

        // Dev/testing hook: select an editor tab without UI automation.
        if (int.TryParse(System.Environment.GetEnvironmentVariable("MOONLACE_AUTOTAB"), out var tabIndex))
            Loaded += (_, _) => EditorTabs.SelectedIndex = tabIndex;
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.ModelLoaded -= OnModelLoaded;
            _subscribed = DataContext as BrowserViewModel;
            if (_subscribed is not null)
                _subscribed.ModelLoaded += OnModelLoaded;
        };
    }

    private void OnModelLoaded(Core.Models.RenderModel? model)
    {
        Viewport.Model = model;
    }
}
