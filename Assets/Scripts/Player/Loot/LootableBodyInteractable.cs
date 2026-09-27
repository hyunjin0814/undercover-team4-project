using UnityEngine;

/// <summary>
/// 쓰러진 동료 몸의 약탈 가능 여부를 판정한다 — PlayerLooter가 R 입력에서 직접 찾아 쓴다.
/// 플레이어 루트에 붙인다.
/// </summary>
[RequireComponent(typeof(PlayerLootable))]
public class LootableBodyInteractable : MonoBehaviour
{
    private PlayerLootable m_body;

    public PlayerLootable Body => m_body;

    private void Awake()
    {
        m_body = GetComponent<PlayerLootable>();
    }

    /// <summary>R이 실제로 동작하는 상태인지 — 약탈 가능 상태와 자기 자신 제외를 함께 본다. 서버 가드와 같은 기준.</summary>
    public bool CanLoot(GameObject looterObject)
    {
        if (!m_body.CanBeLooted)
            return false;

        PlayerLooter looter =
            looterObject != null ? looterObject.GetComponentInParent<PlayerLooter>() : null;
        return looter != null && looter.gameObject != gameObject;
    }
}
