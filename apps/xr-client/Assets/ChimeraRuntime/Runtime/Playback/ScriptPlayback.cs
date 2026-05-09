using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Chimera.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public class ScriptPlayback : MonoBehaviour
    {
        public AudioSource AudioSource;

        public event Action<ScriptSegment> OnSegmentStart;
        public event Action<ScriptSegment> OnSegmentEnd;
        public event Action<PresentationCue> OnCueFire;
        public event Action OnTrackComplete;

        public bool IsPlaying { get; private set; }
        public ScriptTrack CurrentTrack { get; private set; }
        public ScriptSegment CurrentSegment { get; private set; }

        private Coroutine _coroutine;
        private AssetDownloader _downloader;
        private CancellationTokenSource _cts;

        public void Configure(AssetDownloader downloader)
        {
            _downloader = downloader;
            if (AudioSource == null) AudioSource = GetComponent<AudioSource>();
        }

        public void Play(ScriptTrack track, IList<PresentationCue> cues = null)
        {
            Stop();
            if (track == null || track.Segments == null || track.Segments.Count == 0) return;
            CurrentTrack = track;
            _cts = new CancellationTokenSource();
            _coroutine = StartCoroutine(PlayCoroutine(track, cues, _cts.Token));
        }

        public void Stop()
        {
            if (_coroutine != null) { StopCoroutine(_coroutine); _coroutine = null; }
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            if (AudioSource != null && AudioSource.isPlaying) AudioSource.Stop();
            IsPlaying = false;
            CurrentSegment = null;
        }

        private IEnumerator PlayCoroutine(ScriptTrack track, IList<PresentationCue> cues, CancellationToken ct)
        {
            IsPlaying = true;
            float trackStart = Time.time;
            int cueIndex = 0;
            cues ??= Array.Empty<PresentationCue>();

            foreach (var segment in track.Segments)
            {
                if (ct.IsCancellationRequested) break;
                CurrentSegment = segment;

                AudioClip clip = null;
                if (segment.Audio != null && _downloader != null)
                {
                    var fetch = _downloader.GetMp3Async(segment.Audio, $"seg-{segment.Id}", ct);
                    while (!fetch.IsCompleted) yield return null;
                    if (fetch.IsFaulted)
                    {
                        Debug.LogWarning($"[Chimera] segment {segment.Id} audio fetch failed: {fetch.Exception?.GetBaseException().Message}");
                    }
                    else
                    {
                        clip = fetch.Result;
                    }
                }

                OnSegmentStart?.Invoke(segment);

                if (clip != null && AudioSource != null)
                {
                    AudioSource.clip = clip;
                    AudioSource.Play();
                    while (AudioSource.isPlaying && !ct.IsCancellationRequested)
                    {
                        FireDueCues(cues, ref cueIndex, (long)((Time.time - trackStart) * 1000f));
                        yield return null;
                    }
                }
                else
                {
                    int waitMs = Mathf.Max(1, segment.DurationEstimateSec.GetValueOrDefault(5)) * 1000;
                    int elapsed = 0;
                    while (elapsed < waitMs && !ct.IsCancellationRequested)
                    {
                        FireDueCues(cues, ref cueIndex, (long)((Time.time - trackStart) * 1000f));
                        yield return null;
                        elapsed += (int)(Time.unscaledDeltaTime * 1000);
                    }
                }

                OnSegmentEnd?.Invoke(segment);
                CurrentSegment = null;
            }

            IsPlaying = false;
            OnTrackComplete?.Invoke();
        }

        private void FireDueCues(IList<PresentationCue> cues, ref int index, long elapsedMs)
        {
            while (index < cues.Count && cues[index].StartMs <= elapsedMs)
            {
                OnCueFire?.Invoke(cues[index]);
                index++;
            }
        }

        private void OnDestroy() => Stop();
    }
}
