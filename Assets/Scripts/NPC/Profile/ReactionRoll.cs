using Random = UnityEngine.Random;

/// <summary>
/// 검거 반응(순응/도주/저항)을 가중치 비율로 추첨하는 공통 규칙.
/// </summary>
public static class ReactionRoll
{
    /// <summary>가중치 비율로 반응을 뽑는다. 셋 다 0이면 순응.</summary>
    public static ReactionType Roll(float compliantWeight, float fleeWeight, float resistWeight)
    {
        float total = compliantWeight + fleeWeight + resistWeight;
        if (total <= 0f)
            return ReactionType.Compliant;

        float roll = Random.Range(0f, total);
        if (roll < compliantWeight)
            return ReactionType.Compliant;
        if (roll < compliantWeight + fleeWeight)
            return ReactionType.Flee;
        return ReactionType.Resist;
    }
}
