// SPDX-FileCopyrightText: 2026 Kofeecheks
// SPDX-License-Identifier: LicenseRef-Kofeecheks

using Content.Shared.Damage.Systems;
using Content.Shared.DeadSpace._Soyuz.Botany;
using Content.Shared.Projectiles;
using Robust.Shared.Timing;

namespace Content.Server.DeadSpace._Soyuz.Botany;

/// <summary>
/// DS14-Soyuz: applies damage only while a black rose remains embedded.
/// </summary>
public sealed class SoyuzRoseEmbedDamageSystem : EntitySystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SoyuzRoseEmbedDamageComponent, EmbeddableProjectileComponent>();
        while (query.MoveNext(out var uid, out var rose, out var projectile))
        {
            if (projectile.EmbeddedIntoUid is not { } target || TerminatingOrDeleted(target))
                continue;

            if (_timing.CurTime < rose.NextDamage)
                continue;

            rose.NextDamage = _timing.CurTime + TimeSpan.FromSeconds(rose.Interval);
            _damageable.TryChangeDamage(target, rose.Damage, origin: uid);
        }
    }
}
