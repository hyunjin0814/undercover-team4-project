using EPOOutline;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 오너 전용 조준 피드백 — 실제로 할 수 있는 행동이 있는 대상에만 윤곽선과 안내 문구를 표시한다.
/// 매 프레임 재평가하며, Outlinable은 첫 조준 시(씬 배치물은 로딩 중) 부착한다.
/// </summary>
[RequireComponent(typeof(PlayerInteractor))]
public class InteractionFeedback : NetworkBehaviour
{
    [Header("HUD")]
    [Tooltip("씬에 HUD가 없으면 오너 스폰 시 이 프리팹을 생성한다")]
    [SerializeField]
    private GameObject m_hudPrefab;

    [Header("아웃라인 (EPO)")]
    [Tooltip(
        "공용 색 팔레트 — E 상호작용 대상의 윤곽선에 Highlight를 쓴다 (#951). "
        + "아이템 사용 대상은 ItemBase.TargetOutlineColor로 아이템별 색을 낸다"
    )]
    [SerializeField]
    private UiColorPalette m_palette;

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_input;

    private PlayerEscorter m_escorter;
    private PlayerCarrier m_carrier;

    private PlayerIncapacitation m_incapacitation;
    private Outlinable m_currentOutlinable;

    private const int k_jailOutlineLayer = 1;

    private Outliner m_outliner;

    private PlayerTerminalFocus m_terminalFocus;

    private bool IsAimSuppressed =>
        m_incapacitation != null && m_incapacitation.IsIncapacitated
        || m_input != null && m_input.IsSuspended;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        EnsureHud();
        App.OnSceneLoaded += HandleSceneLoaded;

