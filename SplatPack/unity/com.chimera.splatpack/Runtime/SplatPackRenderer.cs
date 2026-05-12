using System;
using Diagnostics = System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace SplatPack.Runtime
{
    public enum SplatPackSortMode
    {
        None,
        ChunkDepth,
        GpuDepthBucket,
        SplatDepth,
    }

    [DisallowMultipleComponent]
    public sealed class SplatPackRenderer : MonoBehaviour
    {
        private const int ProjectionThreadGroupSize = 64;
        private const int SortThreadGroupSize = 64;
        private const int MaxProjectedEyes = 2;
        private const float StandaloneSafeOpacityScale = 1f;
        private const float StandaloneSafeMaxRadiusPixels = 48f;
        private const float MinimumTailAlphaClip = 0.002f;

        [Header("Input")]
        [SerializeField] private SplatPackAsset asset;
        [SerializeField] private TextAsset packageBytes;

        [Header("Rendering")]
        [SerializeField] private Material splatMaterial;
        [SerializeField] private ComputeShader projectionCompute;
        [SerializeField] private ComputeShader sortCompute;
        [SerializeField] private Camera targetCamera;

        [Header("Placement")]
        [SerializeField] private bool recenterInFrontOfCameraOnFirstDraw;
        [SerializeField] private float recenterDistanceMeters = 3f;

        [Header("Quality")]
        [SerializeField] private float opacityScale = 1f;
        [SerializeField] private float opacityPower = 1.25f;
        [SerializeField] private float splatScale = 0.9f;
        [SerializeField] private float alphaClip = 0.002f;
        [SerializeField] private float minScreenRadiusPixels = 0.75f;
        [SerializeField] private float maxScreenRadiusPixels = 48f;
        [SerializeField] private float maxScreenEccentricity = 8f;
        [SerializeField] private float kernel2DSize = 0.05f;
        [SerializeField] private float eigenTermFloor = 1e-8f;
        [SerializeField] private bool enforceStandaloneRadiusCap = true;

        [Header("Sorting")]
        [SerializeField] private SplatPackSortMode sortMode = SplatPackSortMode.GpuDepthBucket;
        [SerializeField] private int sortIntervalFrames = 1;
        [SerializeField] private int xrSortIntervalFrames = 2;
        [SerializeField] private int depthSortBinCount = 8192;
        [SerializeField] private bool skipStableSortFrames = true;

        [Header("Temporal Cache")]
        [SerializeField] private bool reuseStableProjectionCache = true;
        [SerializeField] private float cacheTranslationThresholdMeters = 0.001f;
        [SerializeField] private float cacheRotationThresholdDegrees = 0.05f;

        [Header("Diagnostics")]
        [SerializeField] private bool logDiagnostics = true;
        [SerializeField] private bool readbackProjectionDiagnostics;
        [SerializeField] private float diagnosticIntervalSeconds = 5f;

        private SplatPackPackage package;
        private ComputeBuffer splatBuffer;
        private ComputeBuffer drawOrderBuffer;
        private ComputeBuffer projectedBuffer;
        private ComputeBuffer sortBinCountBuffer;
        private ComputeBuffer sortBinOffsetBuffer;
        private MaterialPropertyBlock propertyBlock;
        private int projectionKernel = -1;
        private int sortClearKernel = -1;
        private int sortCountKernel = -1;
        private int sortPrefixKernel = -1;
        private int sortFillKernel = -1;
        private int allocatedSortBinCount;
        private bool subscribed;
        private bool loggedFirstDiagnostics;
        private float nextDiagnosticLogTime;
        private float lastProjectionDispatchCpuMs;
        private int lastProjectionEyeCount;
        private string lastProjectionPath = "not-run";
        private uint[] drawOrderCpu;
        private ChunkSortItem[] chunkSortItems;
        private SplatSortItem[] splatSortItems;
        private int lastDrawOrderSortFrame = -1;
        private float lastDrawOrderSortCpuMs;
        private string lastSortPath = "not-run";
        private bool lastSortReused;
        private bool didRecenter;
        private bool warnedUnsupportedMaterial;
        private bool warnedGpuSortUnavailable;
        private bool hasProjectionCacheState;
        private Vector3 lastProjectionCameraPosition;
        private Quaternion lastProjectionCameraRotation = Quaternion.identity;
        private Matrix4x4 lastProjectionLocalToWorld = Matrix4x4.identity;
        private int lastProjectionWidth;
        private int lastProjectionHeight;
        private bool lastProjectionStereo;
        private float lastProjectionOpacityPower;
        private float lastProjectionSplatScale;
        private float lastProjectionMinRadius;
        private float lastProjectionMaxRadius;
        private float lastProjectionMaxEccentricity;
        private float lastProjectionKernel2DSize;
        private bool hasSortCameraState;
        private Vector3 lastSortCameraPosition;
        private Quaternion lastSortCameraRotation = Quaternion.identity;
        private Matrix4x4 lastSortLocalToWorld = Matrix4x4.identity;

        private struct ChunkSortItem
        {
            public int ChunkIndex;
            public float ViewDepth;
        }

        private struct SplatSortItem
        {
            public int SplatIndex;
            public float ViewDepth;
        }

        public void Configure(
            SplatPackAsset packageAsset,
            ComputeShader projection,
            Material material,
            Camera camera = null,
            bool recenterOnFirstDraw = false,
            ComputeShader sort = null)
        {
            asset = packageAsset;
            projectionCompute = projection;
            splatMaterial = material;
            targetCamera = camera;
            recenterInFrontOfCameraOnFirstDraw = recenterOnFirstDraw;
            sortCompute = sort;
            didRecenter = false;
        }

        private void OnEnable()
        {
            LoadPackage();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ReleaseBuffers();
        }

        public void LoadPackage()
        {
            ReleaseBuffers();

            if (asset != null)
            {
                package = asset.Load();
            }
            else if (packageBytes != null)
            {
                package = SplatPackLoader.Load(packageBytes.bytes);
            }
            else
            {
                package = null;
                return;
            }

            if (package.SplatCount == 0)
            {
                return;
            }

            didRecenter = false;
            splatBuffer = new ComputeBuffer(package.SplatCount, SplatPackFormat.SplatStride, ComputeBufferType.Structured);
            splatBuffer.SetData(package.Splats);

            drawOrderCpu = new uint[package.SplatCount];
            for (uint i = 0; i < drawOrderCpu.Length; i++)
            {
                drawOrderCpu[i] = i;
            }

            drawOrderBuffer = new ComputeBuffer(package.SplatCount, sizeof(uint), ComputeBufferType.Structured);
            drawOrderBuffer.SetData(drawOrderCpu);
            chunkSortItems = new ChunkSortItem[package.ChunkCount];
            splatSortItems = new SplatSortItem[package.SplatCount];
            lastDrawOrderSortFrame = -1;
            lastDrawOrderSortCpuMs = 0f;

            projectedBuffer = new ComputeBuffer(package.SplatCount * MaxProjectedEyes, SplatPackFormat.ProjectedSplatStride, ComputeBufferType.Structured);
            propertyBlock ??= new MaterialPropertyBlock();

            if (projectionCompute != null)
            {
                projectionKernel = projectionCompute.FindKernel("ProjectSplats");
            }

            ResetSortState();
            Debug.Log($"[SplatPack] Loaded package: splats={package.SplatCount}, chunks={package.ChunkCount}.");
        }

        private void Subscribe()
        {
            if (subscribed)
            {
                return;
            }

            RenderPipelineManager.endCameraRendering += HandleEndCameraRendering;
            Camera.onPostRender += HandleCameraPostRender;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed)
            {
                return;
            }

            RenderPipelineManager.endCameraRendering -= HandleEndCameraRendering;
            Camera.onPostRender -= HandleCameraPostRender;
            subscribed = false;
        }

        private void HandleEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!ShouldRender(camera))
            {
                return;
            }

            Draw(context, camera);
        }

        private void HandleCameraPostRender(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null || !ShouldRender(camera))
            {
                return;
            }

            Draw(camera);
        }

        private bool ShouldRender(Camera camera)
        {
            if (package == null || package.SplatCount == 0 || splatBuffer == null || drawOrderBuffer == null)
            {
                return false;
            }

            if (targetCamera != null)
            {
                return camera == targetCamera;
            }

            return camera != null && camera.cameraType == CameraType.Game;
        }

        private void Draw(ScriptableRenderContext context, Camera camera)
        {
            Material material = ResolveMaterial();
            if (material == null || !TryPrepare(camera, material))
            {
                return;
            }

            var command = new CommandBuffer { name = "SplatPack Procedural Splats" };
            try
            {
                command.DrawProcedural(
                    Matrix4x4.identity,
                    material,
                    0,
                    MeshTopology.Triangles,
                    package.SplatCount * 6,
                    ResolveProceduralDrawInstanceCount(camera),
                    propertyBlock);
                context.ExecuteCommandBuffer(command);
            }
            finally
            {
                command.Release();
            }

            MaybeLogDiagnostics(camera, "srp-end");
        }

        private void Draw(Camera camera)
        {
            Material material = ResolveMaterial();
            if (material == null || !TryPrepare(camera, material))
            {
                return;
            }

            var command = new CommandBuffer { name = "SplatPack Procedural Splats" };
            try
            {
                command.DrawProcedural(
                    Matrix4x4.identity,
                    material,
                    0,
                    MeshTopology.Triangles,
                    package.SplatCount * 6,
                    ResolveProceduralDrawInstanceCount(camera),
                    propertyBlock);
                Graphics.ExecuteCommandBuffer(command);
            }
            finally
            {
                command.Release();
            }

            MaybeLogDiagnostics(camera, "camera-post-command");
        }

        private bool TryPrepare(Camera camera, Material material)
        {
            if (projectionCompute == null || projectionKernel < 0 || projectedBuffer == null)
            {
                return false;
            }

            if (!material.shader.isSupported)
            {
                if (!warnedUnsupportedMaterial)
                {
                    warnedUnsupportedMaterial = true;
                    Debug.LogError(
                        "[SplatPack] Splat material shader is not supported by the current render pipeline or graphics API: "
                        + material.shader.name);
                }

                return false;
            }

            RecenterIfNeeded(camera);
            UpdateDrawOrder(camera);
            int eyeCount = DispatchProjectedSplats(camera);
            propertyBlock.Clear();
            propertyBlock.SetBuffer("_Splats", splatBuffer);
            propertyBlock.SetBuffer("_DrawOrder", drawOrderBuffer);
            propertyBlock.SetBuffer("_ProjectedSplats", projectedBuffer);
            propertyBlock.SetFloat("_OpacityScale", ResolveOpacityScale());
            propertyBlock.SetFloat("_SplatScale", ResolveSplatScale());
            propertyBlock.SetFloat("_AlphaClip", ResolveAlphaClip());
            propertyBlock.SetInt("_ProjectedSplatCacheEyeStride", package.SplatCount);
            propertyBlock.SetInt("_ProjectedSplatCacheEyeCount", eyeCount);
            ApplyMaterialProperties(material, eyeCount);
            return true;
        }

        private void ApplyMaterialProperties(Material material, int eyeCount)
        {
            material.SetBuffer("_Splats", splatBuffer);
            material.SetBuffer("_DrawOrder", drawOrderBuffer);
            material.SetBuffer("_ProjectedSplats", projectedBuffer);
            material.SetFloat("_OpacityScale", ResolveOpacityScale());
            material.SetFloat("_SplatScale", ResolveSplatScale());
            material.SetFloat("_AlphaClip", ResolveAlphaClip());
            material.SetFloat("_UseProjectedSplatCache", 1f);
            material.SetInt("_ProjectedSplatCacheEyeStride", package.SplatCount);
            material.SetInt("_ProjectedSplatCacheEyeCount", eyeCount);
        }

        private void UpdateDrawOrder(Camera camera)
        {
            if (sortMode == SplatPackSortMode.None
                || camera == null
                || package == null
                || drawOrderCpu == null
                || drawOrderBuffer == null
                || package.ChunkCount <= 0)
            {
                return;
            }

            int frame = Time.frameCount;
            if (skipStableSortFrames && hasSortCameraState && IsCachedCameraStateStable(camera, lastSortCameraPosition, lastSortCameraRotation, lastSortLocalToWorld))
            {
                lastSortReused = true;
                lastDrawOrderSortCpuMs = 0f;
                return;
            }

            int interval = ResolveSortIntervalFrames(camera);
            if (lastDrawOrderSortFrame >= 0 && frame - lastDrawOrderSortFrame < interval)
            {
                return;
            }

            Diagnostics.Stopwatch stopwatch = Diagnostics.Stopwatch.StartNew();
            Vector3 cameraPosition = camera.transform.position;
            Vector3 cameraForward = camera.transform.forward.normalized;
            bool uploadedCpuOrder = false;
            if (sortMode == SplatPackSortMode.GpuDepthBucket)
            {
                if (TryUpdateGpuDepthBucketOrder(camera))
                {
                    lastSortPath = $"gpu-depth-bucket/{allocatedSortBinCount}";
                }
                else
                {
                    UpdateChunkDepthOrder(cameraPosition, cameraForward);
                    uploadedCpuOrder = true;
                    lastSortPath = $"chunk-depth-fallback/{package.ChunkCount}";
                }
            }
            else if (sortMode == SplatPackSortMode.SplatDepth && splatSortItems != null)
            {
                UpdateSplatDepthOrder(cameraPosition, cameraForward);
                uploadedCpuOrder = true;
                lastSortPath = $"cpu-splat-depth/{package.SplatCount}";
            }
            else if (chunkSortItems != null)
            {
                UpdateChunkDepthOrder(cameraPosition, cameraForward);
                uploadedCpuOrder = true;
                lastSortPath = $"chunk-depth/{package.ChunkCount}";
            }

            if (uploadedCpuOrder)
            {
                drawOrderBuffer.SetData(drawOrderCpu);
            }

            stopwatch.Stop();
            lastDrawOrderSortFrame = frame;
            lastDrawOrderSortCpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            lastSortReused = false;
            RememberSortCameraState(camera);
        }

        private bool TryUpdateGpuDepthBucketOrder(Camera camera)
        {
            if (camera == null || package == null || package.SplatCount <= 1 || splatBuffer == null || drawOrderBuffer == null)
            {
                return false;
            }

            if (!EnsureGpuSortResources())
            {
                return false;
            }

            if (!TryResolveSortDepthRange(camera, out float depthMin, out float depthInvRange))
            {
                return false;
            }

            try
            {
                sortCompute.SetInt("_SplatCount", package.SplatCount);
                sortCompute.SetInt("_SortBinCount", allocatedSortBinCount);
                sortCompute.SetFloat("_DepthMin", depthMin);
                sortCompute.SetFloat("_DepthInvRange", depthInvRange);
                sortCompute.SetVector("_CameraPosition", camera.transform.position);
                sortCompute.SetVector("_CameraForward", camera.transform.forward.normalized);
                sortCompute.SetMatrix("_SplatLocalToWorld", transform.localToWorldMatrix);

                BindSortBuffers(sortClearKernel);
                BindSortBuffers(sortCountKernel);
                BindSortBuffers(sortPrefixKernel);
                BindSortBuffers(sortFillKernel);

                sortCompute.Dispatch(sortClearKernel, Mathf.CeilToInt(allocatedSortBinCount / (float)SortThreadGroupSize), 1, 1);
                sortCompute.Dispatch(sortCountKernel, Mathf.CeilToInt(package.SplatCount / (float)SortThreadGroupSize), 1, 1);
                sortCompute.Dispatch(sortPrefixKernel, 1, 1, 1);
                sortCompute.Dispatch(sortFillKernel, Mathf.CeilToInt(package.SplatCount / (float)SortThreadGroupSize), 1, 1);
                return true;
            }
            catch (Exception ex)
            {
                if (!warnedGpuSortUnavailable)
                {
                    warnedGpuSortUnavailable = true;
                    Debug.LogWarning("[SplatPack] GPU depth-bucket sort failed; falling back to chunk sorting. " + ex.Message);
                }

                return false;
            }
        }

        private bool EnsureGpuSortResources()
        {
            if (sortCompute == null)
            {
                sortCompute = Resources.Load<ComputeShader>("SplatPackDepthSort");
            }

            if (sortCompute == null)
            {
                if (!warnedGpuSortUnavailable)
                {
                    warnedGpuSortUnavailable = true;
                    Debug.LogWarning("[SplatPack] GPU depth-bucket sort is unavailable because no sort compute shader is assigned.");
                }

                return false;
            }

            if (sortClearKernel < 0 || sortCountKernel < 0 || sortPrefixKernel < 0 || sortFillKernel < 0)
            {
                try
                {
                    sortClearKernel = sortCompute.FindKernel("ClearBins");
                    sortCountKernel = sortCompute.FindKernel("CountBins");
                    sortPrefixKernel = sortCompute.FindKernel("PrefixBins");
                    sortFillKernel = sortCompute.FindKernel("FillDrawOrder");
                }
                catch (Exception ex)
                {
                    if (!warnedGpuSortUnavailable)
                    {
                        warnedGpuSortUnavailable = true;
                        Debug.LogWarning("[SplatPack] GPU depth-bucket sort kernel lookup failed; falling back to chunk sorting. " + ex.Message);
                    }

                    return false;
                }
            }

            int requestedBinCount = Mathf.Clamp(depthSortBinCount, 256, 16384);
            if (sortBinCountBuffer != null && allocatedSortBinCount == requestedBinCount)
            {
                return true;
            }

            ReleaseSortBuffers();
            allocatedSortBinCount = requestedBinCount;
            sortBinCountBuffer = new ComputeBuffer(allocatedSortBinCount, sizeof(uint), ComputeBufferType.Structured);
            sortBinOffsetBuffer = new ComputeBuffer(allocatedSortBinCount, sizeof(uint), ComputeBufferType.Structured);
            return true;
        }

        private void BindSortBuffers(int kernel)
        {
            sortCompute.SetBuffer(kernel, "_Splats", splatBuffer);
            sortCompute.SetBuffer(kernel, "_DrawOrder", drawOrderBuffer);
            sortCompute.SetBuffer(kernel, "_BinCounts", sortBinCountBuffer);
            sortCompute.SetBuffer(kernel, "_BinOffsets", sortBinOffsetBuffer);
        }

        private bool TryResolveSortDepthRange(Camera camera, out float depthMin, out float depthInvRange)
        {
            depthMin = 0f;
            depthInvRange = 1f;
            if (camera == null || package == null)
            {
                return false;
            }

            Vector3 forward = camera.transform.forward.normalized;
            Vector3 cameraPosition = camera.transform.position;
            Bounds bounds = ResolveDrawBounds();
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            if (extents.sqrMagnitude <= 1e-8f)
            {
                extents = Vector3.one * 0.5f;
            }

            float minDepth = float.PositiveInfinity;
            float maxDepth = float.NegativeInfinity;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                        float depth = Vector3.Dot(corner - cameraPosition, forward);
                        minDepth = Mathf.Min(minDepth, depth);
                        maxDepth = Mathf.Max(maxDepth, depth);
                    }
                }
            }

            if (float.IsNaN(minDepth)
                || float.IsNaN(maxDepth)
                || float.IsInfinity(minDepth)
                || float.IsInfinity(maxDepth))
            {
                return false;
            }

            float range = Mathf.Max(0.01f, maxDepth - minDepth);
            float padding = Mathf.Max(0.05f, range * 0.02f);
            depthMin = minDepth - padding;
            depthInvRange = 1f / (range + padding * 2f);
            return true;
        }

        private void UpdateChunkDepthOrder(Vector3 cameraPosition, Vector3 cameraForward)
        {
            for (int i = 0; i < package.Chunks.Length; i++)
            {
                SplatPackChunk chunk = package.Chunks[i];
                Vector3 localCenter = (chunk.BoundsMin + chunk.BoundsMax) * 0.5f;
                Vector3 worldCenter = transform.TransformPoint(localCenter);
                chunkSortItems[i] = new ChunkSortItem
                {
                    ChunkIndex = i,
                    ViewDepth = -Vector3.Dot(worldCenter - cameraPosition, cameraForward),
                };
            }

            Array.Sort(chunkSortItems, (a, b) => a.ViewDepth.CompareTo(b.ViewDepth));

            int cursor = 0;
            for (int sortedIndex = 0; sortedIndex < chunkSortItems.Length; sortedIndex++)
            {
                SplatPackChunk chunk = package.Chunks[chunkSortItems[sortedIndex].ChunkIndex];
                int end = Mathf.Min(package.SplatCount, chunk.SplatOffset + chunk.SplatCount);
                for (int splatIndex = chunk.SplatOffset; splatIndex < end; splatIndex++)
                {
                    drawOrderCpu[cursor++] = (uint)splatIndex;
                }
            }

            if (cursor < drawOrderCpu.Length)
            {
                for (int splatIndex = cursor; splatIndex < drawOrderCpu.Length; splatIndex++)
                {
                    drawOrderCpu[splatIndex] = (uint)splatIndex;
                }
            }
        }

        private void UpdateSplatDepthOrder(Vector3 cameraPosition, Vector3 cameraForward)
        {
            for (int i = 0; i < package.Splats.Length; i++)
            {
                Vector4 center = package.Splats[i].CenterWS;
                Vector3 localCenter = new Vector3(center.x, center.y, center.z);
                Vector3 worldCenter = transform.TransformPoint(localCenter);
                splatSortItems[i] = new SplatSortItem
                {
                    SplatIndex = i,
                    ViewDepth = -Vector3.Dot(worldCenter - cameraPosition, cameraForward),
                };
            }

            Array.Sort(splatSortItems, (a, b) => a.ViewDepth.CompareTo(b.ViewDepth));
            for (int i = 0; i < splatSortItems.Length; i++)
            {
                drawOrderCpu[i] = (uint)splatSortItems[i].SplatIndex;
            }
        }

        private int DispatchProjectedSplats(Camera camera)
        {
            Diagnostics.Stopwatch stopwatch = Diagnostics.Stopwatch.StartNew();
            int width;
            int height;
            ResolveProjectionViewport(camera, out width, out height);
            if (CanReuseProjectedSplats(camera, width, height))
            {
                lastProjectionPath = "cache";
                lastProjectionDispatchCpuMs = 0f;
                return Mathf.Max(1, lastProjectionEyeCount);
            }

            int groups = Mathf.CeilToInt(package.SplatCount / (float)ProjectionThreadGroupSize);

            projectionCompute.SetBuffer(projectionKernel, "_Splats", splatBuffer);
            projectionCompute.SetBuffer(projectionKernel, "_ProjectedSplats", projectedBuffer);
            projectionCompute.SetInt("_SplatCount", package.SplatCount);
            projectionCompute.SetFloat("_SplatScale", ResolveSplatScale());
            projectionCompute.SetFloat("_OpacityScale", ResolveOpacityScale());
            projectionCompute.SetFloat("_OpacityPower", ResolveOpacityPower());
            projectionCompute.SetFloat("_MinScreenRadiusPixels", Mathf.Max(0f, minScreenRadiusPixels));
            projectionCompute.SetFloat("_MaxScreenRadiusPixels", ResolveMaxScreenRadiusPixels());
            projectionCompute.SetFloat("_MaxScreenEccentricity", ResolveMaxScreenEccentricity());
            projectionCompute.SetFloat("_Kernel2DSize", Mathf.Max(0f, kernel2DSize));
            projectionCompute.SetFloat("_EigenTermFloor", Mathf.Max(0f, eigenTermFloor));
            projectionCompute.SetMatrix("_SplatLocalToWorld", transform.localToWorldMatrix);

            if (camera != null && camera.stereoEnabled)
            {
                DispatchEye(camera, Camera.StereoscopicEye.Left, 0, width, height, groups);
                DispatchEye(camera, Camera.StereoscopicEye.Right, package.SplatCount, width, height, groups);
                stopwatch.Stop();
                lastProjectionDispatchCpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
                lastProjectionEyeCount = 2;
                lastProjectionPath = "dispatch";
                RememberProjectionCacheState(camera, width, height, true);
                return 2;
            }

            Matrix4x4 worldToView = camera != null ? camera.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 projection = camera != null ? camera.projectionMatrix : Matrix4x4.identity;
            DispatchProjection(worldToView, GL.GetGPUProjectionMatrix(projection, false), 0, width, height, groups);
            stopwatch.Stop();
            lastProjectionDispatchCpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            lastProjectionEyeCount = 1;
            lastProjectionPath = "dispatch";
            RememberProjectionCacheState(camera, width, height, false);
            return 1;
        }

        private void DispatchEye(Camera camera, Camera.StereoscopicEye eye, int offset, int width, int height, int groups)
        {
            Matrix4x4 worldToView = camera.GetStereoViewMatrix(eye);
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), false);
            DispatchProjection(worldToView, projection, offset, width, height, groups);
        }

        private void DispatchProjection(Matrix4x4 worldToView, Matrix4x4 projection, int projectedOffset, int width, int height, int groups)
        {
            projectionCompute.SetInt("_ProjectedSplatOffset", projectedOffset);
            projectionCompute.SetMatrix("_WorldToView", worldToView);
            projectionCompute.SetMatrix("_ProjectionMatrix", projection);
            projectionCompute.SetMatrix("_ViewProjection", projection * worldToView);
            projectionCompute.SetVector("_CameraWorldPosition", ExtractCameraWorldPosition(worldToView));
            projectionCompute.SetVector("_ViewportSize", new Vector4(width, height, 1f / width, 1f / height));
            projectionCompute.Dispatch(projectionKernel, groups, 1, 1);
        }

        private float ResolveOpacityScale()
        {
            float value = Mathf.Max(0f, opacityScale);
            return enforceStandaloneRadiusCap ? Mathf.Min(value, StandaloneSafeOpacityScale) : value;
        }

        private float ResolveOpacityPower()
        {
            return Mathf.Max(0.01f, opacityPower);
        }

        private float ResolveSplatScale()
        {
            float value = Mathf.Max(0f, splatScale);
            return enforceStandaloneRadiusCap ? Mathf.Min(value, 1f) : value;
        }

        private float ResolveAlphaClip()
        {
            return Mathf.Max(enforceStandaloneRadiusCap ? MinimumTailAlphaClip : 0f, alphaClip);
        }

        private float ResolveMaxScreenRadiusPixels()
        {
            float value = Mathf.Max(minScreenRadiusPixels, maxScreenRadiusPixels);
            return enforceStandaloneRadiusCap ? Mathf.Min(value, StandaloneSafeMaxRadiusPixels) : value;
        }

        private float ResolveMaxScreenEccentricity()
        {
            float value = Mathf.Max(1f, maxScreenEccentricity);
            return enforceStandaloneRadiusCap ? Mathf.Min(value, 12f) : value;
        }

        private int ResolveSortIntervalFrames(Camera camera)
        {
            int interval = Mathf.Max(1, sortIntervalFrames);
            if ((camera != null && camera.stereoEnabled) || XRSettings.enabled || XRSettings.isDeviceActive)
            {
                interval = Mathf.Max(interval, xrSortIntervalFrames);
            }

            return interval;
        }

        private Material ResolveMaterial()
        {
            if (splatMaterial != null)
            {
                return splatMaterial;
            }

            Shader shader = Shader.Find("Chimera/SplatPack Gaussian Splat");
            if (shader == null)
            {
                return null;
            }

            splatMaterial = new Material(shader);
            return splatMaterial;
        }

        private void RecenterIfNeeded(Camera camera)
        {
            if (!recenterInFrontOfCameraOnFirstDraw || didRecenter || package == null)
            {
                return;
            }

            Camera viewCamera = camera != null ? camera : targetCamera != null ? targetCamera : Camera.main;
            if (viewCamera == null)
            {
                return;
            }

            Vector3 forward = viewCamera.transform.forward;
            Vector3 horizontalForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (horizontalForward.sqrMagnitude > 0.0001f)
            {
                forward = horizontalForward.normalized;
            }
            else if (forward.sqrMagnitude > 0.0001f)
            {
                forward.Normalize();
            }
            else
            {
                forward = Vector3.forward;
            }

            float radius = package.Bounds.extents.magnitude;
            float distance = Mathf.Max(0.5f, Mathf.Max(recenterDistanceMeters, radius * 1.25f));
            transform.position = viewCamera.transform.position + forward * distance - package.Bounds.center;
            transform.rotation = Quaternion.identity;
            didRecenter = true;
            ResetProjectionCacheState();
            hasSortCameraState = false;

            Debug.Log(
                "[SplatPack] Recentered sample in front of camera: "
                + $"camera={viewCamera.name}, distance={distance:0.###}m, "
                + $"packageCenter={package.Bounds.center}, rendererPosition={transform.position}.");
        }

        private Bounds ResolveDrawBounds()
        {
            if (package == null)
            {
                return new Bounds(transform.position, Vector3.one);
            }

            Vector3 localCenter = package.Bounds.center;
            Vector3 localExtents = package.Bounds.extents;
            Vector3 worldCenter = transform.TransformPoint(localCenter);
            Vector3 axisX = transform.TransformVector(localExtents.x, 0f, 0f);
            Vector3 axisY = transform.TransformVector(0f, localExtents.y, 0f);
            Vector3 axisZ = transform.TransformVector(0f, 0f, localExtents.z);
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(worldCenter, worldExtents * 2f);
        }

        private static int ResolveProceduralDrawInstanceCount(Camera camera)
        {
            return 1;
        }

        private static Vector3 ExtractCameraWorldPosition(Matrix4x4 worldToView)
        {
            Matrix4x4 viewToWorld = worldToView.inverse;
            return viewToWorld.GetColumn(3);
        }

        private static void ResolveProjectionViewport(Camera camera, out int width, out int height)
        {
            width = Mathf.Max(1, camera != null ? camera.pixelWidth : Screen.width);
            height = Mathf.Max(1, camera != null ? camera.pixelHeight : Screen.height);
            if (camera != null && camera.stereoEnabled && XRSettings.eyeTextureWidth > 0 && XRSettings.eyeTextureHeight > 0)
            {
                width = Mathf.Max(1, XRSettings.eyeTextureWidth);
                height = Mathf.Max(1, XRSettings.eyeTextureHeight);
            }
        }

        private void MaybeLogDiagnostics(Camera camera, string phase)
        {
            if (!logDiagnostics || package == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (loggedFirstDiagnostics && now < nextDiagnosticLogTime)
            {
                return;
            }

            loggedFirstDiagnostics = true;
            nextDiagnosticLogTime = now + Mathf.Max(0.5f, diagnosticIntervalSeconds);
            string stereoEye = camera != null ? camera.stereoActiveEye.ToString() : "None";
            Debug.Log(
                "[SplatPack] Draw submitted: "
                + $"phase={phase}, camera={camera?.name ?? "none"}, "
                + $"cameraType={camera?.cameraType}, stereo={camera != null && camera.stereoEnabled}, "
                + $"stereoEye={stereoEye}, xrEnabled={XRSettings.enabled}, xrActive={XRSettings.isDeviceActive}, "
                + $"eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, "
                + $"rendererPosition={transform.position}, "
                + $"projection={lastProjectionPath}/{lastProjectionDispatchCpuMs:0.###}ms-cpu/{Mathf.Max(1, lastProjectionEyeCount)}eye, "
                + $"sort={BuildSortDiagnostics()}, "
                + $"quality=opacity/{ResolveOpacityScale():0.###},opPow/{ResolveOpacityPower():0.###},scale/{ResolveSplatScale():0.###},maxR/{ResolveMaxScreenRadiusPixels():0.###},ecc/{ResolveMaxScreenEccentricity():0.###},clip/{ResolveAlphaClip():0.####}, "
                + BuildProjectionDiagnostics(camera)
                + $"splats={package.SplatCount}, chunks={package.ChunkCount}, "
                + $"vertices={package.SplatCount * 6 * ResolveProceduralDrawInstanceCount(camera)}.");
        }

        private string BuildSortDiagnostics()
        {
            return sortMode == SplatPackSortMode.None
                ? "off"
                : $"{lastSortPath}{(lastSortReused ? "/cache" : string.Empty)}/{lastDrawOrderSortCpuMs:0.###}ms-cpu";
        }

        private string BuildProjectionDiagnostics(Camera camera)
        {
            string cameraInfo = camera != null
                ? $"cameraPos={camera.transform.position}, cameraForward={camera.transform.forward}, "
                : string.Empty;
            if (!readbackProjectionDiagnostics)
            {
                return cameraInfo + "projectionReadback=off, ";
            }

            if (projectedBuffer == null || package == null || package.SplatCount <= 0)
            {
                return cameraInfo + "projected=unavailable, ";
            }

            try
            {
                var projected = new SplatPackProjectedSplat[package.SplatCount];
                projectedBuffer.GetData(projected, 0, 0, package.SplatCount);

                int validCount = 0;
                int firstValid = -1;
                float maxRadius = 0f;
                float maxAlpha = 0f;
                Vector2 firstNdc = Vector2.zero;
                float firstRadius = 0f;
                float firstAlpha = 0f;

                for (int i = 0; i < projected.Length; i++)
                {
                    SplatPackProjectedSplat splat = projected[i];
                    if (splat.Meta.z < 0.5f)
                    {
                        continue;
                    }

                    validCount++;
                    float alpha = splat.Color.w;
                    float radius = splat.Meta.y;
                    maxAlpha = Mathf.Max(maxAlpha, alpha);
                    maxRadius = Mathf.Max(maxRadius, radius);

                    if (firstValid < 0)
                    {
                        firstValid = i;
                        float invW = Mathf.Abs(splat.ClipCenter.w) > 1e-6f ? 1f / splat.ClipCenter.w : 0f;
                        firstNdc = new Vector2(splat.ClipCenter.x * invW, splat.ClipCenter.y * invW);
                        firstRadius = radius;
                        firstAlpha = alpha;
                    }
                }

                return cameraInfo
                    + $"validProjected={validCount}/{package.SplatCount}, "
                    + $"firstValid={firstValid}, firstNdc=({firstNdc.x:0.###},{firstNdc.y:0.###}), "
                    + $"firstRadius={firstRadius:0.###}, firstAlpha={firstAlpha:0.###}, "
                    + $"maxRadius={maxRadius:0.###}, maxAlpha={maxAlpha:0.###}, ";
            }
            catch (Exception ex)
            {
                return $"projectionReadback=failed:{ex.GetType().Name}, ";
            }
        }

        private void ReleaseBuffers()
        {
            splatBuffer?.Release();
            drawOrderBuffer?.Release();
            projectedBuffer?.Release();
            ReleaseSortBuffers();
            splatBuffer = null;
            drawOrderBuffer = null;
            projectedBuffer = null;
            drawOrderCpu = null;
            chunkSortItems = null;
            splatSortItems = null;
            ResetProjectionCacheState();
        }

        private void ReleaseSortBuffers()
        {
            sortBinCountBuffer?.Release();
            sortBinOffsetBuffer?.Release();
            sortBinCountBuffer = null;
            sortBinOffsetBuffer = null;
            allocatedSortBinCount = 0;
        }

        private void ResetSortState()
        {
            ReleaseSortBuffers();
            sortClearKernel = -1;
            sortCountKernel = -1;
            sortPrefixKernel = -1;
            sortFillKernel = -1;
            lastSortPath = "not-run";
            lastSortReused = false;
            warnedGpuSortUnavailable = false;
            hasSortCameraState = false;
        }

        private bool CanReuseProjectedSplats(Camera camera, int width, int height)
        {
            if (!reuseStableProjectionCache || !hasProjectionCacheState || camera == null)
            {
                return false;
            }

            if (width != lastProjectionWidth || height != lastProjectionHeight || camera.stereoEnabled != lastProjectionStereo)
            {
                return false;
            }

            if (!IsCachedCameraStateStable(camera, lastProjectionCameraPosition, lastProjectionCameraRotation, lastProjectionLocalToWorld))
            {
                return false;
            }

            return Mathf.Approximately(lastProjectionOpacityPower, ResolveOpacityPower())
                   && Mathf.Approximately(lastProjectionSplatScale, ResolveSplatScale())
                   && Mathf.Approximately(lastProjectionMinRadius, Mathf.Max(0f, minScreenRadiusPixels))
                   && Mathf.Approximately(lastProjectionMaxRadius, ResolveMaxScreenRadiusPixels())
                   && Mathf.Approximately(lastProjectionMaxEccentricity, ResolveMaxScreenEccentricity())
                   && Mathf.Approximately(lastProjectionKernel2DSize, Mathf.Max(0f, kernel2DSize));
        }

        private void RememberProjectionCacheState(Camera camera, int width, int height, bool stereo)
        {
            if (camera == null)
            {
                ResetProjectionCacheState();
                return;
            }

            hasProjectionCacheState = true;
            lastProjectionCameraPosition = camera.transform.position;
            lastProjectionCameraRotation = camera.transform.rotation;
            lastProjectionLocalToWorld = transform.localToWorldMatrix;
            lastProjectionWidth = width;
            lastProjectionHeight = height;
            lastProjectionStereo = stereo;
            lastProjectionOpacityPower = ResolveOpacityPower();
            lastProjectionSplatScale = ResolveSplatScale();
            lastProjectionMinRadius = Mathf.Max(0f, minScreenRadiusPixels);
            lastProjectionMaxRadius = ResolveMaxScreenRadiusPixels();
            lastProjectionMaxEccentricity = ResolveMaxScreenEccentricity();
            lastProjectionKernel2DSize = Mathf.Max(0f, kernel2DSize);
        }

        private void ResetProjectionCacheState()
        {
            hasProjectionCacheState = false;
            lastProjectionPath = "not-run";
        }

        private void RememberSortCameraState(Camera camera)
        {
            if (camera == null)
            {
                hasSortCameraState = false;
                return;
            }

            hasSortCameraState = true;
            lastSortCameraPosition = camera.transform.position;
            lastSortCameraRotation = camera.transform.rotation;
            lastSortLocalToWorld = transform.localToWorldMatrix;
        }

        private bool IsCachedCameraStateStable(
            Camera camera,
            Vector3 cachedPosition,
            Quaternion cachedRotation,
            Matrix4x4 cachedLocalToWorld)
        {
            if (camera == null)
            {
                return false;
            }

            float translationThreshold = Mathf.Max(0f, cacheTranslationThresholdMeters);
            float rotationThreshold = Mathf.Max(0f, cacheRotationThresholdDegrees);
            return Vector3.SqrMagnitude(camera.transform.position - cachedPosition) <= translationThreshold * translationThreshold
                   && Quaternion.Angle(camera.transform.rotation, cachedRotation) <= rotationThreshold
                   && MatrixApproximately(transform.localToWorldMatrix, cachedLocalToWorld, 1e-5f);
        }

        private static bool MatrixApproximately(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > epsilon)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
