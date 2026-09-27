using UnityEngine;

/// <summary>
/// 지정한 씬에서만 m_content를 켜는 스위치. 씬 전환을 구독해 갱신한다.
/// </summary>
public class SceneScopedVisibility : MonoBehaviour
{
    [Tooltip("이 씬일 때만 켠다")]
    [SerializeField]
    private EScene m_visibleIn = EScene.Shop;

    [Tooltip("껐다 켤 대상 — 비워 두면 아무것도 하지 않는다")]
    [SerializeField]
    private GameObject m_content;

    private void Start()
    {
        if (m_content == null)
        {
            Debug.LogWarning($"[{nameof(SceneScopedVisibility)}] 껐다 켤 대상이 연결되지 않았습니다 (#850)", this);
            enabled = false;
            return;
        }

        App.OnSceneLoaded += Apply;
        Apply(App.CurrentScene);
    }

    private void OnDestroy() => App.OnSceneLoaded -= Apply;

    private void Apply(EScene scene)
    {
        if (m_content != null)
            m_content.SetActive(scene == m_visibleIn);
    }
}
