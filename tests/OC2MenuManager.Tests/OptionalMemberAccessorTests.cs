// Verifies the quiet reflection boundary used for optional provider metadata.
// Missing, inherited, static, null, indexed, and throwing members are exercised
// without requiring either optional mod assembly.
using System;
using System.Globalization;
using OC2MenuManager;
using Xunit;

namespace OC2MenuManager.Tests;

public sealed class OptionalMemberAccessorTests
{
    [Fact]
    public void ReadsPrivateMembersAcrossTheInheritanceChain()
    {
        var metadata = new DerivedMetadata();

        Assert.Equal("base-field", OptionalMemberAccessor.GetValue(metadata, "baseField"));
        Assert.Equal("base-property", OptionalMemberAccessor.GetValue(metadata, "BaseProperty"));
        Assert.Equal("derived-field", OptionalMemberAccessor.GetValue(metadata, "Label"));
    }

    [Fact]
    public void MissingMembersRemainQuietAndDeterministicAcrossRepeatedReads()
    {
        var metadata = new DerivedMetadata();

        Assert.Null(OptionalMemberAccessor.GetValue(metadata, "NotAuthored"));
        Assert.Null(OptionalMemberAccessor.GetValue(metadata, "NotAuthored"));
        Assert.False(OptionalMemberAccessor.TryGetInstanceValue(metadata, "NotAuthored", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void StrictReadDistinguishesPresentNullFromMissing()
    {
        var metadata = new DerivedMetadata();

        Assert.True(OptionalMemberAccessor.TryGetInstanceValue(metadata, "PresentNull", out var value));
        Assert.Null(value);
        Assert.False(OptionalMemberAccessor.TryGetInstanceValue(metadata, "Missing", out _));
    }

    [Fact]
    public void StaticIndexedAndThrowingMembersFailSafelyForStrictReads()
    {
        var metadata = new DerivedMetadata();

        Assert.Equal("static", OptionalMemberAccessor.GetValue(metadata, "StaticValue"));
        Assert.False(OptionalMemberAccessor.TryGetInstanceValue(metadata, "StaticValue", out _));
        Assert.Null(OptionalMemberAccessor.GetValue(metadata, "Item"));
        Assert.False(OptionalMemberAccessor.TryGetInstanceValue(metadata, "Item", out _));
        Assert.Null(OptionalMemberAccessor.GetValue(metadata, "Throwing"));
        Assert.False(OptionalMemberAccessor.TryGetInstanceValue(metadata, "Throwing", out _));
    }

    private class BaseMetadata
    {
#pragma warning disable CS0414
        private readonly string baseField = "base-field";
#pragma warning restore CS0414
        private readonly string baseProperty = "base-property";
        private readonly string baseLabel = "base-property-label";

        private string BaseProperty => baseProperty;

        public string Label => baseLabel;
    }

    private sealed class DerivedMetadata : BaseMetadata
    {
#pragma warning disable CS0108, CS0414
        private readonly string Label = "derived-field";
#pragma warning restore CS0108, CS0414
#pragma warning disable CS0649
        private readonly object? presentNull;
#pragma warning restore CS0649
        private readonly string throwingMessage = "provider getter failed";

        public object? PresentNull => presentNull;

        public static string StaticValue => "static";

        public string Throwing => throw new InvalidOperationException(throwingMessage);

        public string this[int index] => index.ToString(CultureInfo.InvariantCulture);
    }
}
