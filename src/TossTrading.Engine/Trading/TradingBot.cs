using TossTrading.Domain;
using TossTrading.Engine.Market;
using TossTrading.Engine.Strategies;

namespace TossTrading.Engine.Trading;

/// <summary>봇이 엔진에 요청하는 기능 (주문, 리스크 확인, 사이징, 기록)</summary>
public interface IBotHost
{
    DateTimeOffset Now { get; }
    CostModel Cost { get; }
    ExecutionMode Execution { get; }
    int MaxOrderErrorsPerBot { get; }

    /// <summary>주문 제출 → clientOrderId 반환. 결과는 OnFill/OnOrderDone 으로 비동기 통지.</summary>
    string SubmitOrder(TradingBot bot, OrderSide side, OrderType type, decimal quantity, decimal? price, OrderPriority priority, string reason);

    void ModifyOrder(TradingBot bot, string clientOrderId, OrderType type, decimal quantity, decimal? price);
    void CancelOrder(TradingBot bot, string clientOrderId);

    (bool Allowed, string? Reason) CanEnter(TradingBot bot, decimal amount);
    decimal SizeFor(TradingBot bot, decimal entryPrice, decimal stopPrice);

    void OnTradeClosed(TradingBot bot, ClosedTrade trade);
    void Log(LogLevel level, string source, string message);
}

/// <summary>봇 저장 상태 (JSON)</summary>
public sealed record BotPersistState(
    string Id, string Symbol, string Name, BotSettings Settings, BotState State, string StateReason,
    decimal Quantity, decimal AveragePrice, decimal InitialStop, decimal? StopPrice, decimal PeakPrice,
    DateTimeOffset EntryTime, bool PartialTaken, string StopKind,
    decimal RealizedNet, int Entries, int Wins, int Losses,
    decimal TradeBuyQty, decimal TradeBuyValue, decimal TradeSellQty, decimal TradeSellValue, decimal TradeNet, string EntryReason,
    DateOnly SavedDate);

/// <summary>
/// 종목 1개를 담당하는 봇. 상태 머신 (설계 문서 8.5) + 청산 규칙 (6.2).
/// 엔진 이벤트 루프에서만 호출된다 (단일 스레드).
/// </summary>
public sealed class TradingBot
{
    private sealed class WorkingOrder
    {
        public required string ClientOrderId { get; init; }
        public required OrderSide Side { get; init; }
        public required decimal Quantity { get; set; }
        public required DateTimeOffset SubmittedAt { get; set; }
        public required string Reason { get; init; }
        public decimal Filled { get; set; }
        public bool CancelRequested { get; set; }
        public bool Escalated { get; set; }
        public decimal Remaining => Quantity - Filled;
    }

    private readonly IBotHost _host;
    private IEntrySignal? _signal;
    private WorkingOrder? _entryOrder;
    private WorkingOrder? _exitOrder;
    private EntrySignal? _pendingSignal;
    private decimal _plannedStop;
    private string _entryReason = "";
    private string _exitReason = "";
    private DateTimeOffset _cooldownUntil;
    private bool _stopAfterFlat;
    private bool _haltAfterFlat;
    private BotState _stateBeforeSuspend;
    private string _stopKind = "손절";

    // 현재 거래(사이클) 누적
    private decimal _tradeBuyQty, _tradeBuyValue, _tradeSellQty, _tradeSellValue, _tradeNet;

    public TradingBot(string id, SymbolContext context, BotSettings settings, IBotHost host)
    {
        Id = id;
        Context = context;
        Settings = settings.Clone();
        _host = host;
        _signal = EntrySignalFactory.Create(Settings.Strategy);
    }

    public string Id { get; }
    public SymbolContext Context { get; }
    public string Symbol => Context.Symbol;
    public BotSettings Settings { get; private set; }
    public BotState State { get; private set; } = BotState.Idle;
    public string StateReason { get; private set; } = "";

