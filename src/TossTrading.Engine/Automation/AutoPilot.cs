using TossTrading.Domain;
using TossTrading.Engine.Trading;

namespace TossTrading.Engine.Automation;

/// <summary>자동 운용이 엔진에 요청하는 기능</summary>
public interface IAutoPilotHost
{
    DateTimeOffset Now { get; }
    IReadOnlyList<ScanCandidate> Candidates { get; }
    IReadOnlyList<TradingBot> Bots { get; }

    /// <summary>신규 진입이 막힌 이유 (킬스위치·일 손실 한도 등). 없으면 null.</summary>
    string? EntryBlockReason { get; }

    /// <summary>운용 기준 금액 (오버나잇 바스켓 종목당 투입금 계산)</summary>
    decimal Equity { get; }

    void SetScanMode(ScanMode? mode);

    /// <summary>봇을 만들고 시작한다. 실패하면 null.</summary>
    TradingBot? AddAutoBot(string symbol, string name, BotSettings settings, string role);

    void RemoveBot(TradingBot bot);
    void Log(LogLevel level, string message);
}

/// <summary>자동 운용 현재 상태 (UI 표시용)</summary>
public sealed record AutoPilotView(bool Enabled, string Phase, string Status, ScanMode ScanMode);

/// <summary>
/// 자동 운용: 시간대에 따라 스캐너 모드를 바꾸고, 후보 상위 종목으로 봇을 만들어 돌리고, 끝난 봇은 정리한다.
///
///   09:05 ~ 14:30  단타 후보 상위 N종목 → 단타 봇 (완전자동). 끝난/놀고 있는 봇은 다른 종목으로 교체
///   14:30 ~ 14:50  단타 신규 진입 중단, 보유분은 봇 규칙대로 관리
///   14:40          스캐너 → 종가매매 후보 모드
///   14:50          단타 봇 전량 정리 (종가 자금 확보)
///   14:55 ~ 15:15  종가 후보(조건 통과) 상위 M종목 → 종가베팅 봇 (15:00~15:19 진입, 익일 매도)
///   15:20 이후     진입 못 한 종가 봇 정리. 보유분은 다음 날 봇이 매도 후 자동 정리
///
/// 사용자가 직접 만든 봇(AutoRole == null)은 건드리지 않는다. 엔진 이벤트 루프에서만 호출된다.
/// </summary>
public sealed class AutoPilot
{
    public const string DayRole = "day";
    public const string ClosingRole = "closing";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeOnly SessionEnd = new(15, 30);

    private readonly IAutoPilotHost _host;
    private readonly Dictionary<string, DateTimeOffset> _addedAt = new();
    private readonly HashSet<string> _usedDay = new();
    private readonly HashSet<string> _usedClosing = new();
    private readonly HashSet<string> _windingDown = new();
    private readonly Dictionary<string, int> _closingMisses = new();

    /// <summary>스캔 결과가 흔들려도 바로 교체하지 않도록, 연속으로 이만큼 탈락해야 교체</summary>
    private const int ClosingMissesToReplace = 3;
    private DateOnly _date;
    private DateTimeOffset _lastTick;
    private ScanMode? _appliedMode;

    public AutoPilot(IAutoPilotHost host, AutoPilotPlan plan)
    {
        _host = host;
        Plan = plan;
    }

    public AutoPilotPlan Plan { get; private set; }
    public bool Enabled => Plan.Settings.Enabled;
    public string Phase { get; private set; } = "꺼짐";
    public string Status { get; private set; } = "";

    public AutoPilotView View(ScanMode currentMode) => new(Enabled, Phase, Status, currentMode);

    public void UpdatePlan(AutoPilotPlan plan)
    {
        var wasEnabled = Enabled;
        Plan = plan;
        _lastTick = default;
        if (wasEnabled && !Enabled)
        {
            Phase = "꺼짐";
            Status = "새 종목 선정 중지 — 보유 종목은 각 봇이 계속 관리합니다";
            _host.Log(LogLevel.Warn, "자동 운용 꺼짐 (새 종목을 고르지 않음, 이미 만든 봇은 규칙대로 관리)");
        }
        else if (!wasEnabled && Enabled)
        {
            _host.Log(LogLevel.Info, "자동 운용 켜짐");
        }
    }

