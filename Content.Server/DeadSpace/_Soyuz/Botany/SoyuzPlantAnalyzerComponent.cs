using Content.Shared.DoAfter;
using Robust.Shared.Audio;

namespace Content.Server.DeadSpace._Soyuz.Botany;

[RegisterComponent]
public sealed partial class SoyuzPlantAnalyzerComponent : Component
{
    [DataField]
    public bool AdvancedScan;

    [DataField]
    public float ScanDelay = 0.5f;

    [DataField]
    public float AdvancedScanDelay = 1f;

    [DataField]
    public SoundSpecifier? ScanSound;

    [ViewVariables]
    public DoAfterId? CurrentScan;
}
