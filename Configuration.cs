using Dalamud.Configuration;

namespace XIVSchAssitant;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;

    // Alerte sonore quand Biolysis (Loi de l'infection) tombe du boss.
    public bool DotAlertEnabled { get; set; } = true;

    // Compte a rebours local 5s avant que Chain Stratagem / Emergency Tactics revienne.
    public bool CooldownAlertEnabled { get; set; } = true;
}