    // 포지션
    public decimal Quantity { get; private set; }
    public decimal AveragePrice { get; private set; }
    public decimal? StopPrice { get; private set; }
    public decimal InitialStop { get; private set; }
    public decimal PeakPrice { get; private set; }
    public DateTimeOffset EntryTime { get; private set; }
    public bool PartialTaken { get; private set; }

    // 통계
    public decimal RealizedNet { get; private set; }
    public int Entries { get; private set; }
    public int Wins { get; private set; }
    public int Losses { get; private set; }
    public int OrderErrors { get; private set; }

    public decimal R => Math.Max(AveragePrice - InitialStop, TickRules.TickSize(AveragePrice, Context.Market));
    public bool HasPosition => Quantity > 0;
    public bool HasWorkingOrders => _entryOrder is not null || _exitOrder is not null;
    public string? PendingSignalText => _pendingSignal?.Reason;
    public decimal TargetAmount => Settings.MaxPositionAmount * Settings.BotTargetProfitPct / 100m;

    /// <summary>보유 + 미체결 매수 금액 (리스크 노출 계산용)</summary>
    public decimal Exposure => Quantity * (Context.LastPrice > 0 ? Context.LastPrice : AveragePrice)
                               + (_entryOrder?.Remaining ?? 0) * Context.LastPrice;

    public decimal UnrealizedNet
    {
        get
        {
            if (Quantity <= 0 || Context.LastPrice <= 0) return 0;
            return _host.Cost.NetPnl(AveragePrice, Context.LastPrice, Quantity);
        }
    }

    // ================================================================ 저장 / 복원 (익일 보유 대비)

    /// <summary>재시작 후 이어서 관리하기 위한 봇 상태 (미체결 주문은 저장하지 않는다)</summary>
    public BotPersistState Capture() => new(
        Id, Symbol, Context.Name, Settings.Clone(), State, StateReason,
        Quantity, AveragePrice, InitialStop, StopPrice, PeakPrice, EntryTime, PartialTaken, _stopKind,
        RealizedNet, Entries, Wins, Losses,
        _tradeBuyQty, _tradeBuyValue, _tradeSellQty, _tradeSellValue, _tradeNet, _entryReason,
        Kst.DateOf(_host.Now));

    /// <summary>
    /// 저장된 상태로 봇을 되살린다. 날짜가 바뀌었으면 당일 통계(실현손익·진입 횟수·승패)는 새로 시작하고,
    /// 보유 포지션과 진행 중인 거래 기록은 유지한다. 보유분이 있으면 즉시 청산 관리(InPosition)로 시작한다.
    /// </summary>
    public static TradingBot Restore(BotPersistState st, SymbolContext context, IBotHost host)
    {
        var bot = new TradingBot(st.Id, context, st.Settings, host)
        {
            Quantity = st.Quantity,
            AveragePrice = st.AveragePrice,
            InitialStop = st.InitialStop,
            StopPrice = st.StopPrice,
            PeakPrice = st.PeakPrice,
            EntryTime = st.EntryTime,
            PartialTaken = st.PartialTaken,
        };
        bot._stopKind = st.StopKind;
        bot._tradeBuyQty = st.TradeBuyQty;
        bot._tradeBuyValue = st.TradeBuyValue;
        bot._tradeSellQty = st.TradeSellQty;
        bot._tradeSellValue = st.TradeSellValue;
        bot._tradeNet = st.TradeNet;
        bot._entryReason = st.EntryReason;

        var sameDay = st.SavedDate == Kst.DateOf(host.Now);
        if (sameDay)
        {
            bot.RealizedNet = st.RealizedNet;
            bot.Entries = st.Entries;
            bot.Wins = st.Wins;
            bot.Losses = st.Losses;
        }
        else
        {
            bot.Entries = bot.HasPosition ? 1 : 0;
        }

        if (bot.HasPosition) bot.SetState(BotState.InPosition, sameDay ? "재시작 복원" : "익일 보유 복원");
        else if (sameDay && st.State.IsFinished()) bot.SetState(st.State, st.StateReason);
        else bot.SetState(BotState.Idle, "재시작 복원 — [시작]을 누르세요");
        return bot;
    }

