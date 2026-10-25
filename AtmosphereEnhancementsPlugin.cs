namespace AtmosphereEnhancements;

[BepInPlugin(ModInfo.PLUGIN_GUID, ModInfo.PLUGIN_NAME, ModInfo.PLUGIN_VERSION)]
public sealed class AtmosphereEnhancementsPlugin : BaseUnityPlugin
{
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

    private ConfigEntry<bool>? _enableOneShots;
    private ConfigEntry<float>? _oneShotMinDelaySeconds;
    private ConfigEntry<float>? _oneShotMaxDelaySeconds;
    private ConfigEntry<float>? _oneShotBaseVolume;
    private ConfigEntry<float>? _oneShotPitchVariance;
    private ConfigEntry<float>? _oneShotVolumeVariance;
    private ConfigEntry<float>? _oneShotNoiseThresholdOverride;
    private ConfigEntry<bool>? _enableDrips;
    private ConfigEntry<bool>? _enableCreaks;
    private ConfigEntry<bool>? _enableRockTicks;
    private ConfigEntry<bool>? _enableMetalClinks;
    private ConfigEntry<bool>? _enableWind;
    private ConfigEntry<float>? _dripsWeight;
    private ConfigEntry<float>? _creaksWeight;
    private ConfigEntry<float>? _rockTicksWeight;
    private ConfigEntry<float>? _metalClinksWeight;
    private ConfigEntry<float>? _windWeight;

    private AudioSource? _bedSource;
    private AudioSource? _oneShotSource;
    private AmbientAudioLibrary? _audioLibrary;
    private AmbientNoiseMonitor? _noiseMonitor;
    private AmbientBedManager? _bedManager;
    private IncidentalAmbientManager? _oneShotManager;

    private void Awake()
    {
        BindConfig();

        _bedSource = CreateAudioSource("AmbientBedsSource", priority: 64, defaultVolume: 0f);
        _oneShotSource = CreateAudioSource("AmbientOneShotsSource", priority: 80, defaultVolume: 0f);

        if (!TryInitializeSystems())
        {
            enabled = false;
            return;
        }

        StartCoroutine(LoadAudioLibrary());
    }

    private void Update()
    {
        if (_enabled == null || _audioLibrary == null || _bedManager == null || _oneShotManager == null || _noiseMonitor == null)
        {
            return;
        }

        if (!_enabled.Value)
        {
            _bedManager.HandleDisabled();
            _oneShotManager.HandleDisabled();
            _noiseMonitor.Reset();
            return;
        }

        _noiseMonitor.Update();

        if (!_audioLibrary.IsLoaded)
        {
            return;
        }

        _bedManager.Update(_noiseMonitor);
        _oneShotManager.Update(_noiseMonitor);
    }

    private void OnDestroy()
    {
        StopAllCoroutines();

        _bedManager?.HandleDisabled();
        _oneShotManager?.HandleDisabled();
        _audioLibrary?.DestroyLoadedAudio();
    }

