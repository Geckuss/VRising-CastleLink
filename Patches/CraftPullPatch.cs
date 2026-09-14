using Unity.Collections;
using Unity.Entities;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using ProjectM.Scripting;
using Stunlock.Core;
using CastleLink.Services;

namespace CastleLink.Patches;

/// <summary>
/// On-demand cross-castle CRAFT PULL.
///
/// Station-crafting affordability is computed client-side and its server path is
/// IL2CPP-inlined, so we can't make the craft button "see" other castles. Instead,
/// when a player queues/adjusts a recipe at a station (StopCraftingSystem event),
/// we compute the recipe's requirements, see how much is missing from the station
/// (+ player) inventory, and physically pull the shortfall from the player's OTHER
/// castles' chests into the station. The vanilla local check then passes.
///
/// Modeled on KindredLogistics' craft-pull, but sourcing from the player's own
/// sibling castles rather than a single territory.
/// </summary>
[HarmonyPatch(typeof(StopCraftingSystem), nameof(StopCraftingSystem.OnUpdate))]
internal static class CraftPullPatch
{
    private static EntityManager EM => Core.EntityManager;
    private static ServerGameManager SGM => Core.ServerGameManager;

    private static void Prefix(StopCraftingSystem __instance)
    {
        if (!Core.Initialized) return;

        NativeArray<Entity> events;
        try { events = __instance._EventQuery.ToEntityArray(Allocator.Temp); }
        catch { return; }

        try
        {
            foreach (var evt in events)
            {
                if (!EM.HasComponent<StopCraftItemEvent>(evt) || !EM.HasComponent<FromCharacter>(evt))
                    continue;

                var stop = EM.GetComponentData<StopCraftItemEvent>(evt);
                var from = EM.GetComponentData<FromCharacter>(evt);
                var character = from.Character;

                if (!EM.Exists(character) || !EM.HasComponent<Interactor>(character)) continue;
                var station = EM.GetComponentData<Interactor>(character).Target;
                if (!EM.Exists(station)) continue;

                TryPullForRecipe(character, station, stop.RecipeGuid);
            }
        }
        catch (System.Exception ex)
        {
            Core.Log?.LogError($"CastleLink CraftPull error: {ex}");
        }
        finally
        {
            events.Dispose();
        }
    }

    private static void TryPullForRecipe(Entity character, Entity station, PrefabGUID recipeGuid)
    {
        // Which castles can we pull from?
        var siblings = OwnerCastleService.GetSiblingHeartsForTarget(character);
        if (siblings.Count == 0) return;

        // Resolve the recipe entity -> requirement buffer.
        var prefabMap = Core.PrefabCollectionSystem._PrefabGuidToEntityMap;
        if (!prefabMap.TryGetValue(recipeGuid, out var recipeEntity)) return;
        if (!EM.HasBuffer<RecipeRequirementBuffer>(recipeEntity)) return;

        // Pull materials into the PLAYER's inventory (per preference). The craft
        // then draws from station + player inventory.
        if (!InventoryUtilities.TryGetInventoryEntity(EM, character, out var playerInventory) || !EM.Exists(playerInventory))
            return;

        // Also count what's in the station, so we only pull the true shortfall.
        TryGetStationInventory(station, out var stationInventory);

        var reqs = EM.GetBuffer<RecipeRequirementBuffer>(recipeEntity);
        for (int i = 0; i < reqs.Length; i++)
        {
            var need = reqs[i];
            PrefabGUID item = need.Guid;
            int required = need.Amount;
            if (required <= 0) continue;

            // How much is already available locally (station + player)?
            int haveStation = EM.Exists(stationInventory) ? SGM.GetInventoryItemCount(stationInventory, item) : 0;
            int havePlayer = SGM.GetInventoryItemCount(playerInventory, item);

            int shortfall = required - (haveStation + havePlayer);
            if (shortfall <= 0) continue;

            // Pull the shortfall from sibling castles into the player's inventory.
            foreach (var heart in siblings)
            {
                if (shortfall <= 0) break;
                int pulled = StashService.RemoveItemFromCastle(heart, item, shortfall);
                if (pulled <= 0) continue;

                var add = SGM.TryAddInventoryItem(playerInventory, item, pulled);
                if (add.Success)
                {
                    shortfall -= pulled;
                }
                else
                {
                    // Inventory full / couldn't add: put it back where we took it.
                    StashService.ReturnItemToCastle(heart, item, pulled);
                }
            }
        }
    }

    /// <summary>
    /// The station's own inventory entity. For most workstations the station
    /// entity IS the inventory owner; fall back to TryGetInventoryEntity.
    /// </summary>
    private static bool TryGetStationInventory(Entity station, out Entity inventory)
    {
        if (InventoryUtilities.TryGetInventoryEntity(EM, station, out inventory) && EM.Exists(inventory))
            return true;
        inventory = station;
        return EM.Exists(station);
    }
}