    // ================================================================ 명령

    public void Start()
    {
        if (State is BotState.Idle or BotState.Stopped or BotState.Completed or BotState.Halted)
        {
            _stopAfterFlat = false;
            _haltAfterFlat = false;
            SetState(HasPosition ? BotState.InPosition : BotState.Watching, "시작");
        }
    }

    public void UpdateSettings(BotSettings settings)
    {
        var strategyChanged = settings.Strategy != Settings.Strategy;
        Settings = settings.Clone();
        if (strategyChanged) _signal = EntrySignalFactory.Create(Settings.Strategy);
        _host.Log(LogLevel.Info, Id, "설정 변경");
    }

    /// <summary>수동 매수 (운용 모드 A). 손절 = 설정 %.</summary>
    public bool ManualBuy()
    {
        if (State is not (BotState.Watching or BotState.SignalPending or BotState.Idle or BotState.Cooldown))
        {
            _host.Log(LogLevel.Warn, Id, $"수동 매수 불가 상태: {State.ToKorean()}");
            return false;
        }
        if (State == BotState.Idle) SetState(BotState.Watching, "수동 시작");
        return TryEnter(null, "수동 매수");
    }

    /// <summary>반자동 신호 승인</summary>
    public bool ApproveSignal()
    {
        if (State != BotState.SignalPending || _pendingSignal is null) return false;
        var sig = _pendingSignal;
        _pendingSignal = null;
        SetState(BotState.Watching, "승인");
        return TryEnter(sig, sig.Reason);
    }

    public void RejectSignal()
    {
        if (State != BotState.SignalPending) return;
        _pendingSignal = null;
        SetState(BotState.Watching, "신호 거절");
    }

    /// <summary>보유분 즉시 청산 (수동 청산 / 킬스위치)</summary>
    public void Flatten(string reason, bool emergency)
    {
        CancelEntryOrder();
        _pendingSignal = null;
        if (Quantity <= 0)
        {
            if (!State.IsFinished() && State != BotState.EntryPending) SetState(BotState.Watching, reason);
            return;
        }
        if (_exitOrder is not null)
        {
            if (!_exitOrder.Escalated) EscalateExit(reason);
            return;
        }
        SubmitExit(Quantity, reason, emergency ? OrderPriority.Emergency : OrderPriority.Exit, market: emergency);
    }

    /// <summary>킬스위치: 미체결 취소 + 보유분 시장가 청산 + 정지</summary>
    public void Kill()
    {
        _stopAfterFlat = true;
        _pendingSignal = null;
        CancelEntryOrder();
        if (HasPosition) Flatten("킬스위치", emergency: true);
        else if (_entryOrder is null) SetState(BotState.Stopped, "킬스위치");
    }

    /// <summary>봇 정지. 보유분이 있으면 청산 후 정지.</summary>
    public void Stop(bool flatten)
    {
        _pendingSignal = null;
        CancelEntryOrder();
        if (HasPosition && flatten)
        {
            _stopAfterFlat = true;
            Flatten("봇 정지", emergency: false);
            return;
        }
        if (HasPosition)
        {
            // 청산 없이 정지: 포지션 관리를 멈추지 않도록 신규 진입만 막는다
            _stopAfterFlat = true;
            _host.Log(LogLevel.Warn, Id, "보유분이 있어 청산 관리는 계속하고 신규 진입만 중단합니다.");
            return;
        }
        SetState(BotState.Stopped, "사용자 정지");
    }

    public void Suspend(string reason)
    {
        if (State is BotState.Suspended || State.IsFinished() || State == BotState.Idle) return;
        _stateBeforeSuspend = State;
        SetState(BotState.Suspended, reason);
    }

    public void Resume()
    {
        if (State != BotState.Suspended) return;
        var next = HasPosition ? BotState.InPosition
            : _stateBeforeSuspend is BotState.EntryPending or BotState.ExitPending or BotState.InPosition ? BotState.Watching
            : _stateBeforeSuspend;
        SetState(next, "재개");
    }

