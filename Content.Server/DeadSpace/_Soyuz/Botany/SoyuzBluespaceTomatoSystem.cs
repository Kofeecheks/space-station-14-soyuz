// SPDX-FileCopyrightText: 2026 Kofeecheks
// SPDX-License-Identifier: LicenseRef-Kofeecheks

using Content.Server.Tiles;
using Content.Shared.Chasm;
using Content.Shared.DeadSpace._Soyuz.Botany;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition;
using Content.Shared.Physics;
using Content.Shared.Throwing;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;

namespace Content.Server.DeadSpace._Soyuz.Botany;

/// <summary>
/// DS14-Soyuz: a thrown tomato swaps the thrower and victim, then scatters the
/// victim nearby; eating one scatters only the eater.
/// </summary>
public sealed class SoyuzBluespaceTomatoSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    private const float Radius = 5f;
    private const int SearchAttempts = 30;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SoyuzBluespaceTomatoComponent, FullyEatenEvent>(OnEaten);
        SubscribeLocalEvent<SoyuzBluespaceTomatoComponent, ThrowDoHitEvent>(OnHit);
    }

    private void OnEaten(Entity<SoyuzBluespaceTomatoComponent> ent, ref FullyEatenEvent args)
    {
        TryScatter(args.User);
    }

    private void OnHit(Entity<SoyuzBluespaceTomatoComponent> ent, ref ThrowDoHitEvent args)
    {
        if (args.Component.Thrower is not { } thrower ||
            thrower == args.Target ||
            !HasComp<MobStateComponent>(args.Target) ||
            !HasComp<MobStateComponent>(thrower) ||
            TerminatingOrDeleted(thrower) ||
            TerminatingOrDeleted(args.Target))
            return;

        var throwerPosition = Transform(thrower).Coordinates;
        var victimPosition = Transform(args.Target).Coordinates;
        if (_transform.GetMapId(throwerPosition) != _transform.GetMapId(victimPosition))
            return;

        _transform.SetCoordinates(thrower, victimPosition);
        _transform.SetCoordinates(args.Target, throwerPosition);
        TryScatter(args.Target);
        QueueDel(ent.Owner);
    }

    private bool TryScatter(EntityUid subject)
    {
        var xform = Transform(subject);
        if (xform.MapUid is not { } mapUid || !TryComp<PhysicsComponent>(subject, out var physics))
            return false;

        var origin = _transform.GetMapCoordinates(subject);
        for (var i = 0; i < SearchAttempts; i++)
        {
            var offset = _random.NextVector2(Radius);
            if (offset.LengthSquared() < 0.25f)
                continue;

            var candidate = new EntityCoordinates(mapUid, origin.Position + offset);
            if (!_turf.TryGetTileRef(candidate, out var tile) ||
                tile.Value.Tile.IsEmpty ||
                _turf.IsSpace(tile.Value) ||
                _turf.IsTileBlocked(tile.Value, (CollisionGroup) physics.CollisionMask) ||
                HasHazard(tile.Value))
                continue;

            _transform.SetCoordinates(subject, _map.ToCenterCoordinates(tile.Value));
            return true;
        }

        return false;
    }

    private bool HasHazard(TileRef tile)
    {
        if (!TryComp<MapGridComponent>(tile.GridUid, out var grid))
            return true;

        var anchored = _map.GetAnchoredEntitiesEnumerator(tile.GridUid, grid, tile.GridIndices);
        while (anchored.MoveNext(out var uid))
        {
            if (uid is { } hazard &&
                (HasComp<ChasmComponent>(hazard) || HasComp<TileEntityEffectComponent>(hazard)))
                return true;
        }

        return false;
    }
}
