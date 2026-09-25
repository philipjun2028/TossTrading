using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class BotSettingsWindow : ThemedWindow
{
    public BotSettingsWindow(BotSettingsViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.RequestClose += ok => DialogResult = ok;
        Loaded += (_, _) => vm.UpdateCostHint();
    }

    public BotSettingsViewModel ViewModel { get; }
}
