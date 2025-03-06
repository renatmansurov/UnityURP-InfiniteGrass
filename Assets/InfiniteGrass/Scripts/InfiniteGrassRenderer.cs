using UnityEngine;

[ExecuteAlways]
public class InfiniteGrassRenderer : MonoBehaviour
{
	public static InfiniteGrassRenderer Instance; //Global ref of the script
	private static readonly int CenterPos = Shader.PropertyToID("_CenterPos");
	private static readonly int DrawDistance = Shader.PropertyToID("_DrawDistance");
	private static readonly int TextureUpdateThreshold = Shader.PropertyToID("_TextureUpdateThreshold");

	[Header("Internal")] public Material grassMaterial;

	[Header("Grass Properties")] public float spacing = 0.5f; //Spacing between blades, Please don't make it too low
	public float drawDistance = 300;
	public float fullDensityDistance = 50; //After this distance, we start removing some blades of grass in sake of performance
	public int grassMeshSubdivision = 5; //How many sections you will have in your grass blade mesh, 0 will give a triangle, having more sections will make the wind animation and the curvature looks better
	public float textureUpdateThreshold = 10.0f; //The distance that the camera should move before we update the "Data Textures"

	[Header("Max Buffer Count (Millions)")]
	public float maxBufferCount = 2; //The number we gonna use to initialize the positions buffer
	//Don't make it too high cause that gonna impact performance, usually 2 - 3 should be enough unless you are using a crazy spacing
	//Also don't make it too low cause it's gonna negativly impact the performance

	[Header("Debug (Enabling this will make the performance drop a lot)")]
	public bool previewVisibleGrassCount;

	public Camera mainCamera;
	public ComputeBuffer ArgsBuffer;

	private Mesh cachedGrassMesh;

	int oldSubdivision = -1;
	public ComputeBuffer TemporalBuffer; //Just a temp buffer to preview the visible grass count

	private void Start()
	{
		mainCamera = Camera.main;
	}

	private void LateUpdate()
	{
		ReleaseBuffers();

		if (spacing == 0 || !grassMaterial) return;

		var cameraBounds = CalculateCameraBounds(mainCamera, drawDistance);
		var centerPos = CalculateCenterPosition(mainCamera, textureUpdateThreshold);

		InitializeBuffers();
		SetupMaterial(centerPos);

		Graphics.DrawMeshInstancedIndirect(GetGrassMeshCache(), 0, grassMaterial, cameraBounds, ArgsBuffer);
	}

	private void OnEnable()
	{
		Instance = this;
	}

	private void OnDisable()
	{
		Instance = null;

		ArgsBuffer?.Release();
		TemporalBuffer?.Release();
	}

	private void OnGUI()
	{
		if (previewVisibleGrassCount)
		{
			GUI.contentColor = Color.black;
			var style = new GUIStyle
			{
				fontSize = 25
			};

			var count = new uint[1];
			TemporalBuffer.GetData(count); //Reading back data from GPU

			//Recalculating the GridSize used for dispatching
			var cameraBounds = CalculateCameraBounds(mainCamera, drawDistance);
			var gridSize = new Vector2Int(Mathf.CeilToInt(cameraBounds.size.x / spacing), Mathf.CeilToInt(cameraBounds.size.z / spacing));

			GUI.Label(new Rect(50, 50, 400, 200), "Dispatch Size : " + gridSize.x + "x" + gridSize.y + " = " + (gridSize.x * gridSize.y), style);
			GUI.Label(new Rect(50, 80, 400, 200), "Visible Grass Count : " + count[0], style);
		}
	}

	private void ReleaseBuffers()
	{
		ArgsBuffer?.Release();
		TemporalBuffer?.Release();
	}

