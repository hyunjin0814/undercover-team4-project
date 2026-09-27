using System;

/// <summary>
/// 충전 가능한 아이템 인터페이스 — 배터리 상태를 노출하고 Charge로 충전한다.
/// </summary>
public interface IChargeable
{
    int CurrentBattery { get; }

    int MaxBattery { get; }

    bool IsFullyCharged { get; }

    bool IsDepleted { get; }

    event Action<int> OnCharged;

    /// <summary>배터리를 amount만큼 충전한다. 최대치를 넘지 않도록 구현체가 클램프한다. (본부 충전기가 호출)</summary>
    void Charge(int amount);
}
