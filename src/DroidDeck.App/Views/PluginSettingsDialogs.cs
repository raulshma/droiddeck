using System.Globalization;
using System.Text.Json.Nodes;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>
/// Shows the settings dialog a plugin's settings.schema.json generates: typed
/// controls per field, inline field errors, and secret fields that are set and
/// cleared but never displayed. Saving is blocked while field errors exist.
/// </summary>
internal static class PluginSettingsDialogs
{
    /// <summary>
    /// Shows the settings for one plugin. Plugins without a schema, or with a
    /// schema the host cannot use, get an explanation instead of controls.
    /// </summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, ExtensionsPage.PluginRow row, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(secrets);

        if (!File.Exists(PluginSettingsSchemaReader.GetSchemaPath(row.InstallPath)))
        {
            await ShowNoticeAsync(
                xamlRoot,
                $"{row.Title} settings",
                "This plugin does not ship a settings schema, so it has no settings to configure here.");
            return;
        }

        var read = await PluginSettingsSchemaReader.ReadAsync(row.InstallPath);
        if (read.Schema is null)
        {
            var reason = read.Validation.Issues.FirstOrDefault(static issue =>
                issue.Severity == PluginValidationSeverity.Error)?.Message ?? "The schema is invalid.";
            await ShowNoticeAsync(
                xamlRoot,
                $"{row.Title} settings",
                "The plugin's settings schema could not be used: " + reason);
            return;
        }