	private static Vector2 CalculateCenterPosition(Camera camera, float textureUpdateThreshold)
	{
		return new Vector2(
			Mathf.Floor(camera.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold,
			Mathf.Floor(camera.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold
		);
	}

	private void InitializeBuffers()
	{
		ArgsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
		TemporalBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);

		var args = new uint[5];
		args[0] = GetGrassMeshCache().GetIndexCount(0);
		args[1] = (uint)(maxBufferCount * 1000000);
		args[2] = GetGrassMeshCache().GetIndexStart(0);
		args[3] = GetGrassMeshCache().GetBaseVertex(0);
		args[4] = 0;
		ArgsBuffer.SetData(args);
	}

	private void SetupMaterial(Vector2 centerPos)
	{
		grassMaterial.SetVector(CenterPos, centerPos);
		grassMaterial.SetFloat(DrawDistance, drawDistance);
		grassMaterial.SetFloat(TextureUpdateThreshold, textureUpdateThreshold);
	}

	private Mesh GetGrassMeshCache()
	{
		if (cachedGrassMesh && oldSubdivision == grassMeshSubdivision) return cachedGrassMesh;

		cachedGrassMesh = new Mesh();
		var vertexCount = 3 + 4 * grassMeshSubdivision;
		var triangleCount = (1 + 2 * grassMeshSubdivision) * 3;

		var vertices = new Vector3[vertexCount];
		var triangles = new int[triangleCount];

		for (var i = 0; i < grassMeshSubdivision; i++)
		{
			var y1 = (float)i / (grassMeshSubdivision + 1);
			var y2 = (float)(i + 1) / (grassMeshSubdivision + 1);

			var baseIndex = i * 4;
			vertices[baseIndex] = new Vector3(-0.25f, y1);
			vertices[baseIndex + 1] = new Vector3(0.25f, y1);
			vertices[baseIndex + 2] = new Vector3(-0.25f, y2);
			vertices[baseIndex + 3] = new Vector3(0.25f, y2);

			var triBaseIndex = i * 6;
			triangles[triBaseIndex] = baseIndex;
			triangles[triBaseIndex + 1] = baseIndex + 3;
			triangles[triBaseIndex + 2] = baseIndex + 1;
			triangles[triBaseIndex + 3] = baseIndex;
			triangles[triBaseIndex + 4] = baseIndex + 2;
			triangles[triBaseIndex + 5] = baseIndex + 3;
		}

		var topVertexIndex = grassMeshSubdivision * 4;
		vertices[topVertexIndex] = new Vector3(-0.25f, (float)grassMeshSubdivision / (grassMeshSubdivision + 1));
		vertices[topVertexIndex + 1] = new Vector3(0, 1);
		vertices[topVertexIndex + 2] = new Vector3(0.25f, (float)grassMeshSubdivision / (grassMeshSubdivision + 1));

		var topTriBaseIndex = grassMeshSubdivision * 6;
		triangles[topTriBaseIndex] = topVertexIndex;
		triangles[topTriBaseIndex + 1] = topVertexIndex + 1;
		triangles[topTriBaseIndex + 2] = topVertexIndex + 2;

		cachedGrassMesh.SetVertices(vertices);
		cachedGrassMesh.SetTriangles(triangles, 0);

		oldSubdivision = grassMeshSubdivision;
		return cachedGrassMesh;
	}

	private static Bounds CalculateCameraBounds(Camera sourceCamera, float drawDistance)
	{
		var nearCorners = new[]
		{
			sourceCamera.ViewportToWorldPoint(new Vector3(0, 1, sourceCamera.nearClipPlane)),
			sourceCamera.ViewportToWorldPoint(new Vector3(1, 1, sourceCamera.nearClipPlane)),
			sourceCamera.ViewportToWorldPoint(new Vector3(0, 0, sourceCamera.nearClipPlane)),
			sourceCamera.ViewportToWorldPoint(new Vector3(1, 0, sourceCamera.nearClipPlane))
		};

		var farCorners = new[]
		{
			sourceCamera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance)),
			sourceCamera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance)),
			sourceCamera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance)),
			sourceCamera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance))
		};

		var startX = Mathf.Max(farCorners[0].x, farCorners[1].x, nearCorners[0].x, nearCorners[1].x, farCorners[2].x, farCorners[3].x, nearCorners[2].x, nearCorners[3].x);
		var endX = Mathf.Min(farCorners[0].x, farCorners[1].x, nearCorners[0].x, nearCorners[1].x, farCorners[2].x, farCorners[3].x, nearCorners[2].x, nearCorners[3].x);

		var startY = Mathf.Max(farCorners[0].y, farCorners[1].y, nearCorners[0].y, nearCorners[1].y, farCorners[2].y, farCorners[3].y, nearCorners[2].y, nearCorners[3].y);
		var endY = Mathf.Min(farCorners[0].y, farCorners[1].y, nearCorners[0].y, nearCorners[1].y, farCorners[2].y, farCorners[3].y, nearCorners[2].y, nearCorners[3].y);

		var startZ = Mathf.Max(farCorners[0].z, farCorners[1].z, nearCorners[0].z, nearCorners[1].z, farCorners[2].z, farCorners[3].z, nearCorners[2].z, nearCorners[3].z);
		var endZ = Mathf.Min(farCorners[0].z, farCorners[1].z, nearCorners[0].z, nearCorners[1].z, farCorners[2].z, farCorners[3].z, nearCorners[2].z, nearCorners[3].z);

		var center = new Vector3((startX + endX) / 2, (startY + endY) / 2, (startZ + endZ) / 2);
		var size = new Vector3(Mathf.Abs(startX - endX), Mathf.Abs(startY - endY), Mathf.Abs(startZ - endZ));

		var bounds = new Bounds(center, size);
		bounds.Expand(1);
		return bounds;
	}
}