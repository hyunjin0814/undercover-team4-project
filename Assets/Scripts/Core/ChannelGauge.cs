using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 채널링 게이지와 루프음을 <b>오너 화면에</b> 띄우는 능력 컴포넌트 (#184).
/// 판정은 서버가 하되 보여줄 곳은 그 행동을 시작한 오너뿐이므로, 호스트 오너·오프라인은 로컬로
/// 즉시 구동하고 원격 오너에게는 SendTo.Owner RPC로 전달한다.
///
/// 채널링이 필요한 프리팹에만 부착한다 — 소비자는 <c>[RequireComponent(typeof(ChannelGauge))]</c>로
/// 선언한다. 상속에서 합성으로 옮긴 경위는 docs/channeled-interaction-split.md 참고.
/// (데디케이티드 서버 등 HUD가 없는 환경에선 App.UI.Gauge가 null이라 무동작 — 안전)
/// </summary>
public class ChannelGauge : NetworkBehaviour
{
    /// <summary>채널링 게이지 표시 — 서버·오프라인은 로컬, 원격 오너에겐 RPC.</summary>
    public void Begin(float seconds, EAudioClip loopSound) => Begin(seconds, 0f, loopSound);

    /// <summary>
    /// 이미 진행 중인 것을 중간부터 이어 표시한다 — elapsed초 지난 상태로 시작. (#455)
    /// 전체 시간과 경과 시간을 함께 넘기는 이유는 ChannelingGaugeUI.Show(seconds, elapsed) 문서에 있다.
    /// </summary>
    public void Begin(float seconds, float elapsed, EAudioClip loopSound)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            BeginRpc(seconds, elapsed, loopSound);
            return;
        }
        Show(seconds, elapsed, loopSound);
    }

    /// <summary>게이지 숨김 — 완료·취소·거리이탈 등 어떤 종료 경로에서도 반드시 호출.</summary>
    public void End()
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            EndRpc();
            return;
        }
        Hide();
    }

    /// <summary>
    /// RPC를 타지 않는 오너 로컬 전용 숨김 — 이미 오너에서 도는 경로용(PlayerLoadout.EquipSlot #455).
    /// 서버 판정을 기다리지 않고 즉시 지워야 반응이 또렷하다.
    /// </summary>
    public void HideLocal() => Hide();

    // 소리를 RPC 인자로 실어 보내는 이유 — 게이지와 소리가 같은 한 번의 결정에서 나와야 둘이 어긋날 여지가 없다.
    [Rpc(SendTo.Owner)]
    private void BeginRpc(float seconds, float elapsed, EAudioClip sound) =>
        Show(seconds, elapsed, sound);

    [Rpc(SendTo.Owner)]
    private void EndRpc() => Hide();

    // owner로 this를 넘긴다 — 게이지를 여럿이 공유하므로 참조 동일성으로 남의 게이지를 끄지 않게 한다. (#725)
    private void Show(float seconds, float elapsed, EAudioClip sound)
    {
        App.UI.Gauge?.Show(seconds, elapsed, this);
        App.Sound?.PlayLoop2D(sound);
    }

    private void Hide()
    {
        App.UI.Gauge?.Hide(this);
        App.Sound?.StopLoop2D();
    }
}
