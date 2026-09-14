using System.Collections.Generic;
using Unity.Entities;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Scripting;
using Stunlock.Core;

namespace CastleLink.Services;

/// <summary>
/// Enumerates the chests/containers belonging to a castle (via the heart's
/// shared-inventory manager) and helps count/remove items across them.
/// </summary>
internal static class StashService
{
    private static EntityManager EM => Core.EntityManager;
    private static ServerGameManager SGM => Core.ServerGameManager;

    /// <summary>
    /// All container ("stash") entities in the castle owned by <paramref name="heart"/>.
    /// These are the InventorySource entities from the heart's SharedCastleInventories.
    /// </summary>
    public static List<Entity> GetStashes(Entity heart)
    {
        var result = new List<Entity>();
        if (!EM.Exists(heart) || !EM.HasComponent<SharedCastleInventoryConnection>(heart))
            return result;

        var conn = EM.GetComponentData<SharedCastleInventoryConnection>(heart);
        var manager = conn.SharedInventoryManager.GetEntityOnServer();
        if (!EM.Exists(manager) || !EM.HasBuffer<SharedCastleInventories>(manager))
            return result;

        var buffer = EM.GetBuffer<SharedCastleInventories>(manager);
        for (int i = 0; i < buffer.Length; i++)
        {
            var src = buffer[i].InventorySource;
            if (EM.Exists(src)) result.Add(src);
        }
        return result;
    }

    /// <summary>
    /// Resolve the real inventory container entity for a stash source. The
    /// SharedCastleInventories.InventorySource is often a wrapper whose items live
    /// on an attached sub-entity; TryGetInventoryEntity resolves the correct one.
    /// </summary>
    public static bool TryGetInventory(Entity stash, out Entity inventory)
    {
        inventory = stash;
        try
        {
            if (InventoryUtilities.TryGetInventoryEntity(EM, stash, out var inv) && EM.Exists(inv))
            {
                inventory = inv;
                return true;
            }
        }
        catch { }
        return EM.Exists(stash);
    }

    /// <summary>
    /// The maximum stack size for an item, from its ItemData. Defensive: returns a
    /// safe fallback if the lookup isn't available. Wrapped so it can't crash.
    /// </summary>
    public static int GetMaxStack(PrefabGUID item, int fallback = 1000)
    {
        try
        {
            var map = Core.GameDataSystem.ItemHashLookupMap;
            if (!map.IsCreated) return fallback;
            if (map.TryGetValue(item, out var data) && data.MaxAmount > 0)
                return data.MaxAmount;
        }
        catch { }
        return fallback;
    }

    /// <summary>
    /// The item's category flags (Weapon/Armor/Herb/etc.), from ItemData. Defensive:
    /// returns NONE if the lookup isn't available. Wrapped so it can't crash.
    /// </summary>
    public static ItemCategory GetItemCategory(PrefabGUID item)
    {
        try
        {
            var map = Core.GameDataSystem.ItemHashLookupMap;
            if (!map.IsCreated) return ItemCategory.NONE;
            if (map.TryGetValue(item, out var data))
                return data.ItemCategory;
        }
        catch { }
        return ItemCategory.NONE;
    }

