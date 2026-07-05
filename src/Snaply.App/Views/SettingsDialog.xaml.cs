using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Serilog.Core;
using Serilog.Events;
using Snaply.Diagnostics;
using Snaply.Services;
using Snaply.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Snaply.Views;

/// <summary>
/// The Settings dialog: hosts the changeable beautify controls (background, drop
/// shadow, aspect ratio), the hide-while-capturing toggle, the display-language
/// picker, and the diagnostics panel (app info, logs folder, verbose logging). It binds
/// to the shared <see cref="MainViewModel"/> singleton resolved from the composition
/// root, so edits re-render the current preview live. The host must set
/// <see cref="Microsoft.UI.Xaml.UIElement.XamlRoot"/> before calling <c>ShowAsync</c>
/// (required for unpackaged WinUI dialogs), and should check
/// <see cref="NeedsRestartForLanguage"/> after the dialog closes.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly LanguageService _language;
    private readonly ThemeService _theme;
    private readonly SettingsStore _settingsStore;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly ILogger<SettingsDialog> _logger;
    private readonly AppLanguage _initialLanguage;
    private bool _initializing;

    /// <summary>The shared view model that backs the settings controls (bound via x:Bind).</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>Resolves the shared services and wires up the dialog.</summary>
    public SettingsDialog()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _language = App.Services.GetRequiredService<LanguageService>();
        _theme = App.Services.GetRequiredService<ThemeService>();
        _settingsStore = App.Services.GetRequiredService<SettingsStore>();
        _levelSwitch = App.Services.GetRequiredService<LoggingLevelSwitch>();
        _logger = App.Services.GetRequiredService<ILogger<SettingsDialog>>();
        IUiStrings strings = App.Services.GetRequiredService<IUiStrings>();

        InitializeComponent();

        // Title / close button are set here rather than via x:Uid so the dialog root
        // stays free of resource-resolution edge cases.
        Title = strings.Get("SettingsTitle");
        CloseButtonText = strings.Get("SettingsCloseButton");

        _initialLanguage = _language.CurrentLanguage;

        // Suppress the change handlers while we set initial control state.
        _initializing = true;
        try
        {
            ThemeSegmented.SelectedIndex = (int)_theme.CurrentTheme;
            PopulateLanguages(strings);
            InitializeDiagnostics();
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// True when the user selected a display language that differs from the one active
    /// when the dialog opened; the host should then offer to restart the app (the new
    /// language only takes full effect after a restart).
    /// </summary>
    public bool NeedsRestartForLanguage => _language.CurrentLanguage != _initialLanguage;

    private void PopulateLanguages(IUiStrings strings)
    {
        // Order must match the AppLanguage enum (System, English, Japanese, Chinese).
        // Native language names stay in their own script; only "System default" is localized.
        LanguageComboBox.Items.Add(strings.Get("LanguageSystemDefault"));
        LanguageComboBox.Items.Add("English");
        LanguageComboBox.Items.Add("日本語");
        LanguageComboBox.Items.Add("简体中文");
        LanguageComboBox.SelectedIndex = (int)_language.CurrentLanguage;
    }

    private void InitializeDiagnostics()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        AppInfoText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Snaply {version}\n{RuntimeInformation.FrameworkDescription}\n{Environment.OSVersion}");

        VerboseLoggingToggle.IsOn = _settingsStore.Load().VerboseLogging;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageComboBox.SelectedIndex < 0)
        {
            return;
        }

        _language.SetLanguage((AppLanguage)LanguageComboBox.SelectedIndex);
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeSegmented.SelectedIndex < 0)
        {
            return;
        }

        // AppTheme's ordinals match the SegmentedItem order (System, Light, Dark).
        // Theme applies immediately (unlike language, which needs a restart).
        _theme.SetTheme((AppTheme)ThemeSegmented.SelectedIndex);
    }

    private void OnVerboseLoggingToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        bool on = VerboseLoggingToggle.IsOn;

        // Take effect immediately for this session, and persist for the next launch.
        _levelSwitch.MinimumLevel = on ? LogEventLevel.Debug : LogEventLevel.Information;
        _settingsStore.Update(settings => settings.VerboseLogging = on);
    }

    private void OnOpenLogsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!ShellActions.OpenFolder(AppPaths.LogsDirectory))
        {
            AppLog.OpenLogsFailed(_logger);
        }
    }

    private void OnCopyDiagnosticsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(BuildDiagnostics());
            Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Copying diagnostics is a convenience; the info is still visible in the panel.
        }
    }

    private string BuildDiagnostics()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Snaply:     {version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS:         {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime:    {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Arch:       OS={RuntimeInformation.OSArchitecture}, Process={RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"UICulture:  {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Language:   {_language.CurrentLanguage}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Theme:      {_theme.CurrentTheme}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Logs:       {AppPaths.LogsDirectory}");
        return sb.ToString();
    }
}
