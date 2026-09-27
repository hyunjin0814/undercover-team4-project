using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 조건이 성립하는 동안 화면 중앙 아래에 떠 있는 안내. App.UI.Prompt로 접근하며, 호출부가 Hide로 지워야 한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class PromptView : LocalizedMessageView
{
    /// <summary>안내를 띄운다 — 지울 때까지 남는다. 이미 떠 있으면 덮어쓴다.</summary>
    public void Show(LocalizedString message) => ShowMessage(message);
}
