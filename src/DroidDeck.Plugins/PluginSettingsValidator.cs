using System.Globalization;
using System.Text.Json.Nodes;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>One invalid value found while validating settings against a schema.</summary>
/// <param name="Field">The field key, or a dotted path into an object field.</param>
/// <param name="Code">A stable machine-readable error code.</param>
/// <param name="Message">A human-readable explanation for the UI.</param>
public sealed record PluginSettingsFieldError(string Field, string Code, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>Validates setting values against plugin settings schema fields.</summary>
public static class PluginSettingsValidator
{
    /// <summary>
    /// Validates one value against one field. <see langword="null"/> values pass:
    /// required-ness is checked by the caller that knows whether a default fills in.
    /// </summary>
    /// <param name="field">The field the value belongs to.</param>
    /// <param name="value">The value, or <see langword="null"/> when the setting is absent.</param>
    public static IReadOnlyList<PluginSettingsFieldError> Validate(PluginSettingsField field, JsonNode? value)
        => Validate(field, value, field.Key);

    private static List<PluginSettingsFieldError> Validate(
        PluginSettingsField field,
        JsonNode? value,
        string path)
    {
        var errors = new List<PluginSettingsFieldError>();
        if (value is null)
        {
            return errors;
        }

        switch (field.Type)
        {
            case PluginSettingsFieldType.Text or PluginSettingsFieldType.Path or PluginSettingsFieldType.Secret:
                ValidateText(field, value, path, errors);
                break;
            case PluginSettingsFieldType.Enum:
                if (value is JsonValue enumValue && enumValue.TryGetValue<string>(out var choice))
                {
                    if (!field.EnumValues.Contains(choice, StringComparer.Ordinal))
                    {
                        errors.Add(new PluginSettingsFieldError(
                            path,
                            "settings.value.enum",
                            $"'{choice}' is not one of the allowed values: {string.Join(", ", field.EnumValues)}."));
                    }
                }
                else
                {
                    AddTypeMismatch(path, "a string", errors);
                }

                break;
            case PluginSettingsFieldType.Number or PluginSettingsFieldType.WholeNumber:
                ValidateNumber(field, value, path, errors);
                break;
            case PluginSettingsFieldType.Boolean:
                if (value is not JsonValue boolValue || !boolValue.TryGetValue<bool>(out _))
                {
                    AddTypeMismatch(path, "true or false", errors);
                }

                break;
            case PluginSettingsFieldType.List:
                if (value is JsonArray list)
                {
                    foreach (var (item, index) in list.Select((item, index) => (item, index)))
                    {
                        var itemPath = $"{path}[{index}]";
                        switch (field.ItemType)
                        {
                            case PluginSettingsFieldType.Number or PluginSettingsFieldType.WholeNumber:
                                ValidateListNumber(field, item, itemPath, errors);
                                break;
                            case PluginSettingsFieldType.Boolean:
                                if (item is not JsonValue itemBool || !itemBool.TryGetValue<bool>(out _))
                                {
                                    AddTypeMismatch(itemPath, "true or false", errors);
                                }

                                break;
                            default:
                                if (item is not JsonValue itemText || !itemText.TryGetValue<string>(out _))
                                {
                                    AddTypeMismatch(itemPath, "a string", errors);
                                }

                                break;
                        }
                    }
                }
                else
                {
                    AddTypeMismatch(path, "a list", errors);
                }

                break;
            case PluginSettingsFieldType.Group:
                if (value is JsonObject nested)
                {
                    foreach (var property in field.Properties)
                    {
                        var nestedValue = nested[property.Key];
                        if (nestedValue is null && property.Required)
                        {
                            errors.Add(new PluginSettingsFieldError(
                                $"{path}.{property.Key}",
                                "settings.value.required",
                                $"'{property.DisplayName}' is required."));
                            continue;
                        }

                        foreach (var error in Validate(
                            property,
                            nestedValue,
                            $"{path}.{property.Key}"))
                        {
                            errors.Add(error);
                        }
                    }
                }
                else
                {
                    AddTypeMismatch(path, "an object", errors);
                }

                break;
        }

        return errors;
    }

    private static void ValidateText(
        PluginSettingsField field,
        JsonNode value,
        string path,
        List<PluginSettingsFieldError> errors)
    {
        if (value is not JsonValue textValue || !textValue.TryGetValue<string>(out var text))
        {
            AddTypeMismatch(path, "a string", errors);
            return;
        }

        if (field.MinLength is { } minLength && text.Length < minLength)
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.minLength",
                $"The value must be at least {minLength} character(s) long."));
        }

        if (field.MaxLength is { } maxLength && text.Length > maxLength)
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.maxLength",
                $"The value must be at most {maxLength} character(s) long."));
        }

        if (field.PatternRegex is { } pattern && !pattern.IsMatch(text))
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.pattern",
                $"The value does not match the required pattern '{field.Pattern}'."));
        }
    }

    private static void ValidateNumber(
        PluginSettingsField field,
        JsonNode value,
        string path,
        List<PluginSettingsFieldError> errors)
    {
        if (value is not JsonValue numberValue || !TryGetDouble(numberValue, out var number))
        {
            AddTypeMismatch(path, field.Type == PluginSettingsFieldType.WholeNumber ? "a whole number" : "a number", errors);
            return;
        }

        if (field.Type == PluginSettingsFieldType.WholeNumber && Math.Floor(number) != number)
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.integer",
                $"The value must be a whole number, not {Format(number)}."));
        }

        if (field.Minimum is { } minimum && number < minimum)
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.minimum",
                $"The value must be at least {Format(minimum)}, not {Format(number)}."));
        }

        if (field.Maximum is { } maximum && number > maximum)
        {
            errors.Add(new PluginSettingsFieldError(
                path,
                "settings.value.maximum",
                $"The value must be at most {Format(maximum)}, not {Format(number)}."));
        }
    }

    private static void ValidateListNumber(
        PluginSettingsField field,
        JsonNode? item,
        string itemPath,
        List<PluginSettingsFieldError> errors)
    {
        if (item is not JsonValue numberValue || !TryGetDouble(numberValue, out var number))
        {
            AddTypeMismatch(itemPath, field.ItemType == PluginSettingsFieldType.WholeNumber ? "a whole number" : "a number", errors);
            return;
        }

        if (field.ItemType == PluginSettingsFieldType.WholeNumber && Math.Floor(number) != number)
        {
            errors.Add(new PluginSettingsFieldError(
                itemPath,
                "settings.value.integer",
                $"The value must be a whole number, not {Format(number)}."));
        }
    }

    private static bool TryGetDouble(JsonValue value, out double number)
    {
        // In-memory values can be exact-typed wrappers, so every numeric
        // representation is tried before giving up.
        if (value.TryGetValue<double>(out number))
        {
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        if (value.TryGetValue<long>(out var integer))
        {
            number = integer;
            return true;
        }

        if (value.TryGetValue<int>(out var int32))
        {
            number = int32;
            return true;
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            number = (double)decimalValue;
            return true;
        }

        number = 0;
        return false;
    }

    private static void AddTypeMismatch(string path, string expected, List<PluginSettingsFieldError> errors)
        => errors.Add(new PluginSettingsFieldError(
            path,
            "settings.value.type",
            $"The value must be {expected}."));

    private static string Format(double number)
        => number.ToString("0.###############", CultureInfo.InvariantCulture);
}
