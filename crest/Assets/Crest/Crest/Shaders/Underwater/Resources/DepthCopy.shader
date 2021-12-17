Shader "Hidden/Crest/Helpers/Depth"
{
	SubShader
	{
		Cull Off ZWrite On ZTest Always

		HLSLINCLUDE
		#pragma vertex Vertex

		#include "UnityCG.cginc"

		#include "../../Helpers/BIRP/Core.hlsl"
		#include "../../Helpers/BIRP/InputsDriven.hlsl"

		#include "../../OceanShaderHelpers.hlsl"

		struct Attributes
		{
			float4 positionOS : POSITION;
		};

		struct Varyings
		{
			float4 positionCS : SV_POSITION;
		};

		TEXTURE2D_X(_CameraDepthTexture);

		Varyings Vertex(Attributes input)
		{
			Varyings output;
			output.positionCS = UnityObjectToClipPos(input.positionOS);
			return output;
		}
		ENDHLSL

		Pass
		{
			Name "Copy Depth"

			HLSLPROGRAM
			#pragma fragment Fragment

			half4 Fragment(Varyings input, out float o_depth : SV_Depth) : SV_Target
			{
				o_depth = LOAD_DEPTH_TEXTURE_X(_CameraDepthTexture, input.positionCS.xy);
				return 1.0;
			}
			ENDHLSL
		}

		Pass
		{
			Name "Clear Depth Only"

			HLSLPROGRAM
			#pragma fragment Fragment

			half4 Fragment(Varyings input, out float o_depth : SV_Depth) : SV_Target
			{
				o_depth = 0.0;
				return 1.0;
			}
			ENDHLSL
		}
	}
}
