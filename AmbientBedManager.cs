namespace AtmosphereEnhancements;

internal sealed class AmbientBedManager
{
    private readonly AudioSource _audioSource;
    private readonly ManualLogSource _logger;
    private readonly ConfigEntry<float> _ambientVolume;
    private readonly ConfigEntry<float> _fadeSeconds;
    private readonly ConfigEntry<float> _initialDelayMinSeconds;
    private readonly ConfigEntry<float> _initialDelayMaxSeconds;
    private readonly ConfigEntry<float> _silenceGapMinSeconds;
    private readonly ConfigEntry<float> _silenceGapMaxSeconds;
    private readonly ConfigEntry<float> _quietRetrySeconds;

    private IReadOnlyList<AmbientTrack> _tracks = Array.Empty<AmbientTrack>();
    private bool _tracksLoaded;
    private bool _trackIsActive;
    private bool _stopTrackAfterFadeOut;
    private float _currentVolume;
    private float _targetVolume;
    private float _nextTrackAttemptAt;
    private int _lastTrackIndex = -1;

    public AmbientBedManager(
        AudioSource audioSource,
        ManualLogSource logger,
        ConfigEntry<float> ambientVolume,
        ConfigEntry<float> fadeSeconds,
        ConfigEntry<float> initialDelayMinSeconds,
        ConfigEntry<float> initialDelayMaxSeconds,
        ConfigEntry<float> silenceGapMinSeconds,
        ConfigEntry<float> silenceGapMaxSeconds,
        ConfigEntry<float> quietRetrySeconds)
    {
        _audioSource = audioSource;
        _logger = logger;
        _ambientVolume = ambientVolume;
        _fadeSeconds = fadeSeconds;
        _initialDelayMinSeconds = initialDelayMinSeconds;
        _initialDelayMaxSeconds = initialDelayMaxSeconds;
        _silenceGapMinSeconds = silenceGapMinSeconds;
        _silenceGapMaxSeconds = silenceGapMaxSeconds;
        _quietRetrySeconds = quietRetrySeconds;

        ScheduleInitialTrackAttempt();
    }

    public void SetTracks(IReadOnlyList<AmbientTrack> tracks)
    {
        _tracks = tracks ?? Array.Empty<AmbientTrack>();
        _tracksLoaded = true;
    }

    public void Update(AmbientNoiseMonitor noiseMonitor)
    {
        UpdateVolumeFade();

        if (!_tracksLoaded)
        {
            return;
        }

        if (_trackIsActive)
        {
            if (_stopTrackAfterFadeOut)
            {
                return;
            }

            if (noiseMonitor.ShouldStopActiveBed())
            {
                _targetVolume = 0f;
                _stopTrackAfterFadeOut = true;
                return;
            }

            if (!_audioSource.isPlaying)
            {
                FinishTrack(interruptedByNoise: false);
            }

            return;
        }

        if (Time.realtimeSinceStartup >= _nextTrackAttemptAt)
        {
            TryStartTrack(noiseMonitor);
        }
    }

    public void HandleDisabled()
    {
        if (_audioSource.isPlaying)
        {
            _audioSource.Stop();
        }

        _trackIsActive = false;
        _stopTrackAfterFadeOut = false;
        _targetVolume = 0f;
        _currentVolume = 0f;
        _audioSource.volume = 0f;
        _audioSource.clip = null;
    }

    private void ScheduleInitialTrackAttempt()
    {
        float min = Mathf.Max(0f, _initialDelayMinSeconds.Value);
        float max = Mathf.Max(min, _initialDelayMaxSeconds.Value);
        _nextTrackAttemptAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(min, max);
    }

    private void ScheduleNextTrackAttempt(float delaySeconds)
    {
        _nextTrackAttemptAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySeconds);
    }

    private void TryStartTrack(AmbientNoiseMonitor noiseMonitor)
    {
        if (_tracks.Count == 0)
        {
            ScheduleNextTrackAttempt(_quietRetrySeconds.Value);
            return;
        }

        if (!noiseMonitor.CanStartPlayback(null, "Ambient bed"))
        {
            ScheduleNextTrackAttempt(Mathf.Max(1f, _quietRetrySeconds.Value));
            return;
        }

        int trackIndex = UnityEngine.Random.Range(0, _tracks.Count);
        if (_tracks.Count > 1 && trackIndex == _lastTrackIndex)
        {
            trackIndex = (trackIndex + 1 + UnityEngine.Random.Range(0, _tracks.Count - 1)) % _tracks.Count;
        }

        _lastTrackIndex = trackIndex;

        AmbientTrack track = _tracks[trackIndex];
        _audioSource.pitch = 1f;
        _audioSource.clip = track.Clip;
        _audioSource.volume = 0f;
        _audioSource.Play();

        _currentVolume = 0f;
        _targetVolume = Mathf.Clamp01(_ambientVolume.Value);
        _trackIsActive = true;
        _stopTrackAfterFadeOut = false;

        _logger.LogInfo($"Playing ambient bed: {Path.GetFileName(track.FilePath)}");
    }

    private void UpdateVolumeFade()
    {
        float fadeDuration = Mathf.Max(0.01f, _fadeSeconds.Value);
        _currentVolume = Mathf.MoveTowards(_currentVolume, _targetVolume, Time.unscaledDeltaTime / fadeDuration);
        _audioSource.volume = _currentVolume;

        if (_stopTrackAfterFadeOut && _currentVolume <= 0.001f)
        {
            _audioSource.Stop();
            FinishTrack(interruptedByNoise: true);
        }
    }

    private void FinishTrack(bool interruptedByNoise)
    {
        _trackIsActive = false;
        _stopTrackAfterFadeOut = false;
        _targetVolume = 0f;
        _currentVolume = 0f;
        _audioSource.volume = 0f;
        _audioSource.clip = null;

        if (interruptedByNoise)
        {
            ScheduleNextTrackAttempt(Mathf.Max(1f, _quietRetrySeconds.Value));
            return;
        }

        float minGap = Mathf.Max(0f, _silenceGapMinSeconds.Value);
        float maxGap = Mathf.Max(minGap, _silenceGapMaxSeconds.Value);
        ScheduleNextTrackAttempt(UnityEngine.Random.Range(minGap, maxGap));
    }
}
