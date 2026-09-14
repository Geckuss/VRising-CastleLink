using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using ProjectM;
using ProjectM.CastleBuilding;

namespace CastleLink.Services;

/// <summary>
/// Maps a player (by PlatformId) to all the castle hearts they own, so crafting
/// and building can pull resources across every castle the player owns.
///
/// Results are cached briefly (TTL) because building the map walks every castle
/// heart entity. The cache is rebuilt on demand when stale.
/// </summary>
internal static class OwnerCastleService
{
    // How long (seconds) a built ownership map stays valid before a rebuild.
    private const double CacheTtlSeconds = 5.0;

    private static double _lastBuild = double.NegativeInfinity;
    private static readonly Dictionary<ulong, List<Entity>> _heartsByOwner = new();

    private static EntityManager EM => Core.EntityManager;

    /// <summary>
    /// All castle heart entities owned by the given player, EXCEPT the one passed
    /// in <paramref name="excludeHeart"/> (typically the castle the action is
    /// happening in). Raided hearts are skipped. Returns an empty list if none.
    /// </summary>
    public static List<Entity> GetOtherHeartsOwnedBy(ulong platformId, Entity excludeHeart)
    {
        EnsureFresh();

        var result = new List<Entity>();
        if (!_heartsByOwner.TryGetValue(platformId, out var hearts)) return result;

        foreach (var heart in hearts)
        {
            if (heart == excludeHeart) continue;
            if (!EM.Exists(heart)) continue;
            if (IsRaided(heart)) continue;
            result.Add(heart);
        }
        return result;
    }

    /// <summary>The owner PlatformId of a given castle heart, or 0 if unknown.</summary>
    public static ulong GetOwnerPlatformId(Entity heart)
    {
        if (!EM.Exists(heart) || !EM.HasComponent<CastleHeart>(heart)) return 0;
        var ch = EM.GetComponentData<CastleHeart>(heart);
        var territory = ch.CastleTerritoryEntity;
        if (territory == Entity.Null || !EM.Exists(territory)) return 0;
        var em = EM;
        return GetTerritoryOwnerRequestSystem.GetPlatformId(ref em, territory);
    }

    /// <summary>
    /// Resolve the castle heart entity that a station/inventory <paramref name="target"/>
    /// belongs to, via its CastleHeartConnection. Returns Entity.Null if the target
    /// isn't in a castle (e.g. a dropped bag or a non-castle container).
    /// </summary>
    public static Entity GetHeartOf(Entity target)
    {
        if (!EM.Exists(target) || !EM.HasComponent<CastleHeartConnection>(target)) return Entity.Null;
        var conn = EM.GetComponentData<CastleHeartConnection>(target);
        var heart = conn.CastleHeartEntity.GetEntityOnServer();
        return EM.Exists(heart) ? heart : Entity.Null;
    }

    /// <summary>
    /// Get the owning player's PlatformId from a crafting/inventory target. The
    /// target is typically the PlayerCharacter entity, so we read its User.
    /// Returns 0 if it can't be resolved.
    /// </summary>
    public static ulong GetOwnerPlatformIdFromTarget(Entity target)
    {
        if (!EM.Exists(target)) return 0;
        if (EM.HasComponent<ProjectM.PlayerCharacter>(target))
        {
            var pc = EM.GetComponentData<ProjectM.PlayerCharacter>(target);
            var userEnt = pc.UserEntity;
            if (EM.Exists(userEnt) && EM.HasComponent<ProjectM.Network.User>(userEnt))
                return EM.GetComponentData<ProjectM.Network.User>(userEnt).PlatformId;
        }
        // Fallback: target is a station/container in a castle.
        var heart = GetHeartOf(target);
        return heart != Entity.Null ? GetOwnerPlatformId(heart) : 0;
    }

    /// <summary>
    /// The castle heart at the given world tile, or Entity.Null. Used to find the
    /// castle a crafting character is currently standing in, so we can exclude it
    /// from the "other castles" set (avoid double-counting its inventory).
    /// </summary>
    public static Entity GetHeartAtTile(Unity.Mathematics.int2 tile)
    {
        EnsureFresh();
        // Walk cached hearts and test territory membership.
        foreach (var kv in _heartsByOwner)
        {
            foreach (var heart in kv.Value)
            {
                if (!EM.Exists(heart) || !EM.HasComponent<CastleHeart>(heart)) continue;
                var ch = EM.GetComponentData<CastleHeart>(heart);
                var territory = ch.CastleTerritoryEntity;
                if (territory == Entity.Null || !EM.Exists(territory)) continue;
                if (!EM.HasComponent<CastleTerritory>(territory)) continue;
                var ct = EM.GetComponentData<CastleTerritory>(territory);
                var em = EM;
                var terr = territory;
                if (CastleTerritoryExtensions.IsTileInTerritory(em, tile, ref terr, out ct))
                    return heart;
            }
        }
        return Entity.Null;
    }

    /// <summary>
    /// Given a crafting/inventory target (typically the PlayerCharacter), return
    /// the OTHER castle hearts owned by that player, excluding the castle they're
    /// currently standing in (to avoid double-counting its inventory). Skips
    /// raided hearts. Empty if the player owns no other (usable) castles.
    /// </summary>
    public static List<Entity> GetSiblingHeartsForTarget(Entity target)
    {
        var owner = GetOwnerPlatformIdFromTarget(target);
        if (owner == 0) return new List<Entity>();

        // Determine the current castle to exclude: the one at the character's tile.
        Entity currentHeart = Entity.Null;
        if (EM.Exists(target) && EM.HasComponent<TilePosition>(target))
        {
            var tp = EM.GetComponentData<TilePosition>(target);
            currentHeart = GetHeartAtTile(tp.Tile);
        }

        return GetOtherHeartsOwnedBy(owner, currentHeart);
    }

    /// <summary>The castle heart the character is currently standing in, or Entity.Null.</summary>
    public static Entity GetCurrentHeart(Entity character)
    {
        if (!EM.Exists(character) || !EM.HasComponent<TilePosition>(character)) return Entity.Null;
        var tp = EM.GetComponentData<TilePosition>(character);
        return GetHeartAtTile(tp.Tile);
    }

    private static bool IsRaided(Entity heart)
    {
        if (!EM.HasComponent<CastleHeart>(heart)) return true;
        var ch = EM.GetComponentData<CastleHeart>(heart);
        // Any active event >= Attacked means the castle is under raid; don't
        // touch it as a source of resources.
        return ch.ActiveEvent >= CastleHeartEvent.Attacked;
    }

    private static void EnsureFresh()
    {
        var now = UnityEngine.Time.realtimeSinceStartupAsDouble;
        if (now - _lastBuild < CacheTtlSeconds && _heartsByOwner.Count > 0) return;
        Rebuild();
        _lastBuild = now;
    }

    private static void Rebuild()
    {
        _heartsByOwner.Clear();

        var query = EM.CreateEntityQuery(ComponentType.ReadOnly<CastleHeart>());
        var hearts = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var heart in hearts)
            {
                var owner = GetOwnerPlatformId(heart);
                if (owner == 0) continue;
                if (!_heartsByOwner.TryGetValue(owner, out var list))
                {
                    list = new List<Entity>();
                    _heartsByOwner[owner] = list;
                }
                list.Add(heart);
            }
        }
        finally
        {
            hearts.Dispose();
        }
    }
}
