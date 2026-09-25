using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DevExpress.Xpf.Charts;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.Views;

/// <summary>DevExpress ChartControl 기반 가격 차트. 엔진 ChartView 를 받아 시리즈/상수선을 갱신한다.</summary>
public partial class PriceChart : UserControl
{
    public sealed record CandlePoint(string Time, double Open, double High, double Low, double Close);
    public sealed record LinePoint(string Time, double Value);

    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(
        nameof(Chart), typeof(ChartView), typeof(PriceChart), new PropertyMetadata(null, (d, _) => ((PriceChart)d).Render()));

    public PriceChart() => InitializeComponent();

    public ChartView? Chart
    {
        get => (ChartView?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    private void Render()
    {
        var view = Chart;
        if (view is null || view.Bars.Count == 0)
        {
            CandleSeries.DataSource = null;
            VwapSeries.DataSource = null;
            AxisY.ConstantLinesInFront.Clear();
            return;
        }

        string T(Bar b) => Kst.TimeOf(b.Start).ToString("HH:mm");
        CandleSeries.DataSource = view.Bars
            .Select(b => new CandlePoint(T(b), (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close)).ToList();
        VwapSeries.DataSource = view.VwapSeries.Count == view.Bars.Count
            ? view.Bars.Select((b, i) => new LinePoint(T(b), (double)view.VwapSeries[i])).ToList()
            : null;

        AxisY.ConstantLinesInFront.Clear();
        foreach (var l in view.Lines)
        {
            var color = l.Kind switch
            {
                "stop" => Color.FromRgb(0x3B, 0x82, 0xF6),
                "entry" => Color.FromRgb(0xE6, 0xE8, 0xEC),
                _ => Color.FromRgb(0x8B, 0x5C, 0xF6),
            };
            var brush = new SolidColorBrush(color);
            AxisY.ConstantLinesInFront.Add(new ConstantLine
            {
                Value = (double)l.Price,
                Brush = brush,
                LineStyle = new LineStyle { Thickness = 1, DashStyle = DashStyles.Dash },
                Title = new ConstantLineTitle { Content = $"{l.Label} {l.Price:N0}", Foreground = brush, Alignment = ConstantLineTitleAlignment.Far },
            });
        }
    }
}
