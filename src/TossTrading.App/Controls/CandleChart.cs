using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.Controls;

/// <summary>
/// 경량 캔들 차트 (외부 라이브러리 없이 OnRender 로 직접 그림).
/// 1분봉 + VWAP + 수평선(평균단가/손절/시가범위). 국내 관례: 양봉 빨강, 음봉 파랑.
/// </summary>
public sealed class CandleChart : FrameworkElement
{
    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(
        nameof(Chart), typeof(ChartView), typeof(CandleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartView? Chart
    {
        get => (ChartView?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    private static readonly Brush Background = Frozen(Color.FromRgb(0x1D, 0x20, 0x27));
    private static readonly Brush UpBrush = Frozen(Color.FromRgb(0xF0, 0x44, 0x52));
    private static readonly Brush DownBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x9A, 0xA1, 0xAD));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x2C, 0x30, 0x3A), 1);
    private static readonly Pen VwapPen = FrozenPen(Color.FromRgb(0xF5, 0xA5, 0x24), 1.5);
    private static readonly Pen UpPen = FrozenPen(Color.FromRgb(0xF0, 0x44, 0x52), 1);
    private static readonly Pen DownPen = FrozenPen(Color.FromRgb(0x3B, 0x82, 0xF6), 1);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var chart = Chart;
        if (chart is null || chart.Bars.Count == 0 || w < 100 || h < 60)
        {
            DrawText(dc, "표시할 데이터가 없습니다. 스캐너/봇에서 종목을 선택하세요.", new Point(12, 12), TextBrush, 12);
            return;
        }

        const double axisW = 70, axisH = 18, pad = 8;
        var plot = new Rect(pad, pad, Math.Max(10, w - axisW - pad), Math.Max(10, h - axisH - pad * 2));

        var bars = chart.Bars;
        var hi = bars.Max(b => b.High);
        var lo = bars.Min(b => b.Low);
        foreach (var v in chart.VwapSeries) { hi = Math.Max(hi, v); lo = Math.Min(lo, v); }
        foreach (var l in chart.Lines) { hi = Math.Max(hi, l.Price); lo = Math.Min(lo, l.Price); }
        if (hi <= lo) { hi += 1; lo -= 1; }
        var range = (double)(hi - lo);
        hi += (decimal)(range * 0.05);
        lo -= (decimal)(range * 0.05);

        double Y(decimal p) => plot.Bottom - (double)(p - lo) / (double)(hi - lo) * plot.Height;
        var slot = plot.Width / Math.Max(bars.Count, 30);
        double X(int i) => plot.Left + slot * (i + 0.5);

        // 가격 격자
        for (var i = 0; i <= 5; i++)
        {
            var p = lo + (hi - lo) * i / 5m;
            var y = Y(p);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, TickRules.RoundDown(p).ToString("N0", CultureInfo.CurrentCulture), new Point(plot.Right + 6, y - 7), TextBrush, 11);
        }

        // 캔들
        var bodyW = Math.Max(1, slot * 0.65);
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var up = b.Close >= b.Open;
            var pen = up ? UpPen : DownPen;
            var x = X(i);
            dc.DrawLine(pen, new Point(x, Y(b.High)), new Point(x, Y(b.Low)));
            var top = Y(Math.Max(b.Open, b.Close));
            var bottom = Y(Math.Min(b.Open, b.Close));
            dc.DrawRectangle(up ? UpBrush : DownBrush, null, new Rect(x - bodyW / 2, top, bodyW, Math.Max(1, bottom - top)));
            if (i % 30 == 0)
                DrawText(dc, Kst.TimeOf(b.Start).ToString("HH:mm"), new Point(x - 14, plot.Bottom + 3), TextBrush, 10);
        }

        // VWAP
        if (chart.VwapSeries.Count == bars.Count && bars.Count > 1)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(chart.VwapSeries[0])), false, false);
                for (var i = 1; i < bars.Count; i++) ctx.LineTo(new Point(X(i), Y(chart.VwapSeries[i])), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, VwapPen, geo);
            DrawText(dc, "VWAP", new Point(plot.Left + 4, Y(chart.VwapSeries[^1]) - 16), VwapPen.Brush, 10);
        }

        // 수평선
        foreach (var l in chart.Lines)
        {
            var color = l.Kind switch
            {
                "stop" => Color.FromRgb(0x3B, 0x82, 0xF6),
                "entry" => Color.FromRgb(0xE6, 0xE8, 0xEC),
                _ => Color.FromRgb(0x8B, 0x5C, 0xF6),
            };
            var pen = new Pen(new SolidColorBrush(color), 1) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            var y = Y(l.Price);
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, $"{l.Label} {l.Price:N0}", new Point(plot.Right - 120, y - 15), pen.Brush, 10);
        }

        // 현재가
        var last = bars[^1].Close;
        DrawText(dc, $"{chart.Name} {last:N0}", new Point(plot.Left + 4, plot.Top + 2), UpBrush, 13);
    }

    private void DrawText(DrawingContext dc, string text, Point at, Brush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }
}
