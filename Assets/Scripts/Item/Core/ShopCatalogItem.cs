using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 장비 카탈로그 아이템 — 사용하면 주문창(ShopBrowserPanel)을 연다. 상점 씬에서만 지급된다.
/// </summary>
public class ShopCatalogItem : ItemBase
{
    public override LocalizedString HeldPromptLabel() => InteractPrompts.CatalogOpen;

    public override void Use(GameObject target)
    {
        if (!IsOwner)
            return;

        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out ShopBrowserPanel panel))
        {
            Debug.LogWarning(
                "카탈로그: 주문창을 찾지 못했다 — ShopBrowserPanel은 상점 씬에만 있다",
                this
            );
            return;
        }

        panel.Open(Holder);
    }
}
