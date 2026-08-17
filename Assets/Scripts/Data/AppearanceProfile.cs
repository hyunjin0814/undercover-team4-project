using System;
using Unity.Netcode;

/// <summary>
/// 외형 특징 축 — 몽타주로 구두 전달 가능한 특징 종류.
/// 축별 옵션(표시 이름·시각 리소스)은 AppearanceDatabase가 정의한다.
/// </summary>
// 값 이름이 곧 축 이름 문구의 키다 (Npc.Axis. + 이름) — 축을 추가하면 NpcTable에 같은 이름의 키를
// 함께 넣을 것. 축은 데이터가 아니라 코드가 정하는 목록이라 AppearanceDatabase 에셋에 이름을 두지 않는다. (#497)
[LocalizedEnum("NpcTable", "Npc.Axis.")]
public enum AppearanceAxis
{
    HairStyle = 0,
    HairColor = 1,
    SkinColor = 2,
    FacialHair = 3,
    Headwear = 4,
    Eyewear = 5,
}

/// <summary>
/// 수배 조건이 걸린 축들의 집합 — 축 하나당 비트 1개(전부 합쳐 1바이트).
///
/// #669 이전에는 "몽타주로 공개된(=범인의 알려진) 축"이었다. 지금은 <b>검거 조건</b>이다 — 이 축들에서
/// <see cref="AppearanceProfile.MatchesOn"/>이 성립하면 그 NPC는 개체와 무관하게 진범 판정이 난다.
///
/// 수배 항목(<see cref="WantedEntry"/>)에 외형 프로필과 함께 실려 나가, <b>받는 쪽이 자기 언어로</b>
/// 몽타주를 조립하게 한다 (#497). 서버가 완성 문장을 실어 보내던 구조를 대신한다.
///
/// 나열 순서는 담은 순서가 아니라 <see cref="AppearanceAxis"/> 선언 순서다 — 조건 축의 순서 자체는
/// 규칙상 뜻이 없고, 반대로 <b>어느 피어에서 조립해도 같은 문장</b>이 나오는 것은 중요하다
/// (검거로 내렸다가 탈출로 재등재해도(#231) 본부가 기억하던 문장과 어긋나면 안 된다).
/// </summary>
public struct RevealedAxisSet : INetworkSerializable, IEquatable<RevealedAxisSet>
{
    private byte m_mask; // 축별 비트합 — 1 << (int)axis

    public bool IsEmpty => m_mask == 0;

    /// <summary>담긴 축 수.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
            {
                if (Contains((AppearanceAxis)i))
                    count++;
            }
            return count;
        }
    }

    public bool Contains(AppearanceAxis axis) => (m_mask & Bit(axis)) != 0;

    public void Add(AppearanceAxis axis) => m_mask |= Bit(axis);

    public void Clear() => m_mask = 0;

    private static byte Bit(AppearanceAxis axis) => (byte)(1 << (int)axis);

    /// <summary>축 선언 순서로 순회한다 — foreach 전용(할당 없는 구조체 열거자).</summary>
    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        private readonly RevealedAxisSet m_set;
        private int m_index; // 마지막으로 본 축 (-1에서 시작)

        public Enumerator(RevealedAxisSet set)
        {
            m_set = set;
            m_index = -1;
            Current = default;
        }

        public AppearanceAxis Current { get; private set; }

        public bool MoveNext()
        {
            while (++m_index < AppearanceProfile.k_axisCount)
            {
                var axis = (AppearanceAxis)m_index;
                if (!m_set.Contains(axis))
                    continue;

                Current = axis;
                return true;
            }
            return false;
        }
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer)
        where TBuffer : IReaderWriter => serializer.SerializeValue(ref m_mask);

    public bool Equals(RevealedAxisSet other) => m_mask == other.m_mask;

    public override bool Equals(object obj) => obj is RevealedAxisSet other && Equals(other);

    public override int GetHashCode() => m_mask;
}

/// <summary>
/// NPC 한 명의 외형 특징 조합 — 축별 옵션 인덱스만 담는다.
/// CitizenIdentity의 일부로 취급하며, 네트워크로는 이 인덱스만 전송된다.
/// </summary>
[Serializable]
public struct AppearanceProfile : INetworkSerializable, IEquatable<AppearanceProfile>
{
    // 외형 축 개수 — AppearanceAxis enum 값 수와 일치해야 함.
    public const int k_axisCount = 6;

