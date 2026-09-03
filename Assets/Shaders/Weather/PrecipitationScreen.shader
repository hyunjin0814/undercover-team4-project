// 화면 강수 (#782) — 눈·비를 화면에 얹는다 (알파 블렌드. 애디티브는 밝은 하늘에서 묻혀 버렸다).
//
// 파티클 리그(WeatherSkyRig)를 대신한다. 어디에 뿌릴지를 배치로 풀지 않고, PrecipitationMask가
// 구운 "이 방향이 하늘에 열렸는가"로 픽셀마다 가린다 — 그래서 실내·창밖·기둥이 한 규칙으로 끝난다.
// 설계 근거: docs/superpowers/specs/2026-08-21-precipitation-shader-design.md
//
// 눈송이는 <b>월드 좌표의 셀 격자</b>에 놓고 픽셀마다 투영해 그린다 (#847). 화면 uv로 무늬를 짜면
// 카메라를 돌릴 때 통째로 따라 돌고, 좌표를 오프셋으로 밀어 보정하면 송이가 이동하는 대신 늘어난다.
// 월드에 놓으면 회전·이동·전진이 전부 저절로 맞는다 — 보정할 항이 없다.
//
Shader "Undercover/Weather/PrecipitationScreen"
{
    Properties
    {
        [HDR] _Tint ("색", Color) = (0.8, 0.85, 0.95, 1)
        _Cells ("칸 수 (밀도의 기준)", Float) = 40
        _Fall ("낙하 속도 (m/s)", Float) = 1.5
        _Streak ("줄기 길이 (1=점, 크면 선)", Float) = 8
        _Thickness ("굵기", Range(1, 40)) = 14
        _Occupancy ("칸이 채워질 확률", Range(0.02, 1)) = 0.35
        _Tilt ("기울기 (바람)", Range(-1, 1)) = 0.15
        _Drift ("흔들림 (눈)", Range(0, 0.5)) = 0
        _Layers ("겹 수", Range(1, 4)) = 3
        _Opacity ("전체 진하기", Range(0, 1)) = 0.6
        _CenterClear ("화면 중앙 비우기", Range(0, 1)) = 0.55
        _MaskCut ("마스크 경계 기준", Range(0.1, 0.9)) = 0.55
        _MaskSoft ("마스크 경계 부드러움", Range(0.01, 0.4)) = 0.10
        _FovH ("가로 시야각(라디안, 코드가 넣는다)", Float) = 1.6
        _NearDistance ("가장 가까운 겹의 거리(m)", Float) = 3
        _FarDistance ("가장 먼 겹의 거리(m)", Float) = 15
        _LandFade ("닿기 전 흐려지는 폭(m)", Float) = 2
    }

    SubShader
    {
        // 불투명·투명이 다 그려진 뒤 화면에 얹는다. 깊이를 쓰지 않으므로 정렬에 끼지 않는다.
        Tags { "RenderType" = "Overlay" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" }

        Pass
        {
            Name "Precipitation"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always           // 카메라 자식 쿼드라 깊이로 걸러질 이유가 없다
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // 눈이 벽·바닥에 닿아 사라지게 하려면 씬까지의 거리가 필요하다 (#847).
            // PC_RPAsset의 Require Depth Texture가 꺼지면 전부 먼 것으로 읽혀 종전처럼 그려진다.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // ⚠ 전역은 CBUFFER 밖에 둔다. Properties에 넣으면 머티리얼 상수 버퍼로 들어가
            // Shader.SetGlobalTexture/Float이 덮지 못한다 (PrecipitationMask가 전역으로 넣는다).
            TEXTURE2D(_PrecipMask);
            SAMPLER(sampler_PrecipMask);
            float _PrecipAmount;

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Cells;
                float _Fall;
                float _Streak;
                float _Thickness;
                float _Occupancy;
                float _Tilt;
                float _Drift;
                float _Layers;
                float _Opacity;
                float _CenterClear;
                float _MaskCut;
                float _MaskSoft;
                float _FovH;
                float _NearDistance;
                float _FarDistance;
                float _LandFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS; // 픽셀이 보는 월드 방향을 여기서 얻는다 (#847)
                output.uv = input.uv; // 쿼드가 화면을 꽉 채우므로 이 uv가 곧 화면 uv다
                return output;
            }

            float Hash31(float3 p)
            {
                p = frac(p * float3(123.34, 456.21, 789.13));
                p += dot(p, p.yzx + 45.32);
                return frac((p.x + p.y) * p.z);
            }

            float3 Hash33(float3 p)
            {
                return float3(Hash31(p + 0.13), Hash31(p + 7.71), Hash31(p + 19.3));
            }

            //
            // <b>어긋남에서 깊이 성분을 버린다.</b> 3차원 거리로 재면 껍질(거리 distance)이 송이를
            // 스치는 순간에만 보여 태반이 사라지고 움직일 때마다 튄다. 시선에 수직인 성분만 재면
            // 송이가 칸 안 어느 깊이에 있어도 제 위치에 그려진다.
            //
            // <b>방울은 머리에서 꼬리로 이어지는 선분이다</b> (#981). 동그라미를 화면 세로로 늘이면
            // 사선으로 내리는데 줄기만 수직이라 어긋나고, 늘어난 만큼 흐려져 얼룩처럼 보였다.
            // 꼬리를 실제 낙하의 반대 방향으로 뻗으면 바람·시점이 저절로 맞는다. _Streak이 1이면
            // 선분이 점으로 줄어 종전의 동그란 송이(눈)와 같다.
            //
            // 격자는 바람만큼 <b>비틀어</b> 둔다 — 방울이 칸의 세로축을 그대로 타고 내려가므로
            // 줄기가 칸을 옆으로 삐져나가지 않고, 위아래 두 칸만 훑으면 잘림이 없다.
            float Layer(
                float3 dir, float3 axisAzimuth, float3 axisElevation,
                float distance, float cells, float seed, float time
            )
            {
                // 칸 크기 — 그 거리에서 화면 폭을 cells로 나눈 길이. 거리에 비례하므로 화면에서 보이는
                // 칸 크기는 겹마다 같다(_Cells의 뜻이 종전과 같게 유지된다).
                float cellSize = 2.0 * distance * tan(_FovH * 0.5) / max(cells, 1.0);
                float3 world = _WorldSpaceCameraPos + dir * distance;

                // 격자를 시간에 따라 밀어 강수를 내린다 — 셀 안에서 되돌리면 방울이 제자리로 튄다.
                // x를 y만큼 비틀어(shear) 두면 바람이 불어도 낙하가 격자 세로축과 나란하다.
                float fallShift = time * _Fall / cellSize;
                float3 p = float3(
                    (world.x + _Tilt * world.y) / cellSize,
                    world.y / cellSize + fallShift,
                    world.z / cellSize
                );

                // 꼬리 방향 = 낙하의 반대. 시선과 나란할수록 화면에서 짧아 보인다(투영 단축).
                float3 tailWS = normalize(float3(-_Tilt, 1.0, 0.0));
                float2 tailProjected = float2(dot(tailWS, axisAzimuth), dot(tailWS, axisElevation));
                float foreshorten = length(tailProjected);
                float2 tailDir = foreshorten > 1e-4 ? tailProjected / foreshorten : float2(0.0, 1.0);

                float margin = 1.0 / max(_Thickness, 1.0); // 최대 반지름. 이만큼 안쪽에만 놓는다
                // 줄기 길이(칸 단위). 칸을 넘으면 두 칸 훑기로 못 잡으므로 1칸에서 자른다.
                float tailBase = min(2.0 * margin * max(_Streak - 1.0, 0.0), 1.0);

                float3 baseCell = floor(p);
                float best = 0;

                // 아래 칸에서 출발한 줄기가 이 칸까지 올라온다 — 두 칸을 봐야 경계에서 안 끊긴다
                [unroll]
                for (int j = 0; j < 2; j++)
                {
                    float3 cell = baseCell - float3(0.0, (float)j, 0.0);
                    if (Hash31(cell + seed * 7.13) > _Occupancy)
                        continue;

                    // 방울 반지름(칸 단위) — 크기를 흔들어 큰 것과 잔 것을 섞는다
                    float radius = lerp(0.55, 1.0, Hash31(cell + seed * 11.3 + 63.1)) * margin;
                    // 길이도 흔든다 — 전부 같은 길이면 무늬가 자로 잰 듯 보인다
                    float tail = min(tailBase * lerp(0.6, 1.4, Hash31(cell + seed * 5.17 + 21.7)), 1.0);

                    float3 offsetInCell = lerp(margin, 1.0 - margin, Hash33(cell + seed * 3.71));
                    float3 headP = cell + offsetInCell;

                    // 비틀고 밀어 둔 격자를 되돌려 머리의 월드 위치를 얻는다
                    float headY = (headP.y - fallShift) * cellSize;
                    float3 headWS = float3(
                        headP.x * cellSize - _Tilt * headY,
                        headY,
                        headP.z * cellSize
                    );

                    // 눈은 좌우로 흔들린다. 비는 _Drift가 0이라 이 항이 사라진다.
                    headWS.x += sin(time * 2.0 + offsetInCell.x * 6.2831) * _Drift * cellSize;

                    // 시선에 수직인 어긋남만 잰다 — 깊이는 버린다(위 주석 참고)
                    float3 offset = headWS - world;
                    float radial = dot(offset, dir);
                    float3 perpendicular = offset - radial * dir;

                    // 머리를 원점으로 둔 화면 좌표에서, 머리~꼬리 선분까지의 거리를 잰다
                    float2 fromHead = -float2(
                        dot(perpendicular, axisAzimuth),
                        dot(perpendicular, axisElevation)
                    );
                    float tailLength = tail * cellSize * foreshorten;
                    float along = tailLength > 1e-5
                        ? saturate(dot(fromHead, tailDir) / tailLength)
                        : 0.0;
                    float2 across = fromHead - tailDir * (along * tailLength);

                    // 꼬리로 갈수록 가늘고 옅어진다 — 끝이 0이라 칸 경계에서 잘려도 티가 안 난다
                    float width = radius * cellSize * lerp(1.0, 0.5, along);
                    float shape = saturate(1.0 - length(across) / max(width, 1e-5));
                    shape *= smoothstep(1.0, 0.3, along);

                    // 칸의 앞뒤 끝에서는 흐려 둔다 — 껍질이 칸을 지날 때 방울이 툭 나타나지 않게
                    float crossFade = saturate(1.0 - abs(radial) / cellSize);

                    best = max(best, shape * crossFade); // 겹치면 더 진한 쪽. 더하면 교차점만 튄다
                }

                // 화면에서 1픽셀 밑으로 가늘어지면 지운다 — 그대로 두면 픽셀 사이를 오가며 반짝인다.
                // 화면 폭이 _FovH를 담으므로 픽셀당 각도가 나온다. 거리는 약분된다(칸이 거리에 비례).
                float pixelRadius = 0.78 * margin * 2.0 * tan(_FovH * 0.5) / max(cells, 1.0)
                    * _ScreenParams.x / max(_FovH, 0.01);

                return best * smoothstep(0.6, 1.5, pixelRadius);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float amount = saturate(_PrecipAmount);
                if (amount <= 0.001)
                    return 0;

                // 이 픽셀이 보는 방향이 하늘에 열렸는가 — 실내·처마·기둥이 여기서 한 번에 걸러진다
                float open = SAMPLE_TEXTURE2D(_PrecipMask, sampler_PrecipMask, input.uv).r;

                // ⚠ <b>경계를 세운다.</b> 마스크는 저해상(칸 십여 개)이라 바이리니어로 늘리면 문 구멍이
                // 주변 실내 벽까지 번지고, 깊은 실내에서 밖을 볼 때 <b>강수가 실내에 들어온 것처럼</b>
                // 보인다. 중간값을 0/1로 밀어 번짐을 걷는다.
                open = smoothstep(_MaskCut - _MaskSoft, _MaskCut + _MaskSoft, open);

                float gate = amount * open;
                if (gate <= 0.001)
                    return 0;

                float3 dir = normalize(input.positionWS - _WorldSpaceCameraPos);

                // 화면 가로·세로에 대응하는 접선 — 송이의 어긋남을 이 둘로 갈라 잰다
                float3 axisAzimuth = normalize(float3(dir.z, 0.0, -dir.x) + 1e-6);
                float3 axisElevation = cross(dir, axisAzimuth);

                // 씬까지의 거리 — 눈송이가 무엇에 닿는지를 이 값으로 안다 (#847)
                float sceneDistance = LinearEyeDepth(
                    SampleSceneDepth(GetNormalizedScreenSpaceUV(input.positionCS)),
                    _ZBufferParams
                );

                float time = _Time.y;
                float sum = 0;
                int layers = (int)round(_Layers);

                [unroll(4)]
                for (int i = 0; i < layers; i++)
                {
                    // 겹마다 칸을 촘촘히 — 가까운 눈과 먼 눈이 갈려 깊이가 생긴다
                    float scale = 1.0 + i * 0.55;

                    // 촘촘하고 어두운 겹일수록 멀다 — 그 거리에 눈이 떠 있다고 친다
                    float far = layers > 1 ? (float)i / (float)(layers - 1) : 0.0;
                    float layerDistance = lerp(_NearDistance, _FarDistance, far);

                    // 씬이 이 겹보다 가까우면 눈은 그 뒤다 — 닿기 직전부터 흐려져 사라진다
                    float land = saturate((sceneDistance - layerDistance) / max(_LandFade, 0.01));

                    sum += Layer(
                        dir, axisAzimuth, axisElevation,
                        layerDistance, _Cells * scale, i + 1, time
                    ) * land / scale;
                }

                // <b>화면 중앙을 비운다</b> — 전면에 고르게 덮으면 세계의 날씨가 아니라 <b>렌즈에 묻은 것</b>
                // 처럼 보이고, 크로스헤어·표적이 있는 중앙까지 가려 플레이에 방해가 된다. 
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 fromCenter = float2((input.uv.x - 0.5) * aspect, input.uv.y - 0.5);
                float edge = saturate(length(fromCenter) / 0.7);
                float centerFade = lerp(1.0 - _CenterClear, 1.0, smoothstep(0.0, 1.0, edge));

                // 알파 블렌드 — 색은 그대로, 덮는 정도로 낸다. 배경이 밝아도 어두워도 읽힌다.
                return half4(_Tint.rgb, saturate(sum) * gate * _Opacity * centerFade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
