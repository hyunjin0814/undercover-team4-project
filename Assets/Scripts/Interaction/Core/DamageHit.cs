using UnityEngine;

/// <summary>
/// 피격 1회의 표현용 정보 — OnDamaged 이벤트 페이로드로, 가해자를 월드 좌표로 환산해 담는다.
/// </summary>
public readonly struct DamageHit
{
    public readonly int Amount;

    public readonly Vector3 AttackerPosition;

    public readonly bool HasAttacker;

    public DamageHit(int amount, Vector3 attackerPosition, bool hasAttacker)
    {
        Amount = amount;
        AttackerPosition = attackerPosition;
        HasAttacker = hasAttacker;
    }
}
