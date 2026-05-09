using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Quest 3 QR scanner backed by Meta XR Passthrough Camera API + ZXing.
    ///
    /// Build gates:
    ///   CHIMERA_META_PASSTHROUGH_CAMERA  Passthrough Camera API available (Meta XR SDK)
    ///   CHIMERA_ZXING                     ZXing.Net plugin imported under /Plugins
    ///
    /// Phase 2 work fills in:
    ///   - Subscribe to passthrough camera feed (CPU YUV / RGB)
    ///   - Decode each frame with ZXing.BarcodeReader (limit to QR_CODE)
    ///   - Validate "chimera://session?..." prefix; ignore other QRs
    ///   - Surface the parsed SessionJoinToken via the awaited Task
    /// </summary>
    public class MetaPassthroughQrScanner : MonoBehaviour, ISessionEntryProvider
    {
        public Task<SessionJoinToken> RequestEntryAsync(CancellationToken ct)
        {
#if CHIMERA_META_PASSTHROUGH_CAMERA && CHIMERA_ZXING
            // TODO Phase 2: real implementation. Sketch:
            //
            //   var tcs = new TaskCompletionSource<SessionJoinToken>();
            //   var camera = OVRPassthroughLayer.AcquireCamera();   // pseudo-name
            //   var reader = new ZXing.BarcodeReader { ... };
            //   camera.OnFrame += frame => {
            //       var result = reader.Decode(frame.Pixels, frame.Width, frame.Height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
            //       if (result == null) return;
            //       if (!SessionJoinToken.TryParse(result.Text, out var token, out _)) return;
            //       tcs.TrySetResult(token);
            //   };
            //   ct.Register(() => tcs.TrySetCanceled());
            //   return tcs.Task;
            return Task.FromResult<SessionJoinToken>(null);
#else
            Debug.LogWarning(
                "[Chimera] MetaPassthroughQrScanner present but CHIMERA_META_PASSTHROUGH_CAMERA / CHIMERA_ZXING are not defined. " +
                "Falling back to null entry; wire ManualSessionEntry on this scene.");
            return Task.FromResult<SessionJoinToken>(null);
#endif
        }
    }
}
