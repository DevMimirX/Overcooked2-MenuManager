// Provides reflection-only soft-dependency boundaries for DIY Level and Recipe
// Extension. Recipe Extension entries are snapshotted in their source order once
// per synchronized round and exposed through caller-owned lists. Author metadata
// is read through a quiet cached accessor because missing members are normal gaps,
// not Harmony contract failures.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OC2MenuManager.Infrastructure;

namespace OC2MenuManager
{
    /// <summary>
    /// Carries the stable scene identity and localized frontend metadata exposed by
    /// the optional DIY loader without leaking its concrete types into the tracker.
    /// </summary>
    internal sealed class DIYLevelDescriptor
    {
        public string SceneName;
        public string EnglishDisplayName;
        public string ChineseDisplayName;
        public object LevelInfo;
    }

    /// <summary>
    /// Classifies whether the optional provider supplied a trustworthy catalog,
    /// a usable partial catalog, or no replaceable data for this refresh.
    /// </summary>
    internal enum DIYLevelCatalogReadState
    {
        Unavailable,
        Loading,
        Complete,
        Partial,
        Error
    }

    /// <summary>
    /// Reports provider-level counts and the first diagnostic while descriptors
    /// remain in the caller-owned list. A partial read can still replace a snapshot.
    /// </summary>
    internal struct DIYLevelCatalogReadResult
    {
        public DIYLevelCatalogReadState State;
        public int SourceLevelSetCount;
        public int SourceLevelCount;
        public int AcceptedSceneCount;
        public int RejectedEntryCount;
        public string Detail;
    }

    /// <summary>
    /// Carries a DIY recipe's stable identity, optional hydrated base-game
    /// definition, and bounded provider-neutral category evidence. Custom DIY
    /// recipes intentionally remain metadata-only pre-round.
    /// </summary>
    internal sealed class DIYRecipeDescriptor
    {
        public int Id;
        public string InternalName;
        public OrderDefinitionNode Definition;
        public RecipeCategoryEvidence CategoryEvidence;
    }

    /// <summary>
    /// Owns all reflection contracts for optional recipe providers. Failed contracts
    /// remain isolated from the core plugin, and Recipe Extension snapshots preserve
    /// the provider's patch/entry order for every downstream consumer.
    /// </summary>
    internal static class OptionalRecipeAdapters
    {
        internal const string DIYLevelPluginGuid = "dev.gua.overcooked.diylevel";
        internal const string ManyRecipesPluginGuid = "dev.gua.overcooked.manyrecipes";

        private const string DIYManagerTypeName = "OC2DIYLevel.DIYLevelAssetBundleManager";
        private const string DIYRecipeHelperTypeName = "OC2DIYLevel.RecipeHelper";
        private const string ManyRecipesPluginTypeName = "OC2ManyRecipes.ManyRecipesPlugin";
        private const string ManyRecipesSettingsTypeName = "OC2ManyRecipes.ManyRecipesSettings";
        private const int DIYEvidenceMaximumDepth = 4;
        private const int DIYEvidenceMaximumObjects = 64;
        private const int DIYEvidenceMaximumComponents = 64;

        private static readonly List<RecipeList.Entry> ManyRecipeEntriesCache = new List<RecipeList.Entry>();
        private static readonly List<object> ManyRecipePatchIdentityBuffer = new List<object>();
        private static readonly List<RecipeList.Entry[]> ManyRecipeEntryArrayIdentityBuffer = new List<RecipeList.Entry[]>();
        private static readonly List<int> ManyRecipeEntryIdIdentityBuffer = new List<int>();
        private static readonly HashSet<string> DIYAcceptedSceneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Type diyManagerType;
        private static PropertyInfo diyIsInitializedProperty;
        private static FieldInfo diyLevelSetInfosField;
        private static MethodInfo diyGetRecipeMethod;
        private static int diyGetRecipeParameterCount;
        private static bool diyContractResolved;
        private static bool diyContractWarningLogged;
        private static bool diyActivationLogged;

        private static Type manyRecipesPluginType;
        private static Type manyRecipesSettingsType;
        private static FieldInfo manyRecipePatchesField;
        private static FieldInfo manyRecipesEnabledField;
        private static bool manyContractResolved;
        private static bool manyContractWarningLogged;
        private static bool manyActivationLogged;
        private static bool manyRecipeEntriesCacheValid;
        private static ManyRecipesSnapshotState manyRecipesSnapshotState = ManyRecipesSnapshotState.Absent;

