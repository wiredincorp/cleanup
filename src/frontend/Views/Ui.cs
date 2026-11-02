using Microsoft.UI.Xaml;

namespace Cleanup.Views;

/// <summary>Small helpers for x:Bind function bindings.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static string RemovalHint(bool stopping) => stopping
        ? "Stopping after this app. Apps still waiting won't be touched."
        : "Follow any steps the app's uninstaller shows.";

    public static string RenamedNote(bool renamed, string destination) =>
        renamed ? $"Name taken; would be saved as \"{Path.GetFileName(destination)}\"" : "";

    public static string Size(ulong bytes) => ViewModels.AppItemViewModel.FormatSize(bytes) ?? "0 B";

    /// <summary>Which date decided a photo or video's folder.</summary>
    public static string CaptureNote(string? captured, string modified) =>
        string.IsNullOrEmpty(captured) ? $"No date taken. Last changed {modified}" : $"Taken {captured}";
}
