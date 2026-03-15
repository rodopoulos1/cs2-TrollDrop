using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;

namespace TrollDrop
{
    // Weapon slot classification used for smart swap targeting
    internal enum WeaponSlot { Primary, Secondary, Grenade, Other }

    internal static class SwapService
    {
        // ─── slot detection ───────────────────────────────────────────────────

        private static WeaponSlot GetSlot(string name) => name switch
        {
            "weapon_hegrenade" or "weapon_smokegrenade" or "weapon_flashbang"
            or "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy"
                => WeaponSlot.Grenade,

            "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000"
            or "weapon_p250" or "weapon_fiveseven" or "weapon_cz75a"
            or "weapon_deagle" or "weapon_revolver" or "weapon_tec9"
            or "weapon_elite"
                => WeaponSlot.Secondary,

            var n when n.StartsWith("weapon_") => WeaponSlot.Primary,
            _ => WeaponSlot.Other
        };

        // ─── weighted random pick ────────────────────────────────────────────

        private static string? PickWeighted(Dictionary<string, int> pool)
        {
            if (pool.Count == 0) return null;
            int total = pool.Values.Sum();
            if (total <= 0) return null;

            int roll = Random.Shared.Next(total);
            int acc  = 0;
            foreach (var (name, weight) in pool)
            {
                acc += weight;
                if (roll < acc) return name;
            }
            return pool.Keys.First();
        }

        // Picks from the pool prioritising weapons that match preferredSlot.
        // Fallback order if none match: Primary → Secondary → Grenade → anything.
        private static string? PickWithPriority(Dictionary<string, int> pool, WeaponSlot preferredSlot)
        {
            var bySlot = pool
                .GroupBy(kv => GetSlot(kv.Key))
                .ToDictionary(g => g.Key, g => g.ToDictionary(kv => kv.Key, kv => kv.Value));

            var priority = new[] { preferredSlot, WeaponSlot.Primary, WeaponSlot.Secondary, WeaponSlot.Grenade }
                .Distinct();

            foreach (var slot in priority)
                if (bySlot.TryGetValue(slot, out var sub) && sub.Count > 0)
                    return PickWeighted(sub);

            return PickWeighted(pool); // last resort: anything in the pool
        }

        // ─── knife detection ─────────────────────────────────────────────────

        private static bool EhFaca(string name) =>
            name.Contains("knife") || name.Contains("bayonet") || name == "weapon_knifegg";

        // ─── main entry point ────────────────────────────────────────────────

        // Called in Server.NextFrame after a rigged weapon is picked up.
        // capturedActiveWeapon: the weapon name that was ACTIVE in the player's hand
        //   at the exact moment of pickup (captured before NextFrame in the event handler).
        public static void ExecutarSwap(int entityIndex, string? capturedActiveWeapon, TrollConfig cfg)
        {
            if (!BounceService.EhSwapRigged(entityIndex)) return;
            BounceService.RemoverSwap(entityIndex);

            // Never swap if the player had their knife out when picking up
            if (capturedActiveWeapon != null && EhFaca(capturedActiveWeapon)) return;

            var weapon = Utilities.GetEntityFromIndex<CCSWeaponBase>(entityIndex);
            if (weapon == null || !weapon.IsValid) return;

            // OwnerEntity is unreliable right after pickup — scan all players instead
            CCSPlayerController? controller = null;
            foreach (var p in Utilities.GetPlayers())
            {
                if (!p.IsValid || !p.PawnIsAlive) continue;
                var ws = p.PlayerPawn.Value?.WeaponServices?.MyWeapons;
                if (ws == null) continue;
                foreach (var h in ws)
                {
                    var w = h.Value;
                    if (w != null && w.IsValid && (int)w.Index == entityIndex)
                    {
                        controller = p;
                        break;
                    }
                }
                if (controller != null) break;
            }

            if (controller == null || !controller.IsValid || !controller.PawnIsAlive) return;

            // Pick a replacement from the pool favouring the same slot as the active weapon
            WeaponSlot activeSlot = capturedActiveWeapon != null
                ? GetSlot(capturedActiveWeapon)
                : GetSlot(weapon.DesignerName);
            string? replacement = PickWithPriority(cfg.SwapWeapons, activeSlot);
            if (replacement == null) return;

            // Build restore list:
            //   • ALWAYS keep knives (never remove)
            //   • Skip the rigged pickup weapon (entityIndex)
            //   • Skip the ONE weapon that was active in hand — only that one gets replaced
            bool activeRemoved = false;
            var  restore       = new List<string>();

            var myWeapons = controller.PlayerPawn.Value?.WeaponServices?.MyWeapons;
            if (myWeapons != null)
            foreach (var handle in myWeapons)
            {
                var w = handle.Value;
                if (w == null || !w.IsValid) continue;
                var wname = w.DesignerName;
                if (string.IsNullOrEmpty(wname)) continue;

                // Knife: always restore, never remove
                if (EhFaca(wname)) { restore.Add(wname); continue; }

                // The rigged pickup weapon disappears (replaced by `replacement`)
                if ((int)w.Index == entityIndex) continue;

                // The weapon that was in the player's hand gets replaced — remove it once
                if (!activeRemoved && capturedActiveWeapon != null && wname == capturedActiveWeapon)
                {
                    activeRemoved = true;
                    continue;
                }

                restore.Add(wname);
            }

            // RemoveWeapons() is the safe method — never call weapon.Remove() or
            // AcceptInput("Kill") directly as they cause a WriteEnterPVS crash.
            controller.RemoveWeapons();

            // NextFrame: engine finishes destroying entities before we create new ones.
            Server.NextFrame(() =>
            {
                if (!controller.IsValid || !controller.PawnIsAlive) return;
                foreach (var name in restore)
                    controller.GiveNamedItem(name);
                controller.GiveNamedItem(replacement);
            });
        }
    }
}
