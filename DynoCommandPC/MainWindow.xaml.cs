using System.Windows;
using DynoCommandPC.ViewModel;

namespace DynoCommandPC;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += (_, _) => _vm.TryAutoConnectAtStartup();
        Closed += (_, _) => _vm.Shutdown();
#if EDM_SIM
        Title += "  —  Sim build";
#elif DYNO_NET
        Title += "  —  Net build";
        BtnEdmSim.Visibility = Visibility.Collapsed; // network handheld: no simulator
#else
        BtnEdmSim.Visibility = Visibility.Collapsed; // handheld-only build: no simulator
#endif
    }

    private void OpenEdmServer_Click(object sender, RoutedEventArgs e)
    {
#if EDM_SIM
        new EdmServerWindow { Owner = this }.Show();
#endif
    }
}
