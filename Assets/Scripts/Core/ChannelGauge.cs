using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 채널링 게이지와 루프음을 행동을 시작한 오너 화면에만 띄우는 능력 컴포넌트.
/// 호스트 오너·오프라인은 로컬로, 원격 오너에게는 Owner RPC로 전달한다.
/// </summary>
public class ChannelGauge : NetworkBehaviour
{
    /// <summary>채널링 게이지 표시 — 서버·오프라인은 로컬, 원격 오너에겐 RPC.</summary>
    public void Begin(float seconds, EAudioClip loopSound) => Begin(seconds, 0f, loopSound);

    /// <summary>이미 elapsed초 진행된 채널링 게이지와 루프음을 이어서 표시한다.</summary>
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

    /// <summary>RPC 없이 오너 로컬에서 게이지를 즉시 숨긴다.</summary>
    public void HideLocal() => Hide();

    [Rpc(SendTo.Owner)]
    private void BeginRpc(float seconds, float elapsed, EAudioClip sound) =>
        Show(seconds, elapsed, sound);

    [Rpc(SendTo.Owner)]
    private void EndRpc() => Hide();

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
