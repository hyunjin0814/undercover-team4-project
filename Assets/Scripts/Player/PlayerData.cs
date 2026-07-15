using Unity.Netcode;
using UnityEngine;

public class PlayerData : NetworkBehaviour, IDamageable
{
    [Header("스테이터스")]
    [SerializeField] private int m_maxHp = 100;

    [Tooltip("구조(리바이브) 시 회복되는 HP — 부분 회복 (GDD 7-5, #105)")]
    [SerializeField] private int m_reviveHp = 50;

    // 서버 권위 HP — 서버만 쓰고 모든 클라이언트가 읽는다.
    // m_hp는 서버·오프라인의 진실값 (NpcController의 상태/게이지 이중 구조와 동일 패턴, #79)
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    // HP 0 도달 시 다운시킬 무력화 컴포넌트 (#105). 같은 플레이어 오브젝트에 있음.
    private PlayerIncapacitation m_incapacitation;

    public ulong PlayerId => OwnerClientId;
    public int MaxHp => m_maxHp;
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    /// <summary>
    /// 아직 행동 가능한 상태인지 — 살아있고(HP&gt;0) 무력화되지 않은 플레이어. (#105, #106)
    /// 돌발 이벤트·NPC가 표적을 고를 때 쓴다: 다운된 플레이어는 이미 무력화됐으므로 표적에서 뺀다.
    /// </summary>
    public bool IsTargetable =>
        CurrentHp > 0 && (m_incapacitation == null || !m_incapacitation.IsIncapacitated);

    private void Awake()
    {
        m_hp = m_maxHp; // 오프라인(비네트워크) Play 테스트 폴백 초기값
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            SetHp(m_maxHp);
        }
    }

    // 서버 권위로만 실제 값 변경. (데미지 소스가 클라라면 별도 ServerRpc로 요청)
    public void ModifyHp(int delta)
    {
        if (IsSpawned && !IsServer) return;

        SetHp(Mathf.Clamp(CurrentHp + delta, 0, m_maxHp));
    }

    /// <summary>
    /// 피격 — 저항형 NPC 범위 타격 등 데미지 소스의 공통 경로. (#79)
    /// HP가 0이 되면 다운(무력화) 처리로 이어진다 (SetHp 내부, GDD 7-5 / #105).
    /// </summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        ModifyHp(-amount);
    }

    /// <summary>
    /// 구조(리바이브) — 동료의 채널링이 성공하면 서버(또는 오프라인)에서 호출된다. (#105, GDD 7-5)
    /// HP를 일부 회복하고 무력화를 해제한다.
    /// </summary>
    public void ServerRevive()
    {
        if (IsSpawned && !IsServer) return; // 서버 권위 방어
        if (CurrentHp > 0) return; // 다운(HP 0) 상태에서만 유효

        // 회복인데 0이면 여전히 다운이므로 최소 1 보장
        SetHp(Mathf.Clamp(m_reviveHp, 1, m_maxHp));
        m_incapacitation?.Recover();
    }

    private void SetHp(int value)
    {
        int previous = CurrentHp; // 변경 전 값 — HP 0 도달 순간을 감지하기 위함
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        // HP가 0에 도달하는 순간 다운(무력화) 진입. 서버(또는 오프라인)에서만 실행되며
        // Incapacitate 자체에도 서버 가드가 있다. (#105, GDD 7-5)
        if (value == 0 && previous > 0)
            m_incapacitation?.Incapacitate();
    }
}
