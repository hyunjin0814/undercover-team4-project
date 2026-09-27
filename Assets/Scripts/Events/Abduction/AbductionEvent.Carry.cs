using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// AbductionEvent의 포획 이후 파트 — 접수·호송·맨홀 결말·구조를 처리한다.
/// 호송 연출은 CarryEscortSequence를 함께 쓴다.
/// </summary>
public partial class AbductionEvent
{
    private void HandleAbductorDamaged(NpcController abductor, GameObject attacker)
    {
        ServerRepelAbductor(abductor);
    }

    private void HandleAbductorStunned(NpcController abductor, Transform threat)
    {
        ServerRepelAbductor(abductor);
    }

    /// <summary>표적이 납치 외 사유로 쓰러지면 납치를 중단한다.</summary>
    private void HandleVictimCauseChanged()
    {
        if (!HasServerAuthority)
            return;

        if (m_carryTarget == null)
        {
            if (m_chaseTarget == null)
                return;

            PlayerIncapacitation chaseIncap = m_chaseTarget.GetComponent<PlayerIncapacitation>();
            if (chaseIncap != null && chaseIncap.Cause == IncapacitationCause.Die)
            {
                Debug.Log($"[납치] 추격 무산 — 표적이 납치 밖의 사유로 사망: {m_chaseTarget.name}");
                ReleaseAllAbductors();
                Finish();
            }
            return;
        }

        if (m_descending || m_finishing)
            return;

        PlayerIncapacitation incap = m_carryTarget.GetComponent<PlayerIncapacitation>();
        if (incap == null || incap.Cause == IncapacitationCause.Abducted)
            return;

        Debug.Log($"[납치] 호송 중단 — {m_carryTarget.name}이 납치 밖의 사유로 쓰러졌다 ({incap.Cause})");

        ReleaseAllAbductors();
    }

    private void HandleAbductionCaught(NpcController catcher, Transform caught)
    {
        if (m_carryTarget != null || caught == null)
            return;

        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();

        if (incap != null && incap.IsIncapacitated)
            return;

        m_carryTarget = caught;

        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Abducted);

        PruneDead(m_abductors);
        foreach (NpcController abductor in m_abductors)
            abductor.Penalty.StartPenaltyConverge(caught);

        Debug.Log($"[납치] 포획 — {catcher.name} → {caught.name}");

