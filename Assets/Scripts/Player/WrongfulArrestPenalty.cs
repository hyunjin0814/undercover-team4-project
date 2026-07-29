using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오검거 페널티 (GDD 7-3, #101 → 2단계 #276) — 팀 공유 오검거 카운트를 서버 권위로 누적하고,
/// 허용 횟수(k_maxWrongful) 초과 시 원한 구역에 수용된 오검거 시민들을 추격대로 출동시킨다.
///
/// 흐름: 오검거 판정 → 시민을 원한 구역 수용(#277, 구역 인원 = 팀 카운트) → 임계치 초과 순간
/// 구역 전원 출동 + <b>팀 카운트 즉시 리셋</b>(#278 — 추격 중 새 오검거는 새 카운트로 쌓여 새 추격대가 된다)
/// → 추격 NPC에게 잡힌 플레이어(원인 제공자가 아니어도!)가 페널티 확정 → <b>포획 시점에 미해소였던</b>
/// 페널티 NPC 전원이 수렴한 뒤 2명이 양옆에서 광장까지 끌고 가고(#279) → 30초 매달기(행동불능) 후 자동 복귀.
///
/// <b>추격에 시간 제한은 없다(팀 결정)</b> — 못 잡으면 사냥 모드로 계속 배회하며 노린다.
/// 페널티는 잡히거나 격퇴로 미뤄질 뿐 사라지지 않는다. 폴백(#101 텔레포트 집행)은 한 겹만 남는다:
/// 출동 시점에 구역이 비어 추격대를 꾸릴 수 없는 예외 상황 — 기존 매달기 로직이 최후 보루다.
///
/// 호루라기(#250)는 이번 범위 밖 — 격퇴 진입점(<see cref="RepelChasers"/>)만 열어 둔다.
/// 판정·카운트·추격·호송은 모두 서버에서만 일어나고 결과(팀 카운트·NPC 상태·행동불능)만 동기화된다(#56).
/// 행동불능 상태 자체는 PlayerIncapacitation(#105)이, 플레이어 끌려가기 표현은 오너 추종
/// (PlayerPenaltyView→PlayerMovement.BeginCarriedFollow — NetworkTransform 오너 권한)이 담당한다.
///
/// 개인별 오검거 집계는 정산 "최다 오검거" 코믹 스탯(GDD 7-3)용으로 팀 카운트와 별개로 유지한다.
/// 오검거는 팀 자금·라운드 종료와 무관하다(GDD 7-3 확정) — 여기서 자금/라운드를 건드리지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public partial class WrongfulArrestPenalty : NetworkedManagerBase
{
    private const int k_maxWrongful = 1;      // 이 값을 "초과"하면(2회째) 추격대 출동
    private const float k_hangSeconds = 30f;  // 광장 매달기(행동불능) 지속 시간 — NPC 수와 무관하게 고정 (#276 확정)

    private const float k_carrierGap = 1.1f;         // 양옆 끌기 담당의 선두 기준 좌우 간격(m)
    private const float k_plazaArriveDistance = 2f;  // 호송 선두의 광장 도착 판정 거리(m)
    private const float k_carryTravelTimeoutSeconds = 90f; // 호송 이동 안전 상한(초) — 넘으면 스냅 텔레포트로 마무리
    private const float k_warningSeconds = 8f;       // 출동 알림 표시 시간(초) — 카운트다운이 아니라 잠깐 뜨는 경고

    [Header("광장 (매달기 지점) — 비우면 원점")]
    [Tooltip("페널티 확정 시 끌려가/이송될 맵 중앙 지점. 씬의 빈 GameObject를 지정한다")]
    [SerializeField] private Transform m_plazaPoint;

    [Header("원한 구역 (오검거 시민 수용 지점) — 비우면 그 자리 수용 (#277)")]
    [Tooltip("오검거당한 시민이 걸어가 대기하는 지점. NavMesh 위에 둘 것 — 여러 명은 회피가 흩어 준다")]
    [SerializeField] private Transform m_detentionPoint;

    [Header("추격 (#278)")]
    [Tooltip("격퇴(RepelChasers, 호루라기 #250 예정)가 미치는 반경(m)")]
    [SerializeField] private float m_repelRadius = 10f;

    [Header("수렴·호송 (#279)")]
    [Tooltip("포획된 플레이어 주변 이 거리(m) 안에 전원이 모이면 호송을 시작한다")]
    [SerializeField] private float m_convergeArriveDistance = 2.5f;
    [Tooltip("수렴 대기 상한(초) — 길이 막힌 NPC가 있어도 이 시간이 지나면 모인 인원으로 호송을 시작한다")]
    [SerializeField] private float m_convergeTimeoutSeconds = 20f;

    private ArrestJudge Judge => App.Game.ArrestJudge;

    // 서버만 쓰고 전 클라가 읽는 팀 공유 카운트 = 페널티 게이지. (정산 개인 집계와 별개)
    private readonly NetworkVariable<int> m_teamCountSynced = new NetworkVariable<int>();

    // 정산 코믹 스탯용 개인 집계 — clientId별 실제 오검거 횟수. 서버에서만 누적(표시·동기화는 정산 UI 이슈).
    private readonly Dictionary<ulong, int> m_perPlayerCounts = new Dictionary<ulong, int>();

    // 원한 구역 대기 인원(#277) — 불변식: 이 목록 수 == 팀 카운트. 출동 시 통째로 추격대가 되며 비워진다.
    private readonly List<NpcController> m_detained = new List<NpcController>();

    // 미해소 페널티 NPC 전원(전 출동분 합산) — 포획 시점의 스냅샷이 수렴 대상이 된다(독박, #276 확정).
    private readonly List<NpcController> m_activeNpcs = new List<NpcController>();

    // 현재 호송(수렴~광장) 처리 중인 대상 — 동시에 하나만. 추격 상태의 늦은 포획 통보는 무시된다
    // (호송 중 새로 출동한 추격대는 계속 추격하다가, 이 호송이 끝난 뒤의 포획부터 다시 접수된다).
    private Transform m_carryTarget;

    /// <summary>팀 공유 오검거 카운트(페널티 게이지). 전 피어 읽기 가능.</summary>
    public int TeamWrongfulCount => m_teamCountSynced.Value;

    /// <summary>정산용 개인 오검거 집계(clientId→횟수). 서버에서만 채워진다.</summary>
    public IReadOnlyDictionary<ulong, int> PerPlayerCounts => m_perPlayerCounts;

    public override void OnNetworkSpawn()
    {
        // 판정은 서버 권위이므로 서버에서만 구독한다 (WantedListManager 관례).
        // 재시작(Shutdown 후 StartHost) 시 씬 NetworkObject에 이전 세션 값이 남으므로 새로 0에서 시작한다.
        if (IsServer)
        {
            m_teamCountSynced.Value = 0;
            m_perPlayerCounts.Clear();
            m_detained.Clear();
            m_activeNpcs.Clear();
            m_carryTarget = null;

            if (Judge != null)
                Judge.OnArrestJudged += HandleArrestJudged;
            else
                Debug.LogWarning("WrongfulArrestPenalty: ArrestJudge를 찾지 못해 오검거를 집계할 수 없다", this);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    // ---- 오검거 접수: 개인 집계 + 수용 + 출동 판단 (서버 전용) ----

    // 오검거 판정 수신 — 시민을 원한 구역에 수용하고(#277), 카운트를 올리고, 임계치 초과면 출동시킨다(#278).
    // 수용·카운트·출동이 한 구독자 안에 있어야 순서가 보장된다 — CustodyRouter가 오검거 신병을 이쪽에 넘기는 이유.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WrongfulArrest)
            return;

        // 오검거 카운트는 매 인계마다 오른다 — 무고한 시민을 다시 잡아 인계하면 또 한 번의 오검거다 (#358).
        // IsFirstDelivery로 막지 않는 이유: 시민은 석방(ReleaseFromCustody)돼도 ClearDelivered가 불리지 않아
        // IsDelivered가 영구 true로 남는다 → 가드를 걸면 첫 인계 이후 오검거가 영영 안 세진다. 재판정 스팸은
        // 인계가 수동 상호작용(E)이 되면서(#414) 구조적으로 없다 — 누른 횟수만큼만 판정된다.

        // 개인 집계는 실제 오검거 기록 — 페널티 결과와 무관하게 항상 +1 (정산 코믹 스탯용).
        // 밧줄이 걸린 채 인계존까지 들어갔으면 전원이 관여자다 (#390 규칙 4) — 줄다리기로 남이 밀어넣었어도
        // 손을 떼는 수단(E 놓고 걸어가 줄 끊기 / 자기 줄 풀기)이 있었고, 실제로는 인계존까지 따라가는 동안
        // 거리 초과로 줄이 먼저 끊기므로 "끝까지 붙어 있었다"만 남는다.
        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer == null)
                continue;

            ulong clientId = deliverer.OwnerClientId;
            m_perPlayerCounts.TryGetValue(clientId, out int prev);
            m_perPlayerCounts[clientId] = prev + 1;
        }

        // 팀 카운트(페널티 게이지) +1 — 원한 구역 수용과 함께 오르므로 "구역 인원 = 팀 카운트"가 유지된다.
        m_teamCountSynced.Value += 1;
        DetainNpc(result.Npc);

        Debug.Log($"[오검거] 팀 카운트 {m_teamCountSynced.Value} — {FormatPerPlayerCounts()}");

        if (m_teamCountSynced.Value > k_maxWrongful)
            LaunchSquad(CollectTargets(result));
    }

    // 추격 대상 트랜스폼만 뽑아낸다 — 인계자 전원이 대상이다 (#390 규칙 5).
    private static List<Transform> CollectTargets(ArrestResult result)
    {
        var targets = new List<Transform>();
        foreach (PlayerEscorter deliverer in result.DeliveredBy)
            if (deliverer != null)
                targets.Add(deliverer.transform);

        return targets;
    }

    // 원한 구역 수용(#277) — 석방 대신 전용 구역으로 보내 출동 대기시킨다.
    private void DetainNpc(NpcController npc)
    {
        if (npc == null)
            return;

        if (m_detentionPoint == null)
            Debug.LogWarning("WrongfulArrestPenalty: 원한 구역(Detention Point) 미배선 — 그 자리에서 수용된다", this);

        m_detained.Add(npc);
        npc.SendToDetention(m_detentionPoint);
        Debug.Log($"[오검거] 원한 구역 수용: {npc.name} — 대기 {m_detained.Count}명");
    }

    // ---- 출동 (#278) ----

    // 구역 전원을 추격대로 출동시킨다. 팀 카운트는 이 순간 리셋 — 이후 오검거는 새 게이지로 쌓인다 (#276 확정).
    // 대상이 여럿이면(줄다리기로 함께 인계, #390) 추격 NPC마다 <b>자기와 가장 가까운</b> 대상을 문다 —
    // 전원이 한 사람에게 몰리면 나머지는 벌을 안 받고, 한 명은 감당 못 할 수를 맞는다.
    private void LaunchSquad(List<Transform> targets)
    {
        m_teamCountSynced.Value = 0;
        PruneDead(m_detained);

        // 아래에서 네 번 도는 목록이라 여기서 한 번만 정규화한다 — null 목록·죽은 항목 둘 다.
        targets ??= new List<Transform>();
        targets.RemoveAll(t => t == null);

        // 폴백 ① — 구역이 비어 추격대를 꾸릴 수 없다(리셋 타이밍 등 예외 상황). 기존 텔레포트 집행 (#101)
        if (m_detained.Count == 0)
        {
            Debug.LogWarning("[오검거] 원한 구역이 비어 있음 — 텔레포트 집행 폴백", this);
            foreach (Transform target in targets)
                HangAsync(target).Forget();
            return;
        }

        int launched = m_detained.Count;
        foreach (NpcController npc in m_detained)
        {
            m_activeNpcs.Add(npc);
            npc.OnPenaltyCaught += HandlePenaltyCaught;
            // null이면 사냥 모드로 시작해 범위에 드는 플레이어를 문다
            npc.StartPenaltyChase(NearestTarget(npc.transform.position, targets));
        }
        m_detained.Clear();

        // 대상 본인들에게 알림(잠깐 표시 후 자동 소멸). 추격에 시간 제한은 없다(팀 결정) —
        // 못 잡으면 사냥 모드로 계속 배회하며 노리므로, 페널티는 잡히거나 격퇴로 미뤄질 뿐 사라지지 않는다.
        foreach (Transform target in targets)
            ShowWarning(target, k_warningSeconds);

        string targetNames = targets.Count > 0
            ? string.Join(", ", targets.ConvertAll(t => t.name))
            : "(없음 — 사냥 모드)";
        Debug.Log($"[오검거] 추격대 출동 — {launched}명, 초기 타겟 {targetNames}");
    }

    // 출동 지점에서 가장 가까운 대상 — 대상이 없으면 null(사냥 모드).
    private static Transform NearestTarget(Vector3 from, List<Transform> targets)
    {
        if (targets == null || targets.Count == 0)
            return null;

        Transform nearest = targets[0];
        float best = (nearest.position - from).sqrMagnitude;
        for (int i = 1; i < targets.Count; i++)
        {
            float distance = (targets[i].position - from).sqrMagnitude;
            if (distance >= best)
                continue;

            best = distance;
            nearest = targets[i];
        }

        return nearest;
    }

    /// <summary>
    /// 격퇴 — 호루라기(#250 후속)의 연결고리. user 주변 반경의 추격 NPC들이 도주 후
    /// 재추격 쿨다운 동안 그 플레이어를 노리지 않는다. 페널티는 취소되지 않는다 — 유예·전가만 된다. (#278)
    /// 서버(또는 오프라인)에서만 유효.
    /// </summary>
    public void RepelChasers(Transform user)
    {
        if (IsSpawned && !IsServer)
            return;
        if (user == null)
            return;

        PruneDead(m_activeNpcs);
        foreach (NpcController npc in m_activeNpcs)
        {
            if (Vector3.Distance(npc.transform.position, user.position) <= m_repelRadius)
                npc.ApplyChaseRepel(user);
        }
    }

    // ---- 매달기 (#101 유지 — 폴백 겸 호송 마무리) ----

    // 광장 스냅 + 30초 행동불능 → 자동 복귀. 호송 마무리(위치 보정)와 폴백 집행이 공유한다. (서버 전용)
    private async UniTask HangAsync(Transform target)
    {
        if (target == null)
            return;

        PlayerMovement movement = target.GetComponent<PlayerMovement>();
        PlayerIncapacitation incap = target.GetComponent<PlayerIncapacitation>();

        if (m_plazaPoint == null)
            Debug.LogWarning("WrongfulArrestPenalty: Plaza Point 미할당 — 원점(0,0,0)으로 이송된다. 인스펙터에 광장 지점을 지정할 것", this);

        Vector3 pos = m_plazaPoint != null ? m_plazaPoint.position : Vector3.zero;
        Quaternion rot = m_plazaPoint != null ? m_plazaPoint.rotation : Quaternion.identity;

        if (movement != null)
            movement.ServerTeleport(pos, rot); // 오너 권한 경로 — 호스트·원격 클라 모두 이동
        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Penalty); // 이미 무력화면 무동작(중복 트리거 무시) — 폴백 경로에선 여기서 진입

        Debug.Log($"[오검거] 광장 매달기 — {target.name} → {pos}, {k_hangSeconds}초");

        // 씬 전환·파괴 시 토큰으로 안전 중단한다.
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(k_hangSeconds), cancellationToken: destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 매니저 파괴 — 복귀 처리 없이 종료(대상도 함께 정리되는 상황)
        }

        // 자동 복귀 — 대상이 퇴장·파괴됐을 수 있어 fake-null 가드
        if (incap != null)
            incap.Recover();
    }

    // ---- 정리 헬퍼 ----
    // (호송 전용 헬퍼 ReleaseNpc/ReleaseAll/AllWithin은 WrongfulArrestPenalty.Carry.cs에 있다)

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);

    private static void ShowWarning(Transform target, float seconds)
    {
        if (target == null)
            return;

        PlayerPenaltyView view = target.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.ShowWarning(seconds);
    }

    // 개인 집계 전체를 "client N:x회" 형태로 이어붙인다 — 정산 코믹 스탯이 붙기 전까지 로그로 확인용.
    private string FormatPerPlayerCounts()
    {
        if (m_perPlayerCounts.Count == 0)
            return "개인집계 없음";

        var sb = new System.Text.StringBuilder("개인집계 [");
        bool first = true;
        foreach (KeyValuePair<ulong, int> pair in m_perPlayerCounts)
        {
            if (!first) sb.Append(", ");
            sb.Append($"client {pair.Key}:{pair.Value}회");
            first = false;
        }
        sb.Append(']');
        return sb.ToString();
    }
}
