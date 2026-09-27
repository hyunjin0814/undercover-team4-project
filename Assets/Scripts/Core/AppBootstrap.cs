using UnityEngine;

/// <summary>
/// 상주 매니저 루트 — 자신을 DontDestroyOnLoad로 올리고, 이미 상주 인스턴스가 있으면 즉시 파괴한다.
/// 자식 매니저 Awake보다 먼저 실행돼 중복 사본이 App에 등록되지 않게 한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.Bootstrap)]
public class AppBootstrap : MonoBehaviour
{
    private const int k_vSyncCount = 1;

    private static AppBootstrap s_instance;

    private void Awake()
    {
        if (s_instance != null)
        {
            gameObject.SetActive(false);
            Destroy(gameObject);
            return;
        }

        s_instance = this;
        DontDestroyOnLoad(gameObject);
        ApplyDisplaySettings();
    }

    /// <summary>빌드에서 수직동기화를 켜 티어링을 막는다.</summary>
    private static void ApplyDisplaySettings()
    {
        QualitySettings.vSyncCount = k_vSyncCount;

        Application.targetFrameRate = -1;

        Debug.Log(
            $"[AppBootstrap] 표시 설정 — vSync={QualitySettings.vSyncCount}"
                + $" 상한={Application.targetFrameRate}"
                + $" 주사율={Screen.currentResolution.refreshRateRatio.value:F1}Hz"
                + $" 모드={Screen.fullScreenMode}"
        );
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => s_instance = null;
}