    /// <summary>재동기화: 브로커 잔고 기준으로 수량을 맞춘다.</summary>
    public void Reconcile(decimal brokerQuantity, decimal brokerAveragePrice)
    {
        if (brokerQuantity == Quantity) return;
        _host.Log(LogLevel.Warn, Id, $"잔고 불일치 보정: 봇 {Quantity} → 계좌 {brokerQuantity}");
        Quantity = brokerQuantity;
        if (brokerQuantity > 0 && brokerAveragePrice > 0) AveragePrice = brokerAveragePrice;
        if (Quantity <= 0 && State == BotState.InPosition) SetState(BotState.Watching, "재동기화");
    }

    // ================================================================ 시장 이벤트

    public void OnMarket(SignalTrigger trigger)
    {
        var now = _host.Now;
        switch (State)
        {
            case BotState.Watching:
                if (Settings.Mode != BotMode.ManualEntry) EvaluateEntry(now, trigger);
                else _signal?.Evaluate(Context, now, Settings, trigger); // 상태 추적만
                break;
            case BotState.SignalPending:
                _signal?.Evaluate(Context, now, Settings, trigger);
                break;
            case BotState.InPosition:
            case BotState.EntryPending when HasPosition:
                EvaluateExit(now);
                break;
            default:
                _signal?.Evaluate(Context, now, Settings, trigger);
                break;
        }
    }

    /// <summary>1초 주기 타이머: 주문 타임아웃, 시간 기반 청산, 쿨다운, 신호 만료.</summary>
    public void OnTimer()
    {
        var now = _host.Now;

        if (_entryOrder is { CancelRequested: false } eo && (now - eo.SubmittedAt).TotalSeconds >= Settings.EntryTimeoutSeconds)
        {
            eo.CancelRequested = true;
            _host.CancelOrder(this, eo.ClientOrderId);
            _host.Log(LogLevel.Info, Id, $"매수 미체결 {Settings.EntryTimeoutSeconds}초 → 취소 (체결 {eo.Filled}/{eo.Quantity})");
        }

        if (_exitOrder is { Escalated: false } xo && (now - xo.SubmittedAt).TotalSeconds >= Settings.ExitTimeoutSeconds)
            EscalateExit("매도 미체결");

        switch (State)
        {
            case BotState.InPosition:
                EvaluateExit(now);
                break;
            case BotState.SignalPending when _pendingSignal is not null:
                if ((now - _pendingSignal.At).TotalSeconds > 60 || Context.LastPrice > _pendingSignal.TriggerPrice * 1.01m)
                {
                    _pendingSignal = null;
                    SetState(BotState.Watching, "신호 만료");
                }
                break;
            case BotState.Cooldown when now >= _cooldownUntil:
                SetState(BotState.Watching, "쿨다운 종료");
                break;
        }
    }

    // ================================================================ 주문 결과 (엔진이 호출)

    public void OnFill(string clientOrderId, OrderSide side, decimal qty, decimal price)
    {
        var now = _host.Now;
        if (side == OrderSide.Buy)
        {
            var newQty = Quantity + qty;
            AveragePrice = newQty > 0 ? (AveragePrice * Quantity + price * qty) / newQty : 0;
            var first = Quantity == 0;
            Quantity = newQty;
            _tradeBuyQty += qty;
            _tradeBuyValue += price * qty;
            if (_entryOrder?.ClientOrderId == clientOrderId) _entryOrder.Filled += qty;

            if (first)
            {
                EntryTime = now;
                InitialStop = Math.Min(_plannedStop, TickRules.AddTicks(AveragePrice, -1, Context.Market));
                StopPrice = InitialStop;
                _stopKind = "손절";
                PeakPrice = AveragePrice;
                PartialTaken = false;
                SetState(BotState.InPosition, "매수 체결");
            }
            _host.Log(LogLevel.Trade, Id, $"매수 체결 {qty:N0}주 @ {price:N0} → 보유 {Quantity:N0}주 평균 {AveragePrice:N0}");
        }
        else
        {
            var sellQty = Math.Min(qty, Quantity);
            var net = _host.Cost.NetPnl(AveragePrice, price, sellQty);
            _tradeNet += net;
            _tradeSellQty += sellQty;
            _tradeSellValue += price * sellQty;
            Quantity -= sellQty;
            if (_exitOrder?.ClientOrderId == clientOrderId) _exitOrder.Filled += qty;
            _host.Log(LogLevel.Trade, Id, $"매도 체결 {sellQty:N0}주 @ {price:N0} 순손익 {net:+#,0;-#,0;0} ({_exitReason})");
            if (Quantity <= 0) CloseTrade(now);
        }
    }