    /// <summary>
    /// 매 타이머 호출. 꺼져 있어도 스캐너 모드는 시간대에 맞추고 끝난 봇은 정리한다
    /// (종목을 직접 고르는 기능이 없으므로 화면에 남은 봇을 사람이 지울 필요가 없게).
    /// </summary>
    public void OnTimer()
    {
        var now = _host.Now;
        if (_lastTick != default && now - _lastTick < TickInterval && now >= _lastTick) return;
        _lastTick = now;

        if (!Enabled)
        {
            var tt = Kst.TimeOf(now);
            var ps = Plan.Settings;
            ApplyScanMode(ps.ClosingEnabled && tt >= ps.ClosingScanTime && tt < SessionEnd ? ScanMode.ClosingBet : ScanMode.DayTrading);
            Cleanup(now, tt);
            Phase = "꺼짐";
            Status = "새 종목 선정 중지 — 보유 종목은 각 봇이 계속 관리합니다";
            return;
        }

        var today = Kst.DateOf(now);
        if (today != _date)
        {
            _date = today;
            _usedDay.Clear();
            _usedClosing.Clear();
            _windingDown.Clear();
        }

        var s = Plan.Settings;
        var t = Kst.TimeOf(now);

        ApplyScanMode(s.ClosingEnabled && t >= s.ClosingScanTime && t < SessionEnd ? ScanMode.ClosingBet : ScanMode.DayTrading);
        if (s.DayTradingEnabled && t >= s.DayExitTime) WindDownDayBots();
        Cleanup(now, t);

        // 휴장일은 스캐너 후보가 없어 봇이 만들어지지 않는다 (시뮬레이션은 주말에도 동작)
        if (t < Kst.MarketOpen || t >= SessionEnd)
        {
            Phase = "장외";
            Status = CarriedText() ?? "장 시작(09:00) 대기";
            return;
        }

        var block = _host.EntryBlockReason;

        // ---- 단타 ----
        if (s.DayTradingEnabled && t >= s.DayStartTime && t < s.DayEntryEndTime)
        {
            Phase = "단타";
            if (block is null)
            {
                ReplaceIdleDayBots(now);
                FillDayBots(t);
            }
        }

        // ---- 종가 ----
        if (s.ClosingEnabled && t >= s.ClosingSelectTime && t < s.ClosingSelectEndTime)
        {
            Phase = "종가 선정";
            if (block is null) FillClosingBots();
        }
        else if (s.ClosingEnabled && t >= s.ClosingSelectEndTime && t < SessionEnd)
        {
            Phase = "종가 진입/보유";
        }
        else if (!(s.DayTradingEnabled && t >= s.DayStartTime && t < s.DayEntryEndTime))
        {
            Phase = t < s.DayStartTime ? "장 초반 대기" : s.ClosingEnabled && t < s.ClosingSelectTime ? "종가 준비" : "대기";
        }

        var day = ActiveBots(DayRole).Count();
        var closing = ActiveBots(ClosingRole).Count(b => !b.IsCarriedOver);
        Status = $"단타 봇 {day}/{s.MaxDayBots} · 종가 봇 {closing}/{s.MaxClosingBots}"
                 + (CarriedText() is { } c ? $" · {c}" : "")
                 + (block is not null ? $" · 신규 선정 중단: {block}" : "");
    }

    // ------------------------------------------------------------------ 단타

    private void FillDayBots(TimeOnly t)
    {
        var s = Plan.Settings;
        var free = s.MaxDayBots - ActiveBots(DayRole).Count();
        if (free <= 0) return;

        foreach (var c in _host.Candidates)
        {
            if (free <= 0) break;
            if (c.ClosingTotal > 0) continue;                 // 종가 모드 결과는 단타 대상 아님
            if (s.MinDayScore > 0 && c.Score < s.MinDayScore) continue;
            if (_usedDay.Contains(c.Symbol) || HasBot(c.Symbol)) continue;

            var preset = t < s.MorningUntil ? Plan.Morning : Plan.Day;
            if (preset is null) return;                       // 장중 프리셋 "사용 안 함" → 오전 이후 새 단타 없음
            var bs = preset.Clone();
            bs.Mode = BotMode.FullAuto;
            if (bs.Strategy is EntryStrategyKind.Manual or EntryStrategyKind.ClosingBet or EntryStrategyKind.OvernightBasket) bs.Strategy = EntryStrategyKind.OpeningRangeBreakout;
            bs.HoldOvernight = false;
            if (bs.EntryStartTime < s.DayStartTime) bs.EntryStartTime = s.DayStartTime;
            // 프리셋의 진입 시간대를 넓히지 않는다 (ORB 는 오전 전략 — 오후에 아침 범위 "돌파"는 의미 없음)
            if (bs.EntryEndTime > s.DayEntryEndTime) bs.EntryEndTime = s.DayEntryEndTime;
            if (bs.ForceExitTime > s.DayExitTime) bs.ForceExitTime = s.DayExitTime;
            if (bs.EntryEndTime <= Kst.TimeOf(_host.Now)) continue; // 진입 시간이 이미 지난 프리셋으로는 만들지 않음
            // ORB 는 하루 첫 돌파만 → 한 번 거래하면 봇을 끝내 다른 종목에 자리를 넘긴다
            if (bs.Strategy == EntryStrategyKind.OpeningRangeBreakout) bs.MaxEntries = 1;

            _usedDay.Add(c.Symbol);
            if (Add(c, bs, DayRole, $"단타 선정 (점수 {c.Score:0}, {c.ChangePct:+0.0;-0.0}%)")) free--;
        }
    }

