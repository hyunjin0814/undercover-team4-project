using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 머리 위 이름표 — 닉네임·발화 아이콘·음소거 아이콘을 표시한다.
/// 닉네임·PlayerId·음소거는 오너 쓰기 NetworkVariable로, 발화는 각 클라의 Vivox로 로컬 판정한다.
/// </summary>
public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField]
    private TextMeshProUGUI m_label;

    [SerializeField]
    private Transform m_tagRoot;

    [SerializeField]
    private float m_showDistance = 15f;
    private Transform m_cam;

    private readonly NetworkVariable<FixedString64Bytes> m_name = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    [SerializeField]
    private GameObject m_speakerIcon;

    [SerializeField]
    private GameObject m_micMutedIcon;

    private bool m_speaking;

    public string DisplayName => m_name.Value.ToString();

    private readonly NetworkVariable<FixedString64Bytes> m_playerId = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private readonly NetworkVariable<bool> m_micMuted = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private bool m_isLocalPlayer;

    private void LateUpdate()
    {
        if (m_isLocalPlayer || !IsSpawned)
            return;

        if (m_name.Value.IsEmpty)
        {
            m_tagRoot.gameObject.SetActive(false);
            return;
        }

        var cam = ResolveCamera();
        if (cam == null)
            return;

        m_tagRoot.rotation = cam.rotation;
        m_tagRoot.gameObject.SetActive(
            Vector3.Distance(cam.position, transform.position) <= m_showDistance
        );
    }

    public override void OnNetworkSpawn()
    {
        m_isLocalPlayer = IsOwner;

        if (IsOwner)
        {
            m_name.Value = App.Net.Auth.Nickname.ToFixed64();
            m_playerId.Value = App.Net.Auth.PlayerId.ToFixed64();

            m_micMuted.Value = GameSettings.MicMuted;
            GameSettings.OnMicMutedChanged += HandleOwnerMicMutedChanged;

            m_tagRoot.gameObject.SetActive(false);

            if (m_speakerIcon != null)
                m_speakerIcon.SetActive(false);

            if (m_micMutedIcon != null)
                m_micMutedIcon.SetActive(false);
        }
        else
        {
            m_name.OnValueChanged += HandleNameChanged;
            if (!m_name.Value.IsEmpty)
                HandleNameChanged(default, m_name.Value);

            if (App.Net.Vivox != null)
                App.Net.Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            m_playerId.OnValueChanged += HandleIdChanged;
            m_micMuted.OnValueChanged += HandleMicMutedChanged;

            RefreshMicMutedIcon();
            RefreshSpeakerIcon();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_name.OnValueChanged -= HandleNameChanged;
        m_playerId.OnValueChanged -= HandleIdChanged;
        m_micMuted.OnValueChanged -= HandleMicMutedChanged;
        if (App.Net.Vivox != null)
            App.Net.Vivox.OnSpeakingChanged -= HandleSpeakingChanged;

        GameSettings.OnMicMutedChanged -= HandleOwnerMicMutedChanged;
    }

    private void HandleNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        m_label.text = current.ToString();
    }

    private void HandleIdChanged(FixedString64Bytes previous, FixedString64Bytes current) =>
        RefreshSpeakerIcon();

    private void HandleSpeakingChanged(string playerId, bool speaking)
    {
        if (playerId == m_playerId.Value.ToString())
            SetSpeakerIcon(speaking);
    }

    private void RefreshSpeakerIcon()
    {
        string pid = m_playerId.Value.ToString();
        bool speaking =
            !string.IsNullOrEmpty(pid) && App.Net.Vivox != null && App.Net.Vivox.IsSpeaking(pid);
        SetSpeakerIcon(speaking);
    }

    private void SetSpeakerIcon(bool on)
    {
        m_speaking = on;

        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on && !m_micMuted.Value);
    }

    private void HandleOwnerMicMutedChanged(bool muted)
    {
        if (IsOwner && IsSpawned)
            m_micMuted.Value = muted;
    }

    private void HandleMicMutedChanged(bool previous, bool current) => RefreshMicMutedIcon();

    private void RefreshMicMutedIcon()
    {
        if (m_micMutedIcon != null)
            m_micMutedIcon.SetActive(m_micMuted.Value);

        SetSpeakerIcon(m_speaking);
    }

    private Transform ResolveCamera()
    {
        if (m_cam != null)
            return m_cam;

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();
            if (cam != null)
                m_cam = cam.transform;
        }

        if (m_cam == null && Camera.main != null)
            m_cam = Camera.main.transform;

        return m_cam;
    }
}
