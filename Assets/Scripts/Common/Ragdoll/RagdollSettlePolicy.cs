using UnityEngine;

/// <summary>정착 폴링이 이번 프레임에 내린 결정 — 처리는 부르는 쪽이 한다.</summary>
public enum ERagdollSettleStep
{
    /// <summary>아직 아무것도 하지 않는다 — 무너지는 중이거나, 끌리는 중이거나, 아직 공중이다.</summary>
    Wait,

    /// <summary>물리가 스스로 잠들었다 — 정착시킨다.</summary>
    Settle,

    /// <summary>타임아웃 — 지형에 물려 스스로 못 잠드는 몸이다. <b>대신 재운 뒤</b> 정착시킨다.</summary>
    ForceSleepThenSettle,
}

/// <summary>
/// <b>언제 정착시킬 것인가</b> — 래그돌이 무너지는 동안의 시간 판정 하나만 쥔다.
///
/// <c>NpcRagdoll</c>과 <c>PlayerRagdoll</c>이 <b>같은 절차를 두 벌</b> 구현하고 있던 것을 모았다.
/// 경과 누산 · 타임아웃 · "아직 공중이면 4배까지 기다린다"가 양쪽에 복제돼 있었고, 한쪽만 고치면
/// 조용히 어긋나는 종류였다.
///
/// <b>정착 여부(<c>m_settled</c>)는 여기 없다.</b> 그 깃발은 소유자가 쥔다 — 플레이어 쪽은
/// <c>PlayerIncapacitation</c>이 <c>IsSettled</c>로 밖에서 읽고, 깨어남 처리도 소유자마다 다르다
/// (플레이어는 구조 채널링 중이면 깨우지 않고 도로 재운다, #865). 여기는 <b>결정만</b> 돌려주고
/// 처리는 각자 한다 — <c>docs/refactoring/playerloadout-split.md</c> §5의 <c>NetworkRefResolver</c>와
/// 같은 형태다.
///
/// 물리도 네트워크도 모른다. 권위 게이트는 부르는 쪽이 이미 지난 뒤다.
/// </summary>
public sealed class RagdollSettlePolicy
{
    // 지면을 못 찾아도 결국 정착시키는 최후 배수 — 맵 밖으로 떨어진 몸이 Ragdoll에 갇히지 않게.
    private const float k_lostBodyTimeoutFactor = 4f;

    private float m_elapsed;

    /// <summary>이번 무너짐이 시작됐다(또는 끝났다) — 경과를 0으로 되돌린다.</summary>
    public void Reset() => m_elapsed = 0f;

    /// <summary>
    /// 한 프레임. <b>아직 정착하지 않은 몸에만</b> 부른다.
    /// </summary>
    /// <param name="allAsleep">전 뼈가 잠들었는가 — <b>정착은 물리가 정한다</b>(docs §9).</param>
    /// <param name="beingCarried">
    /// 밧줄에 끌리는 중인가 — 참이면 경과를 0으로 되돌린다. 끌리는 몸은 어차피 안 잠들지만
    /// 타임아웃까지 흐르면 <b>끌고 가는 중에 강제 수면이 걸린다.</b>
    /// </param>
    /// <param name="timeoutSeconds">소유자의 인스펙터 값 — 프리팹이 정본이라 매 프레임 넘겨받는다.</param>
    /// <param name="hasGroundUnderHips">
    /// 골반 밑에 지면이 있는가. ⚠ <b>지연 평가로 받는다</b> — 레이캐스트라서, 타임아웃을 넘긴
    /// 프레임에만 쏴야 시체가 쌓이는 라운드에서 비용이 시체 수에 비례하지 않는다.
    /// </param>
    public ERagdollSettleStep Tick(
        bool allAsleep,
        bool beingCarried,
        float timeoutSeconds,
        System.Func<bool> hasGroundUnderHips
    )
    {
        if (beingCarried)
        {
            m_elapsed = 0f; // 놓는 순간부터 다시 센다
            return ERagdollSettleStep.Wait;
        }

        m_elapsed += Time.deltaTime;

        // <b>정착은 물리가 정한다.</b> 전 뼈가 하나도 안 남고 잠들어야 참이다 — 팔 하나가 아직
        // 흔들리고 있으면 그 팔이 전체를 붙잡는다.
        if (allAsleep)
            return ERagdollSettleStep.Settle;

        if (m_elapsed < timeoutSeconds)
            return ERagdollSettleStep.Wait;

        // 아직 공중이다 — 여기서 재우면 <b>떠 있는 시체</b>가 된다. 폭발에 크게 날아간 몸은
        // 타임아웃 뒤에도 비행 중일 수 있다. 다만 맵 밖으로 떨어진 몸이 영원히 갇히지 않게
        // 무한정 기다리지는 않는다.
        if (
            m_elapsed < timeoutSeconds * k_lostBodyTimeoutFactor
            && hasGroundUnderHips != null
            && !hasGroundUnderHips()
        )
        {
            return ERagdollSettleStep.Wait;
        }

        return ERagdollSettleStep.ForceSleepThenSettle;
    }
}
