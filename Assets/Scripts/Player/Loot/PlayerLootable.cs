using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 털리는 쪽 — 약탈 대상으로서의 플레이어. (#487)
///
/// 약탈은 두 역할로 나뉘고, 이쪽은 <b>당하는 쪽의 얼굴</b>이다: 지금 털릴 수 있는 상태인가,
/// 무엇을 들고 있고 지갑은 어디인가, 그리고 <b>당했을 때 본인에게 알리는 것</b>. 실제로 무엇을
/// 가져갈지 정하고 옮기는 것은 행위 주체인 <see cref="PlayerLooter"/>다.
///
/// 소매치기(<see cref="Pickpocket"/>, #303)가 세운 축과 같다 — 피해자 쪽에서는 목록만 내주고,
/// 어느 것을 채는지는 채는 쪽이 정한다. 피해자에게 탈취 로직을 두면 "털린다"가 소지품 관리의
/// 일부가 되어 버린다.
///
/// <b><see cref="PlayerCarrier"/>처럼 두 역할을 한 컴포넌트에 합치지 않은 이유</b>는 약탈에
/// 짝 상태가 없기 때문이다. 운반은 끄는 쪽과 끌려가는 쪽이 서로를 가리키며 "끌면서 동시에
/// 끌려가는" 조합을 막아야 해서 한 몸이어야 하지만, 약탈은 서버에 세션을 두지 않고 요청마다
/// 처음부터 검증한다(<see cref="PlayerLooter"/>) — 공유할 상태가 아예 없다.
///
/// 알림이 <b>당한 본인에게</b> 가야 해서 NetworkBehaviour다. 다만 <see cref="OwnerFeedback"/>의
/// <c>SendTo.Owner</c>는 <b>쓸 수 없다</b> — 쓰러진 몸은 오너가 서버로 옮겨져 있다(#763/#865).
/// 대상을 직접 지정하는 이유가 그것이다(아래 주석).
/// </summary>
public class PlayerLootable : NetworkBehaviour
{
    private PlayerIncapacitation m_incapacitation;
    private PlayerLoadout m_loadout;
    private PlayerWallet m_wallet;
    private PlayerTheftView m_theftView; // 소매치기(#303)와 같은 도난 알림을 재사용한다

    /// <summary>
    /// 지금 이 몸을 털 수 있는가 — 다운(유예)·기능 정지(Die) 둘 다(#725). 전 피어에서 같은 답이 나온다.
    /// 테이저 기절(<c>Stun</c>)은 제외한다: 스스로 일어나는 상태까지 털 수 있으면 테이저가 최고의
    /// 강도 도구가 된다.
    /// </summary>
    public bool CanBeLooted => m_incapacitation != null && m_incapacitation.IsOutOfAction;

    /// <summary>이 몸의 소지품 — 약탈자가 목록을 읽고(표시) 서버가 이전 대상을 확인한다.</summary>
    internal PlayerLoadout Loadout => m_loadout;

    /// <summary>이 몸의 개인 자금 — 약탈자 서버 경로가 전액 이전의 출발점으로 쓴다.</summary>
    internal PlayerWallet Wallet => m_wallet;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_wallet = GetComponent<PlayerWallet>();
        m_theftView = GetComponent<PlayerTheftView>();
    }

    // ---- 피해 알림 (서버가 약탈자 경로에서 호출 → <b>몸의 진짜 주인</b>에게만 간다) ----
    //
    // ⚠ <b>OwnerFeedback(SendTo.Owner)을 쓰지 않는다.</b> 쓰러져 있는 동안 이 오브젝트의 오너는
    // 서버다(PlayerIncapacitation.ApplyDeathOwnership — #763은 Die, #865는 Down까지). 그러면
    // 서버가 스스로에게 보내는 것으로 끝나 털린 본인은 아무것도 못 받는다(#820 함정 1).
    // <b>유예 60초가 주된 약탈 창</b>이므로 여기서는 BodyOwnerClientId를 직접 지정한다.
    //
    // OwnerFeedback.NotifyOwner는 <b>고치지 말 것</b> — 아이템에 붙은 컴포넌트에서는 사망 중에도
    // 소유권이 그대로라 SendTo.Owner가 정확하다(#820이 자가 부활 채널링을 아이템에 둔 근거).

    /// <summary>소지품을 뺏겼다 — 본인에게만 알린다. 서버 전용.</summary>
    internal void ServerNotifyRobbedItem() =>
        ServerNotifyVictim("[약탈] 소지품을 빼앗겼다", stolenToast: true);

    /// <summary>
    /// 개인 자금을 뺏겼다 — 본인에게만 알린다. 서버 전용.
    /// 잔액 표시 UI가 없어 지금은 로그뿐이다. 금액을 인자로 받아 두는 이유는 표시가 생기면
    /// 여기만 바꾸면 되기 때문이다.
    /// </summary>
    internal void ServerNotifyRobbedFunds(int amount) =>
        ServerNotifyVictim($"[약탈] 개인 자금을 빼앗겼다 — {amount}", stolenToast: false);

    // 토스트는 소매치기와 같은 채널을 쓴다 (#303) — 자금은 표시가 없어 로그만 간다.
    private void ServerNotifyVictim(string message, bool stolenToast)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔 — OwnerFeedback과 같은 관례

        ulong victim =
            m_incapacitation != null ? m_incapacitation.BodyOwnerClientId : OwnerClientId;

        // 오프라인이거나 몸의 주인이 호스트 자신이면 RPC를 돌 이유가 없다.
        if (!IsSpawned || !IsServer || victim == NetworkManager.ServerClientId)
        {
            if (stolenToast)
                m_theftView?.ShowStolenLocal();
            return;
        }

        VictimNotifyRpc(message, stolenToast, RpcTarget.Single(victim, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void VictimNotifyRpc(string message, bool stolenToast, RpcParams rpcParams)
    {
        Debug.Log(message);
        if (stolenToast)
            m_theftView?.ShowStolenLocal();
    }
}
