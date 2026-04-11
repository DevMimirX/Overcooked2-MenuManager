using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;
using HostUtilities;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private static bool RunPreparedBootstrapStep()
        {
            switch (preparedSourceBootstrapStage)
            {
                case 0:
                    RegisterPreparedSources(FindObjectsSafe<ClientPlate>());
                    break;
                case 1:
                    RegisterPreparedSources(FindObjectsSafe<ClientCookableContainer>());
                    break;
                case 2:
                    RegisterPreparedSources(FindObjectsSafe<ClientPreparationContainer>());
                    break;
                case 3:
                    RegisterPreparedSources(FindObjectsSafe<ClientItemContainer>());
                    break;
                case 4:
                    RegisterPreparedSources(FindObjectsSafe<ClientLadleContainer>());
                    break;
                case 5:
                    RegisterPreparedSources(FindObjectsSafe<ClientMixableContainer>());
                    break;
                case 6:
                    RegisterPreparedSourcesFromCarriers(FindObjectsSafe<ClientPlayerAttachmentCarrier>());
                    break;
                default:
                    PrunePreparedSources();
                    preparedSourceBootstrapStage = 0;
                    return true;
            }

            preparedSourceBootstrapStage++;
            return false;
        }

        private static T[] FindObjectsSafe<T>() where T : Component
        {
            try
            {
                return UnityEngine.Object.FindObjectsOfType<T>();
            }
            catch
            {
                return new T[0];
            }
        }

        private static void RegisterPreparedSources<T>(T[] components) where T : Component
        {
            if (components == null)
            {
                return;
            }

            for (int i = 0; i < components.Length; i++)
            {
                RegisterPreparedSource(components[i]);
            }
        }

        private static void RegisterPreparedSourcesFromCarriers(ClientPlayerAttachmentCarrier[] carriers)
        {
            if (carriers == null)
            {
                return;
            }

            for (int i = 0; i < carriers.Length; i++)
            {
                TryRegisterPreparedSourcesFromCarrier(carriers[i]);
            }
        }

        private static void TryRegisterPreparedSource(Component component)
        {
            if (!IsPreparedTrackingEnabled() || enabled == null || !enabled.Value || component == null || !IsInActiveRound())
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null || scene.OrderedRecipes.Count == 0 || !HasAnyTrackedRecipes(scene))
            {
                return;
            }

            if (!string.Equals(preparedSourceSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                ClearPreparedState();
                preparedSourceSceneName = scene.SceneName;
            }

            RegisterPreparedSource(component);
        }

        private static void TryRegisterPreparedSourceFromGameObject(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            Component bestSource = null;
            int bestPriority = int.MaxValue;
            IClientOrderDefinition[] recursiveProviders = gameObject.RequestInterfacesRecursive<IClientOrderDefinition>();
            for (int i = 0; i < recursiveProviders.Length; i++)
            {
                ConsiderPreparedSourceCandidate(recursiveProviders[i] as Component, ref bestSource, ref bestPriority);
            }

            Transform parent = gameObject.transform.parent;
            while (parent != null)
            {
                ConsiderPreparedSourceCandidate(parent.gameObject.RequestInterface<IClientOrderDefinition>() as Component, ref bestSource, ref bestPriority);
                parent = parent.parent;
            }

            if (bestSource != null)
            {
                TryRegisterPreparedSource(bestSource);
            }
        }

        private static void ConsiderPreparedSourceCandidate(Component component, ref Component bestSource, ref int bestPriority)
        {
            if (component == null || !(component is IClientOrderDefinition))
            {
                return;
            }

            int priority = GetPreparedSourcePriority(component);
            if (priority >= bestPriority)
            {
                return;
            }

            bestPriority = priority;
            bestSource = component;
        }

        private static void TryRegisterPreparedSourcesFromCarrier(ClientPlayerAttachmentCarrier carrier)
        {
            if (carrier == null)
            {
                return;
            }

            TryRegisterPreparedSourceFromGameObject(carrier.InspectCarriedItem());
            TryRegisterPreparedSourceFromGameObject(carrier.InspectCarriedItem(PlayerAttachTarget.Back));
        }

        private static void RegisterPreparedSource(Component component)
        {
            IClientOrderDefinition provider = component as IClientOrderDefinition;
            if (component == null || provider == null || component.gameObject == null)
            {
                return;
            }

            int instanceId = component.GetInstanceID();
            if (PreparedSourcesByInstanceId.ContainsKey(instanceId))
            {
                return;
            }

            int gameObjectInstanceId = component.gameObject.GetInstanceID();
            int existingSourceId;
            if (PreparedSourceIdsByGameObjectId.TryGetValue(gameObjectInstanceId, out existingSourceId))
            {
                PreparedSourceState existingSource;
                if (!PreparedSourcesByInstanceId.TryGetValue(existingSourceId, out existingSource) || existingSource == null)
                {
                    PreparedSourceIdsByGameObjectId.Remove(gameObjectInstanceId);
                }
                else if (GetPreparedSourcePriority(existingSource.Component) <= GetPreparedSourcePriority(component))
                {
                    return;
                }
                else
                {
                    RemovePreparedSource(existingSourceId);
                }
            }

            PreparedSourceState source = new PreparedSourceState();
            source.InstanceId = instanceId;
            source.GameObjectInstanceId = gameObjectInstanceId;
            source.Component = component;
            source.Provider = provider;
            source.PendingRemoval = false;
            source.RemovalGraceUntilFrame = 0;
            source.Callback = delegate
            {
                QueuePreparedSourceRefresh(instanceId);
            };

            PreparedSourcesByInstanceId[instanceId] = source;
            PreparedSourceIdsByGameObjectId[gameObjectInstanceId] = instanceId;

            try
            {
                provider.RegisterOrderCompositionChangedCallback(source.Callback);
            }
            catch
            {
            }

            QueuePreparedSourceRefresh(instanceId);
        }

        private static int GetPreparedSourcePriority(Component component)
        {
            if (component is ClientPlate)
            {
                return 0;
            }
            if (component is ClientCookablePreparationContainer)
            {
                return 1;
            }
            if (component is ClientCookableContainer)
            {
                return 1;
            }
            if (component is ClientPreparationContainer)
            {
                return 2;
            }
            if (component is ClientItemContainer)
            {
                return 3;
            }
            if (component is ClientLadleContainer)
            {
                return 4;
            }
            if (component is ClientMixableContainer)
            {
                return 5;
            }
            if (component is AssignableOrderDefinition)
            {
                return 6;
            }
            if (component is ItemPropertiesComponent)
            {
                return 7;
            }
            if (component is IngredientPropertiesComponent)
            {
                return 8;
            }

            return 100;
        }

        private static bool TryGetPreparedSceneInfo(out SceneInfo scene)
        {
            scene = null;
            if (!string.IsNullOrEmpty(preparedSourceSceneName) && SceneCache.TryGetValue(preparedSourceSceneName, out scene) && scene != null)
            {
                return true;
            }

            if (TryGetCurrentSceneInfo(out scene) && scene != null)
            {
                preparedSourceSceneName = scene.SceneName;
                return true;
            }

            scene = null;
            return false;
        }

        private static void QueuePreparedSourceRefresh(int instanceId)
        {
            QueuePreparedSourceRefresh(instanceId, PreparedSourceRefreshIntervalFrames);
        }

        private static void QueuePreparedSourceRefresh(int instanceId, int delayFrames)
        {
            if (instanceId != 0)
            {
                DirtyPreparedSourceIds.Add(instanceId);
                int safeDelayFrames = Math.Max(0, delayFrames);
                int targetFrame = Time.frameCount + safeDelayFrames;
                if (nextPreparedSourceRefreshFrame == 0 || targetFrame < nextPreparedSourceRefreshFrame)
                {
                    nextPreparedSourceRefreshFrame = targetFrame;
                }
            }
        }

        private static void RegisterPreparedCookingHandler(Component sourceComponent)
        {
            if (sourceComponent == null || sourceComponent.gameObject == null)
            {
                return;
            }

            ClientCookingHandler cookingHandler = sourceComponent.gameObject.GetComponent<ClientCookingHandler>();
            if (cookingHandler == null)
            {
                return;
            }

            PreparedSourceComponentByHandlerId[cookingHandler.GetInstanceID()] = sourceComponent;
        }

        private static void QueueCookablePreparedSourceRefresh(Component sourceComponent)
        {
            if (sourceComponent == null || !enabled.Value || !IsPreparedTrackingEnabled())
            {
                return;
            }

            int instanceId = sourceComponent.GetInstanceID();
            ClientCookingHandler cookingHandler = sourceComponent.gameObject != null
                ? sourceComponent.gameObject.GetComponent<ClientCookingHandler>()
                : null;
            if (cookingHandler == null)
            {
                TryRegisterPreparedSource(sourceComponent);
                QueuePreparedSourceRefresh(instanceId, 2);
                return;
            }

            CookedCompositeOrderNode.CookingProgress nextState = cookingHandler.GetCookedOrderState();
            CookedCompositeOrderNode.CookingProgress previousState;
            bool hasPreviousState = PreparedCookStateBySourceId.TryGetValue(instanceId, out previousState);
            if (hasPreviousState && previousState == nextState)
            {
                return;
            }

            PreparedCookStateBySourceId[instanceId] = nextState;
            if (!hasPreviousState && nextState == CookedCompositeOrderNode.CookingProgress.Raw)
            {
                return;
            }

            TryRegisterPreparedSource(sourceComponent);
            QueuePreparedSourceRefresh(instanceId, 2);
        }

        private static Component GetPreparedCookingSource(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            ClientCookablePreparationContainer cookablePreparationContainer = gameObject.GetComponent<ClientCookablePreparationContainer>();
            if (cookablePreparationContainer != null)
            {
                return cookablePreparationContainer;
            }

            ClientCookableContainer cookableContainer = gameObject.GetComponent<ClientCookableContainer>();
            if (cookableContainer != null)
            {
                return cookableContainer;
            }

            return null;
        }

        private static void RefreshDirtyPreparedSources(int maxCount)
        {
            if (DirtyPreparedSourceIds.Count == 0)
            {
                return;
            }

            PreparedSourceRefreshBuffer.Clear();
            foreach (int instanceId in DirtyPreparedSourceIds)
            {
                PreparedSourceRefreshBuffer.Add(instanceId);
                if (PreparedSourceRefreshBuffer.Count >= maxCount)
                {
                    break;
                }
            }

            for (int i = 0; i < PreparedSourceRefreshBuffer.Count; i++)
            {
                int instanceId = PreparedSourceRefreshBuffer[i];
                DirtyPreparedSourceIds.Remove(instanceId);
                RefreshPreparedSource(instanceId);
            }
        }

        private static void RefreshPreparedSource(int instanceId)
        {
            PreparedSourceState source;
            if (!PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) || source == null)
            {
                return;
            }

            source.PendingRemoval = false;
            source.RemovalGraceUntilFrame = 0;

            if (source.Component == null || source.Provider == null || source.Component.gameObject == null || !source.Component.gameObject.activeInHierarchy)
            {
                RemovePreparedSource(instanceId);
                return;
            }

            SceneInfo scene;
            if (!TryGetPreparedSceneInfo(out scene))
            {
                SetPreparedSourceMatch(source, 0);
                return;
            }

            try
            {
                AssembledDefinitionNode composition = source.Provider.GetOrderComposition();
                bool isCookedSource = RequiresCookedPreparedState(source.Component);
                if (isCookedSource && !IsPreparedCookingSourceCooked(source.Component, composition))
                {
                    SetPreparedSourceMatch(source, 0);
                    return;
                }

                int matchedRecipeId = MatchPreparedRecipe(scene, composition, isCookedSource);
                SetPreparedSourceMatch(source, matchedRecipeId);
            }
            catch
            {
                SetPreparedSourceMatch(source, 0);
            }
        }

        private static bool RequiresCookedPreparedState(Component component)
        {
            return component != null
                && component.gameObject != null
                && component.gameObject.GetComponent<ClientCookingHandler>() != null;
        }

        private static bool IsPreparedCookingSourceCooked(Component component, AssembledDefinitionNode composition)
        {
            if (component == null)
            {
                return false;
            }

            GameObject gameObject = component.gameObject;
            ClientCookingHandler cookingHandler = gameObject != null ? gameObject.GetComponent<ClientCookingHandler>() : null;
            if (cookingHandler != null)
            {
                return cookingHandler.GetCookedOrderState() == CookedCompositeOrderNode.CookingProgress.Cooked;
            }

            CookedCompositeAssembledNode cookedNode = composition as CookedCompositeAssembledNode;
            return cookedNode != null && cookedNode.m_progress == CookedCompositeOrderNode.CookingProgress.Cooked;
        }

        private static int MatchPreparedRecipe(SceneInfo scene, AssembledDefinitionNode composition, bool allowCookedFallback)
        {
            if (scene == null || composition == null)
            {
                return 0;
            }

            List<int> candidateRecipeIds = GetPreparedCandidateRecipeIds(scene);
            if (candidateRecipeIds == null || candidateRecipeIds.Count == 0)
            {
                return 0;
            }

            AssembledDefinitionNode simplifiedComposition = null;
            AssembledDefinitionNode unwrappedSimplifiedComposition = null;
            bool cookedFallbackInitialized = false;
            for (int i = 0; i < candidateRecipeIds.Count; i++)
            {
                RecipeInfo recipe;
                if (!scene.RecipesById.TryGetValue(candidateRecipeIds[i], out recipe) || recipe == null || recipe.Definition == null)
                {
                    continue;
                }

                if (AssembledDefinitionNode.Matching(composition, recipe.Definition))
                {
                    return recipe.Id;
                }

                if (!allowCookedFallback)
                {
                    continue;
                }

                if (!cookedFallbackInitialized)
                {
                    simplifiedComposition = SafeSimplifyNode(composition);
                    unwrappedSimplifiedComposition = simplifiedComposition != null
                        ? UnwrapCookedCompositeNode(simplifiedComposition)
                        : null;
                    cookedFallbackInitialized = true;
                }

                if (simplifiedComposition == null || ReferenceEquals(unwrappedSimplifiedComposition, simplifiedComposition))
                {
                    continue;
                }

                AssembledDefinitionNode simplifiedDefinition = GetSimplifiedPreparedRecipeDefinition(recipe);
                if (simplifiedDefinition != null
                    && AssembledDefinitionNode.MatchingAlreadySimple(unwrappedSimplifiedComposition, simplifiedDefinition))
                {
                    return recipe.Id;
                }

                AssembledDefinitionNode unwrappedSimplifiedDefinition = GetUnwrappedPreparedRecipeDefinition(recipe);
                if (unwrappedSimplifiedDefinition != null
                    && !ReferenceEquals(unwrappedSimplifiedDefinition, simplifiedDefinition)
                    && AssembledDefinitionNode.MatchingAlreadySimple(unwrappedSimplifiedComposition, unwrappedSimplifiedDefinition))
                {
                    return recipe.Id;
                }
            }

            return 0;
        }

        private static AssembledDefinitionNode GetSimplifiedPreparedRecipeDefinition(RecipeInfo recipe)
        {
            if (recipe == null)
            {
                return null;
            }

            if (recipe.SimplifiedDefinition == null && recipe.Definition != null)
            {
                recipe.SimplifiedDefinition = SafeSimplifyNode(recipe.Definition);
            }

            return recipe.SimplifiedDefinition;
        }

        private static AssembledDefinitionNode GetUnwrappedPreparedRecipeDefinition(RecipeInfo recipe)
        {
            if (recipe == null)
            {
                return null;
            }

            if (recipe.SimplifiedUnwrappedDefinition == null)
            {
                AssembledDefinitionNode simplifiedDefinition = GetSimplifiedPreparedRecipeDefinition(recipe);
                recipe.SimplifiedUnwrappedDefinition = simplifiedDefinition != null
                    ? UnwrapCookedCompositeNode(simplifiedDefinition)
                    : null;
            }

            return recipe.SimplifiedUnwrappedDefinition;
        }

        private static AssembledDefinitionNode SafeSimplifyNode(object node)
        {
            if (node == null)
            {
                return null;
            }

            try
            {
                OrderDefinitionNode definition = node as OrderDefinitionNode;
                if (definition != null)
                {
                    return definition.Simpilfy();
                }

                AssembledDefinitionNode assembledNode = node as AssembledDefinitionNode;
                return assembledNode != null ? assembledNode.Simpilfy() : null;
            }
            catch
            {
                return null;
            }
        }

        private static AssembledDefinitionNode UnwrapCookedCompositeNode(AssembledDefinitionNode node)
        {
            CookedCompositeAssembledNode cookedNode = node as CookedCompositeAssembledNode;
            if (cookedNode == null || cookedNode.m_progress != CookedCompositeOrderNode.CookingProgress.Cooked)
            {
                return node;
            }

            CompositeAssembledNode unwrappedNode = new CompositeAssembledNode();
            unwrappedNode.m_freeObject = cookedNode.m_freeObject;
            unwrappedNode.m_permittedEntries = cookedNode.m_permittedEntries;
            unwrappedNode.m_composition = cookedNode.m_composition ?? new AssembledDefinitionNode[0];
            unwrappedNode.m_optional = cookedNode.m_optional ?? new AssembledDefinitionNode[0];
            return unwrappedNode.Simpilfy();
        }

        private static List<int> GetPreparedCandidateRecipeIds(SceneInfo scene)
        {
            if (scene == null)
            {
                PreparedCandidateRecipeIdsBuffer.Clear();
                return PreparedCandidateRecipeIdsBuffer;
            }

            if (!preparedCandidateRecipeIdsDirty
                && string.Equals(preparedCandidateSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                return PreparedCandidateRecipeIdsBuffer;
            }

            preparedCandidateSceneName = scene.SceneName;
            preparedCandidateRecipeIdsDirty = false;
            PreparedCandidateRecipeIdsBuffer.Clear();

            HashSet<int> trackedIds;
            bool hasExplicitTrackedIds = TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds) && trackedIds != null;
            if (hasExplicitTrackedIds && trackedIds.Count == 0)
            {
                return PreparedCandidateRecipeIdsBuffer;
            }

            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            Dictionary<int, double> probabilityByRecipeId = GetProbabilityMap(scene, EnsureRun(scene));
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null)
                {
                    continue;
                }

                if (hasExplicitTrackedIds && !trackedIds.Contains(recipe.Id))
                {
                    continue;
                }

                if (GetCount(currentMenuCounts, recipe.Id) > 0 || GetProbability(probabilityByRecipeId, recipe.Id) > 0d)
                {
                    PreparedCandidateRecipeIdsBuffer.Add(recipe.Id);
                }
            }

            return PreparedCandidateRecipeIdsBuffer;
        }

        private static void SetPreparedSourceMatch(PreparedSourceState source, int matchedRecipeId)
        {
            if (source == null || source.MatchedRecipeId == matchedRecipeId)
            {
                return;
            }

            if (source.MatchedRecipeId != 0)
            {
                AdjustPreparedCount(source.MatchedRecipeId, -1);
            }

            source.MatchedRecipeId = matchedRecipeId;
            if (matchedRecipeId != 0)
            {
                bool consumedPendingTransfer = ConsumePendingPreparedTransfer(matchedRecipeId, source.InstanceId);
                if (!consumedPendingTransfer)
                {
                    AdjustPreparedCount(matchedRecipeId, 1);
                }
            }

            InvalidateOverlay();
            InvalidateReferenceTickets();
            InvalidateTicketWidgets();
        }

        private static bool ConsumePendingPreparedTransfer(int matchedRecipeId, int targetInstanceId)
        {
            if (matchedRecipeId == 0)
            {
                return false;
            }

            int pendingSourceId = 0;
            PreparedSourceState pendingSourceState = null;
            foreach (KeyValuePair<int, PreparedSourceState> pair in PreparedSourcesByInstanceId)
            {
                PreparedSourceState pendingSource = pair.Value;
                if (pendingSource == null
                    || pair.Key == targetInstanceId
                    || !pendingSource.PendingRemoval
                    || pendingSource.MatchedRecipeId != matchedRecipeId)
                {
                    continue;
                }

                pendingSourceId = pair.Key;
                pendingSourceState = pendingSource;
                break;
            }

            if (pendingSourceState == null)
            {
                return false;
            }

            PreparedCookStateBySourceId.Remove(pendingSourceId);
            DirtyPreparedSourceIds.Remove(pendingSourceId);
            if (pendingSourceState.GameObjectInstanceId != 0)
            {
                PreparedSourceIdsByGameObjectId.Remove(pendingSourceState.GameObjectInstanceId);
            }

            if (pendingSourceState.Provider != null && pendingSourceState.Callback != null)
            {
                try
                {
                    pendingSourceState.Provider.UnregisterOrderCompositionChangedCallback(pendingSourceState.Callback);
                }
                catch
                {
                }
            }

            PreparedSourcesByInstanceId.Remove(pendingSourceId);
            return true;
        }

        private static void AdjustPreparedCount(int recipeId, int delta)
        {
            int nextValue = Math.Max(0, GetCount(PreparedCountsByRecipe, recipeId) + delta);
            if (nextValue > 0)
            {
                PreparedCountsByRecipe[recipeId] = nextValue;
            }
            else
            {
                PreparedCountsByRecipe.Remove(recipeId);
            }
        }

        private static void RemovePreparedSource(int instanceId)
        {
            PreparedSourceState source;
            if (!PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) || source == null)
            {
                return;
            }

            if (source.MatchedRecipeId != 0 && !source.PendingRemoval)
            {
                source.PendingRemoval = true;
                source.RemovalGraceUntilFrame = Time.frameCount + PreparedSourceRemovalGraceFrames;
                int targetFrame = source.RemovalGraceUntilFrame;
                if (nextPreparedSourcePruneFrame == 0 || targetFrame < nextPreparedSourcePruneFrame)
                {
                    nextPreparedSourcePruneFrame = targetFrame;
                }
                return;
            }

            PreparedCookStateBySourceId.Remove(instanceId);

            if (source.Provider != null && source.Callback != null)
            {
                try
                {
                    source.Provider.UnregisterOrderCompositionChangedCallback(source.Callback);
                }
                catch
                {
                }
            }

            if (source.MatchedRecipeId != 0)
            {
                AdjustPreparedCount(source.MatchedRecipeId, -1);
                InvalidateOverlay();
                InvalidateReferenceTickets();
                InvalidateTicketWidgets();
            }

            PreparedSourcesByInstanceId.Remove(instanceId);
            DirtyPreparedSourceIds.Remove(instanceId);
            if (source.GameObjectInstanceId != 0)
            {
                PreparedSourceIdsByGameObjectId.Remove(source.GameObjectInstanceId);
            }
        }

        private static void PrunePreparedSources()
        {
            PreparedSourceRemovalBuffer.Clear();
            foreach (KeyValuePair<int, PreparedSourceState> pair in PreparedSourcesByInstanceId)
            {
                PreparedSourceState source = pair.Value;
                if (source == null)
                {
                    PreparedSourceRemovalBuffer.Add(pair.Key);
                    continue;
                }

                if (source.PendingRemoval)
                {
                    if (Time.frameCount >= source.RemovalGraceUntilFrame)
                    {
                        PreparedSourceRemovalBuffer.Add(pair.Key);
                    }

                    continue;
                }

                if (source.Component == null || source.Component.gameObject == null || !source.Component.gameObject.activeInHierarchy)
                {
                    PreparedSourceRemovalBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PreparedSourceRemovalBuffer.Count; i++)
            {
                RemovePreparedSource(PreparedSourceRemovalBuffer[i]);
            }
        }

        [HarmonyPatch(typeof(ClientPlate), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPlate_StartSynchronising_Postfix(ClientPlate __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientCookableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientCookableContainer_StartSynchronising_Postfix(ClientCookableContainer __instance)
        {
            RegisterPreparedCookingHandler(__instance);
            TryRegisterPreparedSource(__instance);
            QueueCookablePreparedSourceRefresh(__instance);
        }

        [HarmonyPatch(typeof(ClientCookablePreparationContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientCookablePreparationContainer_StartSynchronising_Postfix(ClientCookablePreparationContainer __instance)
        {
            RegisterPreparedCookingHandler(__instance);
            TryRegisterPreparedSource(__instance);
            QueueCookablePreparedSourceRefresh(__instance);
        }

        [HarmonyPatch(typeof(ClientCookingHandler), "ApplyServerUpdate")]
        [HarmonyPostfix]
        private static void ClientCookingHandler_ApplyServerUpdate_Postfix(ClientCookingHandler __instance)
        {
            if (__instance == null)
            {
                return;
            }

            Component sourceComponent;
            if (!PreparedSourceComponentByHandlerId.TryGetValue(__instance.GetInstanceID(), out sourceComponent) || sourceComponent == null)
            {
                if (__instance.gameObject == null)
                {
                    return;
                }

                sourceComponent = GetPreparedCookingSource(__instance.gameObject);
                if (sourceComponent == null)
                {
                    return;
                }

                RegisterPreparedCookingHandler(sourceComponent);
            }

            QueueCookablePreparedSourceRefresh(sourceComponent);
        }

        [HarmonyPatch(typeof(ClientPreparationContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPreparationContainer_StartSynchronising_Postfix(ClientPreparationContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientItemContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientItemContainer_StartSynchronising_Postfix(ClientItemContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientLadleContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientLadleContainer_StartSynchronising_Postfix(ClientLadleContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientMixableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientMixableContainer_StartSynchronising_Postfix(ClientMixableContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_StartSynchronising_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            TryRegisterPreparedSourcesFromCarrier(__instance);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "CarryItem")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_CarryItem_Postfix(GameObject _object)
        {
            TryRegisterPreparedSourceFromGameObject(_object);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "ApplyServerEvent")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_ApplyServerEvent_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            TryRegisterPreparedSourcesFromCarrier(__instance);
        }

        [HarmonyPatch(typeof(ClientSynchroniserBase), "OnDestroy")]
        [HarmonyPrefix]
        private static void ClientSynchroniserBase_OnDestroy_Prefix(ClientSynchroniserBase __instance)
        {
            Component component = __instance;
            if (component == null)
            {
                return;
            }

            ClientCookingHandler cookingHandler = component as ClientCookingHandler;
            if (cookingHandler != null)
            {
                PreparedSourceComponentByHandlerId.Remove(cookingHandler.GetInstanceID());
            }

            ClientCookableContainer cookableContainer = component as ClientCookableContainer;
            if (cookableContainer != null)
            {
                ClientCookingHandler linkedCookingHandler = cookableContainer.GetCookingHandler();
                if (linkedCookingHandler != null)
                {
                    PreparedSourceComponentByHandlerId.Remove(linkedCookingHandler.GetInstanceID());
                }
            }

            ClientCookablePreparationContainer cookablePreparationContainer = component as ClientCookablePreparationContainer;
            if (cookablePreparationContainer != null && component.gameObject != null)
            {
                ClientCookingHandler linkedCookingHandler = component.gameObject.GetComponent<ClientCookingHandler>();
                if (linkedCookingHandler != null)
                {
                    PreparedSourceComponentByHandlerId.Remove(linkedCookingHandler.GetInstanceID());
                }
            }

            int instanceId = component.GetInstanceID();
            if (PreparedSourcesByInstanceId.ContainsKey(instanceId))
            {
                RemovePreparedSource(instanceId);
            }
        }

    }
}