        internal static bool TryGetDIYLevels(List<DIYLevelDescriptor> destination, out DIYLevelCatalogReadResult result)
        {
            result = new DIYLevelCatalogReadResult();
            result.State = DIYLevelCatalogReadState.Error;
            if (destination == null)
            {
                result.Detail = "No destination was provided.";
                return false;
            }

            destination.Clear();
            DIYAcceptedSceneNames.Clear();
            string error;
            if (!TryResolveDIYContract(out error))
            {
                result.State = diyManagerType == null
                    ? DIYLevelCatalogReadState.Unavailable
                    : DIYLevelCatalogReadState.Error;
                result.Detail = error;
                return false;
            }

            bool initialized;
            try
            {
                initialized = (bool)diyIsInitializedProperty.GetValue(null, null);
            }
            catch (Exception ex)
            {
                result.State = DIYLevelCatalogReadState.Error;
                result.Detail = "Could not read DIY initialization state: " + ex.GetType().Name + ": " + ex.Message;
                LogDIYContractWarning(result.Detail);
                return false;
            }

            if (!initialized)
            {
                result.State = DIYLevelCatalogReadState.Loading;
                result.Detail = "DIY Level metadata is still loading.";
                return false;
            }

            IList levelSetInfos;
            try
            {
                levelSetInfos = diyLevelSetInfosField.GetValue(null) as IList;
            }
            catch (Exception ex)
            {
                result.State = DIYLevelCatalogReadState.Error;
                result.Detail = "Could not read DIY level metadata: " + ex.GetType().Name + ": " + ex.Message;
                LogDIYContractWarning(result.Detail);
                return false;
            }

            if (levelSetInfos == null)
            {
                result.State = DIYLevelCatalogReadState.Loading;
                result.Detail = "DIY Level metadata is not available yet.";
                return false;
            }

            int levelSetCount;
            try
            {
                levelSetCount = levelSetInfos.Count;
            }
            catch (Exception ex)
            {
                result.State = DIYLevelCatalogReadState.Error;
                result.Detail = "Could not read the DIY level-set count: " + ex.GetType().Name + ": " + ex.Message;
                LogDIYContractWarning(result.Detail);
                return false;
            }

            result.SourceLevelSetCount = levelSetCount;
            string firstRejectedEntry = null;
            for (int i = 0; i < levelSetCount; i++)
            {
                object levelSetEntry;
                try
                {
                    levelSetEntry = levelSetInfos[i];
                }
                catch (Exception ex)
                {
                    destination.Clear();
                    result.State = DIYLevelCatalogReadState.Error;
                    result.Detail = "DIY level metadata changed while it was being read: "
                        + ex.GetType().Name + ": " + ex.Message;
                    LogDIYContractWarning(result.Detail);
                    return false;
                }

                object levelSetInfo = OptionalMemberAccessor.GetValue(levelSetEntry, "Value");
                if (levelSetInfo == null)
                {
                    result.RejectedEntryCount++;
                    if (firstRejectedEntry == null)
                    {
                        firstRejectedEntry = "level set " + (i + 1) + " of " + levelSetCount + " could not be read";
                    }
                    continue;
                }

                Array levelInfos = OptionalMemberAccessor.GetValue(levelSetInfo, "levelInfos") as Array;
                if (levelInfos == null)
                {
                    result.RejectedEntryCount++;
                    if (firstRejectedEntry == null)
                    {
                        firstRejectedEntry = "level set " + (i + 1) + " of " + levelSetCount + " does not expose its levels";
                    }
                    continue;
                }

                string englishLevelSetName = GetLocalizedString(levelSetInfo, "levelSetName", "levelSetNameZH");
                string chineseLevelSetName = GetLocalizedString(levelSetInfo, "levelSetNameZH", "levelSetName");
                for (int j = 0; j < levelInfos.Length; j++)
                {
                    result.SourceLevelCount++;
                    object levelInfo;
                    try
                    {
                        levelInfo = levelInfos.GetValue(j);
                    }
                    catch (Exception ex)
                    {
                        result.RejectedEntryCount++;
                        if (firstRejectedEntry == null)
                        {
                            firstRejectedEntry = "level " + (j + 1) + " in set " + (i + 1)
                                + " could not be read: " + ex.GetType().Name + ": " + ex.Message;
                        }
                        continue;
                    }

                    string sceneName = GetStringMember(levelInfo, "sceneName");
                    if (!DIYCatalogRefreshPolicy.TryAcceptSceneName(sceneName, DIYAcceptedSceneNames))
                    {
                        result.RejectedEntryCount++;
                        if (firstRejectedEntry == null)
                        {
                            firstRejectedEntry = !string.IsNullOrEmpty(sceneName) && DIYAcceptedSceneNames.Contains(sceneName)
                                ? "duplicate scene name '" + sceneName + "' in level set " + (i + 1)
                                : "level " + (j + 1) + " of " + levelInfos.Length
                                    + " in level set " + (i + 1) + " has no usable scene name";
                        }
                        continue;
                    }

                    string englishLevelName = GetLocalizedString(levelInfo, "levelName", "levelNameZH");
                    string chineseLevelName = GetLocalizedString(levelInfo, "levelNameZH", "levelName");
                    DIYLevelDescriptor descriptor = new DIYLevelDescriptor();
                    descriptor.SceneName = sceneName;
                    descriptor.EnglishDisplayName = BuildDIYDisplayName(englishLevelSetName, englishLevelName, sceneName);
                    descriptor.ChineseDisplayName = BuildDIYDisplayName(chineseLevelSetName, chineseLevelName, sceneName);
                    descriptor.LevelInfo = levelInfo;
                    destination.Add(descriptor);
                }
            }

            result.AcceptedSceneCount = destination.Count;
            result.State = result.RejectedEntryCount > 0
                ? DIYLevelCatalogReadState.Partial
                : DIYLevelCatalogReadState.Complete;
            if (result.RejectedEntryCount > 0)
            {
                result.Detail = "Skipped " + result.RejectedEntryCount
                    + " invalid or duplicate DIY metadata entr"
                    + (result.RejectedEntryCount == 1 ? "y" : "ies")
                    + ". First issue: " + firstRejectedEntry + ".";
            }

            if (!diyActivationLogged)
            {
                diyActivationLogged = true;
                _MODEntry.LogInfo("[Compatibility] DIY Level adapter active; discovered "
                    + destination.Count + " scene(s) from " + result.SourceLevelSetCount
                    + " level set(s) and " + result.SourceLevelCount + " metadata level entr"
                    + (result.SourceLevelCount == 1 ? "y" : "ies") + ".");
            }

            return true;
        }