    /// <summary>True if the stash has any restricted inventory instance (a storage box).</summary>
    public static bool IsRestrictedStash(Entity stash)
    {
        try
        {
            if (!EM.HasBuffer<InventoryInstanceElement>(stash)) return false;
            var inst = EM.GetBuffer<InventoryInstanceElement>(stash);
            for (int i = 0; i < inst.Length; i++)
            {
                if (inst[i].RestrictedCategory != 0) return true;
                if (inst[i].RestrictedType.GuidHash != 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>The castle's RESTRICTED storage boxes (Material/Herb/Alchemy/etc.).</summary>
    public static List<Entity> GetRestrictedStashes(Entity heart)
    {
        var result = new List<Entity>();
        foreach (var stash in GetStashes(heart))
            if (IsRestrictedStash(stash)) result.Add(stash);
        return result;
    }

    /// <summary>
    /// Deposit up to <paramref name="amount"/> into the castle's RESTRICTED boxes
    /// only. The game rejects items that don't match a box's restriction, so herbs
    /// go to herb boxes, materials to material boxes, etc. Returns amount deposited.
    /// </summary>
    public static int DepositIntoRestrictedBox(Entity heart, PrefabGUID item, int amount)
    {
        int deposited = 0;
        var boxes = GetRestrictedStashes(heart);
        // Prefer boxes already holding the item, then any restricted box.
        foreach (var stash in boxes)
        {
            if (deposited >= amount) break;
            int have; try { have = SGM.GetInventoryItemCount(stash, item); } catch { continue; }
            if (have <= 0) continue;
            deposited += TryDeposit(stash, item, amount - deposited);
        }
        foreach (var stash in boxes)
        {
            if (deposited >= amount) break;
            int have; try { have = SGM.GetInventoryItemCount(stash, item); } catch { continue; }
            if (have > 0) continue;
            deposited += TryDeposit(stash, item, amount - deposited);
        }
        return deposited;
    }

    /// <summary>All distinct item types currently stored in the castle's chests.</summary>
    public static Dictionary<int, PrefabGUID> GetStoredItemTypes(Entity heart)
    {
        var items = new Dictionary<int, PrefabGUID>();
        foreach (var stash in GetStashes(heart))
        {
            if (!TryGetInventory(stash, out var inv)) continue;
            if (!EM.HasBuffer<InventoryBuffer>(inv)) continue;
            var buf = EM.GetBuffer<InventoryBuffer>(inv);
            for (int i = 0; i < buf.Length; i++)
            {
                var t = buf[i].ItemType;
                if (t.GuidHash != 0) items[t.GuidHash] = t;
            }
        }
        return items;
    }

    /// <summary>Total amount of <paramref name="item"/> across all chests in the castle.</summary>
    public static int CountItemInCastle(Entity heart, PrefabGUID item)
    {
        int total = 0;
        foreach (var stash in GetStashes(heart))
            total += SGM.GetInventoryItemCount(stash, item);
        return total;
    }

    /// <summary>
    /// Remove up to <paramref name="amount"/> of <paramref name="item"/> from the
    /// castle's chests. Returns the amount actually removed.
    /// </summary>
    public static int RemoveItemFromCastle(Entity heart, PrefabGUID item, int amount)
    {
        int removed = 0;
        foreach (var stash in GetStashes(heart))
        {
            if (removed >= amount) break;
            int want = amount - removed;
            int have = SGM.GetInventoryItemCount(stash, item);
            if (have <= 0) continue;
            int take = have < want ? have : want;
            if (SGM.TryRemoveInventoryItem(stash, item, take))
                removed += take;
        }
        return removed;
    }

    /// <summary>
    /// Return items to the castle's chests (used to undo a pull when the station
    /// couldn't accept them). Best-effort across chests.
    /// </summary>
    public static void ReturnItemToCastle(Entity heart, PrefabGUID item, int amount)
    {
        foreach (var stash in GetStashes(heart))
        {
            if (amount <= 0) break;
            var resp = SGM.TryAddInventoryItem(stash, item, amount);
            if (resp.Success) { amount = 0; break; }
            // Partial add: reduce by what went in, keep trying next chest.
            amount = resp.RemainingAmount;
        }
    }

    /// <summary>
    /// Deposit up to <paramref name="amount"/> of <paramref name="item"/> into the
    /// castle's chests. Prefers chests that already contain the item (to consolidate
    /// stacks), then falls back to any chest. The game's own inventory restrictions
    /// (e.g. herb/material storage boxes) reject incompatible items automatically,
    /// so herbs land in herb boxes, ore in material boxes, etc.
    /// Returns the amount actually deposited.
    /// </summary>
    public static int DepositIntoMatchingChest(Entity heart, PrefabGUID item, int amount)
    {
        int deposited = 0;
        var stashes = GetStashes(heart);
        var sgm = SGM;

        // Pass 1: chests that already contain this item (consolidate stacks). This
        // naturally routes herbs->herb box, ore->ore box, etc. when those boxes
        // already hold the item.
        foreach (var stash in stashes)
        {
            if (deposited >= amount) break;
            int have;
            try { have = sgm.GetInventoryItemCount(stash, item); }
            catch { continue; }
            if (have <= 0) continue;
            deposited += TryDeposit(stash, item, amount - deposited);
        }

        // Pass 2: any other chest that accepts it. The game's own inventory
        // restrictions (herb/material storage boxes) reject incompatible items,
        // so TryAddInventoryItem simply fails on the wrong box and we move on.
        foreach (var stash in stashes)
        {
            if (deposited >= amount) break;
            int have;
            try { have = sgm.GetInventoryItemCount(stash, item); }
            catch { continue; }
            if (have > 0) continue; // already handled in pass 1
            deposited += TryDeposit(stash, item, amount - deposited);
        }

        return deposited;
    }

    /// <summary>Try to add up to want; returns amount actually added.</summary>
    private static int TryDeposit(Entity stash, PrefabGUID item, int want)
    {
        if (want <= 0) return 0;
        try
        {
            var resp = SGM.TryAddInventoryItem(stash, item, want);
            if (resp.Success) return want;
            // Partial or rejected: RemainingAmount tells us what didn't fit.
            return want - resp.RemainingAmount;
        }
        catch { return 0; }
    }
}
