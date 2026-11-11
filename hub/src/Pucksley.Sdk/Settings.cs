namespace Pucksley.Sdk;

/// <summary>
/// Puts a settings property on the plugin's form in the hub's web page.
/// </summary>
/// <remarks>
/// Supported property types: <c>string</c>, <c>int</c>, <c>double</c>, <c>bool</c>, enums (a dropdown),
/// <c>List&lt;string&gt;</c> (one per line), and <c>List&lt;T&gt;</c> of a class whose properties also carry
/// <see cref="SettingAttribute"/> (a table you can add rows to — devices, actions…). Properties
/// without the attribute are saved but not shown (use them for tokens the plugin stores itself).
/// Secrets are encrypted at rest and never sent back to the browser.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    /// <summary>A line of help under the field.</summary>
    public string? Help { get; init; }

    /// <summary>Stored encrypted; shown as a password field.</summary>
    public bool Secret { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>A text area instead of a one-line box.</summary>
    public bool Multiline { get; init; }

    /// <summary>Fixed choices for a string (a dropdown).</summary>
    public string[]? Options { get; init; }

    /// <summary>
    /// Shows the <see cref="Options"/> as pictures to click instead of a dropdown. The page loads each
    /// one from <c>/plugins/{id}/pictures/{key}/{option}</c>, which the plugin answers in
    /// <see cref="IPlugin.HandleHttpAsync"/> with <see cref="PluginHttpResponse.Bytes"/>.
    /// </summary>
    public bool Pictures { get; init; }

    /// <summary>Sort order on the form (then declaration order).</summary>
    public int Order { get; init; }

    /// <summary>Folded away under "Advanced".</summary>
    public bool Advanced { get; init; }
}

/// <summary>
/// A secret the plugin keeps in its settings without showing it on the form (an OAuth refresh
/// token, a pairing key): encrypted at rest like <c>[Setting(Secret = true)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class SecretAttribute : Attribute { }
