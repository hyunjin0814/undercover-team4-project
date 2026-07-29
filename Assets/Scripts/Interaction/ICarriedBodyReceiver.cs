using UnityEngine;

/// <summary>
/// 운반 중인 '기능 정지(Die) 몸'을 넘겨받는 상호작용 대상. (#365)
///
/// 운반 중 E는 원래 <b>내려놓기</b>가 최우선인데(PlayerInteractor), 그러면 부활 장치를 겨냥해도
/// 그 자리에 몸을 툭 떨어뜨리게 된다. 이 인터페이스를 단 대상만 그 선점을 앞지른다 —
/// "운반 중 아무 상호작용이나 우선"으로 열면 문·콘솔을 겨냥한 채 E를 눌렀을 때 내려놓기가 죽는다.
/// </summary>
public interface ICarriedBodyReceiver : IInteractable { }
