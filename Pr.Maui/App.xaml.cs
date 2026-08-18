namespace PrDesktop;

public partial class App : Application
{
    private SingleInstanceLease? _instanceLease;
    private MainPage? _mainPage;

    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var settings = AppSettings.Load();
        _instanceLease = SingleInstanceLease.AcquireDashboard(settings.SettingsPath);
        _mainPage = new MainPage(settings);
        var navigationPage = new NavigationPage(_mainPage)
        {
            BarBackgroundColor = Color.FromArgb("#FFFFFF"),
            BarTextColor = Color.FromArgb("#171A1F"),
        };
        NavigationPage.SetHasNavigationBar(_mainPage, false);

        var window = new Window(navigationPage)
        {
            Title = "PRs",
            Width = 1280,
            Height = 820,
            MinimumWidth = 980,
            MinimumHeight = 640,
        };
        window.Destroying += OnWindowDestroying;
        return window;
    }

    private void OnWindowDestroying(object? sender, EventArgs args)
    {
        _mainPage?.Stop();
        _instanceLease?.Dispose();
        _instanceLease = null;
    }
}
