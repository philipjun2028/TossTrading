using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Simulation;
using TossTrading.Toss;

namespace TossTrading.App.Services;

/// <summary>설정에 따라 엔진을 조립하고 수명을 관리한다 (Composition Root).</summary>
public sealed class EngineHost : IAsyncDisposable
{
    private TossConnection? _toss;

    /// <summary>봇·모의계좌 저장 폴더 (토스 데이터일 때만 사용)</summary>
    public static string StateDirectory => System.IO.Path.Combine(SettingsStore.DataDirectory, "state");

    public TradingEngine? Engine { get; private set; }
    public SimulatedMarket? Simulation { get; private set; }

    public static TossOptions ToTossOptions(AppSettings s) => new()
    {
        BaseUrl = s.TossBaseUrl,
        WebSocketUrl = s.TossWebSocketUrl,
        ClientId = s.TossClientId,
        ClientSecret = s.TossClientSecret,
        AccountSeq = s.TossAccountSeq,
    };

    public async Task StartAsync(AppSettings s)
    {
        if (Engine is not null) return;
        if (s.Execution == ExecutionMode.Live && s.DataSource != DataSourceKind.Toss)
            throw new InvalidOperationException("실전 주문은 토스 실시간 데이터에서만 가능합니다.");

        var options = new EngineOptions
        {
            Execution = s.Execution,
            DataSource = s.DataSource,
            Risk = s.Risk.Clone(),
            Scanner = s.Scanner.Clone(),
            Cost = s.Cost.Clone(),
            DataDirectory = SettingsStore.DataDirectory,
            RecordTicks = s.RecordTicks && s.DataSource == DataSourceKind.Toss,
            CapitalOverride = s.CapitalOverride,
            OrderRatePerSecond = s.OrderRatePerSecond,
            OpeningOrderRatePerSecond = s.OpeningOrderRatePerSecond,
            AutoPilot = s.AutoPilotPlan(),
            // 익일 보유(종가매매) 포지션을 재시작 후에도 이어서 관리. 시뮬레이션은 종목이 매번 바뀌므로 저장 안 함
            StateDirectory = s.DataSource == DataSourceKind.Toss ? StateDirectory : null,
        };

        IMarketDataFeed feed;
        IMarketDataSource source;
        IClock clock;
        IBroker broker;

        if (s.DataSource == DataSourceKind.Simulation)
        {
            Simulation = new SimulatedMarket(new SimulationOptions { Speed = s.SimulationSpeed, StartTime = s.SimulationStartTime });
            feed = Simulation;
            source = Simulation;
            clock = Simulation;
        }
        else
        {
            var tossOptions = ToTossOptions(s);
            if (!tossOptions.HasCredentials) throw new InvalidOperationException("설정에서 토스 Client ID / Secret 을 입력하세요.");
            _toss = new TossConnection(tossOptions);
            feed = _toss.Feed;
            source = _toss.Source;
            clock = SystemClock.Instance;
        }

        broker = s.Execution == ExecutionMode.Live
            ? _toss!.CreateBroker()
            : new PaperBroker(s.PaperStartingCash, new CostModel(options.Cost), clock);

        Engine = new TradingEngine(options, feed, source, broker, clock);
        try
        {
            await Engine.StartAsync();
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Engine is not null) await Engine.DisposeAsync();
        Engine = null;
        Simulation = null;
        if (_toss is not null) await _toss.DisposeAsync();
        _toss = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
