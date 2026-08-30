// Exercises the pure recipe-family taxonomy and DIY inference pipeline with no
// Unity or optional-mod assemblies loaded.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

#pragma warning disable CA1861

namespace OC2MenuManager.Tests;

public sealed class RecipeCategoryCatalogTests
{
    [Fact]
    public void TokenizationNormalizesSeparatorsCamelCaseCjkWrappersAndNumericSuffixes()
    {
        Assert.Equal(
            new[] { "pan", "cake", "strawberry" },
            RecipeCategoryCatalog.TokenizeMeaningfulName("CompositePanCake_Strawberry_02_SO"));
        Assert.Equal(
            new[] { "草", "莓", "汁", "加", "冰" },
            RecipeCategoryCatalog.TokenizeMeaningfulName(" 草莓汁加冰_03 "));
        Assert.Equal(
            new[] { "moon", "cake", "red" },
            RecipeCategoryCatalog.TokenizeMeaningfulName("MoonCake  Red"));
    }

    [Fact]
    public void SceneInferenceFindsRepeatedCjkSuffixWithoutSpacingTheLabel()
    {
        Dictionary<int, RecipeCategoryAssignment> result = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[]
            {
                Evidence(1, "苹果奶冻", DIYRecipeKind.Mixed),
                Evidence(2, "草莓奶冻", DIYRecipeKind.Mixed)
            });

