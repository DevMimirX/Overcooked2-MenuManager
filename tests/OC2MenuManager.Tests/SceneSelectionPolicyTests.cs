// Verifies scene-selector filtering/layout and authoritative DIY catalog snapshot
// decisions independently from Unity and optional mod binaries.
using System.Collections.Generic;
using System.Linq;
using Xunit;

#pragma warning disable CA1861

namespace OC2MenuManager.Tests;

public sealed class SceneSelectionPolicyTests
{
    [Theory]
    [InlineData("s_rw_3")]
    [InlineData("RW_3")]
    [InlineData("Dessert House")]
    [InlineData("甜品小屋")]
    public void SearchMatchesSceneIdentityAndBothLocalizedNames(string query)
    {
        Assert.True(SceneSelectionPolicy.Matches(
            query,
            "s_rw_3",
            "Current language name [s_rw_3]",
            "RW Collection - Dessert House [s_rw_3]",
            "菊花梨的关卡集 - 甜品小屋 [s_rw_3]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySearchIncludesEveryScene(string query)
    {
        Assert.True(SceneSelectionPolicy.Matches(query, "s_test", "Test", "Test", "测试"));
    }

    [Theory]
    [InlineData(true, "s_rw_5", "s_rw_1", true, "s_sushi_1_1", "s_rw_5")]
    [InlineData(false, "s_rw_5", "s_rw_1", true, "s_sushi_1_1", "s_rw_1")]
    [InlineData(false, "s_rw_5", "missing", false, "s_sushi_1_1", "s_sushi_1_1")]
    [InlineData(true, "", "s_rw_1", true, "s_sushi_1_1", "s_rw_1")]
    public void ActiveSceneOverridesConfiguredSceneOnlyDuringARound(
        bool inActiveRound,
        string activeScene,
        string configuredScene,
        bool configuredSceneAvailable,
        string fallbackScene,
        string expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.ResolveEffectiveSceneName(
                inActiveRound,
                activeScene,
                configuredScene,
                configuredSceneAvailable,
                fallbackScene));
    }

    [Theory]
    [InlineData(true, "s_rw_1", false, "s_rw_5", "s_rw_1")]
    [InlineData(false, "s_rw_1", true, "s_rw_1", "s_rw_1")]
    [InlineData(false, "", false, "s_sushi_1_1", "s_sushi_1_1")]
    [InlineData(false, "temporarily_missing", false, "s_sushi_1_1", "temporarily_missing")]
    public void RuntimeAndTransientFallbacksDoNotOverwriteAnExplicitConfiguredScene(
        bool inActiveRound,
        string configuredScene,
        bool configuredSceneAvailable,
        string effectiveScene,
        string expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.ResolveConfiguredSceneName(
                inActiveRound,
                configuredScene,
                configuredSceneAvailable,
                effectiveScene));
    }

    [Fact]
    public void PostRoundManualSelectionWinsOverTheStillCachedRuntimeScene()
    {
        string configuredScene = "s_rw_5";
        string effectiveScene = SceneSelectionPolicy.ResolveEffectiveSceneName(
            true,
            "s_rw_5",
            configuredScene,
            true,
            "s_sushi_1_1");
        configuredScene = SceneSelectionPolicy.ResolveConfiguredSceneName(
            true,
            configuredScene,
            true,
            effectiveScene);
        Assert.Equal("s_rw_5", configuredScene);

        configuredScene = "s_rw_1";
        effectiveScene = SceneSelectionPolicy.ResolveEffectiveSceneName(
            false,
            "s_rw_5",
            configuredScene,
            true,
            "s_sushi_1_1");
        configuredScene = SceneSelectionPolicy.ResolveConfiguredSceneName(
            false,
            configuredScene,
            true,
            effectiveScene);

        Assert.Equal("s_rw_1", effectiveScene);
        Assert.Equal("s_rw_1", configuredScene);
    }

    [Fact]
    public void OnlyExplicitNavigationRequestsAutomaticScrolling()
    {
        SceneDropdownScrollRequest request = SceneDropdownScrollRequest.None;
        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.CatalogRefreshed);
        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.CatalogRefreshed);
        Assert.Equal(SceneDropdownScrollRequest.None, request);

        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.DropdownOpened);
        Assert.Equal(SceneDropdownScrollRequest.RevealKeyboardTarget, request);
        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.CatalogRefreshed);
        Assert.Equal(SceneDropdownScrollRequest.RevealKeyboardTarget, request);

        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.SearchChanged);
        Assert.Equal(SceneDropdownScrollRequest.ResetToTop, request);
        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.KeyboardMoved);
        Assert.Equal(SceneDropdownScrollRequest.RevealKeyboardTarget, request);
        request = SceneSelectionPolicy.UpdateScrollRequest(request, SceneDropdownNavigationEvent.UserScrolled);
        Assert.Equal(SceneDropdownScrollRequest.None, request);
    }

    [Fact]
    public void ManualScrollSurvivesPollingWhileClearRetargetsTheFirstResult()
    {
        SceneDropdownScrollRequest request = SceneDropdownScrollRequest.None;
        string keyboardTarget = "s_rw_4";

        for (var poll = 0; poll < 4; poll++)
        {
            request = SceneSelectionPolicy.UpdateScrollRequest(
                request,
                SceneDropdownNavigationEvent.CatalogRefreshed);
            keyboardTarget = SceneSelectionPolicy.ResolveKeyboardTargetSceneName(
                false,
                keyboardTarget,
                true,
                "s_rw_1",
                true,
                "s_base");
        }

        Assert.Equal(SceneDropdownScrollRequest.None, request);
        Assert.Equal("s_rw_4", keyboardTarget);

        request = SceneSelectionPolicy.UpdateScrollRequest(
            request,
            SceneDropdownNavigationEvent.SearchChanged);
        keyboardTarget = SceneSelectionPolicy.ResolveKeyboardTargetSceneName(
            true,
            keyboardTarget,
            true,
            "s_rw_1",
            true,
            "s_base");

        Assert.Equal(SceneDropdownScrollRequest.ResetToTop, request);
        Assert.Equal("s_base", keyboardTarget);
    }

    [Theory]
    [InlineData(false, "s_rw_4", true, "s_rw_1", true, "s_base", "s_rw_4")]
    [InlineData(false, "missing", false, "s_rw_1", true, "s_base", "s_rw_1")]
    [InlineData(false, "missing", false, "missing", false, "s_base", "s_base")]
    [InlineData(true, "s_rw_4", true, "s_rw_1", true, "s_base", "s_base")]
    public void KeyboardTargetSurvivesRefreshByIdentityButSearchAndClearStartAtTheFirstResult(
        bool retargetFirstResult,
        string previousTarget,
        bool previousTargetAvailable,
        string selectedScene,
        bool selectedSceneAvailable,
        string firstResult,
        string expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.ResolveKeyboardTargetSceneName(
                retargetFirstResult,
                previousTarget,
                previousTargetAvailable,
                selectedScene,
                selectedSceneAvailable,
                firstResult));
    }

    [Fact]
    public void FilteringPreservesProviderOrderingAndReportsTheExpectedCount()
    {
        var source = new[]
        {
            new SceneSearchCandidate("s_rw_1", "RW One", "RW One", "RW 一"),
            new SceneSearchCandidate("s_li_1", "Li One", "Li One", "Li 一"),
            new SceneSearchCandidate("s_rw_2", "RW Two", "RW Two", "RW 二"),
            new SceneSearchCandidate("s_rw_3", "RW Three", "RW Three", "RW 三"),
            new SceneSearchCandidate("s_rw_4", "RW Four", "RW Four", "RW 四"),
            new SceneSearchCandidate("s_rw_5", "RW Five", "RW Five", "RW 五"),
            new SceneSearchCandidate("s_test", "Test", "Test", "测试")
        };

        var matches = source
            .Where(candidate => SceneSelectionPolicy.Matches(
                "s_rw_",
                candidate.SceneName,
                candidate.DisplayName,
                candidate.EnglishDisplayName,
                candidate.ChineseDisplayName))
            .Select(candidate => candidate.SceneName)
            .ToArray();

        Assert.Equal(new[] { "s_rw_1", "s_rw_2", "s_rw_3", "s_rw_4", "s_rw_5" }, matches);
    }

    [Theory]
    [InlineData(760f, 418f)]
    [InlineData(301f, 165.55f)]
    [InlineData(100f, 160f)]
    [InlineData(1000f, 420f)]
    public void DropdownHeightUsesResponsiveBounds(float windowHeight, float expected)
    {
        float actual = SceneSelectionPolicy.CalculateDropdownHeight(windowHeight, 0.55f, 160f, 420f);
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
    }

    [Theory]
    [InlineData(1080f, 24f, 760f, 620f, 760f)]
    [InlineData(650f, 24f, 760f, 620f, 602f)]
    [InlineData(349f, 24f, 760f, 620f, 301f)]
    [InlineData(20f, 24f, 760f, 620f, 1f)]
    public void SettingsWindowDimensionNeverExceedsTheUsableScreen(
        float screen,
        float margin,
        float desired,
        float minimum,
        float expected)
    {
        float actual = SceneSelectionPolicy.CalculateFittedWindowDimension(screen, margin, desired, minimum);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1920f, 860f, 24f, -100f, 24f)]
    [InlineData(1920f, 860f, 24f, 1800f, 1036f)]
    [InlineData(349f, 301f, 24f, 90f, 24f)]
    public void SettingsWindowPositionRemainsOnScreen(
        float screen,
        float window,
        float margin,
        float current,
        float expected)
    {
        Assert.Equal(expected, SceneSelectionPolicy.CalculateClampedWindowPosition(screen, window, margin, current));
    }

    [Theory]
    [InlineData(100, 0f, 100f, 25f, 1, 0, 5)]
    [InlineData(100, 250f, 100f, 25f, 1, 9, 15)]
    [InlineData(3, 100f, 100f, 25f, 1, 3, 3)]
    [InlineData(0, 0f, 100f, 25f, 1, 0, 0)]
    public void VirtualizedRowsIncludeOnlyTheViewportAndOverscan(
        int itemCount,
        float scroll,
        float viewport,
        float rowHeight,
        int overscan,
        int expectedFirst,
        int expectedEnd)
    {
        SceneSelectionPolicy.CalculateVisibleRange(
            itemCount,
            scroll,
            viewport,
            rowHeight,
            overscan,
            out int first,
            out int end);

        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData(0, 20, 100f, 100f, 25f, 0f)]
    [InlineData(10, 20, 0f, 100f, 25f, 175f)]
    [InlineData(5, 20, 125f, 100f, 25f, 125f)]
    [InlineData(19, 20, 0f, 100f, 25f, 400f)]
    public void KeyboardTargetIsScrolledIntoView(
        int index,
        int count,
        float current,
        float viewport,
        float rowHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.CalculateScrollOffsetForItem(index, count, current, viewport, rowHeight));
    }

    [Theory]
    [InlineData(100, 250f, 100f, 25f, 250f)]
    [InlineData(10, 250f, 100f, 25f, 150f)]
    [InlineData(3, -20f, 100f, 25f, 0f)]
    [InlineData(0, 20f, 100f, 25f, 0f)]
    public void UserScrollIsPreservedAndOnlyClampedWhenResultsShrink(
        int itemCount,
        float current,
        float viewport,
        float rowHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.CalculateClampedScrollOffset(itemCount, current, viewport, rowHeight));
    }

    [Theory]
    [InlineData(100, 250f, 3f, 100f, 25f, 325f)]
    [InlineData(100, 250f, -20f, 100f, 25f, 0f)]
    [InlineData(10, 140f, 3f, 100f, 25f, 150f)]
    [InlineData(3, 20f, 3f, 100f, 25f, 0f)]
    public void NestedSceneWheelInputMovesOnlyWithinTheAvailableListRange(
        int itemCount,
        float current,
        float wheelDelta,
        float viewport,
        float rowHeight,
        float expected)
    {
        Assert.Equal(
            expected,
            SceneSelectionPolicy.CalculateWheelScrollOffset(itemCount, current, wheelDelta, viewport, rowHeight));
    }

    [Fact]
    public void InvalidWheelInputPreservesTheCurrentValidPosition()
    {
        Assert.Equal(
            250f,
            SceneSelectionPolicy.CalculateWheelScrollOffset(100, 250f, float.NaN, 100f, 25f));
    }

    [Fact]
    public void CatalogSnapshotTransitionsRetainOnlyUntrustworthyReads()
    {
        Assert.Equal(DIYCatalogSnapshotAction.Retain, DIYCatalogRefreshPolicy.EvaluateSnapshot(false, 0, 0));
        Assert.Equal(DIYCatalogSnapshotAction.Replace, DIYCatalogRefreshPolicy.EvaluateSnapshot(true, 46, 0));
        Assert.Equal(DIYCatalogSnapshotAction.Replace, DIYCatalogRefreshPolicy.EvaluateSnapshot(true, 45, 1));
        Assert.Equal(DIYCatalogSnapshotAction.Retain, DIYCatalogRefreshPolicy.EvaluateSnapshot(true, 0, 1));
        Assert.Equal(DIYCatalogSnapshotAction.Replace, DIYCatalogRefreshPolicy.EvaluateSnapshot(true, 0, 0));
        Assert.Equal(DIYCatalogSnapshotAction.Retain, DIYCatalogRefreshPolicy.EvaluateSnapshot(true, -1, 0));
    }

    [Fact]
    public void CatalogSceneIdentityRejectsEmptyAndCaseInsensitiveDuplicates()
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(DIYCatalogRefreshPolicy.TryAcceptSceneName(null!, accepted));
        Assert.False(DIYCatalogRefreshPolicy.TryAcceptSceneName("   ", accepted));
        Assert.True(DIYCatalogRefreshPolicy.TryAcceptSceneName("s_rw_1", accepted));
        Assert.False(DIYCatalogRefreshPolicy.TryAcceptSceneName("S_RW_1", accepted));
        Assert.True(DIYCatalogRefreshPolicy.TryAcceptSceneName("s_rw_2", accepted));
    }

    private sealed record SceneSearchCandidate(
        string SceneName,
        string DisplayName,
        string EnglishDisplayName,
        string ChineseDisplayName);
}
