using System;
using System.Configuration;
using System.Data;
using System.Windows;
using LibVLCSharp.Shared;

namespace SmartVideoCutterWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        SettingsManager.Load();
    }
}
