using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>라운드 정산 화면에 표시할 데이터 묶음 — 서버가 종료 시점에 스냅샷으로 채운다. (#107)</summary>
public struct SettlementData
{
    public RoundResult Result;      // 라운드 결과(성공/실패)
    public RoundEndReason Reason;   // 종료 사유(할당량 달성/제한시간 초과/전원 다운)
    public int FundBalance;         // 팀 자금 잔액
    public int FundDelta;           // 이번 라운드 자금 증감(현재-시작) = 팀이 실제로 챙긴 몫 (#340/#395)
    public int GrossEarned;         // 종료 시 정산 원장의 현상금 합 = 할당량 차감 전 총 수익 (#395)
    public int TargetFund;          // 이번 라운드 목표 금액(할당량) — 총 수익에서 이만큼 떼고 남는 게 팀 몫 (#395)
    // 아래 둘은 <b>유치장 점유가 아니라 정산 원장(JailZone.m_records) 기준</b>이다 (#571) —
    // 죽은 대상도 계상되므로(JailZone.RecordDeceased) "유치장에 앉아 있는 수"보다 클 수 있다.
    public int CriminalCount;       // 종료 시 계상된 진범 수 (#340/#571)
    public int MisdemeanorCount;    // 종료 시 계상된 경범죄자(난동꾼·위조범) 수 (#340/#571)
    public string TopOffenderName;  // 이번 판 최다 오검거 플레이어 이름 (없으면 빈 문자열)
    public int TopOffenderCount;    // 그 플레이어의 오검거 횟수 (0이면 오검거 없음)
}

/// <summary>
/// 라운드 정산 화면 제어 (#107, GDD 3-2) — 라운드 종료 시 결과·팀 자금 증감·이번 판 최다 오검거(코믹 스탯)를
/// 모아 전 클라이언트의 정산 패널(SettlementPanel)에 띄운다.
///
/// 전파 흐름은 RoundEndFeedback(#210)과 동일 — 라운드 진행이 서버 권위이므로(#56):
///  · 서버·오프라인 — RoundManager.OnRoundEnded를 직접 구독해 데이터를 모으고, 로컬 패널을 띄운 뒤
///    네트워크 세션이면 커스텀 네임드 메시지로 전 클라이언트에 같은 데이터를 보낸다.
///  · 클라이언트 — 네임드 메시지를 수신해 동일한 패널을 띄운다.
/// 팀 자금은 TeamFund NetworkVariable로 이미 동기화되지만, 결과·오검거 개인집계는 서버 전용이라
/// 종료 시점 스냅샷을 한 번에 묶어 보낸다 — 늦게 접속한 클라와 무관하게 그 순간 값을 그대로 전달한다.
///
/// NetworkBehaviour가 아니므로(RoundEndFeedback와 동일) 씬 네트워크 구성(NetworkObject·프리팹 등록)을 건드리지 않는다.
/// </summary>
public class SettlementController : MonoBehaviour
{
    private const string k_messageName = "RoundSettlement";
    private const int k_writerSize = 128; // byte*2 + int*7 + FixedString64(최대 66) = 96 < 128
    private const int k_personalSharePercent = 10; // 인계자 개인 몫 — 귀속 현상금의 % (#484)

    private RoundManager Round => App.Game.Round;
    private WrongfulArrestPenalty Penalty => App.Game.WrongfulArrestPenalty;
    private TeamFund TeamFund => App.Game.TeamFund;

    private bool m_handlerRegistered;

    // 이번 라운드 시작 시점의 팀 자금 — 정산 증감(현재-시작) 기준. TeamFund가 세션 지속형이라(#214)
    // 세션 초기값이 아니라 "이 라운드가 시작될 때" 잔액을 스냅샷해야 이번 라운드 증감이 나온다.
    private int m_roundStartFund;

    private void OnEnable()
    {
        // 라운드 종료·시작은 서버·오프라인에서만 발행된다 — 권위 피어가 이 훅들로 자금 스냅샷·정산을 처리한다.
        if (Round != null)
        {
            Round.OnRoundStarted += HandleRoundStarted;
            Round.OnRoundEnded += HandleRoundEnded;
        }
    }

    private void OnDisable()
    {
        if (Round != null)
        {
            Round.OnRoundStarted -= HandleRoundStarted;
            Round.OnRoundEnded -= HandleRoundEnded;
        }
    }

    // 라운드 시작(서버·오프라인) 시점 자금을 기록해 둔다 — 종료 시 증감 계산 기준.
    private void HandleRoundStarted()
    {
        m_roundStartFund = TeamFund != null ? TeamFund.Balance : 0;
    }

    private void Start()
    {
        // 클라이언트 수신 등록 — CustomMessagingManager는 NGO가 시작된 뒤에만 존재한다. (RoundEndFeedback와 동일 패턴)
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

        nm.CustomMessagingManager.RegisterNamedMessageHandler(k_messageName, ReceiveSettlement);
        m_handlerRegistered = true;
    }

    // 서버·오프라인: 종료 후 데이터를 모아 로컬 표시 + 세션이면 전 클라 전파.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        GatherAndShowAsync(result, reason).Forget();
    }

    // 라운드 종료가 검거 판정(ArrestJudge.OnArrestJudged)과 같은 프레임에 발생하면(할당량 채운 그 검거),
    // 그 검거의 보상이 팀 자금에 아직 반영되기 전일 수 있다 — 같은 이벤트의 구독자 호출 순서 경쟁 때문.
    // 한 프레임 미뤄 그 디스패치의 모든 구독자(TeamFund 보상 가산 등)가 끝난 뒤의 확정 자금을 읽는다.
    // (라운드 종료 후 리셋까지 여유가 있어 한 프레임 지연은 화면상 보이지 않는다)
    private async UniTaskVoid GatherAndShowAsync(RoundResult result, RoundEndReason reason)
    {
        try
        {
            await UniTask.NextFrame(destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 매니저 파괴 — 정리 중이므로 표시하지 않는다
        }

        SettlementData data = BuildData(result, reason);
        ShowLocal(data);
        Broadcast(data);
    }

    // 결과·종료 사유·자금 증감·최다 오검거를 모은다 (서버·오프라인 권위 데이터).
    // #340: 여기서(서버·오프라인 전용 경로) 라운드 종료 시점의 유치장 점유로 보상을 정산해 자금에 1회
    // 반영한 뒤 잔액을 스냅샷한다 — 판정 즉시 지급을 대체한다. 탈옥해 유치장에 없는 대상은 계상되지 않는다.
    private SettlementData BuildData(RoundResult result, RoundEndReason reason)
    {
        int criminals = 0;
        int misdemeanors = 0;
        int gross = 0;
        JailZone jail = App.Game.Jail;
        if (jail != null)
            (criminals, misdemeanors, gross) = jail.TallySettlement();

        // 할당량은 경찰서에 납부하는 몫이다 — 총 수익에서 목표 금액을 떼고 남은 초과분만 팀이 챙긴다 (#395).
        // 목표를 못 채웠으면 초과분이 없으므로 0원이다(음수를 자금에서 깎지는 않는다 — GDD 9-2 마이너스 방지).
        int target = Round != null ? Round.TargetFund : 0;
        int payout = Mathf.Max(0, gross - target);
        if (TeamFund != null)
            TeamFund.AddSettlement(payout);

        // 개인 몫은 팀 정산액과 무관하게 별도 발생한다 (#484) — payout을 깎지 않는다
        if (jail != null)
            PayPersonalShares(jail);

        int balance = TeamFund != null ? TeamFund.Balance : 0;
        int delta = TeamFund != null ? balance - m_roundStartFund : 0;

        string topName = string.Empty;
        int topCount = 0;
        if (Penalty != null)
            FindTopOffender(Penalty.PerPlayerCounts, out topName, out topCount);

        return new SettlementData
        {
            Result = result,
            Reason = reason,
            FundBalance = balance,
            FundDelta = delta,
            GrossEarned = gross,
            TargetFund = target,
            CriminalCount = criminals,
            MisdemeanorCount = misdemeanors,
            TopOffenderName = topName,
            TopOffenderCount = topCount,
        };
    }

    // 인계자별 개인 자금 지급 — 귀속 현상금(JailZone)에 비율만 적용한다. 서버·오프라인 전용.
    private static void PayPersonalShares(JailZone jail)
    {
        foreach (KeyValuePair<ulong, int> pair in jail.TallyDelivererCredits())
        {
            int share = pair.Value * k_personalSharePercent / 100;
            if (share <= 0)
                continue;

            // 접속이 끊긴 인계자는 지갑이 없다 — 그 몫은 사라진다
            PlayerWallet wallet = PlayerWallet.FindByClientId(pair.Key);
            if (wallet != null)
                wallet.ServerAdd(share);
        }
    }

    // 개인 오검거 집계에서 최다자를 뽑아 clientId를 표시 이름으로 바꾼다. 동률이면 먼저 순회된 쪽.
    private static void FindTopOffender(
        IReadOnlyDictionary<ulong, int> counts,
        out string name,
        out int count
    )
    {
        name = string.Empty;
        count = 0;
        if (counts == null)
            return;

        ulong topClient = 0;
        foreach (KeyValuePair<ulong, int> pair in counts)
        {
            if (pair.Value <= count)
                continue;
            count = pair.Value;
            topClient = pair.Key;
        }

        if (count > 0)
            name = ResolvePlayerName(topClient);
    }

    // clientId → 동기화된 표시 이름. 접속이 끊겼거나 이름이 비었으면 "플레이어 N"으로 폴백.
    private static string ResolvePlayerName(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (
            nm != null
            && nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            && client.PlayerObject != null
        )
        {
            PlayerNameTag tag = client.PlayerObject.GetComponent<PlayerNameTag>();
            if (tag != null && !string.IsNullOrEmpty(tag.DisplayName))
                return tag.DisplayName;
        }
        return $"플레이어 {clientId}";
    }

    private void Broadcast(SettlementData data)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        FixedString64Bytes name = data.TopOffenderName.ToFixed64();

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe((byte)data.Result);
        writer.WriteValueSafe((byte)data.Reason);
        writer.WriteValueSafe(data.FundBalance);
        writer.WriteValueSafe(data.FundDelta);
        writer.WriteValueSafe(data.GrossEarned);
        writer.WriteValueSafe(data.TargetFund);
        writer.WriteValueSafe(data.CriminalCount);
        writer.WriteValueSafe(data.MisdemeanorCount);
        writer.WriteValueSafe(data.TopOffenderCount);
        writer.WriteValueSafe(name);
        nm.CustomMessagingManager.SendNamedMessageToAll(
            k_messageName,
            writer,
            NetworkDelivery.Reliable
        );
    }

    private void ReceiveSettlement(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        // 호스트는 자기 브로드캐스트를 되받을 수 있다 — 이미 ShowLocal로 띄웠으니 무시(중복 방지).
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out byte resultByte);
        reader.ReadValueSafe(out byte reasonByte);
        reader.ReadValueSafe(out int balance);
        reader.ReadValueSafe(out int delta);
        reader.ReadValueSafe(out int gross);
        reader.ReadValueSafe(out int target);
        reader.ReadValueSafe(out int criminals);
        reader.ReadValueSafe(out int misdemeanors);
        reader.ReadValueSafe(out int topCount);
        reader.ReadValueSafe(out FixedString64Bytes name);

        ShowLocal(
            new SettlementData
            {
                Result = (RoundResult)resultByte,
                Reason = (RoundEndReason)reasonByte,
                FundBalance = balance,
                FundDelta = delta,
                GrossEarned = gross,
                TargetFund = target,
                CriminalCount = criminals,
                MisdemeanorCount = misdemeanors,
                TopOffenderCount = topCount,
                TopOffenderName = name.ToString(),
            }
        );
    }

    private static void ShowLocal(SettlementData data)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SettlementPanel panel))
            panel.Show(data);
        else
            Debug.LogWarning("SettlementController: 정산 패널(SettlementPanel)을 찾지 못해 표시하지 못했다");
    }
}
