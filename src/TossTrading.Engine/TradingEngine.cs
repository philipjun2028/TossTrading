using System.Collections.Concurrent;
using System.Threading.Channels;
using TossTrading.Domain;
using TossTrading.Engine.Analytics;
using TossTrading.Engine.Automation;
using TossTrading.Engine.Infrastructure;
using TossTrading.Engine.Market;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Risk;
using TossTrading.Engine.Scanning;
using TossTrading.Engine.Strategies;
using TossTrading.Engine.Trading;

namespace TossTrading.Engine;

public sealed class EngineOptions
{
    public ExecutionMode Execution { get; set; } = ExecutionMode.Paper;
    public DataSourceKind DataSource { get; set; } = DataSourceKind.Simulation;

    /// <summary>자동 운용 (종목 자동 선정·매매). 기본 꺼짐.</summary>
    public AutoPilotPlan AutoPilot { get; set; } = AutoPilotPlan.Default();
    public RiskSettings Risk { get; set; } = new();
    public ScannerSettings Scanner { get; set; } = new();
    public CostSettings Cost { get; set; } = new();

    /// <summary>로그/거래기록/틱기록 저장 폴더 (null 이면 저장 안 함)</summary>
    public string? DataDirectory { get; set; }
    public bool RecordTicks { get; set; }

    /// <summary>주문 호출 한도 (초당). 공개 자료의 대략치보다 낮게 잡는다.</summary>
    public int OrderRatePerSecond { get; set; } = 5;
    public int OpeningOrderRatePerSecond { get; set; } = 2;

    /// <summary>웹소켓 구독 토픽 예산 (연결당 100)</summary>
    public int MaxTopics { get; set; } = 95;

    /// <summary>계좌 평가액 대신 사용할 운용 기준 금액 (0 = 실제 계좌 평가액)</summary>
    public decimal CapitalOverride { get; set; }

    public bool RunScanner { get; set; } = true;

    /// <summary>
    /// 봇·모의계좌 상태 저장 폴더 (null = 저장 안 함). 익일 보유(종가매매) 포지션을 재시작 후에도 이어서 관리하려면 필요.
    /// 시뮬레이션 시장은 종목이 매번 새로 만들어지므로 저장하지 않는다.
    /// </summary>
    public string? StateDirectory { get; set; }
}

/// <summary>
/// 엔진 오케스트레이터. 모든 상태 변경은 단일 이벤트 루프 스레드에서 순차 처리된다
/// (시세·주문 이벤트·사용자 명령·타이머를 하나의 채널로 직렬화 → 락 없는 봇 로직).
/// UI 는 <see cref="Snapshot"/> 을 주기적으로 읽기만 한다.
/// </summary>
public sealed class TradingEngine : IBotHost, IAsyncDisposable
{
    private sealed class TrackedOrder
    {
        public required string ClientOrderId { get; init; }
        public required string BotId { get; init; }
        public required string Symbol { get; init; }
        public required OrderSide Side { get; init; }
        public decimal ReservedAmount { get; set; }
        public string? CurrentOrderId { get; set; }
        public bool Done { get; set; }
        public Dictionary<string, (decimal Filled, decimal Value)> PerOrder { get; } = new();
    }

    private readonly EngineOptions _options;
    private readonly IMarketDataFeed _feed;
    private readonly IMarketDataSource _source;
    private readonly IBroker _broker;
    private readonly IClock _clock;
    private readonly PaperBroker? _paper;
    private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly EngineLog _log;
    private readonly TradeJournal _journal;
    private readonly AnalyticsRecorder _analytics;
    private readonly AutoPilot _autoPilot;
    private ScannerSettings _scannerBase;
    private ScanMode? _scanOverride;
    private readonly TickRecorder? _recorder;
    private readonly OrderManager _orders;
    private readonly ScannerService _scanner;
    private readonly RiskManager _risk;
    private readonly CostModel _cost;

    // ---- 루프 스레드 전용 상태
    private readonly Dictionary<string, SymbolContext> _contexts = new();
    private readonly List<TradingBot> _bots = new();
    private readonly Dictionary<string, TrackedOrder> _byClient = new();
    private readonly Dictionary<string, TrackedOrder> _byOrderId = new();
    private readonly Dictionary<string, (DateTimeOffset At, List<OrderUpdate> Updates)> _unmatched = new();
    private readonly List<ClosedTrade> _closed = new();
    private readonly Dictionary<string, bool> _seeded = new(); // 종목 → 전체 보강 여부
    private IReadOnlyList<ScanCandidate> _candidates = Array.Empty<ScanCandidate>();
    private AccountSnapshot _account = new(0, Array.Empty<Holding>(), Array.Empty<OrderUpdate>());
    private string? _focus;
    private string _status = "중지됨";
    private bool _feedConnected;
    private bool _wasDisconnected;
    private int _botSeq;
    private readonly StateStore? _store;
    private DateOnly _tradingDate;
    private DateTimeOffset _lastSave;
    private DateTimeOffset _lastSecond, _lastAccountRefresh;
    private (HashSet<string> Trades, HashSet<string> Books) _subs = (new(), new());

    private readonly ConcurrentDictionary<string, LiveMetrics> _liveMetrics = new();
    private CancellationTokenSource? _cts;
    private Task? _loop, _timer;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;

