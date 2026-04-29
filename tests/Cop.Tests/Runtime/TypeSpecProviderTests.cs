using NUnit.Framework;
using Cop.Providers;

namespace Cop.Tests;

[TestFixture]
public class TypeSpecProviderTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

    [Test]
    public void TypeSpecHttpProvider_PutPathSegments_FindsOddSegmentPut()
    {
        var scriptsDir = Path.Combine(RepoRoot, "samples", "s7-TypeSpec");
        var rootPath = Path.Combine(scriptsDir, "spec");

        if (!Directory.Exists(scriptsDir) || !Directory.Exists(rootPath))
            Assert.Ignore("Sample directory not found");

        var result = Engine.Run(scriptsDir, rootPath);

        // Should have no fatal errors
        Assert.That(result.Errors, Is.Empty, $"Fatal errors: {string.Join("; ", result.Errors)}");

        // Should produce output — the PUT with odd segments (replaceParts at /widgets/{widgetId}/parts)
        var outputTexts = result.Outputs.Select(o => o.Content.ToPlainText()).ToList();
        Assert.That(outputTexts, Has.Count.GreaterThan(0), "Expected at least one warning output");

        // The odd-segment PUT is "replaceParts" at /widgets/{widgetId}/parts (3 segments)
        var replaceParts = outputTexts.FirstOrDefault(t => t.Contains("replaceParts"));
        Assert.That(replaceParts, Is.Not.Null, $"Expected warning for replaceParts. Outputs: {string.Join(" | ", outputTexts)}");

        // The even-segment PUTs (update, updatePart) should NOT appear
        var evenSegmentWarnings = outputTexts.Where(t => t.Contains("update") && !t.Contains("replaceParts")).ToList();
        Assert.That(evenSegmentWarnings, Is.Empty, $"Even-segment PUTs should not be flagged: {string.Join(" | ", evenSegmentWarnings)}");
    }

    [Test]
    public void TypeSpecHttpProvider_ExtendsResolution_InheritsTemplateOperations()
    {
        // Parse just the dynamic-routes.tsp file to verify extends resolution
        var specDir = Path.Combine(RepoRoot, "samples", "s7-TypeSpec", "spec");
        var dynamicFile = Path.Combine(specDir, "dynamic-routes.tsp");

        if (!File.Exists(dynamicFile))
            Assert.Ignore("dynamic-routes.tsp not found");

        var source = File.ReadAllText(dynamicFile);
        var rawSpec = TypeSpecProvider.TspParser.Parse(source);

        // Should have parsed 4 interfaces (ResourceOperations, Customers, OrderOperations, Orders)
        Assert.That(rawSpec.Interfaces, Has.Count.EqualTo(4), $"Expected 4 interfaces. Got: {string.Join(", ", rawSpec.Interfaces.Select(i => i.Name))}");

        var templateIface = rawSpec.Interfaces.First(i => i.Name == "ResourceOperations");
        Assert.That(templateIface.TemplateParameters, Has.Count.EqualTo(1), "ResourceOperations should have 1 template param");
        Assert.That(templateIface.TemplateParameters[0], Is.EqualTo("T"));
        Assert.That(templateIface.Operations, Has.Count.EqualTo(5), "ResourceOperations should have 5 CRUD ops");

        var customersIface = rawSpec.Interfaces.First(i => i.Name == "Customers");
        Assert.That(customersIface.Extends, Has.Count.EqualTo(1));
        Assert.That(customersIface.Extends[0], Does.Contain("ResourceOperations"));
        Assert.That(customersIface.Operations, Has.Count.EqualTo(0), "Before transform, Customers has no own operations");

        // Now run the HTTP transformer which resolves extends
        var httpSpec = TypeSpecProvider.HttpTransformer.Transform(rawSpec);

        // Customers should now have inherited operations with Customer type substituted
        var customerOps = httpSpec.Operations.Where(o => o.Interface == "Customers").ToList();
        Assert.That(customerOps, Has.Count.EqualTo(5), $"Customers should have 5 inherited ops. Got: {string.Join(", ", httpSpec.Operations.Select(o => $"{o.Name}({o.Interface})"))}");

        // Verify type substitution happened (T → Customer)
        var listOp = customerOps.First(o => o.Name == "list");
        Assert.That(listOp.Verb, Is.EqualTo("get"));
        Assert.That(listOp.Path, Does.Contain("/customers"));

        var updateOp = customerOps.First(o => o.Name == "update");
        Assert.That(updateOp.Verb, Is.EqualTo("put"));
        Assert.That(updateOp.Path, Does.Contain("/customers"));

        // Verify @autoRoute: Orders should have computed route /orders
        var orderOps = httpSpec.Operations.Where(o => o.Interface == "Orders").ToList();
        Assert.That(orderOps, Has.Count.EqualTo(4), $"Orders should have 4 inherited ops. Got: {string.Join(", ", orderOps.Select(o => o.Name))}");

        var orderListOp = orderOps.First(o => o.Name == "list");
        Assert.That(orderListOp.Path, Does.Contain("/orders"), $"Expected /orders route from @autoRoute. Got: {orderListOp.Path}");

        // read op should have key path param injected
        var orderReadOp = orderOps.First(o => o.Name == "read");
        Assert.That(orderReadOp.Path, Does.Contain("/orders"), $"Expected /orders in read path. Got: {orderReadOp.Path}");
        Assert.That(orderReadOp.Path, Does.Contain("{orderId}"), $"Expected {{orderId}} path param from @key. Got: {orderReadOp.Path}");
    }
}
