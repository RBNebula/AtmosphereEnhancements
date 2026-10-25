namespace AtmosphereEnhancements;

internal sealed class AmbientAudioLibrary
{
    private readonly ManualLogSource _logger;
    private readonly List<AmbientTrack> _bedTracks = new List<AmbientTrack>();
    private readonly Dictionary<string, List<AmbientTrack>> _oneShotTracksByCategory = new Dictionary<string, List<AmbientTrack>>(StringComparer.OrdinalIgnoreCase);

    public AmbientAudioLibrary(ManualLogSource logger)
    {
        _logger = logger;

        for (int i = 0; i < AmbientAudioDefinitions.OneShotCategories.Length; i++)
        {
            string category = AmbientAudioDefinitions.OneShotCategories[i];
            _oneShotTracksByCategory[category] = new List<AmbientTrack>();
        }
    }

    public bool IsLoaded { get; private set; }

    public string RuntimeAudioFolderPath { get; private set; } = string.Empty;

    public string BedsFolderPath { get; private set; } = string.Empty;

    public string OneShotsFolderPath { get; private set; } = string.Empty;

    public IReadOnlyList<AmbientTrack> BedTracks => _bedTracks;

    public IReadOnlyDictionary<string, List<AmbientTrack>> OneShotTracksByCategory => _oneShotTracksByCategory;

    public void PrepareRuntimeAudioFolder(string pluginDirectory)
    {
        RuntimeAudioFolderPath = Path.Combine(pluginDirectory, AmbientAudioDefinitions.RuntimeAudioFolderName);
        BedsFolderPath = Path.Combine(RuntimeAudioFolderPath, AmbientAudioDefinitions.BedsFolderName);
        OneShotsFolderPath = Path.Combine(RuntimeAudioFolderPath, AmbientAudioDefinitions.OneShotsFolderName);

        Directory.CreateDirectory(RuntimeAudioFolderPath);
        Directory.CreateDirectory(BedsFolderPath);
        Directory.CreateDirectory(OneShotsFolderPath);

        for (int i = 0; i < AmbientAudioDefinitions.OneShotCategories.Length; i++)
        {
            Directory.CreateDirectory(Path.Combine(OneShotsFolderPath, AmbientAudioDefinitions.OneShotCategories[i]));
        }

        WriteGuidanceFile();
    }

    public void ExportPackagedAudio(Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(RuntimeAudioFolderPath))
        {
            return;
        }

