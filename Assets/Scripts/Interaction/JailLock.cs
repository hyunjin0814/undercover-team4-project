using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 철창문 잠금 상태 — 잠김/열림만 갖는다. (GDD 7-2, #231/#744)
/// <see cref="JailZone"/>과 같은 오브젝트에 둔다.
///
/// <b>누가 왜 여는지는 모른다</b> — 범인 탈출 이벤트(JailbreakEvent)가 침입자의 <b>배전반 해킹</b>이
/// 끝나는 순간 연다. <b>플레이어가 잠그는 조작은 없다</b> (#744 — 문 E의 '잠그기' 갈래를 없앴다).
/// 되돌리는 길은 아래 <see cref="m_relockSeconds"/> 자동 재잠금 하나뿐이다.
///
/// <b>이 값을 쓰는 곳은 본부 경보등 하나다</b> (<see cref="JailAlarmBeacon"/>). 사슬(옛 JailLockView)은
/// 철창문에 어울리지 않아 걷어냈고 문짝에 여닫는 연출도 없으므로, 잠금 상태를 사람에게 보여 주는
/// 수단은 경보등뿐이다. 배전반 앞을 지켜보는 것은 CCTV 채널이 맡는다 (#744 D).
///
/// 상태는 서버 권위로 정해 NetworkVariable로 전 피어에 동기화한다 (#56) —
/// 철창문이 열린 것은 본부 화면에서 보여야 하므로 클라이언트도 읽을 수 있어야 한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailLock : NetworkedManagerBase
{
    [Header("접근 지점 (비우면 자물쇠 자신의 위치)")]
    [Tooltip("침입자(#231)가 걸어와 서는 지점 — 배전반 앞에 둔다. 자물쇠 자신이 우리 안에 있으면 경로가 막힌다 (#415)")]
    [SerializeField] private Transform m_approachPoint;

    [Header("자동 재잠금")]
    [Tooltip(
        "해킹으로 열린 뒤 이 시간(초)이 지나면 철창문이 스스로 닫힌다 (#744).\n\n"
            + "<b>본부 경보등의 탈옥 경보 시간보다 짧게 두지 말 것</b> — 재잠금은 경보를 '상황 종료'로 "
            + "꺼 버리므로, 짧으면 울리는 중인 경보가 잘린다. 둘은 같은 순간에 시작하니 "
            + "<b>같은 값이면 경보가 제 시간을 다 채우고 닫힌다</b>(기본값 10초가 그 값이다)"
    )]
    [Min(0f)]
    [SerializeField] private float m_relockSeconds = 10f;

    // 서버 권위 잠금 상태 — 기본은 잠김
    private readonly NetworkVariable<bool> m_locked = new NetworkVariable<bool>(true);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — JailZone의 수용 인원과 동일 구조
    private bool m_localLocked = true;

    // 자동 재잠금 예정 시각 — 열려 있는 동안만 의미가 있다. 서버(또는 오프라인) 전용.
    private float m_relockAt;

    /// <summary>자물쇠가 잠겨 있는가. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsLocked => IsSpawned ? m_locked.Value : m_localLocked;

    /// <summary>
    /// 침입자가 자물쇠를 열려고 걸어오는 목표 지점 — 미배선이면 자물쇠 자신의 위치. (#415)
    /// 자물쇠는 유치장(JailZone)과 같은 오브젝트에 있어 좌표가 창살 우리 <b>안</b>이 되므로,
    /// 시민 통행이 금지된 Jail 영역 밖(문 앞)의 지점을 따로 가리켜야 침입 경로가 성립한다.
    /// </summary>
    public Transform ApproachPoint => m_approachPoint != null ? m_approachPoint : transform;

    /// <summary>잠금 상태 변경 — 서버·클라이언트 모든 피어에서 발생한다. 문 연출·본부 경보등이 구독.</summary>
    public event Action<bool> OnLockChanged;

    /// <summary>
    /// 해제 '시도'가 시작됐다 — 서버·클라이언트 모든 피어에서 발생한다. (#311/#493)
    /// 잠금 상태(<see cref="OnLockChanged"/>)와 달리 "성공 전의 낌새"라 NetworkVariable로 표현할 수
    /// 없어 RPC로 전파한다. 본부 경보등(<see cref="JailAlarmBeacon"/>)이 구독해 점멸한다.
    /// </summary>
    public event Action OnUnlockAttempt;

    public override void OnNetworkSpawn()
    {
        m_locked.OnValueChanged += HandleLockedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_locked.OnValueChanged -= HandleLockedChanged;
    }

    private void HandleLockedChanged(bool previous, bool current)
    {
        // NetworkVariable 콜백은 모든 피어에서 돌므로 별도 RPC 없이 전 피어의 경보등이 반응한다 (#311)
        OnLockChanged?.Invoke(current);
    }

    /// <summary>
    /// 해제 '시도' 전파 (#311) — 침입자가 자물쇠 앞에서 해제 채널링을 시작했을 때 탈출 이벤트가 호출한다.
    /// 모든 피어에 <see cref="OnUnlockAttempt"/>를 발생시킨다 — 잠금 상태와 마찬가지로
    /// "자물쇠에서 벌어지는 일은 자물쇠가 전파한다". 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerAnnounceUnlockAttempt()
    {
        if (IsSpawned && !IsServer)
            return;

        OnUnlockAttempt?.Invoke(); // 서버(호스트)·오프라인 자기 쪽
        if (IsSpawned && IsServer)
            AnnounceUnlockAttemptClientRpc();
    }

    [ClientRpc]
    private void AnnounceUnlockAttemptClientRpc()
    {
        // 호스트는 위에서 이미 발생시켰다 — 원격 클라에서만 중계 (SuddenEventManager.AnnounceEventClientRpc와 동일)
        if (IsServer)
            return;

        OnUnlockAttempt?.Invoke();
    }

    /// <summary>개방 — 침입자의 배전반 해킹이 끝났을 때 탈출 이벤트가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerUnlock()
    {
        if (SetLocked(false))
            Debug.Log($"[유치장] 철창문 개방 — {m_relockSeconds}초 뒤 자동으로 닫힌다");
    }

    /// <summary>
    /// 재잠금 — <see cref="Update"/>의 자동 복구 타이머가 부른다. 서버(또는 오프라인) 전용. (#744)
    ///
    /// <b>이것이 유일한 복구 경로다.</b> 플레이어가 문에 E를 눌러 잠그던 조작은 없앴고(#744),
    /// 수감 시 자동 재잠금은 그 전에 이미 없어졌다(#492). 되돌리지 않으면 <see cref="ServerUnlock"/>이
    /// 값 변화 없음으로 조기 반환해 <see cref="OnLockChanged"/>가 다시는 발생하지 않고,
    /// 그러면 <b>두 번째 탈옥부터 본부 경보등이 울리지 않는다</b>.
    /// </summary>
    public void ServerRelock()
    {
        if (SetLocked(true))
            Debug.Log("[유치장] 철창문 자동 재잠금");
    }

    // 자동 복구 — 서버(또는 오프라인)에서만 돈다. 열려 있는 동안에만 시각을 본다.
    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsLocked || Time.time < m_relockAt)
            return;

        ServerRelock();
    }

    // 서버 진실값과 동기화 변수에 함께 기록한다. 값이 실제로 바뀐 경우에만 true를 돌려준다
    // (같은 값으로 겹쳐 불려도 로그가 도배되지 않게 한다). JailZone.SetInmateCount와 같은 구조 —
    // 오프라인에서는 NetworkVariable에 쓰지 않고 이벤트를 직접 발행한다.
    private bool SetLocked(bool value)
    {
        if (IsSpawned && !IsServer)
            return false; // 상태는 서버 권위 — 클라이언트에서 불려도 무시한다

        if (IsLocked == value)
            return false;

        m_localLocked = value;

        // 여는 쪽에서만 타이머를 건다 — 재잠금 예정 시각을 여기 한 곳에서 정해야 개방 경로가
        // 늘어나도(치트·연출) 복구가 딸려 온다. Update가 이 시각만 보고 되돌린다.
        if (!value)
            m_relockAt = Time.time + m_relockSeconds;

        if (IsSpawned && IsServer)
            m_locked.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
        {
            // 오프라인 — 동기화 콜백이 없으므로 여기서 직접 알린다 (#311). 경보등이 이 이벤트를 구독한다.
            OnLockChanged?.Invoke(value);
        }

        return true;
    }
}
