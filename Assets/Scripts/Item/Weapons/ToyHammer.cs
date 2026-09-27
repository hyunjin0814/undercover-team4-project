using UnityEngine;

/// <summary>
/// 거대 뿅망치 — 낮은 확률로 대박이 터지면 9999 데미지를 넣는 진압봉 파생 아이템.
/// </summary>
public class ToyHammer : Baton
{
    [Header("거대 뿅망치 (#816)")]
    [Tooltip("한 대당 대박이 터질 확률. 0.01 = 1%. 굴림은 서버에서만 돈다 (RollSwingPower)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_criticalChance = 0.01f;

    [Tooltip("대박이 터졌을 때의 데미지 — 어떤 체력이든 0으로 만든다(동료는 다운 또는 확인사살)")]
    [Min(1)]
    [SerializeField]
    private int m_criticalDamage = 9999;

    protected override string WeaponLogName => "뿅망치";

    /// <summary>평타 데미지에 낮은 확률로 대박 데미지를 얹는다. 서버 판정 경로에서만 호출된다.</summary>
    protected override SwingPower RollSwingPower() =>
        Random.value < m_criticalChance
            ? new SwingPower(m_criticalDamage, true)
            : base.RollSwingPower();

    /// <summary>평타·대박 여부에 따른 뿅망치 전용 타격 연출을 돌려준다.</summary>
    protected override EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical) =>
        critical ? EFx.HammerCrit : EFx.HammerHit;
}
