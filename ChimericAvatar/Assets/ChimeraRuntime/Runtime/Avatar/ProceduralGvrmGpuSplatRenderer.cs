using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Chimera.Runtime
{
    public enum GvrmSplatRuntimePath
    {
        ReferenceVertexShader,
        VertexSkinningCache,
        ProjectedSplatCache
    }

    [DisallowMultipleComponent]
    public sealed class ProceduralGvrmGpuSplatRenderer : MonoBehaviour, IGvrmRenderBackend
    {
        private const float ShC0 = 0.28209479177387814f;
        private const int ProceduralSplatStride = 24 * sizeof(float);
        private const int SphericalHarmonicsSplatStride = 60 * sizeof(float);
        private const int SplatSkinningInputStride = 9 * 4 * sizeof(float);
        private const int VertexSkinningInputStride = 3 * 4 * sizeof(float);
        private const int VertexSkinningStateStride = 7 * 4 * sizeof(float);
        private const int ProjectedSplatStride = 20 * sizeof(float);
        private const int BoneMatrixStride = 16 * sizeof(float);
        private const int SkinningThreadGroupSize = 64;
        private const int ProjectionThreadGroupSize = 64;
        private const int ProjectedSplatCacheMaxEyeCount = 2;
        private const int SortThreadGroupSize = 64;
        private const int CullingThreadGroupSize = 128;
        private const int IndirectArgsCount = 4;

        [SerializeField] private Material splatMaterial;
        [SerializeField] private ComputeShader skinningComputeShader;
        [SerializeField] private ComputeShader projectionComputeShader;
        [SerializeField] private ComputeShader sortComputeShader;
        [SerializeField] private ComputeShader cullingComputeShader;
        [SerializeField] private Camera viewCamera;
        [SerializeField] private GvrmSplatRuntimePath runtimePath = GvrmSplatRuntimePath.ProjectedSplatCache;
        [SerializeField] private bool followSourceSkinning = true;
        [SerializeField] private bool preferGpuSkinning = true;
        [SerializeField] private bool preferGpuDepthBucketSort = true;
        [SerializeField] private bool preferGpuVisibleCulling = true;
        [SerializeField] private bool disableIndirectVisibleDrawInXr = true;
        [SerializeField] private bool useXrPerformanceBudget = true;
        [SerializeField, Range(1, 4)] private int xrSortEveryNthFrame = 2;
        [SerializeField] private bool xrThrottleSkinningDrivenSorts = true;
        [SerializeField] private int xrDepthSortBinCap = 4096;
        [SerializeField] private float xrSortCameraPositionEpsilonMeters = 0.01f;
        [SerializeField] private float xrSortCameraAngleEpsilonDegrees = 0.25f;
        [SerializeField] private bool logPeriodicDrawDiagnostics;
        [SerializeField] private int depthSortBinCount = 4096;
        [SerializeField, Range(0f, 1f)] private float cullingFrustumPadding = 0.18f;
        [SerializeField, Range(0f, 2f)] private float cullingMinScreenRadiusPixels;
        [SerializeField] private bool skipStableSkinningFrames = true;
        [SerializeField] private bool skipStableSortFrames = true;
        [SerializeField] private float skinningMatrixChangeEpsilon = 0.000001f;
        [SerializeField] private float sortCameraPositionEpsilonMeters = 0.001f;
        [SerializeField] private float sortCameraAngleEpsilonDegrees = 0.03f;
        [SerializeField] private bool sortBackToFront = true;
        [SerializeField] private bool useViewDependentSphericalHarmonics = true;
        [SerializeField] private float opacityScale = 1f;
        [SerializeField] private float splatScale = 1f;
        [SerializeField] private float alphaClip = 0f;
        [SerializeField] private float minScreenRadiusPixels = 0f;
        [SerializeField] private float maxScreenRadiusPixels = 1024f;
        [SerializeField] private float kernel2DSize = 0.3f;
        [SerializeField] private float antialiasOpacityCompensation = 0f;
        [SerializeField] private float eigenTermFloor = 0.1f;
        [SerializeField] private float opacityPower = 1f;
        [SerializeField] private float opacityDensity = 1f;
        [SerializeField] private float sphericalHarmonicsScale = 1f;
        [SerializeField, Range(0f, 1f)] private float surfaceContinuity = 0.3f;
        [SerializeField, Range(0f, 2f)] private float surfaceContinuityKernelPixels = 0.45f;
        [SerializeField, Range(0f, 1f)] private float surfaceContinuityShStability = 0.15f;
        [SerializeField] private bool quantizeColorLikeGs3d = true;
        [SerializeField] private bool linearizeColorInLinearProjects = false;
        [SerializeField] private bool preserveStaticRestPose = false;
        [SerializeField] private bool matchWebCovarianceQuaternionYFlip = true;

        private Material runtimeMaterial;
        private MaterialPropertyBlock propertyBlock;
        private ComputeBuffer splatBuffer;
        private ComputeBuffer sphericalHarmonicsBuffer;
        private ComputeBuffer drawOrderBuffer;
        private ComputeBuffer skinningInputBuffer;
        private ComputeBuffer vertexSkinningInputBuffer;
        private ComputeBuffer vertexSkinningStateBuffer;
        private ComputeBuffer projectedSplatBuffer;
        private ComputeBuffer projectionDummySphericalHarmonicsBuffer;
        private ComputeBuffer currentBoneMatrixBuffer;
        private ComputeBuffer restSkinInverseMatrixBuffer;
        private ComputeBuffer sortBinCountBuffer;
        private ComputeBuffer sortBinOffsetBuffer;
        private ComputeBuffer visibleDrawOrderBuffer;
        private ComputeBuffer visibleFlagBuffer;
        private ComputeBuffer cullBlockCountBuffer;
        private ComputeBuffer cullBlockOffsetBuffer;
        private ComputeBuffer indirectArgsBuffer;
        private GaussianSplatStream splatStream;
        private SkinnedMeshRenderer sourceSkinnedMesh;
        private GvrmSkinnedSplatBinding skinningBinding;
        private Matrix4x4 gsLocalToRenderer = Matrix4x4.identity;
        private GvrmRenderBudget budget;
        private int framesUntilSort;

        private int[] splatIndices = Array.Empty<int>();
        private int[] sortOrder = Array.Empty<int>();
        private uint[] drawOrderUpload = Array.Empty<uint>();
        private float[] sortKeys = Array.Empty<float>();
        private ProceduralSplat[] splats = Array.Empty<ProceduralSplat>();
        private SplatSkinningInput[] skinningInputs = Array.Empty<SplatSkinningInput>();
        private VertexSkinningInput[] vertexSkinningInputs = Array.Empty<VertexSkinningInput>();
        private SphericalHarmonicsSplat[] sphericalHarmonics = Array.Empty<SphericalHarmonicsSplat>();
        private Vector3[] centersWorld = Array.Empty<Vector3>();
        private Vector3[] axis0World = Array.Empty<Vector3>();
        private Vector3[] axis1World = Array.Empty<Vector3>();
        private Vector3[] axis2World = Array.Empty<Vector3>();
        private Vector3[] axis0GsLocal = Array.Empty<Vector3>();
        private Vector3[] axis1GsLocal = Array.Empty<Vector3>();
        private Vector3[] axis2GsLocal = Array.Empty<Vector3>();
        private Vector3[] relativePositionsMeshLocal = Array.Empty<Vector3>();
        private Vector3[] skinnedCentersWorld = Array.Empty<Vector3>();
        private Vector3[] meshPositions = Array.Empty<Vector3>();
        private BoneWeight[] boneWeights = Array.Empty<BoneWeight>();
        private Matrix4x4[] restBoneMatrices = Array.Empty<Matrix4x4>();
        private Matrix4x4[] currentBoneMatrices = Array.Empty<Matrix4x4>();
        private Matrix4x4[] restSkinInverseMatrices = Array.Empty<Matrix4x4>();
        private GpuMatrix4x4[] currentBoneMatricesGpu = Array.Empty<GpuMatrix4x4>();
        private GpuMatrix4x4[] restSkinInverseMatricesGpu = Array.Empty<GpuMatrix4x4>();
        private Matrix4x4 gsRotation = Matrix4x4.identity;
        private Matrix4x4 gsRotationInverse = Matrix4x4.identity;
        private Bounds drawBounds = new Bounds(Vector3.zero, Vector3.one);
        private bool splatBufferDirty;
        private bool drawOrderDirty;
        private bool gpuSkinningUnavailable;
        private bool gpuProjectionUnavailable;
        private bool gpuSortUnavailable;
        private bool gpuCullingUnavailable;
        private bool visibleDrawReady;
        private bool hasCapturedBoneFrame;
        private bool hasUploadedSkinningFrame;
        private bool hasSortCameraState;
        private bool hasCullCameraState;
        private Matrix4x4 lastRendererLocalToWorld = Matrix4x4.identity;
        private Matrix4x4 lastSourceMeshLocalToWorld = Matrix4x4.identity;
        private Vector3 lastSortCameraPosition;
        private Quaternion lastSortCameraRotation = Quaternion.identity;
        private Vector3 lastCullCameraPosition;
        private Quaternion lastCullCameraRotation = Quaternion.identity;
        private int activeSphericalHarmonicsDegree;
        private int vertexSkinningKernel = -1;
        private int skinningKernel = -1;
        private int projectionKernel = -1;
        private int sortClearKernel = -1;
        private int sortCountKernel = -1;
        private int sortPrefixKernel = -1;
        private int sortFillKernel = -1;
        private int cullClearKernel = -1;
        private int cullMarkKernel = -1;
        private int cullPrefixKernel = -1;
        private int cullCompactKernel = -1;
        private int allocatedSortBinCount;
        private int allocatedCullBlockCount;
        private bool warnedMissingMaterial;
        private bool loggedFirstDraw;
        private int lastDrawFrame = -1;
        private string lastDrawCameraName = "none";
        private string lastDrawPath = "none";
        private string lastDrawSkipReason = "not-rendered-yet";
        private string lastLoggedDrawSignature = string.Empty;
        private float nextDrawDiagnosticLogTime;
        private bool lastProjectedSplatCacheUsed;
        private int lastProjectedSplatCacheEyeCount;
        private float lastProjectionDispatchCpuMs;

        public string BackendName => "Procedural GVRM GPU Splat";
        public bool IsReady => splatBuffer != null && drawOrderBuffer != null && RenderedSplatCount > 0;
        public int RenderedSplatCount { get; private set; }
        public bool IsGpuSkinningActive => UseGpuSkinning;
        public bool IsGpuDepthBucketSortActive => UseGpuDepthBucketSort;
        public bool IsGpuVisibleCullingActive => UseGpuVisibleCulling;
        public bool IsIndirectVisibleDrawActive => ShouldUseIndirectVisibleDraw();
        public int ActiveSphericalHarmonicsDegree => activeSphericalHarmonicsDegree;
        public int AllocatedSortBinCount => allocatedSortBinCount;
        public int AllocatedCullBlockCount => allocatedCullBlockCount;
        public string RuntimeDiagnostics =>
            $"path={runtimePath}, splats={RenderedSplatCount}, sh={activeSphericalHarmonicsDegree}, " +
            $"skinning={(HasSkinning ? (UseGpuSkinning ? $"gpu-vertex-cache/{meshPositions.Length}" : "cpu") : "off")}, " +
            $"projection={ProjectionDiagnostics}, " +
            $"sort={(UseGpuDepthBucketSort ? $"gpu-bucket/{allocatedSortBinCount}" : "cpu")}, " +
            $"sortEvery={EffectiveSortEveryNthFrame}, xrPerf={(UseXrPerformanceBudget ? "on" : "off")}, " +
            $"cull={(UseGpuVisibleCulling ? $"gpu-indirect/{allocatedCullBlockCount}" : "off")}, " +
            $"draw={(ShouldUseIndirectVisibleDraw() ? "indirect-visible" : IsXrRenderingActive() ? "full-xr-safe" : "full")}, " +
            $"lastDraw={lastDrawPath}@{lastDrawCameraName}/f{lastDrawFrame}, skip={lastDrawSkipReason}";

        private string ProjectionDiagnostics =>
            lastProjectedSplatCacheUsed
                ? $"gpu-cache/{lastProjectionDispatchCpuMs:0.###}ms-cpu/{Mathf.Max(1, lastProjectedSplatCacheEyeCount)}eye"
                : "vertex-shader";

        public static bool IsSupportedOnCurrentDevice(out string reason)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                reason = "compute/structured buffers are not supported by the active graphics device.";
                return false;
            }

            if (SystemInfo.graphicsShaderLevel < 45)
            {
                reason = $"shader model 4.5 is required, current shader level is {SystemInfo.graphicsShaderLevel}.";
                return false;
            }

            switch (SystemInfo.graphicsDeviceType)
            {
                case GraphicsDeviceType.Null:
                case GraphicsDeviceType.OpenGLES3:
                    reason = $"{SystemInfo.graphicsDeviceType} is not a supported target for the procedural splat backend; use Vulkan, Metal, or D3D11+.";
                    return false;
                default:
                    reason = string.Empty;
                    return true;
            }
        }

        private bool HasSkinning => followSourceSkinning
                                    && sourceSkinnedMesh != null
                                    && skinningBinding != null
                                    && meshPositions.Length > 0
                                    && boneWeights.Length == meshPositions.Length
                                    && restBoneMatrices.Length > 0;

        private bool UseGpuSkinning => preferGpuSkinning
                                       && !gpuSkinningUnavailable
                                       && HasSkinning
                                       && skinningComputeShader != null
                                       && vertexSkinningKernel >= 0
                                       && skinningKernel >= 0
                                       && skinningInputBuffer != null
                                       && vertexSkinningInputBuffer != null
                                       && vertexSkinningStateBuffer != null
                                       && currentBoneMatrixBuffer != null
                                       && restSkinInverseMatrixBuffer != null;

        private bool UseGpuDepthBucketSort => preferGpuDepthBucketSort
                                              && sortBackToFront
                                              && !gpuSortUnavailable
                                              && sortComputeShader != null
                                              && sortClearKernel >= 0
                                              && sortCountKernel >= 0
                                              && sortPrefixKernel >= 0
                                              && sortFillKernel >= 0
                                              && splatBuffer != null
                                              && drawOrderBuffer != null
                                              && sortBinCountBuffer != null
                                              && sortBinOffsetBuffer != null;

        private bool UseGpuVisibleCulling => preferGpuVisibleCulling
                                             && !ShouldDisableIndirectVisibleDrawInXr()
                                             && !gpuCullingUnavailable
                                             && cullingComputeShader != null
                                             && cullClearKernel >= 0
                                             && cullMarkKernel >= 0
                                             && cullCompactKernel >= 0
                                             && splatBuffer != null
                                             && drawOrderBuffer != null
                                             && visibleDrawOrderBuffer != null
                                             && visibleFlagBuffer != null
                                             && cullBlockCountBuffer != null
                                             && cullBlockOffsetBuffer != null
                                             && indirectArgsBuffer != null;

        private bool UseProjectedSplatCache => runtimePath == GvrmSplatRuntimePath.ProjectedSplatCache
                                               && !gpuProjectionUnavailable
                                               && projectionComputeShader != null
                                               && projectionKernel >= 0
                                               && projectedSplatBuffer != null;

        private static bool IsXrRenderingActive()
        {
            return XRSettings.enabled
                   || XRSettings.isDeviceActive
                   || InputDevices.GetDeviceAtXRNode(XRNode.Head).isValid;
        }

        private bool ShouldDisableIndirectVisibleDrawInXr()
        {
            return disableIndirectVisibleDrawInXr && IsXrRenderingActive();
        }

        private bool UseXrPerformanceBudget => useXrPerformanceBudget && IsXrRenderingActive();

        private int EffectiveSortEveryNthFrame
        {
            get
            {
                var sortEvery = budget.SortEveryNthFrame;
                if (sortEvery <= 0 || !UseXrPerformanceBudget)
                {
                    return sortEvery;
                }

                return Mathf.Max(sortEvery, Mathf.Max(1, xrSortEveryNthFrame));
            }
        }

        private int EffectiveDepthSortBinCount
        {
            get
            {
                var requested = Mathf.Clamp(depthSortBinCount, 256, 8192);
                if (!UseXrPerformanceBudget)
                {
                    return requested;
                }

                return Mathf.Min(requested, Mathf.Clamp(xrDepthSortBinCap, 256, 8192));
            }
        }

        private float EffectiveSortCameraPositionEpsilonMeters =>
            UseXrPerformanceBudget
                ? Mathf.Max(sortCameraPositionEpsilonMeters, xrSortCameraPositionEpsilonMeters)
                : sortCameraPositionEpsilonMeters;

        private float EffectiveSortCameraAngleEpsilonDegrees =>
            UseXrPerformanceBudget
                ? Mathf.Max(sortCameraAngleEpsilonDegrees, xrSortCameraAngleEpsilonDegrees)
                : sortCameraAngleEpsilonDegrees;

        private bool ShouldUseIndirectVisibleDraw()
        {
            return visibleDrawReady
                   && !ShouldDisableIndirectVisibleDrawInXr()
                   && indirectArgsBuffer != null
                   && visibleDrawOrderBuffer != null;
        }

        private void OnEnable()
        {
            RenderPipelineManager.endCameraRendering += HandleEndCameraRendering;
            Camera.onPostRender += HandleCameraPostRender;
        }

        private void LateUpdate()
        {
            if (!IsReady)
            {
                return;
            }

            var splatFrameChanged = UpdateSplatFrameData();
            UploadSplatBufferIfDirty();

            var sortCamera = ResolveSortCamera();
            var sortedThisFrame = false;
            if (ShouldSortThisFrame(sortCamera, splatFrameChanged))
            {
                if (!TrySortSplatsGpu(sortCamera))
                {
                    EnsureCentersForCpuSort();
                    SortSplatsCpu(sortCamera);
                }

                sortedThisFrame = true;
                RememberSortCamera(sortCamera);
            }

            UploadDrawOrderBufferIfDirty();
            if (ShouldCullThisFrame(sortCamera, splatFrameChanged || sortedThisFrame))
            {
                visibleDrawReady = TryCullVisibleSplatsGpu(sortCamera);
                if (visibleDrawReady)
                {
                    RememberCullCamera(sortCamera);
                }
            }
            else if (!UseGpuVisibleCulling)
            {
                visibleDrawReady = false;
            }
        }

        private void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= HandleEndCameraRendering;
            Camera.onPostRender -= HandleCameraPostRender;
            Unload();
        }

        private void OnDestroy()
        {
            Unload();
        }

        public void Load(GvrmRenderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            Unload();

            viewCamera = context.ViewCamera != null ? context.ViewCamera : Camera.main;
            budget = context.Budget;
            if (budget.MaxSplats <= 0)
            {
                budget = GvrmRenderBudget.ForQuality(context.Quality);
            }
            ApplyQualityPreset(context.Quality);
            followSourceSkinning = context.FollowSourceSkinning;
            preserveStaticRestPose = followSourceSkinning;

            splatStream = ResolveSplatStream(context);
            gsLocalToRenderer = BuildGsLocalToRenderer(context);
            SetupSkinning(context, splatStream);
            BuildSplatBuffers(splatStream);
            EnsureGpuResources();
            UpdateSplatFrameData();
            UploadSplatBufferIfDirty();
            var initialSortCamera = ResolveSortCamera();
            if (!TrySortSplatsGpu(initialSortCamera))
            {
                EnsureCentersForCpuSort();
                SortSplatsCpu(initialSortCamera);
                UploadDrawOrderBufferIfDirty();
            }
            RememberSortCamera(initialSortCamera);
            visibleDrawReady = UseGpuVisibleCulling && TryCullVisibleSplatsGpu(initialSortCamera);
            if (visibleDrawReady)
            {
                RememberCullCamera(initialSortCamera);
            }
            framesUntilSort = Mathf.Max(1, EffectiveSortEveryNthFrame);

            Debug.Log(
                $"[Chimera GVRM] {BackendName} loaded: quality={context.Quality}, path={runtimePath}, "
                + $"splats={RenderedSplatCount}/{splatStream.Count}, "
                + $"shDegree={activeSphericalHarmonicsDegree}, "
                + $"sortEvery={EffectiveSortEveryNthFrame}, "
                + $"skinning={(UseGpuSkinning ? $"gpu-vertex-cache/{meshPositions.Length}" : HasSkinning ? "cpu" : "none")}, "
                + $"projection={(UseProjectedSplatCache ? "gpu-splat-cache" : "vertex-shader")}, "
                + $"sort={(UseGpuDepthBucketSort ? $"gpu-depth-bucket/{allocatedSortBinCount}" : "cpu-exact")}, "
                + $"visibleCull={(UseGpuVisibleCulling ? $"gpu-indirect-block/{allocatedCullBlockCount}" : "off")}, "
                + $"xrPerf={(UseXrPerformanceBudget ? $"on/binCap={EffectiveDepthSortBinCount}" : "off")}, "
                + $"stableSkip={(skipStableSkinningFrames || skipStableSortFrames ? "on" : "off")}, "
                + $"surfaceContinuity={surfaceContinuity:0.00}/{surfaceContinuityKernelPixels:0.00}px.");
            if (context.Quality == GvrmRenderQuality.Maximum && splatStream.Count < 100000)
            {
                Debug.LogWarning(
                    "[Chimera GVRM] This .gvrm contains fewer than 100k splats. "
                    + "The renderer can draw it at maximum settings, but close-up face detail is export-density limited.");
            }
        }

        private void ApplyQualityPreset(GvrmRenderQuality quality)
        {
            // Keep Maximum on gaussian-splats-3d's rasterization contract:
            // splatScale=1, straight sigmoid opacity, SH contribution at source scale.
            // Lower modes may bias opacity slightly to hide mobile downsampling.
            opacityScale = quality switch
            {
                GvrmRenderQuality.Maximum => 1f,
                GvrmRenderQuality.Quality => 1f,
                GvrmRenderQuality.Presentation => 1.5f,
                _ => 1.25f
            };
            splatScale = quality switch
            {
                GvrmRenderQuality.Maximum => 1f,
                GvrmRenderQuality.Quality => 1f,
                GvrmRenderQuality.Presentation => 0.95f,
                _ => 1f
            };
            opacityPower = quality switch
            {
                GvrmRenderQuality.Maximum => 1f,
                GvrmRenderQuality.Quality => 1f,
                GvrmRenderQuality.Presentation => 0.94f,
                _ => 1f
            };
            opacityDensity = quality switch
            {
                GvrmRenderQuality.Maximum => 1.35f,
                GvrmRenderQuality.Quality => 1.2f,
                GvrmRenderQuality.Presentation => 1.1f,
                _ => 1f
            };
            sphericalHarmonicsScale = quality switch
            {
                GvrmRenderQuality.Maximum => 1f,
                GvrmRenderQuality.Quality => 1f,
                GvrmRenderQuality.Presentation => 0.9f,
                _ => 0f
            };
            alphaClip = 0f;
            minScreenRadiusPixels = quality switch
            {
                GvrmRenderQuality.Maximum => 0.1f,
                GvrmRenderQuality.Quality => 0.12f,
                GvrmRenderQuality.Presentation => 0.16f,
                _ => 0.12f
            };
            kernel2DSize = quality switch
            {
                GvrmRenderQuality.Maximum => 0.36f,
                GvrmRenderQuality.Quality => 0.4f,
                GvrmRenderQuality.Presentation => 0.44f,
                _ => 0.36f
            };
            antialiasOpacityCompensation = quality switch
            {
                GvrmRenderQuality.Maximum => 0.08f,
                GvrmRenderQuality.Quality => 0.1f,
                GvrmRenderQuality.Presentation => 0.08f,
                _ => 0.12f
            };
            eigenTermFloor = 0.1f;
            surfaceContinuity = quality switch
            {
                GvrmRenderQuality.Maximum => 0.28f,
                GvrmRenderQuality.Quality => 0.32f,
                GvrmRenderQuality.Presentation => 0.38f,
                _ => 0.3f
            };
            surfaceContinuityKernelPixels = quality switch
            {
                GvrmRenderQuality.Maximum => 0.38f,
                GvrmRenderQuality.Quality => 0.44f,
                GvrmRenderQuality.Presentation => 0.5f,
                _ => 0.4f
            };
            surfaceContinuityShStability = quality switch
            {
                GvrmRenderQuality.Maximum => 0.12f,
                GvrmRenderQuality.Quality => 0.16f,
                GvrmRenderQuality.Presentation => 0.2f,
                _ => 0.12f
            };
            quantizeColorLikeGs3d = true;
            // GS3D treats the decoded PLY color bytes directly in the shader;
            // Unity should not apply an extra sRGB->linear conversion when
            // matching the Dashboard/Web preview.
            linearizeColorInLinearProjects = false;
            maxScreenRadiusPixels = quality == GvrmRenderQuality.Preview ? 256f : 1024f;
            depthSortBinCount = quality switch
            {
                GvrmRenderQuality.Maximum => 8192,
                GvrmRenderQuality.Quality => 4096,
                GvrmRenderQuality.Presentation => 2048,
                _ => 1024
            };
            cullingFrustumPadding = quality switch
            {
                GvrmRenderQuality.Maximum => 0.18f,
                GvrmRenderQuality.Quality => 0.2f,
                GvrmRenderQuality.Presentation => 0.22f,
                _ => 0.25f
            };
            cullingMinScreenRadiusPixels = quality == GvrmRenderQuality.Preview ? 0.05f : 0f;
        }

        public void Unload()
        {
            ReleaseGpuResources();
            DestroyUnityObject(runtimeMaterial);

            runtimeMaterial = null;
            propertyBlock = null;
            splatStream = null;
            sourceSkinnedMesh = null;
            skinningBinding = null;
            gsLocalToRenderer = Matrix4x4.identity;
            RenderedSplatCount = 0;

            splatIndices = Array.Empty<int>();
            sortOrder = Array.Empty<int>();
            drawOrderUpload = Array.Empty<uint>();
            sortKeys = Array.Empty<float>();
            splats = Array.Empty<ProceduralSplat>();
            skinningInputs = Array.Empty<SplatSkinningInput>();
            vertexSkinningInputs = Array.Empty<VertexSkinningInput>();
            sphericalHarmonics = Array.Empty<SphericalHarmonicsSplat>();
            centersWorld = Array.Empty<Vector3>();
            axis0World = Array.Empty<Vector3>();
            axis1World = Array.Empty<Vector3>();
            axis2World = Array.Empty<Vector3>();
            axis0GsLocal = Array.Empty<Vector3>();
            axis1GsLocal = Array.Empty<Vector3>();
            axis2GsLocal = Array.Empty<Vector3>();
            relativePositionsMeshLocal = Array.Empty<Vector3>();
            skinnedCentersWorld = Array.Empty<Vector3>();
            meshPositions = Array.Empty<Vector3>();
            boneWeights = Array.Empty<BoneWeight>();
            restBoneMatrices = Array.Empty<Matrix4x4>();
            currentBoneMatrices = Array.Empty<Matrix4x4>();
            restSkinInverseMatrices = Array.Empty<Matrix4x4>();
            currentBoneMatricesGpu = Array.Empty<GpuMatrix4x4>();
            restSkinInverseMatricesGpu = Array.Empty<GpuMatrix4x4>();
            gsRotation = Matrix4x4.identity;
            gsRotationInverse = Matrix4x4.identity;
            drawBounds = new Bounds(Vector3.zero, Vector3.one);
            splatBufferDirty = false;
            drawOrderDirty = false;
            gpuSkinningUnavailable = false;
            gpuProjectionUnavailable = false;
            gpuSortUnavailable = false;
            gpuCullingUnavailable = false;
            visibleDrawReady = false;
            hasCapturedBoneFrame = false;
            hasUploadedSkinningFrame = false;
            hasSortCameraState = false;
            hasCullCameraState = false;
            lastRendererLocalToWorld = Matrix4x4.identity;
            lastSourceMeshLocalToWorld = Matrix4x4.identity;
            lastSortCameraPosition = Vector3.zero;
            lastSortCameraRotation = Quaternion.identity;
            lastCullCameraPosition = Vector3.zero;
            lastCullCameraRotation = Quaternion.identity;
            activeSphericalHarmonicsDegree = 0;
            vertexSkinningKernel = -1;
            skinningKernel = -1;
            projectionKernel = -1;
            sortClearKernel = -1;
            sortCountKernel = -1;
            sortPrefixKernel = -1;
            sortFillKernel = -1;
            cullClearKernel = -1;
            cullMarkKernel = -1;
            cullPrefixKernel = -1;
            cullCompactKernel = -1;
            allocatedSortBinCount = 0;
            allocatedCullBlockCount = 0;
            lastProjectedSplatCacheUsed = false;
            lastProjectionDispatchCpuMs = 0f;
        }

        private static GaussianSplatStream ResolveSplatStream(GvrmRenderContext context)
        {
            if (context.Splats != null)
            {
                return context.Splats;
            }

            if (context.Bundle?.PlyBytes == null || context.Bundle.PlyBytes.Length == 0)
            {
                throw new InvalidOperationException("GVRM render context must provide Splats or Bundle.PlyBytes.");
            }

            return GaussianSplatStream.Read(context.Bundle.PlyBytes);
        }

        private static Matrix4x4 BuildGsLocalToRenderer(GvrmRenderContext context)
        {
            if (!IsIdentity(context.GsLocalToAvatar))
            {
                return context.GsLocalToAvatar;
            }

            var metadata = context.Bundle?.Metadata;
            return metadata != null ? GvrmMetadataTransform.BuildGsLocalToAvatar(metadata) : Matrix4x4.identity;
        }

        private void SetupSkinning(GvrmRenderContext context, GaussianSplatStream stream)
        {
            if (!followSourceSkinning || context.SourceSkinnedMesh == null || context.Bundle?.Metadata == null)
            {
                return;
            }

            var mesh = context.SourceSkinnedMesh.sharedMesh;
            if (mesh == null)
            {
                return;
            }

            try
            {
                var positions = mesh.vertices;
                var weights = mesh.boneWeights;
                if (positions == null || weights == null || positions.Length == 0 || positions.Length != weights.Length)
                {
                    return;
                }

                var binding = GvrmSkinnedSplatBinding.From(context.Bundle.Metadata, stream, context.SourceSkinnedMesh.bones);
                binding.ValidateAgainst(context.SourceSkinnedMesh);

                sourceSkinnedMesh = context.SourceSkinnedMesh;
                skinningBinding = binding;
                meshPositions = positions;
                boneWeights = weights;
                restBoneMatrices = GvrmSkinnedSplatDeformer.CaptureUnityLocalBoneMatrices(sourceSkinnedMesh);
                currentBoneMatrices = new Matrix4x4[restBoneMatrices.Length];
                currentBoneMatricesGpu = new GpuMatrix4x4[restBoneMatrices.Length];
                vertexSkinningInputs = BuildVertexSkinningInputs(meshPositions, boneWeights);
                restSkinInverseMatrices = BuildVertexRestSkinInverseMatrices(boneWeights, restBoneMatrices);
                LogWebParitySkinningDiagnostics(context, stream, binding);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chimera GVRM] Procedural GPU splats falling back to static centers: " + ex.Message);
                sourceSkinnedMesh = null;
                skinningBinding = null;
                meshPositions = Array.Empty<Vector3>();
                boneWeights = Array.Empty<BoneWeight>();
                restBoneMatrices = Array.Empty<Matrix4x4>();
                currentBoneMatrices = Array.Empty<Matrix4x4>();
                currentBoneMatricesGpu = Array.Empty<GpuMatrix4x4>();
                vertexSkinningInputs = Array.Empty<VertexSkinningInput>();
                restSkinInverseMatrices = Array.Empty<Matrix4x4>();
            }
        }

        private void LogWebParitySkinningDiagnostics(
            GvrmRenderContext context,
            GaussianSplatStream stream,
            GvrmSkinnedSplatBinding binding)
        {
            var metadata = context.Bundle?.Metadata;
            var mesh = sourceSkinnedMesh != null ? sourceSkinnedMesh.sharedMesh : null;
            var runtimeBones = sourceSkinnedMesh != null ? sourceSkinnedMesh.bones : null;
            var serverBoneNames = metadata?.ServerBoneNames;
            var exactBoneOrder = false;
            var comparedBoneCount = 0;
            var mismatchedBoneCount = 0;
            if (serverBoneNames != null && runtimeBones != null)
            {
                comparedBoneCount = Mathf.Min(serverBoneNames.Count, runtimeBones.Length);
                exactBoneOrder = serverBoneNames.Count == runtimeBones.Length;
                for (var i = 0; i < comparedBoneCount; i++)
                {
                    var runtimeName = runtimeBones[i] != null ? runtimeBones[i].name : string.Empty;
                    if (!string.Equals(serverBoneNames[i], runtimeName, StringComparison.Ordinal))
                    {
                        mismatchedBoneCount++;
                        exactBoneOrder = false;
                    }
                }
            }

            var boneOperationCount = metadata?.BoneOperations != null ? metadata.BoneOperations.Count : 0;
            Debug.Log(
                "[Chimera GVRM] Web-parity dynamic GVRM skinning contract: "
                + $"splats={stream.Count}, runtimeVertices={mesh?.vertexCount ?? 0}, "
                + $"serverVertices={metadata?.ServerMeshVertexCount?.ToString() ?? "n/a"}, "
                + $"maxSplatVertex={binding.MaxSplatVertexIndex}, runtimeBones={runtimeBones?.Length ?? 0}, "
                + $"serverBones={serverBoneNames?.Count ?? 0}, maxSplatBone={binding.MaxSplatBoneIndex}, "
                + $"boneOrderExact={exactBoneOrder}, boneOrderMismatches={mismatchedBoneCount}/{comparedBoneCount}, "
                + $"boneOperations={boneOperationCount}, "
                + "restSnapshot=after-vrm-load-and-boneOperations.");

            var expectedVertexCount = metadata != null && metadata.ServerMeshVertexCount.HasValue
                ? metadata.ServerMeshVertexCount.Value
                : 0;
            if (mesh != null
                && expectedVertexCount > 0
                && mesh.vertexCount != expectedVertexCount)
            {
                Debug.LogWarning(
                    "[Chimera GVRM] Runtime SkinnedMeshRenderer vertex count does not match _serverMeshVertexCount. "
                    + "Dynamic GVRM parity requires the same primitive/mesh selected by the server preprocess.");
            }

            if (mismatchedBoneCount > 0)
            {
                Debug.LogWarning(
                    "[Chimera GVRM] Runtime bone order differs from server bone order. "
                    + "Name remapping is active; unresolved helper/end bones are remapped to parents where possible.");
            }
        }

        private void BuildSplatBuffers(GaussianSplatStream stream)
        {
            var renderableSplatCount = GetRenderableSplatCount(stream);
            var renderCount = budget.MaxSplats > 0
                ? Mathf.Min(budget.MaxSplats, renderableSplatCount)
                : renderableSplatCount;

            RenderedSplatCount = Mathf.Max(0, renderCount);
            splatIndices = new int[RenderedSplatCount];
            sortOrder = new int[RenderedSplatCount];
            drawOrderUpload = new uint[RenderedSplatCount];
            sortKeys = new float[RenderedSplatCount];
            splats = new ProceduralSplat[RenderedSplatCount];
            skinningInputs = HasSkinning ? new SplatSkinningInput[RenderedSplatCount] : Array.Empty<SplatSkinningInput>();
            activeSphericalHarmonicsDegree = ResolveSphericalHarmonicsDegree(
                stream,
                budget,
                useViewDependentSphericalHarmonics);
            sphericalHarmonics = activeSphericalHarmonicsDegree > 0
                ? new SphericalHarmonicsSplat[RenderedSplatCount]
                : Array.Empty<SphericalHarmonicsSplat>();
            centersWorld = new Vector3[RenderedSplatCount];
            axis0World = new Vector3[RenderedSplatCount];
            axis1World = new Vector3[RenderedSplatCount];
            axis2World = new Vector3[RenderedSplatCount];
            skinnedCentersWorld = HasSkinning ? new Vector3[RenderedSplatCount] : Array.Empty<Vector3>();
            axis0GsLocal = HasSkinning ? new Vector3[RenderedSplatCount] : Array.Empty<Vector3>();
            axis1GsLocal = HasSkinning ? new Vector3[RenderedSplatCount] : Array.Empty<Vector3>();
            axis2GsLocal = HasSkinning ? new Vector3[RenderedSplatCount] : Array.Empty<Vector3>();
            relativePositionsMeshLocal = HasSkinning ? new Vector3[RenderedSplatCount] : Array.Empty<Vector3>();

            if (RenderedSplatCount == 0)
            {
                drawBounds = new Bounds(transform.position, Vector3.one);
                return;
            }

            var localToWorld = transform.localToWorldMatrix;
            gsRotation = BuildOrthonormalRotationMatrix(gsLocalToRenderer);
            gsRotationInverse = Matrix4x4.Transpose(gsRotation);
            var selectedSplatIndices = BuildSelectedSplatIndices(stream, RenderedSplatCount, renderableSplatCount);
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var restCorrectionSum = 0f;
            var restCorrectionMax = 0f;
            var restCorrectionCount = 0;

            for (var renderIndex = 0; renderIndex < RenderedSplatCount; renderIndex++)
            {
                var splatIndex = selectedSplatIndices[renderIndex];
                splatIndices[renderIndex] = splatIndex;
                sortOrder[renderIndex] = renderIndex;
                drawOrderUpload[renderIndex] = (uint)renderIndex;

                var centerLocal = TransformRawPosition(stream, splatIndex);
                EstimateRawAxes(stream, splatIndex, out var axis0Raw, out var axis1Raw, out var axis2Raw);
                var axis0Local = gsLocalToRenderer.MultiplyVector(axis0Raw);
                var axis1Local = gsLocalToRenderer.MultiplyVector(axis1Raw);
                var axis2Local = gsLocalToRenderer.MultiplyVector(axis2Raw);

                centersWorld[renderIndex] = localToWorld.MultiplyPoint3x4(centerLocal);
                axis0World[renderIndex] = localToWorld.MultiplyVector(axis0Local);
                axis1World[renderIndex] = localToWorld.MultiplyVector(axis1Local);
                axis2World[renderIndex] = localToWorld.MultiplyVector(axis2Local);

                if (HasSkinning)
                {
                    axis0GsLocal[renderIndex] = axis0Raw;
                    axis1GsLocal[renderIndex] = axis1Raw;
                    axis2GsLocal[renderIndex] = axis2Raw;
                    relativePositionsMeshLocal[renderIndex] = ResolveRestPoseRelativePosition(
                        splatIndex,
                        centerLocal,
                        localToWorld,
                        out var restCorrectionMagnitude);
                    if (preserveStaticRestPose)
                    {
                        restCorrectionSum += restCorrectionMagnitude;
                        restCorrectionMax = Mathf.Max(restCorrectionMax, restCorrectionMagnitude);
                        restCorrectionCount++;
                    }
                    skinningInputs[renderIndex] = BuildSkinningInput(stream, splatIndex, renderIndex);
                }

                min = Vector3.Min(min, centersWorld[renderIndex]);
                max = Vector3.Max(max, centersWorld[renderIndex]);
                splats[renderIndex] = BuildProceduralSplat(renderIndex);
                if (sphericalHarmonics.Length > 0)
                {
                    sphericalHarmonics[renderIndex] = BuildSphericalHarmonicsSplat(stream, splatIndex);
                }
            }

            drawBounds = new Bounds((min + max) * 0.5f, max - min);
            drawBounds.Expand(1f);
            if (HasSkinning && preserveStaticRestPose && restCorrectionCount > 0)
            {
                Debug.Log(
                    "[Chimera GVRM] Rest-pose static PLY parity correction: "
                    + $"mean={restCorrectionSum / restCorrectionCount:0.000000}m, "
                    + $"max={restCorrectionMax:0.000000}m over {restCorrectionCount} splats.");
            }

            splatBufferDirty = true;
            drawOrderDirty = true;
        }

        private Vector3 ResolveRestPoseRelativePosition(
            int splatIndex,
            Vector3 centerRendererLocal,
            Matrix4x4 rendererLocalToWorld,
            out float correctionMagnitude)
        {
            correctionMagnitude = 0f;
            var metadataRelative = skinningBinding.RelativePositions[splatIndex];
            if (!preserveStaticRestPose)
            {
                return metadataRelative;
            }

            var vertexIndex = skinningBinding.SplatVertexIndices[splatIndex];
            if (vertexIndex < 0 || vertexIndex >= meshPositions.Length || vertexIndex >= boneWeights.Length)
            {
                return metadataRelative;
            }

            var restSkinMatrix = GvrmSkinnedSplatDeformer.BuildSkinMatrix(
                boneWeights[vertexIndex],
                restBoneMatrices,
                Matrix4x4.identity,
                Matrix4x4.identity);
            var restVertexMeshLocal = restSkinMatrix.MultiplyPoint3x4(meshPositions[vertexIndex]);
            var staticCenterWorld = rendererLocalToWorld.MultiplyPoint3x4(centerRendererLocal);
            var staticCenterMeshLocal = sourceSkinnedMesh.transform.worldToLocalMatrix.MultiplyPoint3x4(staticCenterWorld);
            var correctedRelative = staticCenterMeshLocal - restVertexMeshLocal;
            correctionMagnitude = (correctedRelative - metadataRelative).magnitude;
            return correctedRelative;
        }

        private int GetRenderableSplatCount(GaussianSplatStream stream)
        {
            var count = Mathf.Max(0, stream.Count);
            if (skinningBinding != null)
            {
                count = Mathf.Min(count, skinningBinding.SplatCount);
            }

            count = Mathf.Min(count, ChannelCount(stream.PositionsXYZ, 3));
            count = Mathf.Min(count, ChannelCount(stream.ScalesLog, 3));
            count = Mathf.Min(count, ChannelCount(stream.RotationsXYZW, 4));
            if (stream.OpacitiesRaw != null)
            {
                count = Mathf.Min(count, stream.OpacitiesRaw.Length);
            }

            return count;
        }

        private static int ChannelCount(float[] values, int stride)
        {
            return values == null || stride <= 0 ? 0 : values.Length / stride;
        }

        private static int SelectSplatIndex(int renderIndex, int renderCount, int splatCount)
        {
            if (splatCount <= 1 || renderCount <= 1)
            {
                return 0;
            }

            var t = renderIndex / (float)(renderCount - 1);
            return Mathf.Clamp(Mathf.RoundToInt(t * (splatCount - 1)), 0, splatCount - 1);
        }

        private static int[] BuildSelectedSplatIndices(GaussianSplatStream stream, int renderCount, int splatCount)
        {
            var selected = new int[Mathf.Max(0, renderCount)];
            if (renderCount <= 0 || splatCount <= 0)
            {
                return selected;
            }

            if (renderCount >= splatCount)
            {
                for (var i = 0; i < renderCount; i++)
                {
                    selected[i] = i;
                }

                return selected;
            }

            var candidates = new SplatImportance[splatCount];
            for (var i = 0; i < splatCount; i++)
            {
                candidates[i] = new SplatImportance
                {
                    Index = i,
                    Score = EstimateSplatImportance(stream, i)
                };
            }

            Array.Sort(candidates, (a, b) => b.Score.CompareTo(a.Score));
            for (var i = 0; i < renderCount; i++)
            {
                selected[i] = candidates[i].Index;
            }

            Array.Sort(selected);
            return selected;
        }

        private static float EstimateSplatImportance(GaussianSplatStream stream, int splatIndex)
        {
            var alpha = stream.OpacitiesRaw != null && splatIndex < stream.OpacitiesRaw.Length
                ? Sigmoid(stream.OpacitiesRaw[splatIndex])
                : 1f;

            var scaleOffset = splatIndex * 3;
            if (stream.ScalesLog == null || scaleOffset + 2 >= stream.ScalesLog.Length)
            {
                return alpha;
            }

            var sx = SafeExp(stream.ScalesLog[scaleOffset]);
            var sy = SafeExp(stream.ScalesLog[scaleOffset + 1]);
            var sz = SafeExp(stream.ScalesLog[scaleOffset + 2]);
            return alpha * Mathf.Max(1e-8f, sx * sy + sy * sz + sz * sx);
        }

        private static float SafeExp(float value)
        {
            return Mathf.Exp(Mathf.Clamp(value, -20f, 20f));
        }

        private Vector3 TransformRawPosition(GaussianSplatStream stream, int splatIndex)
        {
            var offset = splatIndex * 3;
            var raw = new Vector3(
                stream.PositionsXYZ[offset],
                stream.PositionsXYZ[offset + 1],
                stream.PositionsXYZ[offset + 2]);

            return gsLocalToRenderer.MultiplyPoint3x4(raw);
        }

        private static void EstimateRawAxes(
            GaussianSplatStream stream,
            int splatIndex,
            out Vector3 axis0,
            out Vector3 axis1,
            out Vector3 axis2)
        {
            var scaleOffset = splatIndex * 3;
            var sx = SafeExp(stream.ScalesLog[scaleOffset]);
            var sy = SafeExp(stream.ScalesLog[scaleOffset + 1]);
            var sz = SafeExp(stream.ScalesLog[scaleOffset + 2]);
            var rotation = EstimateSplatRotation(stream, splatIndex);

            axis0 = rotation * (Vector3.right * sx);
            axis1 = rotation * (Vector3.up * sy);
            axis2 = rotation * (Vector3.forward * sz);
        }

        private static Quaternion EstimateSplatRotation(GaussianSplatStream stream, int splatIndex)
        {
            return GaussianSplatMath.DecodeRotationXYZW(stream.RotationsXYZW, splatIndex);
        }

        private bool UpdateSplatFrameData()
        {
            if (!HasSkinning)
            {
                return false;
            }

            var boneMatricesChanged = UpdateCurrentBoneMatrices();
            var skinningTransformChanged = HasSkinningTransformChanged();
            var shouldUpdateSkinning = !skipStableSkinningFrames
                                       || !hasUploadedSkinningFrame
                                       || boneMatricesChanged
                                       || skinningTransformChanged;
            drawBounds = new Bounds(sourceSkinnedMesh.bounds.center, sourceSkinnedMesh.bounds.size + Vector3.one * 2f);

            if (!shouldUpdateSkinning)
            {
                return false;
            }

            if (UseGpuSkinning && TryDispatchGpuSkinning())
            {
                if (!UseGpuDepthBucketSort)
                {
                    UpdateCentersForSort();
                }
                MarkSkinningFrameUploaded();
                splatBufferDirty = false;
                return true;
            }

            DeformCentersCpu(skinnedCentersWorld);

            for (var i = 0; i < RenderedSplatCount; i++)
            {
                centersWorld[i] = skinnedCentersWorld[i];
                UpdateSkinnedAxes(i);
            }

            for (var i = 0; i < RenderedSplatCount; i++)
            {
                splats[i] = BuildProceduralSplat(i);
            }

            MarkSkinningFrameUploaded();
            splatBufferDirty = true;
            return true;
        }

        private void UpdateCentersForSort()
        {
            DeformCentersCpu(skinnedCentersWorld);

            for (var i = 0; i < RenderedSplatCount; i++)
            {
                centersWorld[i] = skinnedCentersWorld[i];
            }
        }

        private void EnsureCentersForCpuSort()
        {
            if (!HasSkinning || !UseGpuSkinning)
            {
                return;
            }

            UpdateCentersForSort();
        }

        private void UpdateSkinnedAxes(int renderIndex)
        {
            var splatIndex = splatIndices[renderIndex];
            var vertexIndex = skinningBinding.SplatVertexIndices[splatIndex];
            if (vertexIndex < 0 || vertexIndex >= boneWeights.Length)
            {
                return;
            }

            var skinDelta = GvrmSkinnedSplatDeformer.BuildSkinningDeltaMatrix(
                boneWeights[vertexIndex],
                Matrix4x4.identity,
                Matrix4x4.identity,
                Matrix4x4.identity,
                Matrix4x4.identity,
                currentBoneMatrices,
                restBoneMatrices);

            var relativeRotation = BuildWebParityCovarianceRotation(skinDelta);
            var covarianceAxisRotation = Matrix4x4.Transpose(relativeRotation);
            var localToWorld = transform.localToWorldMatrix;
            axis0World[renderIndex] = localToWorld.MultiplyVector(
                gsLocalToRenderer.MultiplyVector(covarianceAxisRotation.MultiplyVector(axis0GsLocal[renderIndex])));
            axis1World[renderIndex] = localToWorld.MultiplyVector(
                gsLocalToRenderer.MultiplyVector(covarianceAxisRotation.MultiplyVector(axis1GsLocal[renderIndex])));
            axis2World[renderIndex] = localToWorld.MultiplyVector(
                gsLocalToRenderer.MultiplyVector(covarianceAxisRotation.MultiplyVector(axis2GsLocal[renderIndex])));
        }

        private Matrix4x4 BuildWebParityCovarianceRotation(Matrix4x4 skinDelta)
        {
            var skinRotation = BuildOrthonormalRotationMatrix(skinDelta);
            var relativeRotation = gsRotationInverse * skinRotation * gsRotation;
            if (!matchWebCovarianceQuaternionYFlip)
            {
                return relativeRotation;
            }

            var relativeQuaternion = QuaternionFromRotationMatrix(relativeRotation);
            relativeQuaternion.y = -relativeQuaternion.y;
            return Matrix4x4.Rotate(NormalizeQuaternion(relativeQuaternion));
        }

        private static Matrix4x4 BuildOrthonormalRotationMatrix(Matrix4x4 value)
        {
            var x = value.MultiplyVector(Vector3.right);
            var y = value.MultiplyVector(Vector3.up);
            if (x.sqrMagnitude <= 1e-12f || y.sqrMagnitude <= 1e-12f)
            {
                return Matrix4x4.identity;
            }

            x.Normalize();
            y -= x * Vector3.Dot(y, x);
            if (y.sqrMagnitude <= 1e-12f)
            {
                return Matrix4x4.identity;
            }

            y.Normalize();
            var z = Vector3.Cross(x, y);
            if (z.sqrMagnitude <= 1e-12f)
            {
                return Matrix4x4.identity;
            }

            z.Normalize();
            var result = Matrix4x4.identity;
            result.SetColumn(0, new Vector4(x.x, x.y, x.z, 0f));
            result.SetColumn(1, new Vector4(y.x, y.y, y.z, 0f));
            result.SetColumn(2, new Vector4(z.x, z.y, z.z, 0f));
            return result;
        }

        private static Quaternion QuaternionFromRotationMatrix(Matrix4x4 value)
        {
            var trace = value[0, 0] + value[1, 1] + value[2, 2];
            if (trace > 0f)
            {
                var s = 0.5f / Mathf.Sqrt(trace + 1f);
                return new Quaternion(
                    (value[2, 1] - value[1, 2]) * s,
                    (value[0, 2] - value[2, 0]) * s,
                    (value[1, 0] - value[0, 1]) * s,
                    0.25f / s);
            }

            if (value[0, 0] > value[1, 1] && value[0, 0] > value[2, 2])
            {
                var s = 2f * Mathf.Sqrt(1f + value[0, 0] - value[1, 1] - value[2, 2]);
                return new Quaternion(
                    0.25f * s,
                    (value[0, 1] + value[1, 0]) / s,
                    (value[0, 2] + value[2, 0]) / s,
                    (value[2, 1] - value[1, 2]) / s);
            }

            if (value[1, 1] > value[2, 2])
            {
                var s = 2f * Mathf.Sqrt(1f + value[1, 1] - value[0, 0] - value[2, 2]);
                return new Quaternion(
                    (value[0, 1] + value[1, 0]) / s,
                    0.25f * s,
                    (value[1, 2] + value[2, 1]) / s,
                    (value[0, 2] - value[2, 0]) / s);
            }

            var zScale = 2f * Mathf.Sqrt(1f + value[2, 2] - value[0, 0] - value[1, 1]);
            return new Quaternion(
                (value[0, 2] + value[2, 0]) / zScale,
                (value[1, 2] + value[2, 1]) / zScale,
                0.25f * zScale,
                (value[1, 0] - value[0, 1]) / zScale);
        }

        private void DeformCentersCpu(Vector3[] outputCenters)
        {
            var meshLocalToWorld = sourceSkinnedMesh.transform.localToWorldMatrix;
            for (var i = 0; i < RenderedSplatCount; i++)
            {
                var splatIndex = splatIndices[i];
                var vertexIndex = skinningBinding.SplatVertexIndices[splatIndex];
                if (vertexIndex < 0 || vertexIndex >= meshPositions.Length || vertexIndex >= boneWeights.Length)
                {
                    outputCenters[i] = transform.position;
                    continue;
                }

                var skinWeights = boneWeights[vertexIndex];
                var currentSkinMatrix = GvrmSkinnedSplatDeformer.BuildSkinMatrix(
                    skinWeights,
                    currentBoneMatrices,
                    Matrix4x4.identity,
                    Matrix4x4.identity);
                var skinDelta = GvrmSkinnedSplatDeformer.BuildSkinningDeltaMatrix(
                    skinWeights,
                    Matrix4x4.identity,
                    Matrix4x4.identity,
                    Matrix4x4.identity,
                    Matrix4x4.identity,
                    currentBoneMatrices,
                    restBoneMatrices);

                var skinnedVertex = currentSkinMatrix.MultiplyPoint3x4(meshPositions[vertexIndex]);
                var skinnedRelativePosition = skinDelta.MultiplyVector(relativePositionsMeshLocal[i]);
                outputCenters[i] = meshLocalToWorld.MultiplyPoint3x4(skinnedVertex + skinnedRelativePosition);
            }
        }

        private SplatSkinningInput BuildSkinningInput(GaussianSplatStream stream, int splatIndex, int renderIndex)
        {
            var vertexIndex = skinningBinding.SplatVertexIndices[splatIndex];
            var weight = boneWeights[vertexIndex];
            var color = EstimateColor(stream, splatIndex);
            return new SplatSkinningInput
            {
                MeshPosition = ToVector4(meshPositions[vertexIndex], 1f),
                RelativePosition = ToVector4(relativePositionsMeshLocal[renderIndex], 0f),
                Axis0RestMeshLocal = ToVector4(axis0GsLocal[renderIndex], 0f),
                Axis1RestMeshLocal = ToVector4(axis1GsLocal[renderIndex], 0f),
                Axis2RestMeshLocal = ToVector4(axis2GsLocal[renderIndex], 0f),
                BoneIndices = new Vector4(weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3),
                BoneWeights = new Vector4(weight.weight0, weight.weight1, weight.weight2, weight.weight3),
                Color = new Vector4(color.r, color.g, color.b, color.a),
                Meta = new Vector4(sphericalHarmonics.Length > 0 ? renderIndex : -1f, vertexIndex, 0f, 0f)
            };
        }

        private static VertexSkinningInput[] BuildVertexSkinningInputs(
            Vector3[] positions,
            BoneWeight[] weights)
        {
            if (positions == null || weights == null || positions.Length == 0 || positions.Length != weights.Length)
            {
                return Array.Empty<VertexSkinningInput>();
            }

            var inputs = new VertexSkinningInput[positions.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                var weight = weights[i];
                inputs[i] = new VertexSkinningInput
                {
                    MeshPosition = ToVector4(positions[i], 1f),
                    BoneIndices = new Vector4(weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3),
                    BoneWeights = new Vector4(weight.weight0, weight.weight1, weight.weight2, weight.weight3)
                };
            }

            return inputs;
        }

        private static Matrix4x4[] BuildVertexRestSkinInverseMatrices(
            BoneWeight[] weights,
            Matrix4x4[] restMatrices)
        {
            if (weights == null || restMatrices == null || weights.Length == 0 || restMatrices.Length == 0)
            {
                return Array.Empty<Matrix4x4>();
            }

            var inverses = new Matrix4x4[weights.Length];
            for (var i = 0; i < inverses.Length; i++)
            {
                inverses[i] = GvrmSkinnedSplatDeformer.BuildSkinMatrix(
                    weights[i],
                    restMatrices,
                    Matrix4x4.identity,
                    Matrix4x4.identity).inverse;
            }

            return inverses;
        }

        private bool TryDispatchGpuSkinning()
        {
            if (!UseGpuSkinning || RenderedSplatCount <= 0)
            {
                return false;
            }

            try
            {
                UploadMatricesForCompute(currentBoneMatrices, currentBoneMatricesGpu, currentBoneMatrixBuffer);

                skinningComputeShader.SetInt("_SplatCount", RenderedSplatCount);
                skinningComputeShader.SetInt("_VertexCount", meshPositions.Length);
                skinningComputeShader.SetMatrix("_MeshLocalToWorld", sourceSkinnedMesh.transform.localToWorldMatrix);
                skinningComputeShader.SetMatrix("_GsLocalToWorld", transform.localToWorldMatrix * gsLocalToRenderer);
                skinningComputeShader.SetMatrix("_GsRotation", gsRotation);
                skinningComputeShader.SetMatrix("_GsRotationInverse", gsRotationInverse);
                skinningComputeShader.SetInt("_MatchWebCovarianceQuaternionYFlip", matchWebCovarianceQuaternionYFlip ? 1 : 0);
                skinningComputeShader.SetBuffer(vertexSkinningKernel, "_VertexSkinningInputs", vertexSkinningInputBuffer);
                skinningComputeShader.SetBuffer(vertexSkinningKernel, "_CurrentBoneMatrices", currentBoneMatrixBuffer);
                skinningComputeShader.SetBuffer(vertexSkinningKernel, "_RestSkinInverseMatrices", restSkinInverseMatrixBuffer);
                skinningComputeShader.SetBuffer(vertexSkinningKernel, "_VertexSkinningStates", vertexSkinningStateBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_SkinningInputs", skinningInputBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_VertexSkinningInputs", vertexSkinningInputBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_CurrentBoneMatrices", currentBoneMatrixBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_RestSkinInverseMatrices", restSkinInverseMatrixBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_VertexSkinningStates", vertexSkinningStateBuffer);
                skinningComputeShader.SetBuffer(skinningKernel, "_OutputSplats", splatBuffer);

                var vertexGroups = Mathf.CeilToInt(meshPositions.Length / (float)SkinningThreadGroupSize);
                skinningComputeShader.Dispatch(vertexSkinningKernel, Mathf.Max(1, vertexGroups), 1, 1);
                var groups = Mathf.CeilToInt(RenderedSplatCount / (float)SkinningThreadGroupSize);
                skinningComputeShader.Dispatch(skinningKernel, Mathf.Max(1, groups), 1, 1);
                return true;
            }
            catch (Exception ex)
            {
                gpuSkinningUnavailable = true;
                Debug.LogWarning("[Chimera GVRM] GPU splat skinning failed; falling back to CPU-fed splat updates. " + ex.Message);
                return false;
            }
        }

        private bool UpdateCurrentBoneMatrices()
        {
            if (sourceSkinnedMesh == null || sourceSkinnedMesh.sharedMesh == null)
            {
                return false;
            }

            var bones = sourceSkinnedMesh.bones;
            var bindPoses = sourceSkinnedMesh.sharedMesh.bindposes;
            if (bones == null || bindPoses == null || bones.Length != bindPoses.Length)
            {
                return false;
            }

            if (currentBoneMatrices == null || currentBoneMatrices.Length != bones.Length)
            {
                currentBoneMatrices = new Matrix4x4[bones.Length];
                currentBoneMatricesGpu = new GpuMatrix4x4[bones.Length];
                hasCapturedBoneFrame = false;
            }

            var changed = !hasCapturedBoneFrame;
            var epsilon = Mathf.Max(0f, skinningMatrixChangeEpsilon);
            var worldToRenderer = sourceSkinnedMesh.transform.worldToLocalMatrix;
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null)
                {
                    continue;
                }

                var matrix = worldToRenderer * bones[i].localToWorldMatrix * bindPoses[i];
                if (!changed && !MatrixApproximatelyEqual(currentBoneMatrices[i], matrix, epsilon))
                {
                    changed = true;
                }

                currentBoneMatrices[i] = matrix;
            }

            hasCapturedBoneFrame = true;
            return changed;
        }

        private bool HasSkinningTransformChanged()
        {
            if (!hasUploadedSkinningFrame || sourceSkinnedMesh == null)
            {
                return true;
            }

            var epsilon = Mathf.Max(0f, skinningMatrixChangeEpsilon);
            return !MatrixApproximatelyEqual(lastRendererLocalToWorld, transform.localToWorldMatrix, epsilon)
                   || !MatrixApproximatelyEqual(lastSourceMeshLocalToWorld, sourceSkinnedMesh.transform.localToWorldMatrix, epsilon);
        }

        private void MarkSkinningFrameUploaded()
        {
            hasUploadedSkinningFrame = true;
            lastRendererLocalToWorld = transform.localToWorldMatrix;
            lastSourceMeshLocalToWorld = sourceSkinnedMesh != null
                ? sourceSkinnedMesh.transform.localToWorldMatrix
                : Matrix4x4.identity;
        }

        private static void UploadMatricesForCompute(
            Matrix4x4[] sourceMatrices,
            GpuMatrix4x4[] uploadMatrices,
            ComputeBuffer targetBuffer)
        {
            if (sourceMatrices == null || uploadMatrices == null || targetBuffer == null)
            {
                return;
            }

            var count = Mathf.Min(sourceMatrices.Length, uploadMatrices.Length);
            for (var i = 0; i < count; i++)
            {
                uploadMatrices[i] = GpuMatrix4x4.FromUnityMatrix(sourceMatrices[i]);
            }

            targetBuffer.SetData(uploadMatrices, 0, 0, count);
        }

        private static Vector4 ToVector4(Vector3 value, float w)
        {
            return new Vector4(value.x, value.y, value.z, w);
        }

        private ProceduralSplat BuildProceduralSplat(int renderIndex)
        {
            var splatIndex = splatIndices[renderIndex];
            var color = EstimateColor(splatStream, splatIndex);
            return new ProceduralSplat
            {
                CenterWorld = new Vector4(centersWorld[renderIndex].x, centersWorld[renderIndex].y, centersWorld[renderIndex].z, 1f),
                Axis0World = new Vector4(axis0World[renderIndex].x, axis0World[renderIndex].y, axis0World[renderIndex].z, 0f),
                Axis1World = new Vector4(axis1World[renderIndex].x, axis1World[renderIndex].y, axis1World[renderIndex].z, 0f),
                Axis2World = new Vector4(axis2World[renderIndex].x, axis2World[renderIndex].y, axis2World[renderIndex].z, 0f),
                Color = new Vector4(color.r, color.g, color.b, color.a),
                Meta = new Vector4(sphericalHarmonics.Length > 0 ? renderIndex : -1f, 0f, 0f, 0f)
            };
        }

        private static int ResolveSphericalHarmonicsDegree(
            GaussianSplatStream stream,
            GvrmRenderBudget budget,
            bool useViewDependentSphericalHarmonics)
        {
            if (!useViewDependentSphericalHarmonics
                || !budget.EnableSphericalHarmonics
                || stream?.ShRest == null
                || stream.ShRestStride <= 0)
            {
                return 0;
            }

            var coefficientCountPerChannel = stream.ShRestStride / 3;
            var availableDegree = coefficientCountPerChannel >= 15
                ? 3
                : coefficientCountPerChannel >= 8
                    ? 2
                    : coefficientCountPerChannel >= 3
                        ? 1
                        : 0;
            return Mathf.Clamp(budget.SphericalHarmonicsDegree, 0, availableDegree);
        }

        private static SphericalHarmonicsSplat BuildSphericalHarmonicsSplat(GaussianSplatStream stream, int splatIndex)
        {
            if (stream.ShRest == null || stream.ShRestStride < 9)
            {
                return default;
            }

            var coefficientCountPerChannel = stream.ShRestStride / 3;
            var restOffset = splatIndex * stream.ShRestStride;
            if (coefficientCountPerChannel < 3 || restOffset + stream.ShRestStride > stream.ShRest.Length)
            {
                return default;
            }

            return new SphericalHarmonicsSplat
            {
                Sh1 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 0),
                Sh2 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 1),
                Sh3 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 2),
                Sh4 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 3),
                Sh5 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 4),
                Sh6 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 5),
                Sh7 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 6),
                Sh8 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 7),
                Sh9 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 8),
                Sh10 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 9),
                Sh11 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 10),
                Sh12 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 11),
                Sh13 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 12),
                Sh14 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 13),
                Sh15 = ToShCoefficientOrZero(stream, restOffset, coefficientCountPerChannel, 14)
            };
        }

        private static Vector4 ToShCoefficientOrZero(
            GaussianSplatStream stream,
            int restOffset,
            int coefficientCountPerChannel,
            int coefficientIndex)
        {
            if (coefficientIndex < 0 || coefficientIndex >= coefficientCountPerChannel)
            {
                return Vector4.zero;
            }

            return new Vector4(
                stream.ShRest[restOffset + coefficientIndex],
                stream.ShRest[restOffset + coefficientCountPerChannel + coefficientIndex],
                stream.ShRest[restOffset + coefficientCountPerChannel * 2 + coefficientIndex],
                0f);
        }

        private Color EstimateColor(GaussianSplatStream stream, int splatIndex)
        {
            var alpha = stream.OpacitiesRaw != null && splatIndex < stream.OpacitiesRaw.Length
                ? Sigmoid(stream.OpacitiesRaw[splatIndex])
                : 1f;

            if (stream.ShDc == null || stream.ShDc.Length < (splatIndex + 1) * 3)
            {
                return FinalizeGs3dColor(new Color(0f, 0f, 0f, alpha));
            }

            var offset = splatIndex * 3;
            return FinalizeGs3dColor(new Color(
                Mathf.Clamp01(0.5f + ShC0 * stream.ShDc[offset]),
                Mathf.Clamp01(0.5f + ShC0 * stream.ShDc[offset + 1]),
                Mathf.Clamp01(0.5f + ShC0 * stream.ShDc[offset + 2]),
                alpha));
        }

        private Color FinalizeGs3dColor(Color color)
        {
            if (!quantizeColorLikeGs3d)
            {
                return color;
            }

            return new Color(
                QuantizeGs3dByte(color.r),
                QuantizeGs3dByte(color.g),
                QuantizeGs3dByte(color.b),
                QuantizeGs3dByte(color.a));
        }

        private static float QuantizeGs3dByte(float value)
        {
            return Mathf.Floor(Mathf.Clamp01(value) * 255f) / 255f;
        }

        private void SortSplatsCpu(Camera sortCamera)
        {
            for (var i = 0; i < RenderedSplatCount; i++)
            {
                sortOrder[i] = i;
            }

            drawOrderDirty = true;

            if (!sortBackToFront || RenderedSplatCount <= 1)
            {
                return;
            }

            var cameraTransform = sortCamera != null ? sortCamera.transform : ResolveSortCamera()?.transform;
            if (cameraTransform == null)
            {
                return;
            }

            var cameraPosition = cameraTransform.position;
            var cameraForward = cameraTransform.forward;
            for (var i = 0; i < RenderedSplatCount; i++)
            {
                sortKeys[i] = -Vector3.Dot(centersWorld[i] - cameraPosition, cameraForward);
            }

            Array.Sort(sortKeys, sortOrder);
        }

        private bool TrySortSplatsGpu(Camera sortCamera)
        {
            if (!UseGpuDepthBucketSort || RenderedSplatCount <= 1)
            {
                return false;
            }

            var camera = sortCamera != null ? sortCamera : ResolveSortCamera();
            if (camera == null || !TryResolveSortDepthRange(camera, out var depthMin, out var depthInvRange))
            {
                return false;
            }

            try
            {
                var binCount = allocatedSortBinCount;
                sortComputeShader.SetInt("_SplatCount", RenderedSplatCount);
                sortComputeShader.SetInt("_SortBinCount", binCount);
                sortComputeShader.SetFloat("_DepthMin", depthMin);
                sortComputeShader.SetFloat("_DepthInvRange", depthInvRange);
                sortComputeShader.SetVector("_CameraPosition", camera.transform.position);
                sortComputeShader.SetVector("_CameraForward", camera.transform.forward.normalized);

                BindSortBuffers(sortClearKernel);
                BindSortBuffers(sortCountKernel);
                BindSortBuffers(sortPrefixKernel);
                BindSortBuffers(sortFillKernel);

                sortComputeShader.Dispatch(sortClearKernel, Mathf.CeilToInt(binCount / (float)SortThreadGroupSize), 1, 1);
                sortComputeShader.Dispatch(sortCountKernel, Mathf.CeilToInt(RenderedSplatCount / (float)SortThreadGroupSize), 1, 1);
                sortComputeShader.Dispatch(sortPrefixKernel, 1, 1, 1);
                sortComputeShader.Dispatch(sortFillKernel, Mathf.CeilToInt(RenderedSplatCount / (float)SortThreadGroupSize), 1, 1);

                drawOrderDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                gpuSortUnavailable = true;
                Debug.LogWarning("[Chimera GVRM] GPU depth-bucket sort failed; falling back to CPU exact sort. " + ex.Message);
                return false;
            }
        }

        private bool ShouldSortThisFrame(Camera sortCamera, bool splatFrameChanged)
        {
            if (!sortBackToFront || RenderedSplatCount <= 1)
            {
                return false;
            }

            if (!hasSortCameraState)
            {
                ResetSortCountdown();
                return true;
            }

            if (splatFrameChanged && !ShouldThrottleSkinningDrivenSorts())
            {
                ResetSortCountdown();
                return true;
            }

            if (!IsSortDueByInterval())
            {
                return false;
            }

            if (splatFrameChanged)
            {
                return true;
            }

            if (!skipStableSortFrames || sortCamera == null)
            {
                return true;
            }

            return HasSortCameraChanged(sortCamera);
        }

        private bool ShouldThrottleSkinningDrivenSorts()
        {
            return UseXrPerformanceBudget
                   && xrThrottleSkinningDrivenSorts
                   && EffectiveSortEveryNthFrame > 1;
        }

        private bool IsSortDueByInterval()
        {
            var sortEvery = EffectiveSortEveryNthFrame;
            if (sortEvery <= 0)
            {
                return true;
            }

            framesUntilSort--;
            if (framesUntilSort > 0)
            {
                return false;
            }

            ResetSortCountdown();
            return true;
        }

        private void ResetSortCountdown()
        {
            framesUntilSort = Mathf.Max(1, EffectiveSortEveryNthFrame);
        }

        private bool HasSortCameraChanged(Camera sortCamera)
        {
            if (!hasSortCameraState || sortCamera == null)
            {
                return true;
            }

            var positionDelta = sortCamera.transform.position - lastSortCameraPosition;
            var positionThreshold = Mathf.Max(0f, EffectiveSortCameraPositionEpsilonMeters);
            if (positionDelta.sqrMagnitude > positionThreshold * positionThreshold)
            {
                return true;
            }

            var angleThreshold = Mathf.Max(0f, EffectiveSortCameraAngleEpsilonDegrees);
            return Quaternion.Angle(sortCamera.transform.rotation, lastSortCameraRotation) > angleThreshold;
        }

        private void RememberSortCamera(Camera sortCamera)
        {
            if (sortCamera == null)
            {
                return;
            }

            hasSortCameraState = true;
            lastSortCameraPosition = sortCamera.transform.position;
            lastSortCameraRotation = sortCamera.transform.rotation;
        }

        private bool ShouldCullThisFrame(Camera camera, bool drawOrderOrSplatChanged)
        {
            if (!UseGpuVisibleCulling || RenderedSplatCount <= 0)
            {
                return false;
            }

            if (!visibleDrawReady || drawOrderOrSplatChanged || !hasCullCameraState)
            {
                return true;
            }

            return HasCullCameraChanged(camera);
        }

        private bool HasCullCameraChanged(Camera camera)
        {
            if (!hasCullCameraState || camera == null)
            {
                return true;
            }

            var positionDelta = camera.transform.position - lastCullCameraPosition;
            var positionThreshold = Mathf.Max(0f, EffectiveSortCameraPositionEpsilonMeters);
            if (positionDelta.sqrMagnitude > positionThreshold * positionThreshold)
            {
                return true;
            }

            var angleThreshold = Mathf.Max(0f, EffectiveSortCameraAngleEpsilonDegrees);
            return Quaternion.Angle(camera.transform.rotation, lastCullCameraRotation) > angleThreshold;
        }

        private void RememberCullCamera(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            hasCullCameraState = true;
            lastCullCameraPosition = camera.transform.position;
            lastCullCameraRotation = camera.transform.rotation;
        }

        private bool TryCullVisibleSplatsGpu(Camera camera)
        {
            if (!UseGpuVisibleCulling || RenderedSplatCount <= 0)
            {
                return false;
            }

            camera = camera != null ? camera : ResolveCamera();
            if (camera == null)
            {
                return false;
            }

            try
            {
                var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
                var viewProjection = gpuProjection * camera.worldToCameraMatrix;
                var focalX = Mathf.Abs(gpuProjection.m00) * Mathf.Max(1, camera.pixelWidth) * 0.5f;
                var focalY = Mathf.Abs(gpuProjection.m11) * Mathf.Max(1, camera.pixelHeight) * 0.5f;

                cullingComputeShader.SetInt("_SplatCount", RenderedSplatCount);
                cullingComputeShader.SetMatrix("_WorldToView", camera.worldToCameraMatrix);
                cullingComputeShader.SetMatrix("_ViewProjection", viewProjection);
                cullingComputeShader.SetVector(
                    "_FocalPixels",
                    new Vector4(focalX, focalY, Mathf.Max(focalX, focalY), 0f));
                cullingComputeShader.SetFloat("_SplatScale", Mathf.Max(0f, splatScale * (budget.SplatScale > 0f ? budget.SplatScale : 1f)));
                cullingComputeShader.SetFloat("_FrustumPadding", Mathf.Max(0f, cullingFrustumPadding));
                cullingComputeShader.SetFloat("_MinScreenRadiusPixels", Mathf.Max(0f, cullingMinScreenRadiusPixels));

                BindCullingClearKernel();
                BindCullingMarkKernel();
                BindCullingPrefixKernel();
                BindCullingCompactKernel();

                var cullGroups = Mathf.Max(1, Mathf.CeilToInt(RenderedSplatCount / (float)CullingThreadGroupSize));
                cullingComputeShader.Dispatch(cullClearKernel, 1, 1, 1);
                cullingComputeShader.Dispatch(
                    cullMarkKernel,
                    cullGroups,
                    1,
                    1);
                cullingComputeShader.Dispatch(cullPrefixKernel, 1, 1, 1);
                cullingComputeShader.Dispatch(cullCompactKernel, cullGroups, 1, 1);
                return true;
            }
            catch (Exception ex)
            {
                gpuCullingUnavailable = true;
                visibleDrawReady = false;
                Debug.LogWarning("[Chimera GVRM] GPU visible culling failed; falling back to direct procedural draw. " + ex.Message);
                return false;
            }
        }

        private void BindCullingClearKernel()
        {
            cullingComputeShader.SetBuffer(cullClearKernel, "_IndirectArgs", indirectArgsBuffer);
        }

        private void BindCullingMarkKernel()
        {
            cullingComputeShader.SetBuffer(cullMarkKernel, "_Splats", splatBuffer);
            cullingComputeShader.SetBuffer(cullMarkKernel, "_DrawOrder", drawOrderBuffer);
            cullingComputeShader.SetBuffer(cullMarkKernel, "_VisibleFlags", visibleFlagBuffer);
            cullingComputeShader.SetBuffer(cullMarkKernel, "_BlockCounts", cullBlockCountBuffer);
        }

        private void BindCullingPrefixKernel()
        {
            cullingComputeShader.SetBuffer(cullPrefixKernel, "_BlockCounts", cullBlockCountBuffer);
            cullingComputeShader.SetBuffer(cullPrefixKernel, "_BlockOffsets", cullBlockOffsetBuffer);
            cullingComputeShader.SetBuffer(cullPrefixKernel, "_IndirectArgs", indirectArgsBuffer);
        }

        private void BindCullingCompactKernel()
        {
            cullingComputeShader.SetBuffer(cullCompactKernel, "_DrawOrder", drawOrderBuffer);
            cullingComputeShader.SetBuffer(cullCompactKernel, "_VisibleDrawOrder", visibleDrawOrderBuffer);
            cullingComputeShader.SetBuffer(cullCompactKernel, "_VisibleFlags", visibleFlagBuffer);
            cullingComputeShader.SetBuffer(cullCompactKernel, "_BlockOffsets", cullBlockOffsetBuffer);
        }

        private void BindSortBuffers(int kernel)
        {
            sortComputeShader.SetBuffer(kernel, "_Splats", splatBuffer);
            sortComputeShader.SetBuffer(kernel, "_DrawOrder", drawOrderBuffer);
            sortComputeShader.SetBuffer(kernel, "_BinCounts", sortBinCountBuffer);
            sortComputeShader.SetBuffer(kernel, "_BinOffsets", sortBinOffsetBuffer);
        }

        private bool TryResolveSortDepthRange(Camera camera, out float depthMin, out float depthInvRange)
        {
            depthMin = 0f;
            depthInvRange = 1f;
            if (camera == null)
            {
                return false;
            }

            var forward = camera.transform.forward.normalized;
            var cameraPosition = camera.transform.position;
            var center = drawBounds.center;
            var extents = drawBounds.extents;
            if (extents.sqrMagnitude <= 1e-8f)
            {
                extents = Vector3.one * 0.5f;
            }

            var minDepth = float.PositiveInfinity;
            var maxDepth = float.NegativeInfinity;
            for (var x = -1; x <= 1; x += 2)
            {
                for (var y = -1; y <= 1; y += 2)
                {
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                        var depth = Vector3.Dot(corner - cameraPosition, forward);
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

            var range = Mathf.Max(0.01f, maxDepth - minDepth);
            var padding = Mathf.Max(0.05f, range * 0.02f);
            depthMin = minDepth - padding;
            depthInvRange = 1f / (range + padding * 2f);
            return true;
        }

        private Camera ResolveSortCamera()
        {
#if UNITY_EDITOR
            var sceneView = UnityEditor.SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.hasFocus && sceneView.camera != null)
            {
                return sceneView.camera;
            }
#endif
            return ResolveCamera();
        }

        private void UploadSplatBufferIfDirty()
        {
            if (!splatBufferDirty || splatBuffer == null || RenderedSplatCount == 0)
            {
                return;
            }

            splatBuffer.SetData(splats, 0, 0, RenderedSplatCount);
            splatBufferDirty = false;
        }

        private void UploadDrawOrderBufferIfDirty()
        {
            if (!drawOrderDirty || drawOrderBuffer == null || RenderedSplatCount == 0)
            {
                return;
            }

            for (var i = 0; i < RenderedSplatCount; i++)
            {
                drawOrderUpload[i] = (uint)sortOrder[i];
            }

            drawOrderBuffer.SetData(drawOrderUpload, 0, 0, RenderedSplatCount);
            drawOrderDirty = false;
        }

        private void HandleEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (ShouldRenderForCamera(camera, out var skipReason))
            {
                DrawSplats(context, camera);
                return;
            }

            RememberSkippedCamera(camera, skipReason);
        }

        private void HandleCameraPostRender(Camera camera)
        {
            if (RenderPipelineManager.currentPipeline != null)
            {
                return;
            }

            if (ShouldRenderForCamera(camera, out var skipReason))
            {
                DrawSplats(camera);
                return;
            }

            RememberSkippedCamera(camera, skipReason);
        }

        private bool ShouldRenderForCamera(Camera camera, out string skipReason)
        {
            if (!IsReady || camera == null || !isActiveAndEnabled)
            {
                skipReason = !IsReady ? "not-ready" : camera == null ? "null-camera" : "disabled";
                return false;
            }

            if (IsXrRenderingActive())
            {
                if (camera.cameraType != CameraType.Game)
                {
                    skipReason = $"xr-skip-{camera.cameraType}";
                    return false;
                }

                if (camera.stereoEnabled)
                {
                    skipReason = string.Empty;
                    return true;
                }

                skipReason = $"xr-skip-mono-game:{camera.name}";
                return false;
            }

            if (viewCamera == null)
            {
                if (camera.cameraType == CameraType.Game || camera == Camera.main)
                {
                    skipReason = string.Empty;
                    return true;
                }

                skipReason = $"non-game-{camera.cameraType}";
                return false;
            }

            if (camera == viewCamera)
            {
                skipReason = string.Empty;
                return true;
            }

            skipReason = $"not-view-camera:{camera.name}";
            return false;
        }

        private void DrawSplats(ScriptableRenderContext context, Camera camera)
        {
            var material = ResolveMaterial();
            if (!TryPrepareDrawProperties(material, camera, out var useIndirectVisibleDraw))
            {
                RememberSkippedCamera(camera, "draw-properties-unavailable");
                return;
            }

            var command = new CommandBuffer { name = "Chimera GVRM Procedural Splats" };
            try
            {
                if (useIndirectVisibleDraw)
                {
                    command.DrawProceduralIndirect(
                        Matrix4x4.identity,
                        material,
                        0,
                        MeshTopology.Triangles,
                        indirectArgsBuffer,
                        0,
                        propertyBlock);
                }
                else
                {
                    command.DrawProcedural(
                        Matrix4x4.identity,
                        material,
                        0,
                        MeshTopology.Triangles,
                        RenderedSplatCount * 6,
                        1,
                        propertyBlock);
                }

                context.ExecuteCommandBuffer(command);
                RememberDraw(camera, useIndirectVisibleDraw, "srp-end");
            }
            finally
            {
                command.Release();
            }
        }

        private void DrawSplats(Camera camera)
        {
            var material = ResolveMaterial();
            if (!TryPrepareDrawProperties(material, camera, out var useIndirectVisibleDraw))
            {
                RememberSkippedCamera(camera, "draw-properties-unavailable");
                return;
            }

#pragma warning disable 0618
            if (useIndirectVisibleDraw)
            {
                Graphics.DrawProceduralIndirect(
                    material,
                    drawBounds,
                    MeshTopology.Triangles,
                    indirectArgsBuffer,
                    0,
                    null,
                    propertyBlock,
                    ShadowCastingMode.Off,
                    false,
                    gameObject.layer);
                RememberDraw(camera, true, "camera-post");
                return;
            }

            var vertexCount = RenderedSplatCount * 6;
            Graphics.DrawProcedural(
                material,
                drawBounds,
                MeshTopology.Triangles,
                vertexCount,
                1,
                null,
                propertyBlock,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
            RememberDraw(camera, false, "camera-post");
#pragma warning restore 0618
        }

        private void RememberDraw(Camera camera, bool useIndirectVisibleDraw, string phase)
        {
            lastDrawFrame = Time.frameCount;
            lastDrawCameraName = camera != null ? camera.name : "unknown-camera";
            lastDrawPath = $"{phase}-{(useIndirectVisibleDraw ? "indirect" : IsXrRenderingActive() ? "full-xr-safe" : "full")}";
            lastDrawSkipReason = "none";

            var stereoEye = camera != null ? camera.stereoActiveEye.ToString() : "None";
            var signature = $"{lastDrawPath}|{lastDrawCameraName}|{camera?.cameraType}|xr:{IsXrRenderingActive()}|eye:{stereoEye}";
            if (!loggedFirstDraw
                || signature != lastLoggedDrawSignature
                || (logPeriodicDrawDiagnostics && Time.unscaledTime >= nextDrawDiagnosticLogTime))
            {
                loggedFirstDraw = true;
                lastLoggedDrawSignature = signature;
                nextDrawDiagnosticLogTime = Time.unscaledTime + 5f;
                Debug.Log(
                    "[Chimera GVRM] Procedural splat draw submitted: "
                    + $"path={lastDrawPath}, camera={lastDrawCameraName}, "
                    + $"cameraType={camera?.cameraType}, stereo={camera != null && camera.stereoEnabled}, "
                    + $"stereoEye={stereoEye}, "
                    + $"xrActive={IsXrRenderingActive()}, platform={Application.platform}, "
                    + $"graphics={SystemInfo.graphicsDeviceType}, runtimePath={runtimePath}, "
                    + $"projection={ProjectionDiagnostics}, "
                    + $"splats={RenderedSplatCount}, vertices={RenderedSplatCount * 6}.");
            }
        }

        private void RememberSkippedCamera(Camera camera, string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            lastDrawSkipReason = reason;
            if (camera != null && lastDrawCameraName == "none")
            {
                lastDrawCameraName = camera.name;
            }
        }

        private bool TryPrepareDrawProperties(Material material, Camera camera, out bool useIndirectVisibleDraw)
        {
            useIndirectVisibleDraw = false;
            lastProjectedSplatCacheUsed = false;
            if (material == null || splatBuffer == null || drawOrderBuffer == null || RenderedSplatCount == 0)
            {
                if (material == null && !warnedMissingMaterial)
                {
                    warnedMissingMaterial = true;
                    Debug.LogError(
                        "[Chimera GVRM] Procedural splat material is unavailable. "
                        + "Ensure Resources/Chimera/GvrmProceduralGaussianSplat.mat is included in the build.");
                }

                return false;
            }

            propertyBlock ??= new MaterialPropertyBlock();
            propertyBlock.Clear();
            propertyBlock.SetBuffer("_Splats", splatBuffer);
            propertyBlock.SetBuffer(
                "_DrawOrder",
                visibleDrawReady && visibleDrawOrderBuffer != null ? visibleDrawOrderBuffer : drawOrderBuffer);
            propertyBlock.SetFloat("_OpacityScale", opacityScale);
            propertyBlock.SetFloat("_SplatScale", Mathf.Max(0f, splatScale * (budget.SplatScale > 0f ? budget.SplatScale : 1f)));
            propertyBlock.SetFloat("_AlphaClip", Mathf.Max(0f, alphaClip));
            propertyBlock.SetFloat("_MinScreenRadiusPixels", Mathf.Max(0f, minScreenRadiusPixels));
            propertyBlock.SetFloat("_MaxScreenRadiusPixels", Mathf.Max(minScreenRadiusPixels, maxScreenRadiusPixels));
            propertyBlock.SetFloat("_Kernel2DSize", Mathf.Max(0f, kernel2DSize));
            propertyBlock.SetFloat("_AntialiasOpacityCompensation", Mathf.Clamp01(antialiasOpacityCompensation));
            propertyBlock.SetFloat("_EigenTermFloor", Mathf.Max(0f, eigenTermFloor));
            propertyBlock.SetFloat("_OpacityPower", Mathf.Max(0.25f, opacityPower));
            propertyBlock.SetFloat("_OpacityDensity", Mathf.Max(0.25f, opacityDensity));
            propertyBlock.SetFloat("_SurfaceContinuity", Mathf.Clamp01(surfaceContinuity));
            propertyBlock.SetFloat("_SurfaceContinuityKernelPixels", Mathf.Max(0f, surfaceContinuityKernelPixels));
            propertyBlock.SetFloat("_SurfaceContinuityShStability", Mathf.Clamp01(surfaceContinuityShStability));
            propertyBlock.SetFloat(
                "_LinearizeColor",
                linearizeColorInLinearProjects && QualitySettings.activeColorSpace == ColorSpace.Linear ? 1f : 0f);
            propertyBlock.SetFloat("_UseSphericalHarmonics", sphericalHarmonicsBuffer != null ? 1f : 0f);
            propertyBlock.SetFloat("_SphericalHarmonicsDegree", activeSphericalHarmonicsDegree);
            propertyBlock.SetFloat("_SphericalHarmonicsScale", Mathf.Max(0f, sphericalHarmonicsScale));
            useIndirectVisibleDraw = ShouldUseIndirectVisibleDraw();
            propertyBlock.SetFloat("_UseProceduralInstances", useIndirectVisibleDraw ? 1f : 0f);
            propertyBlock.SetMatrix("_WorldToGsLocal", BuildWorldToGsLocalMatrix());
            if (runtimePath == GvrmSplatRuntimePath.ProjectedSplatCache
                && projectedSplatBuffer == null
                && !gpuProjectionUnavailable)
            {
                EnsureProjectionGpuResources();
            }
            lastProjectedSplatCacheUsed = TryProjectSplatsGpu(camera);
            propertyBlock.SetFloat("_UseProjectedSplatCache", lastProjectedSplatCacheUsed ? 1f : 0f);
            propertyBlock.SetInt("_ProjectedSplatCacheEyeStride", RenderedSplatCount);
            propertyBlock.SetInt("_ProjectedSplatCacheEyeCount", lastProjectedSplatCacheUsed ? Mathf.Max(1, lastProjectedSplatCacheEyeCount) : 1);
            if (lastProjectedSplatCacheUsed)
            {
                propertyBlock.SetBuffer("_ProjectedSplats", projectedSplatBuffer);
            }
            if (sphericalHarmonicsBuffer != null)
            {
                propertyBlock.SetBuffer("_SphericalHarmonics", sphericalHarmonicsBuffer);
            }

            return true;
        }

        private bool TryProjectSplatsGpu(Camera camera)
        {
            if (!UseProjectedSplatCache || RenderedSplatCount <= 0 || splatBuffer == null || projectedSplatBuffer == null)
            {
                return false;
            }

            camera = camera != null ? camera : ResolveCamera();
            if (camera == null)
            {
                return false;
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                ResolveProjectionViewport(camera, out var width, out var height);
                var groups = Mathf.CeilToInt(RenderedSplatCount / (float)ProjectionThreadGroupSize);
                var dispatchGroups = Mathf.Max(1, groups);

                projectionComputeShader.SetInt("_SplatCount", RenderedSplatCount);
                projectionComputeShader.SetMatrix("_WorldToGsLocal", BuildWorldToGsLocalMatrix());
                projectionComputeShader.SetFloat("_SplatScale", Mathf.Max(0f, splatScale * (budget.SplatScale > 0f ? budget.SplatScale : 1f)));
                projectionComputeShader.SetFloat("_OpacityScale", opacityScale);
                projectionComputeShader.SetFloat("_MinScreenRadiusPixels", Mathf.Max(0f, minScreenRadiusPixels));
                projectionComputeShader.SetFloat("_MaxScreenRadiusPixels", Mathf.Max(minScreenRadiusPixels, maxScreenRadiusPixels));
                projectionComputeShader.SetFloat("_Kernel2DSize", Mathf.Max(0f, kernel2DSize));
                projectionComputeShader.SetFloat("_AntialiasOpacityCompensation", Mathf.Clamp01(antialiasOpacityCompensation));
                projectionComputeShader.SetFloat("_EigenTermFloor", Mathf.Max(0f, eigenTermFloor));
                projectionComputeShader.SetFloat("_SurfaceContinuity", Mathf.Clamp01(surfaceContinuity));
                projectionComputeShader.SetFloat("_SurfaceContinuityKernelPixels", Mathf.Max(0f, surfaceContinuityKernelPixels));
                projectionComputeShader.SetFloat("_SurfaceContinuityShStability", Mathf.Clamp01(surfaceContinuityShStability));
                projectionComputeShader.SetFloat("_UseSphericalHarmonics", sphericalHarmonicsBuffer != null ? 1f : 0f);
                projectionComputeShader.SetFloat("_SphericalHarmonicsDegree", activeSphericalHarmonicsDegree);
                projectionComputeShader.SetFloat("_SphericalHarmonicsScale", Mathf.Max(0f, sphericalHarmonicsScale));
                projectionComputeShader.SetFloat(
                    "_LinearizeColor",
                    linearizeColorInLinearProjects && QualitySettings.activeColorSpace == ColorSpace.Linear ? 1f : 0f);
                projectionComputeShader.SetBuffer(projectionKernel, "_Splats", splatBuffer);
                projectionComputeShader.SetBuffer(
                    projectionKernel,
                    "_SphericalHarmonics",
                    sphericalHarmonicsBuffer != null ? sphericalHarmonicsBuffer : projectionDummySphericalHarmonicsBuffer);
                projectionComputeShader.SetBuffer(projectionKernel, "_ProjectedSplats", projectedSplatBuffer);

                if (IsSinglePassStereoCamera(camera))
                {
                    ResolveStereoProjectionMatrices(camera, Camera.StereoscopicEye.Left, out var leftWorldToView, out var leftProjection);
                    DispatchProjectedSplats(leftWorldToView, leftProjection, 0, width, height, dispatchGroups);
                    ResolveStereoProjectionMatrices(camera, Camera.StereoscopicEye.Right, out var rightWorldToView, out var rightProjection);
                    DispatchProjectedSplats(rightWorldToView, rightProjection, RenderedSplatCount, width, height, dispatchGroups);
                    lastProjectedSplatCacheEyeCount = 2;
                }
                else
                {
                    ResolveProjectionMatrices(camera, out var worldToView, out var gpuProjection);
                    DispatchProjectedSplats(worldToView, gpuProjection, 0, width, height, dispatchGroups);
                    lastProjectedSplatCacheEyeCount = 1;
                }

                stopwatch.Stop();
                lastProjectionDispatchCpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                gpuProjectionUnavailable = true;
                lastProjectedSplatCacheUsed = false;
                Debug.LogWarning("[Chimera GVRM] Projected splat cache failed; falling back to the reference vertex-shader projection path. " + ex.Message);
                return false;
            }
        }

        private void DispatchProjectedSplats(
            Matrix4x4 worldToView,
            Matrix4x4 gpuProjection,
            int projectedSplatOffset,
            int width,
            int height,
            int dispatchGroups)
        {
            var viewProjection = gpuProjection * worldToView;
            projectionComputeShader.SetInt("_ProjectedSplatOffset", projectedSplatOffset);
            projectionComputeShader.SetMatrix("_WorldToView", worldToView);
            projectionComputeShader.SetMatrix("_ProjectionMatrix", gpuProjection);
            projectionComputeShader.SetMatrix("_ViewProjection", viewProjection);
            projectionComputeShader.SetVector("_CameraWorldPosition", ExtractCameraWorldPosition(worldToView));
            projectionComputeShader.SetVector("_ViewportSize", new Vector4(width, height, 1f / width, 1f / height));
            projectionComputeShader.Dispatch(projectionKernel, dispatchGroups, 1, 1);
        }

        private static void ResolveProjectionMatrices(
            Camera camera,
            out Matrix4x4 worldToView,
            out Matrix4x4 gpuProjection)
        {
            if (camera != null && camera.stereoEnabled)
            {
                if (camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                {
                    ResolveStereoProjectionMatrices(camera, Camera.StereoscopicEye.Left, out worldToView, out gpuProjection);
                    return;
                }

                if (camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                {
                    ResolveStereoProjectionMatrices(camera, Camera.StereoscopicEye.Right, out worldToView, out gpuProjection);
                    return;
                }
            }

            worldToView = camera != null ? camera.worldToCameraMatrix : Matrix4x4.identity;
            var projection = camera != null ? camera.projectionMatrix : Matrix4x4.identity;
            gpuProjection = GL.GetGPUProjectionMatrix(projection, false);
        }

        private static void ResolveStereoProjectionMatrices(
            Camera camera,
            Camera.StereoscopicEye eye,
            out Matrix4x4 worldToView,
            out Matrix4x4 gpuProjection)
        {
            worldToView = camera.GetStereoViewMatrix(eye);
            gpuProjection = GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), false);
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

        private static Vector3 ExtractCameraWorldPosition(Matrix4x4 worldToView)
        {
            var cameraToWorld = worldToView.inverse;
            return cameraToWorld.MultiplyPoint3x4(Vector3.zero);
        }

        private static bool IsSinglePassStereoCamera(Camera camera)
        {
            if (camera == null || !camera.stereoEnabled)
            {
                return false;
            }

            var activeEye = camera.stereoActiveEye;
            return camera != null
                   && activeEye != Camera.MonoOrStereoscopicEye.Left
                   && activeEye != Camera.MonoOrStereoscopicEye.Right;
        }

        private Matrix4x4 BuildWorldToGsLocalMatrix()
        {
            var gsLocalToWorld = transform.localToWorldMatrix * gsLocalToRenderer;
            return gsLocalToWorld.inverse;
        }

        private void EnsureGpuResources()
        {
            if (RenderedSplatCount <= 0)
            {
                return;
            }

            splatBuffer = new ComputeBuffer(RenderedSplatCount, ProceduralSplatStride, ComputeBufferType.Structured);
            drawOrderBuffer = new ComputeBuffer(RenderedSplatCount, sizeof(uint), ComputeBufferType.Structured);
            if (sphericalHarmonics.Length > 0)
            {
                sphericalHarmonicsBuffer = new ComputeBuffer(RenderedSplatCount, SphericalHarmonicsSplatStride, ComputeBufferType.Structured);
                sphericalHarmonicsBuffer.SetData(sphericalHarmonics, 0, 0, RenderedSplatCount);
            }

            EnsureSkinningGpuResources();
            EnsureProjectionGpuResources();
            EnsureSortGpuResources();
            EnsureCullingGpuResources();
        }

        private void EnsureSkinningGpuResources()
        {
            if (!preferGpuSkinning
                || !HasSkinning
                || skinningInputs.Length != RenderedSplatCount
                || vertexSkinningInputs.Length != meshPositions.Length
                || restSkinInverseMatrices.Length != meshPositions.Length)
            {
                return;
            }

            var compute = ResolveSkinningComputeShader();
            if (compute == null)
            {
                return;
            }

            try
            {
                vertexSkinningKernel = compute.FindKernel("SkinVertices");
                skinningKernel = compute.FindKernel("SkinSplats");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chimera GVRM] GPU splat skinning disabled because the compute kernel was not found: " + ex.Message);
                vertexSkinningKernel = -1;
                skinningKernel = -1;
                return;
            }

            skinningInputBuffer = new ComputeBuffer(RenderedSplatCount, SplatSkinningInputStride, ComputeBufferType.Structured);
            vertexSkinningInputBuffer = new ComputeBuffer(meshPositions.Length, VertexSkinningInputStride, ComputeBufferType.Structured);
            vertexSkinningStateBuffer = new ComputeBuffer(meshPositions.Length, VertexSkinningStateStride, ComputeBufferType.Structured);
            currentBoneMatrixBuffer = new ComputeBuffer(currentBoneMatrices.Length, BoneMatrixStride, ComputeBufferType.Structured);
            restSkinInverseMatrixBuffer = new ComputeBuffer(meshPositions.Length, BoneMatrixStride, ComputeBufferType.Structured);
            skinningInputBuffer.SetData(skinningInputs, 0, 0, RenderedSplatCount);
            vertexSkinningInputBuffer.SetData(vertexSkinningInputs, 0, 0, vertexSkinningInputs.Length);
            restSkinInverseMatricesGpu = new GpuMatrix4x4[meshPositions.Length];
            UploadMatricesForCompute(restSkinInverseMatrices, restSkinInverseMatricesGpu, restSkinInverseMatrixBuffer);
        }

        private ComputeShader ResolveSkinningComputeShader()
        {
            if (skinningComputeShader != null)
            {
                return skinningComputeShader;
            }

            skinningComputeShader = Resources.Load<ComputeShader>("Chimera/GvrmProceduralSplatSkinning");
            if (skinningComputeShader == null)
            {
                Debug.LogWarning("[Chimera GVRM] GPU splat skinning compute shader was not found; falling back to CPU-fed splat updates.");
            }

            return skinningComputeShader;
        }

        private void EnsureProjectionGpuResources()
        {
            if (runtimePath != GvrmSplatRuntimePath.ProjectedSplatCache || RenderedSplatCount <= 0)
            {
                return;
            }

            var compute = ResolveProjectionComputeShader();
            if (compute == null)
            {
                return;
            }

            try
            {
                projectionKernel = compute.FindKernel("ProjectSplats");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chimera GVRM] Projected splat cache disabled because the compute kernel was not found: " + ex.Message);
                projectionKernel = -1;
                gpuProjectionUnavailable = true;
                return;
            }

            projectedSplatBuffer = new ComputeBuffer(RenderedSplatCount * ProjectedSplatCacheMaxEyeCount, ProjectedSplatStride, ComputeBufferType.Structured);
            if (sphericalHarmonicsBuffer == null)
            {
                projectionDummySphericalHarmonicsBuffer = new ComputeBuffer(1, SphericalHarmonicsSplatStride, ComputeBufferType.Structured);
                projectionDummySphericalHarmonicsBuffer.SetData(new[] { default(SphericalHarmonicsSplat) });
            }
        }

        private ComputeShader ResolveProjectionComputeShader()
        {
            if (projectionComputeShader != null)
            {
                return projectionComputeShader;
            }

            projectionComputeShader = Resources.Load<ComputeShader>("Chimera/GvrmProceduralSplatProjection");
            if (projectionComputeShader == null)
            {
                Debug.LogWarning("[Chimera GVRM] Projected splat cache compute shader was not found; using the reference vertex-shader projection path.");
            }

            return projectionComputeShader;
        }

        private void EnsureSortGpuResources()
        {
            if (!preferGpuDepthBucketSort || !sortBackToFront || RenderedSplatCount <= 1)
            {
                return;
            }

            var compute = ResolveSortComputeShader();
            if (compute == null)
            {
                return;
            }

            try
            {
                sortClearKernel = compute.FindKernel("ClearBins");
                sortCountKernel = compute.FindKernel("CountBins");
                sortPrefixKernel = compute.FindKernel("PrefixBins");
                sortFillKernel = compute.FindKernel("FillDrawOrder");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chimera GVRM] GPU depth-bucket sort disabled because a compute kernel was not found: " + ex.Message);
                sortClearKernel = -1;
                sortCountKernel = -1;
                sortPrefixKernel = -1;
                sortFillKernel = -1;
                return;
            }

            allocatedSortBinCount = EffectiveDepthSortBinCount;
            sortBinCountBuffer = new ComputeBuffer(allocatedSortBinCount, sizeof(uint), ComputeBufferType.Structured);
            sortBinOffsetBuffer = new ComputeBuffer(allocatedSortBinCount, sizeof(uint), ComputeBufferType.Structured);
        }

        private ComputeShader ResolveSortComputeShader()
        {
            if (sortComputeShader != null)
            {
                return sortComputeShader;
            }

            sortComputeShader = Resources.Load<ComputeShader>("Chimera/GvrmProceduralSplatSort");
            if (sortComputeShader == null)
            {
                Debug.LogWarning("[Chimera GVRM] GPU depth-bucket sort compute shader was not found; falling back to CPU exact sort.");
            }

            return sortComputeShader;
        }

        private void EnsureCullingGpuResources()
        {
            if (!preferGpuVisibleCulling || ShouldDisableIndirectVisibleDrawInXr() || RenderedSplatCount <= 0)
            {
                return;
            }

            var compute = ResolveCullingComputeShader();
            if (compute == null)
            {
                return;
            }

            try
            {
                cullClearKernel = compute.FindKernel("ClearIndirectArgs");
                cullMarkKernel = compute.FindKernel("MarkVisible");
                cullPrefixKernel = compute.FindKernel("PrefixVisibleBlocks");
                cullCompactKernel = compute.FindKernel("CompactVisible");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Chimera GVRM] GPU visible culling disabled because a compute kernel was not found: " + ex.Message);
                cullClearKernel = -1;
                cullMarkKernel = -1;
                cullPrefixKernel = -1;
                cullCompactKernel = -1;
                return;
            }

            allocatedCullBlockCount = Mathf.Max(1, Mathf.CeilToInt(RenderedSplatCount / (float)CullingThreadGroupSize));
            visibleDrawOrderBuffer = new ComputeBuffer(RenderedSplatCount, sizeof(uint), ComputeBufferType.Structured);
            visibleFlagBuffer = new ComputeBuffer(RenderedSplatCount, sizeof(uint), ComputeBufferType.Structured);
            cullBlockCountBuffer = new ComputeBuffer(allocatedCullBlockCount, sizeof(uint), ComputeBufferType.Structured);
            cullBlockOffsetBuffer = new ComputeBuffer(allocatedCullBlockCount, sizeof(uint), ComputeBufferType.Structured);
            indirectArgsBuffer = new ComputeBuffer(
                IndirectArgsCount,
                sizeof(uint),
                ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);
        }

        private ComputeShader ResolveCullingComputeShader()
        {
            if (cullingComputeShader != null)
            {
                return cullingComputeShader;
            }

            cullingComputeShader = Resources.Load<ComputeShader>("Chimera/GvrmProceduralSplatCulling");
            if (cullingComputeShader == null)
            {
                Debug.LogWarning("[Chimera GVRM] GPU visible culling compute shader was not found; drawing the full sorted splat list.");
            }

            return cullingComputeShader;
        }

        private void ReleaseGpuResources()
        {
            if (splatBuffer != null)
            {
                splatBuffer.Release();
                splatBuffer = null;
            }

            if (drawOrderBuffer != null)
            {
                drawOrderBuffer.Release();
                drawOrderBuffer = null;
            }

            if (sphericalHarmonicsBuffer != null)
            {
                sphericalHarmonicsBuffer.Release();
                sphericalHarmonicsBuffer = null;
            }

            if (skinningInputBuffer != null)
            {
                skinningInputBuffer.Release();
                skinningInputBuffer = null;
            }

            if (vertexSkinningInputBuffer != null)
            {
                vertexSkinningInputBuffer.Release();
                vertexSkinningInputBuffer = null;
            }

            if (vertexSkinningStateBuffer != null)
            {
                vertexSkinningStateBuffer.Release();
                vertexSkinningStateBuffer = null;
            }

            if (projectedSplatBuffer != null)
            {
                projectedSplatBuffer.Release();
                projectedSplatBuffer = null;
            }

            if (projectionDummySphericalHarmonicsBuffer != null)
            {
                projectionDummySphericalHarmonicsBuffer.Release();
                projectionDummySphericalHarmonicsBuffer = null;
            }

            if (currentBoneMatrixBuffer != null)
            {
                currentBoneMatrixBuffer.Release();
                currentBoneMatrixBuffer = null;
            }

            if (restSkinInverseMatrixBuffer != null)
            {
                restSkinInverseMatrixBuffer.Release();
                restSkinInverseMatrixBuffer = null;
            }

            if (sortBinCountBuffer != null)
            {
                sortBinCountBuffer.Release();
                sortBinCountBuffer = null;
            }

            if (sortBinOffsetBuffer != null)
            {
                sortBinOffsetBuffer.Release();
                sortBinOffsetBuffer = null;
            }

            if (visibleDrawOrderBuffer != null)
            {
                visibleDrawOrderBuffer.Release();
                visibleDrawOrderBuffer = null;
            }

            if (visibleFlagBuffer != null)
            {
                visibleFlagBuffer.Release();
                visibleFlagBuffer = null;
            }

            if (cullBlockCountBuffer != null)
            {
                cullBlockCountBuffer.Release();
                cullBlockCountBuffer = null;
            }

            if (cullBlockOffsetBuffer != null)
            {
                cullBlockOffsetBuffer.Release();
                cullBlockOffsetBuffer = null;
            }

            if (indirectArgsBuffer != null)
            {
                indirectArgsBuffer.Release();
                indirectArgsBuffer = null;
            }
        }

        private Material ResolveMaterial()
        {
            if (runtimeMaterial != null)
            {
                return runtimeMaterial;
            }

            if (splatMaterial != null)
            {
                runtimeMaterial = new Material(splatMaterial)
                {
                    name = "Chimera Runtime Procedural GVRM Gaussian Splat"
                };
            }
            else
            {
                var resourceMaterial = Resources.Load<Material>("Chimera/GvrmProceduralGaussianSplat");
                if (resourceMaterial != null)
                {
                    runtimeMaterial = new Material(resourceMaterial)
                    {
                        name = "Chimera Runtime Procedural GVRM Gaussian Splat"
                    };
                }
            }

            if (runtimeMaterial == null)
            {
                var shader = Shader.Find("Chimera/GVRM Procedural Gaussian Splat");
                if (shader == null)
                {
                    return null;
                }

                runtimeMaterial = new Material(shader)
                {
                    name = "Chimera Runtime Procedural GVRM Gaussian Splat"
                };
            }

            runtimeMaterial.renderQueue = (int)RenderQueue.Transparent;
            runtimeMaterial.enableInstancing = true;
            return runtimeMaterial;
        }

        private Camera ResolveCamera()
        {
            if (viewCamera != null)
            {
                return viewCamera;
            }

            if (Camera.main != null)
            {
                viewCamera = Camera.main;
                return viewCamera;
            }

            return null;
        }

        private static Quaternion NormalizeQuaternion(Quaternion value)
        {
            var length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (length <= 1e-6f)
            {
                return Quaternion.identity;
            }

            var invLength = 1f / length;
            return new Quaternion(value.x * invLength, value.y * invLength, value.z * invLength, value.w * invLength);
        }

        private static float Sigmoid(float x)
        {
            if (x >= 0f)
            {
                var z = Mathf.Exp(-x);
                return 1f / (1f + z);
            }

            var z2 = Mathf.Exp(x);
            return z2 / (1f + z2);
        }

        private static bool IsIdentity(Matrix4x4 value)
        {
            var identity = Matrix4x4.identity;
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (!Mathf.Approximately(value[row, column], identity[row, column]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool MatrixApproximatelyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(left[row, column] - right[row, column]) > epsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProceduralSplat
        {
            public Vector4 CenterWorld;
            public Vector4 Axis0World;
            public Vector4 Axis1World;
            public Vector4 Axis2World;
            public Vector4 Color;
            public Vector4 Meta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SplatSkinningInput
        {
            public Vector4 MeshPosition;
            public Vector4 RelativePosition;
            public Vector4 Axis0RestMeshLocal;
            public Vector4 Axis1RestMeshLocal;
            public Vector4 Axis2RestMeshLocal;
            public Vector4 BoneIndices;
            public Vector4 BoneWeights;
            public Vector4 Color;
            public Vector4 Meta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VertexSkinningInput
        {
            public Vector4 MeshPosition;
            public Vector4 BoneIndices;
            public Vector4 BoneWeights;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuMatrix4x4
        {
            public Vector4 Row0;
            public Vector4 Row1;
            public Vector4 Row2;
            public Vector4 Row3;

            public static GpuMatrix4x4 FromUnityMatrix(Matrix4x4 value)
            {
                return new GpuMatrix4x4
                {
                    Row0 = new Vector4(value.m00, value.m01, value.m02, value.m03),
                    Row1 = new Vector4(value.m10, value.m11, value.m12, value.m13),
                    Row2 = new Vector4(value.m20, value.m21, value.m22, value.m23),
                    Row3 = new Vector4(value.m30, value.m31, value.m32, value.m33)
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SphericalHarmonicsSplat
        {
            public Vector4 Sh1;
            public Vector4 Sh2;
            public Vector4 Sh3;
            public Vector4 Sh4;
            public Vector4 Sh5;
            public Vector4 Sh6;
            public Vector4 Sh7;
            public Vector4 Sh8;
            public Vector4 Sh9;
            public Vector4 Sh10;
            public Vector4 Sh11;
            public Vector4 Sh12;
            public Vector4 Sh13;
            public Vector4 Sh14;
            public Vector4 Sh15;
        }

        private struct SplatImportance
        {
            public int Index;
            public float Score;
        }
    }
}
