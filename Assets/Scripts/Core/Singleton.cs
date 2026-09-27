/// <summary>
/// 순수 C# 지연 생성 싱글톤 (MonoBehaviour 아님). 도메인 리로드 꺼짐 대비 Reset 제공.
/// </summary>
public class Singleton<T> where T : class, new()
{
    private static T s_instance;
    public static T Instance => s_instance ??= new T();

    /// <summary>Enter Play Mode Options(도메인 리로드 꺼짐)에서 이전 플레이 잔재 제거용.</summary>
    public static void Reset() => s_instance = null;
}
