namespace HarvestCombo;

/// <summary>The player-configurable mod settings.</summary>
public sealed class ModConfig
{
    /// <summary>The number of seconds without a valid crop harvest before the combo ends.</summary>
    public double ComboTimeoutSeconds { get; set; } = 2.0;
}
