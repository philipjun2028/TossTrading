using System.Windows;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += ok => DialogResult = ok;
    }

    // PasswordBox 는 보안상 바인딩을 지원하지 않으므로 코드비하인드로 전달
    private void OnSecretChanged(object sender, RoutedEventArgs e) => _vm.NewClientSecret = SecretBox.Password;
}
