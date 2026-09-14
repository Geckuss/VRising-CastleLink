using System;
using BepInEx.Logging;
using Unity.Entities;
using ProjectM;
using ProjectM.Scripting;

namespace CastleLink;

/// <summary>
/// Static service locator. The ECS world does not exist during Plugin.Load(),
/// so this is initialized lazily once the server world is available (see
/// Patches/InitializationPatch).
/// </summary>
internal static class Core
{
    public static ManualLogSource Log { get; set; }

    public static World Server { get; private set; }
    public static EntityManager EntityManager { get; private set; }
    public static ServerGameManager ServerGameManager => Server.GetExistingSystemManaged<ServerScriptMapper>().GetServerGameManager();
    public static PrefabCollectionSystem PrefabCollectionSystem { get; private set; }
    public static GameDataSystem GameDataSystem { get; private set; }

    public static bool Initialized { get; private set; }

    public static void Initialize()
    {
        if (Initialized) return;

        Server = GetServerWorld() ?? throw new Exception("CastleLink: server World not found");
        EntityManager = Server.EntityManager;
        PrefabCollectionSystem = Server.GetExistingSystemManaged<PrefabCollectionSystem>();
        GameDataSystem = Server.GetExistingSystemManaged<GameDataSystem>();

        Initialized = true;
        Log?.LogInfo("CastleLink Core initialized (server world resolved).");
    }

    private static World GetServerWorld()
    {
        foreach (var world in World.s_AllWorlds)
        {
            if (world.Name == "Server") return world;
        }
        return null;
    }
}