        var settings = new SchemaPluginSettings(
            Path.Combine(row.InstallPath, "settings.json"),
            read.Schema,
            row.PluginId,
            secrets);
        try
        {
            var editor = await PluginSettingsEditor.LoadAsync(settings);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"{row.Title} settings",
                Content = new ScrollViewer
                {
                    Content = editor.Root,
                    MaxHeight = 480,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                // Saving is asynchronous; invalid values keep the dialog open and
                // show the field errors inline instead of closing.
                args.Cancel = true;
                if (await editor.SaveAsync())
                {
                    sender.Hide();
                }
            };
            await dialog.ShowAsync();
        }
        finally
        {
            settings.Dispose();
        }
    }

    private static async Task ShowNoticeAsync(XamlRoot xamlRoot, string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
        }.ShowAsync();
    }

    /// <summary>One schema field rendered as a control with its inline error slot.</summary>
    private sealed class PluginSettingsRow
    {
        private const string SecretPlaceholder = "Enter a new value to set";

        private readonly SchemaPluginSettings _settings;
        private readonly List<PluginSettingsRow> _children = [];
        private FrameworkElement _control = new StackPanel();
        private TextBox? _textBox;
        private NumberBox? _numberBox;
        private ToggleSwitch? _toggle;
        private ComboBox? _combo;
        private PasswordBox? _passwordBox;
        private TextBlock? _secretStatus;
        private TextBlock? _errorText;

        private PluginSettingsRow(SchemaPluginSettings settings, PluginSettingsField field)
        {
            _settings = settings;
            Field = field;
        }

        public PluginSettingsField Field { get; }

        /// <summary>The full row layout: title, description, control, error slot.</summary>
        public StackPanel Root { get; private set; } = new();

        /// <summary>Builds the row for a schema field, seeded with its current value.</summary>
        public static async Task<PluginSettingsRow> BuildAsync(
            SchemaPluginSettings settings,
            PluginSettingsField field,
            JsonNode? parentValue)
        {
            var row = new PluginSettingsRow(settings, field);
            var value = parentValue is null
                ? await settings.GetNodeAsync(field.Key)
                : parentValue[field.Key];
            await row.BuildControlAsync(value);
            row.Root = row.BuildLayout();
            return row;
        }

        private async Task BuildControlAsync(JsonNode? value)
        {
            switch (Field.Type)
            {
                case PluginSettingsFieldType.Text or PluginSettingsFieldType.Path:
                    _textBox = new TextBox
                    {
                        Text = value is JsonValue textValue &&
                            textValue.TryGetValue<string>(out var text)
                                ? text
                                : string.Empty,
                    };
                    _control = _textBox;
                    break;
                case PluginSettingsFieldType.WholeNumber:
                    _numberBox = CreateNumberBox(integersOnly: true, value);
                    _control = _numberBox;
                    break;
                case PluginSettingsFieldType.Number:
                    _numberBox = CreateNumberBox(integersOnly: false, value);
                    _control = _numberBox;
                    break;
                case PluginSettingsFieldType.Boolean:
                    _toggle = new ToggleSwitch
                    {
                        IsOn = value is JsonValue toggleValue && toggleValue.TryGetValue<bool>(out var on) && on,
                        OnContent = "On",
                        OffContent = "Off",
                        Margin = new Thickness(0, -8, 0, -12),
                    };
                    _control = _toggle;
                    break;
                case PluginSettingsFieldType.Enum:
                    _combo = new ComboBox { ItemsSource = Field.EnumValues.ToList() };
                    if (value is JsonValue choice && choice.TryGetValue<string>(out var selected))
                    {
                        _combo.SelectedItem = selected;
                    }

                    _control = _combo;
                    break;
                case PluginSettingsFieldType.Secret:
                    _control = BuildSecretControl();
                    break;
                case PluginSettingsFieldType.List:
                    _textBox = new TextBox
                    {
                        AcceptsReturn = true,
                        Height = 96,
                        Text = FormatList(value),
                    };
                    _control = _textBox;
                    break;
                case PluginSettingsFieldType.Group:
                    var group = new StackPanel { Spacing = 12, Padding = new Thickness(12, 8, 0, 0) };
                    foreach (var nested in Field.Properties)
                    {
                        var nestedRow = await BuildAsync(_settings, nested, value);
                        group.Children.Add(nestedRow.Root);
                        _children.Add(nestedRow);
                    }

                    _control = group;
                    break;
            }
        }

        private StackPanel BuildSecretControl()
        {
            _passwordBox = new PasswordBox
            {
                PasswordRevealMode = PasswordRevealMode.Hidden,
                PlaceholderText = SecretPlaceholder,
            };
            _secretStatus = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.75,
                Text = "Not set",
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var setButton = new Button { Content = "Set" };
            setButton.Click += (_, _) => _ = ApplySecretAsync();
            var clearButton = new Button { Content = "Clear" };
            clearButton.Click += (_, _) => _ = ClearSecretAsync();
            buttons.Children.Add(setButton);
            buttons.Children.Add(clearButton);

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(_passwordBox);
            panel.Children.Add(buttons);
            panel.Children.Add(_secretStatus);
            return panel;
        }

        /// <summary>Applies a new secret value and refreshes the status label.</summary>
        private async Task ApplySecretAsync()
        {
            if (_passwordBox is null)
            {
                return;
            }

            var value = _passwordBox.Password;
            if (value.Length == 0)
            {
                ShowOwnError("Enter a value to set, or choose Clear to remove the secret.");
                return;
            }

            var result = await _settings.SetSecretAsync(Field.Key, value);
            _passwordBox.Password = string.Empty;
            if (!result.IsSaved)
            {
                ShowErrors(result.Errors);
                return;
            }

            ClearError();
            _secretStatus!.Text = "Configured";
        }

        private async Task ClearSecretAsync()
        {
            await _settings.ClearSecretAsync(Field.Key);
            ClearError();
            _secretStatus!.Text = "Not set";
        }

        /// <summary>Shows the stored state of a secret without ever showing the value.</summary>
        public async Task RefreshSecretStatusAsync()
        {
            if (_secretStatus is not null)
            {
                _secretStatus.Text = await _settings.IsSecretSetAsync(Field.Key) ? "Configured" : "Not set";
            }
        }

        /// <summary>Writes the control value when it changed; returns field errors.</summary>
        public async Task<IReadOnlyList<PluginSettingsFieldError>> SaveAsync()
        {
            var errors = new List<PluginSettingsFieldError>();
            switch (Field.Type)
            {
                case PluginSettingsFieldType.Secret:
                    // Secrets are applied immediately through the Set and Clear
                    // actions; Save never touches their values.
                    break;
                case PluginSettingsFieldType.Group:
                    // Nested values are written as one object so the schema
                    // validator sees the whole group at once.
                    var composed = new JsonObject();
                    foreach (var child in _children)
                    {
                        var (childValue, childHasValue) = child.ReadControl();
                        if (childHasValue && childValue is not null)
                        {
                            composed[child.Field.Key] = childValue.DeepClone();
                        }
                    }

                    var currentGroup = await _settings.GetNodeAsync(Field.Key);
                    if (!JsonNode.DeepEquals(currentGroup, composed))
                    {
                        var groupResult = await _settings.SetNodeAsync(Field.Key, composed);
                        errors.AddRange(groupResult.Errors);
                    }

                    break;
                default:
                    var (value, hasValue) = ReadControl();
                    if (!hasValue)
                    {
                        break;
                    }

                    var current = await _settings.GetNodeAsync(Field.Key);
                    if (!JsonNode.DeepEquals(current, value))
                    {
                        var result = await _settings.SetNodeAsync(Field.Key, value);
                        errors.AddRange(result.Errors);
                    }

                    break;
            }

            return errors;
        }

        /// <summary>Returns the control value and whether the control holds input.</summary>
        private (JsonNode? Value, bool HasValue) ReadControl()
        {
            switch (Field.Type)
            {
                case PluginSettingsFieldType.Text or PluginSettingsFieldType.Path or PluginSettingsFieldType.Enum:
                    var text = _textBox?.Text ?? (_combo?.SelectedItem as string);
                    if (string.IsNullOrEmpty(text))
                    {
                        // An empty control removes the value.
                        return (null, true);
                    }

                    return (JsonValue.Create(text), true);
                case PluginSettingsFieldType.WholeNumber or PluginSettingsFieldType.Number:
                    if (_numberBox is null || double.IsNaN(_numberBox.Value))
                    {
                        return (null, false);
                    }

                    if (Field.Type == PluginSettingsFieldType.WholeNumber)
                    {
                        return (JsonValue.Create((long)Math.Round(_numberBox.Value)), true);
                    }

                    return (JsonValue.Create(_numberBox.Value), true);
                case PluginSettingsFieldType.Boolean:
                    return _toggle is null ? (null, false) : (JsonValue.Create(_toggle.IsOn), true);
                case PluginSettingsFieldType.List:
                    return ReadListControl();
                default:
                    return (null, false);
            }
        }

        private (JsonNode? Value, bool HasValue) ReadListControl()
        {
            if (_textBox is null)
            {
                return (null, false);
            }

            var list = new JsonArray();
            var lines = _textBox.Text.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0 && lines.Length == 1)
                {
                    continue;
                }

                switch (Field.ItemType)
                {
                    case PluginSettingsFieldType.WholeNumber:
                        list.Add(long.TryParse(
                            line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                            ? whole
                            : line);
                        break;
                    case PluginSettingsFieldType.Number:
                        list.Add(double.TryParse(
                            line, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                            ? number
                            : line);
                        break;
                    case PluginSettingsFieldType.Boolean:
                        list.Add(bool.TryParse(line, out var flag) ? flag : line);
                        break;
                    default:
                        list.Add(line);
                        break;
                }
            }

            return (list, true);
        }

        private static string FormatList(JsonNode? value)
        {
            if (value is not JsonArray list)
            {
                return string.Empty;
            }

            return string.Join(
                Environment.NewLine,
                list.Select(item => item switch
                {
                    null => string.Empty,
                    JsonValue text when text.TryGetValue<string>(out var textValue) => textValue,
                    _ => item.ToJsonString(),
                }));
        }

        private static NumberBox CreateNumberBox(bool integersOnly, JsonNode? value)
        {
            var box = new NumberBox
            {
                SmallChange = integersOnly ? 1 : 0.1,
                LargeChange = integersOnly ? 10 : 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            if (value is JsonValue number && number.TryGetValue<double>(out var numberValue))
            {
                box.Value = numberValue;
            }

            return box;
        }

        /// <summary>Shows one error under this control.</summary>
        private void ShowOwnError(string message)
            => ShowErrors([new PluginSettingsFieldError(Field.Key, "settings.value.invalid", message)]);

        /// <summary>
        /// Routes every error to the row that owns its field, so nested fields
        /// light up under their own control.
        /// </summary>
        public void ShowErrors(IEnumerable<PluginSettingsFieldError> errors)
        {
            foreach (var error in errors)
            {
                if (FindRow(error.Field) is not { } row)
                {
                    continue;
                }

                row._errorText!.Text = error.Message;
                row._errorText.Visibility = Visibility.Visible;
            }
        }

        private PluginSettingsRow? FindRow(string fieldKey)
        {
            if (string.Equals(Field.Key, fieldKey, StringComparison.Ordinal))
            {
                return this;
            }

            foreach (var child in _children)
            {
                if (string.Equals($"{Field.Key}.{child.Field.Key}", fieldKey, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>Hides this row's inline error and every nested row's.</summary>
        public void ClearError()
        {
            if (_errorText is not null)
            {
                _errorText.Visibility = Visibility.Collapsed;
            }

            foreach (var child in _children)
            {
                child.ClearError();
            }
        }

        private StackPanel BuildLayout()
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock
            {
                Text = Field.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (!string.IsNullOrWhiteSpace(Field.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Field.Description,
                    FontSize = 12,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            panel.Children.Add(_control);
            _errorText = new TextBlock
            {
                FontSize = 12,
                Foreground = ResolveBrush("SystemFillColorCriticalBrush"),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(_errorText);
            return panel;
        }

        private static Brush ResolveBrush(string key)
            => Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>Builds the rows for a schema and saves them back to the store.</summary>
    private sealed class PluginSettingsEditor
    {
        private readonly SchemaPluginSettings _settings;
        private readonly List<PluginSettingsRow> _rows = [];

        private PluginSettingsEditor(SchemaPluginSettings settings)
        {
            _settings = settings;
            Root = new StackPanel { Spacing = 16 };
        }

        public StackPanel Root { get; }

        public static async Task<PluginSettingsEditor> LoadAsync(SchemaPluginSettings settings)
        {
            var editor = new PluginSettingsEditor(settings);
            foreach (var field in settings.Schema.Fields)
            {
                var row = await PluginSettingsRow.BuildAsync(settings, field, parentValue: null);
                editor.Root.Children.Add(row.Root);
                editor._rows.Add(row);
                if (field.Type == PluginSettingsFieldType.Secret)
                {
                    await row.RefreshSecretStatusAsync();
                }
            }

            return editor;
        }

        /// <summary>
        /// Writes every changed value. Any field error keeps the dialog open and
        /// shows inline errors, so saving is blocked until the values are valid.
        /// </summary>
        public async Task<bool> SaveAsync()
        {
            foreach (var row in _rows)
            {
                row.ClearError();
            }

            var errors = new List<PluginSettingsFieldError>();
            foreach (var row in _rows)
            {
                errors.AddRange(await row.SaveAsync());
            }

            if (errors.Count == 0)
            {
                errors.AddRange(await _settings.ValidateAsync());
            }

            if (errors.Count > 0)
            {
                foreach (var row in _rows)
                {
                    row.ShowErrors(errors);
                }

                return false;
            }

            return true;
        }
    }
}
