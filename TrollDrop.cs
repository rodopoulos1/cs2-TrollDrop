using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace TrollDrop
{
    public class TrollDrop : BasePlugin
    {
        public override string ModuleName        => "TrollDrop";
        public override string ModuleVersion     => "1.0.0";
        public override string ModuleAuthor      => "RodoCodes";
        public override string ModuleDescription => "Dropped weapons bounce away from anyone who gets too close";

        private TrollConfig _config = new();

        public override void Load(bool hotReload)
        {
            _config = ConfigLoader.Carregar();

            if (hotReload) BounceService.LimparTudo();

            RegisterListener<Listeners.OnEntityParentChanged>(OnEntityParentChanged);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            AddCommand("css_tdb_reload", "Reloads the TrollDropBounce config", OnReloadCommand);

            AddTimer(_config.CheckInterval,
                () => BounceService.ChecarProximidade(_config),
                TimerFlags.REPEAT);
        }

        private void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
        {
            _config = ConfigLoader.Carregar();
            BounceService.LimparTudo();
            command.ReplyToCommand("[TrollDrop] Config reloaded.");
        }

        private void OnEntityParentChanged(CEntityInstance entity, CEntityInstance newParent)
        {
            var weapon = Utilities.GetEntityFromIndex<CCSWeaponBase>((int)entity.Index);
            if (weapon == null || !weapon.IsValid) return;

            // Only process actual weapons (knife, pistols, rifles, etc.)
            if (!weapon.DesignerName.StartsWith("weapon_")) return;

            // PICKUP: weapon gained a parent — check if it's rigged for a swap
            if (newParent != null && newParent.IsValid)
            {
                if (BounceService.EhSwapRigged((int)entity.Index))
                {
                    int pickupIndex = (int)entity.Index;
                    // Capture the active weapon NOW (before NextFrame) — after pickup the
                    // engine may have already auto-switched to the picked-up weapon.
                    var pawn = Utilities.GetEntityFromIndex<CCSPlayerPawn>((int)newParent.Index);
                    string? activeWeapon = pawn?.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                    Server.NextFrame(() => SwapService.ExecutarSwap(pickupIndex, activeWeapon, _config));
                }
                return;
            }

            // DROP: weapon lost its parent
            int entityIndex = (int)weapon.Index;

            Server.NextFrame(() =>
            {
                var dropped = Utilities.GetEntityFromIndex<CCSWeaponBase>(entityIndex);
                if (dropped == null || !dropped.IsValid || dropped.AbsOrigin == null) return;

                // Make sure the weapon has no owner (is actually on the ground)
                if (dropped.OwnerEntity?.Value != null && dropped.OwnerEntity.Value.IsValid) return;

                // Find the dropper — the closest player within ~300 units.
                // If nobody is nearby this is a round-start weapon spawn; ignore it.
                CCSPlayerPawn? dropperPawn = null;
                float          minDrop     = float.MaxValue;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (!p.IsValid || !p.PawnIsAlive) continue;
                    var pos = p.PlayerPawn.Value?.AbsOrigin;
                    if (pos == null) continue;
                    float dx   = pos.X - dropped.AbsOrigin.X;
                    float dy   = pos.Y - dropped.AbsOrigin.Y;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist <= 300f && dist < minDrop)
                    {
                        minDrop     = dist;
                        dropperPawn = p.PlayerPawn.Value;
                    }
                }

                if (dropperPawn == null) return; // round-start spawn — no dropper nearby

                BounceService.AdicionarZona(entityIndex, (int)dropperPawn.Index, _config);
            });
        }

        private HookResult OnRoundStart(EventRoundStart _, GameEventInfo __)
        {
            BounceService.LimparTudo();
            return HookResult.Continue;
        }
    }
}
