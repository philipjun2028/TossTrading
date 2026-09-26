# TossTrading

토스증권 Open API 기반 **C# / .NET 10 / WPF** 모멘텀 데이트레이딩 자동매매 프로그램. 파이썬 없이 C#만으로 만들었습니다.

- **스캐너**: 거래대금·거래량·상승률 랭킹 → 경고/유형/가격/등락률/거래대금/RVOL/틱비용 필터 → 점수화 (Stocks in Play)
- **종목별 봇**: 종목마다 전략·투입금·손절·분할익절·트레일링·타임스탑·목표수익률을 **각각 설정**, 여러 종목 동시 운용
- **운용 모드**: A. 수동진입·자동청산 / B. 반자동(신호→승인) / C. 완전자동
- **종가매매**: 15:00~15:19 강세·고가 마감 종목 매수 → 익일 시초 매도 또는 관리 후 청산 (프리셋 "종가베팅 (익일 매도)"). 스캐너 **종가매매 후보 모드**로 후보 전체를 매수 조건표(✔/✖)와 함께 정렬. 봇·모의계좌는 재시작해도 이어서 관리
- **목표 도달 시 정지 3단계**: 거래 익절 → 봇 누적 목표 도달 시 봇 종료 → 계좌 일 목표/일 손실 한도 도달 시 신규 진입 중지
- **리스크 관리**: 리스크 기반 포지션 사이징, 일 손실 한도, 동시 보유/총 노출 한도, 연속 손실 쿨다운, **킬스위치**
- **페이퍼 트레이딩**(모의 체결) + **시뮬레이션 시장**(API 키 없이 체험) + 실시간 체결/호가 기록

