using System;
using Diagnostics = System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace SplatPack.Runtime
{
    [DisallowMultipleComponent]
    public sealed class SplatPackRenderer : MonoBehaviour
    {
        private const int ProjectionThreadGroupSize = 64;
        private const int MaxProjectedEyes = 2;

        [Header("Input")]
        [SerializeField] private SplatPackAsset asset;
        [SerializeField] private TextAsset packageBytes;

        [Header("Rendering")]
        [SerializeField] private Material splatMaterial;
        [SerializeField] private ComputeShader projectionCompute;
        [SerializeField] private Camera targetCamera;

        [Header("Placement")]
        [SerializeField] private bool recenterInFrontOfCameraOnFirstDraw;
        [SerializeField] private float recenterDistanceMeters = 3f;

        [Header("Quality")]
        [SerializeField] private float opacityScale = 1f;
        [SerializeField] private float splatScale = 1f;
        [SerializeField] private float alphaClip;
        [SerializeField] private float minScreenRadiusPixels;
        [SerializeField] private float maxScreenRadiusPixels = 1024f;
        [SerializeField] private float kernel2DSize = 0.3f;
        [SerializeField] private float eigenTermFloor = 0.1f;

        [Header("Diagnostics")]
        [SerializeField] private bool logDiagnostics = true;
        [SerializeField] private float diagnosticIntervalSeconds = 5f;

        private SplatPackPackage package;
        private ComputeBuffer splatBuffer;
        private ComputeBuffer drawOrderBuffer;
        private ComputeBuffer projectedBuffer;
        private MaterialPropertyBlock propertyBlock;
        private int projectionKernel = -1;
        private bool subscribed;
        private bool loggedFirstDiagnostics;
        private float nextDiagnosticLogTime;
        private float lastProjectionDispatchCpuMs;
        private int lastProjectionEyeCount;
        private bool didRecenter;
        private bool warnedUnsupportedMaterial;

        public void Configure(
            SplatPackAsset packageAsset,
            ComputeShader projection,
            Material material,
            Camera camera = null,
            bool recenterOnFirstDraw = false)
        {
            asset = packageAsset;
            projectionCompute = projection;
            splatMaterial = material;
            targetCamera = camera;
            recenterInFrontOfCameraOnFirstDraw = recenterOnFirstDraw;
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

            var drawOrder = new uint[package.SplatCount];
            for (uint i = 0; i < drawOrder.Length; i++)
            {
                drawOrder[i] = i;
            }

            drawOrderBuffer = new ComputeBuffer(package.SplatCount, sizeof(uint), ComputeBufferType.Structured);
            drawOrderBuffer.SetData(drawOrder);

            projectedBuffer = new ComputeBuffer(package.SplatCount * MaxProjectedEyes, SplatPackFormat.ProjectedSplatStride, ComputeBufferType.Structured);
            propertyBlock ??= new MaterialPropertyBlock();

            if (projectionCompute != null)
            {
                projectionKernel = projectionCompute.FindKernel("ProjectSplats");
            }

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

#pragma warning disable 0618
            Graphics.DrawProcedural(
                material,
                ResolveDrawBounds(),
                MeshTopology.Triangles,
                package.SplatCount * 6,
                ResolveProceduralDrawInstanceCount(camera),
                null,
                propertyBlock,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
#pragma warning restore 0618
            MaybeLogDiagnostics(camera, "camera-post");
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
            int eyeCount = DispatchProjectedSplats(camera);
            propertyBlock.Clear();
            propertyBlock.SetBuffer("_Splats", splatBuffer);
            propertyBlock.SetBuffer("_DrawOrder", drawOrderBuffer);
            propertyBlock.SetBuffer("_ProjectedSplats", projectedBuffer);
            propertyBlock.SetFloat("_OpacityScale", Mathf.Max(0f, opacityScale));
            propertyBlock.SetFloat("_SplatScale", Mathf.Max(0f, splatScale));
            propertyBlock.SetFloat("_AlphaClip", Mathf.Max(0f, alphaClip));
            propertyBlock.SetInt("_ProjectedSplatCacheEyeStride", package.SplatCount);
            propertyBlock.SetInt("_ProjectedSplatCacheEyeCount", eyeCount);
            material.SetFloat("_UseProjectedSplatCache", 1f);
            return true;
        }

        private int DispatchProjectedSplats(Camera camera)
        {
            Diagnostics.Stopwatch stopwatch = Diagnostics.Stopwatch.StartNew();
            int width;
            int height;
            ResolveProjectionViewport(camera, out width, out height);
            int groups = Mathf.CeilToInt(package.SplatCount / (float)ProjectionThreadGroupSize);

            projectionCompute.SetBuffer(projectionKernel, "_Splats", splatBuffer);
            projectionCompute.SetBuffer(projectionKernel, "_ProjectedSplats", projectedBuffer);
            projectionCompute.SetInt("_SplatCount", package.SplatCount);
            projectionCompute.SetFloat("_SplatScale", Mathf.Max(0f, splatScale));
            projectionCompute.SetFloat("_OpacityScale", Mathf.Max(0f, opacityScale));
            projectionCompute.SetFloat("_MinScreenRadiusPixels", Mathf.Max(0f, minScreenRadiusPixels));
            projectionCompute.SetFloat("_MaxScreenRadiusPixels", Mathf.Max(minScreenRadiusPixels, maxScreenRadiusPixels));
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
                return 2;
            }

            Matrix4x4 worldToView = camera != null ? camera.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 projection = camera != null ? camera.projectionMatrix : Matrix4x4.identity;
            DispatchProjection(worldToView, GL.GetGPUProjectionMatrix(projection, false), 0, width, height, groups);
            stopwatch.Stop();
            lastProjectionDispatchCpuMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            lastProjectionEyeCount = 1;
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
                + $"projection={lastProjectionDispatchCpuMs:0.###}ms-cpu/{Mathf.Max(1, lastProjectionEyeCount)}eye, "
                + $"splats={package.SplatCount}, chunks={package.ChunkCount}, "
                + $"vertices={package.SplatCount * 6 * ResolveProceduralDrawInstanceCount(camera)}.");
        }

        private void ReleaseBuffers()
        {
            splatBuffer?.Release();
            drawOrderBuffer?.Release();
            projectedBuffer?.Release();
            splatBuffer = null;
            drawOrderBuffer = null;
            projectedBuffer = null;
        }
    }
}
