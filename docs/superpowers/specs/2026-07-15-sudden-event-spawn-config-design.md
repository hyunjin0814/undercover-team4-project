# 스폰형 돌발 이벤트 설정 리스트화 설계 (이슈 #106)

- **이슈**: [#106 돌발 이벤트 시스템 (거리 난동자 / 괴한 습격 / 전자기기 먹통)](https://github.com/hyunjin0814/undercover-team4-project/issues/106)
- **작성일**: 2026-07-15
- **기획 근거**: GDD 6-4(일반 이벤트), 7-4
- **브랜치**: `feature/106-random-events`
- **선행 문서**: `2026-07-14-random-event-system-design.md` (⚠ 구현과 어긋남 — 아래 8장 참고)

## 1. 문제

스폰형 돌발 이벤트를 하나 늘리려면 `SpawnedNpcEvent` **컴포넌트를 하나 더 붙여야 한다**. 현재 `NPCMove.unity`의 이벤트 오브젝트에는 같은 컴포넌트가 2개 쌓여 있고(거리 난동자·나체 난동꾼), 종류가 늘수록 인스펙터에 동일 컴포넌트가 계속 쌓인다.

두 인스턴스의 실제 설정값은 다음과 같다:

| 이름 | 모드 | 프리팹 GUID | 거리 min/max | NavMesh 샘플 | 시도 | 수익 | 수명 |
|---|---|---|---|---|---|---|---|
| 나체 난동꾼 | `Flee`(1) | `5ec59267efd14b0409911db86746a985` | 6 / 12 | 4 | 8 | 50 | 60 |
| 거리 난동자 | `Resist`(0) | `d683626f03b237a4da9faa4542cc57a0` | 6 / 12 | 4 | 8 | 50 | 60 |

**이름·모드·프리팹을 빼면 두 항목은 완전히 동일하다.** 컴포넌트가 아니라 데이터 항목이어야 한다는 신호다.

## 2. 목표 / 비목표

**목표** — 스폰형 이벤트를 인스펙터 리스트에 `정보 + 프리팹` 항목으로 추가해 늘린다. 코드 수정 없이 종류를 늘릴 수 있어야 한다.

**비목표**
- 괴한 습격·전자기기 먹통은 건드리지 않는다. 각각 세상에 하나뿐인 이벤트라 컴포넌트 1개가 자연스럽고, 고유 필드(타격 사거리·재밍 반경 등)가 서로 완전히 달라 한 리스트에 넣으면 항목마다 안 쓰는 필드가 보인다.
- 게임 동작 변경 없음. 이것은 순수 리팩터다. 발생 확률·동시 발생 가능 여부·스폰/판정 로직은 그대로여야 한다.
- 에디터 확장(프리팹 드래그 시 항목 자동 생성 등)은 범위 밖.

## 3. 설계 결정

### 3-1. 설정 데이터 위치 — 인라인 리스트

`[Serializable]` 설정 클래스 + `List<>`를 컴포넌트에 둔다. ScriptableObject 프로필도 검토했다(CLAUDE.md의 "프로필·스탯 데이터는 ScriptableObject" 규약, GDD 5-3/10-4). 하지만 그 규약은 용의자 프로필·대조 데이터 같은 **게임 콘텐츠 데이터**를 겨냥한 것이고, 여기서 다루는 것은 이벤트 튜닝 수치다. 항목 2~4개에 에셋 파일을 그만큼 만드는 것은 이득 대비 절차가 무겁고, "인스펙터에서 항목만 늘린다"는 목표와도 멀어진다.

**트레이드오프(수용함)**: 설정이 씬에 들어가므로 다른 씬에서 재사용되지 않고, 이벤트를 늘릴 때마다 씬 파일 diff가 생긴다. 현재 돌발 이벤트를 쓰는 씬이 `NPCMove.unity` 하나뿐이라 실질적 비용이 낮다. 씬이 여러 개로 늘고 팀 머지 충돌이 실제로 발생하면 그때 ScriptableObject로 옮긴다.

### 3-2. 매니저 수집 방식 — 제공자 인터페이스

`SuddenEventManager.Awake`의 `GetComponents(m_events)`는 **"컴포넌트 1개 = 이벤트 1개"를 가정**한다. 리스트로 가면 컴포넌트 1개가 이벤트 N개를 품으므로 이 가정이 깨진다. 세 가지를 검토했다.

| 안 | 내용 | 판정 |
|---|---|---|
| **A. 제공자 인터페이스** | `ISuddenEventProvider`를 추가해 매니저가 컴포넌트형 + 제공자형을 모두 수집 | **채택** |
| B. Set이 `ISuddenEvent` 하나인 척, 내부에서 재추첨 | 매니저 무변경 | **탈락** — 동작이 바뀐다 |
| C. 런타임에 항목마다 `AddComponent` | 매니저·로직 무변경 | **탈락** — 목적 배신 |

**B를 버린 이유**: 추첨 단위가 바뀐다. 현재는 4종이 각각 균등 후보인데, B에서는 "스폰형 묶음 / 괴한 / 먹통"의 3택이 되고 거리 난동자·나체 난동꾼은 각각 1/6으로 떨어진다. 또 `IsActive`가 묶음 단위가 되어 거리 난동자가 진행 중이면 나체 난동꾼이 발생하지 못한다 — 현재는 동시 발생이 가능하다. 리팩터가 밸런스를 조용히 바꾸면 안 된다.

**C를 버린 이유**: 플레이 중 인스펙터에 컴포넌트가 도로 쌓인다. 지저분함을 없애려는 작업이 실행 시점에 그것을 되살린다.

**A의 장점**: 기존 설계 원칙인 "**매니저는 어떤 이벤트가 있는지 모른다**"(`SuddenEventManager` 클래스 주석)가 유지된다. 매니저는 인터페이스 뒤에서 "언제 부를지"만 정한다.

## 4. 아키텍처

매니저의 **수집 지점 한 곳만** 넓힌다. 스케줄링·추첨·틱·`ClientRpc` 알림은 전부 무변경이다.

### 파일

| 파일 | 상태 | 역할 |
|---|---|---|
| `Events/ISuddenEventProvider.cs` | 신규 | `void CollectEvents(List<ISuddenEvent> into)` 하나. 기존 버퍼 재사용 스타일에 맞춰 할당 없이 채운다. |

> **`CollectEvents` 계약**: `into`를 **비우지 않고 덧붙인다**. 매니저가 `GetComponents`로 컴포넌트형을 먼저 채운(그 과정에서 풀이 비워진) 뒤 제공자들이 같은 풀에 얹는 순서이므로, 제공자가 `Clear()`를 부르면 괴한·먹통이 조용히 사라진다.
| `Events/SpawnedNpcEvent.cs` | 변경 | `MonoBehaviour` → `[Serializable]` 순수 C# 클래스. `ISuddenEvent` 구현·스폰/판정/이탈 로직은 그대로. |
| `Events/SpawnedNpcEventSet.cs` | 신규 | `MonoBehaviour, ISuddenEventProvider`. `List<SpawnedNpcEvent>` 보유. 인스펙터에서 늘리는 지점. |
| `Events/SuddenEventManager.cs` | 변경 | `Awake` 수집만 확장 (컴포넌트형 + 제공자형). |

### 수명주기

순수 클래스에는 `Awake`/`OnEnable`/`OnDisable`이 없으므로 Set이 대신 돌려준다:

- `Set.Awake` → `ArrestJudge` 1회 탐색 → 각 항목에 `Initialize(owner, judge)`
- `Set.OnEnable` / `Set.OnDisable` → 각 항목의 `Subscribe()` / `Unsubscribe()`

`owner`는 `MonoBehaviour` 참조다. 순수 클래스에서 `Debug.LogWarning(msg, this)`의 컨텍스트 인자로 쓸 `UnityEngine.Object`가 없고, `Instantiate`도 `UnityEngine.Object.Instantiate`로 호출해야 하기 때문이다.

**부수 개선**: `ArrestJudge` 탐색이 이벤트마다 `FindFirstObjectByType` 하던 것에서 Set에서 1회로 줄어든다.

### 배치 규약 유지

`SpawnedNpcEventSet`에 `[RequireComponent(typeof(SuddenEventManager))]`를 붙여 "이벤트는 매니저와 같은 오브젝트에 둔다"는 기존 규약을 유지한다. 매니저가 자식을 훑지 않는 이유(자식 배치 오용 시 두 번째 매니저가 자동 생성되어 조용히 잘못 동작)는 그대로 유효하다.

## 5. 데이터 흐름

**Awake (매니저)** — 컴포넌트형 `ISuddenEvent`(괴한·먹통) 수집 → 제공자형 `ISuddenEventProvider`(스폰 Set) 수집 → 각 제공자가 자기 항목들을 같은 풀에 추가. 결과는 이벤트 4개가 든 평평한 풀 하나로, **현재와 동일하다**.

**런타임** — 무변경. 추첨 대상은 4종 각각, 동시 발생 가능, `ServerBegin`/`ServerTick`/`ServerReset` 호출 규약 동일.

**네트워크** — 무변경. Set은 plain MonoBehaviour다(현재 `SpawnedNpcEvent`와 동일). 항목들은 서버·오프라인에서만 돌고 스폰물은 자기 `NetworkObject`로 복제된다 (#56 패턴).

## 6. 마이그레이션

**위험**: `SpawnedNpcEvent`가 MonoBehaviour가 아니게 되는 순간, 씬의 기존 컴포넌트 2개는 missing script가 되고 **직렬화된 설정값이 소실된다**.

**완화**: 1장의 표에 현재 값 전량을 확보해 두었다. 이 문서가 값의 출처다.

**절차** (Unity MCP 또는 에디터 수동)
1. `NPCMove.unity`의 이벤트 오브젝트에서 `SpawnedNpcEvent` 컴포넌트 2개 제거
2. `SpawnedNpcEventSet` 컴포넌트 추가
3. 1장 표의 값으로 리스트 2항목 작성
4. 씬 저장 후 Play 모드 검증(7장)

**범위**: 돌발 이벤트를 쓰는 씬은 `NPCMove.unity` 하나뿐이다(`Main Scene.unity`에는 `SuddenEventManager`가 없음). 마이그레이션 대상은 1개 씬 / 2개 항목.

## 7. 에러 처리 · 검증

### 에러 처리

| 상황 | 동작 |
|---|---|
| 항목의 프리팹이 비어 있음 | 해당 항목의 표시명을 짚어 경고, 그 항목만 발생하지 않음 (현 동작 유지) |
| 리스트가 비어 있음 | 스폰형 이벤트 0개. 괴한·먹통은 정상 동작 |
| `ArrestJudge`를 못 찾음 | Set에서 1회 경고 (현재는 이벤트마다 2회) |

### 검증 (Play 모드)

Unity Test Framework 코드가 아직 없으므로 Play 모드 수동 검증으로 한다.

1. `NPCMove.unity`에서 라운드 `InProgress` 진입 → 거리 난동자·나체 난동꾼이 **각각** 발생하는지
2. 두 스폰형이 **동시에** 활성화될 수 있는지 (B안이 깨뜨렸을 지점 — 회귀 확인)
3. 제압 → 연행 → HQ 인계 → 경범죄 판정·수익 흐름이 그대로인지
4. **리스트에 항목을 하나 더 추가해 3종으로 늘렸을 때 코드 수정 없이 발생하는지** — 이 리팩터의 목적 자체를 검증한다
5. Unity 콘솔 컴파일 에러·경고 0건

## 8. 후속 (범위 밖)

- **`2026-07-14-random-event-system-design.md`가 구현과 어긋난다.** 문서는 `RandomEventManager` + `RandomEventDefinition`(ScriptableObject)를 기술하나 실제 구현은 `SuddenEventManager` + `ISuddenEvent` 컴포넌트다. 괴한 습격도 문서에는 "제압 가능한 강적"이나 코드는 "제압 대상이 아닌 위협"(GDD 7-4와 일치)이다. 프레임워크 PR에 이 문서가 함께 올라가므로 그 전에 정리 필요.
- PR #187을 프레임워크 base PR로 축소하고 이벤트 4종을 그 위에 스택으로 분할하는 작업 (별도 진행).
