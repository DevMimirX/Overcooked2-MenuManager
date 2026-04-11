using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HostUtilities;

namespace OC2MenuManager
{
    internal static class DishNameCatalog
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

        internal static readonly Dictionary<string, DishNameEntry> Entries = BuildEntries();
        internal static readonly Dictionary<string, string> FullChineseNames = BuildChineseMap(false);
        internal static readonly Dictionary<string, string> ShortChineseNames = BuildChineseMap(true);

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

        private static readonly Dictionary<string, int> CategoryTierOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SortedDictionary<int, string>> DiscoveredRecipesByScene = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

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

            string categoryKey = GetCategoryKey(internalName);
            string categoryName = GetCategoryName(internalName);
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

            string categoryKey = GetCategoryKey(internalName);
            string categoryName = GetEnglishCategoryName(internalName);
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

        public static string GetCategoryName(string internalName)
        {
            return GetCategoryNameByKey(GetCategoryKey(internalName));
        }

        public static string GetCategoryNameByKey(string categoryKey)
        {
            switch ((categoryKey ?? string.Empty).Trim())
            {
                case "pizza":
                    return "披萨";
                case "cake":
                    return "蛋糕";
                case "moonpie":
                    return "月饼";
                case "fruitpie":
                    return "水果派";
                case "roast":
                    return "烧烤";
                case "fried":
                    return "炸物";
                case "pancake":
                    return "煎饼";
                case "dessert":
                    return "甜点";
                case "sushi":
                    return "寿司";
                case "steamed":
                    return "蒸菜";
                case "soup":
                    return "汤";
                case "hotpot":
                    return "火锅";
                case "breakfast":
                    return "早餐";
                case "burger":
                    return "汉堡";
                case "burrito":
                    return "卷饼";
                case "kebob":
                    return "烤串";
                case "donut":
                    return "甜甜圈";
                case "salad":
                    return "沙拉";
                case "pasta":
                    return "意面";
                case "smoothie":
                    return "奶昔";
                case "hotdog":
                    return "热狗";
                case "smores":
                    return "饼干";
                case "fruitplatter":
                    return "水果拼盘";
                case "sashimi":
                    return "刺身";
                case "hotchocolate":
                    return "热可可";
                case "float":
                    return "冰淇淋汽水";
                default:
                    return "其他";
            }
        }

        public static string GetEnglishCategoryName(string internalName)
        {
            return GetEnglishCategoryNameByKey(GetCategoryKey(internalName));
        }

        public static string GetEnglishCategoryNameByKey(string categoryKey)
        {
            switch ((categoryKey ?? string.Empty).Trim())
            {
                case "pizza":
                    return "Pizza";
                case "cake":
                    return "Cake";
                case "moonpie":
                    return "Mooncake";
                case "fruitpie":
                    return "Pie";
                case "roast":
                    return "Roast";
                case "fried":
                    return "Fry";
                case "pancake":
                    return "Pancake";
                case "dessert":
                    return "Dessert";
                case "sushi":
                    return "Sushi";
                case "steamed":
                    return "Steamed";
                case "soup":
                    return "Soup";
                case "hotpot":
                    return "Hot Pot";
                case "breakfast":
                    return "Breakfast";
                case "burger":
                    return "Burger";
                case "burrito":
                    return "Burrito";
                case "kebob":
                    return "Skewer";
                case "donut":
                    return "Donut";
                case "salad":
                    return "Salad";
                case "pasta":
                    return "Pasta";
                case "smoothie":
                    return "Smoothie";
                case "hotdog":
                    return "Hot Dog";
                case "smores":
                    return "S'more";
                case "fruitplatter":
                    return "Platter";
                case "sashimi":
                    return "Sashimi";
                case "hotchocolate":
                    return "Hot Cocoa";
                case "float":
                    return "Float";
                default:
                    return "Other";
            }
        }

        public static string GetDisplayCategoryName(string internalName, bool chinese)
        {
            return chinese ? GetCategoryName(internalName) : GetEnglishCategoryName(internalName);
        }

        public static string GetDisplayCategoryNameByKey(string categoryKey, bool chinese)
        {
            return chinese ? GetCategoryNameByKey(categoryKey) : GetEnglishCategoryNameByKey(categoryKey);
        }

        public static int GetCategoryTier(string internalName)
        {
            return GetCategoryTierByKey(GetCategoryKey(internalName));
        }

