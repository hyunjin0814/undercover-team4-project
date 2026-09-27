using System;
using UnityEngine;

/// <summary>
/// 음성 신호에 사인파를 곱해 로봇 목소리를 만드는 링 모듈레이터 오디오 필터.
/// OnAudioFilterRead는 오디오 스레드에서 돌므로 Unity API를 호출하지 않는다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoiceRingModulator : MonoBehaviour
{
    private const double k_twoPi = 2d * Math.PI;

    private float m_carrierHz = 50f;

    private double m_phase;
    private double m_sampleRate = 48000d;

    private void Awake()
    {
        m_sampleRate = AudioSettings.outputSampleRate;
        if (m_sampleRate <= 0d)
            m_sampleRate = 48000d;
    }

    /// <summary>반송파 주파수를 설정한다 — 메인 스레드에서만 호출할 것.</summary>
    public void Configure(float carrierHz)
    {
        m_carrierHz = Mathf.Max(1f, carrierHz);
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (channels <= 0)
            return;

        double step = k_twoPi * m_carrierHz / m_sampleRate;

        for (int i = 0; i + channels <= data.Length; i += channels)
        {
            float modulator = (float)Math.Sin(m_phase);
            for (int c = 0; c < channels; c++)
                data[i + c] *= modulator;

            m_phase += step;
        }

        m_phase %= k_twoPi;
    }
}
