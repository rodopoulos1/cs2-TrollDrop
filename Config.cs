using System.Reflection;
using CounterStrikeSharp.API;
using Tomlyn;
using Tomlyn.Model;

namespace TrollDrop
{
    public class TrollConfig
    {
        // Master switch — set to false to disable the entire plugin without unloading it
        public bool  Enabled         { get; set; } = true;

        // Upward velocity (Z axis) applied to the weapon when a player is at the very edge of the trigger radius.
        // Lower value = softer bounce when the player is far away.
        public float MinBounceForce  { get; set; } = 400f;

        // Upward velocity (Z axis) applied to the weapon when a player is nearly touching it.
        // Higher value = stronger bounce the closer the player gets.
        public float MaxBounceForce  { get; set; } = 700f;

        // Radius (in Hammer units) around the dropped weapon that triggers a bounce.
        // CS2 scale reference: ~65u ≈ 1 meter. 220u ≈ ~3.4 meters.
        public float TriggerRadius   { get; set; } = 220f;

        // Seconds after the weapon is dropped before the bounce zone becomes active.
        // Useful so the dropper has time to walk away before it activates.
        public float ArmDelay        { get; set; } = 1.5f;

        // How many seconds the bounce zone stays alive before automatically disappearing.
        public float ZoneLifetime    { get; set; } = 30f;

        // Minimum time (in seconds) between consecutive bounces of the same weapon.
        // Prevents the weapon from bouncing every single tick when a player stands next to it.
        public float BounceCooldown  { get; set; } = 0.3f;

        // How often (in seconds) the proximity check runs.
        // Lower = more responsive but slightly more CPU usage. 0.05 = 20 checks per second.
        public float CheckInterval   { get; set; } = 0.05f;

        // Weighted pool that decides which ONE troll effect is assigned to each new drop zone.
        // Keys: "bounce" | "spin" | "swap". Values: relative weights (don't need to sum to 100).
        // Set a weight to 0 to completely disable that effect.
        public Dictionary<string, int> ProximityEvents { get; set; } = new()
        {
            ["bounce"] = 34,
            ["spin"]   = 33,
            ["swap"]   = 33
        };

        // Horizontal speed (units/s) applied to the player when the spin effect fires.
        // The player is launched in the direction they're now facing (after the 180° flip).
        public float SpinForwardSpeed { get; set; } = 500f;

        // Upward velocity (Z) applied to the player alongside the forward launch on spin.
        // Gives the effect a jump feel. Set to 0 for a pure horizontal shove.
        public float SpinLaunchForce  { get; set; } = 300f;

        // Weapon pool used when the "swap" effect fires on pickup.
        // Key = CS2 designer name, Value = relative weight.
        public Dictionary<string, int> SwapWeapons { get; set; } = new()
        {
            ["weapon_negev"]  = 50,
            ["weapon_xm1014"] = 50
        };
    }

    public static class ConfigLoader
    {
        private static readonly string NomeAssembly =
            Assembly.GetExecutingAssembly().GetName().Name ?? "TrollDrop";

        private static string CaminhoConfig =>
            $"{Server.GameDirectory}/csgo/addons/counterstrikesharp/configs/plugins/{NomeAssembly}/Config.toml";

        public static TrollConfig Carregar()
        {
            try
            {
                if (!File.Exists(CaminhoConfig))
                {
                    CriarPadrao();
                    return new TrollConfig();
                }

                var model = (TomlTable)Toml.ToModel(File.ReadAllText(CaminhoConfig));
                var cfg   = new TrollConfig();

                if (model.TryGetValue("enabled",          out var v0)) cfg.Enabled        = (bool)v0;
                if (model.TryGetValue("min_bounce_force",  out var v1)) cfg.MinBounceForce = Convert.ToSingle(v1);
                if (model.TryGetValue("max_bounce_force",  out var v2)) cfg.MaxBounceForce = Convert.ToSingle(v2);
                if (model.TryGetValue("trigger_radius",    out var v3)) cfg.TriggerRadius  = Convert.ToSingle(v3);
                if (model.TryGetValue("arm_delay",         out var v4)) cfg.ArmDelay       = Convert.ToSingle(v4);
                if (model.TryGetValue("zone_lifetime",     out var v5)) cfg.ZoneLifetime   = Convert.ToSingle(v5);
                if (model.TryGetValue("bounce_cooldown",   out var v6)) cfg.BounceCooldown = Convert.ToSingle(v6);
                if (model.TryGetValue("check_interval",    out var v7)) cfg.CheckInterval  = Convert.ToSingle(v7);
                if (model.TryGetValue("proximity_events", out var v8) && v8 is TomlTable evtTbl)
                {
                    cfg.ProximityEvents.Clear();
                    foreach (var kv in evtTbl)
                        cfg.ProximityEvents[kv.Key] = Convert.ToInt32(kv.Value);
                }
                if (model.TryGetValue("spin_forward_speed", out var v10)) cfg.SpinForwardSpeed = Convert.ToSingle(v10);
                if (model.TryGetValue("spin_launch_force",  out var v11)) cfg.SpinLaunchForce  = Convert.ToSingle(v11);
                if (model.TryGetValue("swap_weapons", out var v9) && v9 is TomlTable swapTbl)
                {
                    cfg.SwapWeapons.Clear();
                    foreach (var kv in swapTbl)
                        cfg.SwapWeapons[kv.Key] = Convert.ToInt32(kv.Value);
                }

                return cfg;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrollDrop] Failed to load config: {ex.Message}");
                return new TrollConfig();
            }
        }

