using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 로비에 상시 노출되는 접속자 목록 패널. 명부(App.Game.Roster)가 바뀔 때마다 행을 다시 바인딩한다.
/// </summary>
public class LobbyRosterPanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("호스트 버튼")]
    [SerializeField] private Button m_startButton;

    [SerializeField] private Button m_leaveButton;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_rowContainer;

    [SerializeField] private LobbyRosterRowView m_rowPrefab;

    [Tooltip("세션이 없을 때(씬 직접 Play) 쓸 정원 표시값")]
    [SerializeField] private int m_fallbackMaxSlots = 6;

    [Header("음성 (#430)")]
    [SerializeField] private TextMeshProUGUI m_voiceStatusText;

    [SerializeField] private TextMeshProUGUI m_radioKeyText;

    [Tooltip("무전/음소거 키 안내 — Lobby.Voice.RadioKey ({0}=무전 키, {1}=음소거 키)")]
    [SerializeField] private LocalizedString m_radioKeyFormat;

    [Tooltip("카드에 넣을 얼굴을 굽는 무대 (#598). 비워 두면 얼굴 칸 없이 이름만 나온다")]
    [SerializeField] private LobbyPortraitStage m_portraitStage;

    private const string k_voiceTable = "LobbyTable";
    private const string k_voiceKeyPrefix = "Lobby.Voice.";
    private readonly LocalizedString m_voiceStatus = new LocalizedString();

    private bool m_voiceStatusBound;
    private bool m_radioKeyBound;

    private readonly List<LobbyRosterRowView> m_rows = new List<LobbyRosterRowView>();

    private SessionRoster m_roster;

    private VivoxManager Vivox => App.Net.Vivox;
    private SessionManager Session => App.Net.Session;

    private int MaxSlots =>
        Session != null && Session.CurrentSession != null
            ? Session.CurrentSession.MaxPlayers
            : m_fallbackMaxSlots;

    private LobbyManager Lobby => App.SceneFlow.Lobby;

    private static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    protected override void Awake()
    {
        base.Awake();
        if (m_startButton != null)
            m_startButton.onClick.AddListener(HandleStart);
        if (m_leaveButton != null)
            m_leaveButton.onClick.AddListener(HandleLeave);
    }

    private void OnEnable()
    {
        if (m_startButton != null)
            m_startButton.gameObject.SetActive(IsServer);

        TryBindRoster();

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged += HandleVoiceStateChanged;
        }

        RefreshVoiceStatus();
        RefreshRadioKey();

        Rebuild();
    }

    private void Start() => Rebuild();

    private void Update()
    {
        if (m_roster == null)
            TryBindRoster();
    }

    private void TryBindRoster()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return;

        m_roster = roster;
        m_roster.Players.OnListChanged += HandleListChanged;
        m_roster.OnListReady += Rebuild;

        Rebuild();
    }

    private void OnDisable()
    {
        if (m_roster != null)
        {
            m_roster.Players.OnListChanged -= HandleListChanged;
            m_roster.OnListReady -= Rebuild;
        }
        m_roster = null;

        if (Vivox != null)
        {
            Vivox.OnSpeakingChanged -= HandleSpeakingChanged;
            Vivox.OnVoiceStateChanged -= HandleVoiceStateChanged;
        }

        UnbindVoiceStatus();
        UnbindRadioKey();
    }

    protected override void OnDestroy()
    {
        if (m_startButton != null)
            m_startButton.onClick.RemoveListener(HandleStart);
        if (m_leaveButton != null)
            m_leaveButton.onClick.RemoveListener(HandleLeave);
        base.OnDestroy();
    }

    private void HandleStart()
    {
        m_startButton.interactable = false;
        Lobby?.StartGame();
    }

    private static void HandleLeave() => App.UI.Current?.OpenPanel<LeaveConfirmPanel>();

    private void HandleListChanged(NetworkListEvent<LobbyPlayerEntry> _) => Rebuild();

    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
        {
            Debug.LogWarning("LobbyRosterPanel: 행 프리팹/컨테이너가 지정되지 않았습니다.", this);
            return;
        }

        int playerCount = m_roster != null && m_roster.IsSpawned ? m_roster.Players.Count : 0;
        int slots = Mathf.Max(playerCount, MaxSlots);

        while (m_rows.Count < slots)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > slots)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < slots; i++)
        {
            if (i < playerCount)
            {
                LobbyPlayerEntry entry = m_roster.Players[i];
                m_rows[i].Bind(entry, m_roster.IsHostEntry(entry));

                m_rows[i].SetPortrait(
                    m_portraitStage != null
                        ? m_portraitStage.GetPortrait(entry.Colors, entry.Accessories)
                        : null
                );
            }
            else
            {
                m_rows[i].BindEmpty();
            }
        }

        ApplyRowScale(slots);

        RefreshSpeaking();
    }

    private void ApplyRowScale(int slots)
    {
        if (slots == 0 || m_rows.Count == 0 || m_rows[0] == null)
            return;

        RectTransform firstRow = (RectTransform)m_rows[0].transform;
        float spacing = m_rowContainer.TryGetComponent(out HorizontalLayoutGroup layout) ? layout.spacing : 0f;
        float totalWidth = slots * firstRow.rect.width + (slots - 1) * spacing;
        float scale = totalWidth > m_rowContainer.rect.width ? m_rowContainer.rect.width / totalWidth : 1f;

        foreach (LobbyRosterRowView row in m_rows)
        {
            if (row != null)
                row.transform.localScale = Vector3.one * scale;
        }
    }

    private void HandleSpeakingChanged(string playerId, bool speaking)
    {
        if (string.IsNullOrEmpty(playerId))
            return;

        foreach (LobbyRosterRowView row in m_rows)
        {
            if (row != null && row.PlayerId == playerId)
                row.SetSpeaking(speaking);
        }
    }

    private void RefreshSpeaking()
    {
        if (Vivox == null)
            return;

        foreach (LobbyRosterRowView row in m_rows)
        {
            if (row != null && !string.IsNullOrEmpty(row.PlayerId))
                row.SetSpeaking(Vivox.IsSpeaking(row.PlayerId));
        }
    }

    private void HandleVoiceStateChanged(EVoiceState _) => RefreshVoiceStatus();

    private void RefreshVoiceStatus()
    {
        if (m_voiceStatusText == null)
            return;

        if (Vivox == null)
        {
            UnbindVoiceStatus();
            m_voiceStatusText.text = string.Empty;
            return;
        }

        UnbindVoiceStatus();

        m_voiceStatus.TableReference = k_voiceTable;
        m_voiceStatus.TableEntryReference = k_voiceKeyPrefix + Vivox.VoiceState;
        m_voiceStatus.StringChanged += HandleVoiceStatusChanged;
        m_voiceStatusBound = true;
    }

    private void HandleVoiceStatusChanged(string localized)
    {
        if (m_voiceStatusText != null)
            m_voiceStatusText.text = localized;
    }

    private void UnbindVoiceStatus()
    {
        if (!m_voiceStatusBound)
            return;

        m_voiceStatus.StringChanged -= HandleVoiceStatusChanged;
        m_voiceStatusBound = false;
    }

    private void RefreshRadioKey()
    {
        if (m_radioKeyText == null)
            return;

        if (Vivox == null)
        {
            UnbindRadioKey();
            m_radioKeyText.text = string.Empty;
            return;
        }

        if (m_radioKeyFormat == null || m_radioKeyFormat.IsEmpty)
        {
            Debug.LogWarning("LobbyRosterPanel: 무전 키 안내 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindRadioKey();

        m_radioKeyFormat.Arguments = new object[] { Vivox.PushToTalkBinding, Vivox.MicMuteBinding };
        m_radioKeyFormat.StringChanged += HandleRadioKeyChanged;
        m_radioKeyBound = true;
    }

    private void HandleRadioKeyChanged(string localized)
    {
        if (m_radioKeyText != null)
            m_radioKeyText.text = localized;
    }

    private void UnbindRadioKey()
    {
        if (!m_radioKeyBound)
            return;

        m_radioKeyFormat.StringChanged -= HandleRadioKeyChanged;
        m_radioKeyBound = false;
    }
}
