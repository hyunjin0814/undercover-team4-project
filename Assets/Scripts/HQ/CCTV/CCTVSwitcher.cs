using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 CCTV 콘솔 상태 보유자 — 채널·전원을 서버 권위로 동기화하고, 카메라 렌더/RT 적용은 각 클라가 로컬로 한다.
/// 상호작용은 별도 버튼 컴포넌트가 담당한다.
/// </summary>
public class CCTVSwitcher : NetworkBehaviour
{
    private const int k_noEntry = -1;

    private const int k_maxEntry = 999;

    [Tooltip("이 콘솔이 돌려 볼 CCTV들 — 순서가 곧 채널 번호다")]
    [SerializeField]
    CCTVNode[] m_installations;

    [SerializeField]
    RenderTexture m_monitorRt;

    [SerializeField]
    LayerMask m_volumeLayers = ~0;

    [SerializeField]
    Color m_monitorBacklightEmission = new(0.05f, 0.07f, 0.09f);

    [SerializeField]
    Color m_infraredMonitorEmission = new(1.4f, 1.4f, 1.4f);

    private Camera[] m_cameras;
    private Renderer m_monitorRenderer;

    private readonly NetworkVariable<int> m_currentIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> m_isPowered = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> m_isInfrared = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<int> m_entry = new(
        k_noEntry,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private bool m_externallyJammed;

    private DeviceBlackoutEvent m_blackout;

    public string CurrentLocationLabel =>
        IsDisplaying ? GetLocationLabel(CurrentIndex) : string.Empty;

    /// <summary>index번째 채널의 설치 위치 이름을 돌려준다. 없으면 빈 문자열.</summary>
    public string GetLocationLabel(int index) =>
        m_installations != null
        && index >= 0
        && index < m_installations.Length
        && m_installations[index] != null
            ? m_installations[index].LocationLabel
            : string.Empty;

    public int ChannelCount => m_installations != null ? m_installations.Length : 0;
    public int CurrentIndex => m_currentIndex.Value;
    public bool IsPowered => m_isPowered.Value;
    public bool IsExternallyJammed => m_externallyJammed;
    public bool IsInfrared => m_isInfrared.Value;

    public bool IsDisplaying => m_isPowered.Value && !m_externallyJammed && ChannelCount > 0;

    public int PendingEntry => m_entry.Value;

    public event Action OnDisplayChanged;

    public override void OnNetworkSpawn()
    {
        CacheNodes();
        m_currentIndex.OnValueChanged += HandleIndexChanged;
        m_isPowered.OnValueChanged += HandlePowerChanged;
        m_isInfrared.OnValueChanged += HandleInfraredChanged;
        m_entry.OnValueChanged += HandleEntryChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_currentIndex.OnValueChanged -= HandleIndexChanged;
        m_isPowered.OnValueChanged -= HandlePowerChanged;
        m_isInfrared.OnValueChanged -= HandleInfraredChanged;
        m_entry.OnValueChanged -= HandleEntryChanged;
    }

    private void Start()
    {
        m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();
        if (m_blackout == null)
            return;

        m_blackout.OnCommsBlackoutChanged += SetExternallyJammed;

        SetExternallyJammed(m_blackout.IsCommsBlackout);
    }

    public override void OnDestroy()
    {
        if (m_blackout != null)
            m_blackout.OnCommsBlackoutChanged -= SetExternallyJammed;

        base.OnDestroy();
    }

    private void CacheNodes()
    {
        m_monitorRenderer = GetComponent<Renderer>();

        int count = ChannelCount;
        m_cameras = new Camera[count];
        for (int i = 0; i < count; i++)
        {
            if (m_installations[i] == null)
                continue;

            m_installations[i].SetChannel(i + 1);

            m_cameras[i] = m_installations[i].GetComponentInChildren<Camera>(true);
            if (m_cameras[i] == null)
            {
                Debug.LogWarning($"CCTVSwitcher: CH{i + 1} 설치물에 카메라가 없다", m_installations[i]);
                continue;
            }

            CCTVInfraredLook.SetVolumeLayers(m_cameras[i], m_volumeLayers);
        }
    }

    private void HandleIndexChanged(int previous, int current) => Apply();

    private void HandlePowerChanged(bool previous, bool current) => Apply();

    private void HandleInfraredChanged(bool previous, bool current) => Apply();

    private void HandleEntryChanged(int previous, int current) => OnDisplayChanged?.Invoke();

    [Rpc(SendTo.Server)]
    public void RequestSwitchRpc(int delta)
    {
        int count = ChannelCount;
        if (count == 0 || delta == 0)
            return;
        if (!m_isPowered.Value)
            return;
        if (m_externallyJammed)
            return;

        m_currentIndex.Value = ((m_currentIndex.Value + delta) % count + count) % count;
    }

    /// <summary>키패드 숫자 입력 — 자릿수만큼 쌓인다. 0~9 버튼이 부른다.</summary>
    [Rpc(SendTo.Server)]
    public void RequestAppendDigitRpc(int digit)
    {
        if (digit < 0 || digit > 9)
            return;
        if (!m_isPowered.Value || m_externallyJammed)
            return;

        int next = (m_entry.Value < 0 ? 0 : m_entry.Value) * 10 + digit;
        m_entry.Value = next > k_maxEntry ? digit : next;
    }

    /// <summary>입력한 번호로 채널을 옮기고 입력을 비운다(확인 버튼).</summary>
    [Rpc(SendTo.Server)]
    public void RequestConfirmEntryRpc()
    {
        if (!m_isPowered.Value || m_externallyJammed)
            return;

        int index = m_entry.Value - 1;
        m_entry.Value = k_noEntry;

        if (index >= 0 && index < ChannelCount)
            m_currentIndex.Value = index;
    }

    [Rpc(SendTo.Server)]
    public void RequestTogglePowerRpc()
    {
        if (m_externallyJammed)
            return;

        m_isPowered.Value = !m_isPowered.Value;

        if (!m_isPowered.Value)
            m_entry.Value = k_noEntry;
    }

    [Rpc(SendTo.Server)]
    public void RequestToggleInfraredRpc()
    {
        if (!m_isPowered.Value)
            return;
        if (m_externallyJammed)
            return;

        m_isInfrared.Value = !m_isInfrared.Value;
    }

    /// <summary>외부 차단(먹통 등) 설정 — 로컬 시각 상태. 각 피어가 자기 화면을 끈다. (#106 연동)</summary>
    public void SetExternallyJammed(bool value)
    {
        if (m_externallyJammed == value)
            return;
        m_externallyJammed = value;

        if (value && IsSpawned && IsServer)
            m_entry.Value = k_noEntry;

        Apply();
    }

    private void Apply()
    {
        bool displaying = IsDisplaying;

        if (m_cameras != null)
        {
            for (int i = 0; i < m_cameras.Length; i++)
            {
                if (m_cameras[i] == null)
                    continue;

                bool active = displaying && i == m_currentIndex.Value;
                m_cameras[i].targetTexture = active ? m_monitorRt : null;
                m_cameras[i].enabled = active;
                if (m_installations != null && i < m_installations.Length && m_installations[i] != null)
                    m_installations[i].SetSelected(active);

                CCTVInfraredLook.Apply(m_cameras[i], active && m_isInfrared.Value);
            }
        }

        if (!displaying)
            ClearMonitor();

        CCTVInfraredLook.ApplyMonitorEmission(
            m_monitorRenderer,
            displaying,
            m_isInfrared.Value,
            m_monitorRt,
            m_monitorBacklightEmission,
            m_infraredMonitorEmission
        );

        OnDisplayChanged?.Invoke();
    }

    private void ClearMonitor()
    {
        if (m_monitorRt == null)
            return;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = m_monitorRt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = previous;
    }
}
