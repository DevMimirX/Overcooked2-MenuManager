// Merges optional runtime recipe catalogs after their owning mods finish round
// synchronization. Recipe Extension refreshes establish the ordered snapshot
// reused by probability and Carnival consumers for the rest of the round.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        [HarmonyPatch(typeof(ServerFlowControllerBase), "StartSynchronising")]
        [HarmonyPostfix]
        [HarmonyAfter(OptionalRecipeAdapters.ManyRecipesPluginGuid)]
        private static void ServerFlowControllerBase_StartSynchronising_RecipeCatalog_Postfix(ServerFlowControllerBase __instance)
        {
            try
            {
                RefreshRuntimeRecipeExtensions(__instance != null ? __instance.GetLevelConfig() : null);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("refreshing the server recipe catalog", ex);
            }
        }

        [HarmonyPatch(typeof(ClientFlowControllerBase), "StartSynchronising")]
        [HarmonyPostfix]
        [HarmonyAfter(OptionalRecipeAdapters.ManyRecipesPluginGuid)]
        private static void ClientFlowControllerBase_StartSynchronising_RecipeCatalog_Postfix(ClientFlowControllerBase __instance)
        {
            try
            {
                if (__instance != null)
                {
                    cachedClientFlowController = __instance;
                    nextClientFlowLookupFrame = Time.frameCount + ControllerLookupRetryIntervalFrames;
                    ClientKitchenFlowControllerBase kitchenFlow = __instance as ClientKitchenFlowControllerBase;
                    if (kitchenFlow != null)
                    {
                        cachedKitchenFlowController = kitchenFlow;
                        nextKitchenFlowLookupFrame = Time.frameCount + ControllerLookupRetryIntervalFrames;
                    }
                }

                LevelConfigBase levelConfig = __instance != null ? __instance.GetLevelConfig() : null;
                RefreshRuntimeRecipeExtensions(levelConfig);
                SeedProbabilityReconstruction(levelConfig);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("refreshing the client recipe catalog", ex);
            }
        }

        private static void SeedProbabilityReconstruction(LevelConfigBase levelConfig)
        {
            if (levelConfig == null
                || enabled == null
                || !enabled.Value
                || NoMenuMode.IsActiveForRound)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null)
            {
                return;
            }

            for (int i = 0; i < SupportedTeamIds.Length; i++)
            {
                TeamID teamId = SupportedTeamIds[i];
                ReconstructionReadyTeams.Add(teamId);
                RunInfo run = EnsureRun(scene, teamId);
                run.ReconstructionComplete = true;
            }
        }

        internal static void RefreshRuntimeRecipeExtensions(LevelConfigBase levelConfig)
        {
            if (levelConfig == null)
            {
                return;
            }

            bool extensionAdapterAvailable = OptionalRecipeAdapters.TryGetManyRecipeEntries(RuntimeRecipeEntriesBuffer);

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null)
            {
                SceneDirectoryData.PerPlayerCountDirectoryEntry currentVariant;
                string sceneName = TryGetCurrentSceneVariant(out currentVariant) && currentVariant != null
                    ? currentVariant.SceneName
                    : levelConfig.name;
                if (string.IsNullOrEmpty(sceneName))
                {
                    return;
                }

                if (!SceneCache.TryGetValue(sceneName, out scene) || scene == null)
                {
                    scene = BuildSceneInfo(sceneName, sceneName, levelConfig);
                    if (scene == null)
                    {
                        return;
                    }

                    SceneCache[sceneName] = scene;
                }
            }

            bool changed = MergeLevelConfigIntoScene(scene, levelConfig);
            if (!extensionAdapterAvailable)
            {
                RuntimeRecipeIdsBuffer.Clear();
                changed |= UpdateRecipeSourceIds(scene, scene.ExtensionRecipeIds, RuntimeRecipeIdsBuffer);
                RuntimePhaseRecipeEntriesBuffer.Clear();
                RuntimeRecipeEntriesBuffer.Clear();
                if (changed)
                {
                    NotifyRecipeCatalogChanged(scene);
                }
                return;
            }

            string configName = !string.IsNullOrEmpty(levelConfig.name) ? levelConfig.name : scene.LevelConfigName;
            RuntimeRecipeIdsBuffer.Clear();
            if (scene.PhaseRecipeIds != null && scene.PhaseRecipeIds.Length > 0)
            {
                for (int phaseIndex = 0; phaseIndex < scene.PhaseRecipeIds.Length; phaseIndex++)
                {
                    RuntimePhaseRecipeEntriesBuffer.Clear();
                    OptionalRecipeAdapters.AppendManyRecipeEntriesFromSnapshot(
                        RuntimePhaseRecipeEntriesBuffer,
                        RuntimeRecipeEntriesBuffer,
                        configName,
                        phaseIndex,
                        false);
                    List<int> phaseRecipeIds = scene.PhaseRecipeIds[phaseIndex];
                    if (phaseRecipeIds == null)
                    {
                        phaseRecipeIds = new List<int>();
                        scene.PhaseRecipeIds[phaseIndex] = phaseRecipeIds;
                    }

                    for (int i = 0; i < RuntimePhaseRecipeEntriesBuffer.Count; i++)
                    {
                        RecipeList.Entry entry = RuntimePhaseRecipeEntriesBuffer[i];
                        if (entry == null || entry.m_order == null)
                        {
                            continue;
                        }

                        changed |= EnsureRecipe(scene, entry.m_order);
                        int recipeId = entry.m_order.m_uID;
                        RuntimeRecipeIdsBuffer.Add(recipeId);
                        if (!phaseRecipeIds.Contains(recipeId))
                        {
                            phaseRecipeIds.Add(recipeId);
                            changed = true;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < RuntimeRecipeEntriesBuffer.Count; i++)
                {
                    RecipeList.Entry entry = RuntimeRecipeEntriesBuffer[i];
                    if (entry != null && entry.m_order != null)
                    {
                        changed |= EnsureRecipe(scene, entry.m_order);
                        RuntimeRecipeIdsBuffer.Add(entry.m_order.m_uID);
                    }
                }
            }

            changed |= UpdateRecipeSourceIds(scene, scene.ExtensionRecipeIds, RuntimeRecipeIdsBuffer);
            RuntimePhaseRecipeEntriesBuffer.Clear();
            RuntimeRecipeEntriesBuffer.Clear();

            if (changed)
            {
                NotifyRecipeCatalogChanged(scene);
            }
        }

        internal static void AppendRecipeExtensionEntries(List<RecipeList.Entry> destination, LevelConfigBase levelConfig, bool allPhases, int phaseIndex)
        {
            if (destination == null)
            {
                return;
            }

            OptionalRecipeAdapters.AppendManyRecipeEntries(
                destination,
                levelConfig != null ? levelConfig.name : null,
                phaseIndex,
                allPhases);
        }
    }
}
