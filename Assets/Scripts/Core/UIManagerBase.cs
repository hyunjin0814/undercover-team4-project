using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 씬당 하나인 UI 매니저 베이스 — 패널을 타입으로 관리하고 ESC 스택을 처리한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public abstract class UIManagerBase : CommonManagerBase
{
    private readonly Dictionary<Type, PanelBase> m_panels = new();
    private readonly Stack<PanelBase> m_escStack = new();

    private PanelBase m_escMenuPanel;

#if UNITY_EDITOR
    private bool m_reassertCursorNextFrame;
#endif

    protected virtual void Update()
    {
#if UNITY_EDITOR
        if (m_reassertCursorNextFrame)
        {
            m_reassertCursorNextFrame = false;
            CursorLock.Reassert();
        }
#endif

        if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
            return;

#if UNITY_EDITOR
        m_reassertCursorNextFrame = true;
#endif

        if (m_escStack.TryPeek(out PanelBase top) && top != null)
        {
            if (top.CanCloseWithESC)
                top.ClosePanel();
            return;
        }

        if (m_escMenuPanel != null && m_escMenuPanel.CanOpenFromEsc)
            m_escMenuPanel.OpenPanel();
    }

    public void RegisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        if (!m_panels.TryAdd(panel.GetType(), panel))
        {
            Debug.LogError($"[{GetType().Name}] 패널 중복 등록: {panel.GetType().Name}", panel);
            return;
        }

        if (!panel.IsEscMenu)
            return;

        if (m_escMenuPanel != null)
            Debug.LogError(
                $"[{GetType().Name}] ESC 진입 메뉴가 이미 있음: {m_escMenuPanel.GetType().Name} — {panel.GetType().Name} 무시",
                panel
            );
        else
            m_escMenuPanel = panel;
    }

    public void UnregisterPanel(PanelBase panel)
    {
        if (panel == null)
            return;

        if (
            m_panels.TryGetValue(panel.GetType(), out PanelBase current)
            && ReferenceEquals(current, panel)
        )
            m_panels.Remove(panel.GetType());

        if (ReferenceEquals(m_escMenuPanel, panel))
            m_escMenuPanel = null;
    }

    public void PushUIStack(PanelBase panel)
    {
        if (panel != null)
            m_escStack.Push(panel);
    }

    public void PopUIStack(PanelBase panel)
    {
        if (m_escStack.TryPeek(out PanelBase top) && ReferenceEquals(top, panel))
            m_escStack.Pop();
    }

    public bool TryGetPanel<T>(out T panel)
        where T : PanelBase
    {
        if (m_panels.TryGetValue(typeof(T), out PanelBase value) && value is T typed)
        {
            panel = typed;
            return true;
        }

        panel = null;
        return false;
    }

    public T GetPanel<T>()
        where T : PanelBase
    {
        if (TryGetPanel(out T panel))
            return panel;

        throw new InvalidOperationException(
            $"[{GetType().Name}] 등록되지 않은 패널: {typeof(T).Name}"
        );
    }

    public bool OpenPanel<T>()
        where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
        {
            Debug.LogError(
                $"[{GetType().Name}] 등록되지 않은 패널을 열려 했습니다: {typeof(T).Name} — 씬에 배치됐는지 확인하세요."
            );
            return false;
        }

        panel.OpenPanel();
        return true;
    }

    public bool ClosePanel<T>()
        where T : PanelBase
    {
        if (!TryGetPanel(out T panel))
            return false;

        panel.ClosePanel();
        return true;
    }
}
