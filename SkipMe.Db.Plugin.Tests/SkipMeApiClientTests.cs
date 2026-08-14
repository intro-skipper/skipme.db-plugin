// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using SkipMe.Db.Plugin.Services;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public class SkipMeApiClientTests
{
    [Fact]
    public void TryParseMissingIndexes_SingleMissingIndex_ParsesSuccessfully()
    {
        var json = "{\"error\":\"No timestamps found for item indexes: 1\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 3, out var missing);

        Assert.True(result);
        Assert.Single(missing);
        Assert.Contains(1, missing);
    }

    [Fact]
    public void TryParseMissingIndexes_MultipleMissingIndexes_ParsesAllCorrectly()
    {
        var json = "{\"error\":\"No timestamps found for item indexes: 0, 2, 3, 5\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 6, out var missing);

        Assert.True(result);
        Assert.Equal(4, missing.Count);
        Assert.Contains(0, missing);
        Assert.Contains(2, missing);
        Assert.Contains(3, missing);
        Assert.Contains(5, missing);
    }

    [Fact]
    public void TryParseMissingIndexes_AllMissingIndexes_ParsesAll()
    {
        var json = "{\"error\":\"No timestamps found for item indexes: 0, 1, 2\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 3, out var missing);

        Assert.True(result);
        Assert.Equal(3, missing.Count);
    }

    [Fact]
    public void TryParseMissingIndexes_InvalidJson_ReturnsFalse()
    {
        var invalidJson = "Not a json string";
        var result = SkipMeApiClient.TryParseMissingIndexes(invalidJson, 5, out var missing);

        Assert.False(result);
        Assert.Empty(missing);
    }

    [Fact]
    public void TryParseMissingIndexes_MissingErrorProperty_ReturnsFalse()
    {
        var json = "{\"message\":\"Something else went wrong\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 5, out var missing);

        Assert.False(result);
        Assert.Empty(missing);
    }

    [Fact]
    public void TryParseMissingIndexes_UnrelatedErrorMessage_ReturnsFalse()
    {
        var json = "{\"error\":\"Rate limit exceeded\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 5, out var missing);

        Assert.False(result);
        Assert.Empty(missing);
    }

    [Fact]
    public void TryParseMissingIndexes_IndexOutOfBounds_ReturnsFalse()
    {
        var json = "{\"error\":\"No timestamps found for item indexes: 99\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 5, out var missing);

        Assert.False(result);
        Assert.Empty(missing);
    }

    [Fact]
    public void TryParseMissingIndexes_NegativeIndex_ReturnsFalse()
    {
        var json = "{\"error\":\"No timestamps found for item indexes: -1\"}";
        var result = SkipMeApiClient.TryParseMissingIndexes(json, 5, out var missing);

        Assert.False(result);
        Assert.Empty(missing);
    }
}
