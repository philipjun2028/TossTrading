# TossTrading — 구현 노트 (v0.2)

> 설계 문서(01)의 Phase 0~3 범위를 구현한 1차 버전에 대한 기록입니다.
> 무엇이 구현·검증되었고, 무엇을 실제 토스 계정으로 확인해야 하는지 정리합니다.

## 1. 구현 범위

| 설계 문서 항목 | 상태 | 위치 |
|---|---|---|
| 호가단위 / 비용 모델 / 비용 가드 | ✅ | `Domain/TickRules.cs`, `Domain/CostModel.cs`, 봇 설정창 [비용 점검] |
| 리스크 기반 포지션 사이징 | ✅ | `Domain/PositionSizer.cs` |
| 토스 OAuth 토큰(선제 갱신, 401 재시도) | ✅ | `Toss/TossRestClient.cs` (`TossTokenProvider`) |
| REST 그룹별 호출 한도 + 429 Retry-After + `X-RateLimit-Limit` 자동 보정 | ✅ | `Toss/TossRestClient.cs`, `Domain/RateGate.cs` |
| 웹소켓: 선언형 구독, 텍스트 PING, 지수 백오프 재연결, 거부 처리 | ✅ | `Toss/TossStreamClient.cs` |
| 주문 이벤트 누락 대비 REST 워치독 | ✅ | `Toss/TossAdapters.cs` (`TossBroker`) |
| 스캐너 (랭킹 합집합 → 필터 → RVOL → 점수) | ✅ | `Engine/Scanning/ScannerService.cs` |
| 1분봉·VWAP·ORB·ATR·체결강도, 늦은 시작 시 분봉 복원 | ✅ | `Engine/Market/SymbolContext.cs` |
| 진입 전략: ORB / VWAP 눌림 / 박스 고가 돌파 / 수동 | ✅ | `Engine/Strategies/EntrySignals.cs` |
| 봇 상태머신, 청산 규칙(손절·본절·분할익절·트레일링·타임스탑·강제청산) | ✅ | `Engine/Trading/TradingBot.cs` |
| 목표 도달 시 정지 3단계 | ✅ | 봇(거래/누적), `Engine/Risk/RiskManager.cs`(계좌) |
| 주문 우선순위 큐 (손절 > 취소·정정 > 익절 > 진입), 개장 10분 한도 축소, 멱등 재전송 | ✅ | `Engine/Trading/OrderManager.cs` |
| 정정 시 새 orderId 추적, ack 보다 먼저 온 체결 이벤트 버퍼링 | ✅ | `Engine/TradingEngine.cs` |
| 재연결 후 계좌 재동기화 → 봇 재개 | ✅ | `TradingEngine.ReconcileAsync` |
| 킬스위치 | ✅ | 엔진 + UI |
| 페이퍼 브로커 (호가 걸어 올라가기, 보수적 대기 체결) | ✅ | `Engine/Paper/PaperBroker.cs` |
| 시뮬레이션 시장 (API 키 없이 체험/테스트) | ✅ | `Engine/Simulation/SimulatedMarket.cs` |
| 거래 기록(JSONL), 로그, 틱/호가 기록(gzip) | ✅ | `Engine/Infrastructure/EngineLog.cs` |
| WPF UI (대시보드·스캐너·봇·차트·거래·로그·설정) — DevExpress 24.1.7 (ThemedWindow, GridControl, ChartControl, DevExpress.Mvvm) | ✅ | `App/` |
| 리플레이 백테스터 / 리포트 화면 | ⏳ 다음 단계 | 틱 기록은 이미 쌓이도록 구현됨 |
| 서버측 안전망(조건주문) | ⏳ 다음 단계 | 조건주문 동작 확인 후 |
| 미국 주식 모드 | ⏳ 다음 단계 | 도메인은 `MarketCountry.US` 호가단위 준비됨 |

## 2. 설계 대비 달라진 점

- **스레딩**: 설계 문서 8.4는 "봇마다 Actor"였지만, 구현은 **엔진 전체를 단일 이벤트 루프**로 직렬화했습니다
  (시세·주문 이벤트·사용자 명령·타이머 → 하나의 `Channel<Action>`). 수십 종목 규모에서는 성능 여유가 충분하고,
  봇·리스크·주문 추적 사이에 락이 전혀 없어 경쟁 조건이 원천적으로 사라집니다. 주문 전송(REST)과 스캐너만 별도 스레드입니다.