        internal static bool TryGetDIYRecipes(object levelInfo, List<DIYRecipeDescriptor> destination, out string error)
        {
            error = null;
            if (levelInfo == null || destination == null)
            {
                error = "DIY level metadata is missing.";
                return false;
            }

            destination.Clear();
            if (!TryResolveDIYContract(out error))
            {
                return false;
            }

            Array recipeSources = OptionalMemberAccessor.GetValue(levelInfo, "recipes") as Array;
            if (recipeSources == null)
            {
                error = "The DIY level does not expose a recipe list.";
                LogDIYContractWarning(error);
                return false;
            }

            int requestedCount = GetIntMember(levelInfo, "debugRecipeCount");
            int recipeCount = requestedCount == 0
                ? recipeSources.Length
                : Math.Max(0, Math.Min(requestedCount, recipeSources.Length));
            bool useScore2 = GetBoolMember(levelInfo, "useScore2");

            for (int i = 0; i < recipeCount; i++)
            {
                object recipeSource = recipeSources.GetValue(i);
                if (recipeSource == null)
                {
                    destination.Clear();
                    error = "DIY recipe " + (i + 1) + " of " + recipeCount + " has no metadata.";
                    LogDIYContractWarning(error);
                    return false;
                }

                DIYRecipeDescriptor descriptor;
                if (TryReadCustomDIYRecipe(recipeSource, out descriptor))
                {
                    destination.Add(descriptor);
                    continue;
                }

                RecipeList.Entry entry;
                if (!TryBuildDIYRecipeEntry(recipeSource, useScore2, out entry, out error))
                {
                    destination.Clear();
                    error = "DIY recipe " + (i + 1) + " of " + recipeCount + " could not be preloaded. " + error;
                    LogDIYContractWarning(error);
                    return false;
                }

                if (entry == null || entry.m_order == null)
                {
                    destination.Clear();
                    error = "DIY recipe " + (i + 1) + " of " + recipeCount + " did not resolve to an order definition.";
                    LogDIYContractWarning(error);
                    return false;
                }

                descriptor = new DIYRecipeDescriptor();
                descriptor.Id = entry.m_order.m_uID;
                descriptor.InternalName = entry.m_order.name;
                descriptor.Definition = entry.m_order;
                descriptor.CategoryEvidence = new RecipeCategoryEvidence(descriptor.Id, descriptor.InternalName);
                descriptor.CategoryEvidence.Kind = InferDIYRecipeKind(entry.m_order.GetType().Name);
                destination.Add(descriptor);
            }

            error = null;
            return true;
        }

        internal static ManyRecipesSnapshotState AppendManyRecipeEntries(
            List<RecipeList.Entry> destination,
            string levelConfigName,
            int phaseIndex,
            bool allPhases)
        {
            ManyRecipesSnapshotState state = EnsureManyRecipeEntriesCache();
            if (destination == null || state != ManyRecipesSnapshotState.Ready)
            {
                return state;
            }

            AppendManyRecipeEntriesFromSnapshot(destination, ManyRecipeEntriesCache, levelConfigName, phaseIndex, allPhases);
            return state;
        }

