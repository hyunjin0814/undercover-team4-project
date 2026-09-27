using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 현상금 추첨 — 모든 검거 대상의 금액을 100원 단위로 뽑는 공통 경로.
/// </summary>
public static class BountyRoll
{
    public const int k_unit = 100;

    /// <summary>[min, max] 안에서 단위 배수 금액을 뽑는다. 상한이 하한보다 작아도 하한을 보장한다.</summary>
    public static int Roll(int min, int max)
    {
        if (max < min)
            max = min;

        int lo = Mathf.CeilToInt(min / (float)k_unit);
        int hi = Mathf.FloorToInt(max / (float)k_unit);

        if (hi < lo)
            return Mathf.RoundToInt(min / (float)k_unit) * k_unit;

        return Random.Range(lo, hi + 1) * k_unit;
    }

    /// <summary>비율만큼 깎고 <see cref="k_unit"/> 배수로 떨어뜨린다 — 추첨이 아니라 결정적 계산이라 재판정해도 같은 값이 나온다.</summary>
    public static int Reduce(int bounty, float ratio)
    {
        if (bounty <= 0)
            return 0;

        float kept = 1f - Mathf.Clamp01(ratio);
        return Mathf.Max(0, Mathf.RoundToInt(bounty * kept / k_unit) * k_unit);
    }
}
