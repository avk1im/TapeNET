using HelpNET.Content;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="HelpUri"/> parsing.
/// </summary>
public class HelpUriTests
{
    // ── Topic URIs ────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_TopicUri_ReturnsTopicKindAndId()
    {
        var uri = HelpUri.TryParse("help://topic/dialog.restore");

        Assert.NotNull(uri);
        Assert.Equal(HelpUriKind.Topic, uri.Kind);
        Assert.Equal("dialog.restore", uri.Target);
    }

    [Fact]
    public void TryParse_TopicUri_WithNestedId()
    {
        var uri = HelpUri.TryParse("help://topic/concepts/incremental-backup");

        Assert.NotNull(uri);
        Assert.Equal(HelpUriKind.Topic, uri.Kind);
        Assert.Equal("concepts/incremental-backup", uri.Target);
    }

    // ── Glossary URIs ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_GlossaryUri_ReturnsGlossaryKindAndTerm()
    {
        var uri = HelpUri.TryParse("help://glossary/toc");

        Assert.NotNull(uri);
        Assert.Equal(HelpUriKind.Glossary, uri.Kind);
        Assert.Equal("toc", uri.Target);
    }

    // ── Action URIs ───────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ActionUri_ReturnsActionKindAndId()
    {
        var uri = HelpUri.TryParse("help://action/open-restore-dialog");

        Assert.NotNull(uri);
        Assert.Equal(HelpUriKind.Action, uri.Kind);
        Assert.Equal("open-restore-dialog", uri.Target);
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void TryParse_SchemeIsCaseInsensitive()
    {
        Assert.NotNull(HelpUri.TryParse("HELP://topic/home"));
        Assert.NotNull(HelpUri.TryParse("Help://topic/home"));
    }

    [Fact]
    public void TryParse_CategoryIsCaseInsensitive()
    {
        var uri = HelpUri.TryParse("help://TOPIC/home");
        Assert.NotNull(uri);
        Assert.Equal(HelpUriKind.Topic, uri.Kind);
    }

    // ── Malformed inputs ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_NonHelpScheme_ReturnsNull()
    {
        Assert.Null(HelpUri.TryParse("https://example.com/page"));
        Assert.Null(HelpUri.TryParse("http://localhost/help"));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsNull()
    {
        Assert.Null(HelpUri.TryParse(""));
    }

    [Fact]
    public void TryParse_NoCategorySlash_ReturnsNull()
    {
        Assert.Null(HelpUri.TryParse("help://topiconly"));
    }

    [Fact]
    public void TryParse_EmptyTarget_ReturnsNull()
    {
        Assert.Null(HelpUri.TryParse("help://topic/"));
    }

    [Fact]
    public void TryParse_UnknownCategory_ReturnsNull()
    {
        Assert.Null(HelpUri.TryParse("help://unknown/some-id"));
    }
}
