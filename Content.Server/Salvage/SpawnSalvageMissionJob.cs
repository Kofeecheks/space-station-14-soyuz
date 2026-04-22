using System.Collections;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Robust.Shared.CPUJob.JobQueues;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Atmos;
using Content.Shared.Construction.EntitySystems;
using Content.Shared.Dataset;
using Content.Shared.Gravity;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Physics;
using Content.Shared.Procedural;
using Content.Shared.Procedural.Loot;
using Content.Shared.Random;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Content.Shared.Shuttles.Components;
using Content.Shared.Storage;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Collections;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Server.Shuttles.Components;

namespace Content.Server.Salvage;

public sealed class SpawnSalvageMissionJob : Job<bool>
{
    private readonly IEntityManager _entManager;
    private readonly IGameTiming _timing;
    private readonly IPrototypeManager _prototypeManager;
    private readonly IResourceManager _resourceManager;
    private readonly AnchorableSystem _anchorable;
    private readonly BiomeSystem _biome;
    private readonly DungeonSystem _dungeon;
    private readonly MapLoaderSystem _loader;
    private readonly MetaDataSystem _metaData;
    private readonly SharedMapSystem _map;

    public readonly EntityUid Station;
    public readonly EntityUid? CoordinatesDisk;
    private readonly SalvageMissionParams _missionParams;

    private readonly ISawmill _sawmill;

    private const string ModerateDifficultyId = "Moderate";
    private const string OutpostDifficultyId = "Outpost";
    private static readonly ResPath ExpeditionTemplateDirectory = new("/Maps/_Soyuz/Expeditions/");
    private const byte BiomeChunkSize = 8; // Matches SharedBiomeSystem chunk size.

    public SpawnSalvageMissionJob(
        double maxTime,
        IEntityManager entManager,
        IGameTiming timing,
        ILogManager logManager,
        IPrototypeManager protoManager,
        IResourceManager resourceManager,
        AnchorableSystem anchorable,
        BiomeSystem biome,
        DungeonSystem dungeon,
        MapLoaderSystem loader,
        MetaDataSystem metaData,
        SharedMapSystem map,
        EntityUid station,
        EntityUid? coordinatesDisk,
        SalvageMissionParams missionParams,
        CancellationToken cancellation = default) : base(maxTime, cancellation)
    {
        _entManager = entManager;
        _timing = timing;
        _prototypeManager = protoManager;
        _resourceManager = resourceManager;
        _anchorable = anchorable;
        _biome = biome;
        _dungeon = dungeon;
        _loader = loader;
        _metaData = metaData;
        _map = map;
        Station = station;
        CoordinatesDisk = coordinatesDisk;
        _missionParams = missionParams;
        _sawmill = logManager.GetSawmill("salvage_job");
#if !DEBUG
        _sawmill.Level = LogLevel.Info;
#endif
    }

