# ChanneledInteractionBehaviour 분리 — 인수인계

다른 세션이 이어받아 작업할 수 있게 현재 상태와 남은 일을 정리한다.
**설계 근거·함정은 [channeled-interaction-split.md](channeled-interaction-split.md)가 정본이다.** 이 문서는 "지금 어디까지 했고 다음에 뭘 하냐"만 담는다.

작성 시점: 2026-09-11 / 브랜치 `main` / 마지막 커밋 `8b9145fc`

---

## 한 줄 요약

`ChanneledInteractionBehaviour`가 들고 있던 세 능력(채널링 A / 오너 로그 B / 토스트 C)을 독립 `NetworkBehaviour` 컴포넌트로 쪼개는 3단계 작업. **① 완료(커밋 전), ②③ 미착수.**

---

## 현재 상태

### 단계 ① 완료 — 아직 커밋 안 됨

`ChannelGauge` 분리가 코드·프리팹 모두 끝났고 컴파일도 통과했다. **다만 Play 테스트를 아직 안 했다.**

**신설**

| 파일 | 내용 |
|---|---|
| `Assets/Scripts/Core/ChannelGauge.cs` | 게이지·루프음 능력 컴포넌트 (`Begin`/`End`/`HideLocal` + RPC 2개) |
| `Assets/Scripts/Editor/CapabilityComponentSync.cs` | 프리팹 점검/반영 메뉴 — ②③에서 그대로 재사용 |
| `docs/channeled-interaction-split.md` | 근거·함정 아카이브 (코드 주석이 이걸 가리킨다) |

**수정**

- `Core/ChanneledInteractionBehaviour.cs` — 채널링(A) 멤버 전부 삭제. B·C만 남아 클래스는 유지
- 소비자 6개 — `Taser`, `Scanner`, `ReviveKit`, `HomeRunBaton`, `PlayerEscortCommands`, `PlayerReviver`: `[RequireComponent(typeof(ChannelGauge))]` + lazy `Gauge` 프로퍼티 + 호출부 전환
- `Player/Inventory/PlayerLoadout.cs` — **토큰 회귀 수정**(아래 참고)
- 프리팹 5개 — `Items/{Taser,Scanner,ReviveKit,HomeRunBaton}.prefab`, `Player.prefab`

**커밋 범위 주의.** 작업 시작 시점부터 이미 더러웠던 무관한 변경이 섞여 있다 — **커밋에 넣지 말 것**:
`Assets/Animation/Player.controller`, `Assets/CCTVTest/cam1RenderTexture.renderTexture`,
`Assets/Scenes/Maps/Map_Apocalypse.unity`, `docs/quaternion-compression.md`,
`Assets/AddressableAssetsData/**`, `.agents/`, `.codex/`, `AGENTS.md`

### 결정된 사항

- **PR 전략**: 한 브랜치에서 작업하고 PR만 단계별로 3개 분리 (사용자 확정)
- **게이지 토큰**: 현행 유지 + 후속 이슈로 분리 (사용자 확정 — 아래 "후속 이슈")
- **Play 테스트는 사용자가 수행한다.** 이어받는 세션은 Play 모드 진입 금지. 컴파일·프리팹 점검까지만 검증하고 결과를 보고할 것

---

## 지금 확인해야 할 것 (단계 ① Play 테스트)

커밋·②진행 전에 사용자가 확인할 항목. Multiplayer Play Mode로 호스트+클라 둘 다 볼 것 — 원격 클라가 오너인 경로가 RPC를 타기 때문이다.

