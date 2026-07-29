using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 자물쇠 — 잠김/열림 상태만 갖는다. (GDD 7-2, #231)
/// <see cref="JailZone"/>과 같은 오브젝트에 둔다.
///
/// <b>누가 왜 여는지는 모른다</b> — 범인 탈출 이벤트(JailbreakEvent)가 열고, 새 수감자가 들어오면
/// 유치장이 다시 잠근다. 문 열림 애니메이션·본부 경보 UI는 <see cref="OnLockChanged"/>를 구독해 붙인다.
///
/// 상태는 서버 권위로 정해 NetworkVariable로 전 피어에 동기화한다 (#56) —
/// 자물쇠가 열린 것은 본부 화면에서 보여야 하므로 클라이언트도 읽을 수 있어야 한다.
/// </summary>
public class JailLock : NetworkBehaviour
{
    [Header("접근 지점 (비우면 자물쇠 자신의 위치)")]
    [Tooltip("침입자(#231)가 걸어와 서는 지점 — 창살 문 바깥에 둔다. 자물쇠 자신이 우리 안에 있으면 경로가 막힌다 (#415)")]
    [SerializeField] private Transform m_approachPoint;

    // 서버 권위 잠금 상태 — 기본은 잠김
    private readonly NetworkVariable<bool> m_locked = new NetworkVariable<bool>(true);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — JailZone의 수용 인원과 동일 구조
    private bool m_localLocked = true;

    /// <summary>자물쇠가 잠겨 있는가. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsLocked => IsSpawned ? m_locked.Value : m_localLocked;

    /// <summary>
    /// 침입자가 자물쇠를 열려고 걸어오는 목표 지점 — 미배선이면 자물쇠 자신의 위치. (#415)
    /// 자물쇠는 유치장(JailZone)과 같은 오브젝트에 있어 좌표가 창살 우리 <b>안</b>이 되므로,
    /// 시민 통행이 금지된 Jail 영역 밖(문 앞)의 지점을 따로 가리켜야 침입 경로가 성립한다.
    /// </summary>
    public Transform ApproachPoint => m_approachPoint != null ? m_approachPoint : transform;

    /// <summary>잠금 상태 변경 — 서버·클라이언트 모든 피어에서 발생한다. 문 연출·본부 경보 UI가 구독.</summary>
    public event Action<bool> OnLockChanged;

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
        // NetworkVariable 콜백은 모든 피어에서 돌므로 별도 RPC 없이 전 화면에 경보가 뜬다 (#311)
        if (!current)
            ShowUnlockedAlarm();
        OnLockChanged?.Invoke(current);
    }

    // 탈출 성공(자물쇠 개방) 경보 — 잠금 해제는 범인 탈출 이벤트의 성공 시점뿐이다.
    // 재잠금(수감)에는 울리지 않는다.
    private static void ShowUnlockedAlarm()
    {
        SuddenEventToastHud.Show("🚨 유치장이 열렸습니다 — 수감자가 탈출합니다!");
    }

    /// <summary>
    /// 해제 '시도' 전파 (#311) — 침입자가 자물쇠 앞에서 해제 채널링을 시작했을 때 탈출 이벤트가 호출한다.
    /// 모든 피어 화면에 전역 팝업을 띄운다 — 잠금 상태와 마찬가지로
    /// "자물쇠에서 벌어지는 일은 자물쇠가 전파한다". 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerAnnounceUnlockAttempt()
    {
        if (IsSpawned && !IsServer)
            return;

        ShowUnlockAttemptToast(); // 서버(호스트)·오프라인 자기 화면
        if (IsSpawned && IsServer)
            AnnounceUnlockAttemptClientRpc();
    }

    [ClientRpc]
    private void AnnounceUnlockAttemptClientRpc()
    {
        // 호스트는 위에서 이미 띄웠다 — 원격 클라에서만 중계 (SuddenEventManager.AnnounceEventClientRpc와 동일)
        if (IsServer)
            return;

        ShowUnlockAttemptToast();
    }

    private static void ShowUnlockAttemptToast()
    {
        SuddenEventToastHud.Show("⚠ 침입자가 범죄자 해방을 시도하고 있습니다!");
    }

    /// <summary>자물쇠 해제 — 침입자가 자물쇠에 도달했을 때 탈출 이벤트가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerUnlock()
    {
        if (SetLocked(false))
            Debug.Log("[유치장] 자물쇠 해제됨");
    }

    /// <summary>재잠금 — 새 수감자가 들어오거나(JailZone.Admit) 라운드가 정리될 때. 서버(또는 오프라인) 전용.</summary>
    public void ServerRelock()
    {
        if (SetLocked(true))
            Debug.Log("[유치장] 자물쇠 재잠금");
    }

    // 서버 진실값과 동기화 변수에 함께 기록한다. 값이 실제로 바뀐 경우에만 true를 돌려준다
    // (재잠금은 수감할 때마다 불리므로 로그가 도배되지 않게 한다). JailZone.SetInmateCount와 같은 구조 —
    // 오프라인에서는 NetworkVariable에 쓰지 않고 이벤트를 직접 발행한다.
    private bool SetLocked(bool value)
    {
        if (IsSpawned && !IsServer)
            return false; // 상태는 서버 권위 — 클라이언트에서 불려도 무시한다

        if (IsLocked == value)
            return false;

        m_localLocked = value;

        if (IsSpawned && IsServer)
            m_locked.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
        {
            // 오프라인 — 동기화 콜백이 없으므로 개방 경보도 여기서 직접 울린다 (#311)
            if (!value)
                ShowUnlockedAlarm();
            OnLockChanged?.Invoke(value);
        }

        return true;
    }
}