        internal static ManyRecipesSnapshotState TryGetManyRecipeEntries(List<RecipeList.Entry> destination)
        {
            if (destination == null)
            {
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            destination.Clear();
            ManyRecipesSnapshotState state = EnsureManyRecipeEntriesCache();
            if (state == ManyRecipesSnapshotState.Ready)
            {
                destination.AddRange(ManyRecipeEntriesCache);
            }

            return state;
        }

        internal static ManyRecipesSnapshotState GetManyRecipesSnapshotState()
        {
            return EnsureManyRecipeEntriesCache();
        }

        internal static void InvalidateManyRecipeEntries()
        {
            manyRecipeEntriesCacheValid = false;
            manyRecipesSnapshotState = ManyRecipesSnapshotState.Absent;
            ManyRecipeEntriesCache.Clear();
            ManyRecipePatchIdentityBuffer.Clear();
            ManyRecipeEntryArrayIdentityBuffer.Clear();
            ManyRecipeEntryIdIdentityBuffer.Clear();
        }

        internal static void AppendManyRecipeEntriesFromSnapshot(
            List<RecipeList.Entry> destination,
            List<RecipeList.Entry> entries,
            string levelConfigName,
            int phaseIndex,
            bool allPhases)
        {
            if (destination == null || entries == null)
            {
                return;
            }

            int startIndex;
            int endIndex;
            RecipeExtensionPhasePolicy.GetEntryWindow(levelConfigName, phaseIndex, allPhases, entries.Count, out startIndex, out endIndex);

            for (int i = startIndex; i < endIndex; i++)
            {
                RecipeList.Entry entry = entries[i];
                if (entry != null && entry.m_order != null)
                {
                    destination.Add(entry);
                }
            }
        }

        private static bool TryResolveDIYContract(out string error)
        {
            error = null;
            if (diyContractResolved)
            {
                return true;
            }

            diyManagerType = FindLoadedOptionalType(DIYManagerTypeName);
            if (diyManagerType == null)
            {
                error = "DIY Level is not installed or has not loaded yet.";
                return false;
            }

            diyIsInitializedProperty = AccessTools.Property(diyManagerType, "IsInitialized");
            diyLevelSetInfosField = AccessTools.Field(diyManagerType, "levelSetInfos");
            Type recipeHelperType = FindLoadedOptionalType(DIYRecipeHelperTypeName);
            diyGetRecipeMethod = FindDIYGetRecipeMethod(recipeHelperType);
            ParameterInfo[] getRecipeParameters = diyGetRecipeMethod != null ? diyGetRecipeMethod.GetParameters() : null;
            diyGetRecipeParameterCount = getRecipeParameters != null ? getRecipeParameters.Length : 0;
            bool supportedGetRecipeSignature = diyGetRecipeParameterCount == 1
                || (diyGetRecipeParameterCount == 2 && getRecipeParameters[1].ParameterType == typeof(bool));
            if (diyIsInitializedProperty == null
                || diyLevelSetInfosField == null
                || diyGetRecipeMethod == null
                || !supportedGetRecipeSignature)
            {
                error = "The installed DIY Level version does not expose the expected metadata contract.";
                LogDIYContractWarning(error);
                return false;
            }

            diyContractResolved = true;
            return true;
        }

        private static MethodInfo FindDIYGetRecipeMethod(Type recipeHelperType)
        {
            if (recipeHelperType == null)
            {
                return null;
            }

            MethodInfo oneParameterFallback = null;
            MethodInfo[] methods = recipeHelperType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null
                    || !string.Equals(method.Name, "GetRecipe", StringComparison.Ordinal)
                    || !typeof(RecipeList.Entry).IsAssignableFrom(method.ReturnType))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == typeof(bool))
                {
                    return method;
                }

                if (parameters.Length == 1)
                {
                    oneParameterFallback = method;
                }
            }

