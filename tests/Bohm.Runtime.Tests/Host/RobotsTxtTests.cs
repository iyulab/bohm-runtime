using Bohm.Runtime.Host.Agent;

namespace Bohm.Runtime.Tests.Host;

public sealed class RobotsTxtTests
{
    private const string Token = RobotsPolicy.ProductToken;

    [Theory]
    [InlineData("/search?q=shoes", false)]
    [InlineData("/search/about", true)]
    [InlineData("/maps", true)]
    [InlineData("/", true)]
    public void The_longest_matching_rule_decides(string path, bool allowed)
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /search
            Allow: /search/about
            """);

        Assert.Equal(allowed, robots.Check(Token, path).Allowed);
    }

    [Fact]
    public void An_allow_wins_a_tie_of_equal_length()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /*?
            Allow: /?*
            """);

        Assert.True(robots.Check(Token, "/?q=shoes").Allowed);
        Assert.False(robots.Check(Token, "/lite?q=shoes").Allowed);
    }

    [Fact]
    public void A_group_naming_the_agent_replaces_the_star_group()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /

            User-agent: Bohm-Agent
            Disallow: /private
            """);

        Assert.True(robots.Check(Token, "/public").Allowed);
        Assert.False(robots.Check(Token, "/private/x").Allowed);
        Assert.False(robots.Check("Other-Bot", "/public").Allowed);
    }

    [Fact]
    public void Agent_names_are_matched_without_case_and_consecutive_lines_share_one_group()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: GPTBot
            User-agent: bohm-agent
            Disallow: /
            User-agent: *
            Allow: /
            """);

        Assert.False(robots.Check(Token, "/anything").Allowed);
        Assert.True(robots.Check("Someone", "/anything").Allowed);
    }

    [Fact]
    public void A_dollar_ends_the_path_and_a_star_matches_any_run()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /*.pdf$
            Disallow: /tmp/*/cache
            """);

        Assert.False(robots.Check(Token, "/papers/a.pdf").Allowed);
        Assert.True(robots.Check(Token, "/papers/a.pdf?download=1").Allowed);
        Assert.False(robots.Check(Token, "/tmp/x/y/cache/z").Allowed);
        Assert.True(robots.Check(Token, "/tmp/cache").Allowed);
    }

    [Fact]
    public void An_empty_disallow_comments_and_unknown_lines_change_nothing()
    {
        var robots = RobotsTxt.Parse("﻿User-agent: * # everyone\r\nDisallow:\r\nSitemap: https://example.com/sitemap.xml\r\nNoindex: /x\r\n");

        Assert.True(robots.Check(Token, "/x").Allowed);
        Assert.Null(robots.Check(Token, "/x").Rule);
    }

    [Fact]
    public void A_file_without_a_matching_group_allows_everything_and_robots_txt_itself_is_always_allowed()
    {
        Assert.True(RobotsTxt.Parse("User-agent: SomeBot\nDisallow: /").Check(Token, "/a").Allowed);
        Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow: /").Check(Token, "/robots.txt").Allowed);
    }

    [Fact]
    public void Paths_are_compared_with_non_ascii_characters_percent_encoded()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /café\n");

        Assert.False(robots.Check(Token, "/caf%c3%a9/menu").Allowed);
        Assert.False(robots.Check(Token, "/caf%C3%A9").Allowed);
        Assert.True(robots.Check(Token, "/cafe").Allowed);
    }

    [Fact]
    public void The_deciding_rule_and_the_crawl_delay_are_reported()
    {
        var verdict = RobotsTxt.Parse("User-agent: *\nCrawl-delay: 2.5\nDisallow: /board\n").Check(Token, "/board?page=3");

        Assert.False(verdict.Allowed);
        Assert.Equal("Disallow: /board", verdict.Rule);
        Assert.Equal(2.5, verdict.CrawlDelay);
    }
}
