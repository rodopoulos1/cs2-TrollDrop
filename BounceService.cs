using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace TrollDrop
{
    // The three possible troll effects that can be assigned to a drop zone
    internal enum TrollEffect { Bounce, Spin, Swap }

    // Represents a single drop zone. ONE effect is rolled at creation time from the
    // configured weight pool — the player never knows what's waiting for them.
    internal class DropZone
    {
        public int         EntityIndex      { get; }
        public TrollEffect AssignedEffect   { get; }  // which troll fires when triggered
        public DateTime    ArmsAt           { get; }  // when the zone becomes active (after arm_delay)
        public DateTime    ExpiresAt        { get; }  // when the zone automatically despawns
        public DateTime    LastBounced      { get; private set; } = DateTime.MinValue;
        // Pawn index of who dropped this weapon — zone won't trigger until they leave the radius
        public int         DropperPawnIndex { get; }
        public bool        DropperLeft      { get; private set; } = false;

        public DropZone(int entityIndex, int dropperPawnIndex, TrollConfig cfg)
        {
            EntityIndex      = entityIndex;
            DropperPawnIndex = dropperPawnIndex;
            AssignedEffect   = RolarEfeito(cfg.ProximityEvents);
            ArmsAt           = DateTime.UtcNow.AddSeconds(cfg.ArmDelay);
            ExpiresAt        = DateTime.UtcNow.AddSeconds(cfg.ArmDelay + cfg.ZoneLifetime);
        }

        public bool IsArmed   => DateTime.UtcNow >= ArmsAt;
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        public bool PodeBouncear(float cooldown) =>
            (DateTime.UtcNow - LastBounced).TotalSeconds >= cooldown;

        public void MarcarBounce()      => LastBounced  = DateTime.UtcNow;
        public void MarcarDropperSaiu() => DropperLeft  = true;

        // Weighted random pick from the proximity_events pool.
        // Only "bounce", "spin", "swap" are valid keys — anything else is silently ignored
        // so stray TOML fields can never pollute the roll.
        private static TrollEffect RolarEfeito(Dictionary<string, int> pool)
        {
            // Filter to known keys only
            var known = new[] { "bounce", "spin", "swap" };

            int total = 0;
            foreach (var k in known)
                if (pool.TryGetValue(k, out var w) && w > 0) total += w;

            if (total <= 0) return TrollEffect.Bounce;

            int roll = Random.Shared.Next(total);
            int acc  = 0;
            foreach (var k in known)
            {
                if (!pool.TryGetValue(k, out var w) || w <= 0) continue;
                acc += w;
                if (roll < acc)
                    return k switch
                    {
                        "spin" => TrollEffect.Spin,
                        "swap" => TrollEffect.Swap,
                        _      => TrollEffect.Bounce
                    };
            }
            return TrollEffect.Bounce;
        }
    }

    internal static class BounceService
    {
        private static readonly List<DropZone> _zones      = new();
        // Entity indexes of weapons rigged for a swap-on-pickup
        private static readonly HashSet<int>   _swapRigged = new();

        public static bool EhSwapRigged(int entityIndex) => _swapRigged.Contains(entityIndex);
        public static void RemoverSwap(int entityIndex)   => _swapRigged.Remove(entityIndex);

        // Called from NextFrame after a weapon drop — rolls the effect and registers the zone
        public static void AdicionarZona(int entityIndex, int dropperPawnIndex, TrollConfig cfg)
        {
            if (!cfg.Enabled) return;
            var zone = new DropZone(entityIndex, dropperPawnIndex, cfg);
            _zones.Add(zone);
            // Only mark for swap-on-pickup when the rolled effect is Swap
            if (zone.AssignedEffect == TrollEffect.Swap)
                _swapRigged.Add(entityIndex);
        }

        // Called every tick by the repeating timer — checks if any player is inside a bounce zone
        public static void ChecarProximidade(TrollConfig cfg)
        {
            if (!cfg.Enabled || _zones.Count == 0) return;

            // Clean up expired zones — also unrig any swap that never got triggered
            _zones.RemoveAll(z =>
            {
                if (!z.IsExpired) return false;
                if (z.AssignedEffect == TrollEffect.Swap)
                    _swapRigged.Remove(z.EntityIndex);
                return true;
            });
            if (_zones.Count == 0) return;

            var zonesToRemove = new List<DropZone>();
            var players       = Utilities.GetPlayers();

            foreach (var zone in _zones)
            {
                if (!zone.IsArmed) continue;

                var weapon = Utilities.GetEntityFromIndex<CCSWeaponBase>(zone.EntityIndex);

                // Weapon was destroyed or picked up — remove the zone immediately.
                // This check must run BEFORE the dropper-left guard so zones are never
                // left dangling when the weapon is picked up while the dropper is nearby.
                if (weapon == null || !weapon.IsValid || weapon.AbsOrigin == null ||
                    (weapon.OwnerEntity?.Value != null && weapon.OwnerEntity.Value.IsValid))
                {
                    zonesToRemove.Add(zone);
                    continue;
                }

                // Zone is locked until the dropper physically exits the trigger radius
                if (!zone.DropperLeft)
                {
                    var dropper = Utilities.GetEntityFromIndex<CCSPlayerPawn>(zone.DropperPawnIndex);
                    if (dropper != null && dropper.IsValid && dropper.AbsOrigin != null &&
                        Distancia2D(dropper.AbsOrigin, weapon.AbsOrigin) <= cfg.TriggerRadius)
                    {
                        continue; // dropper still inside — stay dormant
                    }
                    zone.MarcarDropperSaiu();
                }

                if (!zone.PodeBouncear(cfg.BounceCooldown)) continue;

                // Find the closest player within the trigger radius
                CCSPlayerPawn? closest   = null;
                float          closestDist = float.MaxValue;

                foreach (var player in players)
                {
                    if (!player.IsValid || !player.PawnIsAlive) continue;
                    var pawn = player.PlayerPawn.Value;
                    if (pawn?.AbsOrigin == null) continue;

                    float dist = Distancia2D(pawn.AbsOrigin, weapon.AbsOrigin);
                    if (dist <= cfg.TriggerRadius && dist < closestDist)
                    {
                        closestDist = dist;
                        closest     = pawn;
                    }
                }

                if (closest != null)
                {
                    switch (zone.AssignedEffect)
                    {
                        case TrollEffect.Bounce:
                            // Weapon flies away — force scales with how close the player is
                            float t     = 1f - (closestDist / cfg.TriggerRadius);
                            float force = cfg.MinBounceForce + (cfg.MaxBounceForce - cfg.MinBounceForce) * t;
                            BounceArma(weapon, closest.AbsOrigin!, force);
                            zone.MarcarBounce();
                            break;

                        case TrollEffect.Spin:
                            // Spin 180° and launch forward — respects cooldown, zone stays active
                            SpinarELancar(closest, cfg);
                            zone.MarcarBounce();
                            break;

                        case TrollEffect.Swap:
                            // Silent on proximity — the swap fires when the weapon is picked up
                            break;
                    }
                }
            }

            foreach (var z in zonesToRemove) _zones.Remove(z);
        }

        // Clears all active zones and swap tracking — called on round start and hot reload
        public static void LimparTudo()
        {
            _zones.Clear();
            _swapRigged.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────

        // Snaps the player's view 180°, then launches them forward in their new facing direction.
        // Mirrors BounceArma: lift +5 Z to wake VPhysics before applying velocity.
        private static void SpinarELancar(CCSPlayerPawn pawn, TrollConfig cfg)
        {
            var angles   = pawn.EyeAngles;
            float newYaw = angles.Y + 180f;

            // Rotate the player to face the opposite direction
            pawn.Teleport(null, new QAngle(angles.X, newYaw, angles.Z), null);

            // Compute forward direction based on the NEW yaw
            float rad = newYaw * (MathF.PI / 180f);
            float nx  = MathF.Cos(rad);
            float ny  = MathF.Sin(rad);

            // Lift +5 Z to wake VPhysics (same trick as BounceArma), then apply velocity
            var pos = pawn.AbsOrigin!;
            pawn.Teleport(
                new Vector(pos.X, pos.Y, pos.Z + 5f),
                null,
                new Vector(nx * cfg.SpinForwardSpeed, ny * cfg.SpinForwardSpeed, cfg.SpinLaunchForce));
        }

        private static float Distancia2D(Vector a, Vector b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        // Launches the weapon away from the player.
        // Direction: directly away from the player (or random if they're standing on top of the weapon).
        // The +5u Z lift wakes the weapon's VPhysics body so the velocity is actually applied.
        private static void BounceArma(CCSWeaponBase weapon, Vector playerPos, float force)
        {
            var pos = weapon.AbsOrigin!;

            // Escape direction = away from the player
            float dx = pos.X - playerPos.X;
            float dy = pos.Y - playerPos.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);

            float nx, ny;
            if (len < 1f)
            {
                // Player is standing directly on the weapon — pick a random direction
                float a = (float)(Random.Shared.NextDouble() * Math.PI * 2);
                nx = MathF.Cos(a);
                ny = MathF.Sin(a);
            }
            else
            {
                nx = dx / len;
                ny = dy / len;
            }

            // Small random horizontal speed variation to keep bounces from looking scripted
            float hspeed = 300f + (float)(Random.Shared.NextDouble() * 150f);

            // Lift +5 units off the ground to wake VPhysics, then apply directional velocity
            weapon.Teleport(
                new Vector(pos.X, pos.Y, pos.Z + 5f),
                null,
                new Vector(nx * hspeed, ny * hspeed, force));
        }
    }
}