    public int HairStyleIndex;
    public int HairColorIndex;
    public int SkinColorIndex;
    public int FacialHairIndex;
    public int HeadwearIndex;
    public int EyewearIndex;

    // 아직 배정되지 않음(프리팹 기본 외형)을 뜻하는 값.
    public static AppearanceProfile Unassigned =>
        new AppearanceProfile
        {
            HairStyleIndex = -1,
            HairColorIndex = -1,
            SkinColorIndex = -1,
            FacialHairIndex = -1,
            HeadwearIndex = -1,
            EyewearIndex = -1,
        };

    public bool IsAssigned => HairColorIndex >= 0;

    public int GetIndex(AppearanceAxis axis) =>
        axis switch
        {
            AppearanceAxis.HairStyle => HairStyleIndex,
            AppearanceAxis.HairColor => HairColorIndex,
            AppearanceAxis.SkinColor => SkinColorIndex,
            AppearanceAxis.FacialHair => FacialHairIndex,
            AppearanceAxis.Headwear => HeadwearIndex,
            AppearanceAxis.Eyewear => EyewearIndex,
            _ => -1,
        };

    public void SetIndex(AppearanceAxis axis, int value)
    {
        switch (axis)
        {
            case AppearanceAxis.HairStyle:
                HairStyleIndex = value;
                break;
            case AppearanceAxis.HairColor:
                HairColorIndex = value;
                break;
            case AppearanceAxis.SkinColor:
                SkinColorIndex = value;
                break;
            case AppearanceAxis.FacialHair:
                FacialHairIndex = value;
                break;
            case AppearanceAxis.Headwear:
                HeadwearIndex = value;
                break;
            case AppearanceAxis.Eyewear:
                EyewearIndex = value;
                break;
        }
    }

    /// <summary>
    /// 지정한 축만 남기고 나머지를 미배정(-1)으로 지운 사본 — 네트워크로 내보낼 때 쓴다 (#497).
    /// 정답 외형은 서버 전용 값이라(<see cref="CitizenIdentity.Appearance"/>), 몽타주에 공개되지 않은 축은
    /// 화면에 안 띄우는 것으로 끝내지 않고 애초에 실어 보내지 않는다.
    /// 반환값의 <see cref="IsAssigned"/>는 머리색이 공개 축이 아니면 false다 — 표시 조립 전용으로만 쓸 것.
    /// </summary>
    public AppearanceProfile Masked(RevealedAxisSet axes)
    {
        AppearanceProfile masked = Unassigned;
        foreach (AppearanceAxis axis in axes)
            masked.SetIndex(axis, GetIndex(axis));
        return masked;
    }

    // 지정한 축들에서 다른 프로필과 값이 모두 같은지 — 몽타주 부합 판정에 사용.
    public bool MatchesOn(in AppearanceProfile other, RevealedAxisSet axes)
    {
        foreach (AppearanceAxis axis in axes)
        {
            if (GetIndex(axis) != other.GetIndex(axis))
                return false;
        }
        return true;
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer)
        where TBuffer : IReaderWriter
    {
        serializer.SerializeValue(ref HairStyleIndex);
        serializer.SerializeValue(ref HairColorIndex);
        serializer.SerializeValue(ref SkinColorIndex);
        serializer.SerializeValue(ref FacialHairIndex);
        serializer.SerializeValue(ref HeadwearIndex);
        serializer.SerializeValue(ref EyewearIndex);
    }

    public bool Equals(AppearanceProfile other) =>
        HairStyleIndex == other.HairStyleIndex
        && HairColorIndex == other.HairColorIndex
        && SkinColorIndex == other.SkinColorIndex
        && FacialHairIndex == other.FacialHairIndex
        && HeadwearIndex == other.HeadwearIndex
        && EyewearIndex == other.EyewearIndex;

    public override bool Equals(object obj) => obj is AppearanceProfile other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            HairStyleIndex,
            HairColorIndex,
            SkinColorIndex,
            FacialHairIndex,
            HeadwearIndex,
            EyewearIndex
        );
}
