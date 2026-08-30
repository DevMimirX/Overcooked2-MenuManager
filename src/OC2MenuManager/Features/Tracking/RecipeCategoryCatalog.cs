// Owns the canonical recipe-category taxonomy, native-name classification,
// deterministic DIY family and workflow inference, localization, and tier
// inheritance. The classifier is side-effect free; provider adapters supply
// bounded neutral evidence and the game-facing tracker owns assignment refreshes.
#nullable disable
#pragma warning disable CA1846, CA2249
using System;
using System.Collections.Generic;
using System.Text;

namespace OC2MenuManager
{
    /// <summary>Identifies the recipe shape exposed by DIY metadata.</summary>
    internal enum DIYRecipeKind
    {
        Unknown,
        Composite,
        Cooked,
        Mixed
    }

    /// <summary>Identifies which deterministic classifier stage produced an assignment.</summary>
    internal enum RecipeCategorySource
    {
        Native,
        Semantic,
        Workflow,
        Scene,
        Structure,
        Fallback
    }

    /// <summary>
    /// Describes one bounded component identity together with its authoring role.
    /// Lower depths are closer to the recipe root and therefore stronger evidence.
    /// </summary>
    internal sealed class RecipeComponentEvidence
    {
        internal readonly string Identity;
        internal readonly bool IsOptional;
        internal readonly int Depth;

        internal RecipeComponentEvidence(string identity, bool isOptional, int depth)
        {
            Identity = identity ?? string.Empty;
            IsOptional = isOptional;
            Depth = Math.Max(0, depth);
        }
    }

    /// <summary>
    /// Carries provider-neutral recipe metadata used to infer a family without
    /// loading provider assemblies or creating runtime order definitions.
    /// </summary>
    internal sealed class RecipeCategoryEvidence
    {
        internal readonly int RecipeId;
        internal readonly string InternalName;
        internal string AuthoringName = string.Empty;
        internal DIYRecipeKind Kind;
        internal string CookingIdentity = string.Empty;
        internal string MixingIdentity = string.Empty;
        internal string PlatingIdentity = string.Empty;
        internal string ModelIdentity = string.Empty;
        internal string IconIdentity = string.Empty;
        internal readonly List<RecipeComponentEvidence> Components = new List<RecipeComponentEvidence>();

        internal RecipeCategoryEvidence(int recipeId, string internalName)
        {
            RecipeId = recipeId;
            InternalName = internalName ?? string.Empty;
        }
    }

    /// <summary>
    /// Describes a stable selector group. The group key controls membership,
    /// while TierKey inherits ordering from the existing configurable taxonomy.
    /// </summary>
    internal sealed class RecipeCategoryAssignment
    {
        internal readonly string Key;
        internal readonly string EnglishName;
        internal readonly string ChineseName;
        internal readonly string TierKey;
        internal readonly RecipeCategorySource Source;
        internal readonly string InitialCategoryKey;
        internal readonly string InferenceDetail;

        internal RecipeCategoryAssignment(
            string key,
            string englishName,
            string chineseName,
            string tierKey,
            RecipeCategorySource source,
            string initialCategoryKey = null,
            string inferenceDetail = null)
        {
            Key = key ?? string.Empty;
            EnglishName = englishName ?? string.Empty;
            ChineseName = chineseName ?? string.Empty;
            TierKey = tierKey ?? string.Empty;
            Source = source;
            InitialCategoryKey = initialCategoryKey ?? string.Empty;
            InferenceDetail = inferenceDetail ?? string.Empty;
        }
    }

    /// <summary>
    /// Owns category definitions and deterministic classification. Native rules
    /// intentionally preserve their prior exact behavior; broader token and
    /// structural inference is used only for DIY metadata.
    /// </summary>
    internal static class RecipeCategoryCatalog
    {
        private sealed class CategoryDefinition
        {
            internal readonly string Key;
            internal readonly string EnglishName;
            internal readonly string ChineseName;
            internal readonly string TierKey;
            internal readonly int DefaultTier;

            internal CategoryDefinition(
                string key,
                string englishName,
                string chineseName,
                string tierKey,
                int defaultTier)
            {
                Key = key;
                EnglishName = englishName;
                ChineseName = chineseName;
                TierKey = tierKey;
                DefaultTier = defaultTier;
            }
        }

        private sealed class RecipeCandidate
        {
            internal readonly RecipeCategoryEvidence Evidence;
            internal readonly List<string> Tokens;

            internal RecipeCandidate(RecipeCategoryEvidence evidence, List<string> tokens)
            {
                Evidence = evidence;
                Tokens = tokens;
            }
        }

        private sealed class InferenceCandidate
        {
            internal readonly string Key;
            internal readonly string Label;
            internal readonly int TokenCount;
            internal readonly int CharacterCount;
            internal int RecipeCount;

            internal InferenceCandidate(string key, string label, int tokenCount, int characterCount)
            {
                Key = key;
                Label = label;
                TokenCount = tokenCount;
                CharacterCount = characterCount;
            }
        }

        /// <summary>Holds normalized workflow evidence for one tentatively classified recipe.</summary>
        private sealed class WorkflowRecipe
        {
            internal readonly RecipeCategoryEvidence Evidence;
            internal readonly string CategoryKey;
            internal readonly RecipeCategorySource Source;
            internal readonly List<string> Facets;
            internal readonly Dictionary<string, int> ComponentWeights;
            internal readonly string ProcessIdentity;

            internal WorkflowRecipe(
                RecipeCategoryEvidence evidence,
                string categoryKey,
                RecipeCategorySource source,
                List<string> facets,
                Dictionary<string, int> componentWeights,
                string processIdentity)
            {
                Evidence = evidence;
                CategoryKey = categoryKey;
                Source = source;
                Facets = facets;
                ComponentWeights = componentWeights;
                ProcessIdentity = processIdentity;
            }
        }

        /// <summary>Groups immutable initial assignments used to evaluate workflow consensus.</summary>
        private sealed class WorkflowFamily
        {
            internal readonly string Key;
            internal readonly List<WorkflowRecipe> Members = new List<WorkflowRecipe>();

            internal WorkflowFamily(string key)
            {
                Key = key;
            }
        }

        /// <summary>Captures the independent evidence used to compare eligible workflow targets.</summary>
        private sealed class WorkflowTargetScore
        {
            internal readonly string CategoryKey;
            internal readonly string Facet;
            internal readonly string ProcessIdentity;
            internal readonly int NamedCoreCount;
            internal readonly int SharedBaseCount;
            internal readonly int WeightedOverlap;
            internal readonly int CompatibleMemberCount;
            internal readonly int FacetSupportCount;
            internal readonly List<string> SharedComponents;

            internal WorkflowTargetScore(
                string categoryKey,
                string facet,
                string processIdentity,
                int namedCoreCount,
                int sharedBaseCount,
                int weightedOverlap,
                int compatibleMemberCount,
                int facetSupportCount,
                List<string> sharedComponents)
            {
                CategoryKey = categoryKey;
                Facet = facet;
                ProcessIdentity = processIdentity;
                NamedCoreCount = namedCoreCount;
                SharedBaseCount = sharedBaseCount;
                WeightedOverlap = weightedOverlap;
                CompatibleMemberCount = compatibleMemberCount;
                FacetSupportCount = facetSupportCount;
                SharedComponents = sharedComponents;
            }
        }

        private static readonly string[] OrderedCategoryKeys = new string[]
        {
            "pizza",
            "cake",
            "moonpie",
            "fruitpie",
            "roast",
            "fried",
            "pancake",
            "dessert",
            "sushi",
            "steamed",
            "soup",
            "hotpot",
            "breakfast",
            "burger",
            "burrito",
            "kebob",
            "donut",
            "salad",
            "pasta",
            "smoothie",
            "hotdog",
            "smores",
            "fruitplatter",
            "sashimi",
            "hotchocolate",
            "float"
        };