- [ ] **스캐너** 채널링 게이지 + 루프 판독음 (`m_channelSeconds > 0`으로 둔 경우). 0이면 즉시 스캔이라 게이지가 안 뜨는 게 정상
- [ ] **테이저** 발사 후 충전 게이지가 차오름
- [ ] **⚠ 슬롯 전환 시 게이지가 사라짐** — 테이저 충전 중 다른 슬롯으로 바꿨다가 돌아오기. **이번에 고친 회귀 지점이라 가장 중요하다.** 게이지가 화면에 박혀 남으면 실패
- [ ] **테이저 재장착 이어 표시**(#455) — 충전 중 슬롯을 바꿨다 돌아오면 남은 만큼부터 이어서 차오름
- [ ] **부활 키트** 자가 부활 채널링 게이지 + 루프음 (Down/Die 중 E 홀드)
- [ ] **홈런 진압봉** 차지 게이지 + 차지음 (좌클릭 홀드)
- [ ] **구조 채널링**(PlayerReviver) 게이지 + 루프음, 취소 시 소리도 같이 멈춤
- [ ] 밧줄 줄다리기 합류 채널링 — 현재 `Player.prefab`의 `m_channelSeconds: 0`이라 **게이지가 안 뜨는 게 정상**

---

## 단계 ② — ToastFeedback 분리 (PR 2)

가장 작다. 소비자 2개, 프리팹 1개.

### 신설: `Assets/Scripts/Core/ToastFeedback.cs`

`ChanneledInteractionBehaviour`의 C 부분(`ToastOwner`, `OwnerToastRpc`, `RaiseOwnerToast`)을 옮긴다. **`protected virtual RaiseOwnerToast` override 패턴을 이벤트로 뒤집는다** — 상속이 없어지면 override할 자리가 없기 때문:

```csharp
public class ToastFeedback : NetworkBehaviour
{
    public event Action<EItemFeedback> OnToast;

    public void ToastOwner(EItemFeedback feedback) { /* 기존 분기 그대로 */ }

    [Rpc(SendTo.Owner)]
    private void OwnerToastRpc(EItemFeedback feedback) { /* ... */ }
}
```

### 소비자

**`Item/Tools/Scanner.cs`**
- `RaiseOwnerToast` override(현재 75행) 삭제
- `ToastOwner(...)` 4곳(170·187·280·339행) → `Toast.ToastOwner(...)`
- **`public event Action<EItemFeedback> OnScanFeedback`(66행)은 반드시 유지.** `UI/Scan/ScanResultPresenter.cs`가 315행에서 구독하고 303행에서 해제한다 — 건드리면 그쪽까지 번진다
- 기존 `Awake`(`private void Awake()`)에서 **eager 구독**: `m_toast.OnToast += f => OnScanFeedback?.Invoke(f);`
  (게이지와 달리 lazy로 두면 배터리가 먼저 발행한 토스트를 놓친다)
- **91행 `m_battery.OnChargeToast += RaiseOwnerToast;` 중계는 통째로 삭제.** Scanner와 ItemBattery가 `Scanner.prefab` 루트에 같이 있어 `ToastFeedback` 하나를 공유하므로 손으로 이어줄 필요가 없어진다

**`Item/Power/ItemBattery.cs`**
- `RaiseOwnerToast` override(45행) 삭제
- `ToastOwner(FullyChargedFeedback)`(84행) → `Toast.ToastOwner(...)`
- `public event Action<EItemFeedback> OnChargeToast`(42행) — 구독자가 Scanner 중계뿐이라 **같이 삭제 가능**. 삭제 전 다른 구독자가 없는지 grep으로 재확인할 것

### 기반 정리

`ChanneledInteractionBehaviour`에서 C 관련 멤버 삭제. B만 남고 클래스는 유지되어 컴파일된다.

### 프리팹

`Assets/Prefabs/Items/Scanner.prefab` 1개. (`ItemBattery`는 이 프리팹에만 존재한다)
`CapabilityComponentSync.cs`의 `s_capabilities`에 `typeof(ToastFeedback)` 추가 후 아래 "프리팹 반영" 절차대로.

### 확인 포인트 (Play)

스캔 실패 토스트(범위 이탈·먹통·이미 스캔함)와 **배터리 완충 토스트가 스캐너 토스트 채널로 뜨는지** — 중계를 지웠으므로 여기가 핵심이다.

---

## 단계 ③ — OwnerFeedback 분리 + 기반 삭제 (PR 3)

가장 크다. 소비자 10개, 프리팹 8개.

### 신설

- `Assets/Scripts/Core/OwnerFeedback.cs` — `NotifyOwner(string)` + `OwnerLogRpc`
- `Assets/Scripts/Core/NetworkBehaviourExtensions.cs` — `HasServerAuthority()` 확장 메서드

`HasServerAuthority`는 `!IsSpawned || IsServer`뿐이라 컴포넌트가 아니라 **확장 메서드**로 뺀다. 스폰 전 경로(`HealPack`, `ReviveKit`)에서도 안전하고 `GetComponent` 의존이 없다. 이 덕분에 **`HealPack`은 능력 컴포넌트가 0개**가 된다.

```csharp
public static class NetworkBehaviourExtensions
{
    public static bool HasServerAuthority(this NetworkBehaviour self) =>
        !self.IsSpawned || self.IsServer;
}
```

호출부는 `HasServerAuthority` → `this.HasServerAuthority()`로 바뀐다.

### 소비자 — `NotifyOwner` 10개 클래스

`Taser`, `Baton`, `Scanner`, `ReviveKit`, `AreaScanner`, `ItemBattery`,
`PlayerReviver`, `PlayerEscortCommands`, `PlayerEscorter`, `PlayerLooter`

### 소비자 — `HasServerAuthority` 6개 클래스

`Scanner`, `ReviveKit`, `HealPack`, `AreaScanner`, `ItemBattery`, `PlayerLooter`

### ⚠ 동명이인 — 건드리지 말 것

다른 클래스의 **private 동명 멤버**가 따로 있다. grep 결과에 섞여 나오니 주의:

- `HasServerAuthority`: `Events/Abduction/AbductionEvent.cs`, `Events/Ufo/UfoCraft.cs`, `Events/Ufo/UfoAbductor.cs`, `Interaction/Jail/JailIntake.cs`
- `NotifyOwner`: `Player/Combat/PlayerKillCredit.cs`, `Player/Escort/PlayerCarrier.cs`

판별법: 해당 파일이 `ChanneledInteractionBehaviour`를 상속하는지 먼저 볼 것.

### ⚠ `PlayerEscorter`의 다른 인스턴스 호출

`Player/Escort/PlayerEscorter.cs`에 `holder.NotifyOwner(...)`가 있다 — 같은 클래스라 `protected` 접근이 됐던 것. `OwnerFeedback`은 public이므로 라우팅만 해 주면 된다:
`holder.GetComponent<OwnerFeedback>()?.NotifyOwner(...)` 또는 `PlayerEscorter`에 `internal OwnerFeedback Feedback` 접근자를 노출.

### 마무리

- `Assets/Scripts/Core/ChanneledInteractionBehaviour.cs` **삭제**
- `Item/Core/ItemBase.cs` → `public abstract class ItemBase : NetworkBehaviour` + 클래스 주석 갱신
  외부는 전부 `ItemBase` 타입으로만 참조하므로 영향 없다 (`ShopCatalog.cs`, `ShopDelivery.cs`, `Pickpocket.cs`, `Editor/ItemIconBaker.cs`)
- **`<see cref>` 깨짐 수정(CS1574 경고로 잡힌다)**: `Player/Loot/PlayerLooter.cs:360`, `Player/Loot/PlayerLootable.cs:21`, `Player/Escort/PlayerEscortCommands.cs`
- **사과 주석 삭제**: `ItemBattery.cs:12`("피드백 쓰려고 상속한다"), `PlayerLootable.cs:20-23, 55-61`
- **`PlayerLootable`은 `: NetworkBehaviour`로.** 세 능력 다 안 쓴다 — 자체 타겟 RPC를 따로 쓰고 있어 원래 이 계층에 있을 이유가 없던 클래스다
- 텍스트 언급 갱신: `JailDoor.cs`, `Taser.cs`, `PlayerEscorter.cs`, `PlayerEscortCommands.cs`, `PlayerReviver.cs`

### 프리팹 8개

`Items/{Taser,Baton,HomeRunBaton,ToyHammer,Scanner,ReviveKit,AreaScanner}.prefab` + `Player.prefab`

**`ToyHammer`를 빠뜨리지 말 것** — `Item/Weapons/ToyHammer.cs`가 `Baton`을 상속해 `NotifyOwner`를 물려받는다. `Baton` 상속 체인은 `HomeRunBaton`·`ToyHammer` 2갈래다.

### 최종 상태 — 능력 0개가 되어야 하는 것

`Rope`, `PortableMinimap`, `ShopCatalogItem`, `HealPack`, `PlayerLootable`.
점검 메뉴로 확인하고, 이 프리팹들에 컴포넌트가 붙지 않았는지도 함께 볼 것.

---

## 작업 방법

### 컴파일 검증

```bash
dotnet build Assembly-CSharp.csproj -v quiet -nologo
```

**⚠ 새 .cs 파일을 추가한 직후에는 이게 거짓 에러를 낸다.** csproj의 파일 목록이 스테일이라 `CS0246: 형식을 찾을 수 없습니다`가 새 타입 전부에 뜬다. Unity가 임포트해 csproj를 재생성해야 한다 — MCP가 연결돼 있으면 `refresh_unity(compile=request, mode=force)` 후 `read_console`로 보는 쪽이 정본이다. 에디터에서 직접 하면 창을 한 번 포커스하면 된다.

에러가 전부 "새 타입을 못 찾겠다" 한 종류면 스테일이고, 그 외 에러가 섞여 있으면 진짜다.

### 프리팹 반영

1. `CapabilityComponentSync.cs`의 `s_capabilities` 배열에 새 능력 타입 추가
2. Unity 컴파일 대기
3. 메뉴 `Tools/능력 컴포넌트/프리팹 점검` — 누락 목록 확인
4. 메뉴 `Tools/능력 컴포넌트/프리팹 반영` — 추가·저장
5. 다시 `점검` → "누락 없음"
6. `git status -- Assets/Prefabs/`로 실제 디스크 반영 확인 (**이게 최종 확인이다**)

멱등하므로 여러 번 돌려도 안전하다.

### ⚠ 절대 하지 말 것

- **런타임 `AddComponent`** — NGO가 스폰 시점 NetworkBehaviour 인덱스로 RPC를 라우팅한다. 실행 중 추가하면 피어 간 인덱스가 어긋난다
- **로드된 프리팹으로 누락 판정** — `LoadPrefabContents`는 자동 보정된 사본을 준다. 반드시 직렬화된 YAML을 볼 것 (자세한 건 [근거 문서](channeled-interaction-split.md) "함정" 절)
- **브랜치 이동** — 한 브랜치에서 계속 작업하고 PR만 나눈다

---

## 후속 이슈 (이번 작업 범위 밖)

**게이지 토큰 충돌.** `PlayerReviver`와 `PlayerEscortCommands` 사이에 상호 배제 장치가 없다 — 합류 채널링 중 E를 누르면 구조 채널이 그대로 시작된다. 분리 후 둘이 `ChannelGauge` 하나를 공유하므로 증상이 "한쪽 Hide가 무동작"에서 "한쪽 `End()`가 남의 게이지를 끔"으로 바뀐다.

현재 `Player.prefab`의 `m_channelSeconds: 0`이라 합류 게이지가 안 떠서 **관측 불가능한 잠재 버그**다. **이 값을 0보다 크게 되돌리기 전에** 별도 이슈로 처리할 것.

선택지: (a) 호출자를 `NetworkBehaviourReference`로 실어 채널별 토큰 구분 / (b) 이미 활성이면 두 번째 `Begin`을 거부하고 경고 로그.
