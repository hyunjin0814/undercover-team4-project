using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 진범 검거 시 검거자를 뺀 전 플레이어에게 "〈이름〉 검거 — 남은 N명" 알림을 네임드 메시지로 보낸다.
/// 이름·숫자만 보내 받는 쪽이 자기 언어로 조립한다.
/// </summary>
public class ArrestNoticeBroadcaster : MonoBehaviour
{
    private const string k_messageName = "ArrestNotice";
    private const int k_writerSize = 128;

    [Tooltip("전 플레이어에게 띄울 문구 — Hud.Arrest.Notice ({0}=대상 이름, {1}=남은 수배자 수)")]
    [SerializeField] private LocalizedString m_noticeMessage;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(0f)]
    [SerializeField] private float m_noticeSeconds = 3f;

    [Tooltip("알림 배경색 — 판정 배너(VerdictBanner)와 같은 공용 팔레트의 '성공' 색을 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Tooltip("알림 배경 채움 투명도")]
    [Range(0f, 1f)]
    [SerializeField] private float m_noticeAlpha = 0.95f;

    private ArrestJudge Judge => App.Game.ArrestJudge;
    private WantedListManager WantedList => App.Game.WantedList;

    private readonly HashSet<ulong> m_arresters = new HashSet<ulong>();
    private readonly List<ulong> m_targets = new List<ulong>();

    private void OnEnable()
    {
        if (Judge == null)
            return;

        Judge.OnArrestJudged += HandleArrestJudged;
        Judge.OnCorpseJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (Judge == null)
            return;

        Judge.OnArrestJudged -= HandleArrestJudged;
        Judge.OnCorpseJudged -= HandleArrestJudged;
    }

    private NamedMessageSubscription m_message;

    private void Start()
    {
        m_message = new NamedMessageSubscription(k_messageName, ReceiveNotice);
        m_message.Attach();
    }

    private void OnDestroy() => m_message?.Detach();

    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        WantedListManager wantedList = WantedList;
        if (wantedList == null || !wantedList.IsSpawned)
            return;

        int remaining = wantedList.Wanted.Count - (IsOnWantedList(wantedList, result.Npc) ? 1 : 0);

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        string citizenName = ResolveName(result);

        m_arresters.Clear();
        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer != null)
                m_arresters.Add(deliverer.OwnerClientId);
        }

        if (!m_arresters.Contains(nm.LocalClientId))
            ShowLocal(citizenName, remaining);

        Broadcast(citizenName, remaining);
    }

    private static string ResolveName(ArrestResult result)
    {
        if (result.Profile != null)
            return result.Profile.CitizenName;

        return result.Npc != null ? result.Npc.name : "알 수 없음";
    }

    private static bool IsOnWantedList(WantedListManager wantedList, NpcController npc)
    {
        if (npc == null)
            return false;

        for (int i = 0; i < wantedList.Wanted.Count; i++)
        {
            if (wantedList.Wanted[i].NpcId == npc.NetworkObjectId)
                return true;
        }

        return false;
    }

    private void Broadcast(string citizenName, int remaining)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        m_targets.Clear();
        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (clientId == nm.LocalClientId || m_arresters.Contains(clientId))
                continue;

            m_targets.Add(clientId);
        }

        if (m_targets.Count == 0)
            return;

        FixedString64Bytes name = citizenName.ToFixed64();

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe(remaining);
        writer.WriteValueSafe(name);
        nm.CustomMessagingManager.SendNamedMessage(k_messageName, m_targets, writer, NetworkDelivery.Reliable);
    }

    private void ReceiveNotice(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out int remaining);
        reader.ReadValueSafe(out FixedString64Bytes name);
        ShowLocal(name.ToString(), remaining);
    }

    private void ShowLocal(string citizenName, int remaining)
    {
        if (m_noticeMessage == null || m_noticeMessage.IsEmpty)
        {
            Debug.LogWarning("ArrestNoticeBroadcaster: 전체 알림 문구가 연결되지 않았다", this);
            return;
        }

        m_noticeMessage.Arguments = new object[] { citizenName, remaining };
        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds, NoticeTone());
    }

    private Color NoticeTone()
    {
        if (m_palette == null)
        {
            Debug.LogWarning("ArrestNoticeBroadcaster: 색 팔레트가 연결되지 않았다", this);
            return Color.white;
        }

        return UiColorPalette.WithAlpha(m_palette.Positive, m_noticeAlpha);
    }
}
