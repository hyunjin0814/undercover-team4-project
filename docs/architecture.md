# 아키텍처 규칙 (초안 — 팀 검토 필요)

> 이 문서가 코드 구조 규칙의 **정본**이다. PR 자동 리뷰(`.claude/skills/pr-review`)와 CLAUDE.md가 이 문서를 기준으로 검사한다.
> 적용 시점: `refactoring/architecture` 브랜치 머지 이후의 신규·수정 코드부터 (유예 조항 참고).
> 게임 규칙의 정본은 [GDD.md](GDD.md) — 이 문서는 코드 구조만 다룬다.

## 1. 구조 개요

```
사용하는 쪽 컴포넌트  ──(App.그룹.매니저 로만 접근)──▶  App (전역 파사드)
                                                        ▲
매니저  ──(베이스 상속만 하면 Awake에서 자동 등록)──────┘
```

- **App** ([Assets/Scripts/Core/App.cs](../Assets/Scripts/Core/App.cs)) — 전역 매니저 접근의 단일 경로.
  - `App.Net` — SessionManager · AuthBootstrap · VivoxManager
  - `App.Game` — RoundManager · SuddenEventManager · WantedListManager · DirectoryManager · ArrestJudge · CriminalAssigner · NpcSpawner · AppearanceAssigner · WrongfulArrestPenalty · TeamFund · FxManager · EffectManager · JailZone · JailIntake · JailLock
  - `App.UI` — UIManagerBase(3단계 예정) · CrosshairUI · ChannelingGaugeUI
    - **게임 씬에만 있는 등록 대상은 다른 씬에서 `null`이다** — `SceneReadyGate` · `SettlementConfirmGate` · 감옥 시설 셋(`JailZone`·`JailIntake`·`JailLock`). 읽는 쪽이 `?.`나 null 검사로 받는다. "로비·타이틀에서 빈 슬롯이 된다"는 것은 등록을 막는 사유가 아니다 — 빈 것이 곧 "그 씬에는 없다"는 뜻이다 (#592).
  - `App.SceneFlow` — 현재 씬의 SceneManagerBase (3단계 예정)
  - `App.Sound` — SoundManager (그룹이 아닌 단일 프로퍼티 — 시스템 서비스 하나뿐이라 중첩 클래스를 두지 않았다. BGM·UI음이 붙으면(#483) 그때 그룹으로 승격한다)
- **등록 메커니즘** — `CommonManagerBase`(일반) / `NetworkedManagerBase`(NetworkBehaviour)를 상속하면 Awake에서 `ManagerHandler`가 리플렉션으로 App의 같은 타입 필드에 주입하고, 파괴 시 해제한다. **App 필드에 직접 대입하는 코드를 만들지 말 것.**
- **도메인** = `Assets/Scripts/` 하위의 **게임플레이 폴더**. 현재: Round, NPC, Player, HQ, Item, Interaction, Events, Network, Economy, Traffic(도로에 상시 흐르는 차 — #634) (게임플레이 폴더가 새로 생기면 자동 포함). 판별이 애매하면 "이 파일이 바뀌는 이유가 뭐냐"로 판단한다.
  - **도메인으로 세지 않는 폴더**: `Core`(App 인프라 자체), `UI`(각 도메인의 화면 표현), `Data`(전 도메인 공유 어휘), `Scene`(씬 진입점), `Localization`(공유 자원), `Audio`·`Vfx`(전 도메인이 쓰는 연출 재생 기반 — `UI`와 같은 취급, #478), `Common`(도메인에 속하지 않는 공유 부품 — 지금은 `Ragdoll`(#506), Player·NPC가 같은 리그를 쓴다(#572)), `Editor`·`Test`(런타임 아님).
  - 폴더가 새로 생기면 **도메인인지 아닌지를 먼저 이 목록에 적는다** — R3 ②(참조 도메인 수)의 집계가 이 분류에 달려 있어, 미분류 폴더가 있으면 같은 타입의 등록 타당성이 리뷰마다 다르게 계산된다.
  - **참조를 셀 때**: (a) **실제 코드 의존성만** 센다 — 주석·독스트링의 언급(`<see cref=...>` 포함)은 제외. (b) 같은 도메인 내부의 다른 파일 참조는 세지 않는다. (c) 같은 오브젝트에 강제된 컴포넌트(`[RequireComponent]`) 배선은 같은 도메인으로 본다 (별도 도메인이 아님).

## 2. 규칙 (R1–R8)

리뷰에서 기계적으로 판정할 수 있게 쓴다. 각 규칙의 위반 판정 기준을 함께 명시한다.

| # | 규칙 | 위반 판정 |
|---|---|---|
| **R1** | 매니저 접근은 App 경유만 | App 등록 타입을 `FindFirstObjectByType`으로 찾는 코드 (§4 예외 제외) |
| **R2** | 신규 싱글톤 금지 | `static <자기타입> Instance` 프로퍼티/필드 신설 |
| **R3** | App 등록 기준: ① 씬에 개념적으로 하나뿐인 서비스 AND ② 두 개 이상의 도메인이 참조 | 기준 미달인데 App에 필드 추가 (참조자 1곳·같은 도메인이면 SerializeField 연결이 맞다) |
| **R4** | 새 매니저 = 베이스 상속 + 실행 순서 명시 + App 필드/프로퍼티 추가 | 셋 중 하나라도 누락 — `[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]`는 상속돼도 다시 명시 |
| **R5** | 매니저의 `Awake`/`OnDestroy`는 `protected override` + `base` 호출 | base 호출 누락 (선언 자체를 잘못하면 CS0114가 컴파일 에러 — `Assets/csc.rsp`) |
| **R6** | 매니저 간 이벤트 구독은 `Start`에서 (모든 Awake=등록 완료 보장) | 다른 매니저 구독을 Awake/OnEnable에서 수행. 매니저 오브젝트의 씬 도중 비활성화도 금지 |
| **R7** | 씬 전환은 `App.LoadScene`만 (NGO 세션 동기화 자동 분기) | `UnityEngine.SceneManagement.SceneManager.LoadScene` 직접 호출 (AppHelper 내부·§4 예외 제외) |
| **R8** | 매니저 참조는 읽기 프로퍼티: `private X Xxx => App.그룹.X;` | 매니저를 필드에 캐싱 (파괴된 참조를 쥐는 원인). null 가드는 기존 관례대로 사용처에서 |

R9(예약): UI 패널은 `PanelBase` 상속 + `OpenPanel<T>()` 경유 — 4단계(UI 패널 시스템) 시행 시 활성화.

### 연출 전파 규칙 (소리 · 이펙트)

서버 권위 이벤트의 연출을 어떻게 각 피어에 도달시킬지는 **연출이 상태인지 순간인지**로 갈린다. 둘을 섞으면 같은 종류의 연출이 코드마다 다른 방식으로 전파된다.

| 성격 | 전파 | 예 |
|---|---|---|
| **지속 상태** — "지금 어떠하다"를 물을 수 있다 | 서버가 `NetworkVariable`로 동기화하고, 각 피어가 그 값을 보고 로컬에서 켜고 끈다 | 먹통 음성 왜곡(#372), 제보 전화 벨소리(#102), 쿨다운 표시(#488) |
| **일회성 연출** — 동기화할 상태가 없다 | 서버 판정 지점에서 `App.Game.Fx.PlayEverywhere(EFx, 위치)` 한 줄 — 전파 RPC와 오프라인 폴백은 `FxManager`가 들고 있다 (#532) | 타격 먼지·타격음(#478), NPC 소멸 잔상(#310), 진압봉 스윙 모션(#217) |

일회성 쪽은 **연출 오브젝트를 네트워크에 싣지 않는다** — 각 피어가 자기 화면에 스스로 만들므로 `DefaultNetworkPrefabs.asset` 등록이 필요 없고, 늦게 들어온 피어가 지나간 연출을 뒤늦게 받는 일도 없다.

**사용처는 `App.Game.Fx` 하나만 부른다** (#532). 어떤 순간(`EFx`)에 어떤 파티클·소리가 나는지는 씬의 `FxManager` 조합표에 있고, 재생은 그 아래의 `App.Game.Effect`(파티클)·`App.Sound`(효과음)가 한다. 덕분에 아이템마다 전파 RPC를 따로 선언하지 않고, 연출을 바꿀 때 코드를 건드리지 않는다.

- 이미 전 피어에서 도는 경로(`SendTo.Everyone` RPC 안, 전 피어 이벤트 구독) 안에서는 `PlayEverywhere`가 아니라 `PlayHere`를 쓴다 — 전자를 부르면 피어마다 다시 전파돼 소리가 겹친다.
- 파티클만 필요하고 조합이 없다면 `App.Game.Effect`를 직접 불러도 된다. 셋 다 사용처가 없는 씬에서는 null이므로 `?.`로 가드한다.

### 비동기 대기 규칙 (코루틴 금지)

프레임을 넘겨 기다리는 코드는 **UniTask로 쓴다** — `StartCoroutine`/`IEnumerator`는 새로 쓰지 않는다. 관례는 `private async UniTaskVoid XxxAsync()` + 호출부 `XxxAsync().Forget()`이고, 대기에는 `this.GetCancellationTokenOnDestroy()`를 넘긴다 (오브젝트가 죽은 뒤에도 도는 대기 방지).

| 기다릴 것 | 쓸 것 |
|---|---|
| 다음 프레임 (모든 Awake/Start가 끝난 뒤) | `await UniTask.NextFrame(token)` |
| **렌더링까지 끝난** 프레임 끝 | `await UniTask.WaitForEndOfFrame(token)` |
| 시간 | `await UniTask.Delay(TimeSpan.FromSeconds(x), cancellationToken: token)` |
| 조건 | `await UniTask.WaitUntil(() => 조건, cancellationToken: token)` |

`NextFrame`·`DelayFrame`의 기본 타이밍(`PlayerLoopTiming.Update`)은 **렌더링 전**이다. 렌더 결과에 기대는 코드 — 카메라를 RenderTexture에 굽는 등 — 는 `WaitForEndOfFrame`이어야 한다 (Unity 2023.1+ 에서 이 오버로드는 `Awaitable.EndOfFrameAsync`로 간다). URP는 조명·환경 셰이더 상수를 렌더 루프 안에서 채우므로, 첫 프레임이 끝나기 전에 구우면 에디터에선 멀쩡하고 빌드에선 실행마다 결과가 달라진다 (`LobbyPortraitStage`).

## 3. 승격/강등 절차

- **승격**: R3의 ①을 이미 만족하는 클래스에 두 번째 도메인의 참조가 생기는 순간 App에 올린다. 비용은 App 필드+프로퍼티 2줄 + 베이스 상속.
- **강등**: App에서 필드/프로퍼티를 지우면 사용처 전부가 컴파일 에러로 드러난다 — 컴파일러가 수정 목록을 준다.
- **올리지 않는 대안 (부품 조회)**: 승격 대상이 *이미 App에 있는 매니저가 관리하는 부품*이라면, 부품을 따로 등록하지 말고 그 매니저에게 제네릭으로 물어본다 — `App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>()`. 타입은 부르는 쪽에만 남으므로 매니저는 여전히 부품 종류를 모르고, App 필드도 늘지 않는다. 런타임 스폰 프리팹처럼 인스펙터 배선이 불가능한 사용처(스캐너)도 이 경로로 해결된다 (#372 리뷰).
- 판단이 애매하면 PR에서 R3 기준으로 논의한다.

## 4. 의도적 예외 (위반 아님)

| 코드 | 사유 |
|---|---|
| RoundTimerUI → RoundTimerSync `Find` | Round 도메인 내부 부품 (HQ 타이머 표시가 생기면 승격 후보) |
| SessionManager → AuthBootstrap `SerializeField` | 같은 오브젝트/프리팹 내 직접 연결 |
| RoundEndResetter의 테스트 씬 폴백 `SceneManager.LoadScene` | EScene 매핑이 없는 테스트 씬 한정 — 정식 흐름은 App.LoadScene(Title)로 전환 완료 (#247) |
| SceneReadyGate → `App.Game.ReadyGate` 등재 | 세어지는 참조 도메인은 Round(RoundManager) 하나라 R3 ②에 미달하지만, 실사용은 로딩 흐름(InGameManager)·표시(ReadyWaitHud)·라운드 시작(RoundManager)을 가로지르는 서버 권위 코디네이션 지점이다. 특히 ReadyWaitHud는 HUD.prefab이 오너 스폰 시 **런타임 생성**돼 씬 오브젝트를 인스펙터로 배선할 수 없어 App 경로 외 대안이 없다 (#410) |
| LonePlayerWatch → HqOccupancyZone `Find` | 장소(구역) 오브젝트 — 매니저 아님. JailZone·JailIntake와 같은 분류다. 참조 도메인이 Events 하나뿐이라 R3 승격 기준에 미달하고, 쓰는 쪽(AbductionEvent) Awake에서 1회 탐색이라 런타임 비용도 없다. 인스펙터 연결을 우선하고 비었을 때만 씬 탐색으로 폴백한다 (#371) |
| SecretFavorBroker → TipCallPhone `Find`(폴백) | 설치물(전화기) 오브젝트 — 매니저 아님. 참조 도메인이 HQ 하나라 R3 승격 기준에 미달하고, `Start` 1회 탐색이라 런타임 비용도 없다. 인스펙터 연결을 우선하고 비었을 때만 씬 탐색으로 폴백한다 (#485) |
| **연출 재생 기반** → `App.Sound`(SoundManager) · `App.Game.Effect`(EffectManager) 등재 | 참조 도메인은 `Item`(Baton·Taser) 하나뿐이라 R3 ②에 미달하지만, 사용처가 **런타임 스폰되는 아이템 프리팹**이라 인스펙터로 매니저를 배선할 방법이 없다 — 위 SceneReadyGate·HUD 항목과 같은 사정이다. 연출 종류가 늘면 참조 도메인은 NPC·Events로 자연히 퍼진다(그때는 예외가 아니라 정규 등록이 된다). **재생 기반에 한정**한다: 연출 규칙·상태를 들고 있는 쪽은 이 예외를 쓸 수 없다 (#478) |
| **런타임 생성 HUD 표시 컴포넌트** → `App.UI.*` 등재 (`CrosshairUI` · `ChannelingGaugeUI` · `ToastView` · `SignalMessageView` · `PromptView` · `SecretFavorHud` · `DamageVignetteUI` · `TaserShockUI`) | HUD.prefab은 오너 스폰 시 **런타임 생성**되므로 표시 컴포넌트를 인스펙터로 배선할 방법이 아예 없다 — SceneReadyGate 항목과 같은 사정이다. 그래서 참조 도메인이 1개(또는 0개)라도 App 경로가 유일한 대안이다. (`ToastView`만은 HUD가 만들어지지 않는 로비에서 씬에 직접 놓여 있다(#598) — 그 인스턴스는 배선이 가능하지만, 등록 경로를 씬별로 가르지 않고 App 하나로 유지한다.) **표시 컴포넌트에 한정**한다: 상태를 들고 있는 매니저는 이 예외를 쓸 수 없고 R3를 그대로 따른다 (#493) |

| **세션 상주 홀더** → `App.Game.TeamFund` · `App.Game.ShopPurchases` · `App.Game.RoundProgress` · `App.Game.Roster`(SessionRoster) 등재 | `SessionObjectSpawner`가 **Title 씬에서 런타임 스폰**해 씬을 넘어 사는 오브젝트들(`SessionState.prefab` 하나에 넷이 얹혀 있다 — 담긴 것이 늘면서 `TeamFund.prefab`에서 이름을 바꿨다)이라, 게임 씬의 인스펙터로 배선할 방법이 아예 없다 — SceneReadyGate·HUD 항목과 같은 사정이다. 넷 다 도메인 밖 코드 참조가 Round 하나뿐이거나(TeamFund·ShopPurchases·RoundProgress) 아예 없어(`SessionRoster` — 참조자가 Core·UI뿐) **R3 ②에는 미달**한다(`TeamFund`에 대한 HQ·Interaction의 언급은 전부 주석이다 — 참조를 셀 때 §1의 (a)에 걸린다). `SessionRoster`는 로비 씬 오브젝트였다가 입퇴장 알림이 상점·게임맵에서도 떠야 해서 승격했다 — 닉네임을 아는 곳이 여기뿐이라 씬과 함께 죽으면 안 된다(#598). 세션을 넘어 사는 상태를 다루는 홀더가 늘어나면 이 항목에 함께 적는다. 참조 도메인이 둘 이상으로 퍼지면 예외가 아니라 정규 등록이 된다 (#214·#377·#598) |

예외를 추가하려면 이 표에 사유와 함께 기재한다 (기재 없는 예외는 위반).

## 5. 유예 조항

`refactoring/architecture` 머지 **이전에** 열린 브랜치의 코드는 규칙 위반을 지적하되 🟡(후속 조치)로 분류한다. 머지 이후 새로 작성·수정되는 코드는 정식 적용(🟠 이상).

---
*최종 수정: 2026-08-13 (비동기 대기 규칙 추가 — 코루틴 금지·UniTask 관례·WaitForEndOfFrame 주의 · 도메인 목록에 `Traffic` 추가 — #634) · 2026-08-12 (세션 상주 홀더에 `SessionRoster` 추가 · ToastView 예외 사유에 로비 씬 배치 단서 — #598) · 2026-08-10 (세션 상주 홀더 `TeamFund`·`ShopPurchases`·`RoundProgress` 예외 기재 — #214·#377) · 2026-08-06 (감옥 분리에 따른 예외 갱신 — `JailScanner` 폐기 · `JailDoor`·`JailbreakEvent`·`JailRoom` 탐색 기재 · `JailIntake` 참조 도메인 2곳으로 갱신 — #537) · 2026-08-05 (일회성 연출 창구를 App.Game.Fx로 일원화 — #532 · 연출 전파 규칙 추가 · App.Sound·App.Game.Effect 등재 + §4 예외 기재 · Audio·Vfx 폴더 분류 — #478) · 2026-08-04 (LonePlayerWatch → HqOccupancyZone 예외 기재 — #371) · 2026-08-03 (HqDropoffZone 예외 삭제 · JailIntake·JailScanner 예외 기재 — #492) · 2026-08-01 (SceneReadyGate 예외 기재 — #410) · 2026-07-28 (JailZone 예외 기재 — #395) · 작성 근거: refactoring/architecture 브랜치 1–2단계 (커밋 3039cd2…0b7aaab)*
