using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;

namespace noWickyXIV;

// Single audio layer (one .ogg or .wav file) with looping +
// crossfade-friendly volume control + speed-based pitch shifting.
//
// Used by MountAudio to manage idle / cruise / accel / decel /
// mount / dismount tracks separately so each can be playing,
// fading in, fading out, or pitched at its own setting.
//
// NAudio choices:
//   - Vorbis decoder via NAudio.Vorbis (.ogg files — common audio
//     edit format, decent compression for engine loops).
//   - WaveOutEvent for output. WasapiOut is lower latency but more
//     fragile across audio device changes. Mounting/dismounting is
//     not a sub-50ms-critical event so WaveOutEvent is fine.
//   - SmbPitchShiftingSampleProvider for pitch-with-tempo so we
//     can drive cruise rev with speed without changing playback
//     duration (regular speed-shift would also speed up the loop).
public sealed class MountAudioLayer : ILoopLayer
{
    private readonly bool _loop;
    private readonly string _path;
    private readonly float _baseVolume;

    private IWaveProvider _source;
    private VolumeSampleProvider _volumeProvider;
    private SmbPitchShiftingSampleProvider _pitchProvider;
    private WaveOutEvent _output;
    private LoopStream _loopStream;
    // Underlying WaveStream (AudioFileReader / VorbisWaveReader).
    // Stored so MountAudio can query RemainingSeconds for one-shots
    // and overlap the next layer in before this one fully ends.
    private WaveStream _rawStream;
    private bool _playing;

    // Volume envelope state. _targetVolume is what we want; _volume
    // lerps toward it at _envelopeRate per second so transitions
    // crossfade smoothly. baseVol scales the final output so the
    // user's master Mount Audio Volume slider takes effect without
    // the per-layer logic having to know about it.
    private float _volume;
    private float _targetVolume;
    private float _envelopeRate = 4f; // 1/s, ~250 ms halflife

