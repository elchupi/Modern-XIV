using System;

namespace noWickyXIV;

// Common interface for layers that act as a loop. Lets MountAudio
// hold a field that's EITHER a regular MountAudioLayer (configured
// with loop:true) OR a CrossfadeLoopLayer (two-instance crossfade
// loop), based on per-slot config.
public interface ILoopLayer : IDisposable
{
    bool  IsPlaying { get; }
    float RemainingSeconds { get; }
    void  PlayLoopFadeIn(float fadeInSeconds = 0.4f);
    void  FadeOut(float fadeOutSeconds = 0.4f);
    void  Stop();
    void  SetPitch(float pitch);
    void  Tick(float dt);
}

// Two-instance crossfade looping for sounds whose start and end
// samples don't perfectly match (typical for hand-recorded engine
// idle / cruise loops). A single LoopStream that just rewinds the
// reader to position 0 produces an audible click/blip at the seam
// when the waveform jumps. This layer instead keeps TWO underlying
// MountAudioLayer instances of the same .wav and triggers the
// inactive one to restart from the beginning while the active one
// is in its tail. Both fade across the configured CrossfadeMs
// window — by the time the active one ends, the inactive one is
// at full volume, and they swap roles. Repeat indefinitely.
//
// Same public surface as MountAudioLayer so MountAudio.cs can use
// either type interchangeably for loop slots.
public sealed class CrossfadeLoopLayer : ILoopLayer
{
    private readonly MountAudioLayer _a;
    private readonly MountAudioLayer _b;
    private int _activeIdx;          // 0 = A is active, 1 = B is active
    private float _crossfadeSec;
    private bool _playing;
    private float _baseVol;

    private CrossfadeLoopLayer(MountAudioLayer a, MountAudioLayer b, float crossfadeSec, float baseVol)
    {
        _a = a;
        _b = b;
        _crossfadeSec = MathF.Max(0.05f, crossfadeSec);
        _baseVol = baseVol;
    }

    // crossfadeMs = how many ms before the end of each loop iteration
    // should the next instance start from the beginning. Both layers
    // are loaded as one-shots (loop:false) — the crossfade-loop
    // wrapper handles the perceived looping by alternating them.
    public static CrossfadeLoopLayer TryCreate(string path, float baseVol, int crossfadeMs)
    {
        var a = MountAudioLayer.TryCreate(path, loop: false, baseVol: baseVol);
        var b = MountAudioLayer.TryCreate(path, loop: false, baseVol: baseVol);
        if (a == null || b == null)
        {
            a?.Dispose();
            b?.Dispose();
            return null;
        }
        return new CrossfadeLoopLayer(a, b, crossfadeMs / 1000f, baseVol);
    }

    public bool IsPlaying => _playing && (_a.IsPlaying || _b.IsPlaying);

    // Loop is conceptually endless — return MaxValue so callers that
    // use this for "is this layer about to end" logic naturally
    // skip it, same way MountAudioLayer does for its own loops.
    public float RemainingSeconds => float.MaxValue;

    public void PlayLoopFadeIn(float fadeInSeconds = 0.4f)
    {
        if (_playing) return;
        // Start with A. PlayOneShot rewinds the underlying reader
        // and starts playback with a brief fade-in.
        _a.PlayOneShot(fadeInSeconds: fadeInSeconds);
        _activeIdx = 0;
        _playing = true;
    }

    public void FadeOut(float fadeOutSeconds = 0.4f)
    {
        _a.FadeOut(fadeOutSeconds);
        _b.FadeOut(fadeOutSeconds);
    }

    public void Stop()
    {
        _a.Stop();
        _b.Stop();
        _playing = false;
    }

    public void SetPitch(float pitch)
    {
        _a.SetPitch(pitch);
        _b.SetPitch(pitch);
    }

    // Per-frame tick. Drives both inner layers' envelopes AND watches
    // the active one for "almost done" — when the active layer's
    // remaining time drops below the crossfade window, the inactive
    // one starts from the top with a fade-in matching that window.
    // The active layer naturally ends as the inactive ramps up.
    public void Tick(float dt)
    {
        _a.Tick(dt);
        _b.Tick(dt);
        if (!_playing) return;

        var active   = _activeIdx == 0 ? _a : _b;
        var inactive = _activeIdx == 0 ? _b : _a;

        // If the active instance has dropped into its tail and the
        // inactive one isn't already going, kick it off. The active
        // one's natural EOF will silence it; we don't need to FadeOut
        // explicitly because the file ends at its own envelope.
        if (active.IsPlaying
            && active.RemainingSeconds < _crossfadeSec
            && !inactive.IsPlaying)
        {
            inactive.PlayOneShot(fadeInSeconds: _crossfadeSec);
        }

        // When the previously-active instance has fully stopped, the
        // inactive one is now carrying the loop — swap roles.
        if (!active.IsPlaying && inactive.IsPlaying)
        {
            _activeIdx = 1 - _activeIdx;
        }

        // Edge case: both stopped (e.g. a glitch). Restart A from the
        // top so the loop doesn't go silent forever.
        if (_playing && !_a.IsPlaying && !_b.IsPlaying)
        {
            _a.PlayOneShot(fadeInSeconds: _crossfadeSec);
            _activeIdx = 0;
        }
    }

    public void Dispose()
    {
        try { _a?.Dispose(); } catch { }
        try { _b?.Dispose(); } catch { }
        _playing = false;
    }
}
