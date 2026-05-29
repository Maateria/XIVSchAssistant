using Dalamud.Configuration;

namespace XIVSchAssitant;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;

    public float ResurrectionDelaySeconds { get; set; } = 2.5f;
}
