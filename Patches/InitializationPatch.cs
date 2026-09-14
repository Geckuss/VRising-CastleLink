using HarmonyLib;
using ProjectM;

namespace CastleLink.Patches;

/// <summary>
/// One-shot initializer. SpawnTeamSystem_OnPersistenceLoad.OnUpdate runs once the
/// server has loaded its save (world + entities exist), which is when we can
/// safely resolve the ECS world. After first run we mark Core initialized.
/// </summary>
[HarmonyPatch(typeof(SpawnTeamSystem_OnPersistenceLoad), nameof(SpawnTeamSystem_OnPersistenceLoad.OnUpdate))]
internal static class InitializationPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (Core.Initialized) return;
        try
        {
            Core.Initialize();
        }
        catch (System.Exception ex)
        {
            Core.Log?.LogError($"CastleLink Core.Initialize failed: {ex}");
        }
    }
}
