# ChanneledInteractionBehaviour 분리 — 상속에서 합성으로

`ChanneledInteractionBehaviour`가 한꺼번에 들고 있던 세 능력을 독립 `NetworkBehaviour` 컴포넌트로 쪼개는 작업의 근거와 함정을 모아 둔다. 코드 주석은 여기를 가리킨다.

## 왜 바꾸는가

한 추상 클래스가 서로 무관한 세 가지를 제공하고 있었고, `ItemBase`가 그걸 상속하는 탓에 **모든 아이템이 안 쓰는 능력을 강제로 받았다.**

| 능력 | 내용 |
|---|---|
| **A 채널링** | 게이지 표시·숨김 + 루프음 (#184, #483) |
| **B 오너 로그** | `NotifyOwner` — 서버 판정을 오너 콘솔에 (#91) |
| **C 토스트** | `ToastOwner` — 오너 화면 토스트 (#309 → #525) |

`Rope`·`PortableMinimap`·`ShopCatalogItem`은 셋 다 안 쓰는데 `[Rpc]` 4개를 짊어졌다. `ShopCatalogItem`은 자기 주석에 "네트워크 경로가 없다"고 쓰면서도 그랬다. `ItemBattery`와 `PlayerLootable`은 "피드백만 쓰려고 상속한다"고 주석으로 사과하고 있었다.

## 왜 인터페이스가 아니라 합성인가

먼저 검토하고 **기각한** 것들:

- **인터페이스로 분리** — Netcode ILPP는 `NetworkBehaviour`를 상속한 클래스에 **직접 선언된** 메서드만 RPC로 위빙한다. 인터페이스 디폴트 구현에는 위빙이 안 붙어 RPC가 실제로 안 나간다.
- **섞어 상속**(`class Scanner : ItemBase, ChanneledBase`) — C# 단일 상속이라 CS1721. 여러 개 상속되는 건 인터페이스뿐인데 위 이유로 막힌다.
- **계단 상속**(`ItemBase → FeedbackItemBase → ChanneledItemBase`) — 계단은 **중첩된 부분집합만** 표현한다. 그런데 실사용 조합에 `A+B`(Taser)와 `B+C`(ItemBattery)가 있고 이 둘은 포함 관계가 아니라 한 계단에 담기지 않는다. 능력 k개면 조합은 2^k인데 계단은 k+1개만 표현한다 — **상속은 능력 조합의 축이 아니다.**

남는 건 합성뿐이고, 실제로 능력 추가가 클래스 1개 추가로 끝난다(O(n)).

## 구조

능력은 독립 `NetworkBehaviour` 컴포넌트이고, 선택은 소비자 클래스의 `[RequireComponent]` 선언이 단일 출처다.

```csharp
// C#이 막는 것
public class Scanner : ItemBase, OwnerFeedback, ChannelGauge   // ❌ CS1721

// 합법이고 사실상 같은 것
[RequireComponent(typeof(ChannelGauge))]
public class Scanner : ItemBase { ... }
```

소유권은 `NetworkObject` 단위지 `NetworkBehaviour` 단위가 아니라서, `ChannelGauge.IsOwner`는 `Scanner.IsOwner`와 항상 같다 — `SendTo.Owner`가 닿는 피어가 분리 전후로 동일하다는 것이 이 구조의 안전 근거다.

`ReviveKit`의 자가 부활이 플레이어가 아니라 **아이템**에 있는 이유(#820)도 그대로 유지된다: `ChannelGauge`가 아이템 프리팹에 붙으므로 오너 라우팅이 아이템의 소유권을 따라가고, Die 중 플레이어 오브젝트의 오너가 서버로 넘어가도(#763) 영향받지 않는다.

## 함정

### 1. 런타임 `AddComponent` 금지

NGO는 스폰 시점에 `NetworkBehaviour` 목록을 만들고 **그 인덱스로 RPC를 라우팅**한다. 실행 중에 컴포넌트를 추가하면 피어 간 인덱스가 어긋나 RPC가 엉뚱한 behaviour로 간다. 능력 컴포넌트는 반드시 프리팹에 박혀 있어야 하고, "없으면 런타임에 붙이자"는 폴백을 절대 넣으면 안 된다.

(이 프로젝트는 `NetworkBehaviourReference`/`NetworkBehaviourId`를 한 곳도 쓰지 않아, 컴포넌트 추가로 인한 인덱스 이동 자체는 무해함을 확인했다.)

### 2. `[RequireComponent]`는 프리팹 자산을 고쳐 주지 않는다

Unity는 기존 프리팹을 열 때 빠진 필수 컴포넌트를 **메모리에** 보정하고 콘솔에 `Creating missing ChannelGauge component for ...`까지 찍는다. **하지만 .prefab 파일은 그대로다.** 저장하지 않은 채 빌드하면 런타임에 컴포넌트가 없다.

### 3. 그 누락은 로드된 프리팹으로는 감지할 수 없다

위 자동 보정 때문에 `PrefabUtility.LoadPrefabContents`가 돌려주는 사본에는 컴포넌트가 **이미 올라와 있다.** 그래서 `GetComponent(type) != null`로 점검하면 누락이 항상 "없음"으로 나온다 — 점검이 눈을 감는다. (실제로 처음 만든 점검 스크립트가 이 함정에 걸려 "누락 없음"을 보고했고, 같은 시점에 디스크의 .prefab에는 guid가 없었다.)

**판정은 직렬화된 YAML 텍스트에 스크립트 guid가 있는지로 해야 한다.** `Assets/Scripts/Editor/CapabilityComponentSync.cs`가 그렇게 한다:

- `Tools/능력 컴포넌트/프리팹 점검` — 선언과 자산 상태를 대조해 보고만
- `Tools/능력 컴포넌트/프리팹 반영` — 누락분을 붙이고 저장

반영은 로드 사본에 이미 보정된 컴포넌트를 그대로 저장하는 방식이라 멱등하다. 능력을 추가할 때는 그 스크립트의 `s_capabilities` 화이트리스트에 타입을 더하면 된다 — 무관한 `RequireComponent`(Rigidbody 등)까지 임의로 붙지 않게 하는 장치다.

### 4. 컴포넌트 참조는 lazy로

`Awake`에서 캐싱하지 않는다. `Scanner`가 이미 `private void Awake()`를 갖고 있어, 기반에 `Awake`를 새로 넣으면 조용히 가려지기 때문이다. 또 `ReviveKit`의 오프라인 자가 부활처럼 스폰 전에 처음 평가되는 경로가 있다. lazy + `Debug.LogError` 폴백이면 두 경우 모두 안전하다.

## 게이지 토큰 — 남은 문제

`ChannelingGaugeUI`의 소유 토큰은 참조 동일성만 보는 `object`고, `Hide`는 토큰이 다르면 무시한다. 분리 전에는 채널링 클래스마다 토큰이 달랐지만, 분리 후 플레이어 오브젝트에서는 `PlayerReviver`와 `PlayerEscortCommands`가 **`ChannelGauge` 하나를 공유**한다.

그런데 애초에 이 둘 사이에는 **상호 배제 장치가 없다** — 합류 채널링 중 E를 누르면 구조 채널이 그대로 시작된다. 증상만 바뀐다:

- 분리 전: 먼저 시작한 쪽의 `Hide`가 영구 무동작 → 게이지가 남는다
- 분리 후: 토큰이 같아져 한쪽의 `End()`가 다른 쪽 게이지를 끈다

현재 `Player.prefab`의 `m_channelSeconds`가 `0`이라 합류 게이지가 뜨지 않아 **관측 불가능한 잠재 버그**다. 이 값을 0보다 크게 되돌리기 전에 별도로 처리한다. 선택지는 (a) 호출자를 `NetworkBehaviourReference`로 실어 채널별 토큰 구분, (b) 이미 활성이면 두 번째 `Begin`을 거부 + 경고.

`PlayerLoadout.EquipSlot`은 이전 아이템의 게이지를 내릴 때 토큰이 필요하므로, 아이템 인스턴스가 아니라 그 아이템의 `ChannelGauge`로 내린다(`HideLocal`). 분리하면서 여기를 같이 고치지 않으면 슬롯 전환 시 게이지가 화면에 박히는 #455 회귀가 난다.

## 진행 단계

| 단계 | 내용 | 상태 |
|---|---|---|
| ① | `ChannelGauge` 분리 — 소비자 6개, 프리팹 5개 | 완료 (Play 테스트 대기) |
| ② | `ToastFeedback` 분리 — 소비자 2개(Scanner·ItemBattery), 프리팹 1개 | 미착수 |
| ③ | `OwnerFeedback` 분리 + `HasServerAuthority` 확장 메서드화 + 기반 삭제, `ItemBase : NetworkBehaviour` | 미착수 |

**남은 작업의 구체적인 절차·호출부 목록·확인 항목은
[channeled-interaction-split-handoff.md](channeled-interaction-split-handoff.md)에 있다.**