        Assert.Equal(result[1].Key, result[2].Key);
        Assert.Equal("奶冻", result[1].EnglishName);
        Assert.Equal("奶冻", result[1].ChineseName);
        Assert.Equal(RecipeCategorySource.Scene, result[1].Source);
        Assert.Equal("smoothie", result[1].TierKey);
    }

    [Fact]
    public void SceneFamilyUsesOneTierInheritanceWhenSomeMembersLackKindEvidence()
    {
        Dictionary<int, RecipeCategoryAssignment> result = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[]
            {
                Evidence(1, "AuroraTeaApple", DIYRecipeKind.Mixed),
                Evidence(2, "AuroraTeaBerry")
            });

        Assert.Equal(result[1].Key, result[2].Key);
        Assert.Equal("smoothie", result[1].TierKey);
        Assert.Equal(result[1].TierKey, result[2].TierKey);
    }

    [Fact]
    public void UnknownStructuralFamilyCarriesTheOtherTierKey()
    {
        RecipeCategoryEvidence evidence = Evidence(1, "958743");
        evidence.ModelIdentity = "Author_Plate";

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(new[] { evidence })[1];

        Assert.Equal("diy-structure:unknown:author-plate", assignment.Key);
        Assert.Equal("other", assignment.TierKey);
        Assert.Equal(RecipeCategorySource.Structure, assignment.Source);
    }

    [Fact]
    public void CookedStructuralFallbackUsesIconIdentityWhenProcessMetadataIsMissing()
    {
        RecipeCategoryEvidence evidence = Evidence(1, "958743", DIYRecipeKind.Cooked);
        evidence.IconIdentity = "Author_Oven_Icon";

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(new[] { evidence })[1];

        Assert.Equal("diy-structure:cooked:author-oven-icon", assignment.Key);
        Assert.Equal("Cooked DIY: Author Oven Icon", assignment.EnglishName);
        Assert.Equal("roast", assignment.TierKey);
        Assert.Equal(RecipeCategorySource.Structure, assignment.Source);
    }

    [Fact]
    public void ClassificationUsesNativeThenSemanticThenSceneThenStructureThenFallback()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "Cake_Plain"),
            Evidence(2, "Soup_Onion"),
            Evidence(3, "AuroraTeaApple", DIYRecipeKind.Mixed),
            Evidence(4, "AuroraTeaBerry", DIYRecipeKind.Mixed),
            Evidence(5, "Alpha", DIYRecipeKind.Composite, required: new[] { "Author_Base_Wrap" }),
            Evidence(6, "Beta", DIYRecipeKind.Composite, required: new[] { "Author_Base_Wrap" }),
            Evidence(7, "7867856", DIYRecipeKind.Cooked, cookingIdentity: "Oven_Step"),
            Evidence(8, "786786")
        };

        Dictionary<int, RecipeCategoryAssignment> result = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence);

        AssertAssignment(result[1], "cake", "Cake", "蛋糕", "cake", RecipeCategorySource.Native);
        AssertAssignment(result[2], "soup", "Soup", "汤", "soup", RecipeCategorySource.Semantic);
        Assert.Equal(RecipeCategorySource.Scene, result[3].Source);
        Assert.Equal(result[3].Key, result[4].Key);
        Assert.Equal("Aurora Tea", result[3].EnglishName);
        Assert.Equal("smoothie", result[3].TierKey);
        Assert.Equal(RecipeCategorySource.Scene, result[5].Source);
        Assert.Equal(result[5].Key, result[6].Key);
        Assert.Contains("component:author-base-wrap", result[5].Key, StringComparison.Ordinal);
        AssertAssignment(
            result[7],
            "diy-structure:cooked:oven-step",
            "Cooked DIY: Oven Step",
            "DIY 烹饪: Oven Step",
            "roast",
            RecipeCategorySource.Structure);
        AssertAssignment(result[8], "other", "Other", "其他", "other", RecipeCategorySource.Fallback);
    }

    [Fact]
    public void UniqueMeaningfulNameAvoidsOtherWhenStructuralMetadataIsMissing()
    {
        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[] { Evidence(1, "AuthorsAzureFeast") })[1];

        Assert.Equal("diy-name:authors-azure-feast", assignment.Key);
        Assert.Equal("Authors Azure Feast", assignment.EnglishName);
        Assert.Equal(RecipeCategorySource.Fallback, assignment.Source);
    }

    [Fact]
    public void ClassificationIsDeterministicWhenProviderOrderChanges()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(14, "AuthorTeaPeach", DIYRecipeKind.Mixed, required: new[] { "Shared_Cup" }),
            Evidence(11, "AuthorTeaApple", DIYRecipeKind.Mixed, required: new[] { "Shared_Cup" }),
            Evidence(12, "OpaqueAlpha", DIYRecipeKind.Cooked, required: new[] { "Shared_Base" }),
            Evidence(13, "OpaqueBeta", DIYRecipeKind.Cooked, required: new[] { "Shared_Base" })
        };

        Dictionary<int, RecipeCategoryAssignment> forward = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence);
        evidence.Reverse();
        Dictionary<int, RecipeCategoryAssignment> reversed = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence);

        Assert.Equal(forward.Keys.OrderBy(id => id), reversed.Keys.OrderBy(id => id));
        foreach (int id in forward.Keys)
        {
            Assert.True(RecipeCategoryCatalog.AreEquivalent(forward[id], reversed[id]));
        }
    }

    [Fact]
    public void DerivedFamiliesInheritNativeTierOverridesAndLocalizedLabels()
    {
        RecipeCategoryAssignment coldChocolate = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[] { Evidence(1, "ColdChocolateStrawberry") })[1];

        Assert.Equal("Cold Chocolate", RecipeCategoryCatalog.GetDisplayCategoryName(coldChocolate, false));
        Assert.Equal("冷巧克力", RecipeCategoryCatalog.GetDisplayCategoryName(coldChocolate, true));
        Assert.Equal(6, RecipeCategoryCatalog.GetCategoryTierByKey(coldChocolate.Key));

        try
        {
            RecipeCategoryCatalog.SetCategoryTierOverride("hotchocolate", 3);
            Assert.Equal(3, RecipeCategoryCatalog.GetCategoryTierByKey(coldChocolate.Key));
        }
        finally
        {
            RecipeCategoryCatalog.SetCategoryTierOverride("hotchocolate", 6);
        }

        Assert.Equal(6, RecipeCategoryCatalog.GetCategoryTierByKey(coldChocolate.Key));
    }

    [Theory]
    [InlineData("Soup_Onion", "soup")]
    [InlineData("Pizza_Mushroom", "pizza")]
    [InlineData("Kebob_Fish_Prawn_Pineapple_Combo", "kebob")]
    [InlineData("MoonCake_Red", "moonpie")]
    [InlineData("Dumplingsteamer_MeatLettuce", "dumpling")]
    [InlineData("Cream puff_Blueberry", "creampuff")]
    [InlineData("草莓汁加冰", "fruitice")]
    [InlineData("Guozhi_LanMei_SO", "fruitjuice")]
    [InlineData("chaofan_egg", "friedrice")]
    [InlineData("jiaozi_meat", "dumpling")]
    public void SemanticFamiliesCoverInstalledCrossLevelNamingStyles(string internalName, string expectedKey)
    {
        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[] { Evidence(1, internalName) })[1];

        Assert.Equal(expectedKey, assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void SemanticMatchingDoesNotTreatPartialWordsAsKnownFamilies()
    {
        Dictionary<int, RecipeCategoryAssignment> result = RecipeCategoryCatalog.ClassifyDIYRecipes(
            new[]
            {
                Evidence(1, "MoonCakeishExperiment"),
                Evidence(2, "ShowcaseSpecial")
            });

        Assert.NotEqual("moonpie", result[1].Key);
        Assert.NotEqual("cake", result[1].Key);
        Assert.NotEqual("cake", result[2].Key);
    }

    [Fact]
    public void MalformedEvidenceIsIgnoredWithoutDestabilizingValidAssignments()
    {
        Assert.Empty(RecipeCategoryCatalog.ClassifyDIYRecipes(null!));

        var evidence = new RecipeCategoryEvidence?[]
        {
            null,
            Evidence(0, "Soup_Onion"),
            Evidence(9, string.Empty)
        };

        Dictionary<int, RecipeCategoryAssignment> result = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence!);

        Assert.Single(result);
        AssertAssignment(result[9], "other", "Other", "其他", "other", RecipeCategorySource.Fallback);
    }

    [Fact]
    public void RwFiveProducesTheTwelveAgreedFamiliesForAllFiftyRecipes()
    {
        var fixture = new (int Id, string Name)[]
        {
            (15614, "Cake_Plain"),
            (15618, "Cake_Chocolate"),
            (25656, "Pancake_Chocolate"),
            (26020, "FruitPie_Blackberry"),
            (101593, "FruitPlatter_OrangePeachGrapes"),
            (112822, "FruitPie_Apple"),
            (112832, "FruitPie_Cherry"),
            (130976, "Donut_Raspberry"),
            (228988, "Donut_Plain"),
            (228996, "Donut_Chocolate"),
            (19990406, "Milk_Cake_Cherry"),
            (19990407, "Milk_Cake_Pineapple"),
            (19990408, "Milk_Cake_Peach"),
            (19990420, "Donut_Strawberry_Blueberry"),
            (19990430, "CompositePanCakeStrawBanana"),
            (19990431, "OptionalPanCakeStraw"),
            (19990433, "OptionalPanCakeblueberry"),
            (19990440, "FruitPie_AppleBlackberry"),
            (19990441, "FruitPie_AppleBlackberry"),
            (19991000, "HotChocolate"),
            (19991001, "HotChocolateHoney"),
            (19991002, "HotChocolateVanilla"),
            (19991003, "HotChocolateHoneyVanilla"),
            (19991004, "ColdChocolate"),
            (19991005, "ColdChocolateHoney"),
            (19991006, "ColdChocolateHoney"),
            (19991007, "StrawberryMilk"),
            (19991008, "PeachCheeseIceMilk"),
            (19991009, "OrangeJuice"),
            (19991010, "BananaMilk"),
            (19991011, "BlueberryIce"),
            (19991013, "PlatterPeachOrangeStrawberry"),
            (19991014, "PlatterGrapeOrangeBanana"),
            (19991015, "PlatterGrapePeachPineapple"),
            (19991016, "GrapeJuice"),
            (19991017, "PeachJuice"),
            (19991018, "HotAppleOrangePineappleHoney"),
            (19991019, "HotAppleStrawberryPineappleHoney"),
            (19991020, "HotAppleOrangePeachHoney"),
            (19991021, "MelonIce"),
            (19991022, "StrawberryIceMilkCheese"),
            (19991023, "OrangeIceMilkCheese"),
            (19991024, "HotAppleGrapeStrawberryHoney"),
            (19991025, "HotChocolateMarshmellow"),
            (19991026, "ColdChocolateStrawberry"),
            (19991027, "ColdMilk"),
            (19991028, "HotAppleHoney"),
            (19991029, "CherryMilk"),
            (19991030, "HotStrawberryMilk"),
            (19991041, "PlatterStrawberryBananaPineapple")
        };
        List<RecipeCategoryEvidence> evidence = fixture
            .Select(recipe => Evidence(recipe.Id, recipe.Name))
            .ToList();

        Dictionary<int, RecipeCategoryAssignment> assignments = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence);
        Dictionary<string, int> counts = assignments.Values
            .GroupBy(assignment => assignment.EnglishName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Assert.Equal(50, assignments.Count);
        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Cake"] = 5,
                ["Pancake"] = 4,
                ["Fruit Pie"] = 5,
                ["Donut"] = 4,
                ["Hot Chocolate"] = 5,
                ["Cold Chocolate"] = 4,
                ["Milk Drinks"] = 5,
                ["Ice Milk"] = 3,
                ["Fruit Juice"] = 3,
                ["Hot Fruit Drinks"] = 5,
                ["Fruit Ice"] = 2,
                ["Fruit Platter"] = 5
            },
            counts);
    }

    private static RecipeCategoryEvidence Evidence(
        int id,
        string name,
        DIYRecipeKind kind = DIYRecipeKind.Unknown,
        string? cookingIdentity = null,
        string[]? required = null,
        string[]? optional = null)
    {
        var evidence = new RecipeCategoryEvidence(id, name)
        {
            Kind = kind,
            CookingIdentity = cookingIdentity ?? string.Empty
        };
        if (required is not null)
        {
            evidence.RequiredComponentNames.AddRange(required);
        }

        if (optional is not null)
        {
            evidence.OptionalComponentNames.AddRange(optional);
        }

        return evidence;
    }

    private static void AssertAssignment(
        RecipeCategoryAssignment assignment,
        string key,
        string englishName,
        string chineseName,
        string tierKey,
        RecipeCategorySource source)
    {
        Assert.Equal(key, assignment.Key);
        Assert.Equal(englishName, assignment.EnglishName);
        Assert.Equal(chineseName, assignment.ChineseName);
        Assert.Equal(tierKey, assignment.TierKey);
        Assert.Equal(source, assignment.Source);
    }
}
