using Cleanup.Interop;
using Cleanup.Services;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI;

namespace Cleanup.Views;

public sealed partial class SettingsPage : Page
{
    private bool _initializing = true;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tintSaveTimer;
    private bool _tintSavePending;
    private static readonly (string Id, string Name, string Color)[] TintPresets =
    [
        ("system", "Windows default", "#25252D"), ("accent", "Windows accent", "#25252D"),
        ("graphite", "Graphite", "#25252D"), ("violet", "Violet", "#36264D"),
        ("ocean", "Ocean", "#183B4D"), ("forest", "Forest", "#1C4233"),
        ("rose", "Rose", "#492C3B"), ("custom", "Custom color", "#36264D")
    ];

    public SettingsPage()
    {
        InitializeComponent();
        _tintSaveTimer = DispatcherQueue.CreateTimer();
        _tintSaveTimer.Interval = TimeSpan.FromMilliseconds(180);
        _tintSaveTimer.IsRepeating = false;
        _tintSaveTimer.Tick += (_, _) => SaveTint();
        AppSettings settings = SettingsService.Current;
        ThemeBox.SelectedIndex = settings.Theme switch { AppTheme.Light => 1, AppTheme.System => 2, _ => 0 };
        BackdropBox.SelectedIndex = settings.Backdrop switch { BackdropKind.Mica => 1, BackdropKind.Solid => 2, _ => 0 };
        AdvancedToggle.IsOn = settings.AdvancedMode;
        AdminToggle.IsOn = settings.RunAsAdministrator;
        CustomAccentToggle.IsOn = settings.UseCustomAccent;
        AccentHexBox.Text = settings.CustomAccent;
        AccentEditor.Visibility = settings.UseCustomAccent ? Visibility.Visible : Visibility.Collapsed;
        AccentSwatch.Background = AccentService.Brush(AccentService.ParseHex(settings.CustomAccent) ?? AccentService.WindowsAccent);
        TintPresetBox.ItemsSource = TintPresets.Select(p => p.Name).ToArray();
        LoadTintControls();

        var desktopVersion = ReleaseVersion.FromAssembly(typeof(App).Assembly);
        VersionText.Text = $"Version {desktopVersion}";
        BuildChannelText.Text = "Desktop preview";
        if (AppEnvironment.IsPackaged)
        {
            Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
            VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
            BuildChannelText.Text = Windows.ApplicationModel.Package.Current.Id.Name.EndsWith(".Dev", StringComparison.OrdinalIgnoreCase) ? "Development build" : "Release build";
        }
        try
        {
            CoreText.Text = $"Native core {NativeCore.AbiVersion}";
        }
        catch (Exception)
        {
            CoreText.Text = "Native core unavailable";
        }
        RefreshGameFolders();
        ConfigureReleaseLinks();
        Unloaded += (_, _) => FlushTint();
        _initializing = false;
    }

    private void RefreshGameFolders() => GameFolderList.ItemsSource = SettingsService.Current.GameFolders.ToArray();

