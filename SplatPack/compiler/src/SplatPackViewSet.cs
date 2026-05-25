using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplatPack.Compiler;

public enum SplatPackProbeEye
{
    Mono,
    Left,
    Right,
}

public readonly record struct SplatPackViewSample(
    Vector3 Position,
    Vector3 Forward,
    float Weight,
    SplatPackProbeEye Eye = SplatPackProbeEye.Mono,
    float IpdMeters = 0.064f,
    float FovYDegrees = 70f,
    float Aspect = 1f);

public static class SplatPackViewSet
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SplatPackViewSample[] Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        JsonElement root = document.RootElement;
        JsonElement viewsElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("views", out JsonElement nestedViews)
                ? nestedViews
                : throw new InvalidDataException("Viewset JSON must be an array or an object with a 'views' array.");

        var views = new List<SplatPackViewSample>();
        foreach (JsonElement viewElement in viewsElement.EnumerateArray())
        {
            Vector3 position = ReadVector3(viewElement, "position", Vector3.Zero);
            Vector3 forward = ReadVector3(viewElement, "forward", Vector3.UnitZ);
            float weight = ReadSingle(viewElement, "weight", 1f);
            SplatPackProbeEye eye = ReadEye(viewElement, "eye", SplatPackProbeEye.Mono);
            float ipd = ReadSingle(viewElement, "ipdMeters", 0.064f);
            float fovY = ReadSingle(viewElement, "fovYDegrees", 70f);
            float aspect = ReadSingle(viewElement, "aspect", 1f);
            if (!IsFinite(position) || !IsFinite(forward) || forward.LengthSquared() <= 1e-12f || !float.IsFinite(weight) || weight <= 0f)
            {
                continue;
            }

            views.Add(new SplatPackViewSample(
                position,
                Vector3.Normalize(forward),
                weight,
                eye,
                Math.Clamp(ipd, 0f, 0.2f),
                Math.Clamp(fovY, 20f, 140f),
                Math.Clamp(aspect, 0.25f, 4f)));
        }

        if (views.Count == 0)
        {
            throw new InvalidDataException($"Viewset contains no usable views: {path}");
        }

        return views.ToArray();
    }

    public static void WriteDefault(string path)
    {
        var dto = new ViewSetDto
        {
            Views =
            [
                new ViewDto { Position = [0f, 1.6f, -3f], Forward = [0f, -0.1f, 1f], Weight = 1f },
                new ViewDto { Position = [2.5f, 1.6f, -1f], Forward = [-0.8f, -0.05f, 0.6f], Weight = 0.8f },
                new ViewDto { Position = [-2.5f, 1.6f, -1f], Forward = [0.8f, -0.05f, 0.6f], Weight = 0.8f },
                new ViewDto { Position = [0f, 1.6f, 3f], Forward = [0f, -0.05f, -1f], Weight = 0.6f },
            ],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Vector3 ReadVector3(JsonElement element, string propertyName, Vector3 fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 3)
        {
            return new Vector3(
                value[0].GetSingle(),
                value[1].GetSingle(),
                value[2].GetSingle());
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return new Vector3(
                ReadSingle(value, "x", fallback.X),
                ReadSingle(value, "y", fallback.Y),
                ReadSingle(value, "z", fallback.Z));
        }

        return fallback;
    }

    private static float ReadSingle(JsonElement element, string propertyName, float fallback)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetSingle(out float result)
            ? result
            : fallback;
    }

    private static SplatPackProbeEye ReadEye(JsonElement element, string propertyName, SplatPackProbeEye fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text?.ToLowerInvariant() switch
        {
            "left" => SplatPackProbeEye.Left,
            "right" => SplatPackProbeEye.Right,
            "mono" => SplatPackProbeEye.Mono,
            _ => fallback,
        };
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private sealed class ViewSetDto
    {
        [JsonPropertyName("views")]
        public required ViewDto[] Views { get; init; }
    }

    private sealed class ViewDto
    {
        [JsonPropertyName("position")]
        public required float[] Position { get; init; }

        [JsonPropertyName("forward")]
        public required float[] Forward { get; init; }

        [JsonPropertyName("weight")]
        public required float Weight { get; init; }
    }
}
