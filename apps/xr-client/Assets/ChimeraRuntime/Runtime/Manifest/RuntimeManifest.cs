using System.Collections.Generic;
using Newtonsoft.Json;

namespace Chimera.Runtime
{
    public class RuntimeManifest
    {
        [JsonProperty("schemaVersion")] public string SchemaVersion;
        [JsonProperty("sessionCode")] public string SessionCode;
        [JsonProperty("issuedAt")] public string IssuedAt;
        [JsonProperty("expiresAt")] public string ExpiresAt;
        [JsonProperty("tracking")] public TrackingConfig Tracking;
        [JsonProperty("assets")] public ManifestAssets Assets;
        [JsonProperty("posterPanels")] public List<PosterPanel> PosterPanels;
        [JsonProperty("presentationCues")] public List<PresentationCue> PresentationCues;
        [JsonProperty("avatar")] public AvatarManifest Avatar;
        [JsonProperty("scripts")] public List<ScriptTrack> Scripts;
        [JsonProperty("qa")] public QaConfig Qa;
        [JsonProperty("features")] public FeaturesConfig Features;
    }

    public class TrackingConfig
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("referenceImageName")] public string ReferenceImageName;
        [JsonProperty("posterFormat")] public string PosterFormat;
        [JsonProperty("orientation")] public string Orientation;
        [JsonProperty("physicalSizeM")] public float[] PhysicalSizeM;
        [JsonProperty("qrFallback")] public QrFallback QrFallback;
        [JsonProperty("anchorOffset")] public AnchorOffset AnchorOffset;
    }

    public class QrFallback
    {
        [JsonProperty("enabled")] public bool Enabled;
        [JsonProperty("markerName")] public string MarkerName;
        [JsonProperty("placementHint")] public string PlacementHint;
    }

    public class AnchorOffset
    {
        [JsonProperty("position")] public float[] Position;
        [JsonProperty("rotation")] public float[] Rotation;
    }

    public class ManifestAssets
    {
        [JsonProperty("posterImage")] public SignedAssetRef PosterImage;
    }

    public class SignedAssetRef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("url")] public string Url;
        [JsonProperty("expiresAt")] public string ExpiresAt;
        [JsonProperty("mimeType")] public string MimeType;
        [JsonProperty("sizeBytes")] public long SizeBytes;
    }

    public class PosterPanel
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("label")] public string Label;
        [JsonProperty("orderIndex")] public int OrderIndex;
        [JsonProperty("bbox")] public PanelBbox Bbox;
        [JsonProperty("localBoundsM")] public PanelLocalBounds LocalBoundsM;
    }

    public class PanelBbox
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("width")] public float Width;
        [JsonProperty("height")] public float Height;
    }

    public class PanelLocalBounds
    {
        [JsonProperty("center")] public float[] Center;
        [JsonProperty("size")] public float[] Size;
    }

    public class PresentationCue
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("segmentId")] public string SegmentId;
        [JsonProperty("type")] public string Type;
        [JsonProperty("startMs")] public long StartMs;
        [JsonProperty("durationMs")] public long? DurationMs;
        [JsonProperty("payload")] public Dictionary<string, object> Payload;
    }

    public class AvatarManifest
    {
        [JsonProperty("runtimeType")] public string RuntimeType;
        [JsonProperty("format")] public string Format;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("attribution")] public string Attribution;
        [JsonProperty("licenseNote")] public string LicenseNote;
        [JsonProperty("defaultAnimation")] public string DefaultAnimation;
        [JsonProperty("behaviors")] public AvatarBehaviors Behaviors;
        [JsonProperty("placement")] public AvatarPlacement Placement;
        [JsonProperty("assetBundle")] public SignedAssetRef AssetBundle;
        [JsonProperty("metadata")] public GvrmMetadata Metadata;
        [JsonProperty("sources")] public Dictionary<string, SignedAssetRef> Sources;
    }

    public class AvatarBehaviors
    {
        [JsonProperty("idle")] public string Idle;
        [JsonProperty("explain")] public string Explain;
        [JsonProperty("listening")] public string Listening;
        [JsonProperty("thinking")] public string Thinking;
    }

    public class AvatarPlacement
    {
        [JsonProperty("relativeTo")] public string RelativeTo;
        [JsonProperty("position")] public float[] Position;
        [JsonProperty("rotation")] public float[] Rotation;
        [JsonProperty("scale")] public float Scale = 1f;
    }

    public class ScriptTrack
    {
        [JsonProperty("profile")] public string Profile;
        [JsonProperty("language")] public string Language;
        [JsonProperty("segments")] public List<ScriptSegment> Segments;
    }

    public class ScriptSegment
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("panelId")] public string PanelId;
        [JsonProperty("order")] public int Order;
        [JsonProperty("type")] public string Type;
        [JsonProperty("text")] public string Text;
        [JsonProperty("durationEstimateSec")] public int? DurationEstimateSec;
        [JsonProperty("evidenceChunkIds")] public List<string> EvidenceChunkIds;
        [JsonProperty("audio")] public SignedAssetRef Audio;
        [JsonProperty("voice")] public VoiceInfo Voice;
    }

    public class VoiceInfo
    {
        [JsonProperty("provider")] public string Provider;
        [JsonProperty("voiceId")] public string VoiceId;
        [JsonProperty("modelId")] public string ModelId;
    }

    public class QaConfig
    {
        [JsonProperty("websocketUrl")] public string WebsocketUrl;
        [JsonProperty("runtimeToken")] public string RuntimeToken;
        [JsonProperty("runtimeTokenExpiresAt")] public string RuntimeTokenExpiresAt;
        [JsonProperty("defaultProfile")] public string DefaultProfile;
        [JsonProperty("defaultLanguage")] public string DefaultLanguage;
        [JsonProperty("supportedProfiles")] public List<string> SupportedProfiles;
        [JsonProperty("supportedLanguages")] public List<string> SupportedLanguages;
        [JsonProperty("allowEscalation")] public bool AllowEscalation;
        [JsonProperty("approvedSimulatedQas")] public List<ApprovedQa> ApprovedSimulatedQas;
    }

    public class ApprovedQa
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("profile")] public string Profile;
        [JsonProperty("language")] public string Language;
        [JsonProperty("panelId")] public string PanelId;
        [JsonProperty("question")] public string Question;
        [JsonProperty("answer")] public string Answer;
        [JsonProperty("evidenceChunkIds")] public List<string> EvidenceChunkIds;
    }

    public class FeaturesConfig
    {
        [JsonProperty("liveQa")] public bool LiveQa;
        [JsonProperty("dashboardRemoteControl")] public bool DashboardRemoteControl;
        [JsonProperty("handTracking")] public bool HandTracking;
    }
}
