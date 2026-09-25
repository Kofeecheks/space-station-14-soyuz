// SPDX-FileCopyrightText: 2026 Kofeecheks
// SPDX-License-Identifier: LicenseRef-Kofeecheks

using Content.Shared.Damage;

namespace Content.Shared.DeadSpace._Soyuz.Botany;

/// <summary>
/// DS14-Soyuz: a thorned rose continues to hurt while embedded.
/// </summary>
[RegisterComponent]
public sealed partial class SoyuzRoseEmbedDamageComponent : Component
{
    [DataField(required: true)]
    public DamageSpecifier Damage = default!;

    [DataField]
    public float Interval = 1f;

    public TimeSpan NextDamage;
}
