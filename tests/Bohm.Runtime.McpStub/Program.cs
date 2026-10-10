// A stdio MCP server small enough to read: newline-delimited JSON-RPC on stdin/stdout, one tool. The runtime's tests start it to
// see a server connect, list its tools and be closed — no MCP package, so what it says on the wire is all written here.
using System.Text.Json;
using System.Text.Json.Nodes;

var output = Console.Out;
while (Console.In.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    var message = JsonNode.Parse(line)!.AsObject();
    if (message["id"] is not { } id) continue;   // a notification: nothing to answer
    var result = message["method"]?.GetValue<string>() switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = message["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "stub", ["version"] = "0" },
        },
        "tools/list" => new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "echo",
                ["description"] = "Says back the text it is given.",
                ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            }),
        },
        "tools/call" => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message["params"]?["arguments"]?["text"]?.GetValue<string>() ?? "" }),
        },
        "ping" => new JsonObject(),
        _ => null,
    };
    var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
    if (result is null) answer["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
    else answer["result"] = result;
    output.WriteLine(answer.ToJsonString(JsonSerializerOptions.Default));
    output.Flush();
}
