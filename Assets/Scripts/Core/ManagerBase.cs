using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// App의 매니저 필드에 매니저 인스턴스를 리플렉션으로 자동 주입·해제하는 헬퍼.
/// CommonManagerBase·NetworkedManagerBase를 상속하면 App에 등록된다.
/// </summary>
public static class ManagerHandler
{
    private static readonly Dictionary<Type, FieldInfo> s_appFields;

    static ManagerHandler()
    {
        FieldInfo[] fields = typeof(App).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        s_appFields = new Dictionary<Type, FieldInfo>(fields.Length);

        foreach (FieldInfo field in fields)
        {
            if (!s_appFields.TryAdd(field.FieldType, field))
                Debug.LogError($"[ManagerHandler] App에 같은 타입 필드가 중복됨: {field.FieldType.Name}");
        }
    }

    internal static void Register(MonoBehaviour manager)
    {
        FieldInfo field = FindField(manager.GetType());
        if (field == null)
        {
            Debug.LogError($"[ManagerHandler] App에 대응 필드가 없는 매니저: {manager.GetType().Name}");
            return;
        }

        if (field.GetValue(App.Instance) is MonoBehaviour current && current != null && current != manager)
            Debug.LogError($"[ManagerHandler] {field.FieldType.Name} 중복 등록: '{current.name}' 위에 '{manager.name}'. 씬에 같은 매니저가 2개인지 확인.");

        field.SetValue(App.Instance, manager);
    }

    internal static void Unregister(MonoBehaviour manager)
    {
        FieldInfo field = FindField(manager.GetType());
        if (field == null)
            return;

        if (ReferenceEquals(field.GetValue(App.Instance), manager))
            field.SetValue(App.Instance, null);
    }

    private static FieldInfo FindField(Type type)
    {
        if (s_appFields.TryGetValue(type, out FieldInfo exact))
            return exact;

        FieldInfo found = null;
        foreach (FieldInfo field in s_appFields.Values)
        {
            if (!field.FieldType.IsAssignableFrom(type))
                continue;
            if (found != null)
                return null;
            found = field;
        }
        return found;
    }
}

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class CommonManagerBase : MonoBehaviour
{
    protected virtual void Awake() => ManagerHandler.Register(this);
    protected virtual void OnDestroy() => ManagerHandler.Unregister(this);
}

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class NetworkedManagerBase : NetworkBehaviour
{
    protected virtual void Awake() => ManagerHandler.Register(this);

    public override void OnDestroy()
    {
        ManagerHandler.Unregister(this);
        base.OnDestroy();
    }
}
