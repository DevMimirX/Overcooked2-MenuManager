// Provides reflection-only soft-dependency boundaries for DIY Level and Recipe
// Extension. Recipe Extension entries are snapshotted in their source order once
// per synchronized round and exposed through caller-owned lists.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// Carries a DIY recipe's stable identity and an optional hydrated base-game
    /// definition; custom DIY recipes intentionally remain metadata-only pre-round.
    /// </summary>
    internal sealed class DIYRecipeDescriptor
    {
        public int Id;
        public string InternalName;
        public OrderDefinitionNode Definition;
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

        private static readonly List<RecipeList.Entry> ManyRecipeEntriesCache = new List<RecipeList.Entry>();
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
        private static FieldInfo manyRecipePatchesField;
        private static bool manyContractResolved;
        private static bool manyContractWarningLogged;
        private static bool manyActivationLogged;
        private static bool manyRecipeEntriesCacheValid;

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

                object levelSetInfo = GetPairValue(levelSetEntry);
                if (levelSetInfo == null)
                {
                    result.RejectedEntryCount++;
                    if (firstRejectedEntry == null)
                    {
                        firstRejectedEntry = "level set " + (i + 1) + " of " + levelSetCount + " could not be read";
                    }
                    continue;
                }

                Array levelInfos = GetMemberValue(levelSetInfo, "levelInfos") as Array;
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

            Array recipeSources = GetMemberValue(levelInfo, "recipes") as Array;
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
                destination.Add(descriptor);
            }

            error = null;
            return true;
        }

        internal static void AppendManyRecipeEntries(List<RecipeList.Entry> destination, string levelConfigName, int phaseIndex, bool allPhases)
        {
            if (destination == null || !EnsureManyRecipeEntriesCache())
            {
                return;
            }

            AppendManyRecipeEntriesFromSnapshot(destination, ManyRecipeEntriesCache, levelConfigName, phaseIndex, allPhases);
        }

        internal static bool TryGetManyRecipeEntries(List<RecipeList.Entry> destination)
        {
            if (destination == null)
            {
                return false;
            }

            destination.Clear();
            if (!EnsureManyRecipeEntriesCache())
            {
                return false;
            }

            destination.AddRange(ManyRecipeEntriesCache);
            return true;
        }

        internal static void InvalidateManyRecipeEntries()
        {
            manyRecipeEntriesCacheValid = false;
            ManyRecipeEntriesCache.Clear();
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

            diyManagerType = AccessTools.TypeByName(DIYManagerTypeName);
            if (diyManagerType == null)
            {
                error = "DIY Level is not installed or has not loaded yet.";
                return false;
            }

            diyIsInitializedProperty = AccessTools.Property(diyManagerType, "IsInitialized");
            diyLevelSetInfosField = AccessTools.Field(diyManagerType, "levelSetInfos");
            Type recipeHelperType = AccessTools.TypeByName(DIYRecipeHelperTypeName);
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

        private static bool TryResolveManyRecipesContract()
        {
            if (manyContractResolved)
            {
                return true;
            }

            manyRecipesPluginType = AccessTools.TypeByName(ManyRecipesPluginTypeName);
            if (manyRecipesPluginType == null)
            {
                return false;
            }

            manyRecipePatchesField = AccessTools.Field(manyRecipesPluginType, "recipePatches");
            if (manyRecipePatchesField == null)
            {
                if (!manyContractWarningLogged)
                {
                    manyContractWarningLogged = true;
                    _MODEntry.LogWarning("[Compatibility] Recipe Extension was detected, but its recipePatches contract is unavailable. Integration is disabled.");
                }

                return false;
            }

            manyContractResolved = true;
            return true;
        }

        private static bool TryCollectManyRecipeEntries(List<RecipeList.Entry> destination)
        {
            destination.Clear();
            if (!TryResolveManyRecipesContract())
            {
                return false;
            }

            IList patches;
            try
            {
                patches = manyRecipePatchesField.GetValue(null) as IList;
            }
            catch (Exception ex)
            {
                if (!manyContractWarningLogged)
                {
                    manyContractWarningLogged = true;
                    _MODEntry.LogWarning("[Compatibility] Could not read Recipe Extension entries: " + ex.GetType().Name + ": " + ex.Message);
                }

                return false;
            }

            if (patches == null)
            {
                return true;
            }

            for (int i = 0; i < patches.Count; i++)
            {
                Array entries = GetMemberValue(patches[i], "entries") as Array;
                if (entries == null)
                {
                    continue;
                }

                for (int j = 0; j < entries.Length; j++)
                {
                    RecipeList.Entry entry = entries.GetValue(j) as RecipeList.Entry;
                    if (entry != null && entry.m_order != null)
                    {
                        destination.Add(entry);
                    }
                }
            }

            if (destination.Count > 0 && !manyActivationLogged)
            {
                manyActivationLogged = true;
                _MODEntry.LogInfo("[Compatibility] Recipe Extension adapter active; merged " + destination.Count + " generated entry/entries for the current level.");
            }

            return true;
        }

        private static bool EnsureManyRecipeEntriesCache()
        {
            if (manyRecipeEntriesCacheValid)
            {
                return true;
            }

            ManyRecipeEntriesCache.Clear();
            if (!TryCollectManyRecipeEntries(ManyRecipeEntriesCache))
            {
                return false;
            }

            manyRecipeEntriesCacheValid = true;
            return true;
        }

        private static bool TryReadCustomDIYRecipe(object recipeSource, out DIYRecipeDescriptor descriptor)
        {
            descriptor = null;
            object idValue = GetMemberValue(recipeSource, "uID");
            string recipeName = GetStringMember(recipeSource, "recipeName");
            if (!(idValue is int) || string.IsNullOrEmpty(recipeName))
            {
                return false;
            }

            descriptor = new DIYRecipeDescriptor();
            descriptor.Id = (int)idValue;
            descriptor.InternalName = recipeName;
            descriptor.Definition = null;
            return true;
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

        private static object GetPairValue(object pair)
        {
            PropertyInfo valueProperty = pair != null
                ? pair.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)
                : null;
            if (valueProperty == null)
            {
                return null;
            }

            try
            {
                return valueProperty.GetValue(pair, null);
            }
            catch
            {
                return null;
            }
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName))
            {
                return null;
            }

            Type type = instance.GetType();
            FieldInfo field = AccessTools.Field(type, memberName);
            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    return null;
                }
            }

            PropertyInfo property = AccessTools.Property(type, memberName);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
            {
                return null;
            }

            try
            {
                return property.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private static string GetStringMember(object instance, string memberName)
        {
            return GetMemberValue(instance, memberName) as string;
        }

        private static int GetIntMember(object instance, string memberName)
        {
            object value = GetMemberValue(instance, memberName);
            return value is int ? (int)value : 0;
        }

        private static bool GetBoolMember(object instance, string memberName)
        {
            object value = GetMemberValue(instance, memberName);
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
    }
}