    public TradingEngine(EngineOptions options, IMarketDataFeed feed, IMarketDataSource source, IBroker broker, IClock clock)
    {
        _options = options;
        _feed = feed;
        _source = source;
        _broker = broker;
        _clock = clock;
        _paper = broker as PaperBroker;
        _cost = new CostModel(options.Cost);
        _risk = new RiskManager(options.Risk);

        var dir = options.DataDirectory;
        _log = new EngineLog(dir is null ? null : Path.Combine(dir, "logs"), () => _clock.Now);
        _journal = new TradeJournal(dir is null ? null : Path.Combine(dir, "journal"));
        _analytics = new AnalyticsRecorder(dir is null ? null : Path.Combine(dir, "journal"));
        if (options.RecordTicks && dir is not null) _recorder = new TickRecorder(Path.Combine(dir, "ticks"));

        if (options.StateDirectory is not null)
            _store = new StateStore(options.StateDirectory, options.Execution == ExecutionMode.Live ? "live" : "paper");

        _orders = new OrderManager(broker, OrderRateLimit, r => Post(() => HandleJobResult(r)), (l, m) => _log.Write(l, "주문", m));
        _scanner = new ScannerService(source, clock, options.Scanner,
            sym => _liveMetrics.TryGetValue(sym, out var m) ? m : null,
            list => Post(() => HandleCandidates(list)),
            (l, m) => _log.Write(l, "스캐너", m));

        _scannerBase = options.Scanner.Clone();
        _autoPilot = new AutoPilot(new AutoPilotHost(this), options.AutoPilot);

        _feed.Trade += t => Post(() => HandleTrade(t));
        _feed.OrderBook += b => Post(() => HandleBook(b));
        _feed.ConnectionChanged += (c, msg) => Post(() => HandleConnection(c, msg));
        _broker.OrderUpdated += u => Post(() => HandleOrderUpdate(u));
    }

    public EngineSnapshot Snapshot => _snapshot;
    public bool IsRunning => _loop is not null;
    public EngineLog EventLog => _log;

    // ================================================================ 수명주기

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _status = "시작 중";
        _log.Write(LogLevel.Info, "엔진", $"시작: 데이터={_options.DataSource}, 주문={_options.Execution} ({_broker.Name})");

        await _broker.StartAsync(_cts.Token).ConfigureAwait(false);
        if (_store is not null && _paper is not null && _store.LoadPaper() is { } paperState)
        {
            _paper.RestoreState(paperState);
            _log.Write(LogLevel.Info, "엔진", $"모의계좌 복원: 예수금 {paperState.Cash:N0}원, 보유 {paperState.Positions.Count}종목");
        }
        var saved = _store?.LoadBots() ?? Array.Empty<BotPersistState>();
        var snap = await _broker.GetAccountSnapshotAsync(_cts.Token).ConfigureAwait(false);
        Post(() =>
        {
            _account = snap;
            _tradingDate = Kst.DateOf(_clock.Now);
            _risk.SetStartEquity(_options.CapitalOverride > 0 ? _options.CapitalOverride : snap.Equity);
            _log.Write(LogLevel.Info, "엔진", $"운용 기준 금액 {_risk.StartEquity:N0}원 (예수금 {snap.Cash:N0})");
            RestoreBots(saved, snap);
        });