        public static int GetCategoryTierByKey(string categoryKey)
        {
            string normalizedKey = (categoryKey ?? string.Empty).Trim();
            int overrideTier;
            if (CategoryTierOverrides.TryGetValue(normalizedKey, out overrideTier))
            {
                return overrideTier;
            }

            return GetDefaultCategoryTierByKey(normalizedKey);
        }

        public static int GetDefaultCategoryTierByKey(string categoryKey)
        {
            switch ((categoryKey ?? string.Empty).Trim())
            {
                case "pizza":
                case "pancake":
                case "moonpie":
                case "fruitpie":
                    return 1;
                case "roast":
                case "fried":
                case "cake":
                case "dessert":
                    return 2;
                case "sushi":
                case "steamed":
                case "soup":
                case "hotpot":
                case "breakfast":
                    return 3;
                case "burger":
                case "burrito":
                case "kebob":
                case "donut":
                    return 4;
                case "salad":
                case "pasta":
                case "smoothie":
                case "hotdog":
                case "smores":
                case "fruitplatter":
                    return 5;
                case "sashimi":
                case "hotchocolate":
                case "float":
                    return 6;
                default:
                    return 99;
            }
        }

        public static string[] GetOrderedCategoryKeys()
        {
            string[] copy = new string[OrderedCategoryKeys.Length];
            Array.Copy(OrderedCategoryKeys, copy, OrderedCategoryKeys.Length);
            return copy;
        }

        public static void SetCategoryTierOverride(string categoryKey, int tier)
        {
            string normalizedKey = (categoryKey ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return;
            }

            int clampedTier = Math.Max(1, Math.Min(6, tier));
            int defaultTier = GetDefaultCategoryTierByKey(normalizedKey);
            if (defaultTier == 99 || clampedTier == defaultTier)
            {
                CategoryTierOverrides.Remove(normalizedKey);
                return;
            }

            CategoryTierOverrides[normalizedKey] = clampedTier;
        }

