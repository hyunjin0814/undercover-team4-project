/// <summary>
/// 추격 표적으로의 부분 경로가 이어진 시간을 누적해 도달 불가 여부를 판정한다(Unity 비의존).
/// </summary>
public class ChaseReachability
{
    private const float k_unreachableSeconds = 1.8f;

    private readonly float m_unreachableSeconds;

    private float m_partialSince;

    public ChaseReachability(float unreachableSeconds = k_unreachableSeconds)
    {
        m_unreachableSeconds = unreachableSeconds;
    }

    /// <summary>이번 경로 계산이 부분이었는지 알린다 — 정상이면 누적이 풀린다.</summary>
    public void Report(bool partial, float now)
    {
        if (!partial)
        {
            m_partialSince = 0f;
            return;
        }

        if (m_partialSince <= 0f)
            m_partialSince = now;
    }

    /// <summary>부분 경로가 확정 시간만큼 이어졌는가 — 표적이 있는 곳으로 갈 길 자체가 없다.</summary>
    public bool IsUnreachable(float now) =>
        m_partialSince > 0f && now - m_partialSince >= m_unreachableSeconds;

    /// <summary>누적을 버린다 — 표적이 바뀌거나 상태에 새로 진입할 때.</summary>
    public void Clear() => m_partialSince = 0f;
}
