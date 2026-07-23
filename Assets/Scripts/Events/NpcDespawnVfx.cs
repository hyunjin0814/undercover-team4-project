using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 이벤트 NPC 소멸 연출 (#310) — 정리(despawn) 직전, 사라지는 자리에 글리치 이펙트를 전 피어 화면에 남긴다.
/// 돌발 이벤트 스폰물(난동자·난동꾼·침입자·괴한)이 눈앞에서 소리 없이 증발하는 어색함을 세계관(사이버펑크)
/// 연출로 가린다. <see cref="SuddenEventUtil.DespawnOrDestroy"/>가 이 컴포넌트를 발견하면 자동으로 재생하므로,
/// 이벤트 NPC 프리팹에 붙이고 이펙트 프리팹만 배선하면 된다 — 이벤트 코드는 수정할 필요가 없다.
///
/// 전파는 <see cref="SuddenEventManager.Announce"/>와 같은 ClientRpc 패턴이다: 이펙트 오브젝트를 네트워크에
/// 싣지 않고(프리팹 등록 불필요) 각 피어가 자기 화면에 로컬로 스폰한다. RPC는 뒤이은 despawn 메시지보다
/// 먼저 배송되므로 원격 클라에서도 NPC가 사라지기 직전 위치에 재생된다.
/// </summary>
public class NpcDespawnVfx : NetworkBehaviour
{
    [Tooltip("소멸 지점에 남길 이펙트 프리팹 — 루프 없는 파티클 + StopAction=Destroy로 스스로 정리되게 구성할 것")]
    [SerializeField] private GameObject m_vfxPrefab;

    [Tooltip("몸체 잔상(고스트)에 씌울 머티리얼 — NPC의 현재 포즈를 베이크해 플리커시키며 사라지게 한다. 비우면 잔상 없이 파티클만")]
    [SerializeField] private Material m_ghostMaterial;

    [Tooltip("이펙트 스폰 높이 보정(m) — 발밑이 아니라 몸통 높이에서 터지게")]
    [SerializeField] private float m_heightOffset = 1f;

    /// <summary>
    /// 소멸 연출 재생 — despawn '직전'에 서버(또는 오프라인)에서만 호출한다.
    /// 프리팹이 배선되지 않았으면 조용히 무동작(연출은 선택 사항).
    /// </summary>
    public void ServerPlay()
    {
        if (m_vfxPrefab == null && m_ghostMaterial == null)
            return;

        SpawnLocal(); // 서버(호스트)·오프라인 로컬 재생
        if (IsSpawned && IsServer)
            PlayClientRpc();
    }

    [ClientRpc]
    private void PlayClientRpc()
    {
        // 호스트는 위에서 이미 재생했다 — 원격 클라에서만 중계 (SuddenEventManager.AnnounceEventClientRpc와 동일)
        if (IsServer)
            return;
        SpawnLocal();
    }

    // 이 시점에는 NPC가 아직 파괴되지 않았다(RPC가 despawn 메시지보다 먼저 배송) — 잔상 베이크가 가능한 이유
    private void SpawnLocal()
    {
        if (m_vfxPrefab != null)
            Instantiate(m_vfxPrefab, transform.position + Vector3.up * m_heightOffset, Quaternion.identity);

        if (m_ghostMaterial != null)
            NpcDespawnGhost.Spawn(transform, m_ghostMaterial); // 파편(파티클) 위에 몸체 실루엣 잔상을 겹친다
    }
}
