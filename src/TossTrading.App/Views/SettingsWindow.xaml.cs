using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class SettingsWindow : ThemedWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += ok => DialogResult = ok;
    }
}
