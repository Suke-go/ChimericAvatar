using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chimera.Runtime
{
    /// <summary>
    /// Anything that can hand a <see cref="SessionJoinToken"/> back to ChimeraSession.
    /// Concrete implementations:
    ///   Auth/Manual/ManualSessionEntry.cs           inspector / debug
    ///   Auth/Meta/MetaPassthroughQrScanner.cs       Quest 3 passthrough camera + ZXing
    ///   Auth/Xreal/XrealQrScanner.cs                XREAL SDK 3.1.0 image / camera
    ///
    /// Returns <c>null</c> if the user cancels.
    /// </summary>
    public interface ISessionEntryProvider
    {
        Task<SessionJoinToken> RequestEntryAsync(CancellationToken ct);
    }
}
