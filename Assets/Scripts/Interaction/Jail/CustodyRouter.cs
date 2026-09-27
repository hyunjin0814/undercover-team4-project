using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 검거 판정 결과에 따른 신병 처리 — 오검거된 무고한 시민의 수갑을 풀고 석방한다.
/// 오검거 페널티 매니저가 없는 씬의 폴백이며, 서버(또는 오프라인) 전용.
/// </summary>
public class CustodyRouter : MonoBehaviour
{
    [Header("판정 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private ArrestJudge m_arrestJudge;

    private void Awake()
    {
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();
    }

    private void OnEnable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        NpcController npc = result.Npc;
        if (npc == null)
            return;

        if (
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer
        )
            return;

        if (result.Verdict == ArrestVerdict.WrongfulArrest)
        {
            if (App.Game.WrongfulArrestPenalty != null)
                return;

            Debug.Log($"[신병 처리] 오검거 석방(페널티 매니저 없음) — 수갑 해제: {npc.name}");
            npc.Custody.ReleaseFromCustody();
            return;
        }
    }
}
