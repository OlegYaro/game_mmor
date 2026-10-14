using Godot;

namespace Game.Sim;

/// <summary>
/// Player settings in <c>user://settings.cfg</c> (ConfigFile): language and master volume. Loaded and applied at start.
/// The volume only drives the Master audio bus for now; there is no audio yet. Failures never throw: defaults are used
/// and the problem is logged as a warning.
/// </summary>
public static class UserSettings
{
    /// <summary>Supported language codes, in the order of the language selector.</summary>
    public static readonly string[] Languages = ["ru", "en"];

    /// <summary>Default language.</summary>
    public const string DefaultLanguage = "ru";

    /// <summary>Default master volume (0 to 1).</summary>
    public const float DefaultVolume = 0.8f;

    private const string FilePath = "user://settings.cfg";
    private const string Section = "game";
    private const string LanguageKey = "language";
    private const string VolumeKey = "master_volume";

    /// <summary>Current language code.</summary>
    public static string Language { get; private set; } = DefaultLanguage;

    /// <summary>Current master volume, 0 to 1.</summary>
    public static float MasterVolume { get; private set; } = DefaultVolume;

    /// <summary>Reads the file (if any) and applies the values.</summary>
    public static void LoadAndApply()
    {
        var config = new ConfigFile();
        Error error = config.Load(FilePath);
        if (error == Error.Ok)
        {
            string language = config.GetValue(Section, LanguageKey, DefaultLanguage).AsString();
            Language = Array.IndexOf(Languages, language) >= 0 ? language : DefaultLanguage;
            float volume = config.GetValue(Section, VolumeKey, DefaultVolume).AsSingle();
            MasterVolume = float.IsNaN(volume) ? DefaultVolume : Math.Clamp(volume, 0f, 1f);
        }
        else if (error != Error.FileNotFound)
        {
            GD.PushWarning($"Cannot read {FilePath}: {error}. Using defaults.");
        }

        Apply();
    }

    /// <summary>Changes the language, applies it and saves.</summary>
    public static void SetLanguage(string language)
    {
        if (Array.IndexOf(Languages, language) < 0)
        {
            return;
        }

        Language = language;
        Apply();
        Save();
    }

    /// <summary>Switches to the other language (F2).</summary>
    public static void ToggleLanguage() =>
        SetLanguage(Language == Languages[0] ? Languages[1] : Languages[0]);

    /// <summary>Changes the master volume (0 to 1), applies it and saves.</summary>
    public static void SetVolume(float volume)
    {
        MasterVolume = Math.Clamp(volume, 0f, 1f);
        Apply();
        Save();
    }

    /// <summary>Pushes the stored values into the engine.</summary>
    public static void Apply()
    {
        TranslationServer.SetLocale(Language);
        int master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
        {
            AudioServer.SetBusVolumeDb(master, MasterVolume <= 0.0001f ? -80f : Mathf.LinearToDb(MasterVolume));
        }
    }

    /// <summary>Writes the file. Failures are logged, never thrown.</summary>
    public static void Save()
    {
        var config = new ConfigFile();
        config.SetValue(Section, LanguageKey, Language);
        config.SetValue(Section, VolumeKey, MasterVolume);
        Error error = config.Save(FilePath);
        if (error != Error.Ok)
        {
            GD.PushWarning($"Cannot write {FilePath}: {error}.");
        }
    }
}
