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

    [Fact]
    public void AuthoringNameSuppliesClassificationEvidenceWithoutReplacingTheRuntimeName()
    {
        RecipeCategoryEvidence evidence = Evidence(
            1,
            "OpaqueRecipe1999",
            authoringName: "ColdChocolateVanilla");

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(new[] { evidence })[1];

        Assert.Equal("OpaqueRecipe1999", evidence.InternalName);
        AssertAssignment(
            assignment,
            "coldchocolate",
            "Cold Chocolate",
            "冷巧克力",
            "hotchocolate",
            RecipeCategorySource.Semantic);
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
    public void RwFiveWorkflowEvidenceCorrectsOutliersWithoutMergingCoherentDrinkFamilies()
    {
        List<RecipeCategoryEvidence> evidence = BuildRwFiveFixture();

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
                ["Hot Chocolate"] = 6,
                ["Cold Chocolate"] = 5,
                ["Milk Drinks"] = 3,
                ["Ice Milk"] = 3,
                ["Fruit Juice"] = 3,
                ["Hot Fruit Drinks"] = 5,
                ["Fruit Ice"] = 2,
                ["Fruit Platter"] = 5
            },
            counts);

        AssertAssignment(
            assignments[19991030],
            "hotchocolate",
            "Hot Chocolate",
            "热可可",
            "hotchocolate",
            RecipeCategorySource.Workflow);
        Assert.Equal("milkdrink", assignments[19991030].InitialCategoryKey);
        Assert.Contains("facet=hot", assignments[19991030].InferenceDetail, StringComparison.Ordinal);

        AssertAssignment(
            assignments[19991027],
            "coldchocolate",
            "Cold Chocolate",
            "冷巧克力",
            "hotchocolate",
            RecipeCategorySource.Workflow);
        Assert.Equal("milkdrink", assignments[19991027].InitialCategoryKey);
        Assert.Contains("facet=cold", assignments[19991027].InferenceDetail, StringComparison.Ordinal);

        Assert.All(
            new[] { 19991018, 19991019, 19991020, 19991024, 19991028 },
            id => Assert.Equal("hotfruitdrink", assignments[id].Key));
        Assert.All(
            new[] { 19991007, 19991010, 19991029 },
            id => Assert.Equal("milkdrink", assignments[id].Key));
        Assert.All(
            new[] { 19991008, 19991022, 19991023 },
            id => Assert.Equal("icemilk", assignments[id].Key));
        Assert.All(
            new[] { 19991011, 19991021 },
            id => Assert.Equal("fruitice", assignments[id].Key));

        evidence.Reverse();
        Dictionary<int, RecipeCategoryAssignment> reversed = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence);
        foreach (int id in assignments.Keys)
        {
            Assert.True(RecipeCategoryCatalog.AreEquivalent(assignments[id], reversed[id]));
        }
    }

    [Fact]
    public void RwFivePlayerAssignmentsMatchTheCanonicalFamilyGroups()
    {
        Dictionary<int, RecipeCategoryAssignment> assignments =
            RecipeCategoryCatalog.ClassifyDIYRecipes(BuildRwFiveFixture());
        int[] authoredRecipeIds = assignments.Keys.OrderBy(recipeId => recipeId).ToArray();

        Assert.Equal(
            SceneRecipeGroupResolutionStatus.Resolved,
            SceneRecipeGroupCatalog.Resolve(
                "s_rw_5",
                authoredRecipeIds,
                authoredRecipeIds,
                out SceneRecipeSelectionGroupSet? groups,
                out _));
        Assert.NotNull(groups);

        Assert.True(new HashSet<string>(StringComparer.Ordinal)
        {
            "hotchocolate", "fruitpie", "pancake"
        }.SetEquals(groups.Groups[0].RecipeIds.Select(recipeId => assignments[recipeId].Key)));
        Assert.True(new HashSet<string>(StringComparer.Ordinal)
        {
            "coldchocolate", "fruitjuice", "donut"
        }.SetEquals(groups.Groups[1].RecipeIds.Select(recipeId => assignments[recipeId].Key)));
        Assert.True(new HashSet<string>(StringComparer.Ordinal)
        {
            "hotfruitdrink", "cake", "fruitplatter"
        }.SetEquals(groups.Groups[2].RecipeIds.Select(recipeId => assignments[recipeId].Key)));
        Assert.True(new HashSet<string>(StringComparer.Ordinal)
        {
            "fruitplatter", "milkdrink", "icemilk", "fruitice"
        }.SetEquals(groups.Groups[3].RecipeIds.Select(recipeId => assignments[recipeId].Key)));
    }

    [Fact]
    public void WorkflowReconciliationKeepsSemanticCategoryWhenBaseEvidenceIsMissing()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Cocoa" }),
            Evidence(2, "HotChocolateHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Cocoa", "Honey" }),
            Evidence(3, "BananaMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Banana" }),
            Evidence(4, "CherryMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Cherry" }),
            Evidence(5, "HotStrawberryMilk", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Strawberry" })
        };

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[5];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void WorkflowReconciliationPreservesSemanticCategoryWhenTargetsTie()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(2, "HotChocolateHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(3, "HotAppleHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(4, "HotApplePeachHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(5, "BananaMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Banana" }),
            Evidence(6, "CherryMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Cherry" }),
            Evidence(7, "HotMilk", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" })
        };

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[7];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void RequiredBaseComponentsOutrankOptionalFlavorComponents()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(2, "HotChocolateVanilla", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk" }),
            Evidence(3, "HotAppleHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Honey" }),
            Evidence(4, "HotPeachHoney", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Honey" }),
            Evidence(5, "BananaMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Banana" }),
            Evidence(6, "CherryMilk", DIYRecipeKind.Mixed, mixingIdentity: "SharedMixer", required: new[] { "Milk", "Cherry" }),
            Evidence(
                7,
                "HotMilkHoney",
                DIYRecipeKind.Cooked,
                cookingIdentity: "SharedPot",
                required: new[] { "Milk" },
                optional: new[] { "Honey" })
        };

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[7];

        Assert.Equal("hotchocolate", assignment.Key);
        Assert.Equal(RecipeCategorySource.Workflow, assignment.Source);
    }

    [Fact]
    public void WorkflowReconciliationRequiresTargetComponentsToBeatCurrentFamily()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Chocolate" }),
            Evidence(2, "HotChocolateVanilla", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Chocolate", "Vanilla" }),
            Evidence(3, "AlmondMilk", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Almond" }),
            Evidence(4, "CreamMilk", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Cream" }),
            Evidence(5, "HotAlmondCreamMilk", DIYRecipeKind.Cooked, cookingIdentity: "SharedPot", required: new[] { "Milk", "Almond", "Cream" })
        };

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[5];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void WorkflowReconciliationDoesNotTreatSharedPresentationAsAProcess()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, required: new[] { "Milk", "Chocolate" }),
            Evidence(2, "HotChocolateVanilla", DIYRecipeKind.Cooked, required: new[] { "Milk", "Chocolate", "Vanilla" }),
            Evidence(3, "BananaMilk", DIYRecipeKind.Cooked, required: new[] { "Milk", "Banana" }),
            Evidence(4, "CherryMilk", DIYRecipeKind.Cooked, required: new[] { "Milk", "Cherry" }),
            Evidence(5, "HotMilkChocolate", DIYRecipeKind.Cooked, required: new[] { "Milk", "Chocolate" })
        };
        foreach (RecipeCategoryEvidence recipe in evidence)
        {
            recipe.ModelIdentity = "SharedCup";
            recipe.IconIdentity = "SharedDrinkIcon";
        }

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[5];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void WorkflowTargetRequiresStrictProcessMajority()
    {
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(1, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: "PotA", required: new[] { "Milk" }),
            Evidence(2, "HotChocolateHoney", DIYRecipeKind.Cooked, cookingIdentity: "PotA", required: new[] { "Milk" }),
            Evidence(3, "HotChocolateVanilla", DIYRecipeKind.Cooked, cookingIdentity: "PotB", required: new[] { "Milk" }),
            Evidence(4, "HotChocolateMarshmellow", DIYRecipeKind.Cooked, cookingIdentity: "PotB", required: new[] { "Milk" }),
            Evidence(5, "BananaMilk", DIYRecipeKind.Mixed, mixingIdentity: "Mixer", required: new[] { "Milk", "Banana" }),
            Evidence(6, "CherryMilk", DIYRecipeKind.Mixed, mixingIdentity: "Mixer", required: new[] { "Milk", "Cherry" }),
            Evidence(7, "HotMilk", DIYRecipeKind.Cooked, cookingIdentity: "PotA", required: new[] { "Milk" })
        };

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(evidence)[7];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void MalformedComponentEvidenceCannotDestabilizeSemanticClassification()
    {
        RecipeCategoryEvidence evidence = Evidence(1, "ColdMilk", DIYRecipeKind.Mixed, mixingIdentity: "Mixer");
        evidence.Components.Add(null!);
        evidence.Components.Add(new RecipeComponentEvidence(string.Empty, false, -10));

        RecipeCategoryAssignment assignment = RecipeCategoryCatalog.ClassifyDIYRecipes(new[] { evidence })[1];

        Assert.Equal("milkdrink", assignment.Key);
        Assert.Equal(RecipeCategorySource.Semantic, assignment.Source);
    }

    [Fact]
    public void WorkflowSourceNameIsAuditable()
    {
        Assert.Equal("workflow", RecipeCategoryCatalog.GetSourceName(RecipeCategorySource.Workflow));
    }

    private static List<RecipeCategoryEvidence> BuildRwFiveFixture()
    {
        const string pot = "rw-shared-pot";
        const string mixer = "rw-shared-mixer";
        var evidence = new List<RecipeCategoryEvidence>
        {
            Evidence(15614, "Cake_Plain"),
            Evidence(15618, "Cake_Chocolate"),
            Evidence(25656, "Pancake_Chocolate"),
            Evidence(26020, "FruitPie_Blackberry"),
            Evidence(101593, "FruitPlatter_OrangePeachGrapes"),
            Evidence(112822, "FruitPie_Apple"),
            Evidence(112832, "FruitPie_Cherry"),
            Evidence(130976, "Donut_Raspberry"),
            Evidence(228988, "Donut_Plain"),
            Evidence(228996, "Donut_Chocolate"),
            Evidence(19990406, "Milk_Cake_Cherry"),
            Evidence(19990407, "Milk_Cake_Pineapple"),
            Evidence(19990408, "Milk_Cake_Peach"),
            Evidence(19990420, "Donut_Strawberry_Blueberry"),
            Evidence(19990430, "CompositePanCakeStrawBanana"),
            Evidence(19990431, "OptionalPanCakeStraw"),
            Evidence(19990433, "OptionalPanCakeblueberry"),
            Evidence(19990440, "FruitPie_AppleBlackberry"),
            Evidence(19990441, "FruitPie_AppleBlackberry"),

            Evidence(19991000, "HotChocolate", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Chocolate" }),
            Evidence(19991001, "HotChocolateHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Chocolate", "Honey" }),
            Evidence(19991002, "HotChocolateVanilla", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Chocolate", "Vanilla" }),
            Evidence(19991003, "HotChocolateHoneyVanilla", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Chocolate", "Honey", "Vanilla" }),
            Evidence(19991025, "HotChocolateMarshmellow", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Chocolate", "Marshmellow" }),
            Evidence(19991030, "HotStrawberryMilk", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Milk", "Strawberry", "Honey" }),

            Evidence(19991004, "ColdChocolate", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Chocolate", "IceCube" }),
            Evidence(19991005, "ColdChocolateHoney", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Chocolate", "IceCube", "Honey" }),
            Evidence(19991006, "ColdChocolateHoney", DIYRecipeKind.Mixed, mixingIdentity: mixer, authoringName: "ColdChocolateVanilla", required: new[] { "Milk", "Chocolate", "IceCube", "Vanilla" }),
            Evidence(19991026, "ColdChocolateStrawberry", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Chocolate", "IceCube", "Strawberry" }),
            Evidence(19991027, "ColdMilk", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "IceCube", "Vanilla", "Honey" }),

            Evidence(19991007, "StrawberryMilk", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Strawberry" }),
            Evidence(19991010, "BananaMilk", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Banana" }),
            Evidence(19991029, "CherryMilk", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "Cherry" }),

            Evidence(19991008, "PeachCheeseIceMilk", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "IceCube", "Cheese", "Peach" }),
            Evidence(19991022, "StrawberryIceMilkCheese", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "IceCube", "Cheese", "Strawberry" }),
            Evidence(19991023, "OrangeIceMilkCheese", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Milk", "IceCube", "Cheese", "Orange" }),

            Evidence(19991009, "OrangeJuice", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Orange" }),
            Evidence(19991016, "GrapeJuice", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Grape" }),
            Evidence(19991017, "PeachJuice", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Peach" }),

            Evidence(19991011, "BlueberryIce", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Blueberry", "IceCube" }),
            Evidence(19991021, "MelonIce", DIYRecipeKind.Mixed, mixingIdentity: mixer, required: new[] { "Melon", "IceCube" }),

            Evidence(19991018, "HotAppleOrangePineappleHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Apple", "Orange", "Pineapple", "Honey" }),
            Evidence(19991019, "HotAppleStrawberryPineappleHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Apple", "Strawberry", "Pineapple", "Honey" }),
            Evidence(19991020, "HotAppleOrangePeachHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Apple", "Orange", "Peach", "Honey" }),
            Evidence(19991024, "HotAppleGrapeStrawberryHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Apple", "Grape", "Strawberry", "Honey" }),
            Evidence(19991028, "HotAppleHoney", DIYRecipeKind.Cooked, cookingIdentity: pot, required: new[] { "Apple", "Honey" }),

            Evidence(19991013, "PlatterPeachOrangeStrawberry", DIYRecipeKind.Composite, required: new[] { "Peach", "Orange", "Strawberry" }),
            Evidence(19991014, "PlatterGrapeOrangeBanana", DIYRecipeKind.Composite, required: new[] { "Grape", "Orange", "Banana" }),
            Evidence(19991015, "PlatterGrapePeachPineapple", DIYRecipeKind.Composite, required: new[] { "Grape", "Peach", "Pineapple" }),
            Evidence(19991041, "PlatterStrawberryBananaPineapple", DIYRecipeKind.Composite, required: new[] { "Strawberry", "Banana", "Pineapple" })
        };

        return evidence;
    }

    private static RecipeCategoryEvidence Evidence(
        int id,
        string name,
        DIYRecipeKind kind = DIYRecipeKind.Unknown,
        string? cookingIdentity = null,
        string? mixingIdentity = null,
        string? authoringName = null,
        string[]? required = null,
        string[]? optional = null)
    {
        var evidence = new RecipeCategoryEvidence(id, name)
        {
            Kind = kind,
            CookingIdentity = cookingIdentity ?? string.Empty,
            MixingIdentity = mixingIdentity ?? string.Empty,
            AuthoringName = authoringName ?? string.Empty
        };
        if (required is not null)
        {
            foreach (string identity in required)
            {
                evidence.Components.Add(new RecipeComponentEvidence(identity, false, 0));
            }
        }

        if (optional is not null)
        {
            foreach (string identity in optional)
            {
                evidence.Components.Add(new RecipeComponentEvidence(identity, true, 0));
            }
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