- **웹소켓 연결 1개**: 계정당 2개 제한 중 1개만 사용 (봇 종목 체결+호가, 스캐너 상위 후보 체결, 내 주문 이벤트 → 95토픽 예산).
  나머지 1개는 토스 앱/다른 도구용 여유로 남겼습니다.
- **기본 리스크**: 거래당 리스크 기본값을 0.5% → **0.2%** 로 낮췄습니다. 봇 손실 한도(최대 투입금의 2%)와 맞추기 위해서입니다.

## 3. 검증한 것 (이 저장소 안에서)

- `dotnet test` — **56개 테스트 통과** (반복 실행 안정)
  - 호가단위 경계(1,999→2,000→2,005, 5,000−1틱=4,995 등), 왕복비용 0.43%(1만원), 순손익·본절가
  - 사이징(설계 문서 7.1 예시 250주), 리스크 한도·연속손실 쿨다운·킬스위치
  - 봇: 수동매수→손절, 분할익절→트레일링→봇 목표 달성 완료, 진입 타임아웃, 매도 미체결→시장가 정정, 타임스탑, 킬스위치
  - 페이퍼: 호가 걸어 올라가기, 같은 가격 대기 체결 금지, 과매도/예수금 부족/호가단위 위반 거부, 정정 시 새 주문ID
  - 엔진 통합: 시뮬레이션 + 페이퍼로 매수 → 킬스위치 → 전량 청산
  - 토스: 결과 봉투/문자열 숫자 파싱, 계좌 헤더, 주문 본문(숫자=문자열), 에러 봉투 → 예외, 만료 토큰 1회 재발급, 403 IP 안내,
    웹소켓 선언 JSON 형식, 체결/호가/주문 프레임 파싱
- `TossTrading.Cli sim` — 헤드리스로 스캐너→봇 추가→자동 진입→분할익절/트레일링/손절/봇 손실한도/최대 진입 소진까지 전 과정 동작 확인
- WPF 앱 — DevExpress 24.1.7 은 공개 NuGet 에 없어서, 개발 환경에서는 같은 API 의 **DevExpress 25.1.14 로 컴파일 검증**(XAML 포함, 오류 0)했습니다.
  그리드 필드명·바인딩 경로도 정적 점검했습니다. **24.1.7 로컬 패키지로의 빌드와 실행 화면은 Windows 에서 확인 필요** (개발 환경이 Linux)

## 4. 실제 토스 계정으로 확인해야 하는 것 (★)

개발 환경에서 `developers.tossinvest.com` 접근이 막혀, API 형식은 **비공식 SDK(toss-go v0.3.0) 소스**를 기준으로 구현했습니다.
`TossTrading.Cli check` 로 아래를 먼저 확인하세요.

1. 토큰 발급 형식 (`POST /oauth2/token`, form-urlencoded) — `check` 1단계
2. 응답 봉투 `{"result": ...}` 와 필드명 — `check` 의 현재가/호가/분봉/랭킹/종목정보 단계
3. 계좌 헤더 `X-Tossinvest-Account` — `check` 의 매수가능금액/보유종목 단계
4. 웹소켓 선언 형식과 프레임 — `check` 마지막 15초 수신 (장 시간에 실행)
5. 주문 API — **반드시 소액·지정가·체결 안 될 가격**으로 1회 실험 후 취소 (앱의 실전 모드 전에)
6. 호출 한도 수치 — 응답 헤더 `X-RateLimit-Limit` 을 자동 반영하지만 초기값은 `TossOptions` 에서 조정
7. mTLS 인증서 필요 여부 — 필요하다면 `TossRestClient` 의 `HttpClient` 에 클라이언트 인증서를 추가해야 함

형식이 다르면 수정 지점은 `src/TossTrading.Toss/Dtos.cs`(필드명)와 `TossRestClient.cs`/`TossStreamClient.cs` 두 곳으로 한정됩니다.

## 5. 다음 단계 제안

1. **Phase 0 실행**: `check` 결과 공유 → 형식 차이 수정
2. 토스 실시간 + 모의 주문으로 매일 운용 → 틱 기록 축적
3. 리플레이 백테스터 (`ticks/*.jsonl.gz` → `SimulatedMarket` 대체 피드) + 전략별 리포트 화면
4. 조건주문 기반 서버측 안전망, 텔레그램 알림