        CarryToManholeAsync(caught).Forget();
    }

    private async UniTask CarryToManholeAsync(Transform caught)
    {
        Transform manholePoint = PickNearest(m_outskirtPoints, caught.position);
        AbductionManhole manhole = manholePoint != null
            ? manholePoint.GetComponentInChildren<AbductionManhole>()
            : null;

        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance, m_convergeTimeoutSeconds,
            m_carrierGap, m_arriveDistance, m_travelTimeoutSeconds);

        bool arrived = await CarryEscortSequence.RunAsync(
            caught, m_abductors, manholePoint, settings, destroyCancellationToken);

        if (!arrived)
        {
            Debug.Log("[납치] 호송 해체 — 그 자리에서 즉시 풀려난다");
            FinishRescued(caught);
            return;
        }

        if (!await OpenManholeAsync(caught, manhole))
        {
            FinishRescued(caught);
            return;
        }

        if (!await DescendAsync(caught, manholePoint, manhole))
        {
            FinishRescued(caught);
            return;
        }

        m_carryTarget = null;
        Finish();
    }

    /// <summary>맨홀 뚜껑을 열고 대기한다(마지막 구조 창). 끝까지 버티면 true, 구조·소실로 중단되면 false.</summary>
    private async UniTask<bool> OpenManholeAsync(Transform caught, AbductionManhole manhole)
    {
        if (manhole != null)
            manhole.ServerOpen();

        Debug.Log($"[납치] 맨홀 도착 — 뚜껑 열림 ({m_manholeOpenSeconds:F1}초, 납치범 {m_abductors.Count}명)");

        float deadline = Time.time + m_manholeOpenSeconds;

        while (true)
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(0.25), cancellationToken: destroyCancellationToken);

            if (caught == null)
                return false;

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
            {
                Debug.Log("[납치] 맨홀 앞에서 구조 성공 — 납치범이 남지 않았다");
                if (manhole != null)
                    manhole.ServerClose();
                return false;
            }

            if (Time.time >= deadline)
                return true;
        }
    }

    /// <summary>피해자와 납치범을 맨홀 아래로 내려 사라지게 한다. 데려갈 납치범이 남지 않으면 false.</summary>
    private async UniTask<bool> DescendAsync(
        Transform caught, Transform manholePoint, AbductionManhole manhole)
    {
        m_descending = true;

        PruneDead(m_abductors);
        if (caught == null || m_abductors.Count == 0)
        {
            if (manhole != null)
                manhole.ServerClose();
            DisposeAbductors();
            return false;
        }

        PlayerPenaltyView view = caught.GetComponent<PlayerPenaltyView>();
        if (view != null)
        {
            NpcController lead = m_abductors[0];
            NpcController mate = m_abductors.Count > 1 ? m_abductors[1] : lead;
            view.StartCarried(lead, mate);
        }

        for (int i = 0; i < m_abductors.Count; i++)
            m_abductors[i].SetFrozen(true);

        for (int i = 0; i < m_abductors.Count; i++)
            if (m_abductors[i].Agent != null && m_abductors[i].Agent.enabled)
                m_abductors[i].Agent.enabled = false;

        if (manholePoint != null)
        {
            for (int i = 0; i < m_abductors.Count; i++)
            {
                Transform body = m_abductors[i].transform;
                body.position = new Vector3(
                    manholePoint.position.x, body.position.y, manholePoint.position.z);
            }
        }

        if (view != null && manholePoint != null)
        {
            view.SetSpectatePivot(manholePoint.position);

            if (m_descendViewLeadSeconds > 0f)
            {
                await UniTask.Delay(
                    TimeSpan.FromSeconds(m_descendViewLeadSeconds),
                    cancellationToken: destroyCancellationToken);
            }
        }

        Debug.Log($"[납치] 맨홀 하강 — {m_descendDepth:F0}m 아래로 내려간다");

        float descended = 0f;
        while (descended < m_descendDepth)
        {
            await UniTask.Yield(destroyCancellationToken);

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
                break;

            float step = m_descendSpeed * Time.deltaTime;
            descended += step;

            for (int i = 0; i < m_abductors.Count; i++)
                m_abductors[i].transform.position += Vector3.down * step;
        }

        if (manhole != null)
            manhole.ServerClose();

        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null)
        {
            m_finishing = true;
            incap.ServerKillByBodyLost();

            PlayerHealth health = caught.GetComponent<PlayerHealth>();
            if (health != null && health.CurrentHp > 0)
                health.ModifyHp(-health.CurrentHp);

            m_finishing = false;
        }

        await UniTask.Delay(
            TimeSpan.FromSeconds(0.5), cancellationToken: destroyCancellationToken);

        if (view != null)
            view.StopCarried();

        Debug.Log("[납치] 하강 완료 — 맨홀 아래로 사라졌다");
        DisposeAbductors();
        return true;
    }

    private void FinishRescued(Transform caught)
    {
        ReleaseAllAbductors();

        m_carryTarget = null;

        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null && incap.Cause == IncapacitationCause.Abducted)
        {
            incap.Recover();

            Debug.Log($"[납치] 구조 성공 — {caught.name} 풀려남");
        }

        Finish();
    }

    private static Transform PickNearest(Transform[] points, Vector3 from)
    {
        if (points == null)
            return null;

        Transform nearest = null;
        float nearestSqr = float.MaxValue;

        for (int i = 0; i < points.Length; i++)
        {
            Transform point = points[i];
            if (point == null)
                continue;

            float sqr = (point.position - from).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = point;
            }
        }

        return nearest;
    }

    /// <summary>납치범 1명을 호송에서 떼어낸다(구조 진입점). 서버(또는 오프라인) 전용.</summary>
    public void ServerRepelAbductor(NpcController abductor)
    {
        if (!HasServerAuthority || abductor == null)
            return;

        if (m_descending)
            return;

        if (!m_abductors.Contains(abductor))
            return;

        ReleaseAbductor(abductor);
        Debug.Log($"[납치] 납치범 이탈 — {abductor.name}, 남은 {m_abductors.Count}명");
    }
}
