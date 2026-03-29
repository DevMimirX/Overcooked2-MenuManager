using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace HostUtilities
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
                return entry.ChineseFull;
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
                    return entry.ChineseShort;
                }

                if (!string.IsNullOrEmpty(entry.ChineseFull))
                {
                    return entry.ChineseFull;
                }
            }

            return GetEnglishName(internalName);
        }

        public static string GetEnglishName(string internalName)
        {
            DishNameEntry entry;
            if (TryGetEntry(internalName, out entry) && !string.IsNullOrEmpty(entry.English))
            {
                return entry.English;
            }

            return HumanizeInternalName(internalName);
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
                string value = shortName ? pair.Value.ChineseShort : pair.Value.ChineseFull;
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
            AddEntry(entries, "BeefBurger", "素汉堡", "汉堡-素", "");
            AddEntry(entries, "BeefBurgerCheese", "芝士汉堡", "汉堡-芝士", "");
            AddEntry(entries, "BeefBurgerMax", "生菜番茄汉堡", "汉堡-生菜番茄", "");
            AddEntry(entries, "BeefBurgerWithGreensNCheese", "生菜芝士汉堡", "汉堡-生菜芝士", "");
            AddEntry(entries, "BeefPotatoCarrotBroccoliRoast", "牛肉西兰花烧烤", "烧烤-肉+西兰花", "");
            AddEntry(entries, "BeefPotatoCarrotRoast", "牛肉烧烤", "烧烤-肉", "");
            AddEntry(entries, "BlueberryPancake", "蓝莓煎饼", "煎饼-蓝莓", "");
            AddEntry(entries, "Breakfast_Bacon_Egg", "培根蛋", "肉蛋", "");
            AddEntry(entries, "Breakfast_Bacon_Egg_Sausage", "培根蛋肠", "肉蛋肠", "");
            AddEntry(entries, "Breakfast_Sausage_Beans", "肠豆", "肠豆", "");
            AddEntry(entries, "Breakfast_Sausage_Beans_Egg", "肠豆蛋", "肠豆蛋", "");
            AddEntry(entries, "Breakfast_Sausage_Beans_Egg_Bacon", "肠豆培根蛋", "肠豆肉蛋", "");
            AddEntry(entries, "Burger_Cheese_SO", "芝士汉堡", "芝士汉堡", "Cheeseburger");
            AddEntry(entries, "Burger_CheeseLettuce_SO", "生菜芝士汉堡", "生菜芝士汉堡", "Cheese Lettuce Burger");
            AddEntry(entries, "Burger_LettuceTomato_SO", "生菜番茄汉堡", "生菜番茄汉堡", "Lettuce Tomato Burger");
            AddEntry(entries, "Burger_Plain_SO", "原味汉堡", "原味汉堡", "Plain Burger");
            AddEntry(entries, "Burrito_Chicken_SO", "鸡肉卷饼", "鸡肉卷饼", "Chicken Burrito");
            AddEntry(entries, "Burrito_Meat_SO", "牛肉卷饼", "牛肉卷饼", "Meat Burrito");
            AddEntry(entries, "Burrito_Mushroom_SO", "蘑菇卷饼", "蘑菇卷饼", "Mushroom Burrito");
            AddEntry(entries, "Cake_Carrot", "胡萝卜蛋糕", "蛋糕-胡萝卜", "");
            AddEntry(entries, "Cake_Chocolate", "巧克力蛋糕", "蛋糕-巧克力", "");
            AddEntry(entries, "Cake_Chocolate_SO", "巧克力蛋糕", "巧克力蛋糕", "Chocolate Cake");
            AddEntry(entries, "Cake_Honey_SO", "蜂蜜蛋糕", "蜂蜜蛋糕", "Honey Cake");
            AddEntry(entries, "Cake_HoneyCarrot_SO", "蜂蜜胡萝卜蛋糕", "蜂蜜胡萝卜蛋糕", "Honey Carrot Cake");
            AddEntry(entries, "Cake_HoneyChocolate_SO", "蜂蜜巧克力蛋糕", "蜂蜜巧克力蛋糕", "Honey Chocolate Cake");
            AddEntry(entries, "Cake_Plain", "蜂蜜蛋糕", "蛋糕-素", "");
            AddEntry(entries, "Cake_Plain_SO", "原味蛋糕", "原味蛋糕", "Plain Cake");
            AddEntry(entries, "Chicken_Burrito", "鸡肉卷饼", "卷饼-鸡肉", "");
            AddEntry(entries, "ChickenMeatTomatoKebob", "鸡肉番茄牛肉烤串", "鸡番肉", "");
            AddEntry(entries, "ChickenNuggetsAndChips_All", "炸鸡薯条", "炸鸡薯条", "");
            AddEntry(entries, "ChickenNuggetsAndChips_ChickenOnly", "炸鸡", "炸鸡", "");
            AddEntry(entries, "ChickenNuggetsAndChips_ChipsOnly", "炸薯条", "炸薯条", "");
            AddEntry(entries, "ChickenPizza", "鸡肉披萨", "披萨-鸡肉", "");
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
            AddEntry(entries, "DLC09_Pancake_Chocolate", "巧克力煎饼", "煎饼-巧克力", "");
            AddEntry(entries, "DLC09_Pancake_Plain", "素煎饼", "煎饼-素", "");
            AddEntry(entries, "DLC09_Pancake_Strawberry", "草莓煎饼", "煎饼-草莓", "");
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
            AddEntry(entries, "MargheritaPizza", "披萨", "披萨-素", "");
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
            AddEntry(entries, "Meat_Burrito", "肉卷饼", "卷饼-肉", "");
            AddEntry(entries, "MeatMushroomPineappleKebob", "菠萝蘑菇牛肉烤串", "菠蘑肉", "");
            AddEntry(entries, "MegaSmoothie", "四拼奶昔", "四拼", "");
            AddEntry(entries, "MelonSmoothie", "西瓜奶昔", "西瓜", "");
            AddEntry(entries, "Mushroom_Burrito", "蘑菇卷饼", "卷饼-蘑菇", "");
            AddEntry(entries, "MushroomPineappleTomatoKebob", "菠萝蘑菇番茄烤串", "菠蘑番", "");
            AddEntry(entries, "OnionBroccoliCheeseSoup", "西兰花芝士汤", "汤-西兰花芝士", "");
            AddEntry(entries, "OnionCarrotPotatoSoup", "土豆胡萝卜汤", "汤-土豆胡萝卜", "");
            AddEntry(entries, "OnionPotatoSoupLeek", "土豆韭葱汤", "汤-土豆韭葱", "");
            AddEntry(entries, "OrangeSodaFloat_Chocolate", "橙子汽水可可冰淇淋(黄)", "冰淇淋-可可+黄", "");
            AddEntry(entries, "OrangeSodaFloat_Vanilla", "橙子汽水香草冰淇淋(黄)", "冰淇淋-香草+黄", "");
            AddEntry(entries, "Pancake_Chocolate", "巧克力煎饼", "煎饼-巧克力", "");
            AddEntry(entries, "Pancake_Plain", "素煎饼", "煎饼-素", "");
            AddEntry(entries, "Pasta_Marinara_New", "鱼虾意面", "意面-鱼虾", "");
            AddEntry(entries, "Pasta_Marinara_SO", "海鲜意面", "海鲜意面", "Marinara Pasta");
            AddEntry(entries, "Pasta_MeatOnly_New", "牛肉意面", "意面-牛肉", "");
            AddEntry(entries, "Pasta_MeatOnly_SO", "牛肉意面", "牛肉意面", "Meat Pasta");
            AddEntry(entries, "Pasta_MushroomOnly_New", "蘑菇意面", "意面-蘑菇", "");
            AddEntry(entries, "Pasta_MushroomOnly_SO", "蘑菇意面", "蘑菇意面", "Mushroom Pasta");
            AddEntry(entries, "Pasta_TomatoOnly_New", "番茄意面", "意面-番茄", "");
            AddEntry(entries, "Pasta_TomatoOnly_SO", "番茄意面", "番茄意面", "Tomato Pasta");
            AddEntry(entries, "PeperoniPizza", "香肠披萨", "披萨-香肠", "");
            AddEntry(entries, "Pizza_Chicken_SO", "鸡肉披萨", "鸡肉披萨", "Chicken Pizza");
            AddEntry(entries, "Pizza_Olives", "橄榄披萨", "披萨-橄榄", "");
            AddEntry(entries, "Pizza_Peperoni_SO", "香肠披萨", "香肠披萨", "Pepperoni Pizza");
            AddEntry(entries, "Pizza_Plain_SO", "原味披萨", "原味披萨", "Plain Pizza");
            AddEntry(entries, "RootBeerFloat_Chocolate", "根汁汽水可可冰淇淋(棕)", "冰淇淋-可可+棕", "");
            AddEntry(entries, "RootBeerFloat_Vanilla", "根汁汽水香草冰淇淋(棕)", "冰淇淋-香草+棕", "");
            AddEntry(entries, "Salad_Corn_Onion", "生菜玉米沙拉", "沙拉-生菜玉米", "");
            AddEntry(entries, "Salad_Cucumber", "生菜番茄黄瓜沙拉", "沙拉-生菜番茄黄瓜", "");
            AddEntry(entries, "Salad_Cucumber_Onion", "生菜黄瓜沙拉", "沙拉-生菜黄瓜", "");
            AddEntry(entries, "Salad_Cucumber_SO", "黄瓜沙拉", "黄瓜沙拉", "Cucumber Salad");
            AddEntry(entries, "Salad_Cucumber_Tomato_Onion", "生菜番茄黄瓜沙拉", "沙拉-生菜番茄黄瓜", "");
            AddEntry(entries, "Salad_Plain", "生菜沙拉", "沙拉-生菜", "");
            AddEntry(entries, "Salad_Plain_SO", "原味沙拉", "原味沙拉", "Plain Salad");
            AddEntry(entries, "Salad_Tomato", "生菜番茄沙拉", "沙拉-生菜番茄", "");
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
            AddEntry(entries, "SteamedSpecial_Carrot", "蒸胡萝卜", "蒸胡萝卜", "");
            AddEntry(entries, "SteamedSpecial_Fish", "蒸鱼", "蒸鱼", "");
            AddEntry(entries, "SteamedSpecial_Meat", "蒸肉", "蒸肉", "");
            AddEntry(entries, "SteamedSpecial_Prawns", "蒸虾", "蒸虾", "");
            AddEntry(entries, "StrawberryPancake", "草莓煎饼", "煎饼-草莓", "");
            AddEntry(entries, "StrawberrySmoothie", "草莓奶昔", "草莓", "");
            AddEntry(entries, "Sushi_All", "鱼黄瓜寿司", "寿司-鱼黄瓜", "");
            AddEntry(entries, "Sushi_All_SO", "鱼黄瓜寿司", "鱼黄瓜寿司", "Fish Cucumber Sushi");
            AddEntry(entries, "Sushi_Cucumber", "黄瓜寿司", "寿司-黄瓜", "");
            AddEntry(entries, "Sushi_Cucumber_SO", "黄瓜寿司", "黄瓜寿司", "Cucumber Sushi");
            AddEntry(entries, "Sushi_Fish", "鱼寿司", "寿司-鱼", "");
            AddEntry(entries, "Sushi_Fish_SO", "鱼寿司", "鱼寿司", "Fish Sushi");
            AddEntry(entries, "Sushi_PlainFish", "生鱼片", "刺身-鱼", "");
            AddEntry(entries, "Sushi_PlainFish_SO", "鱼刺身", "鱼刺身", "Fish Sashimi");
            AddEntry(entries, "Sushi_PlainPrawn", "生虾片", "刺身-虾", "");
            AddEntry(entries, "Sushi_PlainPrawn_SO", "虾刺身", "虾刺身", "Prawn Sashimi");
            AddEntry(entries, "Tomato_Corn_Onion", "番茄玉米沙拉", "沙拉-番茄玉米", "");
            AddEntry(entries, "Tomato_Cucumber_Onion", "番茄黄瓜沙拉", "沙拉-番茄黄瓜", "");
            return entries;
        }
    }
}
