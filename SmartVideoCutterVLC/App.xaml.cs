using System;
using System.Configuration;
using System.Data;
using System.Windows;
using LibVLCSharp.Shared;
using SmartVideoCutterVLC.Services;

namespace SmartVideoCutterVLC;

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
