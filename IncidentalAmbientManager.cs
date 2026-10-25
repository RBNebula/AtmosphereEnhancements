namespace AtmosphereEnhancements;

internal sealed class IncidentalAmbientManager
{
    private readonly AudioSource _prototypeSource;
    private readonly ManualLogSource _logger;
    private readonly ConfigEntry<bool> _enableOneShots;
    private readonly ConfigEntry<float> _oneShotMinDelaySeconds;
    private readonly ConfigEntry<float> _oneShotMaxDelaySeconds;
    private readonly ConfigEntry<float> _oneShotBaseVolume;
    private readonly ConfigEntry<float> _oneShotPitchVariance;
    private readonly ConfigEntry<float> _oneShotVolumeVariance;
    private readonly ConfigEntry<float> _oneShotNoiseThresholdOverride;
    private readonly List<OneShotCategorySetting> _categorySettings;

    private IReadOnlyDictionary<string, List<AmbientTrack>> _tracksByCategory = new Dictionary<string, List<AmbientTrack>>(StringComparer.OrdinalIgnoreCase);
    private readonly List<AmbientTrack> _playbackSequence = new List<AmbientTrack>();
    private bool _tracksLoaded;
    private float _nextAttemptAt;
    private int _nextSequenceIndex;

    public IncidentalAmbientManager(
        AudioSource prototypeSource,
        ManualLogSource logger,
        ConfigEntry<bool> enableOneShots,
        ConfigEntry<float> oneShotMinDelaySeconds,
        ConfigEntry<float> oneShotMaxDelaySeconds,
        ConfigEntry<float> oneShotBaseVolume,
        ConfigEntry<float> oneShotPitchVariance,
        ConfigEntry<float> oneShotVolumeVariance,
        ConfigEntry<float> oneShotNoiseThresholdOverride,
        ConfigEntry<bool> enableDrips,
        ConfigEntry<bool> enableCreaks,
        ConfigEntry<bool> enableRockTicks,
        ConfigEntry<bool> enableMetalClinks,
        ConfigEntry<bool> enableWind,
        ConfigEntry<float> dripsWeight,
        ConfigEntry<float> creaksWeight,
        ConfigEntry<float> rockTicksWeight,
        ConfigEntry<float> metalClinksWeight,
        ConfigEntry<float> windWeight)
    {
        _prototypeSource = prototypeSource;
        _logger = logger;
        _enableOneShots = enableOneShots;
        _oneShotMinDelaySeconds = oneShotMinDelaySeconds;
        _oneShotMaxDelaySeconds = oneShotMaxDelaySeconds;
        _oneShotBaseVolume = oneShotBaseVolume;
        _oneShotPitchVariance = oneShotPitchVariance;
        _oneShotVolumeVariance = oneShotVolumeVariance;
        _oneShotNoiseThresholdOverride = oneShotNoiseThresholdOverride;

        _categorySettings = new List<OneShotCategorySetting>
        {
            new OneShotCategorySetting(OneShotCategoryNames.Drips, enableDrips, dripsWeight, minQuietSeconds: 0f, categoryCooldownSeconds: 12f),
            new OneShotCategorySetting(OneShotCategoryNames.Creaks, enableCreaks, creaksWeight, minQuietSeconds: 75f, categoryCooldownSeconds: 35f),
            new OneShotCategorySetting(OneShotCategoryNames.RockTicks, enableRockTicks, rockTicksWeight, minQuietSeconds: 20f, categoryCooldownSeconds: 18f),
            new OneShotCategorySetting(OneShotCategoryNames.MetalClinks, enableMetalClinks, metalClinksWeight, minQuietSeconds: 120f, categoryCooldownSeconds: 45f),
            new OneShotCategorySetting(OneShotCategoryNames.Wind, enableWind, windWeight, minQuietSeconds: 45f, categoryCooldownSeconds: 25f)
        };

        ScheduleNextAttempt();
    }

    public void SetTracks(IReadOnlyDictionary<string, List<AmbientTrack>> tracksByCategory)
    {
        _tracksByCategory = tracksByCategory;
        RebuildPlaybackSequence();
        _tracksLoaded = true;
    }

