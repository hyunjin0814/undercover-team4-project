using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 세션 화면에 상시 노출되는 닉네임 편집 표시부.
/// </summary>
public class NicknameView : MonoBehaviour
{
    [SerializeField]
    private TMP_InputField m_nicknameInput;

    [SerializeField]
    private Button m_applyButton;

    [SerializeField]
    private TMP_Text m_statusText;

    private bool m_isApplying;

    private LocalizedMessage m_status;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_applyButton.onClick.AddListener(HandleApplyClicked);

        m_nicknameInput.characterLimit = NicknameRules.MaxLength;

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
            Auth.OnNicknameChanged += Refresh;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh();
    }

    private void OnDisable()
    {
        m_applyButton.onClick.RemoveListener(HandleApplyClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnNicknameChanged -= Refresh;
        }

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RenderStatus();
        Refresh();
    }

    private void SetStatus(in LocalizedMessage message)
    {
        m_status = message;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (m_statusText != null)
            m_statusText.text = m_status.Resolve();
    }

    private void HandleApplyClicked() => ApplyAsync().Forget();

    private async UniTaskVoid ApplyAsync()
    {
        if (m_isApplying || Auth == null)
            return;

        m_isApplying = true;
        string attempted = m_nicknameInput.text;
        bool failed = false;
        try
        {
            await Auth.SetPlayerNameAsync(attempted);
            SetStatus(LocalizedMessage.None);
        }
        catch (LocalizedMessageException ex)
        {
            failed = true;
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            failed = true;
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isApplying = false;
            Refresh();
            if (failed)
                m_nicknameInput.text = attempted;
        }
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;

        bool canEdit = signedIn && !Auth.IsNetworkConnected && !m_isApplying;
        m_nicknameInput.interactable = canEdit;
        m_applyButton.interactable = canEdit;

        if (!m_nicknameInput.isFocused)
            m_nicknameInput.text = signedIn ? Auth.Nickname : string.Empty;
    }
}
