namespace UsageWidget.Core.Config;

/// <summary>
/// Persisted on-screen position of the popup widget. Stored in adapter-config.json as plain UI
/// state (no secrets). Coordinates are in device-independent WPF units; the app clamps them to the
/// current virtual screen on restore so a remembered spot on a now-disconnected monitor can't hide
/// the widget off-screen.
/// </summary>
public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }

    /// <summary>Persisted widget width (0 = use default). Height is content-driven, so not stored.</summary>
    public double Width { get; set; }
}
