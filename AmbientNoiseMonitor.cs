namespace AtmosphereEnhancements;

internal sealed class AmbientNoiseMonitor
{
    private readonly ManualLogSource _logger;
    private readonly ConfigEntry<bool> _onlyStartWhenQuiet;
    private readonly ConfigEntry<float> _quietThresholdToStart;
    private readonly ConfigEntry<float> _quietThresholdToKeepPlaying;
    private readonly ConfigEntry<float> _quietCheckIntervalSeconds;
    private readonly ConfigEntry<bool> _stopIfNoLongerQuiet;
    private readonly ConfigEntry<float> _requiredLoudDurationToStopSeconds;
    private readonly ConfigEntry<bool> _verboseLogging;
    private readonly HashSet<AudioSource> _ignoredSources = new HashSet<AudioSource>();
    private readonly HashSet<AudioSource> _footstepSources = new HashSet<AudioSource>();

    private float _nextQuietSampleAt;
    private float _cachedNonFootstepLoudness;
    private float _loudDurationSeconds;
    private float _nextFootstepCacheRefreshAt;
    private float _quietSinceTime = -1f;
    private bool _isQuietForAmbience;

    public AmbientNoiseMonitor(
        ManualLogSource logger,
        ConfigEntry<bool> onlyStartWhenQuiet,
        ConfigEntry<float> quietThresholdToStart,
        ConfigEntry<float> quietThresholdToKeepPlaying,
        ConfigEntry<float> quietCheckIntervalSeconds,
        ConfigEntry<bool> stopIfNoLongerQuiet,
        ConfigEntry<float> requiredLoudDurationToStopSeconds,
        ConfigEntry<bool> verboseLogging)
    {
        _logger = logger;
        _onlyStartWhenQuiet = onlyStartWhenQuiet;
        _quietThresholdToStart = quietThresholdToStart;
        _quietThresholdToKeepPlaying = quietThresholdToKeepPlaying;
        _quietCheckIntervalSeconds = quietCheckIntervalSeconds;
        _stopIfNoLongerQuiet = stopIfNoLongerQuiet;
        _requiredLoudDurationToStopSeconds = requiredLoudDurationToStopSeconds;
        _verboseLogging = verboseLogging;
    }

    public float QuietDurationSeconds
    {
        get
        {
            if (!_isQuietForAmbience || _quietSinceTime < 0f)
            {
                return 0f;
            }

            return Mathf.Max(0f, Time.realtimeSinceStartup - _quietSinceTime);
        }
    }

    public void RegisterIgnoredSource(AudioSource source)
    {
        if (source != null)
        {
            _ignoredSources.Add(source);
        }
    }

    public void Update()
    {
        RefreshCachedLoudnessIfNeeded();
        UpdateQuietDurationTracking();
    }

    public bool CanStartPlayback(float? thresholdOverride, string contextLabel)
    {
        if (!_onlyStartWhenQuiet.Value)
        {
            return true;
        }

        RefreshCachedLoudnessIfNeeded();

        float threshold = thresholdOverride.HasValue && thresholdOverride.Value > 0f
            ? thresholdOverride.Value
            : Mathf.Max(0f, _quietThresholdToStart.Value);

        bool quietEnough = _cachedNonFootstepLoudness <= threshold;
        if (!quietEnough && _verboseLogging.Value)
        {
            _logger.LogInfo($"{contextLabel} blocked by nearby noise. Loudness={_cachedNonFootstepLoudness:F3}, threshold={threshold:F3}");
        }

        return quietEnough;
    }

    public bool ShouldStopActiveBed()
    {
        if (!_stopIfNoLongerQuiet.Value)
        {
            _loudDurationSeconds = 0f;
            return false;
        }

        RefreshCachedLoudnessIfNeeded();

        float keepPlayingThreshold = Mathf.Max(0f, _quietThresholdToKeepPlaying.Value);
        if (_cachedNonFootstepLoudness <= keepPlayingThreshold)
        {
            _loudDurationSeconds = 0f;
            return false;
        }

        _loudDurationSeconds += Time.unscaledDeltaTime;
        float requiredDuration = Mathf.Max(0.5f, _requiredLoudDurationToStopSeconds.Value);
        if (_loudDurationSeconds < requiredDuration)
        {
            return false;
        }

        if (_verboseLogging.Value)
        {
            _logger.LogInfo($"Stopping ambient bed due to sustained nearby noise. Loudness={_cachedNonFootstepLoudness:F3}, threshold={keepPlayingThreshold:F3}");
        }

        _loudDurationSeconds = 0f;
        return true;
    }

    public void Reset()
    {
        _loudDurationSeconds = 0f;
        _isQuietForAmbience = false;
        _quietSinceTime = -1f;
    }

