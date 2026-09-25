using System.Windows;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class BotSettingsWindow : Window
{
    public BotSettingsWindow(BotSettingsViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.RequestClose += ok => DialogResult = ok;
        Loaded += (_, _) => vm.UpdateCostHintCommand.Execute(null);
    }

    public BotSettingsViewModel ViewModel { get; }
}
