using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 검거 성립 전체 알림 (#616) — 진범이 잡히면(생포·시체 인계 모두) 검거자를 뺀 전 플레이어에게
/// "〈이름〉 검거 — 남은 N명"을 띄운다. 검거자는 개인 배너(<see cref="ArrestVerdictFeedback"/>, #306)로
/// 이미 알고 있으므로 제외한다.
///
/// 이름·남은 수만 싣는다 — 누가 잡았는지·보상액은 안 보낸다(무전으로 알릴 몫을 남겨 둔다).
/// 경범죄·오검거는 알리지 않는다(수배 리스트가 안 줄거나 개인 실수라서). 반출 후 재수감은 남은
/// 수가 그대로여도 다시 알린다(팀 결정) — 진행 신호로 본다.
///
/// 남은 수는 <see cref="RemainingCriminalsHud"/>(#331)와 같은 수배 리스트에서 뽑는다.
/// 전파는 <see cref="SettlementController"/>(#107)와 같은 네임드 메시지 패턴, 완성 문장 대신
/// 이름·숫자만 보내 받는 쪽이 자기 언어로 조립한다(localization.md 결정 (g)).
/// </summary>
public class ArrestNoticeBroadcaster : MonoBehaviour
{
    private const string k_messageName = "ArrestNotice";
    private const int k_writerSize = 128; // int + FixedString64(최대 66) < 128

    [Tooltip("전 플레이어에게 띄울 문구 — Hud.Arrest.Notice ({0}=대상 이름, {1}=남은 수배자 수)")]
    [SerializeField]
    private LocalizedString m_noticeMessage;

    [Tooltip("토스트가 화면에 머무는 시간(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_noticeSeconds = 3f;

    [Tooltip("알림 배경색 — 판정 배너(VerdictBanner)의 '진범 검거' 초록과 맞춘 값")]
    [SerializeField]
    private Color m_noticeTone = new Color(0.20f, 0.70f, 0.35f, 0.95f);

    private ArrestJudge Judge => App.Game.ArrestJudge;
    private WantedListManager WantedList => App.Game.WantedList;

    private bool m_handlerRegistered;

    // 검거마다 재사용하는 버퍼 — 서버에서만 쓴다.
    private readonly HashSet<ulong> m_arresters = new HashSet<ulong>();
    private readonly List<ulong> m_targets = new List<ulong>();

    private void OnEnable()
    {
        if (Judge == null) // 판정기가 없는 단독 테스트 씬은 조용히 빠진다
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

    private void Start()
    {
        // CustomMessagingManager는 NGO가 시작된 뒤에만 존재한다.
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted += RegisterMessageHandler;
        if (nm.IsListening)
            RegisterMessageHandler();
    }

    private void OnDestroy()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted -= RegisterMessageHandler;
        if (m_handlerRegistered && nm.CustomMessagingManager != null)
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(k_messageName);
    }

    private void RegisterMessageHandler()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null)
            return;

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveNotice);
        m_handlerRegistered = true;
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        WantedListManager wantedList = WantedList;
        if (wantedList == null || !wantedList.IsSpawned) // 오프라인 단독 Play엔 수배 리스트가 없다
            return;

        // WantedListManager도 같은 이벤트를 구독해 항목을 지운다 — 구독 순서와 무관하게 맞는
        // 값을 내려고 "아직 리스트에 있으면 하나 뺀다"로 직접 계산한다.
        // 찾는 키는 <b>잡힌 개체가 아니라 충족된 조건</b>이다 (#669) — 조건 부합이면 둘이 다르고,
        // 잡힌 개체로 찾으면 대개 목록에 없어 남은 수가 하나 많게 방송된다.
        int remaining =
            wantedList.OpenCount - (IsEntryOpen(wantedList, result.MatchedWantedId) ? 1 : 0);

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        string citizenName = ResolveName(result);

        // 검거자(줄다리기 인계자 전원, #390)는 개인 배너로 이미 안다 — 대상에서 뺀다.
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

    // 이름 기준은 판정 배너와 같다 (ArrestJudge.LogVerdict와 동일).
    private static string ResolveName(ArrestResult result)
    {
        if (result.Profile != null)
            return result.Profile.CitizenName;

        return result.Npc != null ? result.Npc.name : "알 수 없음";
    }

    // 이 수배 항목이 아직 열려 있는가 — 인덱스 조회뿐이라 훑는다.
    private static bool IsEntryOpen(WantedListManager wantedList, ulong entryNpcId)
    {
        for (int i = 0; i < wantedList.OpenCount; i++)
        {
            if (wantedList.GetOpen(i).NpcId == entryNpcId)
                return true;
        }

        return false;
    }

    private void Broadcast(string citizenName, int remaining)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        // 호스트·검거자는 이미 처리했다 — 남는 사람에게만 보낸다.
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
        nm.CustomMessagingManager.SendNamedMessage(
            k_messageName,
            m_targets,
            writer,
            NetworkDelivery.Reliable
        );
    }

    private void ReceiveNotice(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) // 방어적 가드
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

        m_noticeMessage.Arguments = new object[] { citizenName, remaining }; // Show보다 먼저
        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds, m_noticeTone); // HUD 없으면 무동작
    }
}