        string[] resources = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(AmbientAudioDefinitions.EmbeddedAudioPrefix, StringComparison.Ordinal))
            .ToArray();

        int exportedCount = 0;
        for (int i = 0; i < resources.Length; i++)
        {
            string resourceName = resources[i];
            string relativePath = GetRuntimeRelativePath(resourceName);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            string outputPath = Path.Combine(RuntimeAudioFolderPath, relativePath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

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
            _logger.LogInfo($"Exported {exportedCount} packaged ambient file(s) to: {RuntimeAudioFolderPath}");
        }
    }

    public IEnumerator LoadTracksFromRuntimeFolder(bool streamLoadedAudio)
    {
        IsLoaded = false;
        DestroyLoadedAudio();

        if (string.IsNullOrWhiteSpace(RuntimeAudioFolderPath) || !Directory.Exists(RuntimeAudioFolderPath))
        {
            _logger.LogWarning("Runtime ambient folder does not exist. Ambient playback is disabled.");
            IsLoaded = true;
            yield break;
        }

        HashSet<string> loadedBedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] legacyBedFiles = Directory
            .EnumerateFiles(RuntimeAudioFolderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(AmbientAudioDefinitions.IsSupportedAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (int i = 0; i < legacyBedFiles.Length; i++)
        {
            loadedBedPaths.Add(legacyBedFiles[i]);
        }

        string[] categorizedBedFiles = Directory
            .EnumerateFiles(BedsFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(AmbientAudioDefinitions.IsSupportedAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (int i = 0; i < categorizedBedFiles.Length; i++)
        {
            loadedBedPaths.Add(categorizedBedFiles[i]);
        }

        string[] allBedFiles = loadedBedPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allBedFiles.Length > 0)
        {
            _logger.LogInfo($"Loading {allBedFiles.Length} ambient bed file(s) from: {RuntimeAudioFolderPath}");
        }

        for (int i = 0; i < allBedFiles.Length; i++)
        {
            string filePath = allBedFiles[i];
            AudioClip? clip = null;
            string? error = null;

            yield return LoadClipFromFile(
                filePath,
                streamLoadedAudio,
                loadedClip => clip = loadedClip,
                message => error = message);

            if (clip != null)
            {
                _bedTracks.Add(new AmbientTrack(filePath, clip, AmbientAudioDefinitions.BedsFolderName));
            }
            else
            {
                _logger.LogWarning($"Failed to load ambient bed '{filePath}': {error ?? "Unknown error"}");
            }
        }

        int totalOneShots = 0;
        bool loadedCategorizedOneShots = false;
        for (int categoryIndex = 0; categoryIndex < AmbientAudioDefinitions.OneShotCategories.Length; categoryIndex++)
        {
            string category = AmbientAudioDefinitions.OneShotCategories[categoryIndex];
            string categoryDirectory = Path.Combine(OneShotsFolderPath, category);
            string[] categoryFiles = Directory
                .EnumerateFiles(categoryDirectory, "*.*", SearchOption.AllDirectories)
                .Where(AmbientAudioDefinitions.IsSupportedAudioFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            totalOneShots += categoryFiles.Length;

            for (int fileIndex = 0; fileIndex < categoryFiles.Length; fileIndex++)
            {
                string filePath = categoryFiles[fileIndex];
                AudioClip? clip = null;
                string? error = null;

                yield return LoadClipFromFile(
                    filePath,
                    streamLoadedAudio,
                    loadedClip => clip = loadedClip,
                    message => error = message);

                if (clip != null)
                {
                    _oneShotTracksByCategory[category].Add(new AmbientTrack(filePath, clip, category));
                    loadedCategorizedOneShots = true;
                }
                else
                {
                    _logger.LogWarning($"Failed to load incidental one-shot '{filePath}': {error ?? "Unknown error"}");
                }
            }
        }

        if (!loadedCategorizedOneShots && legacyBedFiles.Length > 0)
        {
            _logger.LogInfo("No categorized one-shot files were found. Reusing legacy loose AmbientAudio files as one-shot test clips.");

            for (int fileIndex = 0; fileIndex < legacyBedFiles.Length; fileIndex++)
            {
                string filePath = legacyBedFiles[fileIndex];
                AudioClip? clip = null;
                string? error = null;

                yield return LoadClipFromFile(
                    filePath,
                    streamLoadedAudio,
                    loadedClip => clip = loadedClip,
                    message => error = message);

                if (clip != null)
                {
                    _oneShotTracksByCategory[OneShotCategoryNames.Drips].Add(new AmbientTrack(filePath, clip, OneShotCategoryNames.Drips));
                    totalOneShots++;
                }
                else
                {
                    _logger.LogWarning($"Failed to load legacy loose one-shot test clip '{filePath}': {error ?? "Unknown error"}");
                }
            }
        }

        if (_bedTracks.Count == 0)
        {
            _logger.LogWarning($"No ambient bed audio files found in '{RuntimeAudioFolderPath}'.");
        }

        if (totalOneShots == 0)
        {
            _logger.LogInfo($"No incidental one-shot files found in '{OneShotsFolderPath}'.");
        }

        IsLoaded = true;
        _logger.LogInfo($"Ambient loading complete. Loaded {_bedTracks.Count} bed(s) and {GetTotalOneShotCount()} one-shot(s).");
    }

    public void DestroyLoadedAudio()
    {
        for (int i = 0; i < _bedTracks.Count; i++)
        {
            DestroyTrackClip(_bedTracks[i]);
        }

        _bedTracks.Clear();

        foreach (KeyValuePair<string, List<AmbientTrack>> pair in _oneShotTracksByCategory)
        {
            List<AmbientTrack> categoryTracks = pair.Value;
            for (int i = 0; i < categoryTracks.Count; i++)
            {
                DestroyTrackClip(categoryTracks[i]);
            }

            categoryTracks.Clear();
        }

        IsLoaded = false;
    }

    private static void DestroyTrackClip(AmbientTrack track)
    {
        if (track.Clip != null)
        {
            UnityEngine.Object.Destroy(track.Clip);
        }
    }

    private static IEnumerator LoadClipFromFile(
        string filePath,
        bool streamLoadedAudio,
        Action<AudioClip> onLoaded,
        Action<string> onFailed)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        AudioType audioType = AmbientAudioDefinitions.GetAudioType(extension);
        if (audioType == AudioType.UNKNOWN)
        {
            onFailed("Unsupported extension: " + extension);
            yield break;
        }

        Uri fileUri = new Uri(filePath);
        using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileUri.AbsoluteUri, audioType);
        if (request.downloadHandler is DownloadHandlerAudioClip handler)
        {
            handler.streamAudio = streamLoadedAudio;
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

    private void WriteGuidanceFile()
    {
        string guidanceFilePath = Path.Combine(RuntimeAudioFolderPath, "_ADD_AMBIENT_AUDIO_HERE.txt");
        if (File.Exists(guidanceFilePath))
        {
            return;
        }

        File.WriteAllLines(guidanceFilePath, new[]
        {
            "Atmosphere Enhancements - Runtime audio folder",
            "Ambient beds go in Beds\\",
            "Incidental one-shots go in OneShots\\Drips, Creaks, RockTicks, MetalClinks, or Wind",
            "Legacy loose audio files placed directly in AmbientAudio\\ still load as ambient beds",
            "Supported formats: .ogg .wav .mp3 .aif .aiff",
            "Packaged tracks from the DLL are exported here automatically if they are missing"
        });
    }

    private static string GetRuntimeRelativePath(string resourceName)
    {
        string relativeName = resourceName.Substring(AmbientAudioDefinitions.EmbeddedAudioPrefix.Length);
        if (string.IsNullOrWhiteSpace(relativeName))
        {
            return string.Empty;
        }

        if (relativeName.StartsWith(AmbientAudioDefinitions.BedsFolderName + ".", StringComparison.Ordinal))
        {
            return Path.Combine(
                AmbientAudioDefinitions.BedsFolderName,
                relativeName.Substring(AmbientAudioDefinitions.BedsFolderName.Length + 1));
        }

        if (relativeName.StartsWith(AmbientAudioDefinitions.OneShotsFolderName + ".", StringComparison.Ordinal))
        {
            string oneShotRelative = relativeName.Substring(AmbientAudioDefinitions.OneShotsFolderName.Length + 1);
            int separatorIndex = oneShotRelative.IndexOf('.');
            if (separatorIndex > 0)
            {
                string category = oneShotRelative.Substring(0, separatorIndex);
                string fileName = oneShotRelative.Substring(separatorIndex + 1);
                return Path.Combine(AmbientAudioDefinitions.OneShotsFolderName, category, fileName);
            }
        }

        return relativeName;
    }

    private int GetTotalOneShotCount()
    {
        int total = 0;
        foreach (KeyValuePair<string, List<AmbientTrack>> pair in _oneShotTracksByCategory)
        {
            total += pair.Value.Count;
        }

        return total;
    }
}
