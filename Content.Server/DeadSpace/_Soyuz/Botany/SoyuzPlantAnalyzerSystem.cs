using Content.Server.Botany;
using Content.Server.Botany.Components;
using Content.Server.Botany.Systems;
using Content.Shared.PowerCell;
using Content.Shared.Atmos;
using Content.Shared.DeadSpace._Soyuz.Botany;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.Server.DeadSpace._Soyuz.Botany;

public sealed class SoyuzPlantAnalyzerSystem : EntitySystem
{
    [Dependency] private readonly BotanySystem _botany = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly PowerCellSystem _power = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SoyuzPlantAnalyzerComponent, AfterInteractEvent>(OnInteract);
        SubscribeLocalEvent<SoyuzPlantAnalyzerComponent, SoyuzPlantAnalyzerDoAfterEvent>(OnScanFinished);
        SubscribeLocalEvent<SoyuzPlantAnalyzerComponent, SoyuzPlantAnalyzerSetMode>(OnModeChanged);
    }

    private void OnInteract(Entity<SoyuzPlantAnalyzerComponent> analyzer, ref AfterInteractEvent args)
    {
        if (args.Target == null || !args.CanReach || !_power.HasActivatableCharge(analyzer.Owner, user: args.User))
            return;

        var target = args.Target.Value;
        var valid = TryGetSeed(target, out _);
        if (!valid)
            return;

        _doAfter.Cancel(analyzer.Comp.CurrentScan);
        var delay = analyzer.Comp.AdvancedScan ? analyzer.Comp.AdvancedScanDelay : analyzer.Comp.ScanDelay;
        _doAfter.TryStartDoAfter(new DoAfterArgs(
            EntityManager,
            args.User,
            delay,
            new SoyuzPlantAnalyzerDoAfterEvent(),
            analyzer,
            target: target,
            used: analyzer)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
        }, out analyzer.Comp.CurrentScan);
    }

    private void OnScanFinished(Entity<SoyuzPlantAnalyzerComponent> analyzer, ref SoyuzPlantAnalyzerDoAfterEvent args)
    {
        analyzer.Comp.CurrentScan = null;
        if (args.Cancelled || args.Handled || args.Args.Target == null)
            return;

        if (analyzer.Comp.AdvancedScan && !_power.TryUseActivatableCharge(analyzer.Owner, user: args.Args.User))
            return;
        if (!_power.TryUseActivatableCharge(analyzer.Owner, user: args.Args.User))
            return;

        if (!TryGetSeed(args.Args.Target.Value, out var seed, out var isPlant))
            return;

        if (!_ui.HasUi(analyzer, SoyuzPlantAnalyzerUiKey.Key))
            return;

        if (TryComp<ActorComponent>(args.Args.User, out var actor))
            _ui.OpenUi(analyzer.Owner, SoyuzPlantAnalyzerUiKey.Key, args.Args.User);

        _ui.ServerSendUiMessage(analyzer.Owner, SoyuzPlantAnalyzerUiKey.Key, BuildReport(seed, args.Args.Target.Value, isPlant, analyzer.Comp.AdvancedScan));
        args.Handled = true;
    }

    private void OnModeChanged(Entity<SoyuzPlantAnalyzerComponent> analyzer, ref SoyuzPlantAnalyzerSetMode args)
    {
        analyzer.Comp.AdvancedScan = args.Advanced;
    }

    private bool TryGetSeed(EntityUid target, out SeedData seed)
    {
        return TryGetSeed(target, out seed, out _);
    }

    private bool TryGetSeed(EntityUid target, out SeedData seed, out bool isPlant)
    {
        isPlant = false;
        seed = null!;
        if (TryComp<SeedComponent>(target, out var seedComponent))
        {
            if (_botany.TryGetSeed(seedComponent, out var packetSeed))
            {
                seed = packetSeed;
                return true;
            }
        }

        if (TryComp<PlantHolderComponent>(target, out var holder) && holder.Seed != null)
        {
            seed = holder.Seed;
            isPlant = true;
            return true;
        }

        return false;
    }

    private SoyuzPlantAnalyzerReport BuildReport(SeedData seed, EntityUid target, bool isPlant, bool advanced)
    {
        return new SoyuzPlantAnalyzerReport
        {
            Target = GetNetEntity(target),
            IsPlant = isPlant,
            SeedNameKey = seed.DisplayName,
            Chemicals = seed.Chemicals.Keys.Select(ReagentKey).ToArray(),
            ConsumedGases = seed.ConsumeGasses.Keys.Select(GasKey).ToArray(),
            EmittedGases = seed.ExudeGasses.Keys.Select(GasKey).ToArray(),
            PossibleMutations = seed.MutationPrototypes
                .Where(id => _prototypes.TryIndex(id, out SeedPrototype? _))
                .Select(id => _prototypes.Index(id).DisplayName)
                .ToArray(),
            HarvestType = seed.HarvestRepeat.ToString(),
            Endurance = seed.Endurance,
            Yield = seed.Yield,
            Potency = seed.Potency,
            Lifespan = seed.Lifespan,
            Maturation = seed.Maturation,
            Production = seed.Production,
            GrowthStages = seed.GrowthStages,
            Advanced = advanced,
            NutrientConsumption = seed.NutrientConsumption,
            WaterConsumption = seed.WaterConsumption,
            IdealHeat = seed.IdealHeat,
            HeatTolerance = seed.HeatTolerance,
            IdealLight = seed.IdealLight,
            LightTolerance = seed.LightTolerance,
            ToxinsTolerance = seed.ToxinsTolerance,
            LowPressureTolerance = seed.LowPressureTolerance,
            HighPressureTolerance = seed.HighPressureTolerance,
            PestTolerance = seed.PestTolerance,
            WeedTolerance = seed.WeedTolerance,
            Seedless = seed.Seedless || seed.PermanentlySeedless,
            Ligneous = seed.Ligneous,
            CanScream = seed.CanScream,
        };
    }

    private static string ReagentKey(string reagent)
    {
        return $"reagent-name-{ToKebab(reagent)}";
    }

    private static string ToKebab(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current) && i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                result.Append('-');
            result.Append(char.ToLowerInvariant(current));
        }
        return result.ToString();
    }

    private static string GasKey(Gas gas)
    {
        return gas switch
        {
            Gas.Oxygen => "gases-oxygen",
            Gas.Nitrogen => "gases-nitrogen",
            Gas.CarbonDioxide => "gases-co2",
            Gas.Plasma => "gases-plasma",
            Gas.Tritium => "gases-tritium",
            Gas.WaterVapor => "gases-water-vapor",
            Gas.Ammonia => "gases-ammonia",
            Gas.NitrousOxide => "gases-n2o",
            Gas.Frezon => "gases-frezon",
            _ => gas.ToString(),
        };
    }
}