    protected override async Task<bool> Process()
    {
        _sawmill.Debug("salvage", $"Spawning salvage mission with seed {_missionParams.Seed}");
        var mapUid = _map.CreateMap(out var mapId, runMapInit: false);
        MetaDataComponent? metadata = null;
        var random = new Random(_missionParams.Seed);

        var difficultyId = _missionParams.Difficulty;
        if (string.IsNullOrWhiteSpace(difficultyId) ||
            !_prototypeManager.TryIndex<SalvageDifficultyPrototype>(difficultyId, out var difficultyProto))
        {
            difficultyId = ModerateDifficultyId;
            difficultyProto = _prototypeManager.Index<SalvageDifficultyPrototype>(difficultyId);
        }

        var templatePath = GetExpeditionTemplate(random, difficultyId);
        var hasTemplateGrid = false;
        var mainGridUid = mapUid;
        MapGridComponent grid;

        if (templatePath != null && _loader.TryLoadGrid(mapId, templatePath.Value, out var loadedGrid))
        {
            mainGridUid = loadedGrid.Value;
            grid = _entManager.GetComponent<MapGridComponent>(mainGridUid);
            hasTemplateGrid = true;
            _sawmill.Info($"Loaded expedition template {templatePath.Value}");
        }
        else
        {
            if (templatePath != null)
                _sawmill.Error($"Failed to load expedition template {templatePath.Value}, falling back to procedural grid");

            grid = _entManager.EnsureComponent<MapGridComponent>(mapUid);
        }

        var destComp = _entManager.AddComponent<FTLDestinationComponent>(mapUid);
        destComp.BeaconsOnly = true;
        destComp.RequireCoordinateDisk = true;
        destComp.Enabled = true;
        _metaData.SetEntityName(
            mapUid,
            _entManager.System<SharedSalvageSystem>().GetFTLName(_prototypeManager.Index(SalvageSystem.PlanetNames), _missionParams.Seed));

        var mapName = _entManager.GetComponent<MetaDataComponent>(mapUid).EntityName;
        var beaconCoordinates = GetMissionBeaconCoordinates(mapUid, grid, hasTemplateGrid);
        var beaconUid = _entManager.SpawnEntity(null, beaconCoordinates);
        _metaData.SetEntityName(beaconUid, mapName);
        _entManager.AddComponent<FTLBeaconComponent>(beaconUid);

        // Saving the mission mapUid to a CD is made optional, in case one is somehow made in a process without a CD entity
        if (CoordinatesDisk.HasValue)
        {
            var cd = _entManager.EnsureComponent<ShuttleDestinationCoordinatesComponent>(CoordinatesDisk.Value);
            cd.Destination = mapUid;
            _entManager.Dirty(CoordinatesDisk.Value, cd);
        }

        // Setup mission configs
        // As we go through the config the rating will deplete so we'll go for most important to least important.
        var mission = _entManager.System<SharedSalvageSystem>()
            .GetMission(difficultyProto, _missionParams.Seed);

        var missionBiome = _prototypeManager.Index<SalvageBiomeModPrototype>(mission.Biome);

        BiomeComponent? biome = null;
        if (missionBiome.BiomePrototype != null)
        {
            biome = _entManager.AddComponent<BiomeComponent>(mainGridUid);
            var biomeSystem = _entManager.System<BiomeSystem>();
            biomeSystem.SetTemplate(mainGridUid, biome, _prototypeManager.Index<BiomeTemplatePrototype>(missionBiome.BiomePrototype));
            biomeSystem.SetSeed(mainGridUid, biome, mission.Seed);

            if (templatePath != null)
                ReserveTemplateTiles(mainGridUid, grid, biome);

            _entManager.Dirty(mainGridUid, biome);
        }

        // Gravity
        var gravity = _entManager.EnsureComponent<GravityComponent>(mapUid);
        gravity.Enabled = true;
        _entManager.Dirty(mapUid, gravity, metadata);

        // Atmos
        var air = _prototypeManager.Index<SalvageAirMod>(mission.Air);
        // copy into a new array since the yml deserialization discards the fixed length
        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        air.Gases.CopyTo(moles, 0);
        var atmos = _entManager.EnsureComponent<MapAtmosphereComponent>(mapUid);
        _entManager.System<AtmosphereSystem>().SetMapSpace(mapUid, air.Space, atmos);
        _entManager.System<AtmosphereSystem>().SetMapGasMixture(mapUid, new GasMixture(moles, mission.Temperature), atmos);

        if (mission.Color != null)
        {
            var lighting = _entManager.EnsureComponent<MapLightComponent>(mapUid);
            lighting.AmbientLightColor = mission.Color.Value;
            _entManager.Dirty(mapUid, lighting);
        }

        _map.InitializeMap(mapId);
        _map.SetPaused(mapUid, true);

        // Setup expedition
        var expedition = _entManager.AddComponent<SalvageExpeditionComponent>(mapUid);
        expedition.Station = Station;
        expedition.EndTime = _timing.CurTime + mission.Duration;
        expedition.MissionParams = _missionParams;

        var landingPadRadius = 24;
        var minDungeonOffset = landingPadRadius + 4;

        // We'll use the dungeon rotation as the spawn angle
        var dungeonRotation = _dungeon.GetDungeonRotation(_missionParams.Seed);

        var maxDungeonOffset = minDungeonOffset + 12;
        var dungeonOffsetDistance = minDungeonOffset + (maxDungeonOffset - minDungeonOffset) * random.NextFloat();
        var dungeonOffset = new Vector2(0f, dungeonOffsetDistance);
        dungeonOffset = dungeonRotation.RotateVec(dungeonOffset);
        var dungeonMod = _prototypeManager.Index<SalvageDungeonModPrototype>(mission.Dungeon);
        var dungeonConfig = _prototypeManager.Index(dungeonMod.Proto);
        var dungeons = await WaitAsyncTask(_dungeon.GenerateDungeonAsync(dungeonConfig, mainGridUid, grid, (Vector2i)dungeonOffset,
            _missionParams.Seed));

        var dungeon = dungeons.First();

        // Aborty
        if (dungeon.Rooms.Count == 0)
        {
            return false;
        }

        expedition.DungeonLocation = dungeonOffset;

        List<Vector2i> reservedTiles = new();

        foreach (var tile in _map.GetTilesIntersecting(mainGridUid, grid, new Circle(Vector2.Zero, landingPadRadius), false))
        {
            if (!_biome.TryGetBiomeTile(mainGridUid, grid, tile.GridIndices, out _))
                continue;

            reservedTiles.Add(tile.GridIndices);
        }

        var budgetEntries = new List<IBudgetEntry>();

        /*
         * GUARANTEED LOOT
         */

        // We'll always add this loot if possible
        // mainly used for ore layers.
        foreach (var lootProto in _prototypeManager.EnumeratePrototypes<SalvageLootPrototype>())
        {
            if (!lootProto.Guaranteed)
                continue;

            try
            {
                await SpawnDungeonLoot(lootProto, mainGridUid);
            }
            catch (Exception e)
            {
                _sawmill.Error($"Failed to spawn guaranteed loot {lootProto.ID}: {e}");
            }
        }

        // Handle boss loot (when relevant).

        // Handle mob loot.

        // Handle remaining loot

        /*
         * MOB SPAWNS
         */

        var mobBudget = difficultyProto.MobBudget;
        var faction = _prototypeManager.Index<SalvageFactionPrototype>(mission.Faction);
        var randomSystem = _entManager.System<RandomSystem>();

        foreach (var entry in faction.MobGroups)
        {
            budgetEntries.Add(entry);
        }

        var probSum = budgetEntries.Sum(x => x.Prob);

        while (mobBudget > 0f)
        {
            var entry = randomSystem.GetBudgetEntry(ref mobBudget, ref probSum, budgetEntries, random);
            if (entry == null)
                break;

            try
            {
                await SpawnRandomEntry((mainGridUid, grid), entry, dungeon, random);
            }
            catch (Exception e)
            {
                _sawmill.Error($"Failed to spawn mobs for {entry.Proto}: {e}");
            }
        }

        var allLoot = _prototypeManager.Index(SharedSalvageSystem.ExpeditionsLootProto);
        var lootBudget = difficultyProto.LootBudget;

        foreach (var rule in allLoot.LootRules)
        {
            switch (rule)
            {
                case RandomSpawnsLoot randomLoot:
                    budgetEntries.Clear();

                    foreach (var entry in randomLoot.Entries)
                    {
                        budgetEntries.Add(entry);
                    }

                    probSum = budgetEntries.Sum(x => x.Prob);

                    while (lootBudget > 0f)
                    {
                        var entry = randomSystem.GetBudgetEntry(ref lootBudget, ref probSum, budgetEntries, random);
                        if (entry == null)
                            break;

                        _sawmill.Debug($"Spawning dungeon loot {entry.Proto}");
                        await SpawnRandomEntry((mainGridUid, grid), entry, dungeon, random);
                    }
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        return true;
    }

    private ResPath? GetExpeditionTemplate(Random random, string difficultyId)
    {
        if (!string.Equals(difficultyId, OutpostDifficultyId, StringComparison.Ordinal))
            return null;

        var templates = _resourceManager.ContentFindFiles(ExpeditionTemplateDirectory)
            .Where(path => path.Extension is "yml" or "yaml")
            .OrderBy(path => path.CanonPath, StringComparer.Ordinal)
            .ToList();

        if (templates.Count == 0)
            return null;

        return templates[random.Next(templates.Count)];
    }

    private EntityCoordinates GetMissionBeaconCoordinates(EntityUid mapUid, MapGridComponent grid, bool hasTemplateGrid)
    {
        if (!hasTemplateGrid)
            return new EntityCoordinates(mapUid, Vector2.Zero);

        // Template maps can have their main grid around 0,0; place beacon just outside the grid so FTLFree can pass
        // without sending the shuttle hundreds of meters away from the outpost.
        const float beaconOffset = 48f;
        var aabb = grid.LocalAABB;
        var position = new Vector2(aabb.Right + beaconOffset, aabb.Center.Y);
        return new EntityCoordinates(mapUid, position);
    }

    private void ReserveTemplateTiles(EntityUid gridUid, MapGridComponent grid, BiomeComponent biome)
    {
        foreach (var tile in _map.GetAllTiles(gridUid, grid))
        {
            if (tile.Tile.IsEmpty)
                continue;

            var chunkOrigin = SharedMapSystem.GetChunkIndices(tile.GridIndices, BiomeChunkSize) * BiomeChunkSize;
            var modified = biome.ModifiedTiles.GetOrNew(chunkOrigin);
            modified.Add(tile.GridIndices);
        }
    }

    private async Task SpawnRandomEntry(Entity<MapGridComponent> grid, IBudgetEntry entry, Dungeon dungeon, Random random)
    {
        await SuspendIfOutOfTime();

        var availableRooms = new ValueList<DungeonRoom>(dungeon.Rooms);
        var availableTiles = new List<Vector2i>();

        while (availableRooms.Count > 0)
        {
            availableTiles.Clear();
            var roomIndex = random.Next(availableRooms.Count);
            var room = availableRooms.RemoveSwap(roomIndex);
            availableTiles.AddRange(room.Tiles);

            while (availableTiles.Count > 0)
            {
                var tile = availableTiles.RemoveSwap(random.Next(availableTiles.Count));

                if (!_anchorable.TileFree(grid, tile, (int)CollisionGroup.MachineLayer,
                        (int)CollisionGroup.MachineLayer))
                {
                    continue;
                }

                var uid = _entManager.SpawnAtPosition(entry.Proto, _map.GridTileToLocal(grid, grid, tile));
                _entManager.RemoveComponent<GhostRoleComponent>(uid);
                _entManager.RemoveComponent<GhostTakeoverAvailableComponent>(uid);
                return;
            }
        }

        // oh noooooooooooo
    }

    private async Task SpawnDungeonLoot(SalvageLootPrototype loot, EntityUid gridUid)
    {
        for (var i = 0; i < loot.LootRules.Count; i++)
        {
            var rule = loot.LootRules[i];

            switch (rule)
            {
                case BiomeMarkerLoot biomeLoot:
                    {
                        if (_entManager.TryGetComponent<BiomeComponent>(gridUid, out var biome))
                        {
                            _biome.AddMarkerLayer(gridUid, biome, biomeLoot.Prototype);
                        }
                    }
                    break;
                case BiomeTemplateLoot biomeLoot:
                    {
                        if (_entManager.TryGetComponent<BiomeComponent>(gridUid, out var biome))
                        {
                            _biome.AddTemplate(gridUid, biome, "Loot", _prototypeManager.Index<BiomeTemplatePrototype>(biomeLoot.Prototype), i);
                        }
                    }
                    break;
            }
        }
    }
}