            return oneParameterFallback;
        }

        private static ManyRecipesSnapshotState ResolveManyRecipesContract()
        {
            if (manyContractResolved)
            {
                return ManyRecipesSnapshotState.Ready;
            }

            manyRecipesPluginType = FindLoadedOptionalType(ManyRecipesPluginTypeName);
            if (manyRecipesPluginType == null)
            {
                return ManyRecipesSnapshotPolicy.Classify(false, false, false, false, false);
            }

            manyRecipesSettingsType = FindLoadedOptionalType(ManyRecipesSettingsTypeName);
            manyRecipePatchesField = AccessTools.Field(manyRecipesPluginType, "recipePatches");
            manyRecipesEnabledField = manyRecipesSettingsType != null
                ? AccessTools.Field(manyRecipesSettingsType, "enabled")
                : null;
            bool contractValid = manyRecipesSettingsType != null
                && manyRecipePatchesField != null
                && manyRecipePatchesField.IsStatic
                && typeof(IList).IsAssignableFrom(manyRecipePatchesField.FieldType)
                && manyRecipesEnabledField != null
                && manyRecipesEnabledField.IsStatic
                && manyRecipesEnabledField.FieldType == typeof(bool);
            if (!contractValid)
            {
                LogManyRecipesContractWarning(
                    "Recipe Extension was detected, but its enabled/recipePatches contract is unavailable. Optional integration will retry and remain fail-closed.");
                return ManyRecipesSnapshotPolicy.Classify(true, false, false, false, false);
            }

            manyContractResolved = true;
            return ManyRecipesSnapshotState.Ready;
        }

        /// <summary>
        /// Resolves an optional provider type only from assemblies already loaded
        /// into the current domain. Unlike Harmony's global type lookup, an absent
        /// provider is an expected no-op and does not emit a warning on every retry.
        /// </summary>
        private static Type FindLoadedOptionalType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type candidate = assemblies[i].GetType(fullName, false, false);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static ManyRecipesSnapshotState CollectManyRecipeEntries(List<RecipeList.Entry> destination)
        {
            destination.Clear();
            ManyRecipePatchIdentityBuffer.Clear();
            ManyRecipeEntryArrayIdentityBuffer.Clear();
            ManyRecipeEntryIdIdentityBuffer.Clear();
            ManyRecipesSnapshotState contractState = ResolveManyRecipesContract();
            if (contractState != ManyRecipesSnapshotState.Ready)
            {
                return contractState;
            }

            bool isEnabled;
            try
            {
                object enabledValue = manyRecipesEnabledField.GetValue(null);
                if (!(enabledValue is bool))
                {
                    LogManyRecipesContractWarning("Recipe Extension's enabled state did not contain a Boolean value.");
                    return ManyRecipesSnapshotState.ActiveUnavailable;
                }

                isEnabled = (bool)enabledValue;
            }
            catch (Exception ex)
            {
                LogManyRecipesContractWarning(
                    "Could not read Recipe Extension's enabled state: " + ex.GetType().Name + ": " + ex.Message);
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            if (!isEnabled)
            {
                return ManyRecipesSnapshotPolicy.Classify(true, true, false, false, false);
            }

            IList patches;
            try
            {
                patches = manyRecipePatchesField.GetValue(null) as IList;
            }
            catch (Exception ex)
            {
                LogManyRecipesContractWarning(
                    "Could not read Recipe Extension entries: " + ex.GetType().Name + ": " + ex.Message);
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            if (patches == null)
            {
                LogManyRecipesContractWarning("Recipe Extension is enabled, but recipePatches is unavailable.");
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            int patchCount;
            try
            {
                patchCount = patches.Count;
            }
            catch (Exception ex)
            {
                LogManyRecipesContractWarning(
                    "Could not read Recipe Extension's provider count: " + ex.GetType().Name + ": " + ex.Message);
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            if (!ManyRecipesSnapshotPolicy.IsProviderRegistryAvailable(patchCount))
            {
                LogManyRecipesContractWarning(
                    "Recipe Extension is enabled, but its provider registry is empty. Optional integration will retry after initialization completes.");
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            for (int i = 0; i < patchCount; i++)
            {
                object patch;
                try
                {
                    patch = patches[i];
                }
                catch (Exception ex)
                {
                    destination.Clear();
                    LogManyRecipesContractWarning(
                        "Recipe Extension's provider list changed while it was read: " + ex.GetType().Name + ": " + ex.Message);
                    return ManyRecipesSnapshotState.ActiveUnavailable;
                }

                object entriesValue;
                if (patch == null || !OptionalMemberAccessor.TryGetInstanceValue(patch, "entries", out entriesValue))
                {
                    destination.Clear();
                    LogManyRecipesContractWarning("Recipe Extension provider " + (i + 1) + " does not expose a readable entries array.");
                    return ManyRecipesSnapshotState.ActiveUnavailable;
                }

                if (entriesValue == null)
                {
                    // Recipe categories not used by the current level intentionally publish null.
                    ManyRecipePatchIdentityBuffer.Add(patch);
                    ManyRecipeEntryArrayIdentityBuffer.Add(null);
                    continue;
                }

                RecipeList.Entry[] entries = entriesValue as RecipeList.Entry[];
                if (entries == null)
                {
                    destination.Clear();
                    LogManyRecipesContractWarning("Recipe Extension provider " + (i + 1) + " exposed entries with an unexpected type.");
                    return ManyRecipesSnapshotState.ActiveUnavailable;
                }

                ManyRecipePatchIdentityBuffer.Add(patch);
                ManyRecipeEntryArrayIdentityBuffer.Add(entries);

                for (int j = 0; j < entries.Length; j++)
                {
                    RecipeList.Entry entry = entries[j];

                    if (entry == null || entry.m_order == null || entry.m_order.m_uID == 0)
                    {
                        destination.Clear();
                        LogManyRecipesContractWarning(
                            "Recipe Extension provider " + (i + 1) + " contains an invalid generated recipe entry at index " + j + ".");
                        return ManyRecipesSnapshotState.ActiveUnavailable;
                    }

                    // Deliberately preserve source order and duplicate entries. The base
                    // game balances frequencies per entry before probabilities aggregate by ID.
                    destination.Add(entry);
                    ManyRecipeEntryIdIdentityBuffer.Add(entry.m_order.m_uID);
                }
            }

            try
            {
                if (patches.Count != patchCount
                    || !IsManyRecipesSnapshotUnchanged(patches, patchCount, destination))
                {
                    destination.Clear();
                    LogManyRecipesContractWarning("Recipe Extension's provider entries changed while they were read.");
                    return ManyRecipesSnapshotState.ActiveUnavailable;
                }
            }
            catch (Exception ex)
            {
                destination.Clear();
                LogManyRecipesContractWarning(
                    "Could not verify Recipe Extension's provider snapshot: " + ex.GetType().Name + ": " + ex.Message);
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            if (!manyActivationLogged)
            {
                manyActivationLogged = true;
                _MODEntry.LogInfo("[Compatibility] Recipe Extension adapter ready; snapshotted "
                    + destination.Count + " generated entry/entries for the current level.");
            }

            ManyRecipePatchIdentityBuffer.Clear();
            ManyRecipeEntryArrayIdentityBuffer.Clear();
            ManyRecipeEntryIdIdentityBuffer.Clear();
            return ManyRecipesSnapshotPolicy.Classify(true, true, true, true, true);
        }

        private static bool IsManyRecipesSnapshotUnchanged(
            IList patches,
            int patchCount,
            List<RecipeList.Entry> orderedEntries)
        {
            if (patches == null
                || orderedEntries == null
                || ManyRecipePatchIdentityBuffer.Count != patchCount
                || ManyRecipeEntryArrayIdentityBuffer.Count != patchCount
                || ManyRecipeEntryIdIdentityBuffer.Count != orderedEntries.Count)
            {
                return false;
            }

            int entryIndex = 0;
            for (int i = 0; i < patchCount; i++)
            {
                object patch = patches[i];
                if (!ReferenceEquals(patch, ManyRecipePatchIdentityBuffer[i]))
                {
                    return false;
                }

                object entriesValue;
                if (!OptionalMemberAccessor.TryGetInstanceValue(patch, "entries", out entriesValue))
                {
                    return false;
                }

                RecipeList.Entry[] entries = entriesValue as RecipeList.Entry[];
                if (!ReferenceEquals(entries, ManyRecipeEntryArrayIdentityBuffer[i]))
                {
                    return false;
                }

                if (entries == null)
                {
                    continue;
                }

                for (int j = 0; j < entries.Length; j++)
                {
                    if (entryIndex >= orderedEntries.Count
                        || !ReferenceEquals(entries[j], orderedEntries[entryIndex])
                        || entries[j] == null
                        || entries[j].m_order == null
                        || entries[j].m_order.m_uID != ManyRecipeEntryIdIdentityBuffer[entryIndex])
                    {
                        return false;
                    }

                    entryIndex++;
                }
            }

            return entryIndex == orderedEntries.Count;
        }

        private static ManyRecipesSnapshotState EnsureManyRecipeEntriesCache()
        {
            if (manyRecipeEntriesCacheValid)
            {
                return manyRecipesSnapshotState;
            }

            ManyRecipeEntriesCache.Clear();
            manyRecipesSnapshotState = CollectManyRecipeEntries(ManyRecipeEntriesCache);
            if (!ManyRecipesSnapshotPolicy.ShouldCache(manyRecipesSnapshotState))
            {
                ManyRecipeEntriesCache.Clear();
                ManyRecipePatchIdentityBuffer.Clear();
                ManyRecipeEntryArrayIdentityBuffer.Clear();
                ManyRecipeEntryIdIdentityBuffer.Clear();
                return manyRecipesSnapshotState;
            }

            manyRecipeEntriesCacheValid = true;
            return manyRecipesSnapshotState;
        }

        private static bool TryReadCustomDIYRecipe(object recipeSource, out DIYRecipeDescriptor descriptor)
        {
            descriptor = null;
            object idValue = OptionalMemberAccessor.GetValue(recipeSource, "uID");
            string recipeName = GetStringMember(recipeSource, "recipeName");
            if (!(idValue is int) || string.IsNullOrEmpty(recipeName))
            {
                return false;
            }

            descriptor = new DIYRecipeDescriptor();
            descriptor.Id = (int)idValue;
            descriptor.InternalName = recipeName;
            descriptor.Definition = null;
            descriptor.CategoryEvidence = BuildCustomDIYCategoryEvidence(recipeSource, descriptor.Id, descriptor.InternalName);
            return true;
        }

        private static RecipeCategoryEvidence BuildCustomDIYCategoryEvidence(object recipeSource, int recipeId, string recipeName)
        {
            RecipeCategoryEvidence evidence = new RecipeCategoryEvidence(recipeId, recipeName);
            evidence.AuthoringName = BoundDIYIdentity(GetStringMember(recipeSource, "name"));
            evidence.Kind = ReadDIYRecipeKind(OptionalMemberAccessor.GetValue(recipeSource, "type"));
            evidence.CookingIdentity = GetFirstDIYIdentity(
                recipeSource,
                "cookingStepSO",
                "cookingStepIconSO",
                "cookingStepIcon");
            evidence.MixingIdentity = GetFirstDIYIdentity(recipeSource, "mixingIconSO", "mixingIcon");
            evidence.PlatingIdentity = GetFirstDIYIdentity(recipeSource, "platingStepSO");
            evidence.ModelIdentity = GetFirstDIYIdentity(recipeSource, "modelSO", "model");
            evidence.IconIdentity = GetFirstDIYIdentity(recipeSource, "iconSO", "icon");

            int requiredObjectCount = 0;
            HashSet<object> requiredVisited = new HashSet<object>(ReferenceIdentityComparer.Instance);
            AppendDIYComponentEvidence(
                recipeSource,
                "compositionSOs",
                evidence.Components,
                false,
                requiredVisited,
                ref requiredObjectCount,
                0);

            int optionalObjectCount = 0;
            HashSet<object> optionalVisited = new HashSet<object>(ReferenceIdentityComparer.Instance);
            AppendDIYComponentEvidence(
                recipeSource,
                "optionalSOs",
                evidence.Components,
                true,
                optionalVisited,
                ref optionalObjectCount,
                0);
            return evidence;
        }

        private static DIYRecipeKind ReadDIYRecipeKind(object value)
        {
            if (value == null)
            {
                return DIYRecipeKind.Unknown;
            }

            Type valueType = value.GetType();
            if (valueType.IsEnum)
            {
                string enumName = Enum.GetName(valueType, value);
                DIYRecipeKind namedKind = InferDIYRecipeKind(enumName);
                if (namedKind != DIYRecipeKind.Unknown)
                {
                    return namedKind;
                }
            }

            int numericValue;
            try
            {
                numericValue = Convert.ToInt32(value);
            }
            catch
            {
                return DIYRecipeKind.Unknown;
            }

            switch (numericValue)
            {
                case 1:
                    return DIYRecipeKind.Composite;
                case 2:
                    return DIYRecipeKind.Cooked;
                case 3:
                    return DIYRecipeKind.Mixed;
                default:
                    return DIYRecipeKind.Unknown;
            }
        }

        private static DIYRecipeKind InferDIYRecipeKind(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return DIYRecipeKind.Unknown;
            }

            if (typeName.IndexOf("Mixed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return DIYRecipeKind.Mixed;
            }

            if (typeName.IndexOf("Cooked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return DIYRecipeKind.Cooked;
            }

            return typeName.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) >= 0
                ? DIYRecipeKind.Composite
                : DIYRecipeKind.Unknown;
        }

        private static void AppendDIYComponentEvidence(
            object owner,
            string memberName,
            List<RecipeComponentEvidence> destination,
            bool isOptional,
            HashSet<object> visited,
            ref int objectCount,
            int depth)
        {
            if (owner == null
                || destination == null
                || visited == null
                || depth > DIYEvidenceMaximumDepth
                || objectCount >= DIYEvidenceMaximumObjects
                || destination.Count >= DIYEvidenceMaximumComponents)
            {
                return;
            }

            IList components = OptionalMemberAccessor.GetValue(owner, memberName) as IList;
            if (components == null)
            {
                return;
            }

            int componentCount;
            try
            {
                componentCount = Math.Min(components.Count, DIYEvidenceMaximumObjects - objectCount);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < componentCount && destination.Count < DIYEvidenceMaximumComponents; i++)
            {
                object component;
                try
                {
                    component = components[i];
                }
                catch
                {
                    continue;
                }

                if (component == null || !visited.Add(component))
                {
                    continue;
                }

                objectCount++;
                AddOrImproveDIYComponentEvidence(destination, GetDIYIdentity(component), isOptional, depth);
                if (depth >= DIYEvidenceMaximumDepth || objectCount >= DIYEvidenceMaximumObjects)
                {
                    continue;
                }

                AppendDIYComponentEvidence(
                    component,
                    "compositionSOs",
                    destination,
                    isOptional,
                    visited,
                    ref objectCount,
                    depth + 1);
                AppendDIYComponentEvidence(
                    component,
                    "optionalSOs",
                    destination,
                    true,
                    visited,
                    ref objectCount,
                    depth + 1);
            }
        }

        private static string GetFirstDIYIdentity(object owner, params string[] memberNames)
        {
            if (owner == null || memberNames == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < memberNames.Length; i++)
            {
                string identity = GetDIYIdentity(OptionalMemberAccessor.GetValue(owner, memberNames[i]));
                if (!string.IsNullOrEmpty(identity))
                {
                    return identity;
                }
            }

            return string.Empty;
        }

        private static string GetDIYIdentity(object source)
        {
            if (source == null)
            {
                return string.Empty;
            }

            string directValue = source as string;
            if (!string.IsNullOrEmpty(directValue))
            {
                return BoundDIYIdentity(directValue);
            }

            string[] identityMembers = new string[]
            {
                "recipeName",
                "prefabName",
                "name",
                "assetPath",
                "bundleName"
            };
            for (int i = 0; i < identityMembers.Length; i++)
            {
                string identity = GetStringMember(source, identityMembers[i]);
                if (!string.IsNullOrEmpty(identity))
                {
                    return BoundDIYIdentity(identity);
                }
            }

            return string.Empty;
        }

        private static string BoundDIYIdentity(string value)
        {
            string identity = (value ?? string.Empty).Trim();
            return identity.Length <= 160 ? identity : identity.Substring(0, 160);
        }

        private static void AddOrImproveDIYComponentEvidence(
            List<RecipeComponentEvidence> destination,
            string identity,
            bool isOptional,
            int depth)
        {
            if (destination == null || string.IsNullOrEmpty(identity))
            {
                return;
            }

            for (int i = 0; i < destination.Count; i++)
            {
                RecipeComponentEvidence existing = destination[i];
                if (!string.Equals(existing.Identity, identity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool incomingIsStronger = (existing.IsOptional && !isOptional)
                    || (existing.IsOptional == isOptional && depth < existing.Depth);
                if (incomingIsStronger)
                {
                    destination[i] = new RecipeComponentEvidence(identity, isOptional, depth);
                }

                return;
            }

            destination.Add(new RecipeComponentEvidence(identity, isOptional, depth));
        }

        /// <summary>Uses object identity so Unity equality overloads cannot collapse distinct metadata nodes.</summary>
        private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();

            bool IEqualityComparer<object>.Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            int IEqualityComparer<object>.GetHashCode(object value)
            {
                return value == null ? 0 : RuntimeHelpers.GetHashCode(value);
            }
        }

        private static bool TryBuildDIYRecipeEntry(object recipeSource, bool useScore2, out RecipeList.Entry entry, out string error)
        {
            entry = null;
            error = null;
            try
            {
                object[] arguments = diyGetRecipeParameterCount == 1
                    ? new object[] { recipeSource }
                    : new object[] { recipeSource, useScore2 };
                entry = diyGetRecipeMethod.Invoke(null, arguments) as RecipeList.Entry;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                error = "Could not hydrate a DIY recipe: " + cause.GetType().Name + ": " + cause.Message;
            }
            catch (Exception ex)
            {
                error = "Could not hydrate a DIY recipe: " + ex.GetType().Name + ": " + ex.Message;
            }

            return false;
        }

        private static string GetStringMember(object instance, string memberName)
        {
            return OptionalMemberAccessor.GetValue(instance, memberName) as string;
        }

        private static int GetIntMember(object instance, string memberName)
        {
            object value = OptionalMemberAccessor.GetValue(instance, memberName);
            return value is int ? (int)value : 0;
        }

        private static bool GetBoolMember(object instance, string memberName)
        {
            object value = OptionalMemberAccessor.GetValue(instance, memberName);
            return value is bool && (bool)value;
        }

        private static string GetLocalizedString(object source, string primaryMemberName, string fallbackMemberName)
        {
            string value = GetStringMember(source, primaryMemberName);
            return !string.IsNullOrEmpty(value) ? value : GetStringMember(source, fallbackMemberName);
        }

        private static string BuildDIYDisplayName(string levelSetName, string levelName, string sceneName)
        {
            string resolvedLevelSetName = string.IsNullOrEmpty(levelSetName) ? "DIY" : levelSetName;
            string resolvedLevelName = string.IsNullOrEmpty(levelName) ? sceneName : levelName;
            return resolvedLevelSetName + " - " + resolvedLevelName + " [" + sceneName + "]";
        }

        private static void LogDIYContractWarning(string message)
        {
            if (diyContractWarningLogged || string.IsNullOrEmpty(message))
            {
                return;
            }

            diyContractWarningLogged = true;
            _MODEntry.LogWarning("[Compatibility] " + message);
        }

        private static void LogManyRecipesContractWarning(string message)
        {
            if (manyContractWarningLogged || string.IsNullOrEmpty(message))
            {
                return;
            }

            manyContractWarningLogged = true;
            _MODEntry.LogWarning("[Compatibility] " + message);
        }
    }
}
