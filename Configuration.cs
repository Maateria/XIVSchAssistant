using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XIVSchAssitant;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // ── Eos ──────────────────────────────────────────────────────────────────
    public bool AutoSummonEnabled { get; set; } = true;
    public bool AutoPlaceEnabled  { get; set; } = true;

    // ── Alerte DoT Biolysis ───────────────────────────────────────────────────
    public bool  DotAlertEnabled { get; set; } = true;
    public float DotAlertVolume  { get; set; } = 1.0f; // 0.0 → 1.0

    // ── Chain Stratagem — Compte a rebours ────────────────────────────────────
    public bool CountdownOverlayEnabled { get; set; } = true;
    public bool CountdownSoundEnabled   { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => _pluginInterface = pi;
    public void Save() => _pluginInterface?.SavePluginConfig(this);
}
