# 돌발 이벤트 시스템 설계 (이슈 #106)

- **이슈**: [#106 돌발 이벤트 시스템 (거리 난동자 / 괴한 습격 / 전자기기 먹통)](https://github.com/hyunjin0814/undercover-team4-project/issues/106)
- **작성일**: 2026-07-14
- **기획 근거**: GDD 6-4(일반 이벤트), 6-5, 7-4
- **브랜치**: `feature/106-random-events`

## 1. 스코프

이번 구현 범위는 **돌발 이벤트 프레임워크 + 이벤트 2종(거리 난동자, 괴한 습격)**.

- **거리 난동자** — 약한 전투 NPC. 제압 시 경범죄 수익.
- **괴한 습격** — 강한 전투 NPC(난동자 강화판). 제압 시 더 높은 수익.
- **전자기기 먹통** — **이번 스코프에서 제외**. 시야·HUD 연출이 필요하고 NPC 없는 전역 이벤트라 별도 작업(후속 이슈)으로 분리.

두 이벤트 모두 "제압 가능한 전투 NPC"라 기계적으로 동일하다(강도만 다름). 따라서 기존 `NpcController`의 `Attack`(범위 타격 + 소란 + 제압 게이지) 메커니즘을 그대로 재사용하고, 강/약 차이는 인스펙터 스탯 + 보상값으로 표현한다.

> 참고: 이슈 원문은 괴한 습격을 "제압 대상이 아닌 위협"으로 적었으나, 팀 결정으로 "제압 가능한 강적(난동자 강화판)"으로 변경했다.

## 2. 아키텍처

서버 권위 랜덤 이벤트 시스템. `RoundManager`·`NpcSpawner`와 동일하게 **plain MonoBehaviour + 서버 게이트**(오프라인 폴백 포함) 패턴을 따른다.

### 신규 파일 (`Assets/Scripts/Events/`)

| 파일 | 종류 | 역할 |
|---|---|---|
| `RandomEventManager.cs` | MonoBehaviour (서버/오프라인 권위) | 랜덤 타이머 → 가중치 선택 → 적대 NPC 스폰(정의 기반) → 그 NPC 상태 구독으로 제압 감지·보상 발행 → 활성/동시수/쿨타임/라운드 종료 정리. `OnEventResolved(ArrestResult)` 발행. |
| `RandomEventDefinition.cs` | ScriptableObject (데이터) | 이벤트 정의 — 표시명·가중치·적대 NPC 프리팹·보상액·지속시간·종류. 난동자/괴한 = 에셋 2개. |

### 신규 에셋 (프리팹)

기존 NPC 프리팹의 **`NpcController` 변형 2개** — 새 스크립트 부착 없이 직렬화 필드 값만 다르게:

- **거리 난동자**: 낮은 `m_subdueGaugeMax`(약한 제압 저항), 낮은 `m_resistAttackDamage`.
- **괴한 습격**: 높은 `m_subdueGaugeMax`, 높은 `m_resistAttackDamage`.

각 변형은 `NetworkObject`·`NetworkTransform`·`NpcSubdueInteractable` 등 기존 NPC 컴포넌트 구성을 그대로 갖는다. 단, 시민 신원/외형/범인 배정과 무관하므로 `CitizenIdentity`는 없어도 된다(이벤트 NPC는 `ArrestJudge`가 아니라 매니저가 직접 보상 발행).

### 재사용 (수정 없음)

`NpcController`(Attack 범위타격 + 소란 + 제압 게이지), `NpcResistState`, `NpcSubdueInteractable`, `PlayerEscorter`(제압 서버 경로), `NpcController.BroadcastDisturbance`, `IDamageable`(플레이어 HP 데미지 → #105 다운 연동), `ArrestResult`/`ArrestVerdict.Misdemeanor`.

### 확장성 (먹통은 나중에)

지금은 이벤트가 "적대 NPC 스폰" 한 종류뿐이라 이벤트-종류 추상화(`RandomEventBase` 등)는 만들지 않는다(YAGNI). `RandomEventDefinition`에 "종류" 필드를 두어, 먹통(전역 효과 이벤트)이 실제로 추가될 때 매니저에 분기 한 곳 + 그때 추상화를 도입한다 — 국소적이고 값싼 리팩터.

## 3. 데이터 흐름 & 라이프사이클

```
라운드 시작 (RoundManager.OnRoundStarted)
 └─ RandomEventManager 타이머 시작 (서버/오프라인만)

타이머 만료 (랜덤 간격, 인스펙터)
 └─ 활성 이벤트 < 최대동시 && 쿨타임 지남?
     └─ 정의 목록에서 가중치로 1개 추첨
         └─ NavMesh 유효 지점 탐색 → 적대 NPC 프리팹 Instantiate
             → (네트워크면 NetworkObject.Spawn) → Attack 강제(StartResist) → BroadcastDisturbance(소란)
             → 활성 목록에 (NPC, 정의) 등록 + 그 NPC의 OnStateChanged 구독

진행 중 (기존 NpcResistState 그대로)
 ├─ 주변 현장 플레이어 범위 타격 → IDamageable.TakeDamage (HP 감소, #105 다운 연동)
 └─ 소란 펄스 → 주변 배회 시민 패닉 (#81)

플레이어가 E로 제압 (NpcSubdueInteractable → ApplySubdueHit → 게이지 0)
 └─ NPC가 Captured 전이 → 매니저가 OnStateChanged로 감지
     └─ ArrestResult(Misdemeanor, 보상액, Profile=null, DeliveredBy=null) 생성 → OnEventResolved 발행
         (보상은 개인 귀속이 아니라 팀 자금 대상이라 DeliveredBy=null·Profile=null. 자금은 #104 구독, 지금은 로그)
         → 짧은 지연(인스펙터) 후 NPC despawn → 활성 목록에서 제거 (이벤트 종료)

대안 종료: 지속시간 초과 / NPC가 도주(Run) 전환(제압 실패, 기존 NpcResistState 규칙) → 보상 없이 despawn

라운드 종료 (RoundManager.OnRoundEnded)
 └─ 타이머 정지 + 활성 이벤트 전부 정리(적대 NPC despawn)
```

## 4. 네트워크 / 소유권

- **매니저**: `RoundManager`와 동일 — plain MonoBehaviour + 서버 게이트. 클라이언트는 타이머를 돌리지 않는다.
- **적대 NPC**: `NetworkObject.Spawn`(서버)으로 전 클라이언트에 복제(NpcSpawner와 동일). FSM·데미지·제압 판정 전부 서버 권위, 클라이언트는 표현만.
- **보상 발행**: 서버측(자금 서버 권위, #104 전제).
- **오프라인 폴백**: `NetworkManager` 없으면 로컬 스폰(NpcSpawner 관례 그대로) — 비네트워크 Play 테스트 유지.

## 5. 엣지 케이스 & 에러 처리

- **NavMesh 유효 지점 못 찾음** → 이번 스폰 스킵, 다음 간격에 재시도(NpcSpawner의 샘플링 실패 처리와 동일 정신).
- **라운드 종료 중 이벤트 진행** → 정리 시 despawn. 이벤트 NPC는 `NpcSpawner.SpawnedNpcs` 목록에 없으므로 `RoundManager.FreezeAllNpcs`가 잡지 못한다 → **매니저가 라운드 종료를 구독해 직접 정리**한다.
- **서버 재시작(#175 패턴)** → 라운드 리셋 시 매니저도 타이머 정지 + 활성 정리. 라운드 라이프사이클 이벤트(`OnRoundStarted`/`OnRoundEnded`) 구독으로 자동 처리.
- **최대 동시 수·쿨타임** → 이벤트 스팸 방지(전부 인스펙터).
- **도주 전환** → 제압 실패로 NPC가 Run 상태가 되면 보상 없이 이벤트 종료(despawn) — 기존 NpcResistState 규칙 재사용, 플레이어 실패 페널티로 수용.

## 6. 테스트

- **Play 실측(#175 검증 방식)**: 이벤트 강제 발생 → 적대 NPC 스폰·공격(플레이어 HP 감소)·소란(시민 패닉)·제압 시 `OnEventResolved` 보상 발행·despawn 확인. 라운드 종료 시 활성 이벤트 정리 확인. 가중치 추첨 분포 확인.
- **EditMode 단위 테스트(가능 시)**: 가중치 추첨 로직을 순수 함수로 분리해 분포 검증.

## 7. 완료 기준 (이번 스코프)

- [ ] 프레임워크가 라운드 중 서버 권위로 랜덤 간격 이벤트를 발생시킨다(동시수·쿨타임·간격 인스펙터).
- [ ] 거리 난동자가 소란(`BroadcastDisturbance`)으로 주변 시민을 패닉시키고, 제압 시 경범죄 보상 이벤트(`OnEventResolved`)가 발행된다.
- [ ] 괴한 습격이 현장 플레이어 HP를 깎고(`IDamageable`), 제압 시 더 높은 보상 이벤트가 발행된다.
- [ ] 이벤트 발생·제압·보상이 서버 권위로 전 클라이언트에 동기화된다(적대 NPC는 NetworkObject 복제).
- [ ] 라운드 종료·서버 재시작 시 활성 이벤트가 정리된다.

## 8. 이번 스코프 밖 (후속)

- **전자기기 먹통** — 전역 시야·통신 제한 이벤트(#67 무전 연동). 이벤트-종류 추상화 도입과 함께 별도 이슈.
- **팀 공용 자금(#104)** — `OnEventResolved`(및 `ArrestJudge.OnArrestJudged`)를 구독해 실제 수익 합산. 본 작업은 이벤트 발행까지만.
