using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 충전식 아이템의 배터리 — 잔량을 서버 권위로 들고 있는 독립 컴포넌트. (#55/#60, GDD 5-2)
/// 아이템 본체와 같은 NetworkObject에 붙는다. 잔량은 아이템에 실려 전 클라에 동기화되고
/// 줍기/버리기 시 함께 이동한다 (#88).
///
/// 언제 충전을 막을지는 본체가 <see cref="CanCharge"/>로 꽂아 주므로, 이 컴포넌트는 자기가 어떤
/// 아이템에 붙었는지 알 필요가 없다 — 참조는 본체 → 배터리 한 방향뿐이다.
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

    // 쓰기는 Server만 — 초기화·소모·충전을 전부 서버가 수행해 클라 조작을 원천 차단한다 (#55).
    // 읽기는 Everyone (본부 UI 포함).
    private readonly NetworkVariable<int> m_current = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int CurrentBattery => m_current.Value;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_current.Value >= m_maxBattery;
    public bool IsDepleted => m_current.Value <= 0;

    // 배터리 변화 시 발행 — NetworkVariable.OnValueChanged로 구동되어 전 클라 UI가 갱신된다.
    public event Action<int> OnCharged;

    /// <summary>지금 충전을 받아도 되는지 — 본체가 꽂는다 (Scanner: 스캔 채널링 중 거부 #60). null이면 항상 허용.</summary>
    public Func<bool> CanCharge;

    /// <summary>CanCharge가 막았을 때 오너 콘솔에 남길 사유. 본체가 자기 표현으로 덮어쓴다.</summary>
    public string ChargeBlockedReason { get; set; } = "충전 실패 — 아이템 사용 중";

    /// <summary>완충 상태에서 충전을 시도했을 때 띄울 오너 토스트 (#309). 본체가 자기 값으로 덮어쓴다.</summary>
    public EItemFeedback FullyChargedFeedback { get; set; } = EItemFeedback.BatteryFull;

    /// <summary>
    /// 배터리를 amount만큼 충전한다. 오너·비오너(본부 충전기 #60) 모두 호출 가능.
    /// 실제 충전은 서버에서 처리되며, 결과는 NetworkVariable로 전 클라에 동기화된다 (#55).
    /// </summary>
    public void Charge(int amount)
    {
        if (amount <= 0)
            return;

        if (this.HasServerAuthority())
        {
            ServerCharge(amount);
            return;
        }

        // 클라 → ServerRpc 경유. InvokePermission = Everyone으로 명시 — 본부 충전기(#60) 등 비오너도 호출 가능.
        RequestChargeRpc(amount);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestChargeRpc(int amount) => ServerCharge(amount);

    private void ServerCharge(int amount)
    {
        if (!this.HasServerAuthority())
            return;

        // 본체가 막는 동안엔 거부 — "충전은 본부에서만·왕복 필요" 리듬 설계를 우회하는 걸 막는다 (#60 리뷰, #109).
        if (CanCharge != null && !CanCharge())
        {
            Feedback?.NotifyOwner(ChargeBlockedReason);
            return;
        }

        // 이미 완충이면 값 변화가 없어 OnCharged가 안 울리므로, 여기서 직접 오너 토스트를 띄운다 (#309).
        if (IsFullyCharged)
        {
            Toast?.ToastOwner(FullyChargedFeedback);
            return;
        }

        m_current.Value = Mathf.Min(m_current.Value + amount, m_maxBattery);
    }

    /// <summary>아이템 사용이 성공한 지점에서 본체가 호출 — 서버가 잔량을 깎는다. (#55)</summary>
    public void ServerConsume(int amount = 1)
    {
        if (!this.HasServerAuthority())
            return;

        m_current.Value = Mathf.Max(m_current.Value - amount, 0);
    }

    public override void OnNetworkSpawn()
    {
        // 초기값 세팅보다 먼저 구독해 서버의 0→최대 변화도 이벤트로 받게 한다.
        m_current.OnValueChanged += HandleChanged;

        // 초기값은 서버가 채운다 — 전 클라에 복제되고, 뒤늦게 접속한 클라도 스폰 동기화로 현재값을 받는다 (#55).
        if (IsServer)
            m_current.Value = m_maxBattery;
    }

    public override void OnNetworkDespawn() => m_current.OnValueChanged -= HandleChanged;

    private void HandleChanged(int previous, int current) => OnCharged?.Invoke(current);
}
