// Owns bilingual dish-name normalization and the auditable discovery report.
// Recipe-family taxonomy and classification live in RecipeCategoryCatalog so
// inferred DIY groups never alter an individual dish's authored display name.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace OC2MenuManager
{
    internal static partial class DishNameCatalog
    {
        internal sealed class DishNameEntry
        {
            public readonly string InternalName;
            public readonly string ChineseFull;
            public readonly string ChineseShort;
            public readonly string English;

            public DishNameEntry(string internalName, string chineseFull, string chineseShort, string english)
            {
                InternalName = internalName ?? string.Empty;
                ChineseFull = chineseFull ?? string.Empty;
                ChineseShort = chineseShort ?? string.Empty;
                English = english ?? string.Empty;
            }
        }

        /// <summary>Captures one report row without deriving category data inside the name catalog.</summary>
        private sealed class DiscoveredRecipeEntry
        {
            internal readonly string InternalName;
            internal readonly RecipeCategoryAssignment Category;

            internal DiscoveredRecipeEntry(string internalName, RecipeCategoryAssignment category)
            {
                InternalName = internalName ?? string.Empty;
                Category = category;
            }
        }

        internal static readonly Dictionary<string, DishNameEntry> Entries = BuildEntries();
        internal static readonly Dictionary<string, string> FullChineseNames = BuildChineseMap(false);
        internal static readonly Dictionary<string, string> ShortChineseNames = BuildChineseMap(true);

        private static readonly Dictionary<string, SortedDictionary<int, DiscoveredRecipeEntry>> DiscoveredRecipesByScene = new Dictionary<string, SortedDictionary<int, DiscoveredRecipeEntry>>(StringComparer.OrdinalIgnoreCase);

        private static string discoveryReportPath;
        private static bool discoveryDirty;

        public static void Awake()
        {
            EnsureDiscoveryPath();
            _MODEntry.LogInfo("[DishNameCatalog] Loaded " + Entries.Count + " dish name entries.");
        }

        public static string GetChineseFullName(string internalName)
        {
            DishNameEntry entry;
            if (TryGetEntry(internalName, out entry) && !string.IsNullOrEmpty(entry.ChineseFull))
            {
                return NormalizeChineseName(internalName, entry.ChineseFull);
            }

            return GetEnglishName(internalName);
        }

        public static string GetChineseShortName(string internalName)
        {
            DishNameEntry entry;
            if (TryGetEntry(internalName, out entry))
            {
                if (!string.IsNullOrEmpty(entry.ChineseShort))
                {
                    return NormalizeChineseName(internalName, entry.ChineseShort);
                }

                if (!string.IsNullOrEmpty(entry.ChineseFull))
                {
                    return NormalizeChineseName(internalName, entry.ChineseFull);
                }
            }

            return GetEnglishName(internalName);
        }

        public static string GetEnglishName(string internalName)
        {
            DishNameEntry entry;
            string rawValue = string.Empty;
            if (TryGetEntry(internalName, out entry))
            {
                rawValue = entry.English;
            }

            if (string.IsNullOrEmpty(rawValue))
            {
                rawValue = HumanizeInternalName(internalName);
            }

            return NormalizeEnglishName(internalName, rawValue);
        }

        private static string NormalizeChineseName(string internalName, string rawValue)
        {
            string value = (rawValue ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            string categoryKey = RecipeCategoryCatalog.GetKnownCategoryKey(internalName);
            string categoryName = RecipeCategoryCatalog.GetCategoryNameByKey(categoryKey);
            if (string.IsNullOrEmpty(categoryKey) || string.IsNullOrEmpty(categoryName) || string.Equals(categoryName, "其他", StringComparison.Ordinal))
            {
                return value;
            }

            if (value.StartsWith(categoryName + "：", StringComparison.Ordinal))
            {
                return value;
            }

            switch (categoryKey)
            {
                case "pizza":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "披萨"));
                case "cake":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "蛋糕"));
                case "moonpie":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "月饼"));
                case "fruitpie":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "水果派"));
                case "roast":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "烧烤"));
                case "fried":
                    return FormatCategoryName(categoryName, NormalizeFriedSuffix(value));
                case "pancake":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "煎饼"));
                case "dessert":
                    return FormatCategoryName(categoryName, NormalizeDessertSuffix(value));
                case "sushi":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "寿司"));
                case "steamed":
                    return FormatCategoryName(categoryName, NormalizeSteamedSuffix(value));
                case "soup":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "汤"));
                case "hotpot":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "火锅"));
                case "breakfast":
                    return FormatCategoryName(categoryName, value);
                case "burger":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "汉堡"));
                case "burrito":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "卷饼"));
                case "kebob":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "烤串"));
                case "donut":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "甜甜圈"));
                case "salad":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "沙拉"));
                case "pasta":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "意面"));
                case "smoothie":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "奶昔"));
                case "hotdog":
                    return FormatCategoryName(categoryName, NormalizeHotdogSuffix(value));
                case "smores":
                    return FormatCategoryName(categoryName, NormalizeSmoresSuffix(value));
                case "fruitplatter":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "水果拼盘"));
                case "sashimi":
                    return FormatCategoryName(categoryName, StripCategorySuffix(value, "刺身"));
                case "hotchocolate":
                    return FormatCategoryName(categoryName, NormalizeHotChocolateSuffix(internalName, value));
                case "float":
                    return FormatCategoryName(categoryName, NormalizeFloatSuffix(internalName, value));
                default:
                    return value;
            }
        }

        private static string NormalizeEnglishName(string internalName, string rawValue)
        {
            string value = (rawValue ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            string categoryKey = RecipeCategoryCatalog.GetKnownCategoryKey(internalName);
            string categoryName = RecipeCategoryCatalog.GetEnglishCategoryNameByKey(categoryKey);
            switch (categoryKey)
            {
                case "fruitpie":
                    categoryName = "Pie";
                    break;
                case "fruitplatter":
                    categoryName = "Platter";
                    break;
                case "hotchocolate":
                    categoryName = "Hot Cocoa";
                    break;
            }

            if (string.IsNullOrEmpty(categoryKey) || string.IsNullOrEmpty(categoryName) || string.Equals(categoryName, "Other", StringComparison.Ordinal))
            {
                return CollapseWhitespace(value);
            }

            if (value.StartsWith(categoryName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return FormatEnglishCategoryName(categoryName, value.Substring(categoryName.Length + 1));
            }

            switch (categoryKey)
            {
                case "pizza":
                    return FormatEnglishCategoryName(categoryName, NormalizePizzaEnglishSuffix(internalName, value));
                case "cake":
                    return FormatEnglishCategoryName(categoryName, NormalizeCakeEnglishSuffix(internalName, value));
                case "moonpie":
                    return FormatEnglishCategoryName(categoryName, NormalizeMoonpieEnglishSuffix(value));
                case "fruitpie":
                    return FormatEnglishCategoryName(categoryName, NormalizeFruitPieEnglishSuffix(value));
                case "roast":
                    return FormatEnglishCategoryName(categoryName, NormalizeRoastEnglishSuffix(internalName, value));
                case "fried":
                    return FormatEnglishCategoryName(categoryName, NormalizeFriedEnglishSuffix(internalName, value));
                case "pancake":
                    return FormatEnglishCategoryName(categoryName, StripEnglishCategorySuffix(value, "Pancake"));
                case "dessert":
                    return FormatEnglishCategoryName(categoryName, NormalizeDessertEnglishSuffix(internalName));
                case "sushi":
                    return FormatEnglishCategoryName(categoryName, NormalizeSushiEnglishSuffix(internalName, value));
                case "steamed":
                    return FormatEnglishCategoryName(categoryName, NormalizeSteamedEnglishSuffix(internalName, value));
                case "soup":
                    return FormatEnglishCategoryName(categoryName, NormalizeSoupEnglishSuffix(internalName, value));
                case "hotpot":
                    return FormatEnglishCategoryName(categoryName, NormalizeHotPotEnglishSuffix(internalName, value));
                case "breakfast":
                    return FormatEnglishCategoryName(categoryName, StripEnglishCategorySuffix(value, "Breakfast"));
                case "burger":
                    return FormatEnglishCategoryName(categoryName, NormalizeBurgerEnglishSuffix(internalName, value));
                case "burrito":
                    return FormatEnglishCategoryName(categoryName, NormalizeBurritoEnglishSuffix(internalName, value));
                case "kebob":
                    return FormatEnglishCategoryName(categoryName, NormalizeKebobEnglishSuffix(internalName, value));
                case "donut":
                    return FormatEnglishCategoryName(categoryName, NormalizeDonutEnglishSuffix(internalName, value));
                case "salad":
                    return FormatEnglishCategoryName(categoryName, NormalizeSaladEnglishSuffix(internalName, value));
                case "pasta":
                    return FormatEnglishCategoryName(categoryName, NormalizePastaEnglishSuffix(internalName, value));
                case "smoothie":
                    return FormatEnglishCategoryName(categoryName, NormalizeSmoothieEnglishSuffix(internalName, value));
                case "hotdog":
                    return FormatEnglishCategoryName(categoryName, NormalizeHotdogEnglishSuffix(internalName));
                case "smores":
                    return FormatEnglishCategoryName(categoryName, NormalizeSmoresEnglishSuffix(internalName, value));
                case "fruitplatter":
                    return FormatEnglishCategoryName(categoryName, NormalizeFruitPlatterEnglishSuffix(internalName, value));
                case "sashimi":
                    return FormatEnglishCategoryName(categoryName, NormalizeSashimiEnglishSuffix(internalName, value));
                case "hotchocolate":
                    return FormatEnglishCategoryName(categoryName, NormalizeHotChocolateEnglishSuffix(internalName, value));
                case "float":
                    return FormatEnglishCategoryName(categoryName, NormalizeFloatEnglishSuffix(internalName));
                default:
                    return CollapseWhitespace(value);
            }
        }

        private static string FormatCategoryName(string categoryName, string suffix)
        {
            string normalizedSuffix = CleanNormalizedSuffix(suffix);
            return categoryName + "：" + normalizedSuffix;
        }

        private static string FormatEnglishCategoryName(string categoryName, string suffix)
        {
            string normalizedSuffix = CleanNormalizedEnglishSuffix(suffix);
            return categoryName + ": " + normalizedSuffix;
        }

        private static string CleanNormalizedSuffix(string suffix)
        {
            string value = (suffix ?? string.Empty).Trim();
            value = StripBracketAnnotations(value);
            value = value.Trim().Trim('：', '-', '+', '/', ' ');
            if (string.IsNullOrEmpty(value)
                || string.Equals(value, "素", StringComparison.Ordinal)
                || string.Equals(value, "plain", StringComparison.OrdinalIgnoreCase))
            {
                return "原味";
            }

            return value;
        }

        private static string CleanNormalizedEnglishSuffix(string suffix)
        {
            string value = CollapseWhitespace(StripBracketAnnotations(suffix ?? string.Empty).Trim());
            value = value.Trim().Trim(':', '-', '+', '/', ' ');
            if (string.IsNullOrEmpty(value)
                || string.Equals(value, "Plain", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Veg", StringComparison.OrdinalIgnoreCase))
            {
                return "Plain";
            }

            return value;
        }

        private static string StripCategorySuffix(string value, string categorySuffix)
        {
            string normalizedValue = StripBracketAnnotations(value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalizedValue))
            {
                return normalizedValue;
            }

            if (!string.IsNullOrEmpty(categorySuffix) && normalizedValue.EndsWith(categorySuffix, StringComparison.Ordinal))
            {
                normalizedValue = normalizedValue.Substring(0, normalizedValue.Length - categorySuffix.Length);
            }
            else if (!string.IsNullOrEmpty(categorySuffix) && normalizedValue.StartsWith(categorySuffix + "：", StringComparison.Ordinal))
            {
                normalizedValue = normalizedValue.Substring(categorySuffix.Length + 1);
            }

            return normalizedValue.Trim();
        }

        private static string StripEnglishCategorySuffix(string value, params string[] categorySuffixes)
        {
            string normalizedValue = CollapseWhitespace(StripBracketAnnotations(value ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(normalizedValue))
            {
                return normalizedValue;
            }

            for (int i = 0; i < categorySuffixes.Length; i++)
            {
                string categorySuffix = categorySuffixes[i];
                if (string.IsNullOrEmpty(categorySuffix))
                {
                    continue;
                }

                if (normalizedValue.EndsWith(categorySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedValue = normalizedValue.Substring(0, normalizedValue.Length - categorySuffix.Length);
                    break;
                }

                if (normalizedValue.StartsWith(categorySuffix + ":", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedValue = normalizedValue.Substring(categorySuffix.Length + 1);
                    break;
                }

                if (normalizedValue.StartsWith(categorySuffix + " ", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedValue = normalizedValue.Substring(categorySuffix.Length);
                    break;
                }
            }

            return CollapseWhitespace(normalizedValue.Trim().Trim(':', '-', '+', '/', ' '));
        }

        private static string StripBracketAnnotations(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool insideBracket = false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current == '(' || current == '（')
                {
                    insideBracket = true;
                    continue;
                }

                if (current == ')' || current == '）')
                {
                    insideBracket = false;
                    continue;
                }

                if (!insideBracket)
                {
                    builder.Append(current);
                }
            }

            return builder.ToString().Trim();
        }

        private static string NormalizeSteamedSuffix(string value)
        {
            if (!string.IsNullOrEmpty(value) && value.StartsWith("蒸", StringComparison.Ordinal) && !value.StartsWith("蒸菜：", StringComparison.Ordinal))
            {
                return value.Substring(1);
            }

            return StripCategorySuffix(value, "蒸菜");
        }

        private static string NormalizeDessertSuffix(string value)
        {
            string normalized = StripCategorySuffix(value, "甜点");
            if (string.Equals(normalized, "橙味", StringComparison.Ordinal))
            {
                return "橙子";
            }

            return normalized;
        }

        private static string NormalizeFriedSuffix(string value)
        {
            string normalized = StripCategorySuffix(value, "炸物");
            if (string.Equals(normalized, "炸鸡", StringComparison.Ordinal))
            {
                return "鸡肉";
            }

            if (string.Equals(normalized, "炸鸡薯条", StringComparison.Ordinal))
            {
                return "鸡肉薯条";
            }

            return normalized;
        }

        private static string NormalizeSmoresSuffix(string value)
        {
            string normalized = StripBracketAnnotations(value ?? string.Empty).Trim();
            const string smoresSuffix = "烤棉花糖饼干";
            if (normalized.EndsWith(smoresSuffix, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - smoresSuffix.Length);
            }

            return normalized;
        }

        private static string NormalizeHotdogSuffix(string value)
        {
            string rawValue = value ?? string.Empty;
            bool hasOnion = rawValue.IndexOf("洋葱", StringComparison.Ordinal) >= 0 || rawValue.IndexOf("(葱)", StringComparison.Ordinal) >= 0 || rawValue.IndexOf("（葱）", StringComparison.Ordinal) >= 0;
            string normalized = StripCategorySuffix(rawValue, "热狗");
            if (string.IsNullOrEmpty(normalized) && hasOnion)
            {
                return "洋葱";
            }

            if (string.Equals(normalized, "葱", StringComparison.Ordinal))
            {
                return "洋葱";
            }

            if (hasOnion && normalized.IndexOf("洋葱", StringComparison.Ordinal) < 0 && normalized.IndexOf("葱", StringComparison.Ordinal) < 0)
            {
                normalized += "洋葱";
            }

            return normalized;
        }

        private static string NormalizeHotChocolateSuffix(string internalName, string value)
        {
            string normalizedInternalName = NormalizeCategorySource(internalName);
            bool hasCream = normalizedInternalName.IndexOf("Cream", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasMallow = normalizedInternalName.IndexOf("Mallow", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasCream && !hasMallow)
            {
                return StripCategorySuffix(value, "热可可");
            }

            if (hasCream && hasMallow)
            {
                return "奶油棉花糖";
            }

            return hasCream ? "奶油" : "棉花糖";
        }

        private static string NormalizeFloatSuffix(string internalName, string value)
        {
            string normalizedInternalName = NormalizeCategorySource(internalName);
            string drink = normalizedInternalName.IndexOf("OrangeSodaFloat", StringComparison.OrdinalIgnoreCase) >= 0 ? "橙子" : "根汁";
            string flavor = normalizedInternalName.IndexOf("Chocolate", StringComparison.OrdinalIgnoreCase) >= 0 ? "可可" : "香草";
            return drink + flavor;
        }

        private static string NormalizePizzaEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "MargheritaPizza":
                case "Pizza_Plain":
                    return "Plain";
                case "PeperoniPizza":
                case "Pizza_Peperoni":
                    return "Pepperoni";
                case "ChickenPizza":
                case "Pizza_Chicken":
                    return "Chicken";
                case "Pizza_Olives":
                    return "Olive";
                default:
                    return StripEnglishCategorySuffix(value, "Pizza");
            }
        }

        private static string NormalizeCakeEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Cake_Plain":
                    return "Honey";
                default:
                    return StripEnglishCategorySuffix(value, "Cake");
            }
        }

        private static string NormalizeMoonpieEnglishSuffix(string value)
        {
            return StripEnglishCategorySuffix(value, "Mooncake", "Moon Pie");
        }

        private static string NormalizeFruitPieEnglishSuffix(string value)
        {
            return StripEnglishCategorySuffix(value, "Fruit Pie", "Pie");
        }

        private static string NormalizeRoastEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "BeefPotatoCarrotRoast":
                    return "Beef";
                case "BeefPotatoCarrotBroccoliRoast":
                    return "Beef Broccoli";
                case "ChickenPotatoCarrotRoast":
                    return "Chicken";
                case "ChickenPotatoCarrotBroccoliRoast":
                    return "Chicken Broccoli";
                default:
                    return StripEnglishCategorySuffix(value, "Roast");
            }
        }

        private static string NormalizeFriedEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "ChickenNuggetsAndChips_All":
                case "Fry_All":
                    return "Chicken Chips";
                case "ChickenNuggetsAndChips_ChickenOnly":
                case "Fry_Chicken":
                    return "Chicken";
                case "ChickenNuggetsAndChips_ChipsOnly":
                case "Fry_Chips":
                    return "Chips";
                default:
                    return StripEnglishCategorySuffix(value, "Fry", "Fried");
            }
        }

        private static string NormalizeDessertEnglishSuffix(string internalName)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "ChristmasPuddingWithOrange":
                    return "Orange";
                default:
                    return "Plain";
            }
        }

        private static string NormalizeSushiEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Sushi_All":
                    return "Fish Cucumber";
                case "Sushi_Cucumber":
                    return "Cucumber";
                case "Sushi_Fish":
                    return "Fish";
                default:
                    return StripEnglishCategorySuffix(value, "Sushi");
            }
        }

        private static string NormalizeSteamedEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "SteamedSpecial_Carrot":
                case "Steamed_Carrot":
                    return "Carrot";
                case "SteamedSpecial_Fish":
                case "Steamed_Fish":
                    return "Fish";
                case "SteamedSpecial_Meat":
                case "Steamed_Meat":
                    return "Meat";
                case "SteamedSpecial_Prawns":
                case "Steamed_Prawns":
                    return "Prawn";
                default:
                    return StripEnglishCategorySuffix(value, "Steamed");
            }
        }

        private static string NormalizeSoupEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "OnionBroccoliCheeseSoup":
                    return "Broccoli Cheese";
                case "OnionCarrotPotatoSoup":
                    return "Potato Carrot";
                case "OnionPotatoSoupLeek":
                    return "Potato Leek";
                default:
                    return StripEnglishCategorySuffix(value, "Soup");
            }
        }

        private static string NormalizeHotPotEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "HotPot_DoubleMeat":
                    return "Double Meat";
                case "HotPot_DoublePrawn":
                    return "Double Prawn";
                case "HotPot_Meat":
                    return "Meat";
                case "HotPot_Mixed":
                    return "Meat Prawn";
                case "HotPot_Prawn":
                    return "Prawn";
                default:
                    return StripEnglishCategorySuffix(value, "Hot Pot");
            }
        }

        private static string NormalizeBurgerEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "BeefBurger":
                case "Burger_Plain":
                    return "Plain";
                case "BeefBurgerCheese":
                case "Burger_Cheese":
                    return "Cheese";
                case "BeefBurgerMax":
                case "Burger_LettuceTomato":
                    return "Lettuce Tomato";
                case "BeefBurgerWithGreensNCheese":
                case "Burger_CheeseLettuce":
                    return "Lettuce Cheese";
                case "HawaiianBurger":
                    return "Pineapple Beef";
                case "MD_Burger_CheeseSticks_Drink03":
                    return "Beef Cheese Sticks Yellow Drink";
                case "MD_Burger_Drink01":
                    return "Beef Red Drink";
                case "MD_Burger_Fries":
                    return "Beef Fries";
                case "MD_Burger_Fries_CheeseSticks":
                    return "Beef Fries Cheese Sticks";
                case "MD_Burger_Fries_Drink02":
                    return "Beef Fries Green Drink";
                case "MD_Burger_OnionRings":
                    return "Beef Onion Rings";
                case "MD_Burger_OnionRings_CheeseSticks":
                    return "Beef Onion Rings Cheese Sticks";
                case "MD_Burger_OnionRings_Drink01":
                    return "Beef Onion Rings Red Drink";
                case "MD_C_Burger_CheeseSticks":
                    return "Chicken Cheese Sticks";
                case "MD_C_Burger_CheeseSticks_Drink02":
                    return "Chicken Cheese Sticks Green Drink";
                case "MD_C_Burger_Drink03":
                    return "Chicken Yellow Drink";
                case "MD_C_Burger_Fries_CheeseSticks":
                    return "Chicken Fries Cheese Sticks";
                case "MD_C_Burger_Fries_Drink03":
                    return "Chicken Fries Yellow Drink";
                case "MD_C_Burger_Fries_OnionRings":
                    return "Chicken Fries Onion Rings";
                case "MD_C_Burger_OnionRings":
                    return "Chicken Onion Rings";
                case "MD_C_Burger_OnionRings_Drink01":
                    return "Chicken Onion Rings Red Drink";
                default:
                    return StripEnglishCategorySuffix(value, "Burger");
            }
        }

        private static string NormalizeBurritoEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Chicken_Burrito":
                case "Burrito_Chicken":
                    return "Chicken";
                case "Meat_Burrito":
                case "Burrito_Meat":
                    return "Beef";
                case "Mushroom_Burrito":
                case "Burrito_Mushroom":
                    return "Mushroom";
                default:
                    return StripEnglishCategorySuffix(value, "Burrito");
            }
        }

        private static string NormalizeKebobEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "ChickenTomatoKebob":
                    return "Chicken Tomato";
                case "ChickenMeatTomatoKebob":
                    return "Chicken Tomato Beef";
                case "MeatMushroomPineappleKebob":
                    return "Pineapple Mushroom Beef";
                case "MushroomPineappleTomatoKebob":
                    return "Pineapple Mushroom Tomato";
                default:
                    return StripEnglishCategorySuffix(value, "Kebob", "Kebab", "Skewer");
            }
        }

        private static string NormalizeDonutEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Donut_Plain":
                    return "Honey";
                default:
                    return StripEnglishCategorySuffix(value, "Donut");
            }
        }

        private static string NormalizeSaladEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Salad_Plain":
                case "Salad_Plain_SO":
                    return "Plain";
                case "Salad_Tomato":
                case "Salad_Tomato_Onion":
                    return "Lettuce Tomato";
                case "Salad_Tomato_SO":
                    return "Tomato";
                case "Salad_Corn_Onion":
                    return "Lettuce Corn";
                case "Salad_Cucumber":
                case "Salad_Cucumber_Tomato_Onion":
                    return "Lettuce Tomato Cucumber";
                case "Salad_Cucumber_Onion":
                    return "Lettuce Cucumber";
                case "Salad_Cucumber_SO":
                    return "Cucumber";
                case "Tomato_Corn_Onion":
                    return "Tomato Corn";
                case "Tomato_Cucumber_Onion":
                    return "Tomato Cucumber";
                default:
                    return StripEnglishCategorySuffix(value, "Salad");
            }
        }

        private static string NormalizePastaEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Pasta_Marinara":
                    return "Seafood";
                case "Pasta_MeatOnly":
                    return "Beef";
                case "Pasta_MushroomOnly":
                    return "Mushroom";
                case "Pasta_TomatoOnly":
                    return "Tomato";
                default:
                    return StripEnglishCategorySuffix(value, "Pasta");
            }
        }

        private static string NormalizeSmoothieEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "MegaSmoothie":
                    return "Four Fruit";
                case "MelonSmoothie":
                    return "Watermelon";
                default:
                    return StripEnglishCategorySuffix(value, "Smoothie");
            }
        }

        private static string NormalizeHotdogEnglishSuffix(string internalName)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Hotdog_Plain":
                    return "Plain";
                case "Hotdog_Onions":
                    return "Onion";
                case "Hotdog_Ketchup":
                    return "Ketchup";
                case "Hotdog_Mustard":
                    return "Mustard";
                case "Hotdog_Ketchup_Mustard":
                    return "Ketchup Mustard";
                case "Hotdog_Onions_Ketchup":
                    return "Onion Ketchup";
                case "Hotdog_Onions_Mustard":
                    return "Onion Mustard";
                default:
                    return "Plain";
            }
        }

        private static string NormalizeSmoresEnglishSuffix(string internalName, string value)
        {
            if (string.Equals(NormalizeCategorySource(internalName), "Smores_Plain", StringComparison.OrdinalIgnoreCase))
            {
                return "Plain";
            }

            return StripEnglishCategorySuffix(value, "Smores", "S'more");
        }

        private static string NormalizeFruitPlatterEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "FruitPlatter_GrapesPeach":
                    return "Peach Grape";
                case "FruitPlatter_OrangeGrapes":
                    return "Orange Grape";
                case "FruitPlatter_OrangePeach":
                    return "Orange Peach";
                case "FruitPlatter_OrangePeachGrapes":
                    return "Trio";
                default:
                    return StripEnglishCategorySuffix(value, "Fruit Platter", "Platter");
            }
        }

        private static string NormalizeSashimiEnglishSuffix(string internalName, string value)
        {
            switch (NormalizeCategorySource(internalName))
            {
                case "Sushi_PlainFish":
                    return "Fish";
                case "Sushi_PlainPrawn":
                    return "Prawn";
                default:
                    return StripEnglishCategorySuffix(value, "Sashimi");
            }
        }

        private static string NormalizeHotChocolateEnglishSuffix(string internalName, string value)
        {
            string normalizedInternalName = NormalizeCategorySource(internalName);
            bool hasCream = normalizedInternalName.IndexOf("Cream", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasMallow = normalizedInternalName.IndexOf("Mallow", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasCream && hasMallow)
            {
                return "Cream Marshmallow";
            }

            if (hasCream)
            {
                return "Cream";
            }

            if (hasMallow)
            {
                return "Marshmallow";
            }

            string normalized = StripEnglishCategorySuffix(value, "Hot Cocoa", "Hot Chocolate", "Cocoa");
            return string.IsNullOrEmpty(normalized) ? "Plain" : normalized;
        }

        private static string NormalizeFloatEnglishSuffix(string internalName)
        {
            string normalizedInternalName = NormalizeCategorySource(internalName);
            string drink = normalizedInternalName.IndexOf("OrangeSodaFloat", StringComparison.OrdinalIgnoreCase) >= 0 ? "Orange" : "Root Beer";
            string flavor = normalizedInternalName.IndexOf("Chocolate", StringComparison.OrdinalIgnoreCase) >= 0 ? "Cocoa" : "Vanilla";
            return drink + " " + flavor;
        }

        public static void RecordRecipe(
            string sceneName,
            int recipeId,
            string internalName,
            RecipeCategoryAssignment category)
        {
            if (recipeId <= 0 || string.IsNullOrEmpty(internalName))
            {
                return;
            }

            string safeSceneName = string.IsNullOrEmpty(sceneName) ? "(unknown)" : sceneName;
            RecipeCategoryAssignment resolvedCategory = category ?? RecipeCategoryCatalog.ResolveKnownOrFallback(internalName);
            SortedDictionary<int, DiscoveredRecipeEntry> sceneRecipes;
            if (!DiscoveredRecipesByScene.TryGetValue(safeSceneName, out sceneRecipes))
            {
                sceneRecipes = new SortedDictionary<int, DiscoveredRecipeEntry>();
                DiscoveredRecipesByScene.Add(safeSceneName, sceneRecipes);
                discoveryDirty = true;
            }

            DiscoveredRecipeEntry knownRecipe;
            if (!sceneRecipes.TryGetValue(recipeId, out knownRecipe)
                || knownRecipe == null
                || !string.Equals(knownRecipe.InternalName, internalName, StringComparison.Ordinal)
                || !RecipeCategoryCatalog.AreEquivalent(knownRecipe.Category, resolvedCategory))
            {
                sceneRecipes[recipeId] = new DiscoveredRecipeEntry(internalName, resolvedCategory);
                discoveryDirty = true;
            }
        }

        public static void RemoveRecipe(string sceneName, int recipeId)
        {
            SortedDictionary<int, DiscoveredRecipeEntry> sceneRecipes;
            if (string.IsNullOrEmpty(sceneName)
                || !DiscoveredRecipesByScene.TryGetValue(sceneName, out sceneRecipes)
                || !sceneRecipes.Remove(recipeId))
            {
                return;
            }

            if (sceneRecipes.Count == 0)
            {
                DiscoveredRecipesByScene.Remove(sceneName);
            }

            discoveryDirty = true;
        }

        public static void ClearSceneRecipes(string sceneName)
        {
            if (!string.IsNullOrEmpty(sceneName) && DiscoveredRecipesByScene.Remove(sceneName))
            {
                discoveryDirty = true;
            }
        }

        public static void FlushDiscoveryReport()
        {
            if (!discoveryDirty)
            {
                return;
            }

            EnsureDiscoveryPath();

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# OC2MenuManager dish discovery report");
            builder.AppendLine("# scene\tid\tinternal\tzh\tzh_short\ten\tcatalog\tcategory_key\tcategory_en\tcategory_zh\tcategory_source\tcategory_initial_key\tcategory_detail");

            HashSet<string> missingCatalogNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, SortedDictionary<int, DiscoveredRecipeEntry>> scenePair in DiscoveredRecipesByScene.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (KeyValuePair<int, DiscoveredRecipeEntry> recipePair in scenePair.Value)
                {
                    DiscoveredRecipeEntry discoveredRecipe = recipePair.Value;
                    if (discoveredRecipe == null)
                    {
                        continue;
                    }

                    DishNameEntry entry;
                    bool known = TryGetEntry(discoveredRecipe.InternalName, out entry);
                    if (!known)
                    {
                        missingCatalogNames.Add(discoveredRecipe.InternalName);
                    }

                    RecipeCategoryAssignment category = discoveredRecipe.Category
                        ?? RecipeCategoryCatalog.ResolveKnownOrFallback(discoveredRecipe.InternalName);

                    builder.Append(scenePair.Key).Append('\t');
                    builder.Append(recipePair.Key).Append('\t');
                    builder.Append(discoveredRecipe.InternalName).Append('\t');
                    builder.Append(GetChineseFullName(discoveredRecipe.InternalName)).Append('\t');
                    builder.Append(GetChineseShortName(discoveredRecipe.InternalName)).Append('\t');
                    builder.Append(GetEnglishName(discoveredRecipe.InternalName)).Append('\t');
                    builder.Append(known ? "mapped" : "fallback").Append('\t');
                    builder.Append(category.Key).Append('\t');
                    builder.Append(category.EnglishName).Append('\t');
                    builder.Append(category.ChineseName).Append('\t');
                    builder.Append(RecipeCategoryCatalog.GetSourceName(category.Source)).Append('\t');
                    builder.Append(category.InitialCategoryKey).Append('\t');
                    builder.Append(category.InferenceDetail);
                    builder.AppendLine();
                }
            }

            if (missingCatalogNames.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("# Missing catalog entries");
                foreach (string internalName in missingCatalogNames.OrderBy(x => x, StringComparer.Ordinal))
                {
                    builder.Append(internalName).Append('\t');
                    builder.Append(GetEnglishName(internalName));
                    builder.AppendLine();
                }
            }

            File.WriteAllText(discoveryReportPath, builder.ToString(), Encoding.UTF8);
            discoveryDirty = false;
        }

        public static string HumanizeInternalName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName))
            {
                return string.Empty;
            }

            string value = internalName;
            value = TrimKnownSuffix(value, "_SO");
            value = TrimKnownSuffix(value, "_New");
            value = TrimDlcPrefix(value);

            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current == '_')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }
                    continue;
                }

                if (ShouldInsertSpace(value, i, builder))
                {
                    builder.Append(' ');
                }

                builder.Append(current);
            }

            return builder.ToString().Trim();
        }

        private static bool TryGetEntry(string internalName, out DishNameEntry entry)
        {
            entry = null;
            return !string.IsNullOrEmpty(internalName) && Entries.TryGetValue(internalName, out entry);
        }

        private static string NormalizeCategorySource(string internalName)
        {
            if (string.IsNullOrEmpty(internalName))
            {
                return string.Empty;
            }

            string value = TrimKnownSuffix(internalName, "_SO");
            value = TrimKnownSuffix(value, "_New");
            value = TrimDlcPrefix(value);
            return value;
        }

        private static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool previousWasWhitespace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (char.IsWhiteSpace(current))
                {
                    if (builder.Length > 0 && !previousWasWhitespace)
                    {
                        builder.Append(' ');
                    }

                    previousWasWhitespace = true;
                    continue;
                }

                builder.Append(current);
                previousWasWhitespace = false;
            }

            return builder.ToString().Trim();
        }

        private static void EnsureDiscoveryPath()
        {
            if (string.IsNullOrEmpty(discoveryReportPath))
            {
                discoveryReportPath = Path.Combine(Paths.ConfigPath, "OC2MenuManager.dish-catalog-report.txt");
            }
        }

        private static string TrimKnownSuffix(string value, string suffix)
        {
            return value != null && value.EndsWith(suffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        private static string TrimDlcPrefix(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("DLC", StringComparison.Ordinal))
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

        private static bool ShouldInsertSpace(string value, int index, StringBuilder builder)
        {
            if (index <= 0 || builder.Length == 0 || builder[builder.Length - 1] == ' ')
            {
                return false;
            }

            char previous = value[index - 1];
            char current = value[index];

            if (current == '_')
            {
                return false;
            }

            if (char.IsUpper(current) && (char.IsLower(previous) || char.IsDigit(previous)))
            {
                return true;
            }

            if (char.IsDigit(current) && char.IsLetter(previous))
            {
                return true;
            }

            if (char.IsLetter(current) && char.IsDigit(previous))
            {
                return true;
            }

            return false;
        }

    }
}