        m_interactor = GetComponent<PlayerInteractor>();
        m_itemUser = GetComponent<PlayerItemUser>();
        m_input = GetComponent<PlayerInputHandler>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_carrier = GetComponent<PlayerCarrier>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_outliner = GetComponentInChildren<Outliner>(true);
        m_terminalFocus = GetComponent<PlayerTerminalFocus>();
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        App.OnSceneLoaded -= HandleSceneLoaded;
        SetOutlined(null, Color.clear);
        App.UI.InteractPrompt?.HidePrompt();
        App.UI.Crosshair?.SetVisible(true);
    }

    private void HandleSceneLoaded(EScene scene) => EnsureHud();

    private void EnsureHud()
    {
        if (App.CurrentScene == EScene.Title || App.CurrentScene == EScene.Lobby)
            return;
        if (App.UI.Crosshair == null && m_hudPrefab != null)
            Instantiate(m_hudPrefab);
    }

    private void Update()
    {
        TickJailOutlineMask();
        Refresh();
    }

    /// <summary>보는 사람이 감옥 방 안에 있을 때만 감옥 윤곽선 레이어를 켠다.</summary>
    private void TickJailOutlineMask()
    {
        if (m_outliner == null)
            return;

        long bit = 1L << k_jailOutlineLayer;
        long current = m_outliner.OutlineLayerMask;
        long next = JailRoom.Contains(transform.position) ? current | bit : current & ~bit;

        if (next != current)
            m_outliner.OutlineLayerMask = next;
    }

    /// <summary>위치로 감옥 방 소속을 판정해 윤곽선 레이어를 정한다.</summary>
    public static void ApplyJailOutlineLayer(Outlinable outlinable, Vector3 worldPosition)
    {
        if (outlinable == null)
            return;

        outlinable.OutlineLayer = JailRoom.Contains(worldPosition) ? k_jailOutlineLayer : 0;
    }

    private bool m_paletteWarned;

    private Color HighlightColor
    {
        get
        {
            if (m_palette != null)
                return m_palette.Highlight;

            if (!m_paletteWarned)
            {
                m_paletteWarned = true;
                Debug.LogWarning("InteractionFeedback: 색 팔레트가 연결되지 않았다", this);
            }

            return Color.white;
        }
    }

    private void Refresh()
    {
        ItemBase equipped = m_itemUser != null ? m_itemUser.EquippedItem : null;
        bool holdingAimedWeapon = equipped is IAimedWeapon;
        bool weaponOnTarget =
            equipped is IAimedWeapon aimedWeapon
            && aimedWeapon.HasValidAimTarget(
                m_interactor.AimOrigin.position,
                m_interactor.AimOrigin.forward
            );

        GameObject aimTarget = m_interactor.CurrentTarget;
        IInteractable interactable = m_interactor.CurrentInteractable;

        GameObject root = interactable is Component component ? component.gameObject : aimTarget;

        PlayerIncapacitation aimedIncap =
            aimTarget != null ? aimTarget.GetComponentInParent<PlayerIncapacitation>() : null;
        Transform rangeAnchor =
            aimedIncap != null ? aimedIncap.transform
            : root != null ? root.transform
            : null;

        bool inRange = false;
        if (rangeAnchor != null)
        {
            float range = m_interactor.Range;
            inRange =
                (rangeAnchor.position - m_interactor.AimOrigin.position).sqrMagnitude
                <= range * range;
        }

        bool itemUsable = inRange && equipped != null && equipped.CanTarget(aimTarget);

        bool interactUsable =
            inRange && !itemUsable && interactable != null && interactable.CanInteract(gameObject);

        bool allyBodyTargetable =
            inRange
            && !itemUsable
            && !interactUsable
            && aimedIncap != null
            && aimedIncap.IsAimTargetable;
        GameObject allyBodyRoot = allyBodyTargetable ? aimedIncap.gameObject : null;

        if (
            holdingAimedWeapon
            && root != null
            && root.GetComponentInParent<NpcController>() != null
        )
        {
            itemUsable = false;
            interactUsable = false;
        }

        if (m_terminalFocus != null && m_terminalFocus.IsFocusing || IsAimSuppressed)
        {
            itemUsable = false;
            interactUsable = false;
            allyBodyTargetable = false;
        }

        if (itemUsable || interactUsable)
            SetOutlined(root, itemUsable ? equipped.TargetOutlineColor : HighlightColor);
        else if (allyBodyTargetable)
            SetOutlined(allyBodyRoot, HighlightColor);
        else
            SetOutlined(null, Color.clear);

        App.UI.Crosshair?.SetVisible(!IsAimSuppressed);

        if (weaponOnTarget)
            App.UI.Crosshair?.SetWeaponTargeting(true);
        else
            App.UI.Crosshair?.SetInteractable(itemUsable || interactUsable || allyBodyTargetable);

        TickPrompt(interactUsable ? interactable : null, itemUsable ? equipped : null);
    }

    /// <summary>조준한 대상의 키와 동작 안내를 표시한다.</summary>
    private void TickPrompt(IInteractable interactable, ItemBase item)
    {
        InteractPromptView view = App.UI.InteractPrompt;
        if (view == null)
            return;

        if (IsAimSuppressed)
        {
            view.HidePrompt();
            return;
        }

        string key = m_input != null ? m_input.InteractBinding : string.Empty;

        LocalizedString held = HeldTargetPrompt();
        if (held != null)
        {
            view.ShowPrompt(key, held, null);
            return;
        }

        if (item != null)
        {
            LocalizedString itemAction = item.TargetPromptLabel(m_interactor.CurrentTarget);
            if (itemAction != null)
            {
                view.ShowPrompt(
                    m_input != null ? m_input.UseItemBinding : string.Empty,
                    itemAction,
                    null
                );
            }
            else
            {
                view.HidePrompt();
            }

            return;
        }

        LocalizedString action = interactable?.PromptLabel(gameObject);
        if (action != null)
        {
            view.ShowPrompt(key, action, interactable.BlockedReason(gameObject));
            return;
        }

        ItemBase equipped = m_itemUser != null ? m_itemUser.EquippedItem : null;
        LocalizedString heldAction = equipped != null ? equipped.HeldPromptLabel() : null;
        if (heldAction == null)
        {
            view.HidePrompt();
            return;
        }

        view.ShowPrompt(m_input != null ? m_input.UseItemBinding : string.Empty, heldAction, null);
    }

    /// <summary>손에 쥔 것이 있을 때 E가 할 동작의 안내 문구를 돌려준다.</summary>
    private LocalizedString HeldTargetPrompt()
    {
        GameObject aimTarget = m_interactor.CurrentTarget;
        if (aimTarget == null)
            return null;

        bool dragging = m_escorter != null && m_escorter.IsDraggingAny;
        bool carrying = m_carrier != null && m_carrier.IsCarrying;
        if (!dragging && !carrying)
            return null;

        if (dragging)
        {
            NpcController aimedNpc = aimTarget.GetComponentInParent<NpcController>();
            if (aimedNpc != null && m_escorter.IsDraggingNpc(aimedNpc))
                return InteractPrompts.NpcRelease;
        }

        if (carrying)
        {
            Transform carried = m_carrier.CarriedTransform;
            if (carried != null && aimTarget.transform.IsChildOf(carried))
                return InteractPrompts.PutDownBody;
        }

        IInteractable aimed = m_interactor.CurrentInteractable;
        if (aimed != null && aimed.CanInteract(gameObject))
            return null;

        if (dragging && carrying)
            return InteractPrompts.ReleaseAll;

        return dragging ? InteractPrompts.NpcRelease : InteractPrompts.PutDownBody;
    }

    private void SetOutlined(GameObject root, Color color)
    {
        if (m_currentOutlinable != null && root == m_currentOutlinable.gameObject)
        {
            m_currentOutlinable.OutlineParameters.Color = color;
            return;
        }

        if (m_currentOutlinable != null)
        {
            if (m_currentOutlinable.TryGetComponent(out DroppedItemHighlight highlight))
                highlight.Restore();
            else
                m_currentOutlinable.enabled = false;
        }
        m_currentOutlinable = null;

        if (root == null)
            return;

        var outlinable = root.GetComponent<Outlinable>();
        if (outlinable == null)
        {
            outlinable = root.AddComponent<Outlinable>();
            AddOutlineTargets(outlinable, root);
        }

        outlinable.OutlineParameters.Color = color;
        ApplyJailOutlineLayer(outlinable, root.transform.position);
        outlinable.enabled = true;
        m_currentOutlinable = outlinable;

        var diagList = new System.Collections.Generic.List<Outlinable>();
        Outlinable.GetAllActiveOutlinables(diagList);
        bool diagRegistered = diagList.Contains(outlinable);
        Camera diagCam = m_outliner != null ? m_outliner.GetComponent<Camera>() : null;
        Debug.Log(
            $"[856-DIAG] root={root.name} registered={diagRegistered} listCount={diagList.Count} " +
            $"targets={outlinable.OutlineTargetsCount} outlineLayer={outlinable.OutlineLayer} " +
            $"mask={(m_outliner != null ? m_outliner.OutlineLayerMask : -999)} " +
            $"outlinerObj={(m_outliner != null ? m_outliner.gameObject.name : "null")} " +
            $"outlinerEnabled={(m_outliner != null ? m_outliner.enabled : false)} " +
            $"camEnabled={(diagCam != null ? diagCam.enabled : false)} " +
            $"forceIntoRT={(diagCam != null ? diagCam.forceIntoRenderTexture : false)}"
        );
    }

    /// <summary>씬에 미리 놓인 상호작용물에 Outlinable을 미리 붙여 둔다(로딩 화면 중 호출).</summary>
    public static void WarmUpInteractableOutlines()
    {
        foreach (
            MonoBehaviour behaviour in Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsSortMode.None
            )
        )
        {
            if (behaviour is not IInteractable || behaviour.GetComponent<Outlinable>() != null)
                continue;

            Outlinable outlinable = behaviour.gameObject.AddComponent<Outlinable>();
            AddOutlineTargets(outlinable, behaviour.gameObject);
            outlinable.enabled = false;
        }
    }

    /// <summary>유효한 메시가 있는 자식 렌더러만 윤곽선 대상으로 수집한다.</summary>
    public static void AddOutlineTargets(Outlinable outlinable, GameObject root)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
                mesh = skinned.sharedMesh;
            else if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter))
                mesh = filter.sharedMesh;

            if (mesh == null)
                continue;

            for (int i = 0; i < mesh.subMeshCount; i++)
                outlinable.AddTarget(new OutlineTarget(renderer, i));
        }
    }
}
