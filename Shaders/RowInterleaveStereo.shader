Shader "UniCAVE/RowInterleaveStereo"
{
    Properties
    {
        _LeftTex  ("Left Eye",  2D) = "black" {}
        _RightTex ("Right Eye", 2D) = "black" {}
        [Toggle] _SwapEyes ("Swap Eyes (flip even/odd assignment)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "RowInterleave"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LeftTex);
            SAMPLER(sampler_LeftTex);
            TEXTURE2D(_RightTex);
            SAMPLER(sampler_RightTex);
            float _SwapEyes;
            float4 _LeftTex_TexelSize;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                // Fullscreen triangle: 3 vertices cover the screen.
                // Flip UV.y: the eye RTs are rendered with GL.GetGPUProjectionMatrix (renderIntoTexture=true), which 
                // places world-up at the BOTTOM of the RT in texture memory. We flip the sampling UV so screen top 
                // (positionCS.y = +1) samples UV.y = 1 (RT bottom = world-up), giving a right-side-up image.
                o.uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
                o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Determine which eye this pixel row belongs to.
                // Screen-space Y pixel coordinate (integer row index).
                float pixelY = input.positionCS.y;
                float rowIndex = floor(pixelY);
                float isOdd = fmod(rowIndex, 2.0);

                // _SwapEyes flips the assignment (depends on panel polarizer orientation).
                float useRight = lerp(isOdd, 1.0 - isOdd, _SwapEyes);

                half4 leftColor  = SAMPLE_TEXTURE2D(_LeftTex,  sampler_LeftTex,  input.uv);
                half4 rightColor = SAMPLE_TEXTURE2D(_RightTex, sampler_RightTex, input.uv);

                return lerp(leftColor, rightColor, useRight);
            }
            ENDHLSL
        }
    }
}
