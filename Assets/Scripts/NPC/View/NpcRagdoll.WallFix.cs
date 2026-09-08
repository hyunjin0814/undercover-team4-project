using UnityEngine;

/// <summary>
/// <see cref="NpcRagdoll"/>의 <b>벽 끼임 처리</b> 배선 — 본체에서 갈라낸 partial이다. (#980)
///
/// 실물은 <see cref="RagdollArmFold"/>가 한다. 여기 있는 것은 <b>인스펙터 값</b>과
/// <b>언제 부르지 않을지</b>(꺼짐·끌리는 중)뿐이다 — 그쪽은 네트워크·정착 국면을 모르기 때문이다.
///
/// 설계 근거와 <b>실패한 대안들</b>(특히 임펄스로 밀어내기)은 <c>docs/980-ragdoll-wall-stuck.md</c>.
/// </summary>
public partial class NpcRagdoll
{
    // 판정 주기(물리 스텝) — 무너지는 중에는 촘촘히, 잠든 뒤에는 성기게. 시체가 쌓이는 라운드의
    // 비용 상한이 이 두 값이다.
    private const int k_wallProbeStepsMoving = 4;
    private const int k_wallProbeStepsSettled = 10;

    // 같은 뼈가 이만큼 연속으로 잡혀야 손댄다 — 날아가며 벽을 스치는 팔을 거른다.
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

    /// <summary>팔 접기를 켜고 끈다 — 개발용 A/B 토글(<c>RagdollWallDevHotkeys</c>)이 쓴다.</summary>
    public bool WallFoldEnabled
    {
        get => m_wallFoldEnabled;
        set => m_wallFoldEnabled = value;
    }

    // 접는 동안에는 루트 추종을 쉰다 — 근거는 FixedUpdate 쪽 주석.
    private bool IsWallFixBusy => m_armFold != null && m_armFold.IsFolding;

    private void SetupWallFix()
    {
        m_armFold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = name };
    }

    /// <summary>
    /// 사람인가 — 플레이어·NPC는 벽이 아니다.
    ///
    /// ⚠ <b>레이어 마스크로는 못 거른다.</b> 이 프로젝트에는 벽 전용 레이어가 없어 벽·바닥·플레이어
    /// 캡슐·NPC 캡슐이 전부 <c>Default</c>에 산다. <b>자기 자신의 루트 캡슐도 여기서 걸린다</b> —
    /// 안 거르면 모든 뼈가 "벽 뒤"로 판정된다(<c>NpcProneCollider</c>의 캡슐은 래그돌 중에도 켜져 있다).
    ///
    /// ⚠ <b>이 술어가 여기 있는 이유</b>: 쓰는 쪽(<see cref="RagdollWallProbe"/>)은 <c>Common/</c>이라
    /// 도메인 타입을 알면 안 된다(<c>docs/architecture.md</c> §1 — "도메인에 속하지 않는 공유 부품").
    /// 그래서 판정만 여기서 넘긴다.
    ///
    /// ⚠ 저장소에 비슷한 술어가 여럿 있으나 <b>같지 않다</b> — <c>AimOcclusion</c>은
    /// <c>PlayerHealth</c>를 안 보고 <c>NpcController.SweepHitsObstacle</c>은
    /// <c>CharacterController</c>를 안 본다. 합치면 동작이 바뀌므로 여기 것만 그대로 옮겼다.
    /// </summary>
    private static bool IsCharacterCollider(Collider collider) =>
        collider.GetComponentInParent<CharacterController>() != null
        || collider.GetComponentInParent<NpcController>() != null
        || collider.GetComponentInParent<PlayerHealth>() != null;

    private void BeginWallFix() => m_armFold?.Begin();

    private void EndWallFix() => m_armFold?.End();

    /// <summary>
    /// 쓰러지는 프레임의 예방 — <b>뼈를 물리에 넘기기 전에</b> 부른다(그때는 아직 키네마틱이라
    /// 벽과 싸우지 않고 자세를 고칠 수 있다).
    /// </summary>
    private void TryFoldArmsBeforePhysics()
    {
        if (!m_wallFoldEnabled || !m_wallFoldOnEntry || !HasMoveAuthority || m_armFold == null)
            return;

        m_armFold.FoldOnEntry(BuildFoldTuning());
    }

    /// <summary>
    /// 한 물리 스텝 — <b><c>FixedUpdate</c>의 마지막</b>에서만 부른다.
    /// 자세가 바뀌는 중이면 스트림을 되살린다: 안 되살리면 <b>호스트에서만 팔이 빠지고 원격
    /// 화면에는 박힌 그대로 남는다</b>(정착과 함께 자세 스트림이 끊겨 있다).
    /// </summary>
    private void TickWallFix()
    {
        if (!m_wallFoldEnabled || m_armFold == null || !m_armFold.IsWindowOpen)
            return;

        // 묶인 몸은 건드리지 않는다 — 끄는 쪽이 이미 빼내는 중이고, 둘이 싸운다.
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