    public void OnOrderDone(string clientOrderId, OrderStatus status, string? message)
    {
        if (_entryOrder?.ClientOrderId == clientOrderId)
        {
            var filled = _entryOrder.Filled;
            _entryOrder = null;
            if (status == OrderStatus.Rejected) RegisterError($"매수 거부: {message}");
            if (filled <= 0 && !HasPosition)
            {
                Entries = Math.Max(0, Entries - 1); // 체결 없는 주문은 진입 횟수에서 제외
                if (_stopAfterFlat) { SetState(BotState.Stopped, "사용자 정지"); return; }
                if (!State.IsFinished()) SetState(BotState.Watching, status == OrderStatus.Rejected ? "매수 거부" : "매수 미체결 취소");
            }
            return;
        }

        if (_exitOrder?.ClientOrderId == clientOrderId)
        {
            _exitOrder = null;
            if (status == OrderStatus.Rejected) RegisterError($"매도 거부: {message}");
            if (HasPosition && State == BotState.ExitPending) SetState(BotState.InPosition, "매도 미완료 → 재시도");
        }
    }

    public void OnOrderSubmitFailed(string clientOrderId, string message) =>
        OnOrderDone(clientOrderId, OrderStatus.Rejected, message);

    // ================================================================ 내부 로직

    private void EvaluateEntry(DateTimeOffset now, SignalTrigger trigger)
    {
        var sig = _signal?.Evaluate(Context, now, Settings, trigger);
        if (sig is null) return;
        if (!EntryWindowOpen(now)) return;
        if (Entries >= Settings.MaxEntries || _stopAfterFlat || _haltAfterFlat) return;

        if (Settings.Mode == BotMode.SemiAuto)
        {
            _pendingSignal = sig;
            SetState(BotState.SignalPending, sig.Reason);
            _host.Log(LogLevel.Info, Id, $"진입 신호 (승인 필요): {sig.Reason}");
        }
        else
        {
            TryEnter(sig, sig.Reason);
        }
    }

    private bool EntryWindowOpen(DateTimeOffset now)
    {
        var t = Kst.TimeOf(now);
        return t >= Settings.EntryStartTime && t < Settings.EntryEndTime && t < LastEntryTime;
    }

    /// <summary>신규 진입 마지노선: 당일 청산 봇은 강제청산 시각, 익일 보유 봇은 종가 단일가 시작(15:20)</summary>
    private TimeOnly LastEntryTime => Settings.HoldOvernight ? BotSettings.MarketCloseAuction : Settings.ForceExitTime;

    /// <summary>KRX 정규장 접속매매 시간 (09:00~15:20). 익일 보유 포지션은 이 시간에만 청산 규칙을 적용한다
    /// (동시호가·NXT 시간외의 얇은 호가에서 손절이 걸리는 것을 막기 위해).</summary>
    private static bool InContinuousSession(DateTimeOffset now)
    {
        var t = Kst.TimeOf(now);
        return t >= Kst.MarketOpen && t < BotSettings.MarketCloseAuction;
    }

    /// <summary>진입한 날보다 뒤의 날짜인가 (익일 보유 중)</summary>
    public bool IsCarriedOver => HasPosition && Kst.DateOf(_host.Now) > Kst.DateOf(EntryTime);