        public static void RecordRecipe(string sceneName, int recipeId, string internalName)
        {
            if (recipeId <= 0 || string.IsNullOrEmpty(internalName))
            {
                return;
            }

            string safeSceneName = string.IsNullOrEmpty(sceneName) ? "(unknown)" : sceneName;
            SortedDictionary<int, string> sceneRecipes;
            if (!DiscoveredRecipesByScene.TryGetValue(safeSceneName, out sceneRecipes))
            {
                sceneRecipes = new SortedDictionary<int, string>();
                DiscoveredRecipesByScene.Add(safeSceneName, sceneRecipes);
                discoveryDirty = true;
            }

            string knownName;
            if (!sceneRecipes.TryGetValue(recipeId, out knownName) || !string.Equals(knownName, internalName, StringComparison.Ordinal))
            {
                sceneRecipes[recipeId] = internalName;
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
            builder.AppendLine("# HostUtilities dish discovery report");
            builder.AppendLine("# scene\tid\tinternal\tzh\tzh_short\ten\tcatalog");

            HashSet<string> missingCatalogNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, SortedDictionary<int, string>> scenePair in DiscoveredRecipesByScene.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (KeyValuePair<int, string> recipePair in scenePair.Value)
                {
                    DishNameEntry entry;
                    bool known = TryGetEntry(recipePair.Value, out entry);
                    if (!known)
                    {
                        missingCatalogNames.Add(recipePair.Value);
                    }

                    builder.Append(scenePair.Key).Append('\t');
                    builder.Append(recipePair.Key).Append('\t');
                    builder.Append(recipePair.Value).Append('\t');
                    builder.Append(GetChineseFullName(recipePair.Value)).Append('\t');
                    builder.Append(GetChineseShortName(recipePair.Value)).Append('\t');
                    builder.Append(GetEnglishName(recipePair.Value)).Append('\t');
                    builder.Append(known ? "mapped" : "fallback");
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

        private static string GetCategoryKey(string internalName)
        {
            string value = NormalizeCategorySource(internalName);
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (StartsWithOrdinalIgnoreCase(value, "Sushi_Plain"))
            {
                return "sashimi";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Sushi_"))
            {
                return "sushi";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Cake_"))
            {
                return "cake";
            }

            if (StartsWithOrdinalIgnoreCase(value, "DLC13_MoonPie_") || StartsWithOrdinalIgnoreCase(value, "MoonPie_"))
            {
                return "moonpie";
            }

            if (StartsWithOrdinalIgnoreCase(value, "FruitPie_"))
            {
                return "fruitpie";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Pancake_") || EndsWithOrdinalIgnoreCase(value, "Pancake"))
            {
                return "pancake";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Steamed") || StartsWithOrdinalIgnoreCase(value, "SteamedSpecial_"))
            {
                return "steamed";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Salad_") || StartsWithOrdinalIgnoreCase(value, "Tomato_"))
            {
                return "salad";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Pasta_"))
            {
                return "pasta";
            }

            if (StartsWithOrdinalIgnoreCase(value, "HotPot_"))
            {
                return "hotpot";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Hotdog_"))
            {
                return "hotdog";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Smores_"))
            {
                return "smores";
            }

            if (EndsWithOrdinalIgnoreCase(value, "Smoothie"))
            {
                return "smoothie";
            }

            if (EndsWithOrdinalIgnoreCase(value, "Soup"))
            {
                return "soup";
            }

            if (EndsWithOrdinalIgnoreCase(value, "Roast"))
            {
                return "roast";
            }

            if (EndsWithOrdinalIgnoreCase(value, "Kebob"))
            {
                return "kebob";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Breakfast_"))
            {
                return "breakfast";
            }

            if (StartsWithOrdinalIgnoreCase(value, "ChristmasPudding"))
            {
                return "dessert";
            }

            if (StartsWithOrdinalIgnoreCase(value, "HotChocolate"))
            {
                return "hotchocolate";
            }

            if (StartsWithOrdinalIgnoreCase(value, "OrangeSodaFloat_") || StartsWithOrdinalIgnoreCase(value, "RootBeerFloat_"))
            {
                return "float";
            }

            if (StartsWithOrdinalIgnoreCase(value, "FruitPlatter_"))
            {
                return "fruitplatter";
            }

            if (StartsWithOrdinalIgnoreCase(value, "ChickenNuggetsAndChips_") || StartsWithOrdinalIgnoreCase(value, "Fry_"))
            {
                return "fried";
            }

            if (StartsWithOrdinalIgnoreCase(value, "Donut_"))
            {
                return "donut";
            }

            if (EndsWithOrdinalIgnoreCase(value, "Pizza"))
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

        private static bool StartsWithOrdinalIgnoreCase(string value, string prefix)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(prefix)
                && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWithOrdinalIgnoreCase(string value, string suffix)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(suffix)
                && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
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
                discoveryReportPath = Path.Combine(Paths.ConfigPath, "HostUtilities-DishCatalogReport.txt");
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

        private static Dictionary<string, string> BuildChineseMap(bool shortName)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DishNameEntry> pair in Entries)
            {
                string value = shortName ? GetChineseShortName(pair.Key) : GetChineseFullName(pair.Key);
                if (!string.IsNullOrEmpty(value))
                {
                    map.Add(pair.Key, value);
                }
            }

            return map;
        }

        private static void AddEntry(Dictionary<string, DishNameEntry> entries, string internalName, string chineseFull, string chineseShort, string english)
        {
            entries[internalName] = new DishNameEntry(internalName, chineseFull, chineseShort, english);
        }

        private static Dictionary<string, DishNameEntry> BuildEntries()
        {
            Dictionary<string, DishNameEntry> entries = new Dictionary<string, DishNameEntry>(StringComparer.Ordinal);
            AddEntry(entries, "BananaPineappleSmoothie", "香蕉菠萝奶昔", "香蕉菠萝", "");
            AddEntry(entries, "BananaSmoothie", "香蕉奶昔", "香蕉", "");
            AddEntry(entries, "BeefBurger", "汉堡：原味", "汉堡：原味", "");
            AddEntry(entries, "BeefBurgerCheese", "汉堡：芝士", "汉堡：芝士", "");
            AddEntry(entries, "BeefBurgerMax", "汉堡：生菜番茄", "汉堡：生菜番茄", "");
            AddEntry(entries, "BeefBurgerWithGreensNCheese", "汉堡：生菜芝士", "汉堡：生菜芝士", "");
            AddEntry(entries, "BeefPotatoCarrotBroccoliRoast", "牛肉西兰花烧烤", "烧烤-肉+西兰花", "");
            AddEntry(entries, "BeefPotatoCarrotRoast", "牛肉烧烤", "烧烤-肉", "");
            AddEntry(entries, "BlueberryPancake", "蓝莓煎饼", "煎饼-蓝莓", "");
            AddEntry(entries, "Breakfast_Bacon_Egg", "培根蛋", "肉蛋", "");
            AddEntry(entries, "Breakfast_Bacon_Egg_Sausage", "培根蛋肠", "肉蛋肠", "");
            AddEntry(entries, "Breakfast_Sausage_Beans", "肠豆", "肠豆", "");
            AddEntry(entries, "Breakfast_Sausage_Beans_Egg", "肠豆蛋", "肠豆蛋", "");
            AddEntry(entries, "Breakfast_Sausage_Beans_Egg_Bacon", "肠豆培根蛋", "肠豆肉蛋", "");
            AddEntry(entries, "Burger_Cheese_SO", "汉堡：芝士", "汉堡：芝士", "Cheeseburger");
            AddEntry(entries, "Burger_CheeseLettuce_SO", "汉堡：生菜芝士", "汉堡：生菜芝士", "Cheese Lettuce Burger");
            AddEntry(entries, "Burger_LettuceTomato_SO", "汉堡：生菜番茄", "汉堡：生菜番茄", "Lettuce Tomato Burger");
            AddEntry(entries, "Burger_Plain_SO", "汉堡：原味", "汉堡：原味", "Plain Burger");
            AddEntry(entries, "Burrito_Chicken_SO", "卷饼：鸡肉", "卷饼：鸡肉", "Chicken Burrito");
            AddEntry(entries, "Burrito_Meat_SO", "卷饼：牛肉", "卷饼：牛肉", "Meat Burrito");
            AddEntry(entries, "Burrito_Mushroom_SO", "卷饼：蘑菇", "卷饼：蘑菇", "Mushroom Burrito");
            AddEntry(entries, "Cake_Carrot", "蛋糕：胡萝卜", "蛋糕：胡萝卜", "");
            AddEntry(entries, "Cake_Chocolate", "蛋糕：巧克力", "蛋糕：巧克力", "");
            AddEntry(entries, "Cake_Chocolate_SO", "蛋糕：巧克力", "蛋糕：巧克力", "Chocolate Cake");
            AddEntry(entries, "Cake_Honey_SO", "蛋糕：蜂蜜", "蛋糕：蜂蜜", "Honey Cake");
            AddEntry(entries, "Cake_HoneyCarrot_SO", "蛋糕：蜂蜜胡萝卜", "蛋糕：蜂蜜胡萝卜", "Honey Carrot Cake");
            AddEntry(entries, "Cake_HoneyChocolate_SO", "蛋糕：蜂蜜巧克力", "蛋糕：蜂蜜巧克力", "Honey Chocolate Cake");
            AddEntry(entries, "Cake_Plain", "蛋糕：蜂蜜", "蛋糕：蜂蜜", "");
            AddEntry(entries, "Cake_Plain_SO", "蛋糕：原味", "蛋糕：原味", "Plain Cake");
            AddEntry(entries, "Chicken_Burrito", "卷饼：鸡肉", "卷饼：鸡肉", "");
            AddEntry(entries, "ChickenMeatTomatoKebob", "鸡肉番茄牛肉烤串", "鸡番肉", "");
            AddEntry(entries, "ChickenNuggetsAndChips_All", "炸物：鸡肉薯条", "炸物：鸡肉薯条", "");
            AddEntry(entries, "ChickenNuggetsAndChips_ChickenOnly", "炸物：鸡肉", "炸物：鸡肉", "");
            AddEntry(entries, "ChickenNuggetsAndChips_ChipsOnly", "炸物：薯条", "炸物：薯条", "");
            AddEntry(entries, "ChickenPizza", "披萨：鸡肉", "披萨：鸡肉", "");
            AddEntry(entries, "ChickenPotatoCarrotBroccoliRoast", "鸡肉西兰花烧烤", "烧烤-鸡+西兰花", "");
            AddEntry(entries, "ChickenPotatoCarrotRoast", "鸡肉烧烤", "烧烤-鸡", "");
            AddEntry(entries, "ChickenTomatoKebob", "鸡肉番茄烤串", "番茄鸡", "");
            AddEntry(entries, "ChristmasPudding", "甜点", "甜点", "");
            AddEntry(entries, "ChristmasPuddingWithOrange", "橙味甜点", "橙味甜点", "");
            AddEntry(entries, "DLC09_ChristmasPudding", "甜点", "甜点", "");
            AddEntry(entries, "DLC09_ChristmasPuddingWithOrange", "橙味甜点", "橙味甜点", "");
            AddEntry(entries, "DLC09_HotChocolate", "热可可", "可可-素", "");
            AddEntry(entries, "DLC09_HotChocolateCream", "奶油热可可", "可可-奶油", "");
            AddEntry(entries, "DLC09_HotChocolateMallow", "棉花糖热可可", "可可-棉花糖", "");
            AddEntry(entries, "DLC09_HotChocolateMallowCream", "奶油棉花糖热可可", "可可-奶油棉花糖", "");
            AddEntry(entries, "DLC09_Pancake_Chocolate", "煎饼：巧克力", "煎饼：巧克力", "");
            AddEntry(entries, "DLC09_Pancake_Plain", "煎饼：原味", "煎饼：原味", "");
            AddEntry(entries, "DLC09_Pancake_Strawberry", "煎饼：草莓", "煎饼：草莓", "");
            AddEntry(entries, "DLC10_FruitPlatter_GrapesPeach", "黄桃葡萄", "黄桃+葡萄", "");
            AddEntry(entries, "DLC10_FruitPlatter_OrangeGrapes", "橙子葡萄", "橙子+葡萄", "");
            AddEntry(entries, "DLC10_FruitPlatter_OrangePeach", "黄桃橙子", "黄桃+橙子", "");
            AddEntry(entries, "DLC10_FruitPlatter_OrangePeachGrapes", "三拼水果拼盘", "三拼", "");
            AddEntry(entries, "DLC10_HotPot_DoubleMeat", "双肉火锅", "火锅-双肉", "");
            AddEntry(entries, "DLC10_HotPot_DoublePrawn", "双虾火锅", "火锅-双虾", "");
            AddEntry(entries, "DLC10_HotPot_Meat", "肉火锅", "火锅-肉", "");
            AddEntry(entries, "DLC10_HotPot_Mixed", "肉虾火锅", "火锅-肉虾", "");
            AddEntry(entries, "DLC10_HotPot_Prawn", "虾火锅", "火锅-虾", "");
            AddEntry(entries, "DLC11_Hotdog_Ketchup", "番茄酱热狗(红)", "热狗-红", "");
            AddEntry(entries, "DLC11_Hotdog_Ketchup_Mustard", "双酱热狗(红黄)", "热狗-双酱", "");
            AddEntry(entries, "DLC11_Hotdog_Mustard", "芥末酱热狗(黄)", "热狗-黄", "");
            AddEntry(entries, "DLC11_Hotdog_Onions_Ketchup", "番茄酱洋葱热狗(葱红)", "热狗-葱红", "");
            AddEntry(entries, "DLC11_Hotdog_Onions_Mustard", "芥末酱洋葱热狗(葱黄)", "热狗-葱黄", "");
            AddEntry(entries, "DLC13_FruitPlatter_GrapesPeach", "黄桃葡萄", "黄桃+葡萄", "");
            AddEntry(entries, "DLC13_FruitPlatter_OrangeGrapes", "橙子葡萄", "橙子+葡萄", "");
            AddEntry(entries, "DLC13_FruitPlatter_OrangePeach", "黄桃橙子", "黄桃+橙子", "");
            AddEntry(entries, "DLC13_FruitPlatter_OrangePeachGrapes", "三拼水果拼盘", "三拼", "");
            AddEntry(entries, "DLC13_MoonPie_Chocolate", "巧克力月饼", "月饼-巧克力", "");
            AddEntry(entries, "DLC13_MoonPie_ChocolateStrawberry", "巧克力草莓月饼", "月饼-巧克力草莓", "");
            AddEntry(entries, "DLC13_MoonPie_Strawberry", "草莓月饼", "月饼-草莓", "");
            AddEntry(entries, "DLC13_MoonPie_Watermelon", "西瓜月饼", "月饼-西瓜", "");
            AddEntry(entries, "Donut_Chocolate", "巧克力甜甜圈", "蛋糕-黑", "");
            AddEntry(entries, "Donut_Plain", "蜂蜜甜甜圈", "蛋糕-黄", "");
            AddEntry(entries, "Donut_Raspberry", "树莓甜甜圈", "蛋糕-红", "");
            AddEntry(entries, "FruitPie_Apple", "苹果水果派", "派-苹果", "");
            AddEntry(entries, "FruitPie_AppleBlackberry", "苹果黑莓水果派", "派-苹果黑莓", "");
            AddEntry(entries, "FruitPie_AppleCherry", "苹果樱桃水果派", "派-苹果樱桃", "");
            AddEntry(entries, "FruitPie_Blackberry", "黑莓水果派", "派-黑莓", "");
            AddEntry(entries, "FruitPie_Cherry", "樱桃水果派", "派-樱桃", "");
            AddEntry(entries, "FruitPlatter_GrapesPeach", "黄桃葡萄", "黄桃+葡萄", "");
            AddEntry(entries, "FruitPlatter_OrangeGrapes", "橙子葡萄", "橙子+葡萄", "");
            AddEntry(entries, "FruitPlatter_OrangePeach", "黄桃橙子", "黄桃+橙子", "");
            AddEntry(entries, "FruitPlatter_OrangePeachGrapes", "三拼水果拼盘", "三拼", "");
            AddEntry(entries, "Fry_All_SO", "炸鸡薯条", "炸鸡薯条", "Chicken and Chips");
            AddEntry(entries, "Fry_Chicken_SO", "炸鸡", "炸鸡", "Fried Chicken");
            AddEntry(entries, "Fry_Chips_SO", "薯条", "薯条", "Chips");
            AddEntry(entries, "HawaiianBurger", "菠萝牛肉汉堡", "汉堡-菠萝肉", "");
            AddEntry(entries, "HotChocolate", "热可可", "可可-素", "");
            AddEntry(entries, "HotChocolateCream", "奶油热可可", "可可-奶油", "");
            AddEntry(entries, "HotChocolateMallow", "棉花糖热可可", "可可-棉花糖", "");
            AddEntry(entries, "HotChocolateMallowCream", "奶油棉花糖热可可", "可可-奶油棉花糖", "");
            AddEntry(entries, "Hotdog_Ketchup", "番茄酱热狗(红)", "肠-红", "");
            AddEntry(entries, "Hotdog_Ketchup_Mustard", "双酱热狗(红黄)", "肠-双酱", "");
            AddEntry(entries, "Hotdog_Mustard", "芥末酱热狗(黄)", "肠-黄", "");
            AddEntry(entries, "Hotdog_Onions", "热狗(葱)", "肠-葱", "");
            AddEntry(entries, "Hotdog_Onions_Ketchup", "番茄酱洋葱热狗(葱红)", "肠-葱红", "");
            AddEntry(entries, "Hotdog_Onions_Mustard", "芥末酱洋葱热狗(葱黄)", "肠-葱黄", "");
            AddEntry(entries, "Hotdog_Plain", "热狗(素)", "肠-素", "");
            AddEntry(entries, "HotPot_DoubleMeat", "双肉火锅", "火锅-双肉", "");
            AddEntry(entries, "HotPot_DoublePrawn", "双虾火锅", "火锅-双虾", "");
            AddEntry(entries, "HotPot_Meat", "肉火锅", "火锅-肉", "");
            AddEntry(entries, "HotPot_Mixed", "肉虾火锅", "火锅-肉虾", "");
            AddEntry(entries, "HotPot_Prawn", "虾火锅", "火锅-虾", "");
            AddEntry(entries, "MargheritaPizza", "披萨：原味", "披萨：原味", "");
            AddEntry(entries, "MD_Burger_CheeseSticks_Drink03", "黄饮料芝士肉汉堡", "肉+黄+芝士", "");
            AddEntry(entries, "MD_Burger_Drink01", "红饮料肉汉堡", "肉+红", "");
            AddEntry(entries, "MD_Burger_Fries", "薯条肉汉堡", "肉+薯条", "");
            AddEntry(entries, "MD_Burger_Fries_CheeseSticks", "薯条芝士肉汉堡", "肉+薯条+芝士", "");
            AddEntry(entries, "MD_Burger_Fries_Drink02", "绿饮料薯条肉汉堡", "肉+绿+薯条", "");
            AddEntry(entries, "MD_Burger_OnionRings", "洋葱肉汉堡", "肉+葱", "");
            AddEntry(entries, "MD_Burger_OnionRings_CheeseSticks", "洋葱芝士肉汉堡", "肉+洋葱+芝士", "");
            AddEntry(entries, "MD_Burger_OnionRings_Drink01", "红饮料洋葱肉汉堡", "肉+红+葱", "");
            AddEntry(entries, "MD_C_Burger_CheeseSticks", "芝士鸡汉堡", "鸡+芝士", "");
            AddEntry(entries, "MD_C_Burger_CheeseSticks_Drink02", "绿饮料芝士鸡汉堡", "鸡+绿+芝士", "");
            AddEntry(entries, "MD_C_Burger_Drink03", "黄饮料鸡汉堡", "鸡+黄", "");
            AddEntry(entries, "MD_C_Burger_Fries_CheeseSticks", "芝士薯条鸡汉堡", "鸡+芝士+薯条", "");
            AddEntry(entries, "MD_C_Burger_Fries_Drink03", "黄饮料薯条鸡汉堡", "鸡+黄+薯条", "");
            AddEntry(entries, "MD_C_Burger_Fries_OnionRings", "洋葱薯条鸡汉堡", "鸡+葱+薯条", "");
            AddEntry(entries, "MD_C_Burger_OnionRings", "洋葱鸡汉堡", "鸡+葱", "");
            AddEntry(entries, "MD_C_Burger_OnionRings_Drink01", "红饮料洋葱鸡汉堡", "鸡+红+葱", "");
            AddEntry(entries, "Meat_Burrito", "卷饼：牛肉", "卷饼：牛肉", "");
            AddEntry(entries, "MeatMushroomPineappleKebob", "菠萝蘑菇牛肉烤串", "菠蘑肉", "");
            AddEntry(entries, "MegaSmoothie", "四拼奶昔", "四拼", "");
            AddEntry(entries, "MelonSmoothie", "西瓜奶昔", "西瓜", "");
            AddEntry(entries, "Mushroom_Burrito", "卷饼：蘑菇", "卷饼：蘑菇", "");
            AddEntry(entries, "MushroomPineappleTomatoKebob", "菠萝蘑菇番茄烤串", "菠蘑番", "");
            AddEntry(entries, "OnionBroccoliCheeseSoup", "西兰花芝士汤", "汤-西兰花芝士", "");
            AddEntry(entries, "OnionCarrotPotatoSoup", "土豆胡萝卜汤", "汤-土豆胡萝卜", "");
            AddEntry(entries, "OnionPotatoSoupLeek", "土豆韭葱汤", "汤-土豆韭葱", "");
            AddEntry(entries, "OrangeSodaFloat_Chocolate", "橙子汽水可可冰淇淋(黄)", "冰淇淋-可可+黄", "");
            AddEntry(entries, "OrangeSodaFloat_Vanilla", "橙子汽水香草冰淇淋(黄)", "冰淇淋-香草+黄", "");
            AddEntry(entries, "Pancake_Chocolate", "煎饼：巧克力", "煎饼：巧克力", "");
            AddEntry(entries, "Pancake_Plain", "煎饼：原味", "煎饼：原味", "");
            AddEntry(entries, "Pasta_Marinara_New", "意面：鱼虾", "意面：鱼虾", "");
            AddEntry(entries, "Pasta_Marinara_SO", "海鲜意面", "海鲜意面", "Marinara Pasta");
            AddEntry(entries, "Pasta_MeatOnly_New", "意面：牛肉", "意面：牛肉", "");
            AddEntry(entries, "Pasta_MeatOnly_SO", "牛肉意面", "牛肉意面", "Meat Pasta");
            AddEntry(entries, "Pasta_MushroomOnly_New", "意面：蘑菇", "意面：蘑菇", "");
            AddEntry(entries, "Pasta_MushroomOnly_SO", "蘑菇意面", "蘑菇意面", "Mushroom Pasta");
            AddEntry(entries, "Pasta_TomatoOnly_New", "意面：番茄", "意面：番茄", "");
            AddEntry(entries, "Pasta_TomatoOnly_SO", "番茄意面", "番茄意面", "Tomato Pasta");
            AddEntry(entries, "PeperoniPizza", "披萨：香肠", "披萨：香肠", "");
            AddEntry(entries, "Pizza_Chicken_SO", "鸡肉披萨", "鸡肉披萨", "Chicken Pizza");
            AddEntry(entries, "Pizza_Olives", "橄榄披萨", "披萨-橄榄", "");
            AddEntry(entries, "Pizza_Peperoni_SO", "香肠披萨", "香肠披萨", "Pepperoni Pizza");
            AddEntry(entries, "Pizza_Plain_SO", "原味披萨", "原味披萨", "Plain Pizza");
            AddEntry(entries, "RootBeerFloat_Chocolate", "根汁汽水可可冰淇淋(棕)", "冰淇淋-可可+棕", "");
            AddEntry(entries, "RootBeerFloat_Vanilla", "根汁汽水香草冰淇淋(棕)", "冰淇淋-香草+棕", "");
            AddEntry(entries, "Salad_Corn_Onion", "沙拉：生菜玉米", "沙拉：生菜玉米", "");
            AddEntry(entries, "Salad_Cucumber", "沙拉：生菜番茄黄瓜", "沙拉：生菜番茄黄瓜", "");
            AddEntry(entries, "Salad_Cucumber_Onion", "生菜黄瓜沙拉", "沙拉-生菜黄瓜", "");
            AddEntry(entries, "Salad_Cucumber_SO", "黄瓜沙拉", "黄瓜沙拉", "Cucumber Salad");
            AddEntry(entries, "Salad_Cucumber_Tomato_Onion", "生菜番茄黄瓜沙拉", "沙拉-生菜番茄黄瓜", "");
            AddEntry(entries, "Salad_Plain", "沙拉：原味", "沙拉：原味", "");
            AddEntry(entries, "Salad_Plain_SO", "原味沙拉", "原味沙拉", "Plain Salad");
            AddEntry(entries, "Salad_Tomato", "沙拉：生菜番茄", "沙拉：生菜番茄", "");
            AddEntry(entries, "Salad_Tomato_Onion", "生菜番茄沙拉", "沙拉-生菜番茄", "");
            AddEntry(entries, "Salad_Tomato_SO", "番茄沙拉", "番茄沙拉", "Tomato Salad");
            AddEntry(entries, "Smores_Banana", "香蕉烤棉花糖饼干", "饼干-香蕉", "");
            AddEntry(entries, "Smores_Chocolate", "巧克力烤棉花糖饼干", "饼干-巧克力", "");
            AddEntry(entries, "Smores_Plain", "烤棉花糖饼干", "饼干-素", "");
            AddEntry(entries, "Smores_Strawberry", "草莓烤棉花糖饼干", "饼干-草莓", "");
            AddEntry(entries, "Smores_Strawberry_Banana", "草莓香蕉烤棉花糖饼干", "饼干-草莓香蕉", "");
            AddEntry(entries, "Steamed_Carrot_SO", "蒸胡萝卜", "蒸胡萝卜", "Steamed Carrot");
            AddEntry(entries, "Steamed_Fish_SO", "蒸鱼", "蒸鱼", "Steamed Fish");
            AddEntry(entries, "Steamed_Meat_SO", "蒸肉", "蒸肉", "Steamed Meat");
            AddEntry(entries, "Steamed_Prawns_SO", "蒸虾", "蒸虾", "Steamed Prawns");
            AddEntry(entries, "SteamedSpecial_Carrot", "蒸菜：胡萝卜", "蒸菜：胡萝卜", "");
            AddEntry(entries, "SteamedSpecial_Fish", "蒸菜：鱼", "蒸菜：鱼", "");
            AddEntry(entries, "SteamedSpecial_Meat", "蒸菜：肉", "蒸菜：肉", "");
            AddEntry(entries, "SteamedSpecial_Prawns", "蒸菜：虾", "蒸菜：虾", "");
            AddEntry(entries, "StrawberryPancake", "煎饼：草莓", "煎饼：草莓", "");
            AddEntry(entries, "StrawberrySmoothie", "草莓奶昔", "草莓", "");
            AddEntry(entries, "Sushi_All", "寿司：鱼黄瓜", "寿司：鱼黄瓜", "");
            AddEntry(entries, "Sushi_All_SO", "鱼黄瓜寿司", "鱼黄瓜寿司", "Fish Cucumber Sushi");
            AddEntry(entries, "Sushi_Cucumber", "寿司：黄瓜", "寿司：黄瓜", "");
            AddEntry(entries, "Sushi_Cucumber_SO", "黄瓜寿司", "黄瓜寿司", "Cucumber Sushi");
            AddEntry(entries, "Sushi_Fish", "寿司：鱼", "寿司：鱼", "");
            AddEntry(entries, "Sushi_Fish_SO", "鱼寿司", "鱼寿司", "Fish Sushi");
            AddEntry(entries, "Sushi_PlainFish", "刺身：鱼", "刺身：鱼", "");
            AddEntry(entries, "Sushi_PlainFish_SO", "鱼刺身", "鱼刺身", "Fish Sashimi");
            AddEntry(entries, "Sushi_PlainPrawn", "刺身：虾", "刺身：虾", "");
            AddEntry(entries, "Sushi_PlainPrawn_SO", "虾刺身", "虾刺身", "Prawn Sashimi");
            AddEntry(entries, "Tomato_Corn_Onion", "番茄玉米沙拉", "沙拉-番茄玉米", "");
            AddEntry(entries, "Tomato_Cucumber_Onion", "番茄黄瓜沙拉", "沙拉-番茄黄瓜", "");
            return entries;
        }
    }
}