    /// <summary>오래 진입이 없고 상위 후보에서 밀려난 단타 봇을 정리 (다음 틱에 다른 종목으로 채운다)</summary>
    private void ReplaceIdleDayBots(DateTimeOffset now)
    {
        var minutes = Plan.Settings.IdleReplaceMinutes;
        if (minutes <= 0) return;
        var top = _host.Candidates.Where(c => c.ClosingTotal == 0).Take(Math.Max(5, Plan.Settings.MaxDayBots * 2)).Select(c => c.Symbol).ToHashSet();
        foreach (var bot in ActiveBots(DayRole).ToList())
        {
            if (bot.Entries > 0 || bot.HasPosition || bot.HasWorkingOrders || bot.State != BotState.Watching) continue;
            if (!_addedAt.TryGetValue(bot.Id, out var at) || now - at < TimeSpan.FromMinutes(minutes)) continue;
            if (top.Contains(bot.Symbol)) continue;
            bot.Stop(flatten: false);
            _host.Log(LogLevel.Info, $"{bot.Context.Name}: {minutes}분간 진입 없음 + 후보 순위 하락 → 교체");
            Remove(bot);
        }
    }

    private void WindDownDayBots()
    {
        foreach (var bot in ActiveBots(DayRole).ToList())
        {
            if (!_windingDown.Add(bot.Id)) continue;
            if (bot.HasPosition)
            {
                bot.Stop(flatten: true);
                _host.Log(LogLevel.Info, $"{bot.Context.Name}: 종가매매 준비 — 단타 보유분 정리");
            }
            else
            {
                bot.Stop(flatten: false);
            }
        }
    }

    // ------------------------------------------------------------------ 종가

    private void FillClosingBots()
    {
        if (Plan.Closing.Strategy == EntryStrategyKind.OvernightBasket) { FillOvernightBasket(); return; }
        var s = Plan.Settings;
        var passing = _host.Candidates.Where(Passes).ToList();

        // 조건에서 탈락한 미진입 종가 봇은 교체
        var keep = passing.Select(c => c.Symbol).ToHashSet();
        var scanned = _host.Candidates.Any(c => c.ClosingTotal > 0); // 종가 스캔 결과가 있을 때만 판단
        foreach (var bot in ActiveBots(ClosingRole).Where(b => !b.IsCarriedOver).ToList())
        {
            if (bot.Entries > 0 || bot.HasPosition || bot.HasWorkingOrders || !scanned) continue;
            if (keep.Contains(bot.Symbol)) { _closingMisses.Remove(bot.Id); continue; }
            var misses = _closingMisses[bot.Id] = _closingMisses.GetValueOrDefault(bot.Id) + 1;
            if (misses < ClosingMissesToReplace) continue;
            bot.Stop(flatten: false);
            _host.Log(LogLevel.Info, $"{bot.Context.Name}: 종가 조건 탈락 → 교체");
            Remove(bot);
        }

        var free = s.MaxClosingBots - ActiveBots(ClosingRole).Count(b => !b.IsCarriedOver);
        foreach (var c in passing)
        {
            if (free <= 0) break;
            if (_usedClosing.Contains(c.Symbol) || HasBot(c.Symbol)) continue;
            var bs = Plan.Closing.Clone();
            bs.Mode = BotMode.FullAuto;
            bs.Strategy = EntryStrategyKind.ClosingBet;
            bs.HoldOvernight = true;
            _usedClosing.Add(c.Symbol);
            if (Add(c, bs, ClosingRole, $"종가 선정 (조건 {c.ClosingPassed}/{c.ClosingTotal}, 점수 {c.Score:0})")) free--;
        }
    }

