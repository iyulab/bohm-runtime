using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class PageCompatibilityTests
{
    private static readonly string[] Relayed = ["api.openai.com", "api.anthropic.com"];

    private static IReadOnlyList<string> Read(string html) => PageCompatibility.Read(html, Relayed);

    [Fact]
    public void A_page_that_keeps_its_data_in_localStorage_and_calls_an_AI_service_has_no_findings() =>
        Assert.Empty(Read("""<script>localStorage.setItem('k', v); fetch("https://api.anthropic.com/v1/messages", { method: "POST" });</script>"""));

    [Theory]
    [InlineData("const request = indexedDB.open('notes', 1);", PageCompatibility.IndexedDb)]
    [InlineData("sessionStorage.setItem('notes', json);", PageCompatibility.SessionStorage)]
    [InlineData("sessionStorage['notes'] = json;", PageCompatibility.SessionStorage)]
    [InlineData("document.cookie = 'notes=' + json;", PageCompatibility.Cookies)]
    public void A_store_that_does_not_stay_is_reported_when_nothing_is_written_to_localStorage(string script, string finding)
    {
        Assert.Equal([finding], Read($"<script>{script}</script>"));
        Assert.Empty(Read($"<script>{script} localStorage.setItem('notes', json);</script>"));
    }

    [Fact]
    public void Reading_cookies_or_comparing_them_is_not_keeping_data_in_them() =>
        Assert.Empty(Read("<script>if (document.cookie == '') show(); const c = document.cookie;</script>"));

    [Theory]
    [InlineData("""fetch("https://api.example.com/rates")""")]
    [InlineData("""fetch(`https://api.example.com/rates?base=${b}`)""")]
    [InlineData("""xhr.open("GET", 'https://api.example.com/rates')""")]
    [InlineData("""new WebSocket("wss://stream.example.com/")""")]
    public void A_request_to_an_outside_server_is_reported(string request) =>
        Assert.Equal([PageCompatibility.OutsideData], Read($"<script>localStorage['k'] = 1; {request};</script>"));

    [Theory]
    [InlineData("""fetch("https://api.openai.com/v1/chat/completions")""")]
    [InlineData("""fetch("/__bohm/llm/company/v1/chat/completions")""")]
    [InlineData("""fetch("data.json")""")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js"></script>""")]
    public void AI_services_relative_addresses_and_CDN_scripts_are_not_outside_data(string text) =>
        Assert.Empty(Read($"<script>localStorage['k'] = 1;</script>{text}"));

    [Fact]
    public void A_host_that_only_ends_like_an_AI_service_is_outside_data() =>
        Assert.Equal([PageCompatibility.OutsideData], Read("""<script>localStorage['k'] = 1; fetch("https://notapi.openai.com.example.net/x");</script>"""));

    [Fact]
    public void An_application_that_keeps_its_data_only_in_an_online_database_is_reported() =>
        Assert.Equal([PageCompatibility.OnlineDatabase], Read("<script>const db = getFirestore(app); addDoc(collection(db, 'e'), x);</script>"));
}
