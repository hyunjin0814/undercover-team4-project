using EPOOutline;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 조준 피드백 — 오너 전용, 순수 로컬 비주얼(네트워크 동기화 없음). (#184)
/// "지금 실제로 할 수 있는 행동이 있는 대상"에만 윤곽선을 표시한다:
///   - 장착 아이템이 대상에 사용 가능(ItemBase.CanTarget) → 아이템별 색 (우선)
///   - E 상호작용 가능(IInteractable.CanInteract) → 기본색
/// 조준 유지 중에도 NPC 상태·배터리·장착 아이템이 변하므로 매 프레임 재평가한다.
/// Outlinable은 첫 조준 시 런타임 부착 후 캐시(비활성 유지)되므로 대상 프리팹 사전 작업이 필요 없다.
/// </summary>
[RequireComponent(typeof(PlayerInteractor))]
public class InteractionFeedback : NetworkBehaviour
{
    [Header("HUD")]
    [Tooltip("씬에 HUD가 없으면 오너 스폰 시 이 프리팹을 생성한다")]
    [SerializeField] private GameObject m_hudPrefab;

    [Header("아웃라인 (EPO)")]
    [Tooltip("E 상호작용 대상의 기본 윤곽선 색 — 아이템 사용 대상은 ItemBase.TargetOutlineColor를 쓴다")]
    [SerializeField] private Color m_outlineColor = new Color(1f, 0.85f, 0.2f, 1f);

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_input; // 조준 안내에 적을 키 표기를 읽는다 (#664)

    // 겨냥한 E를 상호작용보다 먼저 가져가는 두 갈래 — 끌기·운반 (#664, HeldTargetPrompt 참고)
    private PlayerEscorter m_escorter;
    private PlayerCarrier m_carrier;

    // 입력이 죽은 구간에서 안내를 내리는 데 쓴다 (#664, TickPrompt 참고)
    private PlayerIncapacitation m_incapacitation;
    private Outlinable m_currentOutlinable;

    // 감옥 방 안 오브젝트를 담는 EPO 윤곽선 레이어 (#537).
    //
    // <b>EPO의 레이어는 유니티 물리 레이어와 무관한 자체 개념</b>이라, 조준 마스크(Interactable)를
    // 건드리지 않고 "그릴지 말지"만 따로 가를 수 있다. 0번은 그 외 전부가 쓰는 기본 레이어다.
    private const int k_jailOutlineLayer = 1;

    // 플레이어 카메라의 EPO 렌더러 — 감옥 레이어를 켜고 끄는 대상. 비오너는 이 컴포넌트가 꺼지므로
    // 각자 자기 화면 몫만 다룬다.
    private Outliner m_outliner;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        EnsureHud();
        // 호스트는 Title 씬에서 스폰되므로(세션 생성=StartHost, #247) 거기서 만든 HUD가
        // InGame 씬 전환 때 파괴된다 — 씬이 로드될 때마다 다시 보장한다.
        App.OnSceneLoaded += HandleSceneLoaded;

        m_interactor = GetComponent<PlayerInteractor>();
        m_itemUser = GetComponent<PlayerItemUser>(); // 없는 구성(테스트 등)이면 null
        m_input = GetComponent<PlayerInputHandler>(); // 없는 구성(테스트 등)이면 null
        m_escorter = GetComponent<PlayerEscorter>();
        m_carrier = GetComponent<PlayerCarrier>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_outliner = GetComponentInChildren<Outliner>(true); // 카메라에 붙어 있다
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        App.OnSceneLoaded -= HandleSceneLoaded;
        SetOutlined(null, Color.clear);
        App.UI.InteractPrompt?.HidePrompt(); // 퇴장·씬 전환으로 사라질 때 안내가 화면에 남지 않게
    }

    private void HandleSceneLoaded(EScene scene) => EnsureHud();

    // 씬에 HUD가 없으면 생성한다. 타이틀·로비에서는 만들지 않는다 — 인게임 HUD(조준점·타이머·목표
    // 금액)라 UI 대기 화면에 있을 물건이 아니다.
    // 로비를 함께 막는 이유: 플레이어는 destroyWithScene:false로 스폰돼 씬을 넘어 살아남는데,
    // 이 오브젝트가 App.OnSceneLoaded마다 EnsureHud를 다시 부른다. 막지 않으면 라운드 실패로
    // 로비에 돌아왔을 때(#395) 대기 화면 위에 인게임 HUD가 다시 그려진다.
    private void EnsureHud()
    {
        if (App.CurrentScene == EScene.Title || App.CurrentScene == EScene.Lobby)
            return;
        if (App.UI.Crosshair == null && m_hudPrefab != null)
            Instantiate(m_hudPrefab);
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        TickJailOutlineMask();
        Refresh();
    }

    /// <summary>
    /// 감옥 방 윤곽선을 <b>방 안에 있을 때만</b> 그린다. (#537)
    ///
    /// EPO는 윤곽선을 후처리로 그리며 기본값(<c>ComplexMaskingMode.None</c>)에서는 씬 깊이를 보지
    /// 않는다 — 벽 뒤든 400m 밖이든 대상이 목록에 있으면 실루엣이 그대로 비친다. 감옥은 맵 밖에
    /// 따로 떨어진 공간이라(#537) 도시에서 그 윤곽이 벽을 뚫고 보였다.
    ///
    /// 벽을 전부 obstacle로 등록하는 정공법 대신 <b>레이어를 가른다</b>: 감옥 방 안의 대상만
    /// <see cref="k_jailOutlineLayer"/>에 넣고(<see cref="ApplyJailOutlineLayer"/>), 보는 사람이
    /// 방 안에 있을 때만 그 레이어를 켠다. 방 안에서는 종전대로 전부 보인다.
    ///
    /// 다른 비트는 건드리지 않는다 — 마스크의 나머지는 EPO 기본값(전체)이고 이 기능의 소관이 아니다.
    /// </summary>
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

    /// <summary>
    /// 이 윤곽선이 감옥 방 소속인지 정해 레이어에 넣는다 — 켜는 쪽에서 매번 부른다. (#537)
    ///
    /// 위치로 판정하므로 아이템을 감옥 안팎으로 들고 다녀도 알아서 따라온다. 감옥이 없는 씬
    /// (단독 테스트)에서는 <see cref="JailRoom.Contains"/>가 항상 false라 전부 기본 레이어다.
    /// </summary>
    public static void ApplyJailOutlineLayer(Outlinable outlinable, Vector3 worldPosition)
    {
        if (outlinable == null)
            return;

        outlinable.OutlineLayer = JailRoom.Contains(worldPosition) ? k_jailOutlineLayer : 0;
    }

    private void Refresh()
    {
        // 조준 무기(테이저 8m·진압봉 2m)는 3m 상호작용 레이(CanTarget) 대신 자체 사거리 조준 판정으로
        // 크로스헤어를 구동한다 (#328/#217). 아이템 타입으로 분기하지 않고 IAimedWeapon으로 묶어,
        // 무기가 늘어도 이 파일을 고치지 않게 한다.
        ItemBase equipped = m_itemUser != null ? m_itemUser.EquippedItem : null;
        bool holdingAimedWeapon = equipped is IAimedWeapon;
        bool weaponOnTarget = equipped is IAimedWeapon aimedWeapon
            && aimedWeapon.HasValidAimTarget(
                m_interactor.AimOrigin.position, m_interactor.AimOrigin.forward);

        GameObject aimTarget = m_interactor.CurrentTarget;
        IInteractable interactable = m_interactor.CurrentInteractable;

        // 윤곽선 적용 루트를 먼저 해석 — IInteractable 컴포넌트가 붙은 루트 기준(자식 콜라이더 대응),
        // 아이템 경로만 가능하고 IInteractable이 없는 대상이면 조준 대상 자체.
        GameObject root = interactable is Component component ? component.gameObject : aimTarget;

        // 서버 거리 검증과 동일 공식(AimOrigin→대상 루트 중심)으로 재확인 — 레이캐스트는 콜라이더
        // '표면'까지의 거리라서 임계점에서 "윤곽선은 뜨는데 서버가 거부"하는 불일치가 생긴다 (#184)
        bool inRange = false;
        if (root != null)
        {
            float range = m_interactor.Range;
            inRange = (root.transform.position - m_interactor.AimOrigin.position).sqrMagnitude
                <= range * range;
        }

        // ① 아이템 경로 — 장착 아이템이 이 대상에 실제로 사용 가능한가 (아이템별 색, 우선)
        //    색이 "어떤 키가 먹히는지" 안내 역할을 하도록 아이템 경로를 우선한다 (equipped는 위에서 해석)
        bool itemUsable = inRange && equipped != null && equipped.CanTarget(aimTarget);

        // ② E 상호작용 경로 — 지금 상태에서 E가 실제로 동작하는가 (기본색)
        bool interactUsable = inRange && !itemUsable
            && interactable != null && interactable.CanInteract(gameObject);

        // 조준 무기를 든 동안엔 NPC 윤곽선만 끈다 (#328/#363/#217). 상호작용 레이(3m) 기준 윤곽선이
        // 무기의 실제 사거리와 어긋나고(테이저 8m로 더 멀고, 진압봉 2m로 더 가깝다), 겨냥한 몸이
        // 빛나면 오조준의 긴장이 사라지기 때문이다. 명중 가능 여부는 아래 크로스헤어 색으로 알린다.
        // 끄는 범위를 NPC로 좁힌 것이 #363의 수정 — 예전에는 조기 return이라 인명부·콘솔 같은
        // 사격과 무관한 E 상호작용물까지 통째로 표시가 죽었다. 이 무기들의 대상은 NPC뿐이므로,
        // NPC만 빼면 오조준 설계는 그대로 유지된다.
        if (holdingAimedWeapon && root != null && root.GetComponentInParent<NpcController>() != null)
        {
            itemUsable = false;
            interactUsable = false;
        }

        if (itemUsable || interactUsable)
            SetOutlined(root, itemUsable ? equipped.TargetOutlineColor : m_outlineColor);
        else
            SetOutlined(null, Color.clear);

        // 크로스헤어는 무기 명중선이 최우선 — 때리거나 쏠 수 있는 대상을 겨눴다면 그 색을 덮어쓰지 않는다.
        // (둘 다 아니면 어느 쪽을 부르든 기본색이라 분기 하나로 충분하다)
        if (weaponOnTarget)
            App.UI.Crosshair?.SetWeaponTargeting(true);
        else
            App.UI.Crosshair?.SetInteractable(itemUsable || interactUsable);

        TickPrompt(interactUsable ? interactable : null, itemUsable ? equipped : null);
    }

    /// <summary>
    /// 조준한 대상의 키 + 동작을 띄운다 — 대상 판정은 윤곽선과 같은 값을 쓴다. (#664)
    /// 순서는 손에 쥔 것(E) → 아이템(좌클릭) → 상호작용(E)이다. <b>윤곽선 순서와 다른 곳이 하나
    /// 있다</b>: 놓기 안내가 아이템 문구를 이긴다. 색은 무엇을 할 수 있는지를 말하지만 놓기는
    /// 손에 든 것을 잃는 쪽이라, 한 줄뿐인 자리에서는 잃는 쪽을 먼저 알린다(팀 결정).
    /// </summary>
    private void TickPrompt(IInteractable interactable, ItemBase item)
    {
        InteractPromptView view = App.UI.InteractPrompt;
        if (view == null)
            return;

        // 입력이 죽은 구간에서는 안내도 내린다. 무력화 중엔 E도 좌클릭도 각자 가드에 막히고
        // (PlayerInteractor·PlayerItemUser), 입력 정지 중엔 액션 자체가 꺼져 있다.
        // 이 자리를 열어 두면 이 이슈가 없애려던 "떠 있는데 눌러도 반응 없음"이 그대로 남는다.
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated
            || m_input != null && m_input.IsSuspended)
        {
            view.HidePrompt();
            return;
        }

        string key = m_input != null ? m_input.InteractBinding : string.Empty;

        // ① 손에 쥔 것을 겨눈 E — 막힐 일이 없는 갈래라 사유도 없다.
        //    아이템 문구보다 앞이다: 밧줄로 묶을 수 있는 대상(기절·시체)은 CanInteract가 false라
        //    바로 그 자리에서 E가 '전부 놓기'로 나간다. 아이템 문구에 가리면 끌던 대상을
        //    놓치는 것을 화면 어디에서도 예고하지 못한다.
        LocalizedString held = HeldTargetPrompt();
        if (held != null)
        {
            view.ShowPrompt(key, held, null);
            return;
        }

        // ② 아이템 경로 — 키가 좌클릭이라 표기도 그쪽에서 읽는다. 막힘 사유는 두지 않는다
        //    (쓸 수 없으면 CanTarget이 false라 안내째 사라진다).
        if (item != null)
        {
            LocalizedString itemAction = item.TargetPromptLabel(m_interactor.CurrentTarget);
            if (itemAction != null)
            {
                view.ShowPrompt(
                    m_input != null ? m_input.UseItemBinding : string.Empty, itemAction, null);
            }
            else
            {
                // 문구를 안 정한 아이템 — E 안내로 흘려보내지 않는다. 윤곽선이 이미 아이템 색이라
                // 색은 좌클릭을, 글자는 E를 가리키는 어긋남이 생긴다.
                view.HidePrompt();
            }

            return;
        }

        // ③ 상호작용 대상
        LocalizedString action = interactable?.PromptLabel(gameObject);
        if (action == null)
        {
            view.HidePrompt();
            return;
        }

        view.ShowPrompt(key, action, interactable.BlockedReason(gameObject));
    }

    /// <summary>
    /// 손에 쥔 것이 있을 때 E가 무엇을 하는가 — <see cref="PlayerInteractor"/>의 갈래 순서를 그대로
    /// 따라간다(#638/#365). 겨냥한 그 대상만 놓는 갈래가 먼저고, 겨냥한 상호작용이 없으면 전부 놓기다.
    /// 안 그러면 "밧줄 풀기"가 뜨는데 놓기가 나가거나, 안내 없이 동료가 바닥에 떨어진다.
    /// 겨눈 것이 아무것도 없을 때는 다루지 않는다 — 안내를 걸 대상이 없다.
    /// </summary>
    private LocalizedString HeldTargetPrompt()
    {
        GameObject aimTarget = m_interactor.CurrentTarget;
        if (aimTarget == null)
            return null;

        bool dragging = m_escorter != null && m_escorter.IsDraggingAny;
        bool carrying = m_carrier != null && m_carrier.IsCarrying;
        if (!dragging && !carrying)
            return null;

        // ① 끌던 그 대상을 겨눴다 — 그 하나만 놓는다.
        if (dragging)
        {
            NpcController aimedNpc = aimTarget.GetComponentInParent<NpcController>();
            if (aimedNpc != null && m_escorter.IsDraggingNpc(aimedNpc))
                return InteractPrompts.NpcRelease;
        }

        // ② 업은 동료를 겨눴다 — 그 몸만 내려놓는다.
        if (carrying)
        {
            Transform carried = m_carrier.CarriedTransform;
            if (carried != null && aimTarget.transform.IsChildOf(carried))
                return InteractPrompts.PutDownBody;
        }

        // ③ 겨냥한 상호작용이 살아 있으면 그쪽이 이긴다 — 없으면 손에 쥔 것을 전부 놓는다.
        //    사거리·아이템 우선순위가 섞인 interactUsable이 아니라 저쪽과 같은 기준으로 본다.
        IInteractable aimed = m_interactor.CurrentInteractable;
        if (aimed != null && aimed.CanInteract(gameObject))
            return null;

        if (dragging && carrying)
            return InteractPrompts.ReleaseAll;

        return dragging ? InteractPrompts.NpcRelease : InteractPrompts.PutDownBody;
    }

    private void SetOutlined(GameObject root, Color color)
    {
        // 같은 대상이면 색만 갱신 (아이템 스왑 대응) — 껐다 켜는 낭비 방지
        if (m_currentOutlinable != null && root == m_currentOutlinable.gameObject)
        {
            m_currentOutlinable.OutlineParameters.Color = color;
            return;
        }

        if (m_currentOutlinable != null) // 이전 대상 정리 (파괴됐으면 이미 null)
        {
            // 바닥 아이템 상시 하이라이트(#330)와 Outlinable을 공유한다 — 꺼버리면 상시 표시까지
            // 죽으므로, 하이라이트가 있는 대상은 끄는 대신 기본색으로 되돌려 준다.
            if (m_currentOutlinable.TryGetComponent(out DroppedItemHighlight highlight))
                highlight.Restore();
            else
                m_currentOutlinable.enabled = false;
        }
        m_currentOutlinable = null;

        if (root == null) return;

        var outlinable = root.GetComponent<Outlinable>();
        if (outlinable == null)
        {
            // 런타임 AddComponent는 Reset()이 호출되지 않으므로 렌더러 수집을 직접 한다
            outlinable = root.AddComponent<Outlinable>();
            AddOutlineTargets(outlinable, root);
        }

        outlinable.OutlineParameters.Color = color;
        ApplyJailOutlineLayer(outlinable, root.transform.position);
        outlinable.enabled = true;
        m_currentOutlinable = outlinable;
    }

    /// <summary>
    /// 자식 렌더러들을 윤곽선 대상으로 수집한다. EPO의 AddAllChildRenderersToRenderingList는
    /// 모든 MeshRenderer에 MeshFilter가 있다고 가정해 TextMesh(디버그 라벨 등 메시 내부 생성형)에서
    /// MissingComponentException을 던지므로, 유효한 메시가 있는 렌더러만 직접 담는다. (#207)
    /// 바닥 아이템 상시 하이라이트(DroppedItemHighlight, #330)도 같은 수집 규칙을 쓴다 — public인 이유.
    /// </summary>
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
                continue; // TextMesh 라벨·메시 미지정 렌더러 — 윤곽선 대상에서 제외

            for (int i = 0; i < mesh.subMeshCount; i++)
                outlinable.AddTarget(new OutlineTarget(renderer, i));
        }
    }
}