        _orders.Start();
        await _feed.StartAsync(_cts.Token).ConfigureAwait(false);
        if (_options.RunScanner) _scanner.Start();
        _timer = Task.Run(() => TimerAsync(_cts.Token));
        Post(() => _status = "실행 중");
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null && _store is not null)
        {
            try { await Invoke(() => { SaveState(); _analytics.FlushIncomplete(_clock.Now); return true; }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* 저장 실패해도 종료는 진행 */ }
        }
        _cts?.Cancel();
        await _scanner.DisposeAsync().ConfigureAwait(false);
        await _orders.DisposeAsync().ConfigureAwait(false);
        await _feed.DisposeAsync().ConfigureAwait(false);
        await _broker.DisposeAsync().ConfigureAwait(false);
        _inbox.Writer.TryComplete();
        foreach (var t in new[] { _loop, _timer })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); } catch { /* 종료 */ }
        }
        if (_recorder is not null) await _recorder.DisposeAsync().ConfigureAwait(false);
        await _log.DisposeAsync().ConfigureAwait(false);
        _snapshot = _snapshot with { Running = false, Status = "중지됨" };
    }

    private void Post(Action action) => _inbox.Writer.TryWrite(action);

    /// <summary>루프 스레드에서 실행하고 결과를 기다린다 (UI 명령용).</summary>
    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var reader = _inbox.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { _log.Write(LogLevel.Error, "엔진", $"처리 오류: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Post(OnTimer);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>테스트용: 대기 중인 이벤트를 모두 처리할 때까지 기다린다.</summary>
    public Task FlushAsync() => Invoke(() => true);

    private int _pendingAsync;

    /// <summary>
    /// 백테스트용: 이벤트 큐·주문 처리·비동기 조회가 모두 끝날 때까지 기다린다.
    /// (가상 시계를 한 단계 진행할 때마다 호출 → 실시간과 같은 순서로 결정이 이뤄진다)
    /// </summary>
    public async Task SettleAsync(CancellationToken ct = default)
    {
        var idleRounds = 0;
        for (var spin = 0; ; spin++)
        {
            await FlushAsync().ConfigureAwait(false);
            if (_orders.IsIdle && Volatile.Read(ref _pendingAsync) == 0)
            {
                if (++idleRounds >= 2) return; // 방금 처리한 이벤트가 새 작업을 만들었는지 한 번 더 확인
                continue;
            }
            idleRounds = 0;
            ct.ThrowIfCancellationRequested();
            // 대기 작업은 대부분 수 마이크로초 안에 끝난다. Task.Delay(1) 은 Windows 에서 약 15ms 라서 오래 양보만 한다
            if (spin < 20_000) await Task.Yield();
            else await Task.Delay(1, ct).ConfigureAwait(false);
            if (spin > 40_000) throw new TimeoutException("엔진이 한 단계 처리를 끝내지 못했습니다.");
        }
    }

    /// <summary>백테스트용: 스캐너를 지금 한 번 실행하고 결과를 반영한다 (RunScanner=false 일 때).</summary>
    public async Task ScanNowAsync(CancellationToken ct = default)
    {
        var list = await _scanner.ScanOnceAsync(ct).ConfigureAwait(false);
        await Invoke(() => { HandleCandidates(list); return true; }).ConfigureAwait(false);
    }

    /// <summary>테스트용: 타이머 한 번을 즉시 실행한다.</summary>
    public Task TickAsync() => Invoke(() => { OnTimer(); return true; });

    // ================================================================ 명령 (스레드 안전)

    public Task<string> AddBotAsync(string symbol, string? name, BotSettings settings) => Invoke(() => AddBotCore(symbol, name, settings).Id);

    private TradingBot AddBotCore(string symbol, string? name, BotSettings settings, string? autoRole = null)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        if (_bots.Any(b => b.Symbol == symbol && !b.State.IsFinished()))
            throw new InvalidOperationException($"{symbol} 에 이미 실행 중인 봇이 있습니다.");

        var ctx = EnsureContext(symbol, name, full: true);
        var bot = new TradingBot($"{symbol}#{++_botSeq}", ctx, settings, this) { AutoRole = autoRole };
        _bots.Add(bot);
        _focus ??= symbol;
        RecomputeSubscriptions();
        _log.Write(LogLevel.Info, bot.Id, $"봇 추가: {ctx.Name} [{EntrySignalFactory.DisplayName(settings.Strategy)} / {settings.Mode}]{(autoRole is null ? "" : " (자동 운용)")}");
        return bot;
    }

    public Task StartBotAsync(string id) => WithBot(id, b => b.Start());
    public Task StopBotAsync(string id, bool flatten) => WithBot(id, b => b.Stop(flatten));
    public Task ManualBuyAsync(string id) => WithBot(id, b => b.ManualBuy());
    public Task ApproveSignalAsync(string id) => WithBot(id, b => b.ApproveSignal());
    public Task RejectSignalAsync(string id) => WithBot(id, b => b.RejectSignal());
    public Task FlattenBotAsync(string id) => WithBot(id, b => b.Flatten("수동 청산", emergency: false));
    public Task UpdateBotSettingsAsync(string id, BotSettings s) => WithBot(id, b =>
    {
        var errors = s.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        b.UpdateSettings(s);
    });

    public Task RemoveBotAsync(string id) => Invoke(() =>
    {
        var bot = FindBot(id);
        if (bot.HasPosition || bot.HasWorkingOrders)
            throw new InvalidOperationException("보유/미체결 주문이 있는 봇은 삭제할 수 없습니다. 먼저 정지(청산)하세요.");
        _bots.Remove(bot);
        RecomputeSubscriptions();
        return true;
    });

    /// <summary>킬스위치: 전 봇 정지 + 미체결 취소 + 보유 시장가 청산 + 신규 진입 차단</summary>
    public Task KillSwitchAsync() => Invoke(() =>
    {
        _risk.ActivateKillSwitch();
        foreach (var b in _bots) b.Kill();
        _log.Write(LogLevel.Error, "리스크", "■ 킬스위치 작동: 전 봇 정지 및 보유분 시장가 청산");
        return true;
    });

    public Task ResetKillSwitchAsync() => Invoke(() => { _risk.ResetKillSwitch(); _log.Write(LogLevel.Warn, "리스크", "킬스위치 해제"); return true; });

    public Task SetFocusAsync(string? symbol) => Invoke(() => { _focus = symbol; return true; });

    public Task UpdateRiskAsync(RiskSettings s) => Invoke(() => { _risk.UpdateSettings(s); return true; });

    public void UpdateScanner(ScannerSettings s) => Post(() => { _scannerBase = s.Clone(); ApplyScanner(); });

    /// <summary>자동 운용 켜기/끄기·설정 변경</summary>
    public Task SetAutoPilotAsync(AutoPilotPlan plan) => Invoke(() =>
    {
        var errors = plan.Settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        _autoPilot.UpdatePlan(plan);
        if (plan.Settings.Enabled) _autoPilot.OnTimer();
        return true;
    });

    private void ApplyScanner()
    {
        var s = _scannerBase.Clone();
        if (_scanOverride is { } m) s.Mode = m;
        _scanner.UpdateSettings(s);
    }

    /// <summary>자동 운용 → 엔진 연결 (루프 스레드에서만 호출)</summary>
    private sealed class AutoPilotHost(TradingEngine e) : IAutoPilotHost
    {
        public DateTimeOffset Now => e._clock.Now;
        public IReadOnlyList<ScanCandidate> Candidates => e._candidates;
        public IReadOnlyList<TradingBot> Bots => e._bots;
        public string? EntryBlockReason => e._risk.BlockReason(e._clock.Now, e._bots.Sum(b => b.UnrealizedNet));

        public void SetScanMode(ScanMode? mode)
        {
            e._scanOverride = mode;
            e.ApplyScanner();
        }

        public TradingBot? AddAutoBot(string symbol, string name, BotSettings settings, string role)
        {
            try
            {
                var bot = e.AddBotCore(symbol, name, settings, role);
                bot.Start();
                return bot;
            }
            catch (Exception ex)
            {
                e._log.Write(LogLevel.Warn, "자동운용", $"{name}: 봇 추가 실패 — {ex.Message}");
                return null;
            }
        }

        public void RemoveBot(TradingBot bot)
        {
            if (bot.HasPosition || bot.HasWorkingOrders) return;
            e._bots.Remove(bot);
            if (e._focus == bot.Symbol) e._focus = e._bots.FirstOrDefault()?.Symbol;
            e.RecomputeSubscriptions();
        }

        public void Log(LogLevel level, string message) => e._log.Write(level, "자동운용", message);
    }

    private Task WithBot(string id, Action<TradingBot> action) => Invoke(() => { action(FindBot(id)); return true; });

    private TradingBot FindBot(string id) =>
        _bots.FirstOrDefault(b => b.Id == id) ?? throw new InvalidOperationException($"봇 {id} 없음");

    // ================================================================ 이벤트 처리 (루프 스레드)

    private void HandleTrade(TradeTick t)
    {
        if (!_contexts.TryGetValue(t.Symbol, out var ctx)) return;
        var barClosed = ctx.OnTrade(t);
        _paper?.OnTrade(t);
        _recorder?.Record(t);
        foreach (var bot in _bots)
        {
            if (bot.Symbol != t.Symbol) continue;
            if (barClosed) bot.OnMarket(SignalTrigger.BarClosed);
            bot.OnMarket(SignalTrigger.Trade);
        }
    }

    private void HandleBook(OrderBookSnapshot b)
    {
        if (_contexts.TryGetValue(b.Symbol, out var ctx)) ctx.OnOrderBook(b);
        _paper?.OnOrderBook(b);
        _recorder?.Record(b);
    }

    private void HandleConnection(bool connected, string message)
    {
        _feedConnected = connected;
        _log.Write(connected ? LogLevel.Info : LogLevel.Warn, "시세", message);
        if (!connected)
        {
            _wasDisconnected = true;
            foreach (var b in _bots) b.Suspend("시세 연결 끊김");
            return;
        }
        if (_wasDisconnected)
        {
            _wasDisconnected = false;
            _ = ReconcileAsync();
        }
    }

    /// <summary>재연결 후 계좌 기준 재동기화 (설계 문서 3.5, 7.3)</summary>
    private async Task ReconcileAsync()
    {
        try
        {
            var snap = await _broker.GetAccountSnapshotAsync(_cts?.Token ?? default).ConfigureAwait(false);
            Post(() =>
            {
                _account = snap;
                foreach (var bot in _bots)
                {
                    if (bot.HasWorkingOrders) continue;
                    var h = snap.Holdings.FirstOrDefault(x => x.Symbol == bot.Symbol);
                    if (bot.HasPosition || h is not null) bot.Reconcile(h?.Quantity ?? 0, h?.AveragePrice ?? 0);
                }
                foreach (var b in _bots) b.Resume();
                _log.Write(LogLevel.Info, "엔진", "재동기화 완료 → 봇 재개");
            });
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "엔진", $"재동기화 실패 (봇 일시중단 유지): {ex.Message}");
        }
    }

    private void HandleOrderUpdate(OrderUpdate u)
    {
        if (!_byOrderId.TryGetValue(u.OrderId, out var tracked))
        {
            // 주문 응답(ack)보다 체결 이벤트가 먼저 올 수 있다 → 잠시 보관
            if (!_unmatched.TryGetValue(u.OrderId, out var list))
                _unmatched[u.OrderId] = list = (_clock.Now, new List<OrderUpdate>());
            list.Updates.Add(u);
            return;
        }
        ApplyUpdate(tracked, u);
    }

    private void ApplyUpdate(TrackedOrder tracked, OrderUpdate u)
    {
        var bot = _bots.FirstOrDefault(b => b.Id == tracked.BotId);
        tracked.PerOrder.TryGetValue(u.OrderId, out var prev);
        var delta = u.FilledQuantity - prev.Filled;
        if (delta > 0)
        {
            var value = u.FilledQuantity * (u.AverageFilledPrice ?? u.Price ?? 0);
            var price = (value - prev.Value) / delta;
            if (price <= 0) price = u.AverageFilledPrice ?? u.Price ?? 0;
            tracked.PerOrder[u.OrderId] = (u.FilledQuantity, value);
            if (tracked.Side == OrderSide.Buy) tracked.ReservedAmount = Math.Max(0, tracked.ReservedAmount - delta * price);
            bot?.OnFill(tracked.ClientOrderId, tracked.Side, delta, price);
        }
        else if (!tracked.PerOrder.ContainsKey(u.OrderId))
        {
            tracked.PerOrder[u.OrderId] = (0, 0);
        }

        if (u.Status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected
            && u.OrderId == tracked.CurrentOrderId && !tracked.Done)
        {
            tracked.Done = true;
            tracked.ReservedAmount = 0;
            if (u.Status == OrderStatus.Rejected) _log.Write(LogLevel.Error, tracked.BotId, $"주문 거부: {u.Message}");
            bot?.OnOrderDone(tracked.ClientOrderId, u.Status, u.Message);
        }
    }

    private void HandleJobResult(OrderJobResult r)
    {
        if (!_byClient.TryGetValue(r.Job.ClientOrderId, out var tracked)) return;
        var bot = _bots.FirstOrDefault(b => b.Id == tracked.BotId);

        if (r.Ack is not null)
        {
            if (r.Job.Kind is OrderJobKind.Place or OrderJobKind.Modify && r.Ack.OrderId != tracked.CurrentOrderId)
            {
                tracked.CurrentOrderId = r.Ack.OrderId;
                _byOrderId[r.Ack.OrderId] = tracked;
                if (_unmatched.Remove(r.Ack.OrderId, out var pending))
                    foreach (var u in pending.Updates) ApplyUpdate(tracked, u);
            }
            return;
        }

        switch (r.Job.Kind)
        {
            case OrderJobKind.Place:
                tracked.Done = true;
                tracked.ReservedAmount = 0;
                if (r.ErrorCode == "canceled-before-send") bot?.OnOrderDone(tracked.ClientOrderId, OrderStatus.Canceled, r.Error);
                else
                {
                    _log.Write(LogLevel.Error, tracked.BotId, $"주문 실패 [{r.ErrorCode}] {r.Error}");
                    bot?.OnOrderSubmitFailed(tracked.ClientOrderId, r.Error ?? "주문 실패");
                }
                break;
            case OrderJobKind.Modify:
                _log.Write(LogLevel.Warn, tracked.BotId, $"정정 실패 [{r.ErrorCode}] {r.Error}");
                if (!tracked.Done && r.ErrorCode is not ("already-filled" or "already-canceled"))
                    _orders.Enqueue(new OrderJob(OrderJobKind.Cancel, tracked.ClientOrderId, OrderPriority.CancelOrModify));
                break;
            case OrderJobKind.Cancel:
                if (r.ErrorCode is not ("already-filled" or "already-canceled"))
                    _log.Write(LogLevel.Warn, tracked.BotId, $"취소 실패 [{r.ErrorCode}] {r.Error}");
                break;
        }
    }

    private void HandleCandidates(IReadOnlyList<ScanCandidate> list)
    {
        _candidates = list;
        foreach (var c in list)
            if (_contexts.TryGetValue(c.Symbol, out var ctx) && ctx.Name == ctx.Symbol) ctx.Name = c.Name;
        RecomputeSubscriptions();
    }

    private void OnTimer()
    {
        var now = _clock.Now;

        // 날짜가 바뀌면(엔진을 밤새 켜둔 경우) 계좌 레벨 일일 한도를 새로 시작
        var today = Kst.DateOf(now);
        if (_tradingDate != default && today != _tradingDate)
        {
            _tradingDate = today;
            _risk.ResetDaily(_options.CapitalOverride > 0 ? _options.CapitalOverride : _account.Equity);
            _log.Write(LogLevel.Info, "엔진", $"새 거래일 {today:yyyy-MM-dd}: 일일 손익·한도 초기화");

            // 종목별 당일 통계(VWAP·고저·분봉)도 새로 시작하고, 상하한가·전일종가를 다시 채운다
            foreach (var ctx in _contexts.Values) ctx.ResetSession();
            _seeded.Clear();
            _liveMetrics.Clear();
            _candidates = Array.Empty<ScanCandidate>();
            RecomputeSubscriptions();
        }

        if (_store is not null && (now - _lastSave).TotalSeconds >= 2)
        {
            _lastSave = now;
            SaveState();
        }

        foreach (var ctx in _contexts.Values)
        {
            if (!ctx.OnTimer(now)) continue;
            foreach (var bot in _bots)
                if (bot.Symbol == ctx.Symbol) bot.OnMarket(SignalTrigger.BarClosed);
        }

        if ((now - _lastSecond).TotalMilliseconds >= 1000)
        {
            _lastSecond = now;
            foreach (var bot in _bots) bot.OnTimer();
            _autoPilot.OnTimer();
            var followBefore = _analytics.ActiveFollowUps;
            _analytics.OnTimer(now, sym => _contexts.TryGetValue(sym, out var c) ? c : null);
            if (_analytics.ActiveFollowUps != followBefore) RecomputeSubscriptions();

            var unrealized = _bots.Sum(b => b.UnrealizedNet);
            if (_risk.CheckDailyLimits(unrealized))
            {
                _log.Write(LogLevel.Error, "리스크", $"일 손실 한도 {_risk.Settings.DailyLossLimitPct}% 도달 → 신규 진입 중지");
                if (_risk.Settings.FlattenOnDailyLossLimit)
                    foreach (var b in _bots) b.Flatten("일 손실 한도", emergency: false, ExitKind.RiskLimit);
            }

            foreach (var key in _unmatched.Where(kv => now - kv.Value.At > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
                _unmatched.Remove(key);
        }

        var refresh = _options.Execution == ExecutionMode.Live ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
        if (now - _lastAccountRefresh >= refresh || now < _lastAccountRefresh)
        {
            _lastAccountRefresh = now;
            _ = RefreshAccountAsync();
        }

        UpdateLiveMetrics();
        PublishSnapshot(now);
    }

    private async Task RefreshAccountAsync()
    {
        Interlocked.Increment(ref _pendingAsync);
        try { await RefreshAccountCoreAsync().ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _pendingAsync); }
    }

    private async Task RefreshAccountCoreAsync()
    {
        try
        {
            var snap = await _broker.GetAccountSnapshotAsync(_cts?.Token ?? default).ConfigureAwait(false);
            Post(() => _account = snap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write(LogLevel.Warn, "계좌", $"계좌 조회 실패: {ex.Message}");
        }
    }

    // ================================================================ 저장 / 복원

    private void SaveState() => _store?.Save(_bots.Select(b => b.Capture()).ToList(), _paper?.CaptureState());

    /// <summary>
    /// 저장된 봇 복원. 같은 날이면 전부, 날짜가 바뀌었으면 보유 포지션이 남은 봇(익일 보유)만 되살린다.
    /// 실전 모드는 실제 계좌 잔고와 대조해 수량을 맞춘다.
    /// </summary>
    private void RestoreBots(IReadOnlyList<BotPersistState> saved, AccountSnapshot snap)
    {
        var today = Kst.DateOf(_clock.Now);
        foreach (var st in saved)
        {
            if (st.SavedDate != today && st.Quantity <= 0) continue;
            if (_bots.Any(b => b.Id == st.Id)) continue;
            var ctx = EnsureContext(st.Symbol, st.Name, full: true);
            var bot = TradingBot.Restore(st, ctx, this);
            if (_options.Execution == ExecutionMode.Live && bot.HasPosition)
            {
                var h = snap.Holdings.FirstOrDefault(x => x.Symbol == st.Symbol);
                bot.Reconcile(h?.Quantity is { } q ? Math.Min(q, bot.Quantity) : 0, h?.AveragePrice ?? 0);
            }
            _bots.Add(bot);
            if (int.TryParse(st.Id.Split('#').LastOrDefault(), out var n)) _botSeq = Math.Max(_botSeq, n);
            _log.Write(LogLevel.Info, bot.Id, $"봇 복원: {ctx.Name} {(bot.HasPosition ? $"보유 {bot.Quantity:N0}주 @ {bot.AveragePrice:N0}" : bot.State.ToKorean())}");
        }
        if (snap.OpenOrders.Count > 0)
            _log.Write(LogLevel.Warn, "엔진", $"계좌에 미체결 주문 {snap.OpenOrders.Count}건이 있습니다. 이 프로그램이 관리하지 않으므로 필요하면 직접 정리하세요.");
        _focus ??= _bots.FirstOrDefault()?.Symbol;
        RecomputeSubscriptions();
    }

    // ================================================================ 구독/컨텍스트

    private SymbolContext EnsureContext(string symbol, string? name, bool full)
    {
        if (!_contexts.TryGetValue(symbol, out var ctx))
        {
            var cand = _candidates.FirstOrDefault(c => c.Symbol == symbol);
            ctx = new SymbolContext(symbol, name ?? cand?.Name ?? _scanner.NameOf(symbol) ?? symbol);
            _contexts[symbol] = ctx;
            _paper?.SetName(symbol, ctx.Name);
        }
        else if (!string.IsNullOrEmpty(name))
        {
            ctx.Name = name;
        }
        // 스캐너 후보는 분봉(VWAP 복원)만, 봇 종목은 상하한가·호가·전일종가·종목명까지 보강 (호출 한도 절약)
        var seededFull = _seeded.TryGetValue(symbol, out var f) ? f : (bool?)null;
        if (seededFull is null || (full && seededFull == false))
        {
            _seeded[symbol] = full;
            _ = SeedAsync(symbol, includeBars: seededFull is null, full);
        }
        return ctx;
    }

    /// <summary>늦게 시작한 종목의 당일 분봉 복원 (+ 봇 종목은 상하한가·호가·전일종가·종목명)</summary>
    private async Task SeedAsync(string symbol, bool includeBars, bool full)
    {
        Interlocked.Increment(ref _pendingAsync);
        try { await SeedCoreAsync(symbol, includeBars, full).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _pendingAsync); }
    }

    private async Task SeedCoreAsync(string symbol, bool includeBars, bool full)
    {
        var ct = _cts?.Token ?? default;
        try
        {
            var bars = includeBars ? await _source.GetTodayMinuteBarsAsync(symbol, ct).ConfigureAwait(false) : Array.Empty<Bar>();
            PriceLimits? limits = null;
            OrderBookSnapshot? book = null;
            IReadOnlyList<Bar> daily = Array.Empty<Bar>();
            StockInfo? info = null;
            if (full)
            {
                limits = await _source.GetPriceLimitsAsync(symbol, ct).ConfigureAwait(false);
                book = await _source.GetOrderBookAsync(symbol, ct).ConfigureAwait(false);
                daily = await _source.GetDailyBarsAsync(symbol, 1, ct).ConfigureAwait(false);
                info = (await _source.GetStocksAsync(new[] { symbol }, ct).ConfigureAwait(false)).FirstOrDefault();
            }
            Post(() =>
            {
                if (!_contexts.TryGetValue(symbol, out var ctx)) return;
                if (bars.Count > 0) ctx.Seed(bars);
                if (limits is not null) ctx.Limits = limits;
                if (book is not null && ctx.OrderBook is null) ctx.OnOrderBook(book);
                if (daily.Count > 0) ctx.PreviousClose ??= daily[^1].Close;
                if (info is not null && ctx.Name == symbol) { ctx.Name = info.Name; _paper?.SetName(symbol, info.Name); }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write(LogLevel.Warn, symbol, $"초기 데이터 조회 실패: {ex.Message}");
            Post(() => _seeded.Remove(symbol));
        }
    }

    private void RecomputeSubscriptions()
    {
        var botSymbols = _bots.Where(b => !b.State.IsFinished() || b.HasPosition).Select(b => b.Symbol).ToHashSet();
        var trades = new HashSet<string>(botSymbols);
        var books = new HashSet<string>(botSymbols);
        var budget = _options.MaxTopics - trades.Count - books.Count;

        // 청산·신호 이후 가격 추적 중인 종목은 체결만 유지 (분석용)
        foreach (var s in _analytics.FollowUpSymbols)
        {
            if (budget <= 0) break;
            if (trades.Add(s)) budget--;
        }

        foreach (var c in _candidates.Take(_options.Scanner.LiveSubscribeTop))
        {
            if (budget <= 0) break;
            if (trades.Add(c.Symbol)) budget--;
        }
        foreach (var c in _candidates.Take(10))
        {
            if (budget <= 0) break;
            if (books.Add(c.Symbol)) budget--;
        }

        foreach (var s in trades) EnsureContext(s, null, full: botSymbols.Contains(s));
        foreach (var s in _contexts.Keys.Where(k => !trades.Contains(k)).ToList())
        {
            if (_bots.Any(b => b.Symbol == s)) continue;
            _contexts.Remove(s);
            _seeded.Remove(s);
            _liveMetrics.TryRemove(s, out _);
        }

        if (trades.SetEquals(_subs.Trades) && books.SetEquals(_subs.Books)) return;
        _subs = (trades, books);
        var ct = _cts?.Token ?? default;
        Interlocked.Increment(ref _pendingAsync);
        _ = Task.Run(async () =>
        {
            try { await _feed.SetSubscriptionsAsync(trades, books, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.Write(LogLevel.Warn, "시세", $"구독 변경 실패: {ex.Message}"); }
            finally { Interlocked.Decrement(ref _pendingAsync); }
        }, ct);
    }

    private void UpdateLiveMetrics()
    {
        foreach (var ctx in _contexts.Values)
        {
            if (!ctx.HasData) continue;
            _liveMetrics[ctx.Symbol] = new LiveMetrics(ctx.LastPrice, ctx.Vwap, ctx.Strength, ctx.OrderBook?.SpreadTicks(ctx.Market), ctx.RangePosition);
        }
    }

    // ================================================================ IBotHost

    public DateTimeOffset Now => _clock.Now;
    public CostModel Cost => _cost;
    public ExecutionMode Execution => _options.Execution;
    public DataSourceKind DataSource => _options.DataSource;
    public int MaxOrderErrorsPerBot => _risk.Settings.MaxOrderErrorsPerBot;

    public string SubmitOrder(TradingBot bot, OrderSide side, OrderType type, decimal quantity, decimal? price, OrderPriority priority, string reason)
    {
        var id = OrderRequest.NewClientOrderId();
        var tracked = new TrackedOrder
        {
            ClientOrderId = id, BotId = bot.Id, Symbol = bot.Symbol, Side = side,
            ReservedAmount = side == OrderSide.Buy ? quantity * (price ?? bot.Context.LastPrice) : 0,
        };
        _byClient[id] = tracked;
        _orders.Enqueue(new OrderJob(OrderJobKind.Place, id, priority,
            new OrderRequest(id, bot.Symbol, side, type, quantity, type == OrderType.Limit ? price : null, priority, bot.Id, reason)));
        return id;
    }

    public void ModifyOrder(TradingBot bot, string clientOrderId, OrderType type, decimal quantity, decimal? price) =>
        _orders.Enqueue(new OrderJob(OrderJobKind.Modify, clientOrderId, OrderPriority.CancelOrModify,
            ModifyType: type, ModifyQuantity: quantity, ModifyPrice: type == OrderType.Limit ? price : null));

    public void CancelOrder(TradingBot bot, string clientOrderId) =>
        _orders.Enqueue(new OrderJob(OrderJobKind.Cancel, clientOrderId, OrderPriority.CancelOrModify));

    public (bool Allowed, string? Reason) CanEnter(TradingBot bot, decimal amount)
    {
        var open = _bots.Count(b => b != bot && (b.HasPosition || b.State == BotState.EntryPending));
        var exposure = _bots.Where(b => b != bot).Sum(b => b.Exposure);
        return _risk.CanEnter(Now, amount, open, exposure, _bots.Sum(b => b.UnrealizedNet));
    }

    public decimal SizeFor(TradingBot bot, decimal entryPrice, decimal stopPrice)
    {
        var equity = _risk.StartEquity > 0 ? _risk.StartEquity : _account.Equity;
        var reserved = _byClient.Values.Where(t => !t.Done && t.Side == OrderSide.Buy).Sum(t => t.ReservedAmount);
        var buyingPower = Math.Max(0, _account.Cash - reserved) / (1 + _cost.CommissionRate);
        return PositionSizer.Quantity(bot.Settings, equity, entryPrice, stopPrice, buyingPower, _risk.Settings.MaxOrderAmount);
    }

    public void OnTradeClosed(TradingBot bot, ClosedTrade trade, TradeAnalysisRecord analysis)
    {
        _closed.Add(trade);
        _journal.Append(trade);
        _analytics.WriteTrade(analysis);
        _analytics.StartFollowUp(analysis.TradeId, "exit", bot.Symbol, Now, analysis.AverageExit, analysis.AverageEntry);
        _risk.OnTradeClosed(trade, Now);
        RecomputeSubscriptions();
    }

    public void OnSignal(TradingBot bot, SignalRecord signal)
    {
        _analytics.WriteSignal(signal);
        if (signal.Decision != "진입") _analytics.StartFollowUp(signal.SignalId, "signal", bot.Symbol, signal.Time, signal.Price, null);
    }

    public void OnSignalDecision(TradingBot bot, SignalDecisionRecord decision) => _analytics.WriteDecision(decision);

    public void Log(LogLevel level, string source, string message) => _log.Write(level, source, message);

    private int OrderRateLimit()
    {
        var t = Kst.TimeOf(_clock.Now);
        return t >= Kst.MarketOpen && t < new TimeOnly(9, 10) ? _options.OpeningOrderRatePerSecond : _options.OrderRatePerSecond;
    }

    // ================================================================ 스냅샷

    private void PublishSnapshot(DateTimeOffset now)
    {
        var bots = _bots.Select(b =>
        {
            var target = b.TargetAmount;
            var cost = b.AveragePrice * b.Quantity;
            return new BotView(
                b.Id, b.Symbol, b.Context.Name, EntrySignalFactory.DisplayName(b.Settings.Strategy), b.Settings.Mode,
                b.State, $"{b.State.ToKorean()} · {b.StateReason}", b.Quantity, b.AveragePrice, b.Context.LastPrice, b.StopPrice,
                b.UnrealizedNet, cost > 0 ? b.UnrealizedNet / cost * 100m : 0, b.RealizedNet, target,
                target > 0 ? Math.Clamp(b.RealizedNet / target, -1, 1) : 0,
                b.Entries, b.Settings.MaxEntries, b.Wins, b.Losses, b.PendingSignalText, b.Settings.Clone(), b.AutoRole);
        }).ToList();

        var unrealized = _bots.Sum(b => b.UnrealizedNet);
        var exposure = _bots.Sum(b => b.Exposure);
        var account = new AccountView(_account.Equity, _account.Cash, _risk.StartEquity, _risk.RealizedToday, unrealized, exposure);
        var risk = new RiskView(_risk.DailyPnlPct(unrealized), _risk.Settings.DailyLossLimitPct,
            _bots.Count(b => b.HasPosition), _risk.Settings.MaxConcurrentPositions,
            _risk.BlockReason(now, unrealized) is not null, _risk.BlockReason(now, unrealized), _risk.KillSwitchActive);

        _snapshot = new EngineSnapshot(now, true, _feedConnected, _status, _options.Execution, _options.DataSource,
            account, risk, bots, _candidates, _closed.ToList(), _log.Recent(), BuildChart(),
            _autoPilot.View(_scanOverride ?? _scannerBase.Mode));
    }

    private ChartView? BuildChart()
    {
        if (_focus is null || !_contexts.TryGetValue(_focus, out var ctx) || !ctx.HasData) return null;
        var all = ctx.AllBars().ToList();
        decimal cv = 0, cvv = 0;
        var vwap = new List<decimal>(all.Count);
        foreach (var b in all)
        {
            cv += b.Volume;
            cvv += b.Value > 0 ? b.Value : b.TypicalPrice * b.Volume;
            vwap.Add(cv > 0 ? cvv / cv : b.Close);
        }
        const int keep = 150;
        var skip = Math.Max(0, all.Count - keep);
        var lines = new List<ChartLine>();
        var (hi, lo, complete) = ctx.OpeningRange(5, _clock.Now);
        if (complete) { lines.Add(new ChartLine(hi, "OR 고가", "or")); lines.Add(new ChartLine(lo, "OR 저가", "or")); }
        foreach (var bot in _bots.Where(b => b.Symbol == _focus && b.HasPosition))
        {
            lines.Add(new ChartLine(bot.AveragePrice, "평균단가", "entry"));
            if (bot.StopPrice is { } sp) lines.Add(new ChartLine(sp, "손절", "stop"));
        }
        return new ChartView(ctx.Symbol, ctx.Name, all.Skip(skip).Select(b => b.Clone()).ToList(), vwap.Skip(skip).ToList(), lines);
    }
}
