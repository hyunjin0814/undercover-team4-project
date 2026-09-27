using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 충전식 아이템의 배터리 잔량을 서버 권위로 동기화하는 컴포넌트.
/// 충전 차단 조건은 아이템 본체가 CanCharge로 주입한다.
/// </summary>
[RequireComponent(typeof(ToastFeedback))]
[RequireComponent(typeof(OwnerFeedback))]
public class ItemBattery : NetworkBehaviour, IChargeable
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ToastFeedback m_toast;

    private ToastFeedback Toast => this.ResolveCapability(ref m_toast);

    [Tooltip("배터리 최대치 — 아이템 사용 1회당 1 소모한다 (GDD 5-2)")]
    [SerializeField] private int m_maxBattery = 5;

    private readonly NetworkVariable<int> m_current = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int CurrentBattery => m_current.Value;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_current.Value >= m_maxBattery;
    public bool IsDepleted => m_current.Value <= 0;

    public event Action<int> OnCharged;

    public Func<bool> CanCharge;

    public string ChargeBlockedReason { get; set; } = "충전 실패 — 아이템 사용 중";

    public EItemFeedback FullyChargedFeedback { get; set; } = EItemFeedback.BatteryFull;

    /// <summary>배터리를 amount만큼 충전한다. 실제 충전은 서버에서 처리한다.</summary>
    public void Charge(int amount)
    {
        if (amount <= 0)
            return;

        if (this.HasServerAuthority())
        {
            ServerCharge(amount);
            return;
        }

        RequestChargeRpc(amount);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestChargeRpc(int amount) => ServerCharge(amount);

    private void ServerCharge(int amount)
    {
        if (!this.HasServerAuthority())
            return;

        if (CanCharge != null && !CanCharge())
        {
            Feedback?.NotifyOwner(ChargeBlockedReason);
            return;
        }

        if (IsFullyCharged)
        {
            Toast?.ToastOwner(FullyChargedFeedback);
            return;
        }

        m_current.Value = Mathf.Min(m_current.Value + amount, m_maxBattery);
    }

    /// <summary>아이템 사용이 성공한 지점에서 본체가 호출 — 서버가 잔량을 깎는다.</summary>
    public void ServerConsume(int amount = 1)
    {
        if (!this.HasServerAuthority())
            return;

        m_current.Value = Mathf.Max(m_current.Value - amount, 0);
    }

    public override void OnNetworkSpawn()
    {
        m_current.OnValueChanged += HandleChanged;

        if (IsServer)
            m_current.Value = m_maxBattery;
    }

    public override void OnNetworkDespawn() => m_current.OnValueChanged -= HandleChanged;

    private void HandleChanged(int previous, int current) => OnCharged?.Invoke(current);
}
