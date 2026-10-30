using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Themes;

/// <summary>Provides built-in themes and reads custom theme files.</summary>
public sealed class ThemeCatalog
{
    private readonly string _directory;

    /// <summary>Creates a catalog over a custom-theme directory.</summary>
    public ThemeCatalog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>Gets the built-in light theme.</summary>
    public static ThemeDefinition Light { get; } = new()
    {
        Id = "light",
        Name = "Light",
        Base = ThemeBase.Light,
        Colors = new ThemeColorTokens(),
        LogColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["verbose"] = "textMuted",
            ["debug"] = "textMuted",
            ["info"] = "text",
            ["warn"] = "warning",
            ["error"] = "error",
        },
    };

    /// <summary>Gets the built-in dark theme.</summary>
    public static ThemeDefinition Dark { get; } = new()
    {
        Id = "dark",
        Name = "Dark",
        Base = ThemeBase.Dark,
        Colors = new ThemeColorTokens
        {
            Background = "#1C1C1E",
            Surface = "#242426",
            SurfaceAlt = "#2C2C2E",
            Text = "#F2F2F7",
            TextMuted = "#A1A1A6",
            Accent = "#4CC2FF",
            AccentText = "#00243D",
            Border = "#6C6C70",
            Success = "#6CCB5F",
            Warning = "#FCE100",
            Error = "#FF99A4",
            Focus = "#4CC2FF",
        },
        LogColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["verbose"] = "textMuted",
            ["debug"] = "textMuted",
            ["info"] = "text",
            ["warn"] = "warning",
            ["error"] = "error",
        },
    };

    /// <summary>Gets the built-in high-contrast theme.</summary>
    public static ThemeDefinition HighContrast { get; } = new()
    {
        Id = "high-contrast",
        Name = "High contrast",
        Base = ThemeBase.HighContrast,
        Colors = new ThemeColorTokens
        {
            Background = "#000000",
            Surface = "#000000",
            SurfaceAlt = "#1A1A1A",
            Text = "#FFFFFF",
            TextMuted = "#FFFFFF",
            Accent = "#FFFF00",
            AccentText = "#000000",
            Border = "#FFFFFF",
            Success = "#00FF00",
            Warning = "#FFFF00",
            Error = "#FF0000",
            Focus = "#FFFF00",
        },
        ReducedMotion = true,
        LogColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["verbose"] = "textMuted",
            ["debug"] = "textMuted",
            ["info"] = "text",
            ["warn"] = "warning",
            ["error"] = "error",
        },
    };

    /// <summary>Gets the built-in themes.</summary>
    public static IReadOnlyList<ThemeDefinition> BuiltIn { get; } = [Light, Dark, HighContrast];

    /// <summary>Gets the built-in theme matching a base palette.</summary>
    public static ThemeDefinition ForBase(ThemeBase themeBase) => themeBase switch
    {
        ThemeBase.Dark => Dark,
        ThemeBase.HighContrast => HighContrast,
        _ => Light,
    };

    /// <summary>Finds a built-in theme by identifier.</summary>
    public static ThemeDefinition? FindBuiltIn(string themeId) =>
        BuiltIn.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets built-in and custom themes. Invalid custom files are ignored.</summary>
    public async Task<IReadOnlyList<ThemeDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var themes = new List<ThemeDefinition>(BuiltIn);
        if (!Directory.Exists(_directory))
        {
            return themes;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var theme = await TryLoadFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (theme is not null && themes.All(existing =>
                    !string.Equals(existing.Id, theme.Id, StringComparison.OrdinalIgnoreCase)))
            {
                themes.Add(theme);
            }
        }

        return themes;
    }

    /// <summary>Loads a built-in or custom theme by identifier.</summary>
    public async Task<ThemeDefinition?> FindAsync(string themeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);
        ValidateId(themeId);
        if (FindBuiltIn(themeId) is { } builtIn)
        {
            return builtIn.Clone();
        }

        var file = Path.Combine(_directory, themeId + ".json");
        return File.Exists(file)
            ? await TryLoadFileAsync(file, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Writes a custom theme file atomically.</summary>
    public Task SaveAsync(ThemeDefinition theme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ValidateId(theme.Id);
        Directory.CreateDirectory(_directory);
        return Infrastructure.AtomicFile.WriteAllTextAsync(
            Path.Combine(_directory, theme.Id + ".json"),
            Infrastructure.DroidDeckJson.Serialize(theme),
            cancellationToken: cancellationToken);
    }

    /// <summary>Deletes a custom theme file; built-in themes cannot be deleted.</summary>
    public bool Delete(string themeId)
    {
        ValidateId(themeId);
        if (FindBuiltIn(themeId) is not null)
        {
            return false;
        }

        var file = Path.Combine(_directory, themeId + ".json");
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    private static async Task<ThemeDefinition?> TryLoadFileAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var theme = Infrastructure.DroidDeckJson.Deserialize<ThemeDefinition>(text);
            return theme is { } value && !string.IsNullOrWhiteSpace(value.Id) ? value : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void ValidateId(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId) ||
            !themeId.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException($"'{themeId}' is not a valid theme identifier.", nameof(themeId));
        }
    }
}