    // Try to construct. Returns null if the file doesn't exist or
    // decode fails — caller treats null as "this layer just isn't
    // used in this audio pack."
    public static MountAudioLayer TryCreate(string path, bool loop, float baseVol)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var layer = new MountAudioLayer(path, loop, baseVol);
            layer.OpenStream();
            return layer;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] MountAudioLayer load failed for '{path}': {ex.Message}"); } catch { }
            return null;
        }
    }

    private MountAudioLayer(string path, bool loop, float baseVol)
    {
        _path = path;
        _loop = loop;
        _baseVolume = Math.Clamp(baseVol, 0f, 1f);
    }

    private void OpenStream()
    {
        _rawStream = _path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            ? (WaveStream)new VorbisWaveReader(_path)
            : new AudioFileReader(_path);

        ISampleProvider sampleSrc;
        if (_loop)
        {
            _loopStream = new LoopStream(_rawStream);
            sampleSrc = _loopStream.ToSampleProvider();
        }
        else
        {
            sampleSrc = _rawStream.ToSampleProvider();
        }

        _pitchProvider = new SmbPitchShiftingSampleProvider(sampleSrc) { PitchFactor = 1f };
        _volumeProvider = new VolumeSampleProvider(_pitchProvider) { Volume = 0f };
        _source = _volumeProvider.ToWaveProvider();
        _output = new WaveOutEvent { DesiredLatency = 100 }; // 100 ms ok for ambient engine audio
        _output.Init(_source);
    }

    public void PlayLoopFadeIn(float fadeInSeconds = 0.4f)
    {
        if (!_loop || _output == null) return;
        if (!_playing)
        {
            try { _output.Play(); } catch { }
            _playing = true;
        }
        _envelopeRate = MathF.Max(0.5f, 1f / MathF.Max(0.05f, fadeInSeconds));
        _targetVolume = _baseVolume;
    }

    // One-shot playback. Returns a task that completes when the
    // file finishes (so the caller can chain things if it cares),
    // but firing is also fire-and-forget-safe.
    public Task PlayOneShot(float fadeInSeconds = 0.05f)
    {
        if (_loop || _output == null) return Task.CompletedTask;
        try
        {
            // For one-shots we need to rewind the stream each fire
            // — VorbisWaveReader / AudioFileReader both support
            // CurrentTime = 0. Reusing the same file is much cheaper
            // than re-opening it.
            RewindToStart();
            _output.Stop();
            _output.Play();
            _playing = true;
            _envelopeRate = MathF.Max(0.5f, 1f / MathF.Max(0.05f, fadeInSeconds));
            _targetVolume = _baseVolume;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] MountAudioLayer one-shot failed for '{_path}': {ex.Message}"); } catch { }
        }
        return Task.CompletedTask;
    }

    public void FadeOut(float fadeOutSeconds = 0.4f)
    {
        _envelopeRate = MathF.Max(0.5f, 1f / MathF.Max(0.05f, fadeOutSeconds));
        _targetVolume = 0f;
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { }
        _playing = false;
        _volume = 0f;
        _targetVolume = 0f;
        if (_volumeProvider != null) _volumeProvider.Volume = 0f;
    }

    // True while the underlying NAudio output is actively playing
    // samples — i.e. PlaybackState == Playing. Used by MountAudio to
    // detect the natural end of a one-shot clip so it can advance
    // to the next layer (e.g. mid → top) without a fixed-duration
    // timer.
    public bool IsPlaying => _output != null
        && _output.PlaybackState == NAudio.Wave.PlaybackState.Playing
        && _playing;

    // Seconds remaining until the underlying stream's current
    // playback head reaches its end. For non-loop one-shots this is
    // time until the clip ends. For loops, the LoopStream wraps the
    // underlying _rawStream and resets its Position to 0 at EOF —
    // so this returns time until the CURRENT ITERATION'S boundary.
    // Used by MountAudio's debounce to wait for the tail of the
    // current iteration before crossfading to the next layer.
    // Returns 0 if uninitialized.
    public float RemainingSeconds
    {
        get
        {
            try
            {
                if (_rawStream == null) return 0f;
                var remaining = _rawStream.TotalTime - _rawStream.CurrentTime;
                if (remaining < TimeSpan.Zero) return 0f;
                return (float)remaining.TotalSeconds;
            }
            catch { return 0f; }
        }
    }

    public void SetPitch(float pitch)
    {
        if (_pitchProvider == null) return;
        // Clamp to a sane range — extreme pitch shifts produce
        // robot-voice artifacts.
        pitch = Math.Clamp(pitch, 0.5f, 2.0f);
        _pitchProvider.PitchFactor = pitch;
    }

    // Per-frame envelope update. Drives _volume toward _targetVolume
    // and writes through to NAudio. Auto-stops after a fade-out
    // completes so the WaveOutEvent isn't burning cycles on silence.
    public void Tick(float dt)
    {
        if (_volumeProvider == null) return;

        if (Math.Abs(_targetVolume - _volume) > 0.001f)
        {
            float k = 1f - MathF.Exp(-_envelopeRate * MathF.Max(0.001f, dt));
            _volume += (_targetVolume - _volume) * k;
            if (Math.Abs(_targetVolume - _volume) < 0.005f) _volume = _targetVolume;
            _volumeProvider.Volume = _volume;
        }

        if (_playing && _volume <= 0.001f && _targetVolume <= 0f)
        {
            // Fully faded out — stop the output so we're not feeding
            // silence to the device.
            try { _output?.Pause(); } catch { }
            _playing = false;
        }
    }

    private void RewindToStart()
    {
        // Walk back from the volume provider to the underlying
        // WaveStream and seek to 0. SmbPitchShifting and Volume
        // providers don't have their own position state worth
        // resetting; the loop stream / file reader is the source.
        if (_loopStream != null) { _loopStream.Position = 0; return; }
        // Re-open as a fallback for non-loop paths — VorbisWaveReader
        // doesn't always support CurrentTime = 0 cleanly mid-stream.
        try
        {
            _output?.Stop();
            _volumeProvider = null;
            _pitchProvider = null;
            _source = null;
            _output?.Dispose();
            _output = null;
            OpenStream();
        }
        catch { }
    }

    public void Dispose()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _loopStream?.Dispose(); } catch { }
        try { _rawStream?.Dispose(); } catch { }
        _output = null;
        _loopStream = null;
        _rawStream = null;
        _volumeProvider = null;
        _pitchProvider = null;
        _source = null;
    }
}

// Minimal seamless-loop wrapper for any WaveStream. NAudio doesn't
// ship one in the core package; this version forwards Read calls
// and rewinds the underlying stream when it hits EOF, with no
// click between iterations as long as the source is a clean loop.
public sealed class LoopStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopStream(WaveStream source) { _source = source; }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => long.MaxValue;
    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int got = _source.Read(buffer, offset + total, count - total);
            if (got == 0)
            {
                if (_source.Position == 0) break; // empty source — bail
                _source.Position = 0;
                continue;
            }
            total += got;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source?.Dispose();
        base.Dispose(disposing);
    }
}