    private bool TryEnter(EntrySignal? sig, string reason)
    {
        if (HasWorkingOrders) { _host.Log(LogLevel.Warn, Id, "미체결 주문이 있어 진입 보류"); return false; }
        if (Entries >= Settings.MaxEntries) { _host.Log(LogLevel.Warn, Id, "최대 진입 횟수 도달"); return false; }
        if (Context.LastPrice <= 0) { _host.Log(LogLevel.Warn, Id, "시세 없음 — 진입 불가"); return false; }
        if (Kst.TimeOf(_host.Now) >= LastEntryTime) { _host.Log(LogLevel.Warn, Id, $"{LastEntryTime:HH\\:mm} 이후 — 진입 불가"); return false; }

        var ask = Context.OrderBook?.BestAsk ?? Context.LastPrice;
        var limit = TickRules.AddTicks(ask, Settings.EntrySlippageTicks, Context.Market);
        limit = TickRules.Clamp(limit, Context.Limits?.Lower, Context.Limits?.Upper);

        var pctStop = TickRules.RoundDown(limit * (1 - Settings.StopLossPct / 100m), Context.Market);
        var stop = pctStop;
        if (Settings.UseStructuralStop && sig?.StructuralStop is { } s && s > 0 && s < limit)
        {
            var distPct = (limit - s) / limit * 100m;
            if (distPct >= 0.3m && distPct <= Settings.StopLossPct * 2m) stop = s;
        }

        var qty = _host.SizeFor(this, limit, stop);
        if (qty <= 0) { _host.Log(LogLevel.Warn, Id, "매수 가능 수량 0 (투입금/리스크/예수금 확인)"); return false; }

        var (allowed, why) = _host.CanEnter(this, qty * limit);
        if (!allowed) { _host.Log(LogLevel.Warn, Id, $"리스크 관리로 진입 차단: {why}"); return false; }

        _plannedStop = stop;
        _entryReason = reason;
        _tradeBuyQty = _tradeBuyValue = _tradeSellQty = _tradeSellValue = _tradeNet = 0;
        Entries++;
        var id = _host.SubmitOrder(this, OrderSide.Buy, OrderType.Limit, qty, limit, OrderPriority.Entry, reason);
        _entryOrder = new WorkingOrder { ClientOrderId = id, Side = OrderSide.Buy, Quantity = qty, SubmittedAt = _host.Now, Reason = reason };
        SetState(BotState.EntryPending, $"{qty:N0}주 @ {limit:N0} (손절 {stop:N0})");
        _host.Log(LogLevel.Info, Id, $"매수 주문 {qty:N0}주 @ {limit:N0}, 손절 {stop:N0} — {reason}");
        return true;
    }

