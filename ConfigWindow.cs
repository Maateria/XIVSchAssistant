using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XIVSchAssitant;

public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly Plugin        _plugin;

    public ConfigWindow(Configuration config, Plugin plugin)
        : base("XIVSchAssistant — Settings##xivschcfg")
    {
        _config = config;
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 310),
            MaximumSize = new Vector2(600, 600),
        };
    }

    public override void Draw()
    {
        // ── Eos ──────────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Eos", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var autoSummon = _config.AutoSummonEnabled;
            if (ImGui.Checkbox("Auto-summon", ref autoSummon))
            { _config.AutoSummonEnabled = autoSummon; _config.Save(); }
            Tooltip("Automatically summons Eos on login, instance entry, and after a full wipe.");

            var autoPlace = _config.AutoPlaceEnabled;
            if (ImGui.Checkbox("Auto-place at center", ref autoPlace))
            { _config.AutoPlaceEnabled = autoPlace; _config.Save(); }
            Tooltip("Repositions Eos to the arena center in 8-player raids. Triggers automatically when you target the boss.");
        }

        ImGui.Spacing();

        // ── DoT Alert ────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("DoT Alert — Biolysis", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var dotEnabled = _config.DotAlertEnabled;
            if (ImGui.Checkbox("Play alert sound when Biolysis falls off", ref dotEnabled))
            { _config.DotAlertEnabled = dotEnabled; _config.Save(); }

            if (_config.DotAlertEnabled)
            {
                ImGui.SetNextItemWidth(200);
                var vol = _config.DotAlertVolume * 100f;
                if (ImGui.SliderFloat("Volume##dot", ref vol, 0f, 100f, "%.0f%%"))
                { _config.DotAlertVolume = Math.Clamp(vol / 100f, 0f, 1f); _config.Save(); }
                ImGui.SameLine();
                if (ImGui.Button("Test##dot"))
                    _plugin.TestDotSound();
            }
        }

        ImGui.Spacing();

        // ── Chain Stratagem ───────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Chain Stratagem — Countdown", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var overlay = _config.CountdownOverlayEnabled;
            if (ImGui.Checkbox("Show visual countdown", ref overlay))
            { _config.CountdownOverlayEnabled = overlay; _config.Save(); }

            var sound = _config.CountdownSoundEnabled;
            if (ImGui.Checkbox("Play sound each second", ref sound))
            { _config.CountdownSoundEnabled = sound; _config.Save(); }

            ImGui.Spacing();
            if (ImGui.Button("Test countdown (5s)"))
                _plugin.TestCountdown();
        }
    }

    private static void Tooltip(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
