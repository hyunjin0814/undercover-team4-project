using UnityEngine;

/// <summary>
/// 오검거 페널티 NPC 머리 위에 앵그리 마크를 표시한다.
/// 교차 쿼드 2장으로 어느 카메라에서든 보이며, 스프라이트는 런타임에 절차 생성한다.
/// </summary>
public class NpcPenaltyMark : MonoBehaviour
{
    private const float k_height = 2.35f;
    private const float k_baseScale = 0.55f;
    private const float k_pulseAmount = 0.12f;
    private const float k_pulseSpeed = 5f;

    private static Sprite s_sprite;

    /// <summary>앵그리 마크를 표시하거나 숨긴다. 처음 표시할 때 마크를 생성한다.</summary>
    public static void SetVisible(Component npc, bool visible)
    {
        if (npc == null)
            return;

        NpcPenaltyMark mark = npc.GetComponentInChildren<NpcPenaltyMark>(true);
        if (mark == null)
        {
            if (!visible)
                return;
            mark = Create(npc.transform);
        }

        mark.gameObject.SetActive(visible);
    }

    private static NpcPenaltyMark Create(Transform parent)
    {
        var root = new GameObject("PenaltyMark");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0f, k_height, 0f);
        root.transform.localScale = Vector3.one * k_baseScale;

        for (int i = 0; i < 2; i++)
        {
            var quad = new GameObject(i == 0 ? "Quad0" : "Quad90");
            quad.transform.SetParent(root.transform, false);
            quad.transform.localRotation = Quaternion.Euler(0f, i * 90f, 0f);
            quad.AddComponent<SpriteRenderer>().sprite = GetSprite();
        }

        return root.AddComponent<NpcPenaltyMark>();
    }

    private void Update()
    {
        float scale = k_baseScale * (1f + Mathf.Sin(Time.time * k_pulseSpeed) * k_pulseAmount);
        transform.localScale = new Vector3(scale, scale, scale);
    }

    private static Sprite GetSprite()
    {
        if (s_sprite != null)
            return s_sprite;

        const int size = 128;
        const float innerRadius = 30f;
        const float outerRadius = 44f;
        const float segmentHalfAngle = 33f;
        const float soft = 1.5f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        var red = new Color(0.92f, 0.18f, 0.14f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - (size - 1) * 0.5f;
                float dy = y - (size - 1) * 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                float offCenter = Mathf.Abs(Mathf.Repeat(angle, 90f) - 45f);

                float radial =
                    Mathf.InverseLerp(innerRadius - soft, innerRadius + soft, r)
                    * (1f - Mathf.InverseLerp(outerRadius - soft, outerRadius + soft, r));
                float angular =
                    1f - Mathf.InverseLerp(segmentHalfAngle - 2f, segmentHalfAngle + 2f, offCenter);

                float alpha = radial * angular;
                pixels[y * size + x] = new Color32(
                    (byte)(red.r * 255f),
                    (byte)(red.g * 255f),
                    (byte)(red.b * 255f),
                    (byte)(alpha * 255f)
                );
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true);

        s_sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return s_sprite;
    }
}
