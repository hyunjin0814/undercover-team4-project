using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 무채색 베이스 두상 위에 공개된 외형 축만 레이어로 얹어 그리는 그림 몽타주.
/// 겹침 순서는 자식 순서(베이스 → 머리 → 수염 → 모자 → 안경)다.
/// </summary>
public class MontagePortraitView : MonoBehaviour
{
    [Header("레이어 (뒤에서 앞 순서로 배치할 것)")]
    [SerializeField] private Image m_baseImage;
    [SerializeField] private Image m_hairImage;
    [SerializeField] private Image m_facialHairImage;
    [SerializeField] private Image m_headwearImage;
    [SerializeField] private Image m_eyewearImage;

    [Tooltip("공개되지 않은 색 축에 쓰는 표시. 색이 아니라 농도로 말해야 한다 — 무채색으로만 두면 은발(0.76,0.78,0.82)과 구분되지 않아 미공개가 실제 축 값 하나를 사칭하게 된다. 반투명이면 어느 색 값과도 겹치지 않는다")]
    [SerializeField] private Color m_unknownTint = new Color(0.72f, 0.72f, 0.74f, 0.35f);

    private readonly Dictionary<Image, Sprite> m_runtimeSprites = new Dictionary<Image, Sprite>();

    public void Bind(in AppearanceProfile profile, RevealedAxisSet revealedAxes, AppearanceDatabase database)
    {
        if (database == null || database.MontageBase == null)
        {
            Clear();
            return;
        }

        MontageClarityStep clarity = ClarityStepOf(database);

        BindBase(profile, revealedAxes, database, clarity);
        BindHair(profile, revealedAxes, database, clarity);
        BindPropAxis(m_facialHairImage, AppearanceAxis.FacialHair, profile, revealedAxes, database, clarity);
        BindPropAxis(m_headwearImage, AppearanceAxis.Headwear, profile, revealedAxes, database, clarity);
        BindPropAxis(m_eyewearImage, AppearanceAxis.Eyewear, profile, revealedAxes, database, clarity);
    }

    private void OnDestroy()
    {
        foreach (Sprite sprite in m_runtimeSprites.Values)
            DestroyRuntimeSprite(sprite);
        m_runtimeSprites.Clear();
    }

    private static MontageClarityStep ClarityStepOf(AppearanceDatabase database)
    {
        var noDegrade = new MontageClarityStep { PixelSize = int.MaxValue, Fade = 0f };
        if (database.ClarityTable == null)
            return noDegrade;

        RoundProgress progress = App.Game.RoundProgress;
        int round = progress != null ? progress.Current : RoundProgress.k_firstRound;
        return database.ClarityTable.GetStep(round, noDegrade);
    }

    /// <summary>베이스 두상을 그린다. 피부색이 미공개면 흰색으로 둔다.</summary>
    private void BindBase(
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        Color tint = revealedAxes.Contains(AppearanceAxis.SkinColor)
            ? TintOf(AppearanceAxis.SkinColor, profile, revealedAxes, database)
            : Color.white;

        SetLayer(m_baseImage, database.MontageBase, tint, clarity);
    }

    /// <summary>머리 레이어를 그린다. 스타일이 미공개면 형태 미상 머리를 깐다.</summary>
    private void BindHair(
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        Sprite sprite = revealedAxes.Contains(AppearanceAxis.HairStyle)
            ? database.GetOption(AppearanceAxis.HairStyle, profile.HairStyleIndex)?.MontageLayer
            : database.MontageUnknownHair;

        SetLayer(m_hairImage, sprite, TintOf(AppearanceAxis.HairColor, profile, revealedAxes, database), clarity);
    }

    private void BindPropAxis(
        Image image,
        AppearanceAxis axis,
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        if (!revealedAxes.Contains(axis))
        {
            SetLayer(image, null, Color.white, clarity);
            return;
        }

        SetLayer(image, database.GetOption(axis, profile.GetIndex(axis))?.MontageLayer, Color.white, clarity);
    }

    private Color TintOf(
        AppearanceAxis colorAxis,
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database
    )
    {
        if (!revealedAxes.Contains(colorAxis))
            return m_unknownTint;

        AppearanceDatabase.AppearanceOption option = database.GetOption(colorAxis, profile.GetIndex(colorAxis));
        return option != null ? option.Color : m_unknownTint;
    }

    private void Clear()
    {
        var noDegrade = default(MontageClarityStep);
        SetLayer(m_baseImage, null, Color.white, noDegrade);
        SetLayer(m_hairImage, null, Color.white, noDegrade);
        SetLayer(m_facialHairImage, null, Color.white, noDegrade);
        SetLayer(m_headwearImage, null, Color.white, noDegrade);
        SetLayer(m_eyewearImage, null, Color.white, noDegrade);
    }

    private void SetLayer(Image image, Sprite sprite, Color tint, in MontageClarityStep clarity)
    {
        if (image == null)
            return;

        Sprite degraded = Degrade(image, sprite, clarity);
        image.sprite = degraded;
        image.color = tint;
        image.enabled = degraded != null;
    }

    /// <summary>슬롯에 화질 저하 스프라이트를 새로 만들어 넣고, 이전 런타임 버전은 파괴한다(원본 자산은 건드리지 않는다).</summary>
    private Sprite Degrade(Image slot, Sprite source, in MontageClarityStep clarity)
    {
        if (m_runtimeSprites.TryGetValue(slot, out Sprite previous))
        {
            DestroyRuntimeSprite(previous);
            m_runtimeSprites.Remove(slot);
        }

        if (source == null)
            return null;

        int srcSize = source.texture.width;
        if (clarity.PixelSize >= srcSize && clarity.Fade <= 0f)
            return source;

        Color[] degraded = MontageDegrader.Apply(source.texture.GetPixels(), srcSize, clarity);
        var texture = new Texture2D(srcSize, srcSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        texture.SetPixels(degraded);
        texture.Apply();

        Sprite result = Sprite.Create(texture, new Rect(0, 0, srcSize, srcSize), new Vector2(0.5f, 0.5f), source.pixelsPerUnit);
        m_runtimeSprites[slot] = result;
        return result;
    }

    private static void DestroyRuntimeSprite(Sprite sprite)
    {
        if (sprite == null)
            return;

        Destroy(sprite.texture);
        Destroy(sprite);
    }
}
