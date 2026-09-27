using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// P를 누르면 치장 뽑기 토큰을 로컬 계정에 더하는 에디터 전용 개발 단축키.
/// </summary>
public class CosmeticTokenDevHotkey : MonoBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Tooltip("한 번 누를 때 받는 토큰 수")]
    [SerializeField] private int m_amount = 1;

    private void Update()
    {
        if (!m_enabled)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[Key.P].wasPressedThisFrame)
            return;

        CosmeticInventory.AddTokens(m_amount);
        Debug.Log($"[치장/개발용] P — 뽑기 토큰 +{m_amount} (보유 {CosmeticInventory.Tokens}개)", this);
    }
#endif
}