/// <summary>
/// Applies, validates, and persists themes. Applying a theme writes the setting
/// immediately and raises <see cref="Changed"/> so the shell can update without a restart.
/// </summary>
public sealed class ThemeManager
{
    private readonly AppPaths _paths;
    private readonly IConfigurationService _configuration;
    private readonly ThemeCatalog _catalog;

    /// <summary>Creates a theme manager.</summary>
    public ThemeManager(AppPaths paths, IConfigurationService configuration)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _catalog = new ThemeCatalog(paths.ThemesDirectory);
    }

    /// <summary>Raised after the effective theme, accent, font, or motion preference changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the catalog used to load built-in and custom themes.</summary>
    public ThemeCatalog Catalog => _catalog;

    /// <summary>Gets the selected theme identifier, or <see langword="null"/> for the system preference.</summary>
    public string? SelectedThemeId => _configuration.GetString(SettingsPaths.ThemeId);

    /// <summary>Gets the effective accent override, or <see langword="null"/> for the theme accent.</summary>
    public string? AccentOverride => _configuration.GetString(SettingsPaths.AccentColor);

    /// <summary>Gets the effective font override, or <see langword="null"/> for the theme font.</summary>
    public string? FontOverride => _configuration.GetString(SettingsPaths.FontFamily);

    /// <summary>Gets whether motion should be reduced.</summary>
    public bool IsReducedMotion
    {
        get
        {
            var motion = _configuration.GetString(SettingsPaths.Motion);
            if (string.Equals(motion, "reduced", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GetCurrent().ReducedMotion;
        }
    }

    /// <summary>Resolves the effective theme, applying the accent and font overrides.</summary>
    public ThemeDefinition GetCurrent(ThemeBase systemBase = ThemeBase.Light)
    {
        var selected = SelectedThemeId;
        var theme = string.IsNullOrWhiteSpace(selected)
            ? ThemeCatalog.ForBase(ResolveBase(systemBase))
            : ThemeCatalog.FindBuiltIn(selected) ?? LoadCustom(selected) ?? ThemeCatalog.ForBase(ResolveBase(systemBase));
        var copy = theme.Clone();
        if (!string.IsNullOrWhiteSpace(AccentOverride))
        {
            copy.Colors.Accent = AccentOverride;
        }

        if (!string.IsNullOrWhiteSpace(FontOverride))
        {
            copy.FontFamily = FontOverride;
        }

        return copy;
    }

    /// <summary>Loads a theme and applies it after contrast validation.</summary>
    public async Task<ThemeDefinition> ApplyAsync(string themeId, CancellationToken cancellationToken = default)
    {
        var theme = await _catalog.FindAsync(themeId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"The theme '{themeId}' does not exist.", nameof(themeId));
        return await ApplyAsync(theme, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a theme after contrast validation and persists the selection.</summary>
    public async Task<ThemeDefinition> ApplyAsync(ThemeDefinition theme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var validation = ThemeContrast.Validate(theme);
        if (!validation.IsValid)
        {
            throw new ThemeValidationException(validation);
        }

        await _configuration.SetAsync(SettingsPaths.ThemeId, theme.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return theme.Clone();
    }

    /// <summary>Saves a custom theme and applies it immediately.</summary>
    public async Task<ThemeDefinition> SaveCustomAsync(
        ThemeDefinition theme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var validation = ThemeContrast.Validate(theme);
        if (!validation.IsValid)
        {
            throw new ThemeValidationException(validation);
        }

        await _catalog.SaveAsync(theme, cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(theme, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets an accent color after validating it against the current accent text.</summary>
    public async Task SetAccentAsync(string? color, CancellationToken cancellationToken = default)
    {
        if (color is not null)
        {
            // The system preference has no single base palette, so the light
            // tokens are used as the explicit fallback; an explicit dark or
            // high-contrast preference is honoured through ResolveBase.
            var theme = GetCurrent(ResolveBase(ThemeBase.Light));
            var ratio = ThemeContrast.Ratio(theme.Colors.AccentText, color);
            if (ratio < ThemeContrast.NormalTextMinimum)
            {
                throw new ThemeValidationException(new ThemeValidationResult(
                [
                    new ThemeContrastFinding(
                        "accent text on accent",
                        Math.Round(ratio, 2),
                        ThemeContrast.NormalTextMinimum,
                        IsError: true,
                        $"Accent text has a contrast ratio of {ratio:0.00}:1 against the chosen accent, below the required {ThemeContrast.NormalTextMinimum:0.0}:1."),
                ]));
            }
        }

        await _configuration.SetAsync(SettingsPaths.AccentColor, color, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the UI font family, or clears the override with <see langword="null"/>.</summary>
    public async Task SetFontAsync(string? family, CancellationToken cancellationToken = default)
    {
        await _configuration.SetAsync(SettingsPaths.FontFamily, family, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turns reduced motion on or off and persists the preference.</summary>
    public async Task SetReducedMotionAsync(bool reduced, CancellationToken cancellationToken = default)
    {
        await _configuration.SetAsync(
            SettingsPaths.Motion,
            reduced ? "reduced" : "full",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Exports a theme as JSON.</summary>
    public static string Export(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Infrastructure.DroidDeckJson.Serialize(theme);
    }

    /// <summary>Imports and applies a theme document.</summary>
    public async Task<ThemeDefinition> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ThemeDefinition? theme;
        try
        {
            theme = Infrastructure.DroidDeckJson.Deserialize<ThemeDefinition>(json);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException("The theme document is not valid JSON: " + exception.Message, nameof(json));
        }

        if (theme is null || string.IsNullOrWhiteSpace(theme.Id))
        {
            throw new ArgumentException("The theme document has no identifier.", nameof(json));
        }

        return await SaveCustomAsync(theme, cancellationToken).ConfigureAwait(false);
    }

    private ThemeBase ResolveBase(ThemeBase systemBase)
    {
        var preference = _configuration.GetString(SettingsPaths.Theme);
        if (string.Equals(preference, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeBase.Dark;
        }

        if (string.Equals(preference, "light", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeBase.Light;
        }

        return systemBase;
    }

    private ThemeDefinition? LoadCustom(string themeId)
    {
        if (!IsValidId(themeId))
        {
            return null;
        }

        var file = Path.Combine(_paths.ThemesDirectory, themeId + ".json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            // A hand-edited theme file is validated like an imported one; an
            // unreadable combination falls back to the built-in base palette.
            var theme = Infrastructure.DroidDeckJson.Deserialize<ThemeDefinition>(File.ReadAllText(file));
            return theme is not null && ThemeContrast.Validate(theme).IsValid ? theme : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool IsValidId(string themeId) =>
        !string.IsNullOrWhiteSpace(themeId) &&
        themeId.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
