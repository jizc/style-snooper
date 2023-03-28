using System;
using System.ComponentModel;
using System.Configuration;
using System.Windows;

namespace StyleSnooper;

/// <summary>
///   Persists a Window's Size, Location and WindowState to UserScopeSettings
/// </summary>
public class WindowSettings
{
    /// <summary>
    ///   Register the "Save" attached property and the "OnSaveInvalidated" callback
    /// </summary>
    public static readonly DependencyProperty SaveProperty = DependencyProperty.RegisterAttached(
        "Save",
        typeof(bool),
        typeof(WindowSettings),
        new FrameworkPropertyMetadata(OnSaveInvalidated));

    private readonly Window window;

    private WindowApplicationSettings? windowApplicationSettings;

    public WindowSettings(Window window)
    {
        this.window = window;
    }

    [Browsable(false)]
    public WindowApplicationSettings Settings => windowApplicationSettings ??= CreateWindowApplicationSettingsInstance();

    public static void SetSave(DependencyObject depObj, bool enabled) => depObj.SetValue(SaveProperty, enabled);

    protected virtual WindowApplicationSettings CreateWindowApplicationSettingsInstance() => new();

    /// <summary>
    ///   Load the Window Size Location and State from the settings object
    /// </summary>
    protected virtual void LoadWindowState()
    {
        Settings.Reload();
        if (Settings.Location != Rect.Empty)
        {
            window.Left = Settings.Location.Left;
            window.Top = Settings.Location.Top;
            window.Width = Settings.Location.Width;
            window.Height = Settings.Location.Height;
        }

        if (Settings.WindowState is not WindowState.Maximized)
        {
            window.WindowState = Settings.WindowState;
        }
    }

    /// <summary>
    ///   Save the Window Size, Location and State to the settings object
    /// </summary>
    protected virtual void SaveWindowState()
    {
        Settings.WindowState = window.WindowState;
        Settings.Location = window.RestoreBounds;
        Settings.Save();
    }

    /// <summary>
    ///   Called when Save is changed on an object.
    /// </summary>
    private static void OnSaveInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && e.NewValue is true)
        {
            var settings = new WindowSettings(window);
            settings.Attach();
        }
    }

    private void Attach()
    {
        window.Closing += WindowClosing;
        window.Initialized += WindowInitialized;
        window.Loaded += WindowLoaded;
    }

    private void WindowClosing(object? sender, CancelEventArgs e) => SaveWindowState();
    private void WindowInitialized(object? sender, EventArgs e) => LoadWindowState();

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (Settings.WindowState is WindowState.Maximized)
        {
            window.WindowState = Settings.WindowState;
        }
    }

    public class WindowApplicationSettings : ApplicationSettingsBase
    {
        [UserScopedSetting]
        public Rect Location
        {
            get => this["Location"] is { } value ? (Rect)value : Rect.Empty;
            set => this["Location"] = value;
        }

        [UserScopedSetting]
        public WindowState WindowState
        {
            get => this["WindowState"] is { } value ? (WindowState)value : WindowState.Normal;
            set => this["WindowState"] = value;
        }
    }
}
