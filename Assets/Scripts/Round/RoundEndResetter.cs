using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 라운드 종료 시 정산을 보여준 뒤 세션을 유지한 채 다음 씬으로 넘긴다 — 성공은 상점, 실패는 로비(자금 리셋).
/// 전원이 정산을 확인하면 대기 상한 전에 넘어간다. 서버(또는 오프라인)에서만 동작한다.
/// </summary>
public class RoundEndResetter : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;
    private TeamFund TeamFund => App.Game.TeamFund;
    private ShopPurchases ShopPurchases => App.Game.ShopPurchases;
    private RoundProgress RoundProgress => App.Game.RoundProgress;
    private SettlementConfirmGate SettlementGate => App.Game.SettlementGate;

    private bool AllConfirmed => SettlementGate != null && SettlementGate.AllConfirmed;

    [Header("정산 표시")]
    [Tooltip(
        "라운드 종료 후 상점(허브) 복귀까지의 대기 상한(초) — 정산 텍스트 지연+카운트다운과 맞춘다. 0이면 즉시"
    )]
    [SerializeField]
    private float m_resetDelaySeconds = 11.5f;

    private bool m_ending;

    private void OnEnable()
    {
        if (Round != null)
            Round.OnRoundEnded += HandleRoundEnded;
    }

    private void OnDisable()
    {
        if (Round != null)
            Round.OnRoundEnded -= HandleRoundEnded;
    }

    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        if (m_ending)
            return;
        m_ending = true;
        EndRoundAsync(result).Forget();
    }

    private async UniTask WaitForSettlementAsync()
    {
        float deadline = Time.realtimeSinceStartup + m_resetDelaySeconds;

        await UniTask.WaitUntil(
            () => Time.realtimeSinceStartup >= deadline || AllConfirmed,
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );

        if (AllConfirmed)
            Debug.Log($"[RoundEndResetter] 전원 정산 확인({SettlementGate.ConfirmedCount}명) — 대기 상한 전에 복귀한다");
    }

    private async UniTaskVoid EndRoundAsync(RoundResult result)
    {
        if (m_resetDelaySeconds > 0f)
            await WaitForSettlementAsync();

        if (App.CurrentScene == EScene.None)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsListening || nm.IsClient || nm.IsServer))
                nm.Shutdown();
            Scene active = SceneManager.GetActiveScene();
            Debug.Log($"[RoundEndResetter] 라운드 종료 - '{active.name}' 재로드(테스트 씬 폴백)");
            SceneManager.LoadScene(active.name);
            return;
        }

        if (result != RoundResult.Success)
        {
            if (TeamFund != null)
                TeamFund.ResetToStarting();
            else
                Debug.LogWarning("[RoundEndResetter] TeamFund를 찾지 못해 자금을 초기화하지 못했다", this);

            ShopPurchases?.Clear();

            RoundProgress?.ResetToFirst();

            SaveService.DeleteAsync().Forget();

            Debug.Log("[RoundEndResetter] 라운드 실패 — 세션 유지한 채 로비 복귀 (새 판 시작)");
            App.LoadScene(EScene.Lobby);
            return;
        }

        RoundProgress?.Advance();

        SaveService.SaveAsync().Forget();

        Debug.Log("[RoundEndResetter] 라운드 성공 — 세션 유지한 채 상점(허브) 복귀");
        App.LoadScene(EScene.Shop);
    }
}
