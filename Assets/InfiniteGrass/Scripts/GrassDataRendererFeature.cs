using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class GrassDataRendererFeature : ScriptableRendererFeature
{
	[SerializeField] private LayerMask heightMapLayer;
	[SerializeField] private Material heightMapMat;
	[SerializeField] private ComputeShader computeShader;

	GrassDataPass grassDataPass;

	public override void Create()
	{
		grassDataPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader);
		grassDataPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		renderer.EnqueuePass(grassDataPass);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			grassDataPass.Dispose();
		}
	}

	[Serializable]
	public class GrassSettings
	{
		public LayerMask heightMapLayer;
		public Material heightMapMat;
		public ComputeShader computeShader;
	}

	public class GrassData : ContextItem
	{
		public TextureHandle FilterTextureHandle;

		public override void Reset()
		{
			FilterTextureHandle = TextureHandle.nullHandle;
		}
	}


}