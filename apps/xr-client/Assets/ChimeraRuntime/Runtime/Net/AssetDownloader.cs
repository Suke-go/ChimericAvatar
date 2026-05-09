using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Chimera.Runtime
{
    public class AssetDownloader
    {
        private readonly string _cacheRoot;
        private readonly Dictionary<string, byte[]> _memoryCache = new Dictionary<string, byte[]>();

        public AssetDownloader(string cacheRoot = null)
        {
            _cacheRoot = cacheRoot ?? Path.Combine(Application.persistentDataPath, "chimera-cache");
            Directory.CreateDirectory(_cacheRoot);
        }

        public string ResolveCachePath(string assetId) => Path.Combine(_cacheRoot, assetId);

        public async Task<byte[]> GetBytesAsync(SignedAssetRef asset, CancellationToken ct = default)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrEmpty(asset.Url)) throw new ArgumentException("Asset URL missing", nameof(asset));
            if (_memoryCache.TryGetValue(asset.Id, out var cached)) return cached;

            string diskPath = ResolveCachePath(asset.Id);
            if (File.Exists(diskPath))
            {
                byte[] fromDisk = await Task.Run(() => File.ReadAllBytes(diskPath), ct);
                _memoryCache[asset.Id] = fromDisk;
                return fromDisk;
            }

            using var request = UnityWebRequest.Get(asset.Url);
            request.downloadHandler = new DownloadHandlerBuffer();
            await UnityWebRequestExtensions.SendAsync(request, ct);
            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new AssetDownloadException(
                    $"Asset {asset.Id} download failed: {(int)request.responseCode} {request.error}");
            }
            byte[] data = request.downloadHandler.data;
            File.WriteAllBytes(diskPath, data);
            _memoryCache[asset.Id] = data;
            return data;
        }

        public async Task<Texture2D> GetTextureAsync(SignedAssetRef asset, CancellationToken ct = default)
        {
            byte[] bytes = await GetBytesAsync(asset, ct);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.Destroy(tex);
                throw new AssetDownloadException($"Asset {asset.Id} is not a valid image");
            }
            tex.name = asset.Id;
            return tex;
        }

        public async Task<AudioClip> GetMp3Async(SignedAssetRef asset, string clipName = null, CancellationToken ct = default)
        {
            await GetBytesAsync(asset, ct);
            string fileUri = new Uri(ResolveCachePath(asset.Id)).AbsoluteUri;
            using var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.MPEG);
            await UnityWebRequestExtensions.SendAsync(request, ct);
            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new AssetDownloadException($"Audio decode failed for {asset.Id}: {request.error}");
            }
            var clip = DownloadHandlerAudioClip.GetContent(request);
            clip.name = clipName ?? asset.Id;
            return clip;
        }

        public void EvictMemory(string assetId) => _memoryCache.Remove(assetId);
        public void Clear() => _memoryCache.Clear();
    }

    public class AssetDownloadException : Exception
    {
        public AssetDownloadException(string message) : base(message) { }
    }
}
