using UnityEngine;

/// <summary>
/// 피해를 받을 수 있는 대상 인터페이스. 구현체는 서버 권위로만 값을 바꾼다.
/// </summary>
public interface IDamageable
{
    /// <summary>피해 적용. attacker는 데미지 출처 — 패배 판정·어그로 등에 쓰이며 null 허용.</summary>
    void TakeDamage(int amount, GameObject attacker);
}
