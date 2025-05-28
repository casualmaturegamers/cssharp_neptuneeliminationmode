using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace NeptuneEliminationMode
{
    [MinimumApiVersion(80)]
    public class NeptuneEliminationMode : BasePlugin
    {
        private readonly ConcurrentDictionary<int, HashSet<int>> playerKillMap = new();
        private const int MAX_KILLS_PER_PLAYER = 100;

        public override string ModuleName => "Neptune Elimination Mode";
        public override string ModuleVersion => "1.0.0";
        public override string ModuleAuthor => "Neptune890";
        public override string ModuleDescription => "A plugin that implements elimination mode for Counter-Strike 2.";

        public override void Load(bool hotReload)
        {
            try
            {
                Logger.LogInformation("Neptune Elimination Mode plugin is loading. HotReload: {HotReload}", hotReload);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during plugin load");
                throw;
            }
        }

        public override void Unload(bool hotReload)
        {
            try
            {
                playerKillMap.Clear();
                Logger.LogInformation("Neptune Elimination Mode plugin is unloading. HotReload: {HotReload}", hotReload);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during plugin unload");
            }
        }

        [GameEventHandler(HookMode.Post)]
        public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            try
            {
                if (@event == null)
                {
                    Logger.LogWarning("Received null EventPlayerDeath");
                    return HookResult.Continue;
                }

                var killer = @event.Attacker;
                var victim = @event.Userid;

                if (victim == null)
                {
                    Logger.LogWarning("Victim is null in OnPlayerDeath event");
                    return HookResult.Continue;
                }

                if (!IsValidPlayer(victim))
                {
                    Logger.LogWarning("Invalid victim in OnPlayerDeath event");
                    return HookResult.Continue;
                }

                if (IsTeamEliminated(victim))
                {
                    Logger.LogInformation("Team {Team} has been eliminated", victim.Team);
                    return HookResult.Continue;
                }

                if (killer != null && IsValidPlayer(killer) && killer != victim)
                {
                    HandleKillTracking(killer.UserId!.Value, victim.UserId!.Value);
                }

                HandleVictimRespawns(victim.UserId!.Value);

                return HookResult.Continue;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnPlayerDeath event handling");
                return HookResult.Continue;
            }
        }

        [GameEventHandler(HookMode.Pre)]
        public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            try
            {
                if (@event == null)
                {
                    Logger.LogWarning("Received null EventRoundStart");
                    return HookResult.Continue;
                }

                playerKillMap.Clear();
                CleanupStaleEntries();
                Logger.LogInformation("Round has started. Kill map cleared and cleaned.");
                return HookResult.Continue;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnRoundStart event handling");
                return HookResult.Continue;
            }
        }

        [GameEventHandler(HookMode.Post)]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            try
            {
                if (@event?.Userid == null || !@event.Userid.UserId.HasValue)
                {
                    Logger.LogWarning("Invalid or null player in OnPlayerDisconnect");
                    return HookResult.Continue;
                }

                int userId = @event.Userid.UserId.Value;
                if (playerKillMap.TryRemove(userId, out _))
                {
                    Logger.LogInformation("Removed disconnected player {UserId} from kill map", userId);
                }
                return HookResult.Continue;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnPlayerDisconnect handling");
                return HookResult.Continue;
            }
        }

        private bool IsValidPlayer(CCSPlayerController? player)
        {
            return player != null &&
                   player.IsValid &&
                   player.UserId.HasValue &&
                   !player.IsBot &&
                   !player.IsHLTV;
        }

        private bool IsTeamEliminated(CCSPlayerController? victim)
        {
            try
            {
                if (victim == null || !victim.IsValid)
                {
                    Logger.LogWarning("Cannot check team elimination: victim is null or invalid");
                    return false;
                }

                var allPlayers = Utilities.GetPlayers();
                return !allPlayers.Any(p =>
                    p.IsValid &&
                    p.Team == victim.Team &&
                    p.PawnIsAlive &&
                    !p.IsBot &&
                    !p.IsHLTV);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error checking team elimination status for victim {UserId}", victim?.UserId ?? -1);
                return false;
            }
        }

        private void HandleKillTracking(int killerId, int victimId)
        {
            playerKillMap.AddOrUpdate(
                killerId,
                new HashSet<int> { victimId },
                (_, existingKills) =>
                {
                    if (existingKills.Count >= MAX_KILLS_PER_PLAYER)
                    {
                        Logger.LogWarning("Player {KillerId} reached kill tracking limit", killerId);
                        return existingKills;
                    }
                    existingKills.Add(victimId);
                    return existingKills;
                });
        }

        private void HandleVictimRespawns(int victimId)
        {
            if (!playerKillMap.TryRemove(victimId, out var killedPlayers))
                return;

            foreach (var respawnPlayerId in killedPlayers)
            {
                try
                {
                    var player = Utilities.GetPlayerFromUserid(respawnPlayerId);
                    if (player != null && IsValidPlayer(player) && !player.PawnIsAlive)
                    {
                        player.Respawn();
                        Logger.LogInformation("Respawned player {PlayerId}", respawnPlayerId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error respawning player {PlayerId}", respawnPlayerId);
                }
            }
        }

        private void CleanupStaleEntries()
        {
            try
            {
                var allPlayers = Utilities.GetPlayers();
                var activeUserIds = allPlayers
                    .Where(p => IsValidPlayer(p) && p.UserId.HasValue) // Ensure UserId has a value
                    .Select(p => p.UserId!.Value) // Safe now with HasValue check
                    .ToHashSet();

                var staleKeys = playerKillMap.Keys
                    .Where(k => !activeUserIds.Contains(k))
                    .ToList();

                foreach (var key in staleKeys)
                {
                    if (playerKillMap.TryRemove(key, out _))
                    {
                        Logger.LogInformation("Removed stale player {UserId} from kill map", key);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during stale entry cleanup");
            }
        }
    }
}