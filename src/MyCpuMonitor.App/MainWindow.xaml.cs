using Microsoft.UI.Xaml;
using MyCpuMonitor.Shell;

namespace MyCpuMonitor;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InfoText.Text =
            $"Elevated     : {ElevationGate.IsElevated}\n" +
            $"Process path : {Environment.ProcessPath}\n" +
            $"Base dir     : {AppContext.BaseDirectory}\n" +
            $"Runtime      : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    }
}