    private bool TryInitializeSystems()
    {
        if (_bedSource == null || _oneShotSource == null)
        {
            Logger.LogError("Failed to initialize audio sources.");
            return false;
        }

        if (_onlyStartWhenQuiet == null
            || _quietThresholdToStart == null
            || _quietThresholdToKeepPlaying == null
            || _quietCheckIntervalSeconds == null
            || _stopIfNoLongerQuiet == null
            || _requiredLoudDurationToStopSeconds == null
            || _verboseLogging == null
            || _quietRetrySeconds == null
            || _ambientVolume == null
            || _fadeSeconds == null
            || _initialDelayMinSeconds == null
            || _initialDelayMaxSeconds == null
            || _silenceGapMinSeconds == null
            || _silenceGapMaxSeconds == null
            || _enableOneShots == null
            || _oneShotMinDelaySeconds == null
            || _oneShotMaxDelaySeconds == null
            || _oneShotBaseVolume == null
            || _oneShotPitchVariance == null
            || _oneShotVolumeVariance == null
            || _oneShotNoiseThresholdOverride == null
            || _enableDrips == null
            || _enableCreaks == null
            || _enableRockTicks == null
            || _enableMetalClinks == null
            || _enableWind == null
            || _dripsWeight == null
            || _creaksWeight == null
            || _rockTicksWeight == null
            || _metalClinksWeight == null
            || _windWeight == null)
        {
            Logger.LogError("AtmosphereEnhancements config was not fully initialized.");
            return false;
        }

        _audioLibrary = new AmbientAudioLibrary(Logger);
        string pluginDirectory = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
        _audioLibrary.PrepareRuntimeAudioFolder(pluginDirectory);
        _audioLibrary.ExportPackagedAudio(Assembly.GetExecutingAssembly());

        _noiseMonitor = new AmbientNoiseMonitor(
            Logger,
            _onlyStartWhenQuiet,
            _quietThresholdToStart,
            _quietThresholdToKeepPlaying,
            _quietCheckIntervalSeconds,
            _stopIfNoLongerQuiet,
            _requiredLoudDurationToStopSeconds,
            _verboseLogging);

        _noiseMonitor.RegisterIgnoredSource(_bedSource);
        _noiseMonitor.RegisterIgnoredSource(_oneShotSource);

        _bedManager = new AmbientBedManager(
            _bedSource,
            Logger,
            _ambientVolume,
            _fadeSeconds,
            _initialDelayMinSeconds,
            _initialDelayMaxSeconds,
            _silenceGapMinSeconds,
            _silenceGapMaxSeconds,
            _quietRetrySeconds);

        _oneShotManager = new IncidentalAmbientManager(
            _oneShotSource,
            Logger,
            _enableOneShots,
            _oneShotMinDelaySeconds,
            _oneShotMaxDelaySeconds,
            _oneShotBaseVolume,
            _oneShotPitchVariance,
            _oneShotVolumeVariance,
            _oneShotNoiseThresholdOverride,
            _enableDrips,
            _enableCreaks,
            _enableRockTicks,
            _enableMetalClinks,
            _enableWind,
            _dripsWeight,
            _creaksWeight,
            _rockTicksWeight,
            _metalClinksWeight,
            _windWeight);

        return true;
    }

    private IEnumerator LoadAudioLibrary()
    {
        if (_audioLibrary == null || _streamLoadedAudio == null || _bedManager == null || _oneShotManager == null)
        {
            yield break;
        }

        yield return StartCoroutine(_audioLibrary.LoadTracksFromRuntimeFolder(_streamLoadedAudio.Value));

        _bedManager.SetTracks(_audioLibrary.BedTracks);
        _oneShotManager.SetTracks(_audioLibrary.OneShotTracksByCategory);
    }

