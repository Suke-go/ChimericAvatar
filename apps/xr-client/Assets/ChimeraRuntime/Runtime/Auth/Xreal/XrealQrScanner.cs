using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// XREAL One Pro + XREAL Eye QR scanner backed by XREAL SDK 3.1.0.
    ///
    /// Build gate:
    ///   CHIMERA_XREAL_SDK + CHIMERA_ZXING
    ///
    /// Phase 3 work fills in:
    ///   - Open NRSDK.NRRGBCamera or NRSDK.NRYUVCamera frame stream
    ///   - Decode with ZXing.BarcodeReader (QR_CODE only)
    ///   - Validate chimera:// prefix and emit SessionJoinToken
    /// </summary>
    public class XrealQrScanner : MonoBehaviour, ISessionEntryProvider
    {
        public Task<SessionJoinToken> RequestEntryAsync(CancellationToken ct)
        {
#if CHIMERA_XREAL_SDK && CHIMERA_ZXING
            // TODO Phase 3: see MetaPassthroughQrScanner sketch; replace OVR with NRSDK.
            return Task.FromResult<SessionJoinToken>(null);
#else
            Debug.LogWarning(
                "[Chimera] XrealQrScanner present but CHIMERA_XREAL_SDK / CHIMERA_ZXING are not defined. " +
                "Falling back to null entry; wire ManualSessionEntry on this scene.");
            return Task.FromResult<SessionJoinToken>(null);
#endif
        }
    }
}
