using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 남은 치장 뽑기 토큰 수를 표시한다(자판기 라벨·상점 HUD·커스터마이징 창 공용).
/// </summary>
public class CosmeticTokenView : MonoBehaviour
{
    [Tooltip("남은 토큰을 적을 라벨")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 문구 — {0}에 남은 토큰 수가 들어간다")]
    [SerializeField]
    private LocalizedString m_format;

    private void OnEnable()
    {
        if (m_format.IsEmpty)
        {
            Debug.LogWarning($"[{nameof(CosmeticTokenView)}] 표시 문구 키가 연결되지 않았습니다 (#818 D)", this);
            return;
        }

        m_format.Arguments = new object[] { CosmeticInventory.Tokens };
        m_format.StringChanged += SetText;
        CosmeticInventory.OnTokensChanged += Refresh;
    }

    private void OnDisable()
    {
        if (m_format.IsEmpty)
            return;

        m_format.StringChanged -= SetText;
        CosmeticInventory.OnTokensChanged -= Refresh;
    }

    private void Refresh()
    {
        m_format.Arguments = new object[] { CosmeticInventory.Tokens };
        m_format.RefreshString();
    }

    private void SetText(string value)
    {
        if (m_label != null)
            m_label.text = value;
    }
}
