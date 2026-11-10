namespace Pucksley.Sdk;

/// <summary>
/// A dial on the puck's Controls face: one control per page, a big value you turn (num), a switch
/// you tap (bool), a button (act) or a list you turn through (sel).
/// </summary>
public sealed record PuckControl
{
    /// <summary>Unique within the plugin (the hub prefixes the plugin id), e.g. <c>kitchen.vol</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What it controls, e.g. "Kitchen". Short: about 14 characters fit.</summary>
    public required string Name { get; init; }

    /// <summary>Small header above the name, e.g. "Sonos" (defaults to the plugin name).</summary>
    public string? Group { get; init; }

    public ControlKind Kind { get; init; } = ControlKind.Number;

    public double Value { get; init; }

    public double Min { get; init; }

    public double Max { get; init; } = 100;

    /// <summary>Change per detent of the dial.</summary>
    public double Step { get; init; } = 1;

    /// <summary>Unit after the number, e.g. "%" or "°C".</summary>
    public string? Unit { get; init; }

    /// <summary>For <see cref="ControlKind.Toggle"/>.</summary>
    public bool On { get; init; }

    /// <summary>For <see cref="ControlKind.Select"/>: the choices (index in <see cref="Value"/>).</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Overrides the displayed value, e.g. "Off".</summary>
    public string? Text { get; init; }
}

public enum ControlKind { Number, Toggle, Action, Select }

/// <summary>What the person did to a control.</summary>
public sealed record ControlInput
{
    /// <summary>New value (Number: the value; Select: the option index).</summary>
    public double? Value { get; init; }

    /// <summary>New state (Toggle).</summary>
    public bool? On { get; init; }

    /// <summary>The button was pressed (Action).</summary>
    public bool Pressed { get; init; }
}
