/// <summary>
/// 첫 접속 시 한 번 튜토리얼을 권하는 예/아니오 확인창.
/// </summary>
public class TutorialConfirmPanel : ConfirmPanelBase
{
    protected override void OnConfirm() => TutorialFlow.Enter();
}
