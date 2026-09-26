using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class AnalysisWindow : ThemedWindow
{
    public AnalysisWindow(AnalysisViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => vm.AnalyzeCommand.Execute(null);
    }
}
