using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 구역 스캔 결과를 오너 화면 토스트로 띄운다. 오너가 아니면 비활성화된다.
/// </summary>
public class AreaScanPresenter : NetworkBehaviour
{
    [Tooltip("쿨다운 중 사용 시도 토스트 — {0}=남은 초(정수)")]
    [SerializeField]
    private LocalizedString m_cooldownMessage;

    [Tooltip("먹통 중 사용 시도 토스트")]
    [SerializeField]
    private LocalizedString m_blackoutMessage;

    [Tooltip("반경 안에 진범이 있을 때의 판독 결과 토스트 — {0}=반경(m)")]
    [SerializeField]
    private LocalizedString m_hitMessage;

    [Tooltip("반경 안에 진범이 없을 때의 판독 결과 토스트 — {0}=반경(m)")]
    [SerializeField]
    private LocalizedString m_missMessage;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_toastSeconds = 2f;

    private PlayerItemUser m_itemUser;
    private AreaScanner m_scanner;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_itemUser == null)
        {
            Debug.LogWarning("AreaScanPresenter: PlayerItemUser를 찾지 못함 — 구역 스캔 토스트 표시 불가", this);
            return;
        }

        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;

        BindScanner(null);
    }

    private void HandleEquippedItemChanged(ItemBase item) => BindScanner(item as AreaScanner);

    private void BindScanner(AreaScanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
        {
            m_scanner.OnCooldownUseAttempt -= HandleCooldownUseAttempt;
            m_scanner.OnBlackoutUseAttempt -= HandleBlackoutUseAttempt;
            m_scanner.OnScanResult -= HandleScanResult;
        }

        m_scanner = scanner;

        if (m_scanner != null)
        {
            m_scanner.OnCooldownUseAttempt += HandleCooldownUseAttempt;
            m_scanner.OnBlackoutUseAttempt += HandleBlackoutUseAttempt;
            m_scanner.OnScanResult += HandleScanResult;
        }
    }

    private void HandleCooldownUseAttempt(float remaining)
    {
        if (m_cooldownMessage == null || m_cooldownMessage.IsEmpty)
            return;

        m_cooldownMessage.Arguments = new object[] { Mathf.CeilToInt(remaining) };
        App.UI.Toast?.Show(m_cooldownMessage, m_toastSeconds);
    }

    private void HandleScanResult(bool found, float radius)
    {
        LocalizedString message = found ? m_hitMessage : m_missMessage;
        if (message == null || message.IsEmpty)
            return;

        message.Arguments = new object[] { Mathf.RoundToInt(radius) };
        App.UI.Toast?.Show(message, m_toastSeconds);
    }

    private void HandleBlackoutUseAttempt()
    {
        if (m_blackoutMessage == null || m_blackoutMessage.IsEmpty)
            return;

        App.UI.Toast?.Show(m_blackoutMessage, m_toastSeconds);
    }
}
