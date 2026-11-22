// wafgen: standalone CLI that materializes the four OpenResty artifacts from a
// rules JSON file (same camelCase contract as /api/waf/rules/export).
// Usage: wafgen <rules.json> <outdir>
// Lives on the ci/cloud-linux branch only (live-fire assembly helper).
using System.Text.Json;
using System.Text.Json.Serialization;
using TaoOps.Server.Waf;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: wafgen <rules.json> <outdir>");
    return 2;
}

var rulesPath = args[0];
var outDir = args[1];
var opts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};
WafRuleSet ruleSet;
try
{
    ruleSet = JsonSerializer.Deserialize<WafRuleSet>(File.ReadAllText(rulesPath), opts)
              ?? throw new InvalidOperationException("null ruleset");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"wafgen: bad rules file: {ex.Message}");
    return 1;
}

var bundle = OpenRestyConfigGenerator.Generate(ruleSet);
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "waf.conf"), bundle.NginxConf);
File.WriteAllText(Path.Combine(outDir, "site.conf"), bundle.SiteConf);
File.WriteAllText(Path.Combine(outDir, "access.lua"), bundle.AccessLua);
File.WriteAllText(Path.Combine(outDir, "rules.json"), bundle.RulesJson);
Console.WriteLine($"wafgen: 4 artifacts -> {outDir}");
return 0;
