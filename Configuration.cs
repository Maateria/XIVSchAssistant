using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XIVSchAssitant;

[Serializable]
public class ArenaCenter
{
    public float X { get; set; } = 100f;
    public float Z { get; set; } = 100f;
}

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

    // ── Custom arena centers (overrides par territoire) ───────────────────────
    public Dictionary<uint, ArenaCenter> CustomCenters { get; set; } = new();

    // ── Position du bouton "Retour au centre" ─────────────────────────────────
    // NaN = pas encore positionne par l'utilisateur (utilise le centre de l'ecran).
    public float ReturnBtnX { get; set; } = float.NaN;
    public float ReturnBtnY { get; set; } = float.NaN;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => _pluginInterface = pi;
    public void Save() => _pluginInterface?.SavePluginConfig(this);
}
