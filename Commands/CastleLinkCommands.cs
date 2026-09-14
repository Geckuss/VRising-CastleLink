using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using VampireCommandFramework;
using ProjectM;
using ProjectM.Scripting;
using Stunlock.Core;
using CastleLink.Services;

namespace CastleLink.Commands;

internal static class CastleLinkCommands
{
    private static EntityManager EM => Core.EntityManager;
    private static ServerGameManager SGM => Core.ServerGameManager;

    [Command("clhelp", "clh", description: "List all CastleLink commands.", adminOnly: false)]
    public static void Help(ChatCommandContext ctx)
    {
        ctx.Reply("CastleLink \u2014 share resources across your castles:");
        ctx.Reply(".wolt (.all) \u2014 top up THIS castle's storage boxes to one stack of each storable item, from your other castles.");
        ctx.Reply(".pull \u2014 top up items you already carry to one full stack (storable items only).");
        ctx.Reply(".pull <guid> <amount> \u2014 pull a specific item by PrefabGUID hash.");
        ctx.Reply(".pullcat (.pc) <category> \u2014 pull all items of a category (other castles first, then current).");
        ctx.Reply(".deposit (.dep) \u2014 deposit inventory into this castle's dedicated boxes (leaves armor/weapons).");
        ctx.Reply("Categories: " + string.Join(", ", CategoryGroups.Keys));
        ctx.Reply("Crafting: right-click a recipe at a station to pull its materials from your other castles.");
    }

    [Command("pull", description: "Pull resources from your other castles into your inventory (fills until inventory is full).", adminOnly: false)]
    public static void Pull(ChatCommandContext ctx)
    {
        var character = ctx.Event.SenderCharacterEntity;
        var siblings = OwnerCastleService.GetSiblingHeartsForTarget(character);
        if (siblings.Count == 0) { ctx.Reply("No other castles found to pull from."); return; }

        if (!InventoryUtilities.TryGetInventoryEntity(EM, character, out var inv) || !EM.Exists(inv))
        { ctx.Reply("Could not access your inventory."); return; }

        // Gather the distinct STORABLE items available across sibling castles
        // (items that live in a restricted storage box = materials/herbs/alchemy/etc.,
        // not armor/jewels/coins).
        var available = new Dictionary<int, PrefabGUID>();
        foreach (var heart in siblings)
        {
            foreach (var box in StashService.GetRestrictedStashes(heart))
            {
                if (!StashService.TryGetInventory(box, out var boxInv)) continue;
                if (!EM.HasBuffer<InventoryBuffer>(boxInv)) continue;
                var buf = EM.GetBuffer<InventoryBuffer>(boxInv);
                for (int i = 0; i < buf.Length; i++)
                {
                    var t = buf[i].ItemType;
                    if (t.GuidHash != 0) available[t.GuidHash] = t;
                }
            }
        }

        int totalPulled = 0;
        foreach (var kv in available)
        {
            var item = kv.Value;

            // Only top up items you already carry some of, to one full stack.
            int have = SGM.GetInventoryItemCount(inv, item);
            if (have <= 0) continue;                       // don't pull items you have none of
            int maxStack = StashService.GetMaxStack(item);
            int wanted = maxStack - have;
            if (wanted <= 0) continue;                     // already a full stack

            totalPulled += PullItem(siblings, inv, item, wanted);
        }

        ctx.Reply(totalPulled > 0
            ? $"Topped up {totalPulled} items into your inventory (one stack each)."
            : "Nothing to top up (you already have full stacks, or other castles are empty).");
    }

    // Named item-category groups for '.pullcat <category>'.
    private static readonly Dictionary<string, ItemCategory> CategoryGroups = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["armor"]       = ItemCategory.Armor,
        ["weapons"]     = ItemCategory.Weapon,
        ["weapon"]      = ItemCategory.Weapon,
        ["gear"]        = ItemCategory.Armor | ItemCategory.Weapon | ItemCategory.Jewel | ItemCategory.Bag,
        ["herbs"]       = ItemCategory.Herb | ItemCategory.Flower,
        ["materials"]   = ItemCategory.Lumber | ItemCategory.Stone | ItemCategory.Mineral
                          | ItemCategory.Tailoring | ItemCategory.Woodworking | ItemCategory.Alchemy,
        ["jewels"]      = ItemCategory.Jewel | ItemCategory.Gem,
        ["consumables"] = ItemCategory.Consumable | ItemCategory.BloodPotion,
        ["fish"]        = ItemCategory.Fish,
        ["coins"]       = ItemCategory.Silver | ItemCategory.Coin,

