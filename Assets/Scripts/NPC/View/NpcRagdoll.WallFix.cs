using UnityEngine;

/// <summary>
/// NpcRagdoll의 벽 끼임 처리 partial — 인스펙터 값과 호출 조건만 두고 실제 처리는 RagdollArmFold가 한다.
/// </summary>
public partial class NpcRagdoll
{
    private const int k_wallProbeStepsMoving = 4;
    private const int k_wallProbeStepsSettled = 10;

    private const int k_wallConfirmProbes = 3;

    [Header("벽에 박힌 팔 접기 (#980)")]
    [Tooltip("래그돌인 동안 벽에 박힌 팔을 찾아 몸쪽으로 접어서 빼낸다.\n\n" +
             "끄면 박힌 채로 남는다(옛 동작) — 개발 중 A/B 비교용")]
    [SerializeField] private bool m_wallFoldEnabled = true;

    [Tooltip("뼈가 벽면 <b>너머로</b> 이만큼(m) 넘어가 있으면 박힌 것으로 본다.\n\n" +
             "겹침 깊이가 아니다 — 완전히 관통한 팔도 이 값으로 잡힌다. 팔 캡슐 반지름이 0.085m라, " +
             "너무 낮추면 살짝 걸친 팔에도 손대게 된다")]
    [SerializeField] private float m_wallMinDepth = 0.06f;

    [Tooltip("이 질량(kg)을 넘는 뼈는 몸통으로 보고 대상에서 뺀다 — 실측: 팔 4.38 / 몸통 10.94. " +
             "뼈 이름 목록 대신 질량으로 가르는 이유는 프리팹이 진실이어야 해서다")]
    [SerializeField] private float m_wallLimbMassMax = 8f;

    [Tooltip("팔을 다 접는 데 걸리는 시간(초). 빠져나오는 즉시 멈추므로 보통 이보다 짧게 끝난다.\n\n" +
             "짧으면 팔이 툭 접히는 것이 보이고, 길면 빠져나오기 전에 시도가 끝난다")]
    [SerializeField] private float m_wallFoldSeconds = 0.25f;

    [Tooltip("한 번 쓰러질 때 허용하는 시도 횟수")]
    [SerializeField] private int m_wallMaxAttempts = 3;

    [Tooltip("이 시간(초)이 지나면 이번 회차에서는 더 보지 않는다 — 시체가 쌓여도 상시 비용이 " +
             "생기지 않게 하는 상한")]
    [SerializeField] private float m_wallWindowSeconds = 8f;

    [Tooltip("쓰러지는 <b>그 프레임</b>에 팔이 벽 안이면 미리 접고 물리에 넘긴다 — 애초에 박힌 채 " +
             "출발하지 않게 하는 예방책이다. 대부분의 사례가 여기서 걸린다")]
    [SerializeField] private bool m_wallFoldOnEntry = true;

    private RagdollArmFold m_armFold;

    public bool WallFoldEnabled
    {
        get => m_wallFoldEnabled;
        set => m_wallFoldEnabled = value;
    }

    private bool IsWallFixBusy => m_armFold != null && m_armFold.IsFolding;

    private void SetupWallFix()
    {
        m_armFold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = name };
    }

    /// <summary>콜라이더가 사람(플레이어·NPC)인지 판정한다.</summary>
    private static bool IsCharacterCollider(Collider collider) =>
        collider.GetComponentInParent<CharacterController>() != null
        || collider.GetComponentInParent<NpcController>() != null
        || collider.GetComponentInParent<PlayerHealth>() != null;

    private void BeginWallFix() => m_armFold?.Begin();

    private void EndWallFix() => m_armFold?.End();

    /// <summary>뼈를 물리에 넘기기 전(아직 키네마틱일 때) 벽에 박힌 팔을 접는다.</summary>
    private void TryFoldArmsBeforePhysics()
    {
        if (!m_wallFoldEnabled || !m_wallFoldOnEntry || !HasMoveAuthority || m_armFold == null)
            return;

        m_armFold.FoldOnEntry(BuildFoldTuning());
    }

    /// <summary>벽 끼임 처리 한 스텝을 진행한다. FixedUpdate 마지막에서만 호출한다.</summary>
    private void TickWallFix()
    {
        if (!m_wallFoldEnabled || m_armFold == null || !m_armFold.IsWindowOpen)
            return;

        if (m_rope != null && m_rope.IsAttached)
            return;

        if (m_armFold.Tick(m_settled, BuildFoldTuning()) && m_settled)
            ServerResumeFromSleep();
    }

    private RagdollArmFold.Tuning BuildFoldTuning() =>
        new RagdollArmFold.Tuning(
            m_wallMinDepth,
            m_wallFoldSeconds,
            m_wallLimbMassMax,
            m_wallMaxAttempts,
            m_wallWindowSeconds,
            k_wallProbeStepsMoving,
            k_wallProbeStepsSettled,
            k_wallConfirmProbes
        );
}
