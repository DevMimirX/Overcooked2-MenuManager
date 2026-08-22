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
