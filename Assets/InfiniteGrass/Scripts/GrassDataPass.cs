using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

class GrassDataPass : ScriptableRenderPass
{
	private readonly List<ShaderTagId> shaderTagsList = new();

	private RTHandle heightRT;
	private RTHandle heightDepthRT;
	private RTHandle maskRT;
	private RTHandle colorRT;
	private RTHandle slopeRT;

	private LayerMask heightMapLayer;
	private Material heightMapMat;
	private ComputeShader computeShader;

	public GrassDataPass(LayerMask heightMapLayer, Material heightMapMat, ComputeShader computeShader)
	{
		this.heightMapLayer = heightMapLayer;
		this.computeShader = computeShader;
		this.heightMapMat = heightMapMat;

		shaderTagsList.Add(new ShaderTagId("SRPDefaultUnlit"));
		shaderTagsList.Add(new ShaderTagId("UniversalForward"));
		shaderTagsList.Add(new ShaderTagId("UniversalForwardOnly"));
	}

	public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
	{
		var textureSize = 2048;
		var rtDesc = new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RGFloat, 0);
		var rtDescDepth = new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 32);
		var rtDescRFloat = new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 0);
		var rtDescARGB = new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.ARGBFloat, 0);

		RenderingUtils.ReAllocateHandleIfNeeded(ref heightRT, rtDesc, FilterMode.Bilinear);
		RenderingUtils.ReAllocateIfNeeded(ref heightDepthRT, rtDescDepth, FilterMode.Bilinear);
		RenderingUtils.ReAllocateHandleIfNeeded(ref maskRT, rtDescRFloat, FilterMode.Bilinear);
		RenderingUtils.ReAllocateHandleIfNeeded(ref colorRT, rtDescARGB, FilterMode.Bilinear);
		RenderingUtils.ReAllocateHandleIfNeeded(ref slopeRT, rtDescARGB, FilterMode.Bilinear);

		ConfigureTarget(heightRT, heightDepthRT);
		ConfigureClear(ClearFlag.All, Color.black);
	}

	ComputeBuffer grassPositionsBuffer;

	public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
	{
		if (!ValidateComponents()) return;

		var cmd = CommandBufferPool.Get();
		var renderer = InfiniteGrassRenderer.Instance;
		var camera = Camera.main;

		var cameraBounds = CalculateCameraBounds(camera, renderer.drawDistance);
		var centerPos = CalculateCenterPosition(camera, renderer.textureUpdateThreshold);

		SetupCamera(cmd, cameraBounds, centerPos, renderer);
		RenderHeightMap(cmd, context, renderingData, cameraBounds);
		RenderTextures(cmd, context, renderingData);
		ResetCamera(cmd, renderingData);
		SetupGrassBuffer(cmd, cameraBounds, renderer, camera, centerPos);

		context.ExecuteCommandBuffer(cmd);
		CommandBufferPool.Release(cmd);
	}

	private bool ValidateComponents()
	{
		return InfiniteGrassRenderer.Instance != null && heightMapMat != null && computeShader != null;
	}

	private static void SetupCamera(CommandBuffer cmd, Bounds cameraBounds, Vector2 centerPos, InfiniteGrassRenderer renderer)
	{
		var viewMatrix = Matrix4x4.TRS(new Vector3(centerPos.x, cameraBounds.max.y, centerPos.y), Quaternion.LookRotation(-Vector3.up), new Vector3(1, 1, -1)).inverse;
		var projectionMatrix = Matrix4x4.Ortho(-(renderer.drawDistance + renderer.textureUpdateThreshold), renderer.drawDistance + renderer.textureUpdateThreshold, -(renderer.drawDistance + renderer.textureUpdateThreshold),
			renderer.drawDistance + renderer.textureUpdateThreshold, 0, cameraBounds.size.y);
		cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
	}

	private void RenderHeightMap(CommandBuffer cmd, ScriptableRenderContext context, RenderingData renderingData, Bounds cameraBounds)
	{
		using (new ProfilingScope(cmd, new ProfilingSampler("Grass Height Map RT")))
		{
			context.ExecuteCommandBuffer(cmd);
			cmd.Clear();
			var drawSetting = CreateDrawingSettings(shaderTagsList, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
			heightMapMat.SetVector("_BoundsYMinMax", new Vector2(cameraBounds.min.y, cameraBounds.max.y));
			drawSetting.overrideMaterial = heightMapMat;
			var filterSetting = new FilteringSettings(RenderQueueRange.all, heightMapLayer);
			context.DrawRenderers(renderingData.cullResults, ref drawSetting, ref filterSetting);
		}
	}

	private void RenderTextures(CommandBuffer cmd, ScriptableRenderContext context, RenderingData renderingData)
	{
		RenderToTarget(cmd, context, renderingData, maskRT, "GrassMask");
		RenderToTarget(cmd, context, renderingData, colorRT, "GrassColor");
		RenderToTarget(cmd, context, renderingData, slopeRT, "GrassSlope");

		cmd.SetGlobalTexture("_GrassColorRT", colorRT);
		cmd.SetGlobalTexture("_GrassSlopeRT", slopeRT);
	}

	private void RenderToTarget(CommandBuffer cmd, ScriptableRenderContext context, RenderingData renderingData, RTHandle target, string shaderTag)
	{
		cmd.SetRenderTarget(target);
		cmd.ClearRenderTarget(true, true, Color.clear);

		using (new ProfilingScope(cmd, new ProfilingSampler($"Grass {shaderTag} RT")))
		{
			context.ExecuteCommandBuffer(cmd);
			cmd.Clear();

			var settings = CreateDrawingSettings(new ShaderTagId(shaderTag), ref renderingData, SortingCriteria.CommonTransparent);
			var filter = new FilteringSettings(RenderQueueRange.all);
			context.DrawRenderers(renderingData.cullResults, ref settings, ref filter);
		}
	}

	private void ResetCamera(CommandBuffer cmd, RenderingData renderingData)
	{
		cmd.SetViewProjectionMatrices(renderingData.cameraData.camera.worldToCameraMatrix, renderingData.cameraData.camera.projectionMatrix);
	}

	private void SetupGrassBuffer(CommandBuffer cmd, Bounds cameraBounds, InfiniteGrassRenderer renderer, Camera camera, Vector2 centerPos)
	{
		var gridSize = new Vector2Int(Mathf.CeilToInt(cameraBounds.size.x / renderer.spacing), Mathf.CeilToInt(cameraBounds.size.z / renderer.spacing));
		var gridStartIndex = new Vector2Int(Mathf.FloorToInt(cameraBounds.min.x / renderer.spacing), Mathf.FloorToInt(cameraBounds.min.z / renderer.spacing));

		grassPositionsBuffer?.Release();
		grassPositionsBuffer = new ComputeBuffer((int)(1000000 * renderer.maxBufferCount), sizeof(float) * 3, ComputeBufferType.Append);

		computeShader.SetMatrix("_VPMatrix", camera.projectionMatrix * camera.worldToCameraMatrix);
		computeShader.SetFloat("_FullDensityDistance", renderer.fullDensityDistance);
		computeShader.SetVector("_BoundsMin", cameraBounds.min);
		computeShader.SetVector("_BoundsMax", cameraBounds.max);
		computeShader.SetVector("_CameraPosition", camera.transform.position);
		computeShader.SetVector("_CenterPos", centerPos);
		computeShader.SetFloat("_DrawDistance", renderer.drawDistance);
		computeShader.SetFloat("_TextureUpdateThreshold", renderer.textureUpdateThreshold);
		computeShader.SetFloat("_Spacing", renderer.spacing);
		computeShader.SetVector("_GridStartIndex", (Vector2)gridStartIndex);
		computeShader.SetVector("_GridSize", (Vector2)gridSize);
		computeShader.SetBuffer(0, "_GrassPositions", grassPositionsBuffer);
		computeShader.SetTexture(0, "_GrassHeightMapRT", heightRT);
		computeShader.SetTexture(0, "_GrassMaskMapRT", maskRT);

		grassPositionsBuffer.SetCounterValue(0);

		cmd.DispatchCompute(computeShader, 0, Mathf.CeilToInt((float)gridSize.x / 8), Mathf.CeilToInt((float)gridSize.y / 8), 1);

		cmd.SetGlobalBuffer("_GrassPositions", grassPositionsBuffer);
		cmd.CopyCounterValue(grassPositionsBuffer, renderer.ArgsBuffer, 4);

		if (renderer.previewVisibleGrassCount)
		{
			cmd.CopyCounterValue(grassPositionsBuffer, renderer.TemporalBuffer, 0);
		}
	}

	private static Vector2 CalculateCenterPosition(Camera camera, float textureUpdateThreshold)
	{
		return new Vector2(
			Mathf.Floor(camera.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold,
			Mathf.Floor(camera.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold
		);
	}

	private static Bounds CalculateCameraBounds(Camera camera, float drawDistance)
	{
		Vector3[] corners =
		{
			// Near corners
			camera.ViewportToWorldPoint(new Vector3(0, 1, camera.nearClipPlane)),
			camera.ViewportToWorldPoint(new Vector3(1, 1, camera.nearClipPlane)),
			camera.ViewportToWorldPoint(new Vector3(0, 0, camera.nearClipPlane)),
			camera.ViewportToWorldPoint(new Vector3(1, 0, camera.nearClipPlane)),
			// Far corners
			camera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance)),
			camera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance)),
			camera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance)),
			camera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance))
		};

		var min = new Vector3(
			corners.Min(c => c.x),
			corners.Min(c => c.y),
			corners.Min(c => c.z)
		);

		var max = new Vector3(
			corners.Max(c => c.x),
			corners.Max(c => c.y),
			corners.Max(c => c.z)
		);

		var bounds = new Bounds((min + max) * 0.5f, max - min);
		bounds.Expand(1);
		return bounds;
	}

	public void Dispose()
	{
		heightRT?.Release();
		heightDepthRT?.Release();
		maskRT?.Release();
		colorRT?.Release();
		slopeRT?.Release();
		grassPositionsBuffer?.Release();
	}
}