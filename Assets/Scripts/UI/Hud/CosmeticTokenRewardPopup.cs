using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점 진입 시 대기 중인 치장 토큰 지급 축하 팝업을 잠깐 띄운다.
/// </summary>
public class CosmeticTokenRewardPopup : MonoBehaviour
{
    [Tooltip("띄웠다 지울 묶음 — 코인 이미지와 문구가 여기 들어간다")]
    [SerializeField]
    private CanvasGroup m_group;

    [Tooltip("튀어나오는 연출을 걸 대상 — 보통 m_group과 같은 오브젝트")]
    [SerializeField]
    private RectTransform m_pop;

    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 문구 — 개수는 넣지 않는다. 라운드당 1개뿐이다")]
    [SerializeField]
    private LocalizedString m_format;

    [Tooltip("떠 있는 시간(초)")]
    [SerializeField]
    private float m_holdSeconds = 2.5f;

    private const float k_popSeconds = 0.35f;
    private const float k_fadeOutSeconds = 0.4f;
    private const float k_overshoot = 1.12f;

    private CancellationTokenSource m_playCts;

    private void Start()
    {
        Hide();

        App.OnSceneLoaded += HandleSceneLoaded;
        HandleSceneLoaded(App.CurrentScene);
    }

    private void OnDestroy()
    {
        App.OnSceneLoaded -= HandleSceneLoaded;
        Cancel();
    }

    private void HandleSceneLoaded(EScene scene)
    {
        if (scene != EScene.Shop)
        {
            Cancel();
            Hide();
            return;
        }

        int claimed = CosmeticInventory.ClaimRewardNotice();
        if (claimed <= 0)
            return;

        Cancel();
        m_playCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        PlayAsync(m_playCts.Token).Forget();
    }

    private async UniTaskVoid PlayAsync(CancellationToken ct)
    {
        SetText();

        if (m_group != null)
            m_group.alpha = 1f;

        await ScaleAsync(0.6f, k_overshoot, k_popSeconds * 0.6f, ct);
        await ScaleAsync(k_overshoot, 1f, k_popSeconds * 0.4f, ct);

        await UniTask.Delay(
            System.TimeSpan.FromSeconds(m_holdSeconds),
            ignoreTimeScale: true,
            cancellationToken: ct
        );

        await FadeOutAsync(ct);
        Hide();
    }

    private async UniTask ScaleAsync(float from, float to, float seconds, CancellationToken ct)
    {
        if (m_pop == null || seconds <= 0f)
            return;

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            m_pop.localScale = Vector3.one * Mathf.Lerp(from, to, elapsed / seconds);
            await UniTask.Yield(PlayerLoopTiming.Update, ct);
            elapsed += Time.unscaledDeltaTime;
        }

        m_pop.localScale = Vector3.one * to;
    }

    private async UniTask FadeOutAsync(CancellationToken ct)
    {
        if (m_group == null)
            return;

        float elapsed = 0f;
        while (elapsed < k_fadeOutSeconds)
        {
            m_group.alpha = 1f - (elapsed / k_fadeOutSeconds);
            await UniTask.Yield(PlayerLoopTiming.Update, ct);
            elapsed += Time.unscaledDeltaTime;
        }
    }

    private void SetText()
    {
        if (m_label == null || m_format.IsEmpty)
            return;

        m_label.text = m_format.GetLocalizedString();
    }

    private void Cancel()
    {
        if (m_playCts == null)
            return;

        m_playCts.Cancel();
        m_playCts.Dispose();
        m_playCts = null;
    }

    private void Hide()
    {
        if (m_group != null)
            m_group.alpha = 0f;
    }
}
