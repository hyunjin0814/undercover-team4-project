using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>표시에 필요한 판정 데이터 묶음 — 서버가 판정 시점에 채운다. (#306)</summary>
public struct VerdictFeedbackData
{
    public ArrestVerdict Verdict;  // 판정 종류(진범/오검거/경범죄)
    public string CitizenName;     // 대상 시민 이름(정본 CitizenName)
    public int Reward;             // 지급 보상액
}

/// <summary>
/// 검거 판정 결과 배너 전파 (#306) — 본부 인계 판정(ArrestJudge.OnArrestJudged)이 나면 그 대상을
/// 인계(검거)한 플레이어 화면에만 결과 토스트를 띄운다. 판정 결과는 검거자 개인 피드백이므로
/// 전 클라이언트가 아니라 검거자 한 명에게만 보낸다.
///
/// 전파 흐름은 SettlementController(#107)의 네임드 메시지 패턴을 따르되 대상만 좁힌다 — 판정이
/// 서버/오프라인 권위이므로(ArrestJudge):
///  · 오프라인(비세션) — 로컬 플레이어가 곧 검거자다. 그대로 로컬 배너를 띄운다.
///  · 세션 — 서버가 검거자의 clientId를 특정한다. 검거자가 호스트 자신이면 로컬 표시,
///    원격이면 그 클라이언트에게만 네임드 메시지를 보낸다.
///  · 클라이언트 — 자기 앞으로 온 네임드 메시지를 수신해 배너를 띄운다.
///
/// NetworkBehaviour가 아니므로(SettlementController와 동일) 씬 네트워크 구성을 건드리지 않는다.
/// </summary>
public class ArrestVerdictFeedback : MonoBehaviour
{
    private const string k_messageName = "ArrestVerdict";
    private const int k_writerSize = 128; // byte + int + FixedString64(최대 66) < 128

    private ArrestJudge Judge => App.Game.ArrestJudge;

    private bool m_handlerRegistered;

    private void OnEnable()
    {
        // 판정은 서버·오프라인에서만 발행된다 — 권위 피어가 이 훅으로 표시·전파를 처리한다.
        if (Judge != null)
            Judge.OnArrestJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    private void Start()
    {
        // 클라이언트 수신 등록 — CustomMessagingManager는 NGO가 시작된 뒤에만 존재한다. (SettlementController와 동일 패턴)
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

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveVerdict);
        m_handlerRegistered = true;
    }

    // 서버·오프라인: 판정 결과를 표시 데이터로 추리고, 검거한 플레이어에게만 표시(로컬 또는 단일 전송).
    private void HandleArrestJudged(ArrestResult result)
    {
        // 이름: 정본 CitizenName. 프로필 없는 경범죄(난동꾼)는 NPC 이름 폴백 — ArrestJudge.LogVerdict와 동일.
        string citizenName = result.Profile != null ? result.Profile.CitizenName
            : result.Npc != null ? result.Npc.name
            : "알 수 없음";

        var data = new VerdictFeedbackData
        {
            Verdict = result.Verdict,
            CitizenName = citizenName,
            Reward = result.Reward,
        };

        NetworkManager nm = NetworkManager.Singleton;

        // 오프라인(비세션): 로컬 플레이어가 곧 검거자 — 그대로 로컬 표시.
        if (nm == null || !nm.IsListening)
        {
            ShowLocal(data);
            return;
        }

        // 판정은 서버에서만 발행되지만, 안전장치로 서버가 아니면 라우팅하지 않는다.
        if (!nm.IsServer)
            return;

        // 검거자를 특정하지 못하면(자동 판정 등 인계자 없음) 보여줄 대상이 없다 — 조용히 스킵.
        // 줄다리기로 함께 끌고 왔으면 관여자 전원에게 보여준다 (#390) — 페널티도 전원에게 걸리므로
        // 한 명만 결과를 보면 나머지는 왜 쫓기는지 알 수 없다.
        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer == null)
                continue;

            ulong targetClientId = deliverer.OwnerClientId;

            if (targetClientId == nm.LocalClientId)
                ShowLocal(data);                 // 검거자가 호스트 자신 — 로컬 표시
            else
                SendTo(targetClientId, data);    // 원격 검거자에게만 전송
        }
    }

    private void SendTo(ulong clientId, VerdictFeedbackData data)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        FixedString64Bytes name = default;
        name.CopyFromTruncated(data.CitizenName ?? string.Empty);

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe((byte)data.Verdict);
        writer.WriteValueSafe(data.Reward);
        writer.WriteValueSafe(name);
        nm.CustomMessagingManager.SendNamedMessage(k_messageName, clientId, writer, NetworkDelivery.Reliable);
    }

    private void ReceiveVerdict(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        // 서버는 검거자가 자신일 때 SendTo가 아니라 ShowLocal로 직접 띄우므로 자기 메시지를 받지 않는다.
        // (단일 전송이라 브로드캐스트 되받음도 없음) — 방어적 가드로만 남겨 둔다.
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out byte verdictByte);
        reader.ReadValueSafe(out int reward);
        reader.ReadValueSafe(out FixedString64Bytes name);

        ShowLocal(new VerdictFeedbackData
        {
            Verdict = (ArrestVerdict)verdictByte,
            Reward = reward,
            CitizenName = name.ToString(),
        });
    }

    private static void ShowLocal(VerdictFeedbackData data)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out VerdictBanner banner))
            banner.Show(data);
        else
            Debug.LogWarning("ArrestVerdictFeedback: 판정 배너(VerdictBanner)를 찾지 못해 표시하지 못했다");
    }
}