    private void EvaluateExit(DateTimeOffset now)
    {
        if (!HasPosition) return;
        var p = Context.LastPrice;
        if (p <= 0) return;

        if (Settings.HoldOvernight)
        {
            if (!InContinuousSession(now))
            {
                if (_exitOrder is null && State == BotState.InPosition)
                    StateReason = Settings.NextDayExitMode == NextDayExitMode.AtOpen
                        ? "익일 보유 — 시초 매도 예정"
                        : $"익일 보유 — {Settings.NextDayExitTime:HH\\:mm}까지 관리";
                return;
            }
            // 장 시작 전 데이터(전일 종가)가 아니라 오늘 체결가로 판단해야 한다
            if (IsCarriedOver && Kst.DateOf(Context.LastTradeTime) < Kst.DateOf(now)) return;
            // 진입 당일: 익일 갭을 노리는 전략이므로 손절만 적용 (익절·트레일링·본절은 익일부터)
            if (!IsCarriedOver)
            {
                var entryDayStop = StopPrice ?? InitialStop;
                if (_exitOrder is null && p <= entryDayStop)
                    SubmitExit(Quantity, $"손절 ({entryDayStop:N0})", OrderPriority.Emergency, market: false);
                else if (_exitOrder is null && State == BotState.InPosition)
                    StateReason = "종가 보유 중 — 당일은 손절만 적용";
                return;
            }
            if (_exitOrder is null)
            {
                if (Settings.NextDayExitMode == NextDayExitMode.AtOpen)
                {
                    SubmitExit(Quantity, "익일 시초 매도", OrderPriority.Exit, market: true);
                    return;
                }
                if (Kst.TimeOf(now) >= Settings.NextDayExitTime)
                {
                    SubmitExit(Quantity, $"익일 청산 시각 {Settings.NextDayExitTime:HH\\:mm}", OrderPriority.Exit, market: false);
                    return;
                }
            }
        }

        if (p > PeakPrice) PeakPrice = p;

        var stop = StopPrice ?? InitialStop;
        var stopKind = _stopKind;
        if (Settings.MoveStopToBreakEven && PeakPrice >= AveragePrice + R)
        {
            var be = TickRules.RoundUp(_host.Cost.BreakEvenPrice(AveragePrice), Context.Market);
            if (be > stop) { stop = be; stopKind = "본절"; }
        }
        if (Settings.TrailingActivationPct > 0 && PeakPrice >= AveragePrice * (1 + Settings.TrailingActivationPct / 100m))
        {
            var trail = TickRules.RoundDown(PeakPrice * (1 - Settings.TrailingDistancePct / 100m), Context.Market);
            if (trail > stop) { stop = trail; stopKind = "트레일링"; }
        }
        StopPrice = stop;
        _stopKind = stopKind;

        if (_exitOrder is not null) return; // 이미 매도 진행 중

        var sellable = Quantity;
        if (p <= stop)
        {
            SubmitExit(sellable, $"{stopKind} ({stop:N0})", OrderPriority.Emergency, market: false);
            return;
        }
        if (Settings.TakeProfitPct > 0 && p >= AveragePrice * (1 + Settings.TakeProfitPct / 100m))
        {
            SubmitExit(sellable, $"목표 익절 +{Settings.TakeProfitPct}%", OrderPriority.Exit, market: false);
            return;
        }
        if (!PartialTaken && Settings.PartialTakeProfitPct > 0 && p >= AveragePrice * (1 + Settings.PartialTakeProfitPct / 100m))
        {
            var part = Math.Floor(Quantity * Settings.PartialTakeProfitRatioPct / 100m);
            PartialTaken = true;
            if (part >= 1 && part < Quantity)
            {
                SubmitExit(part, $"1차 분할 익절 +{Settings.PartialTakeProfitPct}%", OrderPriority.Exit, market: false);
                return;
            }
        }
        if (Settings.HoldOvernight) return; // 익일 보유 봇은 타임스탑·당일 강제청산 없음

        if (Settings.TimeStopMinutes > 0 && (now - EntryTime).TotalMinutes >= Settings.TimeStopMinutes
            && PeakPrice < AveragePrice + R * Settings.TimeStopMinProgressR)
        {
            SubmitExit(sellable, $"타임스탑 {Settings.TimeStopMinutes}분", OrderPriority.Exit, market: false);
            return;
        }
        if (Kst.TimeOf(now) >= Settings.ForceExitTime)
        {
            SubmitExit(sellable, "장마감 강제청산", OrderPriority.Exit, market: false);
        }
    }

    private void SubmitExit(decimal qty, string reason, OrderPriority priority, bool market)
    {
        if (qty <= 0 || _exitOrder is not null) return;
        CancelEntryOrder();
        _exitReason = reason;
        decimal? price = null;
        var type = OrderType.Market;
        if (!market)
        {
            var bid = Context.OrderBook?.BestBid ?? Context.LastPrice;
            price = TickRules.Clamp(TickRules.AddTicks(bid, -Settings.ExitSlippageTicks, Context.Market), Context.Limits?.Lower, Context.Limits?.Upper);
            type = OrderType.Limit;
        }
        var id = _host.SubmitOrder(this, OrderSide.Sell, type, qty, price, priority, reason);
        _exitOrder = new WorkingOrder { ClientOrderId = id, Side = OrderSide.Sell, Quantity = qty, SubmittedAt = _host.Now, Reason = reason, Escalated = market };
        SetState(BotState.ExitPending, reason);
        _host.Log(LogLevel.Info, Id, $"매도 주문 {qty:N0}주 {(market ? "시장가" : $"@ {price:N0}")} — {reason}");
    }

