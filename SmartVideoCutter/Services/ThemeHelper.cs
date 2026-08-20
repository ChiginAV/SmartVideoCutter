using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SmartVideoCutter.Models;

namespace SmartVideoCutter.Services;

/// <summary>
/// Подстраивает заголовок окна (caption) под текущую тему через DWM:
/// immersive dark mode (Windows 10/11) и точные цвета заголовка (Windows 11).
/// </summary>
public static class ThemeHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 20H2+ / Win11
    private const int DWMWA_CAPTION_COLOR = 35;           // Win11 22000+
    private const int DWMWA_TEXT_COLOR = 36;              // Win11 22000+

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Окна с подпиской на смену темы.
    private static readonly Dictionary<Window, EventHandler> _attached = new();
    private static bool _subscribed;

    /// <summary>
    /// Подключает окно к теме заголовка. Вызывать в конструкторе окна.
    /// </summary>
    public static void Attach(Window window)
    {
        if (_attached.ContainsKey(window))
            return;

        EventHandler onSourceInitialized = (_, _) => ApplyCaption(window);
        _attached[window] = onSourceInitialized;
        window.SourceInitialized += onSourceInitialized;
        window.Closed += (_, _) =>
        {
            if (_attached.Remove(window, out var handler))
                window.SourceInitialized -= handler;
        };

        // одна общая подписка на смену темы
        lock (_attached)
        {
            if (!_subscribed)
            {
                SettingsManager.CurrentSettings.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(Settings.ThemeMode))
                        return;
                    foreach (var w in _attached.Keys)
                        if (w.IsLoaded)
                            ApplyCaption(w);
                };
                _subscribed = true;
            }
        }
    }

    private static void ApplyCaption(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        bool dark = SettingsManager.CurrentSettings.ThemeMode == AppThemeMode.Dark;

        // Windows 10/11: тёмный режим заголовка (фон + системные кнопки)
        SetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);

        // Windows 11: точные цвета заголовка из палитры темы
        var resources = Application.Current.Resources;
        int captionColor = BrushToInt(resources["WindowBackgroundBrush"] as Brush, dark ? unchecked((int)0xFF202020) : unchecked((int)0xFFFFFFFF));
        int textColor = BrushToInt(resources["TextBrush"] as Brush, dark ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFF1A1A1A));

        SetAttribute(hwnd, DWMWA_CAPTION_COLOR, captionColor);
        SetAttribute(hwnd, DWMWA_TEXT_COLOR, textColor);
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // старая версия Windows не знает атрибут — молча игнорируем
        }
    }

    /// <summary>
    /// SolidColorBrush → 0xAARRGGBB. Если кисть недоступна — fallback.
    /// </summary>
    private static int BrushToInt(Brush? brush, int fallback)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            return unchecked((int)0xFF000000 | (c.R << 16) | (c.G << 8) | c.B);
        }
        return fallback;
    }
}
