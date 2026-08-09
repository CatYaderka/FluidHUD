using System.Text.Json.Serialization;

namespace FluidHUD.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OverlayPlacement>))]
public enum OverlayPlacement
{
    BottomCenter,
    TopCenter
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 4;
    public bool OnboardingCompleted { get; set; }
    public bool StartWithWindows { get; set; }
    public HotkeyGesture Hotkey { get; set; } = new();
    public double DisplayDurationSeconds { get; set; } = 4;
    public double GlassOpacity { get; set; } = 0.82;
    public double AppearanceAnimationSpeed { get; set; } = 1.0;
    public OverlayPlacement Placement { get; set; } = OverlayPlacement.BottomCenter;
    public int ScreenMargin { get; set; } = 28;

    public void Normalize()
    {
        SchemaVersion = 4;
        Hotkey ??= new HotkeyGesture();
        DisplayDurationSeconds = Math.Clamp(DisplayDurationSeconds, 2, 12);
        GlassOpacity = Math.Clamp(GlassOpacity, 0.20, 1.00);
        AppearanceAnimationSpeed = Math.Clamp(AppearanceAnimationSpeed, 0.5, 2.0);
        ScreenMargin = Math.Clamp(ScreenMargin, 8, 96);
    }

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        OnboardingCompleted = OnboardingCompleted,
        StartWithWindows = StartWithWindows,
        Hotkey = Hotkey with { },
        DisplayDurationSeconds = DisplayDurationSeconds,
        GlassOpacity = GlassOpacity,
        AppearanceAnimationSpeed = AppearanceAnimationSpeed,
        Placement = Placement,
        ScreenMargin = ScreenMargin
    };
}