    private void BindConfig()
    {
        _enabled = Config.Bind("General", "Enabled", true, "Master toggle for all ambience playback.");
        _ambientVolume = Config.Bind("General", "AmbientVolume", 0.35f, "Base volume for long ambient beds (0-1).");
        _fadeSeconds = Config.Bind("General", "FadeSeconds", 2.5f, "Fade duration in seconds when starting or stopping ambient beds.");

        _initialDelayMinSeconds = Config.Bind("Timing", "InitialDelayMinSeconds", 25f, "Minimum delay before the first ambient bed attempt.");
        _initialDelayMaxSeconds = Config.Bind("Timing", "InitialDelayMaxSeconds", 90f, "Maximum delay before the first ambient bed attempt.");
        _silenceGapMinSeconds = Config.Bind("Timing", "SilenceGapMinSeconds", 70f, "Minimum silence gap between finished ambient beds.");
        _silenceGapMaxSeconds = Config.Bind("Timing", "SilenceGapMaxSeconds", 220f, "Maximum silence gap between finished ambient beds.");

        _onlyStartWhenQuiet = Config.Bind("QuietMode", "OnlyStartWhenQuiet", true, "Only allow ambience to start when nearby non-footstep audio is below the threshold.");
        _quietThresholdToStart = Config.Bind("QuietMode", "QuietThresholdToStart", 0.35f, "Estimated non-footstep loudness threshold required to start ambience.");
        _quietThresholdToKeepPlaying = Config.Bind("QuietMode", "QuietThresholdToKeepPlaying", 0.65f, "If stop-when-loud is enabled, loudness above this value can stop a playing ambient bed.");
        _quietCheckIntervalSeconds = Config.Bind("QuietMode", "QuietCheckIntervalSeconds", 1.5f, "Seconds between nearby loudness checks.");
        _quietRetrySeconds = Config.Bind("QuietMode", "QuietRetrySeconds", 12f, "Retry delay when quiet-mode blocks an ambience start.");
        _stopIfNoLongerQuiet = Config.Bind("QuietMode", "StopIfNoLongerQuiet", true, "Fade out and stop a bed when nearby machines or other noise stay too loud.");
        _requiredLoudDurationToStopSeconds = Config.Bind("QuietMode", "RequiredLoudDurationToStopSeconds", 4f, "How long the world must stay loud before a playing bed is stopped.");

        _streamLoadedAudio = Config.Bind("Loading", "StreamLoadedAudio", true, "Use streaming mode when loading clips from files.");
        _verboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Enable extra logging for quiet checks and playback decisions.");

        _enableOneShots = Config.Bind("OneShots", "EnableOneShots", true, "Enable short incidental one-shot ambience.");
        _oneShotMinDelaySeconds = Config.Bind("OneShots", "OneShotMinDelaySeconds", 15f, "Minimum delay between incidental one-shot attempts.");
        _oneShotMaxDelaySeconds = Config.Bind("OneShots", "OneShotMaxDelaySeconds", 15f, "Maximum delay between incidental one-shot attempts.");
        _oneShotBaseVolume = Config.Bind("OneShots", "OneShotBaseVolume", 0.18f, "Base volume for incidental one-shots (0-1).");
        _oneShotPitchVariance = Config.Bind("OneShots", "OneShotPitchVariance", 0.04f, "Random pitch variance for one-shots. Example: 0.04 allows a pitch range from 0.96 to 1.04.");
        _oneShotVolumeVariance = Config.Bind("OneShots", "OneShotVolumeVariance", 0.03f, "Random volume variance for one-shots.");
        _oneShotNoiseThresholdOverride = Config.Bind("OneShots", "OneShotNoiseThresholdOverride", 0f, "Optional quiet-mode start threshold override for one-shots. Set to 0 to reuse QuietThresholdToStart.");

        _enableDrips = Config.Bind("OneShotCategories", "EnableDrips", true, "Allow water drip and similar small detail one-shots.");
        _enableCreaks = Config.Bind("OneShotCategories", "EnableCreaks", true, "Allow cave creak one-shots.");
        _enableRockTicks = Config.Bind("OneShotCategories", "EnableRockTicks", true, "Allow subtle rock tick one-shots.");
        _enableMetalClinks = Config.Bind("OneShotCategories", "EnableMetalClinks", true, "Allow distant faint metallic clink one-shots.");
        _enableWind = Config.Bind("OneShotCategories", "EnableWind", true, "Allow light wind and air-movement one-shots.");

        _dripsWeight = Config.Bind("OneShotWeights", "DripsWeight", 5f, "Relative selection weight for drip one-shots.");
        _creaksWeight = Config.Bind("OneShotWeights", "CreaksWeight", 2f, "Relative selection weight for creak one-shots.");
        _rockTicksWeight = Config.Bind("OneShotWeights", "RockTicksWeight", 2f, "Relative selection weight for rock tick one-shots.");
        _metalClinksWeight = Config.Bind("OneShotWeights", "MetalClinksWeight", 1f, "Relative selection weight for metallic clink one-shots.");
        _windWeight = Config.Bind("OneShotWeights", "WindWeight", 3f, "Relative selection weight for wind one-shots.");
    }

    private AudioSource CreateAudioSource(string childName, int priority, float defaultVolume)
    {
        GameObject child = new GameObject(childName);
        child.transform.SetParent(transform, false);

        AudioSource source = child.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        source.priority = priority;
        source.ignoreListenerPause = true;
        source.volume = defaultVolume;
        source.dopplerLevel = 0f;
        return source;
    }
}
