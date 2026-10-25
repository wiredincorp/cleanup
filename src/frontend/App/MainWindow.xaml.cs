using System.Numerics;
using Cleanup.Services;
using Cleanup.Views;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Cleanup;

public sealed partial class MainWindow : Window
{
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private BackdropKind? _appliedBackdrop;
    private bool _appliedTint;

    // The page fading out for the navigation in progress, and a counter so that the newest request wins.
    private UIElement? _leaving;
    private int _navigation;

    public MainWindow()
    {
        InitializeComponent();
        if (App.IsAdministrator)
        {
            AdminButton.Visibility = Visibility.Collapsed;
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Cleanup.ico"));
        AppWindow.Resize(new SizeInt32(1240, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 580;
        }
        CenterOnScreen();

        ApplyAppearance();
        RootGrid.Loaded += (_, _) => { UpdateCaptionInset(); PerformanceAudit.WindowLoaded(); };
        AppWindow.Changed += (_, _) => UpdateCaptionInset();
        SettingsService.Changed += ApplyAppearance;
        AccentService.WatchSystemChanges(DispatcherQueue, ApplyAppearance);
        Activated += (_, args) =>
            TitleText.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.55 : 1;
        Closed += (_, _) =>
        {
            SettingsService.Changed -= ApplyAppearance;
            if (ContentFrame.Content is SettingsPage settings) settings.FlushTint();
        };

        Nav.SelectedIndex = 0;
    }

    private bool PrefersDark() => SettingsService.Current.Theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => _uiSettings.GetColorValue(UIColorType.Background).R < 128,
    };

    private void ApplyAppearance()
    {
        AppSettings settings = SettingsService.Current;
        bool dark = PrefersDark();
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        BackdropKind backdrop = _accessibility.HighContrast ? BackdropKind.Solid : settings.Backdrop;
        bool tint = backdrop == BackdropKind.Acrylic && settings.AcrylicTintPreset != "system" &&
            Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        if (_appliedBackdrop != backdrop || _appliedTint != tint)
        {
            if (tint)
            {
                var material = new TintedAcrylicBackdrop();
                material.Update(settings, dark);
                SystemBackdrop = material;
            }
            else SystemBackdrop = WindowMaterials.Create(backdrop);
            _appliedBackdrop = backdrop;
            _appliedTint = tint;
        }
        if (SystemBackdrop is TintedAcrylicBackdrop acrylic) acrylic.Update(settings, dark);
        SolidBackground.Background = new SolidColorBrush(_accessibility.HighContrast ? _uiSettings.GetColorValue(UIColorType.Background) : dark ? WindowMaterials.DarkSurface : WindowMaterials.LightSurface);
        SolidBackground.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;

        // Caption buttons sit on the same surface as the rest of the window.
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        Color foreground = _accessibility.HighContrast ? _uiSettings.GetColorValue(UIColorType.Foreground) : dark ? Colors.White : Colors.Black;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x18, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonPressedForegroundColor = foreground;

        AccentService.Apply(RootGrid);
    }

    private void OnAccentClick(object sender, RoutedEventArgs e) => OpenSettings();

    private async void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Run as administrator",
            Content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 440,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Running as administrator lets cleanup read more of this PC.\n\n"
                            + "More install folders can be measured, including many Store apps, so more sizes show up.\n\n"
                            + "Windows’ activity log can be read, so last-run dates include programs a normal account cannot see.\n\n"
                            + "Uninstallers cleanup starts also run as administrator, so removals that need approval do not ask again.",
                    },
                    new CheckBox
                    {
                        Content = "Always run this app as administrator",
                        IsChecked = SettingsService.Current.RunAsAdministrator,
                    },
                },
            },
            PrimaryButtonText = "Restart as administrator",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (dialog.Content is StackPanel panel && panel.Children.OfType<CheckBox>().FirstOrDefault() is CheckBox always)
        {
            always.Checked += (_, _) => SettingsService.Remember(settings => settings.RunAsAdministrator = true);
            always.Unchecked += (_, _) => SettingsService.Remember(settings => settings.RunAsAdministrator = false);
        }
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        App.RestartElevated();
    }
    internal void PreviewMaterial()
    {
        if (SystemBackdrop is TintedAcrylicBackdrop acrylic) acrylic.Update(SettingsService.Current, PrefersDark());
    }
    private void UpdateCaptionInset() => AppTitleBar.Padding = new Thickness(12, 0, AppWindow.TitleBar.RightInset / (RootGrid.XamlRoot?.RasterizationScale ?? 1), 0);

    private async void OnRemoveSelection(object sender, EventArgs e)
    {
        if (ContentFrame.Content is ApplicationsPage page) await page.RemoveSelectionAsync();
    }

    private void OnClearSelection(object sender, EventArgs e)
    {
        if (ContentFrame.Content is ApplicationsPage page) page.ClearSelection();
    }

    private void OnSelectionSlotSizeChanged(object sender, SizeChangedEventArgs e) => SelectionPanel.MaxHeight = Math.Max(0, e.NewSize.Height);

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is not ListViewItem item) return;
        SettingsNav.SelectedIndex = -1;
        Navigate((item.Tag as string) switch
        {
            "core" => typeof(CorePage),
            "organize" => typeof(OrganizePage),
            "photos" => typeof(PhotosPage),
            "videos" => typeof(VideosPage),
            "folders" => typeof(FoldersPage),
            "history" => typeof(HistoryPage),
            _ => typeof(ApplicationsPage),
        });
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0) return;
        Nav.SelectedIndex = -1;
        Navigate(typeof(SettingsPage));
    }

    private void OnPatchNotesClick(object sender, RoutedEventArgs e)
    {
        Nav.SelectedIndex = -1;
        SettingsNav.SelectedIndex = -1;
        Navigate(typeof(PatchNotesPage));
    }

    private async void Navigate(Type page) => await NavigateAsync(page);

    private async Task NavigateAsync(Type page)
    {
        bool credits = page == typeof(CreditsPage);
        bool crossingCredits = credits || ContentFrame.CurrentSourcePageType == typeof(CreditsPage);
        Sidebar.Visibility = credits ? Visibility.Collapsed : Visibility.Visible;
        TitleBrand.Visibility = credits ? Visibility.Collapsed : Visibility.Visible;
        TitleBackButton.Visibility = credits ? Visibility.Visible : Visibility.Collapsed;
        AppearanceButton.Visibility = credits ? Visibility.Collapsed : Visibility.Visible;
        AdminButton.Visibility = credits || App.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ContentFrame, credits ? 0 : 1);
        Grid.SetColumnSpan(ContentFrame, credits ? 2 : 1);
        SelectionPanel.Visibility = page == typeof(ApplicationsPage) ? Visibility.Visible : Visibility.Collapsed;

        int navigation = ++_navigation;
        if (ContentFrame.CurrentSourcePageType != page)
        {
            if (!crossingCredits && Motion.Enabled && ContentFrame.Content is UIElement current)
            {
                // The current page leaves first, so the change reads as one movement rather than a cut.
                _leaving = current;
                await Motion.Exit(current);
                if (navigation != _navigation) return; // A newer request decides what is shown.
            }
            // Pages arrive with Motion.Enter below, not with the frame's own transition.
            ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
            // Sidebar destinations are peers. Retaining a navigation stack keeps
            // old pages and their native resources alive after every page switch.
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
            _leaving = null;
            if (crossingCredits)
            {
                // The credits canvas fades in place instead of sliding in, and so
                // does the page you return to from it.
                FadeIn(ContentFrame);
                if (credits) FadeIn(TitleBackButton);
            }
            else if (ContentFrame.Content is UIElement arriving)
            {
                Motion.Enter(arriving);
            }
        }
        else if (_leaving is { } stayed && ReferenceEquals(stayed, ContentFrame.Content))
        {
            // The request was for the page that had started to leave, so it comes back.
            _leaving = null;
            Motion.Enter(stayed);
        }
    }

    private void FadeIn(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(Visual.Opacity));
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.Opacity = 1;
            return;
        }
        Compositor compositor = visual.Compositor;
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(700);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    private void OnTitleBackClick(object sender, RoutedEventArgs e) => OpenSettings();

    public void OpenSettings()
    {
        SettingsNav.SelectedIndex = 0;
        Navigate(typeof(SettingsPage));
    }
    public void OpenCredits() => Navigate(typeof(CreditsPage));

    private void CenterOnScreen()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = area.WorkArea;
        SizeInt32 size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }
}