    private void EscalateExit(string why)
    {
        if (_exitOrder is null || _exitOrder.Escalated) return;
        _exitOrder.Escalated = true;
        _exitOrder.SubmittedAt = _host.Now;
        _host.ModifyOrder(this, _exitOrder.ClientOrderId, OrderType.Market, _exitOrder.Remaining, null);
        _host.Log(LogLevel.Warn, Id, $"{why} → 시장가로 정정 ({_exitOrder.Remaining:N0}주)");
    }

    private void CancelEntryOrder()
    {
        if (_entryOrder is { CancelRequested: false } eo)
        {
            eo.CancelRequested = true;
            _host.CancelOrder(this, eo.ClientOrderId);
        }
    }

    private void CloseTrade(DateTimeOffset now)
    {
        var avgEntry = _tradeBuyQty > 0 ? _tradeBuyValue / _tradeBuyQty : AveragePrice;
        var avgExit = _tradeSellQty > 0 ? _tradeSellValue / _tradeSellQty : 0;
        var riskPerShare = avgEntry - InitialStop;
        var trade = new ClosedTrade(
            Id, Symbol, Context.Name, EntrySignalFactory.DisplayName(Settings.Strategy), _host.Execution,
            EntryTime, now, _tradeSellQty, avgEntry, avgExit, _tradeNet,
            avgEntry > 0 && _tradeSellQty > 0 ? _tradeNet / (avgEntry * _tradeSellQty) * 100m : 0,
            riskPerShare > 0 && _tradeSellQty > 0 ? _tradeNet / (riskPerShare * _tradeSellQty) : null,
            _entryReason, _exitReason);

        RealizedNet += _tradeNet;
        if (_tradeNet > 0) Wins++; else Losses++;
        Quantity = 0;
        AveragePrice = 0;
        StopPrice = null;
        PartialTaken = false;
        _host.OnTradeClosed(this, trade);
        _host.Log(LogLevel.Trade, Id, $"거래 종료 순손익 {_tradeNet:+#,0;-#,0;0} ({trade.NetPct:F2}%), 봇 누적 {RealizedNet:+#,0;-#,0;0}");

        // 봇 레벨 목표/한도 (설계 문서 2.1 "목표 도달 시 정지" 3단계 중 ②)
        var basis = Settings.MaxPositionAmount;
        if (_stopAfterFlat) { SetState(BotState.Stopped, "사용자 정지 (청산 완료)"); return; }
        if (_haltAfterFlat) { SetState(BotState.Halted, "주문 오류 누적"); return; }
        if (Settings.BotTargetProfitPct > 0 && RealizedNet >= basis * Settings.BotTargetProfitPct / 100m)
        { SetState(BotState.Completed, $"봇 목표 달성 {RealizedNet:+#,0}"); return; }
        if (Settings.BotMaxLossPct > 0 && RealizedNet <= -basis * Settings.BotMaxLossPct / 100m)
        { SetState(BotState.Halted, $"봇 손실 한도 {RealizedNet:#,0}"); return; }
        if (Entries >= Settings.MaxEntries)
        { SetState(BotState.Completed, $"최대 진입 {Settings.MaxEntries}회 소진"); return; }

        _cooldownUntil = now.AddSeconds(Settings.CooldownSeconds);
        SetState(Settings.CooldownSeconds > 0 ? BotState.Cooldown : BotState.Watching, "청산 완료");
    }

    private void RegisterError(string message)
    {
        OrderErrors++;
        _host.Log(LogLevel.Error, Id, message);
        if (OrderErrors >= Math.Max(1, _host.MaxOrderErrorsPerBot))
        {
            if (HasPosition) _haltAfterFlat = true;
            else SetState(BotState.Halted, "주문 오류 누적");
        }
    }

    private void SetState(BotState state, string reason)
    {
        State = state;
        StateReason = reason;
    }
}
