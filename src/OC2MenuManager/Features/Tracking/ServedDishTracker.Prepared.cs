// Tracks completed dishes through event-registered container sources. Each
// physical source owns one canonical accounting assignment plus every recipe ID
// it covers for presentation. Bounded per-source scheduling coalesces changes,
// while matching remains authoritative for dual-purpose utensils and cook state.
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
            QueuePreparedSourceRefresh(instanceId, PreparedSourceChangeDebounceFrames, true);
        }

        private static void QueuePreparedSourceRefresh(int instanceId, int delayFrames, bool resetFailures)
        {
            if (instanceId == 0)
            {
                return;
            }

            PreparedSourceState source;
            if (resetFailures
                && PreparedSourcesByInstanceId.TryGetValue(instanceId, out source)
                && source != null)
            {
                source.ConsecutiveRefreshFailures = 0;
            }

            int targetFrame = Time.frameCount + Math.Max(0, delayFrames);
            int existingDueFrame;
            if (!PreparedSourceDueFramesByInstanceId.TryGetValue(instanceId, out existingDueFrame)
                || targetFrame < existingDueFrame)
            {
                PreparedSourceDueFramesByInstanceId[instanceId] = targetFrame;
            }

            DirtyPreparedSourceIds.Add(instanceId);
            if (nextPreparedSourceRefreshFrame == 0 || targetFrame < nextPreparedSourceRefreshFrame)
            {
                nextPreparedSourceRefreshFrame = targetFrame;
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
                QueuePreparedSourceRefresh(instanceId);
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
            QueuePreparedSourceRefresh(instanceId);
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
            if (DirtyPreparedSourceIds.Count == 0 || maxCount <= 0)
            {
                nextPreparedSourceRefreshFrame = 0;
                return;
            }

            PreparedSourceRefreshBuffer.Clear();
            int currentFrame = Time.frameCount;
            foreach (int instanceId in DirtyPreparedSourceIds)
            {
                int dueFrame;
                if (PreparedSourceDueFramesByInstanceId.TryGetValue(instanceId, out dueFrame)
                    && dueFrame > currentFrame)
                {
                    continue;
                }

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
                PreparedSourceDueFramesByInstanceId.Remove(instanceId);
                RefreshPreparedSource(instanceId);
            }

            RecalculateNextPreparedSourceRefreshFrame();
        }

        private static void RecalculateNextPreparedSourceRefreshFrame()
        {
            nextPreparedSourceRefreshFrame = 0;
            foreach (int instanceId in DirtyPreparedSourceIds)
            {
                int dueFrame;
                if (!PreparedSourceDueFramesByInstanceId.TryGetValue(instanceId, out dueFrame))
                {
                    dueFrame = Time.frameCount;
                    PreparedSourceDueFramesByInstanceId[instanceId] = dueFrame;
                }

                if (nextPreparedSourceRefreshFrame == 0 || dueFrame < nextPreparedSourceRefreshFrame)
                {
                    nextPreparedSourceRefreshFrame = dueFrame;
                }
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
            source.PendingTransferRecipeIds.Clear();

            if (source.Component == null || source.Provider == null || source.Component.gameObject == null || !source.Component.gameObject.activeInHierarchy)
            {
                RemovePreparedSource(instanceId);
                return;
            }

            SceneInfo scene;
            if (!TryGetPreparedSceneInfo(out scene))
            {
                ClearPreparedSourceState(source);
                return;
            }

            AssembledDefinitionNode composition;
            try
            {
                composition = source.Provider.GetOrderComposition();
            }
            catch (Exception ex)
            {
                HandlePreparedSourceRefreshFailure(scene, source, "reading source composition", ex);
                return;
            }

            try
            {
                bool allowCookedFallback = source.CookingHandler != null;
                int canonicalRecipeId = MatchPreparedRecipe(scene, composition, allowCookedFallback, source);
                source.ConsecutiveRefreshFailures = 0;
                SetPreparedSourceState(
                    source,
                    canonicalRecipeId,
                    PreparedCoveredRecipeIdsBuffer,
                    PreparedCoveredRecipeIdsSetBuffer);
            }
            catch (Exception ex)
            {
                HandlePreparedSourceRefreshFailure(scene, source, "matching source composition", ex);
            }
        }

        private static void HandlePreparedSourceRefreshFailure(
            SceneInfo scene,
            PreparedSourceState source,
            string stage,
            Exception exception)
        {
            if (source == null)
            {
                return;
            }

            ClearPreparedSourceState(source);
            source.ConsecutiveRefreshFailures++;
            LogPreparedMatchingFailure(scene, source, stage, exception);
            if (source.ConsecutiveRefreshFailures <= MaxPreparedSourceRefreshRetries
                && source.Component != null
                && source.Component.gameObject != null
                && source.Component.gameObject.activeInHierarchy)
            {
                QueuePreparedSourceRefresh(
                    source.InstanceId,
                    PreparedSourceFailureRetryFrames,
                    false);
            }
        }

        private static void LogPreparedMatchingFailure(
            SceneInfo scene,
            PreparedSourceState source,
            string stage,
            Exception exception)
        {
            if (exception == null || preparedMatchingDiagnosticCount >= MaxPreparedMatchingDiagnosticsPerRound)
            {
                return;
            }

            string sceneName = scene != null && !string.IsNullOrEmpty(scene.SceneName)
                ? scene.SceneName
                : "<unknown>";
            string sourceType = source != null && source.Component != null
                ? source.Component.GetType().FullName
                : "<unknown>";
            string safeStage = !string.IsNullOrEmpty(stage) ? stage : "matching prepared source";
            Exception diagnosticException = exception.GetBaseException() ?? exception;
            string diagnosticKey = sceneName + "|" + sourceType + "|" + safeStage + "|" + diagnosticException.GetType().FullName;
            if (!PreparedMatchingDiagnosticKeys.Add(diagnosticKey))
            {
                return;
            }

            preparedMatchingDiagnosticCount++;
            _MODEntry.LogWarning(
                "[ServedDishTracker] Prepared matching failed in scene '" + sceneName
                + "' for source '" + sourceType
                + "' while " + safeStage
                + ": " + diagnosticException.GetType().Name + ": " + exception.Message
                + (ReferenceEquals(diagnosticException, exception)
                    ? string.Empty
                    : " Inner error: " + diagnosticException.Message));
        }

        private static void ClearPreparedSourceState(PreparedSourceState source)
        {
            PreparedCoveredRecipeIdsBuffer.Clear();
            PreparedCoveredRecipeIdsSetBuffer.Clear();
            SetPreparedSourceState(
                source,
                0,
                PreparedCoveredRecipeIdsBuffer,
                PreparedCoveredRecipeIdsSetBuffer);
        }

        private static int MatchPreparedRecipe(
            SceneInfo scene,
            AssembledDefinitionNode composition,
            bool allowCookedFallback,
            PreparedSourceState source)
        {
            PreparedCoveredRecipeIdsBuffer.Clear();
            PreparedCoveredRecipeIdsSetBuffer.Clear();
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

            Exception compositionFailure;
            AssembledDefinitionNode simplifiedComposition = SafeSimplifyNode(composition, out compositionFailure);
            if (compositionFailure != null)
            {
                throw new InvalidOperationException("Prepared source composition could not be simplified.", compositionFailure);
            }
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

                Exception definitionFailure;
                AssembledDefinitionNode simplifiedDefinition = GetSimplifiedPreparedRecipeDefinition(
                    recipe,
                    out definitionFailure);
                if (definitionFailure != null)
                {
                    throw new InvalidOperationException(
                        "Recipe " + recipe.Id + " could not be simplified.",
                        definitionFailure);
                }
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

                        Exception unwrappedFailure;
                        AssembledDefinitionNode unwrappedSimplifiedDefinition = GetUnwrappedPreparedRecipeDefinition(
                            recipe,
                            out unwrappedFailure);
                        if (unwrappedFailure != null)
                        {
                            throw new InvalidOperationException(
                                "Recipe " + recipe.Id + " could not be unwrapped for cooked-container matching.",
                                unwrappedFailure);
                        }
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

                if (!matches || !PreparedCoveredRecipeIdsSetBuffer.Add(recipe.Id))
                {
                    continue;
                }

                PreparedCoveredRecipeIdsBuffer.Add(recipe.Id);
                PreparedTicketPriority ticketPriority;
                bool hasTicketPriority = PreparedTicketPrioritiesByRecipeBuffer.TryGetValue(recipe.Id, out ticketPriority);
                PreparedAssignmentCandidatesBuffer.Add(new PreparedRecipeAssignmentCandidate(
                    recipe.Id,
                    GetCount(currentMenuCounts, recipe.Id),
                    GetCount(CanonicalPreparedCountsByRecipe, recipe.Id),
                    source != null && source.CanonicalRecipeId == recipe.Id,
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

        private static AssembledDefinitionNode GetSimplifiedPreparedRecipeDefinition(
            RecipeInfo recipe,
            out Exception failure)
        {
            failure = null;
            if (recipe == null)
            {
                return null;
            }

            if (!recipe.SimplifiedDefinitionResolved)
            {
                AssembledDefinitionNode simplifiedDefinition = SafeSimplifyNode(recipe.Definition, out failure);
                if (failure != null)
                {
                    return null;
                }

                recipe.SimplifiedDefinition = simplifiedDefinition;
                recipe.SimplifiedDefinitionResolved = true;
            }

            return recipe.SimplifiedDefinition;
        }

        private static AssembledDefinitionNode GetUnwrappedPreparedRecipeDefinition(
            RecipeInfo recipe,
            out Exception failure)
        {
            failure = null;
            if (recipe == null)
            {
                return null;
            }

            if (!recipe.SimplifiedUnwrappedDefinitionResolved)
            {
                Exception definitionFailure;
                AssembledDefinitionNode simplifiedDefinition = GetSimplifiedPreparedRecipeDefinition(
                    recipe,
                    out definitionFailure);
                if (definitionFailure != null)
                {
                    failure = definitionFailure;
                    return null;
                }

                try
                {
                    recipe.SimplifiedUnwrappedDefinition = simplifiedDefinition != null
                        ? UnwrapCookedCompositeNode(simplifiedDefinition)
                        : null;
                    recipe.SimplifiedUnwrappedDefinitionResolved = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            return recipe.SimplifiedUnwrappedDefinition;
        }

        private static AssembledDefinitionNode SafeSimplifyNode(object node, out Exception failure)
        {
            failure = null;
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
            catch (Exception ex)
            {
                failure = ex;
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

        private static void SetPreparedSourceState(
            PreparedSourceState source,
            int canonicalRecipeId,
            IList<int> coveredRecipeIds,
            HashSet<int> coveredRecipeIdSet)
        {
            if (source == null)
            {
                return;
            }

            bool coverageChanged = SynchronizePreparedSourceCoverage(
                source,
                coveredRecipeIds,
                coveredRecipeIdSet);
            int previousCanonicalRecipeId = source.CanonicalRecipeId;
            bool assignmentChanged = previousCanonicalRecipeId != canonicalRecipeId;
            if (!assignmentChanged && !coverageChanged)
            {
                return;
            }

            if (assignmentChanged)
            {
                if (previousCanonicalRecipeId != 0)
                {
                    AdjustCanonicalPreparedCount(previousCanonicalRecipeId, -1);
                }

                source.CanonicalRecipeId = canonicalRecipeId;
                if (canonicalRecipeId != 0)
                {
                    bool consumedPendingTransfer = previousCanonicalRecipeId == 0
                        && ConsumePendingPreparedTransfer(source, canonicalRecipeId);
                    if (!consumedPendingTransfer)
                    {
                        AdjustCanonicalPreparedCount(canonicalRecipeId, 1);
                    }
                }
            }

            InvalidateOverlay();
            InvalidateReferenceTickets();
            InvalidateTicketWidgets();
        }

        private static bool SynchronizePreparedSourceCoverage(
            PreparedSourceState source,
            IList<int> coveredRecipeIds,
            HashSet<int> coveredRecipeIdSet)
        {
            PreparedCoverageRemovalBuffer.Clear();
            foreach (int recipeId in source.CoveredRecipeIds)
            {
                if (coveredRecipeIdSet == null || !coveredRecipeIdSet.Contains(recipeId))
                {
                    PreparedCoverageRemovalBuffer.Add(recipeId);
                }
            }

            bool changed = false;
            for (int i = 0; i < PreparedCoverageRemovalBuffer.Count; i++)
            {
                int recipeId = PreparedCoverageRemovalBuffer[i];
                if (source.CoveredRecipeIds.Remove(recipeId))
                {
                    AdjustPreparedCoverageCount(recipeId, -1);
                    changed = true;
                }
            }

            if (coveredRecipeIds != null)
            {
                for (int i = 0; i < coveredRecipeIds.Count; i++)
                {
                    int recipeId = coveredRecipeIds[i];
                    if (source.CoveredRecipeIds.Add(recipeId))
                    {
                        AdjustPreparedCoverageCount(recipeId, 1);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool ConsumePendingPreparedTransfer(PreparedSourceState targetSource, int canonicalRecipeId)
        {
            if (targetSource == null || canonicalRecipeId == 0 || targetSource.CoveredRecipeIds.Count == 0)
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
                    || pendingSource.CanonicalRecipeId == 0
                    || !HaveOverlappingPreparedTransfer(pendingSource, targetSource))
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

            if (pendingSourceState.CanonicalRecipeId != canonicalRecipeId)
            {
                AdjustCanonicalPreparedCount(pendingSourceState.CanonicalRecipeId, -1);
                AdjustCanonicalPreparedCount(canonicalRecipeId, 1);
            }

            RemovePreparedSourceCoverage(pendingSourceState);
            pendingSourceState.PendingTransferRecipeIds.Clear();
            PreparedCookStateBySourceId.Remove(pendingSourceId);
            DirtyPreparedSourceIds.Remove(pendingSourceId);
            PreparedSourceDueFramesByInstanceId.Remove(pendingSourceId);
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
            RecalculateNextPreparedSourceRefreshFrame();
            return true;
        }

        private static bool HaveOverlappingPreparedTransfer(PreparedSourceState pending, PreparedSourceState target)
        {
            foreach (int recipeId in pending.PendingTransferRecipeIds)
            {
                if (target.CoveredRecipeIds.Contains(recipeId))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AdjustCanonicalPreparedCount(int recipeId, int delta)
        {
            int nextValue = Math.Max(0, GetCount(CanonicalPreparedCountsByRecipe, recipeId) + delta);
            if (nextValue > 0)
            {
                CanonicalPreparedCountsByRecipe[recipeId] = nextValue;
            }
            else
            {
                CanonicalPreparedCountsByRecipe.Remove(recipeId);
            }
        }

        private static void AdjustPreparedCoverageCount(int recipeId, int delta)
        {
            int nextValue = Math.Max(0, GetCount(PreparedCoverageCountsByRecipe, recipeId) + delta);
            if (nextValue > 0)
            {
                PreparedCoverageCountsByRecipe[recipeId] = nextValue;
            }
            else
            {
                PreparedCoverageCountsByRecipe.Remove(recipeId);
            }
        }

        private static void RemovePreparedSourceCoverage(PreparedSourceState source)
        {
            if (source == null || source.CoveredRecipeIds.Count == 0)
            {
                return;
            }

            foreach (int recipeId in source.CoveredRecipeIds)
            {
                AdjustPreparedCoverageCount(recipeId, -1);
            }

            source.CoveredRecipeIds.Clear();
        }

        private static void RemovePreparedSource(int instanceId)
        {
            PreparedSourceState source;
            if (!PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) || source == null)
            {
                return;
            }

            if (source.CanonicalRecipeId != 0 && !source.PendingRemoval)
            {
                source.PendingTransferRecipeIds.Clear();
                foreach (int recipeId in source.CoveredRecipeIds)
                {
                    source.PendingTransferRecipeIds.Add(recipeId);
                }

                bool withdrewCoverage = source.CoveredRecipeIds.Count > 0;
                RemovePreparedSourceCoverage(source);
                if (withdrewCoverage)
                {
                    InvalidateOverlay();
                    InvalidateReferenceTickets();
                    InvalidateTicketWidgets();
                }

                source.PendingRemoval = true;
                source.RemovalGraceUntilFrame = Time.frameCount + PreparedSourceRemovalGraceFrames;
                DirtyPreparedSourceIds.Remove(instanceId);
                PreparedSourceDueFramesByInstanceId.Remove(instanceId);
                RecalculateNextPreparedSourceRefreshFrame();
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

            bool hadPreparedAssignment = source.CanonicalRecipeId != 0;
            if (hadPreparedAssignment)
            {
                AdjustCanonicalPreparedCount(source.CanonicalRecipeId, -1);
            }

            bool hadCoverage = source.CoveredRecipeIds.Count > 0;
            RemovePreparedSourceCoverage(source);
            source.PendingTransferRecipeIds.Clear();
            if (hadPreparedAssignment || hadCoverage)
            {
                InvalidateOverlay();
                InvalidateReferenceTickets();
                InvalidateTicketWidgets();
            }

            PreparedSourcesByInstanceId.Remove(instanceId);
            DirtyPreparedSourceIds.Remove(instanceId);
            PreparedSourceDueFramesByInstanceId.Remove(instanceId);
            RecalculateNextPreparedSourceRefreshFrame();
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

        private static void RecalculateNextPreparedSourcePruneFrame()
        {
            int nextFrame = Time.frameCount + PreparedSourcePruneIntervalFrames;
            foreach (PreparedSourceState source in PreparedSourcesByInstanceId.Values)
            {
                if (source != null
                    && source.PendingRemoval
                    && source.RemovalGraceUntilFrame > Time.frameCount
                    && source.RemovalGraceUntilFrame < nextFrame)
                {
                    nextFrame = source.RemovalGraceUntilFrame;
                }
            }

            nextPreparedSourcePruneFrame = nextFrame;
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