        private static readonly Dictionary<string, CategoryDefinition> Definitions = BuildDefinitions();
        private static readonly Dictionary<string, int> CategoryTierOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WrapperTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "composite",
            "optional",
            "recipe",
            "order",
            "definition",
            "ingredient",
            "item",
            "node",
            "custom",
            "combo",
            "easy",
            "dlc",
            "plain",
            "so",
            "new"
        };
        private static readonly HashSet<string> ComponentNoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bowl",
            "container",
            "cup",
            "dish",
            "icon",
            "model",
            "plate",
            "plating",
            "prefab",
            "serve",
            "serving"
        };

        private static readonly string[] FruitMarkers = new string[]
        {
            "apple",
            "banana",
            "blackberry",
            "blueberry",
            "berries",
            "berry",
            "cherry",
            "fruit",
            "grape",
            "grapes",
            "melon",
            "orange",
            "peach",
            "pineapple",
            "raspberry",
            "strawberry",
            "watermelon",
            "苹果",
            "水果",
            "香蕉",
            "黑莓",
            "蓝莓",
            "樱桃",
            "葡萄",
            "瓜",
            "橙",
            "桃",
            "菠萝",
            "树莓",
            "草莓",
            "西瓜",
            "boluo",
            "caomei",
            "lanmei",
            "pingguo",
            "putao",
            "xiangjiao",
            "xigua"
        };

        internal static Dictionary<int, RecipeCategoryAssignment> ClassifyDIYRecipes(IList<RecipeCategoryEvidence> evidence)
        {
            Dictionary<int, RecipeCategoryAssignment> assignments = new Dictionary<int, RecipeCategoryAssignment>();
            if (evidence == null || evidence.Count == 0)
            {
                return assignments;
            }

            List<RecipeCategoryEvidence> orderedEvidence = new List<RecipeCategoryEvidence>();
            for (int i = 0; i < evidence.Count; i++)
            {
                RecipeCategoryEvidence item = evidence[i];
                if (item != null && item.RecipeId != 0)
                {
                    orderedEvidence.Add(item);
                }
            }

            orderedEvidence.Sort(delegate(RecipeCategoryEvidence left, RecipeCategoryEvidence right)
            {
                int idCompare = left.RecipeId.CompareTo(right.RecipeId);
                if (idCompare != 0)
                {
                    return idCompare;
                }

                return string.Compare(left.InternalName, right.InternalName, StringComparison.OrdinalIgnoreCase);
            });

            List<RecipeCandidate> unresolved = new List<RecipeCandidate>();
            for (int i = 0; i < orderedEvidence.Count; i++)
            {
                RecipeCategoryEvidence item = orderedEvidence[i];
                if (assignments.ContainsKey(item.RecipeId))
                {
                    continue;
                }

                string nativeKey = GetKnownCategoryKey(item.InternalName);
                if (!string.IsNullOrEmpty(nativeKey))
                {
                    assignments.Add(
                        item.RecipeId,
                        CreateKnownAssignment(nativeKey, RecipeCategorySource.Native, nativeKey, "exact-name"));
                    continue;
                }

                string semanticKey = GetDIYSemanticCategoryKey(item);
                if (!string.IsNullOrEmpty(semanticKey))
                {
                    assignments.Add(
                        item.RecipeId,
                        CreateKnownAssignment(semanticKey, RecipeCategorySource.Semantic, semanticKey, "whole-token-name"));
                    continue;
                }

                unresolved.Add(new RecipeCandidate(item, TokenizeMeaningfulName(BuildEvidenceName(item))));
            }

            ReconcileWorkflowFamilies(orderedEvidence, assignments);
            AssignSceneFamilies(unresolved, assignments);
            AssignStructuralFamilies(unresolved, assignments);
            return assignments;
        }

        internal static RecipeCategoryAssignment ResolveKnownOrFallback(string internalName)
        {
            string key = GetKnownCategoryKey(internalName);
            return !string.IsNullOrEmpty(key)
                ? CreateKnownAssignment(key, RecipeCategorySource.Native)
                : CreateKnownAssignment("other", RecipeCategorySource.Fallback);
        }

        internal static string GetKnownCategoryKey(string internalName)
        {
            string value = NormalizeCategorySource(internalName);
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (StartsWith(value, "Sushi_Plain"))
            {
                return "sashimi";
            }

            if (StartsWith(value, "Sushi_"))
            {
                return "sushi";
            }

            if (StartsWith(value, "Cake_"))
            {
                return "cake";
            }

            if (StartsWith(value, "DLC13_MoonPie_") || StartsWith(value, "MoonPie_"))
            {
                return "moonpie";
            }

            if (StartsWith(value, "FruitPie_"))
            {
                return "fruitpie";
            }

            if (StartsWith(value, "Pancake_") || EndsWith(value, "Pancake"))
            {
                return "pancake";
            }

            if (StartsWith(value, "Steamed") || StartsWith(value, "SteamedSpecial_"))
            {
                return "steamed";
            }

            if (StartsWith(value, "Salad_") || StartsWith(value, "Tomato_"))
            {
                return "salad";
            }

            if (StartsWith(value, "Pasta_"))
            {
                return "pasta";
            }

            if (StartsWith(value, "HotPot_"))
            {
                return "hotpot";
            }

            if (StartsWith(value, "Hotdog_"))
            {
                return "hotdog";
            }

            if (StartsWith(value, "Smores_"))
            {
                return "smores";
            }

            if (EndsWith(value, "Smoothie"))
            {
                return "smoothie";
            }

            if (EndsWith(value, "Soup"))
            {
                return "soup";
            }

            if (EndsWith(value, "Roast"))
            {
                return "roast";
            }

            if (EndsWith(value, "Kebob"))
            {
                return "kebob";
            }

            if (StartsWith(value, "Breakfast_"))
            {
                return "breakfast";
            }

            if (StartsWith(value, "ChristmasPudding"))
            {
                return "dessert";
            }

            if (StartsWith(value, "HotChocolate"))
            {
                return "hotchocolate";
            }

            if (StartsWith(value, "OrangeSodaFloat_") || StartsWith(value, "RootBeerFloat_"))
            {
                return "float";
            }

            if (StartsWith(value, "FruitPlatter_"))
            {
                return "fruitplatter";
            }

            if (StartsWith(value, "ChickenNuggetsAndChips_") || StartsWith(value, "Fry_"))
            {
                return "fried";
            }

            if (StartsWith(value, "Donut_"))
            {
                return "donut";
            }

            if (EndsWith(value, "Pizza"))
            {
                return "pizza";
            }

            if (value.IndexOf("Burger", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "burger";
            }

            if (value.IndexOf("Burrito", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "burrito";
            }

            return string.Empty;
        }

        internal static string GetCategoryNameByKey(string categoryKey)
        {
            CategoryDefinition definition;
            return Definitions.TryGetValue(NormalizeKey(categoryKey), out definition)
                ? definition.ChineseName
                : Definitions["other"].ChineseName;
        }

        internal static string GetEnglishCategoryNameByKey(string categoryKey)
        {
            CategoryDefinition definition;
            return Definitions.TryGetValue(NormalizeKey(categoryKey), out definition)
                ? definition.EnglishName
                : Definitions["other"].EnglishName;
        }

        internal static string GetDisplayCategoryName(RecipeCategoryAssignment assignment, bool chinese)
        {
            if (assignment == null)
            {
                return chinese ? Definitions["other"].ChineseName : Definitions["other"].EnglishName;
            }

            return chinese ? assignment.ChineseName : assignment.EnglishName;
        }

        internal static string GetDisplayCategoryNameByKey(string categoryKey, bool chinese)
        {
            return chinese ? GetCategoryNameByKey(categoryKey) : GetEnglishCategoryNameByKey(categoryKey);
        }

        internal static int GetCategoryTierByKey(string categoryKey)
        {
            string tierKey = GetTierKey(categoryKey);
            int overrideTier;
            if (CategoryTierOverrides.TryGetValue(tierKey, out overrideTier))
            {
                return overrideTier;
            }

            return GetDefaultCategoryTierByKey(tierKey);
        }

        internal static int GetDefaultCategoryTierByKey(string categoryKey)
        {
            CategoryDefinition definition;
            string normalizedKey = NormalizeKey(categoryKey);
            if (!Definitions.TryGetValue(normalizedKey, out definition))
            {
                return 99;
            }

            if (definition.DefaultTier > 0)
            {
                return definition.DefaultTier;
            }

            return string.Equals(definition.TierKey, definition.Key, StringComparison.OrdinalIgnoreCase)
                ? 99
                : GetDefaultCategoryTierByKey(definition.TierKey);
        }

        internal static string[] GetOrderedCategoryKeys()
        {
            string[] copy = new string[OrderedCategoryKeys.Length];
            Array.Copy(OrderedCategoryKeys, copy, OrderedCategoryKeys.Length);
            return copy;
        }

        internal static void SetCategoryTierOverride(string categoryKey, int tier)
        {
            string normalizedKey = GetTierKey(categoryKey);
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return;
            }

            int defaultTier = GetDefaultCategoryTierByKey(normalizedKey);
            if (defaultTier == 99)
            {
                CategoryTierOverrides.Remove(normalizedKey);
                return;
            }

            int clampedTier = Math.Max(1, Math.Min(6, tier));
            if (clampedTier == defaultTier)
            {
                CategoryTierOverrides.Remove(normalizedKey);
            }
            else
            {
                CategoryTierOverrides[normalizedKey] = clampedTier;
            }
        }

        internal static bool AreEquivalent(RecipeCategoryAssignment left, RecipeCategoryAssignment right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return left != null
                && right != null
                && string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.EnglishName, right.EnglishName, StringComparison.Ordinal)
                && string.Equals(left.ChineseName, right.ChineseName, StringComparison.Ordinal)
                && string.Equals(left.TierKey, right.TierKey, StringComparison.OrdinalIgnoreCase)
                && left.Source == right.Source
                && string.Equals(left.InitialCategoryKey, right.InitialCategoryKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.InferenceDetail, right.InferenceDetail, StringComparison.Ordinal);
        }

        internal static string GetSourceName(RecipeCategorySource source)
        {
            switch (source)
            {
                case RecipeCategorySource.Native:
                    return "native";
                case RecipeCategorySource.Semantic:
                    return "semantic";
                case RecipeCategorySource.Workflow:
                    return "workflow";
                case RecipeCategorySource.Scene:
                    return "scene";
                case RecipeCategorySource.Structure:
                    return "structure";
                default:
                    return "fallback";
            }
        }

        internal static List<string> TokenizeMeaningfulName(string value)
        {
            List<string> rawTokens = Tokenize(value);
            List<string> tokens = new List<string>();
            for (int i = 0; i < rawTokens.Count; i++)
            {
                string token = rawTokens[i];
                if (WrapperTokens.Contains(token) || IsNumeric(token))
                {
                    continue;
                }

                if (IsCjkToken(token) && token.Length > 1)
                {
                    for (int j = 0; j < token.Length; j++)
                    {
                        tokens.Add(token[j].ToString());
                    }
                }
                else
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static Dictionary<string, CategoryDefinition> BuildDefinitions()
        {
            Dictionary<string, CategoryDefinition> definitions = new Dictionary<string, CategoryDefinition>(StringComparer.OrdinalIgnoreCase);
            AddDefinition(definitions, "pizza", "Pizza", "披萨", "pizza", 1);
            AddDefinition(definitions, "cake", "Cake", "蛋糕", "cake", 2);
            AddDefinition(definitions, "moonpie", "Mooncake", "月饼", "moonpie", 1);
            AddDefinition(definitions, "fruitpie", "Fruit Pie", "水果派", "fruitpie", 1);
            AddDefinition(definitions, "roast", "Roast", "烧烤", "roast", 2);
            AddDefinition(definitions, "fried", "Fry", "炸物", "fried", 2);
            AddDefinition(definitions, "pancake", "Pancake", "煎饼", "pancake", 1);
            AddDefinition(definitions, "dessert", "Dessert", "甜点", "dessert", 2);
            AddDefinition(definitions, "sushi", "Sushi", "寿司", "sushi", 3);
            AddDefinition(definitions, "steamed", "Steamed", "蒸菜", "steamed", 3);
            AddDefinition(definitions, "soup", "Soup", "汤", "soup", 3);
            AddDefinition(definitions, "hotpot", "Hot Pot", "火锅", "hotpot", 3);
            AddDefinition(definitions, "breakfast", "Breakfast", "早餐", "breakfast", 3);
            AddDefinition(definitions, "burger", "Burger", "汉堡", "burger", 4);
            AddDefinition(definitions, "burrito", "Burrito", "卷饼", "burrito", 4);
            AddDefinition(definitions, "kebob", "Skewer", "烤串", "kebob", 4);
            AddDefinition(definitions, "donut", "Donut", "甜甜圈", "donut", 4);
            AddDefinition(definitions, "salad", "Salad", "沙拉", "salad", 5);
            AddDefinition(definitions, "pasta", "Pasta", "意面", "pasta", 5);
            AddDefinition(definitions, "smoothie", "Smoothie", "奶昔", "smoothie", 5);
            AddDefinition(definitions, "hotdog", "Hot Dog", "热狗", "hotdog", 5);
            AddDefinition(definitions, "smores", "S'more", "饼干", "smores", 5);
            AddDefinition(definitions, "fruitplatter", "Fruit Platter", "水果拼盘", "fruitplatter", 5);
            AddDefinition(definitions, "sashimi", "Sashimi", "刺身", "sashimi", 6);
            AddDefinition(definitions, "hotchocolate", "Hot Chocolate", "热可可", "hotchocolate", 6);
            AddDefinition(definitions, "float", "Float", "冰淇淋汽水", "float", 6);

            AddDefinition(definitions, "coldchocolate", "Cold Chocolate", "冷巧克力", "hotchocolate", 0);
            AddDefinition(definitions, "milkdrink", "Milk Drinks", "牛奶饮品", "smoothie", 0);
            AddDefinition(definitions, "icemilk", "Ice Milk", "冰牛奶饮品", "smoothie", 0);
            AddDefinition(definitions, "fruitjuice", "Fruit Juice", "果汁", "smoothie", 0);
            AddDefinition(definitions, "hotfruitdrink", "Hot Fruit Drinks", "热水果饮品", "hotchocolate", 0);
            AddDefinition(definitions, "fruitice", "Fruit Ice", "水果冰品", "dessert", 0);
            AddDefinition(definitions, "dumpling", "Dumpling", "饺子", "steamed", 0);
            AddDefinition(definitions, "creampuff", "Cream Puff", "泡芙", "dessert", 0);
            AddDefinition(definitions, "porridge", "Porridge", "粥", "soup", 0);
            AddDefinition(definitions, "friedrice", "Fried Rice", "炒饭", "fried", 0);
            AddDefinition(definitions, "other", "Other", "其他", "other", 99);
            return definitions;
        }

        private static void AddDefinition(
            Dictionary<string, CategoryDefinition> definitions,
            string key,
            string englishName,
            string chineseName,
            string tierKey,
            int defaultTier)
        {
            definitions.Add(key, new CategoryDefinition(key, englishName, chineseName, tierKey, defaultTier));
        }

        private static RecipeCategoryAssignment CreateKnownAssignment(
            string categoryKey,
            RecipeCategorySource source,
            string initialCategoryKey = null,
            string inferenceDetail = null)
        {
            CategoryDefinition definition;
            string normalizedKey = NormalizeKey(categoryKey);
            if (!Definitions.TryGetValue(normalizedKey, out definition))
            {
                definition = Definitions["other"];
                source = RecipeCategorySource.Fallback;
            }

            return new RecipeCategoryAssignment(
                definition.Key,
                definition.EnglishName,
                definition.ChineseName,
                definition.TierKey,
                source,
                initialCategoryKey,
                inferenceDetail);
        }

        private static RecipeCategoryAssignment CreateInferredAssignment(
            string key,
            string label,
            string tierKey,
            RecipeCategorySource source,
            string inferenceDetail = null)
        {
            string resolvedLabel = string.IsNullOrEmpty(label) ? Definitions["other"].EnglishName : label;
            string resolvedTierKey = string.IsNullOrEmpty(tierKey) ? "other" : tierKey;
            return new RecipeCategoryAssignment(
                key,
                resolvedLabel,
                resolvedLabel,
                resolvedTierKey,
                source,
                string.Empty,
                inferenceDetail);
        }

        private static string GetDIYSemanticCategoryKey(RecipeCategoryEvidence evidence)
        {
            string searchable = BuildSearchableName(BuildEvidenceName(evidence));

            if (ContainsAny(searchable, "冷巧克力")
                || HasToken(searchable, "coldchocolate")
                || HasTokenSequence(searchable, "cold", "chocolate"))
            {
                return "coldchocolate";
            }

            if (ContainsAny(searchable, "热巧克力", "热可可")
                || HasToken(searchable, "hotchocolate")
                || HasTokenSequence(searchable, "hot", "chocolate")
                || HasTokenSequence(searchable, "hot", "cocoa"))
            {
                return "hotchocolate";
            }

            if (ContainsAny(searchable, "月饼")
                || HasToken(searchable, "mooncake")
                || HasToken(searchable, "moonpie")
                || HasTokenSequence(searchable, "moon", "cake")
                || HasTokenSequence(searchable, "moon", "pie")
                || HasToken(searchable, "yuebing"))
            {
                return "moonpie";
            }

            if (HasToken(searchable, "fruitpie") || HasTokenSequence(searchable, "fruit", "pie"))
            {
                return "fruitpie";
            }

            if (HasToken(searchable, "pancake")
                || HasTokenSequence(searchable, "pan", "cake")
                || (HasToken(searchable, "pan") && HasTokenPrefix(searchable, "cake"))
                || HasToken(searchable, "dorayaki"))
            {
                return "pancake";
            }

            if (ContainsAny(searchable, "甜甜圈") || HasToken(searchable, "donut") || HasToken(searchable, "donuts"))
            {
                return "donut";
            }

            if (ContainsAny(searchable, "水果拼盘")
                || HasToken(searchable, "fruitplatter")
                || HasTokenSequence(searchable, "fruit", "platter")
                || HasToken(searchable, "platter"))
            {
                return "fruitplatter";
            }

            if (ContainsAny(searchable, "蛋糕") || HasToken(searchable, "cake") || HasToken(searchable, "dangao"))
            {
                return "cake";
            }

            if (ContainsAny(searchable, "饺子", "水饺")
                || HasToken(searchable, "dumpling")
                || HasToken(searchable, "dumplings")
                || HasTokenPrefix(searchable, "dumpling")
                || HasToken(searchable, "jiaozi"))
            {
                return "dumpling";
            }

            if (ContainsAny(searchable, "泡芙")
                || HasToken(searchable, "creampuff")
                || HasToken(searchable, "creampuffs")
                || HasTokenSequence(searchable, "cream", "puff")
                || HasTokenSequence(searchable, "cream", "puffs")
                || HasToken(searchable, "paofu"))
            {
                return "creampuff";
            }

            if (ContainsAny(searchable, "炒饭")
                || HasToken(searchable, "friedrice")
                || HasTokenSequence(searchable, "fried", "rice")
                || HasToken(searchable, "chaofan"))
            {
                return "friedrice";
            }

            if (ContainsAny(searchable, "粥")
                || HasToken(searchable, "porridge")
                || HasToken(searchable, "zhou"))
            {
                return "porridge";
            }

            if (ContainsAny(searchable, "刺身") || HasToken(searchable, "sashimi"))
            {
                return "sashimi";
            }

            if (ContainsAny(searchable, "寿司") || HasToken(searchable, "sushi"))
            {
                return "sushi";
            }

            if (ContainsAny(searchable, "火锅")
                || HasToken(searchable, "hotpot")
                || HasTokenSequence(searchable, "hot", "pot"))
            {
                return "hotpot";
            }

            if (ContainsAny(searchable, "汤") || HasToken(searchable, "borscht") || HasToken(searchable, "soup"))
            {
                return "soup";
            }

            if (ContainsAny(searchable, "蒸") || HasToken(searchable, "steamed") || HasToken(searchable, "steamer"))
            {
                return "steamed";
            }

            if (ContainsAny(searchable, "汉堡") || HasToken(searchable, "burger"))
            {
                return "burger";
            }

            if (ContainsAny(searchable, "卷饼") || HasToken(searchable, "burrito"))
            {
                return "burrito";
            }

            if (ContainsAny(searchable, "烤串")
                || HasToken(searchable, "kebob")
                || HasToken(searchable, "kebab"))
            {
                return "kebob";
            }

            if (ContainsAny(searchable, "披萨") || HasToken(searchable, "pizza"))
            {
                return "pizza";
            }

            if (ContainsAny(searchable, "早餐") || HasToken(searchable, "breakfast"))
            {
                return "breakfast";
            }

            if (ContainsAny(searchable, "沙拉") || HasToken(searchable, "salad"))
            {
                return "salad";
            }

            if (ContainsAny(searchable, "意面", "面条") || HasToken(searchable, "pasta") || HasToken(searchable, "noodle") || HasToken(searchable, "noodles"))
            {
                return "pasta";
            }

            if (ContainsAny(searchable, "奶昔")
                || HasToken(searchable, "smoothie")
                || (HasToken(searchable, "bing") && HasToken(searchable, "sha")))
            {
                return "smoothie";
            }

            if (ContainsAny(searchable, "热狗")
                || HasToken(searchable, "hotdog")
                || HasTokenSequence(searchable, "hot", "dog"))
            {
                return "hotdog";
            }

            if (HasToken(searchable, "smores") || HasToken(searchable, "smore"))
            {
                return "smores";
            }

            if (ContainsAny(searchable, "烧烤", "烤肉")
                || HasToken(searchable, "barbecue")
                || HasToken(searchable, "bbq")
                || HasToken(searchable, "roast")
                || HasTokenPrefix(searchable, "kaochang")
                || HasTokenPrefix(searchable, "kaorou")
                || HasTokenPrefix(searchable, "kaoyu"))
            {
                return "roast";
            }

            if (ContainsAny(searchable, "炸")
                || HasToken(searchable, "fried")
                || HasToken(searchable, "fry")
                || HasToken(searchable, "zhayu"))
            {
                return "fried";
            }

            if (ContainsAny(searchable, "冰牛奶", "冰奶")
                || HasToken(searchable, "icemilk")
                || HasTokenSequence(searchable, "ice", "milk"))
            {
                return "icemilk";
            }

            if ((ContainsAny(searchable, "汁加冰", "加冰", "冰品") || EndsWithToken(searchable, "ice"))
                && ContainsFruitMarker(searchable))
            {
                return "fruitice";
            }

            if (ContainsAny(searchable, "果汁") || HasToken(searchable, "juice") || HasToken(searchable, "guozhi"))
            {
                return "fruitjuice";
            }

            if (ContainsAny(searchable, "牛奶") || HasToken(searchable, "milk"))
            {
                return "milkdrink";
            }

            if ((HasToken(searchable, "hot") || ContainsAny(searchable, "热"))
                && ContainsFruitMarker(searchable))
            {
                return "hotfruitdrink";
            }

            if (ContainsAny(searchable, "甜点")
                || HasToken(searchable, "dessert")
                || HasToken(searchable, "eggtart")
                || HasTokenSequence(searchable, "egg", "tart"))
            {
                return "dessert";
            }

            return string.Empty;
        }

        /// <summary>
        /// Reconciles only high-confidence DIY semantic outliers against stable
        /// scene families. Profiles are built once from the initial assignments so
        /// provider ordering and earlier reassignments cannot affect later results.
        /// </summary>
        private static void ReconcileWorkflowFamilies(
            List<RecipeCategoryEvidence> orderedEvidence,
            Dictionary<int, RecipeCategoryAssignment> assignments)
        {
            List<WorkflowRecipe> recipes = new List<WorkflowRecipe>();
            Dictionary<string, WorkflowFamily> families =
                new Dictionary<string, WorkflowFamily>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> componentFrequencies =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < orderedEvidence.Count; i++)
            {
                RecipeCategoryEvidence evidence = orderedEvidence[i];
                RecipeCategoryAssignment assignment;
                if (!assignments.TryGetValue(evidence.RecipeId, out assignment)
                    || (assignment.Source != RecipeCategorySource.Native
                        && assignment.Source != RecipeCategorySource.Semantic))
                {
                    continue;
                }

                Dictionary<string, int> componentWeights = BuildComponentWeights(evidence);
                WorkflowRecipe recipe = new WorkflowRecipe(
                    evidence,
                    assignment.Key,
                    assignment.Source,
                    BuildWorkflowFacets(evidence),
                    componentWeights,
                    GetWorkflowProcessIdentity(evidence));
                recipes.Add(recipe);

                WorkflowFamily family;
                if (!families.TryGetValue(assignment.Key, out family))
                {
                    family = new WorkflowFamily(assignment.Key);
                    families.Add(assignment.Key, family);
                }

                family.Members.Add(recipe);
                foreach (string componentKey in componentWeights.Keys)
                {
                    int frequency;
                    componentFrequencies.TryGetValue(componentKey, out frequency);
                    componentFrequencies[componentKey] = frequency + 1;
                }
            }

            if (recipes.Count < 5 || families.Count < 2)
            {
                return;
            }

            HashSet<string> sceneCommonComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> pair in componentFrequencies)
            {
                if (pair.Value * 2 > recipes.Count)
                {
                    sceneCommonComponents.Add(pair.Key);
                }
            }

            List<string> orderedFamilyKeys = new List<string>(families.Keys);
            orderedFamilyKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < recipes.Count; i++)
            {
                WorkflowRecipe recipe = recipes[i];
                if (recipe.Source != RecipeCategorySource.Semantic
                    || recipe.Evidence.Kind == DIYRecipeKind.Unknown
                    || string.IsNullOrEmpty(recipe.ProcessIdentity)
                    || recipe.Facets.Count == 0)
                {
                    continue;
                }

                WorkflowFamily currentFamily = families[recipe.CategoryKey];
                List<string> outlierFacets = GetWorkflowOutlierFacets(recipe, currentFamily);
                if (outlierFacets.Count == 0)
                {
                    continue;
                }

                WorkflowTargetScore best = null;
                bool bestIsTied = false;
                for (int familyIndex = 0; familyIndex < orderedFamilyKeys.Count; familyIndex++)
                {
                    string targetKey = orderedFamilyKeys[familyIndex];
                    if (string.Equals(targetKey, recipe.CategoryKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    WorkflowTargetScore score = BuildWorkflowTargetScore(
                        recipe,
                        families[targetKey],
                        outlierFacets,
                        sceneCommonComponents,
                        true);
                    if (score == null)
                    {
                        continue;
                    }

                    int comparison = CompareWorkflowTargetScores(score, best);
                    if (comparison > 0)
                    {
                        best = score;
                        bestIsTied = false;
                    }
                    else if (comparison == 0)
                    {
                        bestIsTied = true;
                    }
                }

                if (best == null || bestIsTied)
                {
                    continue;
                }

                WorkflowTargetScore current = BuildWorkflowTargetScore(
                    recipe,
                    currentFamily,
                    outlierFacets,
                    sceneCommonComponents,
                    false);
                if (current != null && CompareWorkflowComponentEvidence(best, current) <= 0)
                {
                    continue;
                }

                string detail = "from=" + recipe.CategoryKey
                    + ";facet=" + best.Facet
                    + ";kind=" + recipe.Evidence.Kind.ToString().ToLowerInvariant()
                    + ";process=" + best.ProcessIdentity
                    + ";components=" + JoinWorkflowComponents(best.SharedComponents);
                assignments[recipe.Evidence.RecipeId] = CreateKnownAssignment(
                    best.CategoryKey,
                    RecipeCategorySource.Workflow,
                    recipe.CategoryKey,
                    detail);
            }
        }

        private static List<string> GetWorkflowOutlierFacets(WorkflowRecipe recipe, WorkflowFamily currentFamily)
        {
            List<string> outliers = new List<string>();
            int peerCount = currentFamily.Members.Count - 1;
            if (peerCount < 2)
            {
                return outliers;
            }

            for (int facetIndex = 0; facetIndex < recipe.Facets.Count; facetIndex++)
            {
                string facet = recipe.Facets[facetIndex];
                int support = 0;
                for (int memberIndex = 0; memberIndex < currentFamily.Members.Count; memberIndex++)
                {
                    WorkflowRecipe peer = currentFamily.Members[memberIndex];
                    if (peer.Evidence.RecipeId != recipe.Evidence.RecipeId && peer.Facets.Contains(facet))
                    {
                        support++;
                    }
                }

                if (support < 2 || support * 2 <= peerCount)
                {
                    outliers.Add(facet);
                }
            }

            return outliers;
        }

        private static WorkflowTargetScore BuildWorkflowTargetScore(
            WorkflowRecipe recipe,
            WorkflowFamily targetFamily,
            List<string> outlierFacets,
            HashSet<string> sceneCommonComponents,
            bool requireFacetMatch)
        {
            List<WorkflowRecipe> compatibleMembers = new List<WorkflowRecipe>();
            int eligibleMemberCount = 0;
            for (int i = 0; i < targetFamily.Members.Count; i++)
            {
                WorkflowRecipe member = targetFamily.Members[i];
                if (member.Evidence.RecipeId == recipe.Evidence.RecipeId)
                {
                    continue;
                }

                eligibleMemberCount++;
                if (member.Evidence.Kind != recipe.Evidence.Kind || string.IsNullOrEmpty(member.ProcessIdentity))
                {
                    continue;
                }

                if (string.Equals(member.ProcessIdentity, recipe.ProcessIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    compatibleMembers.Add(member);
                }
            }

            if (compatibleMembers.Count < 2 || compatibleMembers.Count * 2 <= eligibleMemberCount)
            {
                return null;
            }

            string selectedFacet = string.Empty;
            int selectedFacetSupport = 0;
            for (int facetIndex = 0; requireFacetMatch && facetIndex < outlierFacets.Count; facetIndex++)
            {
                string facet = outlierFacets[facetIndex];
                int support = 0;
                for (int memberIndex = 0; memberIndex < compatibleMembers.Count; memberIndex++)
                {
                    if (compatibleMembers[memberIndex].Facets.Contains(facet))
                    {
                        support++;
                    }
                }

                if (support >= 2
                    && support * 2 > compatibleMembers.Count
                    && (support > selectedFacetSupport
                        || (support == selectedFacetSupport
                            && string.Compare(facet, selectedFacet, StringComparison.Ordinal) < 0)))
                {
                    selectedFacet = facet;
                    selectedFacetSupport = support;
                }
            }

            if (requireFacetMatch && string.IsNullOrEmpty(selectedFacet))
            {
                return null;
            }

            List<string> nameTokens = TokenizeMeaningfulName(BuildEvidenceName(recipe.Evidence));
            Dictionary<string, int> occurrenceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> accumulatedWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int memberIndex = 0; memberIndex < compatibleMembers.Count; memberIndex++)
            {
                foreach (KeyValuePair<string, int> component in compatibleMembers[memberIndex].ComponentWeights)
                {
                    if (sceneCommonComponents.Contains(component.Key)
                        && !ComponentMatchesNameTokens(component.Key, nameTokens))
                    {
                        continue;
                    }

                    int occurrences;
                    occurrenceCounts.TryGetValue(component.Key, out occurrences);
                    occurrenceCounts[component.Key] = occurrences + 1;

                    int accumulated;
                    accumulatedWeights.TryGetValue(component.Key, out accumulated);
                    accumulatedWeights[component.Key] = accumulated + component.Value;
                }
            }

            int namedCoreCount = 0;
            int sharedBaseCount = 0;
            int weightedOverlap = 0;
            List<string> sharedComponents = new List<string>();
            foreach (KeyValuePair<string, int> component in recipe.ComponentWeights)
            {
                int occurrences;
                int accumulated;
                if ((sceneCommonComponents.Contains(component.Key)
                        && !ComponentMatchesNameTokens(component.Key, nameTokens))
                    || !occurrenceCounts.TryGetValue(component.Key, out occurrences)
                    || !accumulatedWeights.TryGetValue(component.Key, out accumulated))
                {
                    continue;
                }

                sharedComponents.Add(component.Key);
                weightedOverlap += Math.Min(component.Value * compatibleMembers.Count, accumulated);
                if (component.Value >= 3 && occurrences >= 2)
                {
                    sharedBaseCount++;
                }

                if (occurrences * 2 >= compatibleMembers.Count
                    && ComponentMatchesNameTokens(component.Key, nameTokens))
                {
                    namedCoreCount++;
                }
            }

            if (sharedBaseCount == 0 || weightedOverlap == 0)
            {
                return null;
            }

            sharedComponents.Sort(StringComparer.Ordinal);
            return new WorkflowTargetScore(
                targetFamily.Key,
                selectedFacet,
                recipe.ProcessIdentity,
                namedCoreCount,
                sharedBaseCount,
                weightedOverlap,
                compatibleMembers.Count,
                selectedFacetSupport,
                sharedComponents);
        }

        private static int CompareWorkflowTargetScores(WorkflowTargetScore candidate, WorkflowTargetScore current)
        {
            if (candidate == null)
            {
                return current == null ? 0 : -1;
            }

            if (current == null)
            {
                return 1;
            }

            int comparison = CompareWorkflowComponentEvidence(candidate, current);
            if (comparison != 0)
            {
                return comparison;
            }

            long candidateFacetSupport = (long)candidate.FacetSupportCount * current.CompatibleMemberCount;
            long currentFacetSupport = (long)current.FacetSupportCount * candidate.CompatibleMemberCount;
            comparison = candidateFacetSupport.CompareTo(currentFacetSupport);
            return comparison != 0
                ? comparison
                : candidate.CompatibleMemberCount.CompareTo(current.CompatibleMemberCount);
        }

        private static int CompareWorkflowComponentEvidence(
            WorkflowTargetScore candidate,
            WorkflowTargetScore current)
        {
            int comparison = candidate.NamedCoreCount.CompareTo(current.NamedCoreCount);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = candidate.SharedBaseCount.CompareTo(current.SharedBaseCount);
            if (comparison != 0)
            {
                return comparison;
            }

            long candidateOverlap = (long)candidate.WeightedOverlap * current.CompatibleMemberCount;
            long currentOverlap = (long)current.WeightedOverlap * candidate.CompatibleMemberCount;
            return candidateOverlap.CompareTo(currentOverlap);
        }

        private static Dictionary<string, int> BuildComponentWeights(RecipeCategoryEvidence evidence)
        {
            Dictionary<string, int> weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < evidence.Components.Count; i++)
            {
                RecipeComponentEvidence component = evidence.Components[i];
                if (component == null)
                {
                    continue;
                }

                string key = NormalizeComponentIdentity(component.Identity);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                int weight = component.IsOptional
                    ? (component.Depth == 0 ? 2 : 1)
                    : (component.Depth == 0 ? 4 : 3);
                int existingWeight;
                if (!weights.TryGetValue(key, out existingWeight) || weight > existingWeight)
                {
                    weights[key] = weight;
                }
            }

            return weights;
        }

        private static string NormalizeComponentIdentity(string identity)
        {
            List<string> tokens = TokenizeMeaningfulName(identity);
            List<string> meaningfulTokens = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!ComponentNoiseTokens.Contains(tokens[i]))
                {
                    meaningfulTokens.Add(tokens[i]);
                }
            }

            return meaningfulTokens.Count == 0
                ? string.Empty
                : JoinTokens(meaningfulTokens, 0, meaningfulTokens.Count, "-");
        }

        private static bool ComponentMatchesNameTokens(string componentKey, List<string> nameTokens)
        {
            List<string> componentTokens = Tokenize(componentKey);
            for (int componentIndex = 0; componentIndex < componentTokens.Count; componentIndex++)
            {
                for (int nameIndex = 0; nameIndex < nameTokens.Count; nameIndex++)
                {
                    if (string.Equals(componentTokens[componentIndex], nameTokens[nameIndex], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<string> BuildWorkflowFacets(RecipeCategoryEvidence evidence)
        {
            string searchable = BuildSearchableName(BuildEvidenceName(evidence));
            List<string> facets = new List<string>();
            AddWorkflowFacet(facets, "hot", HasToken(searchable, "hot") || ContainsAny(searchable, "热"));
            AddWorkflowFacet(facets, "cold", HasToken(searchable, "cold") || ContainsAny(searchable, "冷"));
            AddWorkflowFacet(
                facets,
                "ice",
                HasToken(searchable, "ice") || HasToken(searchable, "iced") || ContainsAny(searchable, "冰", "加冰"));
            AddWorkflowFacet(
                facets,
                "fried",
                HasToken(searchable, "fried") || HasToken(searchable, "fry") || ContainsAny(searchable, "炸", "煎"));
            AddWorkflowFacet(
                facets,
                "steamed",
                HasToken(searchable, "steam") || HasToken(searchable, "steamed") || ContainsAny(searchable, "蒸"));
            AddWorkflowFacet(
                facets,
                "baked",
                HasToken(searchable, "bake") || HasToken(searchable, "baked") || HasToken(searchable, "roast")
                    || HasToken(searchable, "grill") || HasToken(searchable, "grilled") || ContainsAny(searchable, "烤"));
            AddWorkflowFacet(
                facets,
                "mixed",
                HasToken(searchable, "mix") || HasToken(searchable, "mixed") || HasToken(searchable, "blend")
                    || HasToken(searchable, "blended") || ContainsAny(searchable, "搅拌", "混合"));
            AddWorkflowFacet(
                facets,
                "assembled",
                HasToken(searchable, "assemble") || HasToken(searchable, "assembled")
                    || HasToken(searchable, "composite") || ContainsAny(searchable, "拼装", "组合"));
            return facets;
        }

        private static void AddWorkflowFacet(List<string> facets, string facet, bool present)
        {
            if (present && !facets.Contains(facet))
            {
                facets.Add(facet);
            }
        }

        private static string GetWorkflowProcessIdentity(RecipeCategoryEvidence evidence)
        {
            string identity;
            string prefix;
            switch (evidence.Kind)
            {
                case DIYRecipeKind.Cooked:
                    identity = evidence.CookingIdentity;
                    prefix = "cook:";
                    break;
                case DIYRecipeKind.Mixed:
                    identity = evidence.MixingIdentity;
                    prefix = "mix:";
                    break;
                case DIYRecipeKind.Composite:
                    identity = FirstNonEmpty(
                        evidence.PlatingIdentity,
                        evidence.ModelIdentity,
                        evidence.IconIdentity);
                    prefix = "assemble:";
                    break;
                default:
                    return string.Empty;
            }

            return string.IsNullOrEmpty(identity) ? string.Empty : prefix + NormalizeIdentity(identity);
        }

        private static string BuildEvidenceName(RecipeCategoryEvidence evidence)
        {
            if (evidence == null || string.IsNullOrEmpty(evidence.AuthoringName)
                || string.Equals(evidence.InternalName, evidence.AuthoringName, StringComparison.OrdinalIgnoreCase))
            {
                return evidence == null ? string.Empty : evidence.InternalName;
            }

            return evidence.InternalName + " " + evidence.AuthoringName;
        }

        private static string JoinWorkflowComponents(List<string> components)
        {
            if (components == null || components.Count == 0)
            {
                return "none";
            }

            int count = Math.Min(3, components.Count);
            return JoinTokens(components, 0, count, ",");
        }

        private static void AssignSceneFamilies(
            List<RecipeCandidate> unresolved,
            Dictionary<int, RecipeCategoryAssignment> assignments)
        {
            Dictionary<string, InferenceCandidate> candidates = new Dictionary<string, InferenceCandidate>(StringComparer.Ordinal);
            Dictionary<int, List<string>> keysByRecipe = new Dictionary<int, List<string>>();
            for (int i = 0; i < unresolved.Count; i++)
            {
                RecipeCandidate recipe = unresolved[i];
                RecipeCategoryEvidence evidence = recipe.Evidence;
                if (assignments.ContainsKey(evidence.RecipeId))
                {
                    continue;
                }

                List<string> recipeKeys = BuildBoundaryCandidateKeys(recipe.Tokens, candidates);
                AppendComponentCandidateKeys(evidence.Components, recipeKeys, candidates);
                keysByRecipe[evidence.RecipeId] = recipeKeys;
            }

            CountUniqueRecipeCandidates(keysByRecipe, candidates);
            Dictionary<int, InferenceCandidate> bestByRecipe = new Dictionary<int, InferenceCandidate>();
            Dictionary<string, Dictionary<string, int>> tierCountsByCandidate =
                new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            for (int i = 0; i < unresolved.Count; i++)
            {
                RecipeCategoryEvidence evidence = unresolved[i].Evidence;
                if (assignments.ContainsKey(evidence.RecipeId))
                {
                    continue;
                }

                List<string> recipeKeys;
                if (!keysByRecipe.TryGetValue(evidence.RecipeId, out recipeKeys))
                {
                    continue;
                }

                InferenceCandidate best = SelectBestCandidate(recipeKeys, candidates);
                if (best == null || best.RecipeCount < 2)
                {
                    continue;
                }

                bestByRecipe[evidence.RecipeId] = best;
                if (evidence.Kind == DIYRecipeKind.Unknown)
                {
                    continue;
                }

                Dictionary<string, int> tierCounts;
                if (!tierCountsByCandidate.TryGetValue(best.Key, out tierCounts))
                {
                    tierCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    tierCountsByCandidate.Add(best.Key, tierCounts);
                }

                string tierKey = GetTierKeyForKind(evidence.Kind);
                int tierCount;
                tierCounts.TryGetValue(tierKey, out tierCount);
                tierCounts[tierKey] = tierCount + 1;
            }

            Dictionary<string, string> tierKeyByCandidate = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, int>> pair in tierCountsByCandidate)
            {
                string selectedTierKey = string.Empty;
                int selectedTierCount = -1;
                foreach (KeyValuePair<string, int> tierPair in pair.Value)
                {
                    if (tierPair.Value > selectedTierCount
                        || (tierPair.Value == selectedTierCount
                            && string.Compare(tierPair.Key, selectedTierKey, StringComparison.Ordinal) < 0))
                    {
                        selectedTierKey = tierPair.Key;
                        selectedTierCount = tierPair.Value;
                    }
                }

                tierKeyByCandidate[pair.Key] = selectedTierKey;
            }

            for (int i = 0; i < unresolved.Count; i++)
            {
                RecipeCategoryEvidence evidence = unresolved[i].Evidence;
                InferenceCandidate best;
                if (assignments.ContainsKey(evidence.RecipeId)
                    || !bestByRecipe.TryGetValue(evidence.RecipeId, out best))
                {
                    continue;
                }

                string tierKey;
                if (!tierKeyByCandidate.TryGetValue(best.Key, out tierKey))
                {
                    tierKey = "other";
                }

                assignments.Add(
                    evidence.RecipeId,
                    CreateInferredAssignment(
                        "diy-scene:" + best.Key,
                        best.Label,
                        tierKey,
                        RecipeCategorySource.Scene));
            }
        }

        private static void AssignStructuralFamilies(
            List<RecipeCandidate> unresolved,
            Dictionary<int, RecipeCategoryAssignment> assignments)
        {
            for (int i = 0; i < unresolved.Count; i++)
            {
                RecipeCategoryEvidence evidence = unresolved[i].Evidence;
                if (assignments.ContainsKey(evidence.RecipeId))
                {
                    continue;
                }

                string key;
                string englishName;
                string chineseName;
                string tierKey;
                switch (evidence.Kind)
                {
                    case DIYRecipeKind.Composite:
                        string assemblyIdentity = FirstNonEmpty(
                            evidence.PlatingIdentity,
                            evidence.ModelIdentity,
                            evidence.IconIdentity);
                        key = "diy-structure:composite:" + NormalizeIdentity(assemblyIdentity);
                        englishName = BuildStructuralLabel("Assembled DIY", assemblyIdentity);
                        chineseName = BuildStructuralLabel("DIY 拼装", assemblyIdentity);
                        tierKey = "salad";
                        break;
                    case DIYRecipeKind.Cooked:
                        string cookingIdentity = FirstNonEmpty(
                            evidence.CookingIdentity,
                            evidence.PlatingIdentity,
                            evidence.ModelIdentity,
                            evidence.IconIdentity);
                        key = "diy-structure:cooked:" + NormalizeIdentity(cookingIdentity);
                        englishName = BuildStructuralLabel("Cooked DIY", cookingIdentity);
                        chineseName = BuildStructuralLabel("DIY 烹饪", cookingIdentity);
                        tierKey = "roast";
                        break;
                    case DIYRecipeKind.Mixed:
                        string mixingIdentity = FirstNonEmpty(
                            evidence.MixingIdentity,
                            evidence.PlatingIdentity,
                            evidence.ModelIdentity,
                            evidence.IconIdentity);
                        key = "diy-structure:mixed:" + NormalizeIdentity(mixingIdentity);
                        englishName = BuildStructuralLabel("Mixed DIY", mixingIdentity);
                        chineseName = BuildStructuralLabel("DIY 搅拌", mixingIdentity);
                        tierKey = "smoothie";
                        break;
                    default:
                        string identity = FirstNonEmpty(evidence.PlatingIdentity, evidence.ModelIdentity, evidence.IconIdentity);
                        if (string.IsNullOrEmpty(identity))
                        {
                            List<string> authoredTokens = TokenizeMeaningfulName(BuildEvidenceName(evidence));
                            if (authoredTokens.Count == 0)
                            {
                                assignments.Add(evidence.RecipeId, CreateKnownAssignment("other", RecipeCategorySource.Fallback));
                                continue;
                            }

                            string authoredKey = JoinTokens(authoredTokens, 0, authoredTokens.Count, "-");
                            assignments.Add(
                                evidence.RecipeId,
                                CreateInferredAssignment(
                                    "diy-name:" + authoredKey,
                                    HumanizeTokens(authoredTokens, 0, authoredTokens.Count),
                                    "other",
                                    RecipeCategorySource.Fallback));
                            continue;
                        }

                        key = "diy-structure:unknown:" + NormalizeIdentity(identity);
                        englishName = BuildStructuralLabel("DIY", identity);
                        chineseName = englishName;
                        tierKey = "other";
                        break;
                }

                assignments.Add(
                    evidence.RecipeId,
                    new RecipeCategoryAssignment(key, englishName, chineseName, tierKey, RecipeCategorySource.Structure));
            }
        }

        private static List<string> BuildBoundaryCandidateKeys(
            List<string> tokens,
            Dictionary<string, InferenceCandidate> candidates)
        {
            List<string> keys = new List<string>();
            int maximumLength = tokens.Count;
            for (int length = 1; length <= maximumLength; length++)
            {
                AddInferenceCandidate("name", tokens, 0, length, keys, candidates);
                int suffixStart = tokens.Count - length;
                if (suffixStart != 0)
                {
                    AddInferenceCandidate("name", tokens, suffixStart, length, keys, candidates);
                }
            }

            return keys;
        }

        private static void AddInferenceCandidate(
            string origin,
            List<string> tokens,
            int start,
            int length,
            List<string> recipeKeys,
            Dictionary<string, InferenceCandidate> candidates)
        {
            string stem = JoinTokens(tokens, start, length, "-");
            if (stem.Length < 3)
            {
                return;
            }

            string key = origin + ":" + stem;
            if (!recipeKeys.Contains(key))
            {
                recipeKeys.Add(key);
            }

            if (!candidates.ContainsKey(key))
            {
                candidates.Add(key, new InferenceCandidate(key, HumanizeTokens(tokens, start, length), length, stem.Length));
            }
        }

        private static void AppendComponentCandidateKeys(
            List<RecipeComponentEvidence> components,
            List<string> recipeKeys,
            Dictionary<string, InferenceCandidate> candidates)
        {
            for (int i = 0; i < components.Count; i++)
            {
                RecipeComponentEvidence component = components[i];
                if (component == null)
                {
                    continue;
                }

                List<string> tokens = TokenizeMeaningfulName(component.Identity);
                if (tokens.Count == 0)
                {
                    continue;
                }

                string stem = JoinTokens(tokens, 0, tokens.Count, "-");
                if (stem.Length < 3)
                {
                    continue;
                }

                string key = "component:" + stem;
                if (!recipeKeys.Contains(key))
                {
                    recipeKeys.Add(key);
                }

                if (!candidates.ContainsKey(key))
                {
                    candidates.Add(key, new InferenceCandidate(key, HumanizeTokens(tokens, 0, tokens.Count), tokens.Count, stem.Length));
                }
            }
        }

        private static void CountUniqueRecipeCandidates(
            Dictionary<int, List<string>> keysByRecipe,
            Dictionary<string, InferenceCandidate> candidates)
        {
            foreach (KeyValuePair<int, List<string>> pair in keysByRecipe)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    string key = pair.Value[i];
                    InferenceCandidate candidate;
                    if (seen.Add(key) && candidates.TryGetValue(key, out candidate))
                    {
                        candidate.RecipeCount++;
                    }
                }
            }
        }

        private static InferenceCandidate SelectBestCandidate(
            List<string> recipeKeys,
            Dictionary<string, InferenceCandidate> candidates)
        {
            InferenceCandidate best = null;
            for (int i = 0; i < recipeKeys.Count; i++)
            {
                InferenceCandidate candidate;
                if (!candidates.TryGetValue(recipeKeys[i], out candidate) || candidate.RecipeCount < 2)
                {
                    continue;
                }

                if (best == null
                    || candidate.TokenCount > best.TokenCount
                    || (candidate.TokenCount == best.TokenCount && candidate.CharacterCount > best.CharacterCount)
                    || (candidate.TokenCount == best.TokenCount && candidate.CharacterCount == best.CharacterCount && candidate.RecipeCount > best.RecipeCount)
                    || (candidate.TokenCount == best.TokenCount && candidate.CharacterCount == best.CharacterCount && candidate.RecipeCount == best.RecipeCount
                        && string.Compare(candidate.Key, best.Key, StringComparison.Ordinal) < 0))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static string BuildSearchableName(string internalName)
        {
            List<string> tokens = Tokenize(internalName);
            return tokens.Count == 0 ? string.Empty : JoinTokens(tokens, 0, tokens.Count, " ");
        }

        private static List<string> Tokenize(string value)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(value))
            {
                return tokens;
            }

            StringBuilder current = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (!char.IsLetterOrDigit(character))
                {
                    FlushToken(current, tokens);
                    continue;
                }

                bool boundary = current.Length > 0
                    && i > 0
                    && ((char.IsUpper(character) && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1])))
                        || (char.IsDigit(character) && char.IsLetter(value[i - 1]))
                        || (char.IsLetter(character) && char.IsDigit(value[i - 1]))
                        || (IsCjk(character) != IsCjk(value[i - 1]) && (IsCjk(character) || IsCjk(value[i - 1]))));
                if (boundary)
                {
                    FlushToken(current, tokens);
                }

                current.Append(char.ToLowerInvariant(character));
            }

            FlushToken(current, tokens);
            return tokens;
        }

        private static void FlushToken(StringBuilder current, List<string> tokens)
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Length = 0;
        }

        private static bool HasToken(string searchable, string expected)
        {
            List<string> tokens = Tokenize(searchable);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (string.Equals(tokens[i], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTokenPrefix(string searchable, string expectedPrefix)
        {
            List<string> tokens = Tokenize(searchable);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTokenSequence(string searchable, params string[] expectedTokens)
        {
            if (expectedTokens == null || expectedTokens.Length == 0)
            {
                return false;
            }

            List<string> tokens = Tokenize(searchable);
            for (int start = 0; start <= tokens.Count - expectedTokens.Length; start++)
            {
                bool matches = true;
                for (int i = 0; i < expectedTokens.Length; i++)
                {
                    if (!string.Equals(tokens[start + i], expectedTokens[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EndsWithToken(string searchable, string expected)
        {
            List<string> tokens = Tokenize(searchable);
            return tokens.Count > 0 && string.Equals(tokens[tokens.Count - 1], expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsFruitMarker(string searchable)
        {
            for (int i = 0; i < FruitMarkers.Length; i++)
            {
                string marker = FruitMarkers[i];
                bool requiresSubstringMatch = false;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (marker[j] > 127)
                    {
                        requiresSubstringMatch = true;
                        break;
                    }
                }

                if ((requiresSubstringMatch && searchable.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!requiresSubstringMatch && HasToken(searchable, marker)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && value.IndexOf(candidates[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeIdentity(string value)
        {
            List<string> tokens = TokenizeMeaningfulName(value);
            return tokens.Count == 0 ? "generic" : JoinTokens(tokens, 0, tokens.Count, "-");
        }

        private static string HumanizeTokens(List<string> tokens, int start, int length)
        {
            StringBuilder builder = new StringBuilder();
            bool previousWasCjk = false;
            for (int i = 0; i < length; i++)
            {
                string token = tokens[start + i];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                bool tokenIsCjk = IsCjkToken(token);
                if (builder.Length > 0 && !(previousWasCjk && tokenIsCjk))
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToUpperInvariant(token[0]));
                if (token.Length > 1)
                {
                    builder.Append(token.Substring(1));
                }

                previousWasCjk = tokenIsCjk;
            }

            return builder.ToString();
        }

        private static string BuildStructuralLabel(string prefix, string identity)
        {
            List<string> tokens = TokenizeMeaningfulName(identity);
            string suffix = tokens.Count == 0 ? string.Empty : HumanizeTokens(tokens, 0, Math.Min(3, tokens.Count));
            return string.IsNullOrEmpty(suffix) ? prefix : prefix + ": " + suffix;
        }

        private static string JoinTokens(List<string> tokens, int start, int length, string separator)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    builder.Append(separator);
                }

                builder.Append(tokens[start + i]);
            }

            return builder.ToString();
        }

        private static string GetTierKey(string categoryKey)
        {
            CategoryDefinition definition;
            string normalizedKey = NormalizeKey(categoryKey);
            return Definitions.TryGetValue(normalizedKey, out definition) ? definition.TierKey : normalizedKey;
        }

        private static string GetTierKeyForKind(DIYRecipeKind kind)
        {
            switch (kind)
            {
                case DIYRecipeKind.Composite:
                    return "salad";
                case DIYRecipeKind.Cooked:
                    return "roast";
                case DIYRecipeKind.Mixed:
                    return "smoothie";
                default:
                    return "other";
            }
        }

        private static string NormalizeKey(string categoryKey)
        {
            return (categoryKey ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeCategorySource(string internalName)
        {
            if (string.IsNullOrEmpty(internalName))
            {
                return string.Empty;
            }

            string value = TrimSuffix(internalName, "_SO");
            value = TrimSuffix(value, "_New");
            if (!value.StartsWith("DLC", StringComparison.Ordinal))
            {
                return value;
            }

            int underscoreIndex = value.IndexOf('_');
            if (underscoreIndex <= 3)
            {
                return value;
            }

            for (int i = 3; i < underscoreIndex; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return value;
                }
            }

            return value.Substring(underscoreIndex + 1);
        }

        private static string TrimSuffix(string value, string suffix)
        {
            return value != null && value.EndsWith(suffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWith(string value, string suffix)
        {
            return !string.IsNullOrEmpty(value) && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCjkToken(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (!IsCjk(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCjk(char value)
        {
            return (value >= '\u3400' && value <= '\u4dbf')
                || (value >= '\u4e00' && value <= '\u9fff')
                || (value >= '\uf900' && value <= '\ufaff');
        }

        private static string FirstNonEmpty(string first, string second, string third, string fourth = null)
        {
            if (!string.IsNullOrEmpty(first))
            {
                return first;
            }

            if (!string.IsNullOrEmpty(second))
            {
                return second;
            }

            return !string.IsNullOrEmpty(third) ? third : fourth;
        }
    }
}
