// Tracks completed dishes through event-registered container sources. Each
// physical source owns one canonical count plus every base-game-compatible ID
// used for tinting. Assembled composition is authoritative for dual-purpose
// utensils; cooking-wrapper fallback never discards step or progress.
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
using OC2MenuManager.Infrastructure;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;

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
            int existingSourceId = 0;
            if (PreparedSourceIdsByGameObjectId.TryGetValue(gameObjectInstanceId, out existingSourceId))
            {
                PreparedSourceState existingSource;
                if (!PreparedSourcesByInstanceId.TryGetValue(existingSourceId, out existingSource) || existingSource == null)
                {
                    PreparedSourceIdsByGameObjectId.Remove(gameObjectInstanceId);
                    existingSourceId = 0;
                }
                else if (GetPreparedSourcePriority(existingSource.Component) <= GetPreparedSourcePriority(component))
                {
                    return;
                }
            }

            PreparedSourceState source = new PreparedSourceState();
            source.InstanceId = instanceId;
            source.GameObjectInstanceId = gameObjectInstanceId;
            source.Component = component;
            source.Provider = provider;
            source.CookingHandler = component.gameObject.GetComponent<ClientCookingHandler>();
            source.PendingRemoval = false;
            source.RemovalGraceUntilFrame = 0;
            source.Callback = delegate
            {
                QueuePreparedSourceRefresh(instanceId);
            };

            try
            {
                provider.RegisterOrderCompositionChangedCallback(source.Callback);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("subscribing to a prepared-source composition", ex);
                return;
            }

            if (existingSourceId != 0)
            {
                RemovePreparedSource(existingSourceId);
            }

            PreparedSourcesByInstanceId[instanceId] = source;
            PreparedSourceIdsByGameObjectId[gameObjectInstanceId] = instanceId;
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
            if (!IsPreparedTrackingEnabled() || sourceComponent == null || sourceComponent.gameObject == null)
            {
                return;
            }

            ClientCookingHandler cookingHandler = sourceComponent.gameObject.GetComponent<ClientCookingHandler>();
            if (cookingHandler == null)
            {
                return;
            }

            PreparedSourceComponentByHandlerId[cookingHandler.GetInstanceID()] = sourceComponent;
            PreparedSourceState source;
            if (PreparedSourcesByInstanceId.TryGetValue(sourceComponent.GetInstanceID(), out source) && source != null)
            {
                source.CookingHandler = cookingHandler;
            }
        }

        private static void QueueCookablePreparedSourceRefresh(Component sourceComponent)
        {
            if (sourceComponent == null
                || enabled == null
                || !enabled.Value
                || !IsPreparedTrackingEnabled())
            {
                return;
            }

            int instanceId = sourceComponent.GetInstanceID();
            PreparedSourceState source;
            ClientCookingHandler cookingHandler = PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) && source != null
                ? source.CookingHandler
                : null;
            if (cookingHandler == null && sourceComponent.gameObject != null)
            {
                cookingHandler = sourceComponent.gameObject.GetComponent<ClientCookingHandler>();
                if (source != null)
                {
                    source.CookingHandler = cookingHandler;
                }
            }
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

            if (source.PendingRemoval && source.GameObjectInstanceId != 0)
            {
                int mappedSourceId;
                if (!PreparedSourceIdsByGameObjectId.TryGetValue(source.GameObjectInstanceId, out mappedSourceId)
                    || mappedSourceId != instanceId)
                {
                    return;
                }
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
                ClearPreparedSourceMatch(source);
                return;
            }

            try
            {
                AssembledDefinitionNode composition = source.Provider.GetOrderComposition();
                bool allowCookedFallback = source.CookingHandler != null;
                int matchedRecipeId = MatchPreparedRecipe(scene, composition, allowCookedFallback, source);
                SetPreparedSourceMatch(
                    source,
                    matchedRecipeId,
                    PreparedMatchedRecipeIdsBuffer,
                    PreparedMatchedRecipeIdsSetBuffer);
            }
            catch
            {
                ClearPreparedSourceMatch(source);
            }
        }

        private static void ClearPreparedSourceMatch(PreparedSourceState source)
        {
            PreparedMatchedRecipeIdsBuffer.Clear();
            PreparedMatchedRecipeIdsSetBuffer.Clear();
            SetPreparedSourceMatch(
                source,
                0,
                PreparedMatchedRecipeIdsBuffer,
                PreparedMatchedRecipeIdsSetBuffer);
        }

        private static int MatchPreparedRecipe(
            SceneInfo scene,
            AssembledDefinitionNode composition,
            bool allowCookedFallback,
            PreparedSourceState source)
        {
            PreparedMatchedRecipeIdsBuffer.Clear();
            PreparedMatchedRecipeIdsSetBuffer.Clear();
            PreparedAssignmentCandidatesBuffer.Clear();
            PreparedTicketPrioritiesByRecipeBuffer.Clear();
            if (scene == null || composition == null)
            {
                return 0;
            }

            List<int> candidateRecipeIds = GetPreparedCandidateRecipeIds(scene);
            if (candidateRecipeIds == null || candidateRecipeIds.Count == 0)
            {
                return 0;
            }

            AssembledDefinitionNode simplifiedComposition = SafeSimplifyNode(composition);
            if (simplifiedComposition == null)
            {
                return 0;
            }

            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            BuildPreparedTicketPriorities(scene);
            AssembledDefinitionNode unwrappedSimplifiedComposition = null;
            bool cookedFallbackInitialized = false;
            for (int i = 0; i < candidateRecipeIds.Count; i++)
            {
                RecipeInfo recipe;
                if (!scene.RecipesById.TryGetValue(candidateRecipeIds[i], out recipe) || recipe == null || recipe.Definition == null)
                {
                    continue;
                }

                AssembledDefinitionNode simplifiedDefinition = GetSimplifiedPreparedRecipeDefinition(recipe);
                bool matches = simplifiedDefinition != null
                    && MatchesPreparedRecipeDefinition(recipe.Definition, simplifiedDefinition, simplifiedComposition);
                if (!matches && allowCookedFallback)
                {
                    CookedCompositeAssembledNode requiredCooked = simplifiedDefinition as CookedCompositeAssembledNode;
                    CookedCompositeAssembledNode providedCooked = simplifiedComposition as CookedCompositeAssembledNode;
                    if (CanUseCookedContainerFallback(requiredCooked, providedCooked))
                    {
                        if (!cookedFallbackInitialized)
                        {
                            unwrappedSimplifiedComposition = UnwrapCookedCompositeNode(simplifiedComposition);
                            cookedFallbackInitialized = true;
                        }

                        AssembledDefinitionNode unwrappedSimplifiedDefinition = GetUnwrappedPreparedRecipeDefinition(recipe);
                        matches = unwrappedSimplifiedComposition != null
                            && unwrappedSimplifiedDefinition != null
                            && !ReferenceEquals(unwrappedSimplifiedComposition, simplifiedComposition)
                            && !ReferenceEquals(unwrappedSimplifiedDefinition, simplifiedDefinition)
                            && MatchesPreparedRecipeDefinition(
                                recipe.Definition,
                                unwrappedSimplifiedDefinition,
                                unwrappedSimplifiedComposition);
                    }
                }

                if (!matches || !PreparedMatchedRecipeIdsSetBuffer.Add(recipe.Id))
                {
                    continue;
                }

                PreparedMatchedRecipeIdsBuffer.Add(recipe.Id);
                PreparedTicketPriority ticketPriority;
                bool hasTicketPriority = PreparedTicketPrioritiesByRecipeBuffer.TryGetValue(recipe.Id, out ticketPriority);
                PreparedAssignmentCandidatesBuffer.Add(new PreparedRecipeAssignmentCandidate(
                    recipe.Id,
                    GetCount(currentMenuCounts, recipe.Id),
                    GetCount(PreparedCountsByRecipe, recipe.Id),
                    source != null && source.MatchedRecipeId == recipe.Id,
                    hasTicketPriority ? ticketPriority.Order : int.MaxValue,
                    hasTicketPriority ? ticketPriority.Team : int.MaxValue,
                    i));
            }

            return PreparedRecipeAssignmentPolicy.SelectCanonical(PreparedAssignmentCandidatesBuffer);
        }

        private static void BuildPreparedTicketPriorities(SceneInfo scene)
        {
            PreparedTicketPrioritiesByRecipeBuffer.Clear();
            if (scene == null)
            {
                return;
            }

            foreach (TicketWidgetState state in TicketWidgetsByInstanceId.Values)
            {
                if (state == null
                    || state.Widget == null
                    || state.IsReferenceTicket
                    || state.Order < 0
                    || !IsTracked(scene, state.RecipeId))
                {
                    continue;
                }

                PreparedTicketPriority candidate = new PreparedTicketPriority
                {
                    Order = state.Order,
                    Team = (int)state.TeamId
                };
                PreparedTicketPriority existing;
                if (!PreparedTicketPrioritiesByRecipeBuffer.TryGetValue(state.RecipeId, out existing)
                    || candidate.Order < existing.Order
                    || (candidate.Order == existing.Order && candidate.Team < existing.Team))
                {
                    PreparedTicketPrioritiesByRecipeBuffer[state.RecipeId] = candidate;
                }
            }
        }

        private static bool CanUseCookedContainerFallback(
            CookedCompositeAssembledNode required,
            CookedCompositeAssembledNode provided)
        {
            return required != null
                && provided != null
                && required.m_cookingStep != null
                && provided.m_cookingStep != null
                && required.m_cookingStep.m_uID == provided.m_cookingStep.m_uID
                && required.m_progress == CookedCompositeOrderNode.CookingProgress.Cooked
                && provided.m_progress == CookedCompositeOrderNode.CookingProgress.Cooked;
        }

        private static bool MatchesPreparedRecipeDefinition(
            OrderDefinitionNode requiredDefinition,
            AssembledDefinitionNode simplifiedRequired,
            AssembledDefinitionNode simplifiedProvided)
        {
            if (requiredDefinition == null || simplifiedRequired == null || simplifiedProvided == null)
            {
                return false;
            }

            return requiredDefinition.GetType() == typeof(WildcardOrderNode)
                ? AssembledDefinitionNode.MatchingAlreadySimple(simplifiedRequired, simplifiedProvided)
                : AssembledDefinitionNode.MatchingAlreadySimple(simplifiedProvided, simplifiedRequired);
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
            List<TeamID> activeTeams = GetActiveTeamIds();
            PreparedCandidateRunsBuffer.Clear();
            PreparedCandidateProbabilityMapsBuffer.Clear();
            PreparedCandidateActiveRecipeIdsBuffer.Clear();
            for (int i = 0; i < activeTeams.Count; i++)
            {
                RunInfo run = EnsureRun(scene, activeTeams[i]);
                Dictionary<int, double> probabilityMap = GetProbabilityMap(scene, run);
                PreparedCandidateRunsBuffer.Add(run);
                PreparedCandidateProbabilityMapsBuffer.Add(probabilityMap);
                PreparedCandidateActiveRecipeIdsBuffer.Add(
                    run.ProbabilityAvailable ? null : GetActiveRecipeIds(scene, run));
            }

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

                bool candidate = GetCount(currentMenuCounts, recipe.Id) > 0;
                for (int teamIndex = 0; !candidate && teamIndex < PreparedCandidateRunsBuffer.Count; teamIndex++)
                {
                    RunInfo run = PreparedCandidateRunsBuffer[teamIndex];
                    if (run.ProbabilityAvailable)
                    {
                        candidate = GetProbability(PreparedCandidateProbabilityMapsBuffer[teamIndex], recipe.Id) > 0d;
                    }
                    else
                    {
                        List<int> activeRecipeIds = PreparedCandidateActiveRecipeIdsBuffer[teamIndex];
                        candidate = activeRecipeIds != null && activeRecipeIds.Contains(recipe.Id);
                    }
                }

                if (candidate)
                {
                    PreparedCandidateRecipeIdsBuffer.Add(recipe.Id);
                }
            }

            return PreparedCandidateRecipeIdsBuffer;
        }

        private static void SetPreparedSourceMatch(
            PreparedSourceState source,
            int matchedRecipeId,
            IList<int> compatibleRecipeIds,
            HashSet<int> compatibleRecipeIdSet)
        {
            if (source == null)
            {
                return;
            }

            bool compatibilityChanged = SynchronizePreparedSourceCompatibility(
                source,
                compatibleRecipeIds,
                compatibleRecipeIdSet);
            int previousMatchedRecipeId = source.MatchedRecipeId;
            bool assignmentChanged = previousMatchedRecipeId != matchedRecipeId;
            if (!assignmentChanged && !compatibilityChanged)
            {
                return;
            }

            if (assignmentChanged)
            {
                if (previousMatchedRecipeId != 0)
                {
                    AdjustPreparedCount(previousMatchedRecipeId, -1);
                }

                source.MatchedRecipeId = matchedRecipeId;
                if (matchedRecipeId != 0)
                {
                    bool consumedPendingTransfer = previousMatchedRecipeId == 0
                        && ConsumePendingPreparedTransfer(source, matchedRecipeId);
                    if (!consumedPendingTransfer)
                    {
                        AdjustPreparedCount(matchedRecipeId, 1);
                    }
                }
            }

            if (assignmentChanged)
            {
                InvalidateOverlay();
                InvalidateReferenceTickets();
            }

            InvalidateTicketWidgets();
        }

        private static bool SynchronizePreparedSourceCompatibility(
            PreparedSourceState source,
            IList<int> compatibleRecipeIds,
            HashSet<int> compatibleRecipeIdSet)
        {
            PreparedCompatibilityRemovalBuffer.Clear();
            foreach (int recipeId in source.CompatibleRecipeIds)
            {
                if (compatibleRecipeIdSet == null || !compatibleRecipeIdSet.Contains(recipeId))
                {
                    PreparedCompatibilityRemovalBuffer.Add(recipeId);
                }
            }

            bool changed = false;
            for (int i = 0; i < PreparedCompatibilityRemovalBuffer.Count; i++)
            {
                int recipeId = PreparedCompatibilityRemovalBuffer[i];
                if (source.CompatibleRecipeIds.Remove(recipeId))
                {
                    AdjustPreparedCompatibilityCount(recipeId, -1);
                    changed = true;
                }
            }

            if (compatibleRecipeIds != null)
            {
                for (int i = 0; i < compatibleRecipeIds.Count; i++)
                {
                    int recipeId = compatibleRecipeIds[i];
                    if (source.CompatibleRecipeIds.Add(recipeId))
                    {
                        AdjustPreparedCompatibilityCount(recipeId, 1);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool ConsumePendingPreparedTransfer(PreparedSourceState targetSource, int matchedRecipeId)
        {
            if (targetSource == null || matchedRecipeId == 0 || targetSource.CompatibleRecipeIds.Count == 0)
            {
                return false;
            }

            int pendingSourceId = 0;
            PreparedSourceState pendingSourceState = null;
            foreach (KeyValuePair<int, PreparedSourceState> pair in PreparedSourcesByInstanceId)
            {
                PreparedSourceState pendingSource = pair.Value;
                if (pendingSource == null
                    || pair.Key == targetSource.InstanceId
                    || !pendingSource.PendingRemoval
                    || pendingSource.MatchedRecipeId == 0
                    || !HaveCompatiblePreparedRecipe(pendingSource, targetSource))
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

            if (pendingSourceState.MatchedRecipeId != matchedRecipeId)
            {
                AdjustPreparedCount(pendingSourceState.MatchedRecipeId, -1);
                AdjustPreparedCount(matchedRecipeId, 1);
            }

            RemovePreparedSourceCompatibility(pendingSourceState);
            PreparedCookStateBySourceId.Remove(pendingSourceId);
            DirtyPreparedSourceIds.Remove(pendingSourceId);
            if (pendingSourceState.GameObjectInstanceId != 0)
            {
                int mappedSourceId;
                if (PreparedSourceIdsByGameObjectId.TryGetValue(pendingSourceState.GameObjectInstanceId, out mappedSourceId)
                    && mappedSourceId == pendingSourceId)
                {
                    PreparedSourceIdsByGameObjectId.Remove(pendingSourceState.GameObjectInstanceId);
                }
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

        private static bool HaveCompatiblePreparedRecipe(PreparedSourceState left, PreparedSourceState right)
        {
            foreach (int recipeId in left.CompatibleRecipeIds)
            {
                if (right.CompatibleRecipeIds.Contains(recipeId))
                {
                    return true;
                }
            }

            return false;
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

        private static void AdjustPreparedCompatibilityCount(int recipeId, int delta)
        {
            int nextValue = Math.Max(0, GetCount(PreparedCompatibilityCountsByRecipe, recipeId) + delta);
            if (nextValue > 0)
            {
                PreparedCompatibilityCountsByRecipe[recipeId] = nextValue;
            }
            else
            {
                PreparedCompatibilityCountsByRecipe.Remove(recipeId);
            }
        }

        private static void RemovePreparedSourceCompatibility(PreparedSourceState source)
        {
            if (source == null || source.CompatibleRecipeIds.Count == 0)
            {
                return;
            }

            foreach (int recipeId in source.CompatibleRecipeIds)
            {
                AdjustPreparedCompatibilityCount(recipeId, -1);
            }

            source.CompatibleRecipeIds.Clear();
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
                DirtyPreparedSourceIds.Remove(instanceId);
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

            bool hadPreparedAssignment = source.MatchedRecipeId != 0;
            if (hadPreparedAssignment)
            {
                AdjustPreparedCount(source.MatchedRecipeId, -1);
            }

            bool hadCompatibility = source.CompatibleRecipeIds.Count > 0;
            RemovePreparedSourceCompatibility(source);
            if (hadPreparedAssignment)
            {
                InvalidateOverlay();
                InvalidateReferenceTickets();
            }

            if (hadPreparedAssignment || hadCompatibility)
            {
                InvalidateTicketWidgets();
            }

            PreparedSourcesByInstanceId.Remove(instanceId);
            DirtyPreparedSourceIds.Remove(instanceId);
            if (source.GameObjectInstanceId != 0)
            {
                int mappedSourceId;
                if (PreparedSourceIdsByGameObjectId.TryGetValue(source.GameObjectInstanceId, out mappedSourceId)
                    && mappedSourceId == instanceId)
                {
                    PreparedSourceIdsByGameObjectId.Remove(source.GameObjectInstanceId);
                }
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
            try
            {
                TryRegisterPreparedSource(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized plate", ex);
            }
        }

        [HarmonyPatch(typeof(ClientCookableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientCookableContainer_StartSynchronising_Postfix(ClientCookableContainer __instance)
        {
            try
            {
                RegisterPreparedCookingHandler(__instance);
                TryRegisterPreparedSource(__instance);
                QueueCookablePreparedSourceRefresh(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized cookable container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientCookablePreparationContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientCookablePreparationContainer_StartSynchronising_Postfix(ClientCookablePreparationContainer __instance)
        {
            try
            {
                RegisterPreparedCookingHandler(__instance);
                TryRegisterPreparedSource(__instance);
                QueueCookablePreparedSourceRefresh(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized cookable preparation container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientCookingHandler), "ApplyServerUpdate")]
        [HarmonyPostfix]
        private static void ClientCookingHandler_ApplyServerUpdate_Postfix(ClientCookingHandler __instance)
        {
            try
            {
                if (__instance == null
                    || enabled == null
                    || !enabled.Value
                    || !IsPreparedTrackingEnabled()
                    || NoMenuMode.IsActiveForRound
                    || !IsInActiveRound())
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
            catch (Exception ex)
            {
                LogTrackingHookFailure("processing a cooking update", ex);
            }
        }

        [HarmonyPatch(typeof(ClientPreparationContainer), "StartSynchronising")]
        [HarmonyPostfix]
        [HarmonyAfter(OptionalRecipeAdapters.ManyRecipesPluginGuid)]
        private static void ClientPreparationContainer_StartSynchronising_Postfix(ClientPreparationContainer __instance)
        {
            try
            {
                TryRegisterPreparedSource(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized preparation container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientItemContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientItemContainer_StartSynchronising_Postfix(ClientItemContainer __instance)
        {
            try
            {
                TryRegisterPreparedSource(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized item container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientLadleContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientLadleContainer_StartSynchronising_Postfix(ClientLadleContainer __instance)
        {
            try
            {
                TryRegisterPreparedSource(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized ladle container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientMixableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientMixableContainer_StartSynchronising_Postfix(ClientMixableContainer __instance)
        {
            try
            {
                TryRegisterPreparedSource(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a synchronized mixable container", ex);
            }
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_StartSynchronising_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            try
            {
                TryRegisterPreparedSourcesFromCarrier(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering synchronized carried items", ex);
            }
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "CarryItem")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_CarryItem_Postfix(GameObject _object)
        {
            try
            {
                TryRegisterPreparedSourceFromGameObject(_object);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("registering a carried item", ex);
            }
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "ApplyServerEvent")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_ApplyServerEvent_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            try
            {
                TryRegisterPreparedSourcesFromCarrier(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("processing a carried-item server event", ex);
            }
        }

        [HarmonyPatch(typeof(ClientSynchroniserBase), "OnDestroy")]
        [HarmonyPrefix]
        private static void ClientSynchroniserBase_OnDestroy_Prefix(ClientSynchroniserBase __instance)
        {
            try
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
            catch (Exception ex)
            {
                LogTrackingHookFailure("cleaning a destroyed synchronized object", ex);
            }
        }

    }
}
