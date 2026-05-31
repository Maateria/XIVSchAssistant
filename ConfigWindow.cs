using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XIVSchAssitant;

public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly Plugin        _plugin;

    // ── Edit state for custom center section ──────────────────────────────────
    private uint  _editTerritoryCache = uint.MaxValue;
    private float _editX = 100f;
    private float _editZ = 100f;

    public ConfigWindow(Configuration config, Plugin plugin)
        : base("XIVSchAssistant — Settings##xivschcfg")
    {
        _config = config;
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 380),
            MaximumSize = new Vector2(620, 750),
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

        ImGui.Spacing();

        // ── Custom Arena Centers ──────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Custom Arena Centers", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var territory = _plugin.CurrentTerritoryType;

            // Sync edit fields when territory changes
            if (territory != _editTerritoryCache)
            {
                _editTerritoryCache = territory;
                if (_config.CustomCenters.TryGetValue(territory, out var saved))
                { _editX = saved.X; _editZ = saved.Z; }
                else
                { _editX = 100f; _editZ = 100f; }
            }

            if (territory == 0)
            {
                ImGui.TextDisabled("Enter an 8-player raid to configure its arena center.");
            }
            else
            {
                bool hasCustom = _config.CustomCenters.ContainsKey(territory);

                ImGui.Text($"Territory: {territory}  ");
                ImGui.SameLine();
                if (hasCustom)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "(custom center active)");
                else
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "(using default 100, 100)");

                ImGui.Spacing();

                ImGui.SetNextItemWidth(110);
                ImGui.InputFloat("X##cx", ref _editX, 0f, 0f, "%.2f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                ImGui.InputFloat("Z##cz", ref _editZ, 0f, 0f, "%.2f");
                ImGui.SameLine();
                if (ImGui.Button("Save##savecenter"))
                {
                    _config.CustomCenters[territory] = new ArenaCenter { X = _editX, Z = _editZ };
                    _config.Save();
                }
                if (hasCustom)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Reset##resetcenter"))
                    {
                        _config.CustomCenters.Remove(territory);
                        _editX = 100f;
                        _editZ = 100f;
                        _config.Save();
                    }
                    Tooltip("Remove custom center and revert to default (100, 100).");
                }

                ImGui.Spacing();

                if (ImGui.Button("Use my position"))
                {
                    var pos = _plugin.GetPlayerFlatPosition();
                    if (pos.HasValue) { _editX = pos.Value.X; _editZ = pos.Value.Z; }
                }
                Tooltip("Fill X/Z fields with your current in-game position.");
                ImGui.SameLine();
                if (ImGui.Button("Use Eos position"))
                {
                    var pos = _plugin.GetEosFlatPosition();
                    if (pos.HasValue) { _editX = pos.Value.X; _editZ = pos.Value.Z; }
                }
                Tooltip("Fill X/Z fields with Eos's current position.");
            }

            // ── Saved centers list ────────────────────────────────────────────
            if (_config.CustomCenters.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextDisabled("Saved custom centers:");
                uint? toDelete = null;
                foreach (var (tid, center) in _config.CustomCenters)
                {
                    ImGui.Text($"  Territory {tid}:  X = {center.X:F2}   Z = {center.Z:F2}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##{tid}"))
                        toDelete = tid;
                }
                if (toDelete.HasValue)
                { _config.CustomCenters.Remove(toDelete.Value); _config.Save(); }
            }
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
