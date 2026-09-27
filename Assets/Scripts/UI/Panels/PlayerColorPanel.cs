using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비·상점에서 자기 로봇 색과 치장을 고르는 커스터마이징 창 — 열고 닫기와 미리보기를 맡는다.
/// </summary>
public class PlayerColorPanel : PanelBase
{
    [Tooltip("내 로봇 전신 미리보기 — 비워 두면 미리보기 없이 팔레트만 보인다")]
    [SerializeField] private RawImage m_preview;

    [Tooltip("얼굴을 굽는 무대 — 로비 카드와 같은 것을 물린다")]
    [SerializeField] private LobbyPortraitStage m_portraitStage;

    [SerializeField] private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SetBlocked(true);
        RefreshPreview();
    }

    /// <summary>닫기 — ESC·닫기 버튼·씬 정리가 모두 여기로 모인다. 커서를 반드시 여기서 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return;

        base.ClosePanel();
        SetBlocked(false);
    }

    private void OnDisable()
    {
        HandleDisabled();
        SetBlocked(false);
    }

    protected override PlayerInputHandler BlockTarget => FindLocalInput();

    private static PlayerInputHandler FindLocalInput()
    {
        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        Unity.Netcode.NetworkObject player =
            manager != null && manager.IsListening ? manager.LocalClient.PlayerObject : null;

        return player != null ? player.GetComponent<PlayerInputHandler>() : null;
    }

    private void OnEnable()
    {
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
    }

    private void HandleDisabled()
    {
        CosmeticLoadout.OnPlayerColorChanged -= HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged -= HandleAccessoryChanged;
    }

    private void HandleColorChanged(EBodyPart _) => RefreshPreview();

    private void HandleAccessoryChanged(EAccessorySlot _) => RefreshPreview();

    private void RefreshPreview()
    {
        if (m_preview == null || m_portraitStage == null)
            return;

        m_preview.texture = m_portraitStage.BodyPreview;
    }
}