> ⚠️ 투자 권유가 아닙니다. 실전(Live) 모드는 실제 주문이 나가며 모든 손익 책임은 사용자에게 있습니다.
> 반드시 [설계 문서 10장](docs/01_Concept_and_Design.md#10-검증-프로세스--실전-투입-전-반드시-통과)의 검증 절차(시뮬레이션 → 토스 실시간+모의 주문 → 소액 실전)를 거치세요.

## 빠른 시작 (Windows)

### 1. 받기

```powershell
git clone -b claude/gracious-sagan-hiire2 https://github.com/philipjun2028/TossTrading.git "C:\0. Project\TossTrading"
cd "C:\0. Project\TossTrading"
```

### 2. 준비물

- **.NET 10 SDK** (https://dotnet.microsoft.com/download/dotnet/10.0) — 빌드용
- **.NET 8 Desktop Runtime** — WPF 앱 실행용. DevExpress 24.1 이 .NET 8 까지만 지원해서 앱은 `net8.0-windows` 로 빌드합니다
  (라이브러리는 net8.0/net10.0 멀티 타깃, CLI·테스트는 .NET 10)
- IDE: **Visual Studio 2026** 권장 (.NET 10 지원). Visual Studio 2022 를 쓰면 .NET 10 SDK 설치 후 아래 `dotnet` 명령으로 실행하세요.
- **DevExpress WPF 24.1.7** (UI: ThemedWindow, GridControl, ChartControl, DevExpress.Mvvm, Win11Dark 테마)
  - 설치 시 등록되는 NuGet 로컬 소스 **"DevExpress 24.1 Local"** 에서 패키지를 가져옵니다.
    Visual Studio → 도구 → 옵션 → NuGet 패키지 관리자 → 패키지 소스에 없으면 추가:
    `C:\Program Files\DevExpress 24.1\Components\System\Components\Packages`
  - 사용하는 패키지: `DevExpress.Wpf.Core`, `DevExpress.Wpf.Grid`, `DevExpress.Wpf.Charts`, `DevExpress.Wpf.Themes.Win11Dark`
  - 다른 24.1.x 버전이 설치되어 있으면: `dotnet build -p:DevExpressVersion=24.1.x` (또는 `TossTrading.App.csproj` 의 `DevExpressVersion` 수정)

### 3. 실행

```powershell
dotnet run --project src\TossTrading.App          # WPF 앱
dotnet test --project tests\TossTrading.Tests       # 테스트 (56개, xunit.v3)
```

또는 Visual Studio 에서 `TossTrading.sln` 을 열고 `TossTrading.App` 을 시작 프로젝트로 지정해 F5.

### 4. 사용 순서

1. **시뮬레이션으로 체험** — 데이터 `시뮬레이션`, 주문 `모의` 상태로 [▶ 시작]. 가상 종목·가상 시각(기본 10배속)으로 스캐너와 봇이 돌아갑니다.
2. 스캐너에서 종목 선택 → 프리셋 선택 → **[＋ 선택 종목 봇 추가]** → 설정 확인 → 확인
3. 봇 목록에서 봇 선택 → [수동 매수] (수동진입 모드) / 신호가 뜨면 [✔ 승인] (반자동)
4. **토스 연결** — ⚙ 설정 → 토스 연결: Client ID/Secret 입력, [연결 테스트 · 계좌 조회]
   - 토스증권 WTS → 설정 → Open API 에서 키 발급
   - 같은 메뉴 **허용 IP 관리**에 이 PC 의 공인 IPv4 등록 (안 하면 403)
5. 데이터 `토스 실시간` + 주문 `모의` 로 실제 시세에서 20거래일 이상 검증 → 기준 통과 시 소액 `실전`

### 연결 점검 CLI (Phase 0)

```powershell
$env:TOSS_CLIENT_ID="..."; $env:TOSS_CLIENT_SECRET="..."
dotnet run --project src\TossTrading.Cli -- check     # 토큰·계좌·시세·랭킹·웹소켓 점검
dotnet run --project src\TossTrading.Cli -- sim 60 60 # 헤드리스 시뮬레이션 (60초, 60배속)
```

## 구조

```
src/
  TossTrading.Domain/   도메인: 호가단위, 비용모델, 사이징, 설정, 주문/시세 모델, 추상화(IBroker/IMarketDataFeed/IMarketDataSource)
  TossTrading.Engine/   엔진: 이벤트 루프, 봇 상태머신, 진입 전략, 주문 큐, 리스크, 스캐너, 페이퍼 브로커, 시뮬레이션 시장
  TossTrading.Toss/     토스 어댑터: OAuth 토큰, REST 클라이언트(호출 한도), 웹소켓(선언형 구독/재연결), 실전 브로커
  TossTrading.App/      WPF + DevExpress 24.1 (DevExpress.Mvvm, GridControl, ChartControl 캔들), 설정(DPAPI 암호화)
  TossTrading.Cli/      연결 점검 / 헤드리스 시뮬레이션
tests/TossTrading.Tests/ xUnit 56개 (도메인·봇·페이퍼·엔진 통합·토스 REST/WS 파싱)
```

데이터 폴더: `%LocalAppData%\TossTrading\` (settings.json, logs, journal(거래기록 jsonl), ticks(체결/호가 기록))

**🤖 자동 운용**: 켜 두면 장중에는 단타 후보를 자동으로 골라 단타 봇을 돌리고, 14:50 단타를 정리한 뒤 종가매매 후보를 골라 종가베팅(익일 매도)까지 자동 진행합니다 → [구현 노트 8장](docs/02_Implementation_Notes.md)

**📈 백테스트**: 기간을 정해 과거 데이터(토스 분봉)로 자동 운용을 그대로 재생하고 거래 내역·일별 손익·수익률·수익금·MDD를 리포트합니다 → [구현 노트 9장](docs/02_Implementation_Notes.md)

**성과 분석**: 모든 거래·신호가 분석용으로 `journal\analysis_*.jsonl` 에 기록됩니다 (진입 시 시장 상황, 보유 중 최고/최저, 슬리피지, 청산 후 60분 흐름, 당시 설정). 앱의 [📊 성과 분석] 또는 `TossTrading.Cli report [일수]` 로 전략·조건별 성과와 개선 제안을 볼 수 있습니다 → [구현 노트 7장](docs/02_Implementation_Notes.md)

## 문서

- [01. 컨셉 & 설계](docs/01_Concept_and_Design.md)
- [02. 구현 노트 — 무엇이 되고, 무엇을 확인해야 하나](docs/02_Implementation_Notes.md)
