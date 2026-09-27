using UnityEngine;

/// <summary>
/// 화면 상단 경보 토스트. App.UI.Toast로 접근하며, 표시는 로컬 전용이다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ToastView : TimedMessageView { }
