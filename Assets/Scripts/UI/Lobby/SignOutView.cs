using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 타이틀 화면 하단의 로그아웃 버튼 뷰. 관문 통과 후에만 보이도록 SessionPanel 아래에 둔다.
/// </summary>
public class SignOutView : MonoBehaviour
{
    [Header("UI 참조")]
    [SerializeField]
    private Button m_signOutButton;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        Refresh();
    }

    private void OnDisable()
    {
        m_signOutButton.onClick.RemoveListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
        }

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => Refresh();

    private void HandleSignOutClicked()
    {
        if (Auth != null)
            Auth.SignOut();

        Refresh();
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;

        m_signOutButton.interactable = signedIn && !Auth.IsNetworkConnected;
    }
}
