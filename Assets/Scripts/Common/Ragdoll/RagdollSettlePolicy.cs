using UnityEngine;

public enum ERagdollSettleStep
{
    Wait,

    Settle,

    ForceSleepThenSettle,
}

/// <summary>
/// 래그돌이 무너지는 동안 언제 정착시킬지 시간으로 판정한다(경과·타임아웃·공중 대기).
/// 결정만 돌려주고 정착 처리는 소유자(NpcRagdoll·PlayerRagdoll)가 한다.
/// </summary>
public sealed class RagdollSettlePolicy
{
    private const float k_lostBodyTimeoutFactor = 4f;

    private float m_elapsed;

    /// <summary>이번 무너짐이 시작됐다(또는 끝났다) — 경과를 0으로 되돌린다.</summary>
    public void Reset() => m_elapsed = 0f;

    /// <summary>한 프레임. 아직 정착하지 않은 몸에만 부른다.</summary>
    public ERagdollSettleStep Tick(
        bool allAsleep,
        bool beingCarried,
        float timeoutSeconds,
        System.Func<bool> hasGroundUnderHips
    )
    {
        if (beingCarried)
        {
            m_elapsed = 0f;
            return ERagdollSettleStep.Wait;
        }

        m_elapsed += Time.deltaTime;

        if (allAsleep)
            return ERagdollSettleStep.Settle;

        if (m_elapsed < timeoutSeconds)
            return ERagdollSettleStep.Wait;

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