    private void RefreshCachedLoudnessIfNeeded()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextQuietSampleAt)
        {
            return;
        }

        _cachedNonFootstepLoudness = SampleNearbyNonFootstepLoudness();
        _nextQuietSampleAt = now + Mathf.Max(0.2f, _quietCheckIntervalSeconds.Value);
    }

    private void UpdateQuietDurationTracking()
    {
        bool isQuietNow = _cachedNonFootstepLoudness <= Mathf.Max(0f, _quietThresholdToStart.Value);
        if (!isQuietNow)
        {
            _isQuietForAmbience = false;
            _quietSinceTime = -1f;
            return;
        }

        if (_isQuietForAmbience)
        {
            return;
        }

        _isQuietForAmbience = true;
        _quietSinceTime = Time.realtimeSinceStartup;
    }

    private float SampleNearbyNonFootstepLoudness()
    {
        if (!TryGetListenerPosition(out Vector3 listenerPosition))
        {
            return 0f;
        }

        RefreshFootstepSourceCacheIfNeeded();

        AudioSource[] allSources = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        float loudness = 0f;

        for (int i = 0; i < allSources.Length; i++)
        {
            AudioSource? source = allSources[i];
            if (source == null || _ignoredSources.Contains(source))
            {
                continue;
            }

            if (!source.isActiveAndEnabled || !source.isPlaying || source.mute)
            {
                continue;
            }

            if (IsLikelyFootstepSource(source))
            {
                continue;
            }

            loudness += EstimateAudibilityAtListener(source, listenerPosition);
        }

        return loudness;
    }

    private static bool TryGetListenerPosition(out Vector3 listenerPosition)
    {
        listenerPosition = Vector3.zero;
        AudioListener? listener = UnityEngine.Object.FindAnyObjectByType<AudioListener>(FindObjectsInactive.Exclude);
        if (listener == null)
        {
            return false;
        }

        listenerPosition = listener.transform.position;
        return true;
    }

    private void RefreshFootstepSourceCacheIfNeeded()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextFootstepCacheRefreshAt)
        {
            return;
        }

        _nextFootstepCacheRefreshAt = now + 5f;
        _footstepSources.Clear();

        PlayerFootsteps[] footstepComponents = UnityEngine.Object.FindObjectsByType<PlayerFootsteps>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < footstepComponents.Length; i++)
        {
            PlayerFootsteps footstep = footstepComponents[i];
            if (footstep == null)
            {
                continue;
            }

            TryCacheSourceFromSoundPlayer(footstep.SoundPlayerLeft);
            TryCacheSourceFromSoundPlayer(footstep.SoundPlayerRight);
        }
    }

    private void TryCacheSourceFromSoundPlayer(SoundPlayer? soundPlayer)
    {
        if (soundPlayer == null)
        {
            return;
        }

        AudioSource? source = soundPlayer.GetComponent<AudioSource>();
        if (source != null)
        {
            _footstepSources.Add(source);
        }
    }

    private bool IsLikelyFootstepSource(AudioSource source)
    {
        if (_footstepSources.Contains(source))
        {
            return true;
        }

        if (source.GetComponentInParent<PlayerFootsteps>() != null)
        {
            return true;
        }

        string gameObjectName = source.gameObject.name ?? string.Empty;
        string clipName = source.clip != null ? source.clip.name ?? string.Empty : string.Empty;

        return gameObjectName.IndexOf("footstep", StringComparison.OrdinalIgnoreCase) >= 0
               || gameObjectName.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0
               || clipName.IndexOf("footstep", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float EstimateAudibilityAtListener(AudioSource source, Vector3 listenerPosition)
    {
        if (source.volume <= 0.0001f)
        {
            return 0f;
        }

        if (source.spatialBlend <= 0.01f)
        {
            return 0f;
        }

        float minDistance = Mathf.Max(0.001f, source.minDistance);
        float maxDistance = Mathf.Max(minDistance, source.maxDistance);
        float distance = Vector3.Distance(source.transform.position, listenerPosition);

        if (distance >= maxDistance)
        {
            return 0f;
        }

        float attenuation;
        if (distance <= minDistance)
        {
            attenuation = 1f;
        }
        else if (source.rolloffMode == AudioRolloffMode.Logarithmic)
        {
            attenuation = minDistance / distance;
        }
        else
        {
            attenuation = 1f - ((distance - minDistance) / Mathf.Max(0.001f, maxDistance - minDistance));
        }

        attenuation = Mathf.Clamp01(attenuation);
        return source.volume * attenuation * Mathf.Clamp01(source.spatialBlend);
    }
}