    /// <summary>
    /// 오버나잇 바스켓: 종가 후보 중 거래대금 상위 N종목을 사서 다음 날 시가에 판다.
    /// 종목당 투입금 = 운용 금액 × 바스켓 비중 ÷ N. 상한가 부근·급등 후 밀린 종목은 제외.
    /// </summary>
    private void FillOvernightBasket()
    {
        var s = Plan.Settings;
        var free = s.MaxClosingBots - ActiveBots(ClosingRole).Count(b => !b.IsCarriedOver);
        if (free <= 0) return;
        var perStock = _host.Equity * s.ClosingCapitalPct / 100m / Math.Max(1, s.MaxClosingBots);
        if (perStock <= 0) return;
        var picks = _host.Candidates
            .Where(c => c.ClosingTotal > 0)                                  // 종가 모드 스캔 결과만
            .Where(c => c.ChangePct is >= 0m and < 28m)
            .Where(c => !(c.ChangePct > 8m && c.RangePosition is < 0.3m))
            .OrderByDescending(c => c.TradingAmount);
        foreach (var c in picks)
        {
            if (free <= 0) break;
            if (_usedClosing.Contains(c.Symbol) || HasBot(c.Symbol)) continue;
            var bs = Plan.Closing.Clone();
            bs.Mode = BotMode.FullAuto;
            bs.HoldOvernight = true;
            bs.Sizing = SizingMode.FixedAmount;
            bs.FixedAmount = Math.Floor(perStock);
            bs.MaxPositionAmount = Math.Max(bs.MaxPositionAmount, bs.FixedAmount);
            _usedClosing.Add(c.Symbol);
            if (Add(c, bs, ClosingRole, $"오버나잇 선정 (거래대금 {c.TradingAmount / 100_000_000m:N0}억, {c.ChangePct:+0.0}%)")) free--;
        }
    }

    private bool Passes(ScanCandidate c)
    {
        if (c.ClosingTotal <= 0) return false;
        var min = Plan.Settings.ClosingMinPassed;
        return min <= 0 ? c.ClosingPassed >= c.ClosingTotal : c.ClosingPassed >= Math.Min(min, c.ClosingTotal);
    }

    // ------------------------------------------------------------------ 공통

    /// <summary>끝난 봇, 재시작 후 대기 상태로 남은 봇, 진입 못 한 종가 봇(마감 후)을 목록에서 뺀다 (보유·미체결 없는 것만).</summary>
    private void Cleanup(DateTimeOffset now, TimeOnly t)
    {
        foreach (var bot in _host.Bots.ToList())
        {
            if (bot.HasPosition || bot.HasWorkingOrders) continue;
            var closingMissed = bot.AutoRole == ClosingRole && bot.Entries == 0 && t >= BotSettings.MarketCloseAuction;
            if (bot.State.IsFinished() || bot.State == BotState.Idle || closingMissed)
            {
                if (!bot.State.IsFinished()) bot.Stop(flatten: false);
                Remove(bot);
            }
        }
    }

    private bool Add(ScanCandidate c, BotSettings bs, string role, string why)
    {
        var errors = bs.Validate();
        if (errors.Count > 0)
        {
            _host.Log(LogLevel.Warn, $"{c.Name}: 봇 설정 오류로 건너뜀 — {string.Join(" ", errors)}");
            return false;
        }
        var bot = _host.AddAutoBot(c.Symbol, c.Name, bs, role);
        if (bot is null) return false;
        _addedAt[bot.Id] = _host.Now;
        _host.Log(LogLevel.Info, $"{c.Name}({c.Symbol}) {why} → {Strategies.EntrySignalFactory.DisplayName(bs.Strategy)} 봇 시작");
        return true;
    }

    private void Remove(TradingBot bot)
    {
        _addedAt.Remove(bot.Id);
        _windingDown.Remove(bot.Id);
        _closingMisses.Remove(bot.Id);
        _host.RemoveBot(bot);
    }

    private IEnumerable<TradingBot> ActiveBots(string role) =>
        _host.Bots.Where(b => b.AutoRole == role && (!b.State.IsFinished() || b.HasPosition || b.HasWorkingOrders));

    private bool HasBot(string symbol) => _host.Bots.Any(b => b.Symbol == symbol);

    private string? CarriedText()
    {
        var n = _host.Bots.Count(b => b.AutoRole == ClosingRole && b.HasPosition && b.IsCarriedOver);
        var held = _host.Bots.Count(b => b.AutoRole == ClosingRole && b.HasPosition && !b.IsCarriedOver);
        if (n == 0 && held == 0) return null;
        return n > 0 ? $"익일 매도 대기 {n}종목" : $"종가 보유 {held}종목";
    }

    private void ApplyScanMode(ScanMode mode)
    {
        if (_appliedMode == mode) return;
        _appliedMode = mode;
        _host.SetScanMode(mode);
        _host.Log(LogLevel.Info, mode == ScanMode.ClosingBet ? "스캐너 → 종가매매 후보 모드" : "스캐너 → 단타 후보 모드");
    }
}
