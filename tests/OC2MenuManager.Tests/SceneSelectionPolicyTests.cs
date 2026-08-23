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
