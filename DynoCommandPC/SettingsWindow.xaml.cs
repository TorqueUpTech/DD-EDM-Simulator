using System.Windows;
using DynoCommandPC.Settings;

namespace DynoCommandPC;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings editable)
    {
        InitializeComponent();
        Settings = editable;
        DataContext = Settings;
        txtPath.Text = "Saved to: " + SettingsStore.FileLocation;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
