using System;
using System.Collections.Generic;

/// <summary>
/// 소지 슬롯의 순수 배치 모델 — 고정 칸 배열 위의 대조·순환·선택·스왑 인덱스 연산만 담당한다.
/// </summary>
public sealed class LoadoutSlots<T>
    where T : class
{
    private static readonly EqualityComparer<T> s_comparer = EqualityComparer<T>.Default;

    private readonly T[] m_slots;

    private int m_equippedIndex = -1;

    public LoadoutSlots(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "슬롯 용량은 1 이상이어야 한다."
            );
        }

        m_slots = new T[capacity];
    }

    public IReadOnlyList<T> Slots => m_slots;

    public int EquippedIndex => m_equippedIndex;

    public T Equipped => m_equippedIndex >= 0 ? m_slots[m_equippedIndex] : null;

    /// <summary>index가 칸 범위 안인지 (빈손 -1은 유효 인덱스가 아님).</summary>
    public bool IsValidIndex(int index) => index >= 0 && index < m_slots.Length;

    /// <summary>첫 빈 칸 인덱스. 꽉 찼으면 -1.</summary>
    public int FirstEmptySlot()
    {
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] == null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>첫 항목이 든 칸 인덱스. 전부 비었으면 -1.</summary>
    public int FirstOccupiedSlot()
    {
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] != null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>선택 인덱스를 설정한다. -1(빈손) 또는 유효 칸을 넘긴다 — 유효성은 호출부 책임.</summary>
    public void SetEquippedIndex(int index) => m_equippedIndex = index;

    /// <summary>현재 선택에서 direction만큼 순환 이동한 칸 인덱스를 계산한다(상태 불변).</summary>
    public int NextIndex(int direction)
    {
        int baseIndex = m_equippedIndex >= 0 ? m_equippedIndex : 0;
        int length = m_slots.Length;

        return ((baseIndex + direction) % length + length) % length;
    }

    /// <summary>두 칸의 내용을 맞바꾸고 선택 인덱스도 따라 옮긴다. 실패하면 false.</summary>
    public bool TrySwap(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= m_slots.Length || b >= m_slots.Length)
        {
            return false;
        }

        (m_slots[a], m_slots[b]) = (m_slots[b], m_slots[a]);

        if (m_equippedIndex == a)
        {
            m_equippedIndex = b;
        }
        else if (m_equippedIndex == b)
        {
            m_equippedIndex = a;
        }

        return true;
    }

    /// <summary>진실 목록으로 칸 배치를 대조하고 유지할 선택 인덱스를 돌려준다.</summary>
    public int Reconcile(IReadOnlyList<T> incoming)
    {
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] != null && !Contains(incoming, m_slots[i]))
            {
                m_slots[i] = null;
            }
        }

        for (int i = 0; i < incoming.Count; i++)
        {
            T item = incoming[i];
            if (Array.IndexOf(m_slots, item) >= 0)
            {
                continue;
            }

            int emptySlot = FirstEmptySlot();
            if (emptySlot < 0)
            {
                break;
            }

            m_slots[emptySlot] = item;
        }

        return m_equippedIndex >= 0 ? m_equippedIndex : FirstOccupiedSlot();
    }

    private static bool Contains(IReadOnlyList<T> list, T item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (s_comparer.Equals(list[i], item))
            {
                return true;
            }
        }

        return false;
    }
}
