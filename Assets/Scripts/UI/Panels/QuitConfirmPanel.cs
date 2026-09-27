/// <summary>
/// Title 씬의 ESC 진입 메뉴 — 게임 종료를 확인한다.
/// </summary>
public class QuitConfirmPanel : ConfirmPanelBase
{
    public override bool IsEscMenu => true;

    protected override void OnConfirm() => TitleUIManager.QuitGame();
}