        private static void CriarPadrao()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CaminhoConfig)!);
            const string toml =
                """
                # ─────────────────────────────────────────────────────────────────────────────
                #  TrollDrop — Configuration File
                # ─────────────────────────────────────────────────────────────────────────────
                #  When a weapon is dropped on the ground, the server secretly rolls ONE troll
                #  effect and assigns it to that weapon. The player never knows what's waiting.
                #
                #  There are 3 possible effects (weights configured in [proximity_events]):
                #    bounce — the weapon flies away from anyone who gets close
                #    spin   — the approaching player's camera snaps 180° instantly
                #    swap   — silent trap: nothing happens on approach, but picking the weapon
                #             up secretly replaces it with a random one from [swap_weapons]
                # ─────────────────────────────────────────────────────────────────────────────

                # Master switch. Set to false to disable the plugin without unloading it.
                enabled = true

                # Upward force (Z velocity) applied to the weapon when a player is at the
                # edge of the trigger radius (i.e. just entered the zone — softer bounce).
                min_bounce_force = 400.0

                # Upward force (Z velocity) applied to the weapon when a player is nearly
                # touching it (i.e. maximum danger zone — hardest bounce).
                max_bounce_force = 700.0

                # Radius in Hammer units around the weapon that activates the bounce.
                # CS2 scale: ~65 units ≈ 1 meter. Default 220 ≈ ~3.4 meters.
                trigger_radius = 220.0

                # Seconds after a weapon is dropped before the zone becomes active.
                # Gives the dropper time to move away before the zone arms.
                arm_delay = 1.5

                # How long (in seconds) a bounce zone stays alive before disappearing.
                # After this time, the weapon will no longer bounce players away.
                zone_lifetime = 30.0

                # Minimum time (seconds) between consecutive bounces of the same weapon.
                # Prevents the weapon from bouncing every single tick.
                bounce_cooldown = 0.3

                # How often (seconds) the proximity check runs.
                # Lower = more responsive but slightly more CPU usage. 0.05 = 20 checks/sec.
                check_interval = 0.05

                # Horizontal speed (units/s) the player is launched at when the spin fires.
                # They get pushed in the direction they're now facing after the 180° flip.
                spin_forward_speed = 500.0

                # Upward velocity on spin — gives it a jump feel. Set to 0 for pure shove.
                spin_launch_force = 300.0

                # When a weapon is dropped, the server secretly rolls ONE troll effect
                # from this weighted pool. The player never knows what's waiting for them.
                #
                #   bounce — weapon flies away when someone walks close
                #   spin   — approaching player's camera snaps 180°
                #   swap   — silent proximity (nothing visible), but picking the weapon
                #             up secretly replaces it with one from [swap_weapons]
                #
                # Weights are relative and do NOT need to sum to 100.
                # Set a weight to 0 to disable that effect entirely.
                [proximity_events]
                bounce = 34
                spin   = 33
                swap   = 33

                [swap_weapons]
                # Weapon pool used when the "swap" effect fires on pickup.
                # Format: weapon_designer_name = relative_weight
                #
                # HOW SLOT MATCHING WORKS:
                #   When a player picks up a rigged weapon, the plugin checks what TYPE
                #   of weapon is on the floor (primary / secondary / grenade) and tries
                #   to give back something from the SAME type first. If nothing in the
                #   pool matches that type, it falls back in this order:
                #     primary → secondary → grenade → anything else in the pool.
                #
                #   This means the player always loses a weapon of the same category
                #   they tried to pick up:
                #     • Picks up a rifle   → their current primary gets replaced
                #     • Picks up a pistol  → their current secondary gets replaced
                #     • Picks up a grenade → their current grenade of that type gets replaced
                #
                # SLOT REFERENCE:
                #   Primaries  : rifles, snipers, SMGs, shotguns, LMGs (everything not below)
                #   Secondaries: weapon_glock, weapon_usp_silencer, weapon_hkp2000,
                #                weapon_p250, weapon_fiveseven, weapon_cz75a,
                #                weapon_deagle, weapon_revolver, weapon_tec9, weapon_elite
                #   Grenades   : weapon_hegrenade, weapon_smokegrenade, weapon_flashbang,
                #                weapon_molotov, weapon_incgrenade, weapon_decoy
                #
                # TIP: Add weapons from multiple slots to let the pool cover any scenario.
                weapon_negev  = 50
                weapon_xm1014 = 50
                """;
            File.WriteAllText(CaminhoConfig, toml);
        }
    }
}
