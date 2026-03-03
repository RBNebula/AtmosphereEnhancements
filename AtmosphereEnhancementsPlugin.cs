namespace AtmosphereEnhancements;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class AtmosphereEnhancementsPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.main.atmosphereenhancements";
    public const string PluginName = "Atmosphere Enhancements";
    public const string PluginVersion = "0.1.0";

    private const string RuntimeAudioFolderName = "AmbientAudio";
    private const string EmbeddedAudioPrefix = "AtmosphereEnhancements.Assets.DefaultAmbient.";

    private static readonly string[] SupportedAudioExtensions =
    {
        ".ogg",
        ".wav",
        ".mp3",
        ".aif",
        ".aiff"
    };

    private readonly List<AmbientTrack> _tracks = new List<AmbientTrack>();
    private readonly HashSet<AudioSource> _footstepSources = new HashSet<AudioSource>();

    private ConfigEntry<bool>? _enabled;
    private ConfigEntry<float>? _ambientVolume;
    private ConfigEntry<float>? _fadeSeconds;
    private ConfigEntry<float>? _initialDelayMinSeconds;
    private ConfigEntry<float>? _initialDelayMaxSeconds;
    private ConfigEntry<float>? _silenceGapMinSeconds;
    private ConfigEntry<float>? _silenceGapMaxSeconds;
    private ConfigEntry<bool>? _onlyStartWhenQuiet;
    private ConfigEntry<float>? _quietThresholdToStart;
    private ConfigEntry<float>? _quietThresholdToKeepPlaying;
    private ConfigEntry<float>? _quietCheckIntervalSeconds;
    private ConfigEntry<float>? _quietRetrySeconds;
    private ConfigEntry<bool>? _stopIfNoLongerQuiet;
    private ConfigEntry<float>? _requiredLoudDurationToStopSeconds;
    private ConfigEntry<bool>? _streamLoadedAudio;
    private ConfigEntry<bool>? _verboseLogging;

    private AudioSource? _ambientSource;
    private string _runtimeAudioFolderPath = string.Empty;

    private bool _tracksLoaded;
    private bool _trackIsActive;
    private bool _stopTrackAfterFadeOut;

    private float _currentVolume;
    private float _targetVolume;
    private float _nextTrackAttemptAt;
    private float _nextQuietSampleAt;
    private float _cachedNonFootstepLoudness;
    private float _loudDurationSeconds;
    private float _nextFootstepCacheRefreshAt;

    private int _lastTrackIndex = -1;

    private void Awake()
    {
        BindConfig();
        SetupAudioSource();
        PrepareRuntimeAudioFolder();
        ExportPackagedAudio();
        StartCoroutine(LoadTracksFromRuntimeFolder());
        ScheduleInitialTrackAttempt();
    }

    private void Update()
    {
        if (_ambientSource == null || _enabled == null)
        {
            return;
        }

        if (!_enabled.Value)
        {
            if (_ambientSource.isPlaying)
            {
                _ambientSource.Stop();
            }

            _trackIsActive = false;
            _stopTrackAfterFadeOut = false;
            _targetVolume = 0f;
            _currentVolume = 0f;
            _ambientSource.volume = 0f;
            _ambientSource.clip = null;
            return;
        }

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

            if (_stopIfNoLongerQuiet != null && _stopIfNoLongerQuiet.Value)
            {
                TickNoiseMonitorWhilePlaying();
            }

            if (!_ambientSource.isPlaying)
            {
                FinishTrack(interruptedByNoise: false);
            }

            return;
        }

        if (Time.realtimeSinceStartup >= _nextTrackAttemptAt)
        {
            TryStartTrack();
        }
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
        DestroyLoadedTracks();
    }

    private void BindConfig()
    {
        _enabled = Config.Bind("General", "Enabled", true, "Master toggle for ambient playback.");
        _ambientVolume = Config.Bind("General", "AmbientVolume", 0.35f, "Base ambient volume (0-1).");
        _fadeSeconds = Config.Bind("General", "FadeSeconds", 2.5f, "Fade duration in seconds when starting/stopping ambient tracks.");
        _initialDelayMinSeconds = Config.Bind("Timing", "InitialDelayMinSeconds", 25f, "Minimum delay before the first track attempt.");
        _initialDelayMaxSeconds = Config.Bind("Timing", "InitialDelayMaxSeconds", 90f, "Maximum delay before the first track attempt.");
        _silenceGapMinSeconds = Config.Bind("Timing", "SilenceGapMinSeconds", 70f, "Minimum silence gap between tracks.");
        _silenceGapMaxSeconds = Config.Bind("Timing", "SilenceGapMaxSeconds", 220f, "Maximum silence gap between tracks.");
        _onlyStartWhenQuiet = Config.Bind("QuietMode", "OnlyStartWhenQuiet", true, "Only start a track when nearby non-footstep audio is below the threshold.");
        _quietThresholdToStart = Config.Bind("QuietMode", "QuietThresholdToStart", 0.35f, "Estimated non-footstep loudness threshold required to start a track.");
        _quietThresholdToKeepPlaying = Config.Bind("QuietMode", "QuietThresholdToKeepPlaying", 0.65f, "If stop-when-loud is enabled, loudness above this value can stop a track.");
        _quietCheckIntervalSeconds = Config.Bind("QuietMode", "QuietCheckIntervalSeconds", 1.5f, "Seconds between nearby loudness checks.");
        _quietRetrySeconds = Config.Bind("QuietMode", "QuietRetrySeconds", 12f, "Retry delay when a track could not start because the world was too loud.");
        _stopIfNoLongerQuiet = Config.Bind("QuietMode", "StopIfNoLongerQuiet", true, "Fade out and stop a track when machines/noise become too loud nearby.");
        _requiredLoudDurationToStopSeconds = Config.Bind("QuietMode", "RequiredLoudDurationToStopSeconds", 4f, "How long it must stay loud before stopping a currently playing track.");
        _streamLoadedAudio = Config.Bind("Loading", "StreamLoadedAudio", true, "Use streaming mode when loading clips from files (better for long tracks).");
        _verboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Enable extra logging for quiet checks and playback decisions.");
    }

    private void SetupAudioSource()
    {
        _ambientSource = gameObject.AddComponent<AudioSource>();
        _ambientSource.playOnAwake = false;
        _ambientSource.loop = false;
        _ambientSource.spatialBlend = 0f;
        _ambientSource.priority = 64;
        _ambientSource.ignoreListenerPause = true;
        _ambientSource.volume = 0f;
        _ambientSource.dopplerLevel = 0f;
    }

    private void PrepareRuntimeAudioFolder()
    {
        string pluginDirectory = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
        _runtimeAudioFolderPath = Path.Combine(pluginDirectory, RuntimeAudioFolderName);
        Directory.CreateDirectory(_runtimeAudioFolderPath);

        string guidanceFilePath = Path.Combine(_runtimeAudioFolderPath, "_ADD_AMBIENT_AUDIO_HERE.txt");
        if (!File.Exists(guidanceFilePath))
        {
            File.WriteAllLines(guidanceFilePath, new[]
            {
                "Atmosphere Enhancements - Runtime audio folder",
                "Drop extra ambient tracks here and restart the game.",
                "Supported formats: .ogg .wav .mp3 .aif .aiff",
                "This folder is read by the mod at startup.",
                "Packaged tracks from the DLL are exported here automatically."
            });
        }
    }

    private void ExportPackagedAudio()
    {
        if (string.IsNullOrWhiteSpace(_runtimeAudioFolderPath))
        {
            return;
        }

        Assembly assembly = Assembly.GetExecutingAssembly();
        string[] resources = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(EmbeddedAudioPrefix, StringComparison.Ordinal))
            .ToArray();

        int exportedCount = 0;
        for (int i = 0; i < resources.Length; i++)
        {
            string resourceName = resources[i];
            string fileName = resourceName.Substring(EmbeddedAudioPrefix.Length);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            string outputPath = Path.Combine(_runtimeAudioFolderPath, fileName);
            if (File.Exists(outputPath))
            {
                continue;
            }

            using Stream? resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                continue;
            }

            using FileStream outputStream = File.Create(outputPath);
            resourceStream.CopyTo(outputStream);
            exportedCount++;
        }

        if (exportedCount > 0)
        {
            Logger.LogInfo($"Exported {exportedCount} packaged ambient file(s) to: {_runtimeAudioFolderPath}");
        }
    }

    private IEnumerator LoadTracksFromRuntimeFolder()
    {
        _tracksLoaded = false;
        DestroyLoadedTracks();

        if (string.IsNullOrWhiteSpace(_runtimeAudioFolderPath) || !Directory.Exists(_runtimeAudioFolderPath))
        {
            Logger.LogWarning("Runtime ambient folder does not exist. Ambient playback is disabled.");
            _tracksLoaded = true;
            yield break;
        }

        string[] files = Directory
            .EnumerateFiles(_runtimeAudioFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            Logger.LogWarning($"No ambient audio files found in '{_runtimeAudioFolderPath}'.");
            _tracksLoaded = true;
            yield break;
        }

        Logger.LogInfo($"Loading {files.Length} ambient audio file(s) from: {_runtimeAudioFolderPath}");

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            AudioClip? loadedClip = null;
            string? error = null;

            yield return StartCoroutine(LoadClipFromFile(
                path,
                clip => loadedClip = clip,
                message => error = message));

            if (loadedClip != null)
            {
                _tracks.Add(new AmbientTrack(path, loadedClip));
            }
            else
            {
                Logger.LogWarning($"Failed to load ambient clip '{path}': {error ?? "Unknown error"}");
            }
        }

        _tracksLoaded = true;
        Logger.LogInfo($"Ambient loading complete. Loaded {_tracks.Count} track(s).");
    }

    private IEnumerator LoadClipFromFile(string filePath, Action<AudioClip> onLoaded, Action<string> onFailed)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        AudioType audioType = GetAudioType(extension);
        if (audioType == AudioType.UNKNOWN)
        {
            onFailed("Unsupported extension: " + extension);
            yield break;
        }

        Uri fileUri = new Uri(filePath);
        using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileUri.AbsoluteUri, audioType);
        if (request.downloadHandler is DownloadHandlerAudioClip handler && _streamLoadedAudio != null)
        {
            handler.streamAudio = _streamLoadedAudio.Value;
        }

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
        {
            onFailed(request.error ?? "Unknown UnityWebRequest error");
            yield break;
        }

        AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
        if (clip == null)
        {
            onFailed("Unity returned a null AudioClip.");
            yield break;
        }

        clip.name = Path.GetFileNameWithoutExtension(filePath);
        onLoaded(clip);
    }

    private void ScheduleInitialTrackAttempt()
    {
        float min = Mathf.Max(0f, _initialDelayMinSeconds?.Value ?? 0f);
        float max = Mathf.Max(min, _initialDelayMaxSeconds?.Value ?? min);
        _nextTrackAttemptAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(min, max);
    }

    private void ScheduleNextTrackAttempt(float delaySeconds)
    {
        _nextTrackAttemptAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySeconds);
    }

    private void TryStartTrack()
    {
        if (_ambientSource == null)
        {
            return;
        }

        if (_tracks.Count == 0)
        {
            ScheduleNextTrackAttempt(_quietRetrySeconds?.Value ?? 10f);
            return;
        }

        if (_onlyStartWhenQuiet != null && _onlyStartWhenQuiet.Value)
        {
            float startThreshold = Mathf.Max(0f, _quietThresholdToStart?.Value ?? 0.35f);
            bool quietEnough = IsWorldQuiet(startThreshold);
            if (!quietEnough)
            {
                if (_verboseLogging != null && _verboseLogging.Value)
                {
                    Logger.LogInfo($"Ambient start blocked by nearby noise. Loudness={_cachedNonFootstepLoudness:F3}, threshold={startThreshold:F3}");
                }

                ScheduleNextTrackAttempt(Mathf.Max(1f, _quietRetrySeconds?.Value ?? 12f));
                return;
            }
        }

        int trackIndex = UnityEngine.Random.Range(0, _tracks.Count);
        if (_tracks.Count > 1 && trackIndex == _lastTrackIndex)
        {
            trackIndex = (trackIndex + 1 + UnityEngine.Random.Range(0, _tracks.Count - 1)) % _tracks.Count;
        }
        _lastTrackIndex = trackIndex;

        AmbientTrack track = _tracks[trackIndex];
        _ambientSource.clip = track.Clip;
        _ambientSource.volume = 0f;
        _ambientSource.Play();

        _currentVolume = 0f;
        _targetVolume = Mathf.Clamp01(_ambientVolume?.Value ?? 0.35f);
        _trackIsActive = true;
        _stopTrackAfterFadeOut = false;
        _loudDurationSeconds = 0f;

        Logger.LogInfo($"Playing ambient track: {Path.GetFileName(track.FilePath)}");
    }

    private void TickNoiseMonitorWhilePlaying()
    {
        float now = Time.realtimeSinceStartup;
        float interval = Mathf.Max(0.2f, _quietCheckIntervalSeconds?.Value ?? 1.5f);
        if (now < _nextQuietSampleAt)
        {
            return;
        }

        float keepPlayingThreshold = Mathf.Max(0f, _quietThresholdToKeepPlaying?.Value ?? 0.65f);
        bool quietEnough = IsWorldQuiet(keepPlayingThreshold);
        if (quietEnough)
        {
            _loudDurationSeconds = 0f;
            return;
        }

        _loudDurationSeconds += interval;
        float requiredDuration = Mathf.Max(0.5f, _requiredLoudDurationToStopSeconds?.Value ?? 4f);
        if (_loudDurationSeconds >= requiredDuration)
        {
            if (_verboseLogging != null && _verboseLogging.Value)
            {
                Logger.LogInfo($"Stopping ambient due to sustained nearby noise. Loudness={_cachedNonFootstepLoudness:F3}, threshold={keepPlayingThreshold:F3}");
            }

            _targetVolume = 0f;
            _stopTrackAfterFadeOut = true;
            _loudDurationSeconds = 0f;
        }
    }

    private void UpdateVolumeFade()
    {
        if (_ambientSource == null)
        {
            return;
        }

        float fadeDuration = Mathf.Max(0.01f, _fadeSeconds?.Value ?? 2.5f);
        _currentVolume = Mathf.MoveTowards(_currentVolume, _targetVolume, Time.unscaledDeltaTime / fadeDuration);
        _ambientSource.volume = _currentVolume;

        if (_stopTrackAfterFadeOut && _currentVolume <= 0.001f)
        {
            _ambientSource.Stop();
            FinishTrack(interruptedByNoise: true);
        }
    }

    private void FinishTrack(bool interruptedByNoise)
    {
        if (_ambientSource == null)
        {
            return;
        }

        _trackIsActive = false;
        _stopTrackAfterFadeOut = false;
        _targetVolume = 0f;
        _currentVolume = 0f;
        _ambientSource.volume = 0f;
        _ambientSource.clip = null;
        _loudDurationSeconds = 0f;

        if (interruptedByNoise)
        {
            float retryDelay = Mathf.Max(1f, _quietRetrySeconds?.Value ?? 12f);
            ScheduleNextTrackAttempt(retryDelay);
            return;
        }

        float minGap = Mathf.Max(0f, _silenceGapMinSeconds?.Value ?? 70f);
        float maxGap = Mathf.Max(minGap, _silenceGapMaxSeconds?.Value ?? 220f);
        ScheduleNextTrackAttempt(UnityEngine.Random.Range(minGap, maxGap));
    }

    private bool IsWorldQuiet(float threshold)
    {
        float now = Time.realtimeSinceStartup;
        if (now >= _nextQuietSampleAt)
        {
            _cachedNonFootstepLoudness = SampleNearbyNonFootstepLoudness(threshold);
            _nextQuietSampleAt = now + Mathf.Max(0.2f, _quietCheckIntervalSeconds?.Value ?? 1.5f);
        }

        return _cachedNonFootstepLoudness <= threshold;
    }

    private float SampleNearbyNonFootstepLoudness(float earlyExitThreshold)
    {
        if (!TryGetListenerPosition(out Vector3 listenerPosition))
        {
            return 0f;
        }

        RefreshFootstepSourceCacheIfNeeded();

        AudioSource[] allSources = FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        float loudness = 0f;

        for (int i = 0; i < allSources.Length; i++)
        {
            AudioSource? source = allSources[i];
            if (source == null || source == _ambientSource)
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
            if (loudness > earlyExitThreshold)
            {
                break;
            }
        }

        return loudness;
    }

    private bool TryGetListenerPosition(out Vector3 listenerPosition)
    {
        listenerPosition = Vector3.zero;
        AudioListener? listener = FindAnyObjectByType<AudioListener>(FindObjectsInactive.Exclude);
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

        PlayerFootsteps[] footstepComponents = FindObjectsByType<PlayerFootsteps>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
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

    private static bool IsSupportedAudioFile(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        for (int i = 0; i < SupportedAudioExtensions.Length; i++)
        {
            if (extension == SupportedAudioExtensions[i])
            {
                return true;
            }
        }

        return false;
    }

    private static AudioType GetAudioType(string extension)
    {
        switch (extension)
        {
            case ".ogg":
                return AudioType.OGGVORBIS;
            case ".wav":
                return AudioType.WAV;
            case ".mp3":
                return AudioType.MPEG;
            case ".aif":
            case ".aiff":
                return AudioType.AIFF;
            default:
                return AudioType.UNKNOWN;
        }
    }

    private void DestroyLoadedTracks()
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            AmbientTrack track = _tracks[i];
            if (track.Clip != null)
            {
                Destroy(track.Clip);
            }
        }

        _tracks.Clear();
        _lastTrackIndex = -1;
        _tracksLoaded = false;
        _trackIsActive = false;
        _stopTrackAfterFadeOut = false;
        _currentVolume = 0f;
        _targetVolume = 0f;

        if (_ambientSource != null)
        {
            _ambientSource.Stop();
            _ambientSource.clip = null;
            _ambientSource.volume = 0f;
        }
    }

    private sealed class AmbientTrack
    {
        public AmbientTrack(string filePath, AudioClip clip)
        {
            FilePath = filePath;
            Clip = clip;
        }

        public string FilePath { get; }

        public AudioClip Clip { get; }
    }
}