    public void Update(AmbientNoiseMonitor noiseMonitor)
    {
        if (!_enableOneShots.Value)
        {
            HandleDisabled();
            return;
        }

        if (!_tracksLoaded || _prototypeSource.isPlaying || Time.realtimeSinceStartup < _nextAttemptAt)
        {
            return;
        }

        TryPlayOneShot(noiseMonitor);
    }

    public void HandleDisabled()
    {
        if (_prototypeSource.isPlaying)
        {
            _prototypeSource.Stop();
        }

        _prototypeSource.clip = null;
        _prototypeSource.volume = 0f;
        _prototypeSource.pitch = 1f;

        ScheduleNextAttempt();
    }

    private void TryPlayOneShot(AmbientNoiseMonitor noiseMonitor)
    {
        float? thresholdOverride = _oneShotNoiseThresholdOverride.Value > 0f
            ? _oneShotNoiseThresholdOverride.Value
            : (float?)null;

        if (!noiseMonitor.CanStartPlayback(thresholdOverride, "Ambient one-shot"))
        {
            _nextAttemptAt = Time.realtimeSinceStartup + 1f;
            return;
        }

        AmbientTrack? selectedTrack = GetNextTrackInSequence();
        if (selectedTrack == null)
        {
            _nextAttemptAt = Time.realtimeSinceStartup + 1f;
            return;
        }

        PlayTrack(selectedTrack);
        ScheduleNextAttempt();
    }

    private void RebuildPlaybackSequence()
    {
        _playbackSequence.Clear();

        for (int i = 0; i < _categorySettings.Count; i++)
        {
            OneShotCategorySetting setting = _categorySettings[i];
            if (!setting.EnabledEntry.Value)
            {
                continue;
            }

            if (!_tracksByCategory.TryGetValue(setting.CategoryName, out List<AmbientTrack>? tracks) || tracks.Count == 0)
            {
                continue;
            }

            for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
            {
                _playbackSequence.Add(tracks[trackIndex]);
            }
        }

        _nextSequenceIndex = 0;
    }

    private AmbientTrack? GetNextTrackInSequence()
    {
        if (_playbackSequence.Count == 0)
        {
            return null;
        }

        if (_nextSequenceIndex >= _playbackSequence.Count)
        {
            _nextSequenceIndex = 0;
        }

        AmbientTrack track = _playbackSequence[_nextSequenceIndex];
        _nextSequenceIndex++;
        return track;
    }

    private void PlayTrack(AmbientTrack track)
    {
        _prototypeSource.clip = track.Clip;
        _prototypeSource.pitch = GetRandomPitch();
        _prototypeSource.volume = GetRandomVolume();
        _prototypeSource.Play();

        _logger.LogInfo($"Playing incidental one-shot [{track.CategoryName}]: {Path.GetFileName(track.FilePath)}");
    }

    private void ScheduleNextAttempt()
    {
        _nextAttemptAt = Time.realtimeSinceStartup;
    }

    private float GetRandomPitch()
    {
        float variance = Mathf.Clamp(_oneShotPitchVariance.Value, 0f, 0.25f);
        return 1f + UnityEngine.Random.Range(-variance, variance);
    }

    private float GetRandomVolume()
    {
        float variance = Mathf.Clamp(_oneShotVolumeVariance.Value, 0f, 0.5f);
        float baseVolume = Mathf.Clamp01(_oneShotBaseVolume.Value);
        return Mathf.Clamp01(baseVolume + UnityEngine.Random.Range(-variance, variance));
    }

    private sealed class OneShotCategorySetting
    {
        public OneShotCategorySetting(
            string categoryName,
            ConfigEntry<bool> enabledEntry,
            ConfigEntry<float> weightEntry,
            float minQuietSeconds,
            float categoryCooldownSeconds)
        {
            CategoryName = categoryName;
            EnabledEntry = enabledEntry;
            WeightEntry = weightEntry;
            MinQuietSeconds = minQuietSeconds;
            CategoryCooldownSeconds = categoryCooldownSeconds;
        }

        public string CategoryName { get; }

        public ConfigEntry<bool> EnabledEntry { get; }

        public ConfigEntry<float> WeightEntry { get; }

        public float MinQuietSeconds { get; }

        public float CategoryCooldownSeconds { get; }
    }
}