        // Additional categories.
        ["gems"]        = ItemCategory.Gem,
        ["blood"]       = ItemCategory.Blood | ItemCategory.BloodEssence | ItemCategory.BloodPotion,
        ["soulshards"]  = ItemCategory.Soulshard,
        ["relics"]      = ItemCategory.Relic,
        ["bags"]        = ItemCategory.Bag,
        ["saddles"]     = ItemCategory.Saddle,
        ["stables"]     = ItemCategory.StablesIngredient | ItemCategory.Fish,
        ["magic"]       = ItemCategory.Magic,
        ["lumber"]      = ItemCategory.Lumber | ItemCategory.Woodworking,
        ["stone"]       = ItemCategory.Stone,
        ["minerals"]    = ItemCategory.Mineral,
        ["alchemy"]     = ItemCategory.Alchemy,
        ["tailoring"]   = ItemCategory.Tailoring,
        ["flowers"]     = ItemCategory.Flower,
        ["everything"]  = ItemCategory.ALL,
    };

    [Command("pullcat", "pc", description: "Pull all items of a category from your other castles into your inventory. Categories: armor, weapons, gear, herbs, materials, jewels, consumables, fish, silver.", adminOnly: false)]
    public static void PullCategory(ChatCommandContext ctx, string category)
    {
        if (!CategoryGroups.TryGetValue(category, out var mask))
        {
            ctx.Reply($"Unknown category '{category}'. Options: {string.Join(", ", CategoryGroups.Keys)}.");
            return;
        }

        var character = ctx.Event.SenderCharacterEntity;
        var siblings = OwnerCastleService.GetSiblingHeartsForTarget(character);
        var current = OwnerCastleService.GetCurrentHeart(character);

        // Pull sources: OTHER castles first (prioritized), then the current castle.
        var sources = new List<Entity>(siblings);
        if (current != Entity.Null) sources.Add(current);
        if (sources.Count == 0) { ctx.Reply("No castles found to pull from."); return; }

        if (!InventoryUtilities.TryGetInventoryEntity(EM, character, out var inv) || !EM.Exists(inv))
        { ctx.Reply("Could not access your inventory."); return; }

        var available = new Dictionary<int, PrefabGUID>();
        foreach (var heart in sources)
        {
            foreach (var kv in StashService.GetStoredItemTypes(heart))
            {
                var cat = StashService.GetItemCategory(kv.Value);
                if ((cat & mask) != 0) available[kv.Key] = kv.Value;
            }
        }

        int totalPulled = 0;
        foreach (var kv in available)
            totalPulled += PullItem(sources, inv, kv.Value, int.MaxValue);

        ctx.Reply(totalPulled > 0
            ? $"Pulled {totalPulled} {category} items into your inventory."
            : $"Nothing to pull for '{category}' (none found, or inventory full).");
    }

    [Command("pull", description: "Pull a specific amount of an item (by PrefabGUID hash) into your inventory.", adminOnly: false)]
    public static void PullSpecific(ChatCommandContext ctx, int prefabGuid, int amount)
    {
        var character = ctx.Event.SenderCharacterEntity;
        var siblings = OwnerCastleService.GetSiblingHeartsForTarget(character);
        if (siblings.Count == 0) { ctx.Reply("No other castles found to pull from."); return; }

        if (!InventoryUtilities.TryGetInventoryEntity(EM, character, out var inv) || !EM.Exists(inv))
        { ctx.Reply("Could not access your inventory."); return; }

        int pulled = PullItem(siblings, inv, new PrefabGUID(prefabGuid), amount);
        ctx.Reply(pulled > 0 ? $"Pulled {pulled}x item {prefabGuid}." : "Nothing pulled (not found in other castles, or inventory full).");
    }

    [Command("deposit", "dep", description: "Deposit inventory items into this castle's dedicated storage boxes (materials/herbs/etc.; leaves armor/weapons).", adminOnly: false)]
    public static void Deposit(ChatCommandContext ctx)
    {
        var character = ctx.Event.SenderCharacterEntity;
        var heart = OwnerCastleService.GetCurrentHeart(character);
        if (heart == Entity.Null) { ctx.Reply("You must be standing in one of your castles."); return; }

        if (!InventoryUtilities.TryGetInventoryEntity(EM, character, out var inv) || !EM.Exists(inv))
        { ctx.Reply("Could not access your inventory."); return; }

        // Snapshot the distinct items currently in the player's inventory.
        var items = new Dictionary<int, PrefabGUID>();
        var buffer = EM.GetBuffer<InventoryBuffer>(inv);
        for (int i = 0; i < buffer.Length; i++)
        {
            var slot = buffer[i];
            if (slot.ItemType.GuidHash == 0) continue;
            items[slot.ItemType.GuidHash] = slot.ItemType;
        }

        int totalDeposited = 0;
        foreach (var kv in items)
        {
            var item = kv.Value;
            int have = SGM.GetInventoryItemCount(inv, item);
            if (have <= 0) continue;

            int deposited = StashService.DepositIntoRestrictedBox(heart, item, have);
            if (deposited <= 0) continue;

            // Remove what we deposited from the player's inventory.
            SGM.TryRemoveInventoryItem(inv, item, deposited);
            totalDeposited += deposited;
        }

        ctx.Reply(totalDeposited > 0
            ? $"Deposited {totalDeposited} items into this castle's chests."
            : "Nothing deposited (no matching chests for your items).");
    }

    [Command("wolt", "all", description: "Pull storable resources from your other castles straight into this castle's storage boxes (one stack each).", adminOnly: false)]
    public static void Wolt(ChatCommandContext ctx)
    {
        var character = ctx.Event.SenderCharacterEntity;
        var current = OwnerCastleService.GetCurrentHeart(character);
        if (current == Entity.Null) { ctx.Reply("You must be standing in one of your castles."); return; }

        var siblings = OwnerCastleService.GetSiblingHeartsForTarget(character);
        if (siblings.Count == 0) { ctx.Reply("No other castles found to pull from."); return; }

        // Gather all item types available in sibling castles.
        var available = new Dictionary<int, PrefabGUID>();
        foreach (var heart in siblings)
            foreach (var kv in StashService.GetStoredItemTypes(heart))
                available[kv.Key] = kv.Value;

        int moved = 0;
        foreach (var kv in available)
        {
            var item = kv.Value;

            // Target: one full stack in this castle's storage. Only pull the top-up.
            int maxStack = StashService.GetMaxStack(item);
            int existing = StashService.CountItemInCastle(current, item);
            int wanted = maxStack - existing;
            if (wanted <= 0) continue; // already have a full stack (or more)

            foreach (var heart in siblings)
            {
                if (wanted <= 0) break;

                int taken = StashService.RemoveItemFromCastle(heart, item, wanted);
                if (taken <= 0) continue;

                int deposited = StashService.DepositIntoRestrictedBox(current, item, taken);
                moved += deposited;
                wanted -= deposited;

                // Anything not storable here goes back where it came from.
                int leftover = taken - deposited;
                if (leftover > 0) StashService.ReturnItemToCastle(heart, item, leftover);
            }
        }

        ctx.Reply(moved > 0
            ? $"Topped up {moved} items into this castle's storage boxes (one stack each)."
            : "Nothing to top up (storage already full, or no storable resources in other castles).");
    }

    private static int PullItem(List<Entity> siblings, Entity inventory, PrefabGUID item, int amount)
    {
        int pulled = 0;
        foreach (var heart in siblings)
        {
            if (pulled >= amount) break;
            int want = amount - pulled;
            int got = StashService.RemoveItemFromCastle(heart, item, want);
            if (got <= 0) continue;

            var add = SGM.TryAddInventoryItem(inventory, item, got);
            if (add.Success) { pulled += got; }
            else
            {
                // Put back the part that didn't fit.
                int added = got - add.RemainingAmount;
                pulled += added;
                if (add.RemainingAmount > 0)
                    StashService.ReturnItemToCastle(heart, item, add.RemainingAmount);
                break; // inventory full
            }
        }
        return pulled;
    }
}

