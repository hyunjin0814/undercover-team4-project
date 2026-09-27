using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public struct VerdictFeedbackData
{
    public ArrestVerdict Verdict;
    public string CitizenName;
    public int Reward;
}

/// <summary>
/// 검거 판정 결과 배너를 검거한 플레이어 화면에만 띄운다.
/// 오프라인·호스트는 로컬로, 원격 검거자에게는 네임드 메시지로 보낸다.
/// </summary>
public class ArrestVerdictFeedback : MonoBehaviour
{
    private const string k_messageName = "ArrestVerdict";
    private const int k_writerSize = 128;

    private ArrestJudge Judge => App.Game.ArrestJudge;

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
        m_message = new NamedMessageSubscription(k_messageName, ReceiveVerdict);
        m_message.Attach();
    }

    private void OnDestroy() => m_message?.Detach();

    private void HandleArrestJudged(ArrestResult result)
    {
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

        if (nm == null || !nm.IsListening)
        {
            ShowLocal(data);
            return;
        }

        if (!nm.IsServer)
            return;

        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer == null)
                continue;

            ulong targetClientId = deliverer.OwnerClientId;

            if (targetClientId == nm.LocalClientId)
                ShowLocal(data);
            else
                SendTo(targetClientId, data);
        }
    }

    private void SendTo(ulong clientId, VerdictFeedbackData data)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        FixedString64Bytes name = data.CitizenName.ToFixed64();

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
