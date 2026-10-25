namespace AtmosphereEnhancements;

internal static class AmbientAudioDefinitions
{
    public const string RuntimeAudioFolderName = "AmbientAudio";
    public const string BedsFolderName = "Beds";
    public const string OneShotsFolderName = "OneShots";
    public const string EmbeddedAudioPrefix = "AtmosphereEnhancements.Resources.Audio.DefaultAmbient.";

    public static readonly string[] SupportedAudioExtensions =
    {
        ".ogg",
        ".wav",
        ".mp3",
        ".aif",
        ".aiff"
    };

    public static readonly string[] OneShotCategories =
    {
        OneShotCategoryNames.Drips,
        OneShotCategoryNames.Creaks,
        OneShotCategoryNames.RockTicks,
        OneShotCategoryNames.MetalClinks,
        OneShotCategoryNames.Wind
    };

    public static bool IsSupportedAudioFile(string filePath)
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

    public static AudioType GetAudioType(string extension)
    {
        switch (extension.ToLowerInvariant())
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
}

internal static class OneShotCategoryNames
{
    public const string Drips = "Drips";
    public const string Creaks = "Creaks";
    public const string RockTicks = "RockTicks";
    public const string MetalClinks = "MetalClinks";
    public const string Wind = "Wind";
}

internal sealed class AmbientTrack
{
    public AmbientTrack(string filePath, AudioClip clip, string categoryName)
    {
        FilePath = filePath;
        Clip = clip;
        CategoryName = categoryName;
    }

    public string FilePath { get; }

    public AudioClip Clip { get; }

    public string CategoryName { get; }
}