    private void ConfigureReleaseLinks()
    {
        foreach (var (link, uri) in new[] { (DiscordSupportLink, ReleaseLinks.Discord), (GitHubLink, ReleaseLinks.Repository), (WebsiteLink, ReleaseLinks.Website), (ReleaseDownloadsLink, ReleaseLinks.Releases) })
        {
            if (uri is null) continue;
            link.NavigateUri = uri;
            link.Visibility = Visibility.Visible;
        }
        if (!AppEnvironment.IsPackaged)
        {
            CheckUpdatesButton.IsEnabled = ReleaseLinks.Repository is not null;
            UpdateStatus.Text = "Checks GitHub for a newer release only when you ask. Updating keeps your settings and history.";
        }
        else if (Windows.ApplicationModel.Package.Current.GetAppInstallerInfo() is null)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatus.Text = "Automatic updates require a release installed through its App Installer file. This installation has no update feed.";
        }
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (!AppEnvironment.IsPackaged)
        {
            if (ReleaseLinks.Repository is not { } repository) return;
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatus.Text = "Checking for updates…";
            try
            {
                ReleaseVersion current = ReleaseVersion.FromAssembly(typeof(App).Assembly);
                if (await UpdateCheck.FindNewerAsync(repository, current) is { } release)
                {
                    UpdateStatus.Text = $"Version {release.Version} is available. Close cleanup, then replace Cleanup.exe or run the new setup.";
                    ReleaseDownloadsLink.NavigateUri = release.Page;
                    ReleaseDownloadsLink.Content = $"Get {release.Version}";
                    ReleaseDownloadsLink.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateStatus.Text = "You’re up to date.";
                }
            }
            catch (Exception)
            {
                UpdateStatus.Text = "Updates couldn’t be checked. Check your connection, or open Latest release.";
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
            return;
        }
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates…";
        try
        {
            var result = await Windows.ApplicationModel.Package.Current.CheckUpdateAvailabilityAsync();
            UpdateStatus.Text = result.Availability switch
            {
                Windows.ApplicationModel.PackageUpdateAvailability.Available or Windows.ApplicationModel.PackageUpdateAvailability.Required => "An update is available. Windows will offer it on launch, or open Latest release to install it now.",
                Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates => "You’re up to date.",
                _ => "Updates couldn’t be checked. Try again later or open Latest release."
            };
        }
        catch (Exception)
        {
            UpdateStatus.Text = "Updates couldn’t be checked. Check your connection or open Latest release.";
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private async void OnAddGameFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        SettingsService.Update(settings =>
        {
            if (!settings.GameFolders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) settings.GameFolders.Add(folder.Path);
        });
        RefreshGameFolders();
    }

    private void OnRemoveGameFolderClick(object sender, RoutedEventArgs e)
    {
        if (GameFolderList.SelectedItem is not string folder) return;
        SettingsService.Update(settings => settings.GameFolders.RemoveAll(path => path.Equals(folder, StringComparison.OrdinalIgnoreCase)));
        RefreshGameFolders();
    }

    private void LoadTintControls()
    {
        bool initializing = _initializing;
        _initializing = true;
        AppSettings settings = SettingsService.Current;
        int index = Array.FindIndex(TintPresets, p => p.Id == settings.AcrylicTintPreset);
        TintPresetBox.SelectedIndex = Math.Max(0, index);
        TintHexBox.Text = settings.AcrylicTintColor;
        TintSwatch.Background = AccentService.Brush(AccentService.ParseHex(settings.AcrylicTintColor) ?? AccentService.WindowsAccent);
        TintStrengthSlider.Value = double.IsFinite(settings.AcrylicTintStrength) ? Math.Clamp(settings.AcrylicTintStrength, 0, 100) : 65;
        BackgroundVisibilitySlider.Value = double.IsFinite(settings.AcrylicBackgroundVisibility) ? Math.Clamp(settings.AcrylicBackgroundVisibility, 0, 100) : 40;
        AcrylicOptions.Visibility = settings.Backdrop == BackdropKind.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        TintCustomEditor.Visibility = settings.AcrylicTintPreset == "custom" ? Visibility.Visible : Visibility.Collapsed;
        TintSliders.Visibility = settings.AcrylicTintPreset != "system" ? Visibility.Visible : Visibility.Collapsed;
        _initializing = initializing;
    }

    private void OnTintPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TintPresetBox.SelectedIndex < 0) return;
        var preset = TintPresets[TintPresetBox.SelectedIndex];
        SettingsService.Update(settings => {
            settings.AcrylicTintPreset = preset.Id;
            if (preset.Id is not ("custom" or "system" or "accent")) settings.AcrylicTintColor = preset.Color;
        });
        LoadTintControls();
    }

    private void OnTintSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        SettingsService.Current.AcrylicTintStrength = TintStrengthSlider.Value;
        SettingsService.Current.AcrylicBackgroundVisibility = BackgroundVisibilitySlider.Value;
        (App.MainWindow as MainWindow)?.PreviewMaterial();
        _tintSavePending = true;
        _tintSaveTimer.Stop();
        _tintSaveTimer.Start();
    }

    /// <summary>Saves a tint change still waiting on its debounce. Runs when the page leaves and when the window closes.</summary>
    public void FlushTint()
    {
        _tintSaveTimer.Stop();
        SaveTint();
    }

    private void SaveTint()
    {
        if (!_tintSavePending) return;
        _tintSavePending = false;
        SettingsService.Remember(_ => { });
    }

    private void ApplyTint(Color color)
    {
        SettingsStatus.IsOpen = false;
        SettingsService.Update(settings => { settings.AcrylicTintPreset = "custom"; settings.AcrylicTintColor = AccentService.ToHex(color); });
        LoadTintControls();
    }

    private void OnApplyTintClick(object sender, RoutedEventArgs e)
    {
        if (AccentService.ParseHex(TintHexBox.Text.Trim()) is { } color) ApplyTint(color);
        else { SettingsStatus.Title = "Use a tint such as #36264D"; SettingsStatus.IsOpen = true; }
    }

    private async Task<Color?> ChooseColorAsync(string title, Color initial)
    {
        var picker = new ColorPicker { Color = initial, IsAlphaEnabled = false };
        var dialog = new ContentDialog { Title = title, Content = picker, XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            PrimaryButtonText = "Apply color", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? picker.Color : null;
    }

    private async void OnChooseTintClick(object sender, RoutedEventArgs e)
    {
        if (await ChooseColorAsync("Acrylic tint", AccentService.ParseHex(SettingsService.Current.AcrylicTintColor) ?? AccentService.WindowsAccent) is { } color) ApplyTint(color);
    }

    private async void OnChooseAccentClick(object sender, RoutedEventArgs e)
    {
        if (await ChooseColorAsync("Accent color", AccentService.ParseHex(SettingsService.Current.CustomAccent) ?? AccentService.WindowsAccent) is not { } color) return;
        AccentHexBox.Text = AccentService.ToHex(color);
        OnApplyAccentClick(sender, e);
    }

    private void OnCustomAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AccentEditor.Visibility = CustomAccentToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SettingsService.Update(settings => settings.UseCustomAccent = CustomAccentToggle.IsOn);
    }

    private void OnCreditsClick(object sender, RoutedEventArgs e) => (App.MainWindow as MainWindow)?.OpenCredits();

    private void OnApplyAccentClick(object sender, RoutedEventArgs e)
    {
        if (AccentService.ParseHex(AccentHexBox.Text.Trim()) is not { } color)
        {
            SettingsStatus.Title = "Use a color such as #3B82F6"; SettingsStatus.IsOpen = true; return;
        }
        SettingsStatus.IsOpen = false;
        SettingsService.Update(settings => { settings.CustomAccent = AccentService.ToHex(color); settings.UseCustomAccent = true; });
        AccentSwatch.Background = AccentService.Brush(color);
        CustomAccentToggle.IsOn = true;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            SettingsService.Update(s => s.Theme = ThemeBox.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.System, _ => AppTheme.Dark });
        }
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            SettingsService.Update(s => s.Backdrop = BackdropBox.SelectedIndex switch { 1 => BackdropKind.Mica, 2 => BackdropKind.Solid, _ => BackdropKind.Acrylic });
            LoadTintControls();
        }
    }

    private void OnAdminToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SettingsService.Remember(s => s.RunAsAdministrator = AdminToggle.IsOn);
        }
    }

    private void OnAdvancedToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SettingsService.Update(s => s.AdvancedMode = AdvancedToggle.IsOn);
        }
    }
}
