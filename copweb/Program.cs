using Cop.Driver.Services;
using Cop.Driver.Models;

Console.WriteLine("copweb driver v0.1.0");

var builder = WebApplication.CreateBuilder(args);

// Configure the URL to listen on port 5100
builder.WebHost.UseUrls("http://localhost:5100");

// Register services
builder.Services.AddSingleton<TaskManager>();

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// Map API endpoints for task management
app.MapGet("/api/tasks", (TaskManager tm) => tm.GetAllTasks());

app.MapGet("/api/tasks/{id}", (string id, TaskManager tm) => {
    var task = tm.GetTask(id);
    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapPost("/api/tasks", async (HttpContext ctx, TaskManager tm) => {
    // Read spec from request body
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var force = ctx.Request.Query.ContainsKey("force");
    
    // Extract spec path from header or use default
    var specPath = ctx.Request.Headers["X-Spec-Path"].FirstOrDefault() ?? "spec.md";
    
    try {
        var task = tm.Submit(specPath, body, force);
        return Results.Created($"/api/tasks/{task.Id}", task);
    } catch (DuplicateTaskException ex) {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapDelete("/api/tasks/{id}", (string id, TaskManager tm) => {
    try { 
        tm.CancelTask(id); 
        return Results.Ok(); 
    }
    catch (TaskNotFoundException) { 
        return Results.NotFound(); 
    }
});

app.MapPost("/api/tasks/{id}/feedback", async (string id, HttpContext ctx, TaskManager tm) => {
    using var reader = new StreamReader(ctx.Request.Body);
    var message = await reader.ReadToEndAsync();
    try { 
        tm.SendFeedback(id, message); 
        return Results.Ok(); 
    }
    catch (TaskNotFoundException) { 
        return Results.NotFound(); 
    }
});

app.MapPost("/api/tasks/{id}/pause", (string id, TaskManager tm) => {
    try { 
        tm.PauseTask(id); 
        return Results.Ok(); 
    }
    catch (TaskNotFoundException) { 
        return Results.NotFound(); 
    }
});

app.MapPost("/api/tasks/{id}/resume", async (string id, HttpContext ctx, TaskManager tm) => {
    using var reader = new StreamReader(ctx.Request.Body);
    var message = await reader.ReadToEndAsync();
    try { 
        tm.ResumeTask(id, string.IsNullOrEmpty(message) ? null : message); 
        return Results.Ok(); 
    }
    catch (TaskNotFoundException) { 
        return Results.NotFound(); 
    }
});

app.MapGet("/api/tasks/{id}/logs", (string id, TaskManager tm) => {
    var task = tm.GetTask(id);
    return task is null ? Results.NotFound() : Results.Ok(task.Log);
});

app.MapGet("/", async (HttpContext ctx) => {
    var isLocal = IsLocalRequest(ctx);
    var feedManager = new Cop.Core.FeedManager();
    var feeds = feedManager.GetFeeds();
    var summaries = new List<Cop.Driver.Pages.PackageSummary>();
    
    using var httpClient = new HttpClient();
    var githubSource = new Cop.Core.GitHubPackageSource(httpClient, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    var localSource = new Cop.Core.LocalPackageSource();
    
    foreach (var feed in feeds)
    {
        try
        {
            List<string> names;
            if (Cop.Core.FeedManager.IsLocalFeed(feed))
                names = localSource.ListPackages(feed);
            else
            {
                var parts = feed.Split('/');
                if (parts.Length >= 3)
                    names = await githubSource.ListPackagesAsync(parts[1], parts[2]);
                else continue;
            }

            foreach (var name in names)
            {
                Cop.Core.PackageMetadata? meta = null;
                try
                {
                    if (Cop.Core.FeedManager.IsLocalFeed(feed))
                        meta = await localSource.GetPackageMetadataAsync(feed, name);
                    else
                    {
                        var parts = feed.Split('/');
                        var pkgRef = new Cop.Core.PackageReference { Host = "github.com", FullPath = $"github.com/{parts[1]}/{parts[2]}/{name}", Owner = parts[1], Repo = parts[2], PackageName = name };
                        meta = await githubSource.GetPackageMetadataAsync(pkgRef);
                    }
                }
                catch { }

                bool hasInstructions = false, hasSkills = false, hasRules = false, hasTests = false;
                if (Cop.Core.FeedManager.IsLocalFeed(feed))
                {
                    var pkgDir = Cop.Core.LocalPackageSource.FindPackagePath(feed, name)
                        ?? Path.Combine(feed, name);
                    hasInstructions = HasDisplayableContent(Path.Combine(pkgDir, "instructions"));
                    hasSkills = HasDisplayableContent(Path.Combine(pkgDir, "skills"));
                    hasRules = HasDisplayableContent(Path.Combine(pkgDir, "Rules"));
                    hasTests = HasDisplayableContent(Path.Combine(pkgDir, "tests"));
                }

                summaries.Add(new(name, feed, meta?.Title, meta?.Description, meta?.Version, meta?.Authors, meta?.Tags, meta?.Language,
                    hasInstructions, hasSkills, hasRules, hasTests));
            }
        }
        catch { }
    }
    
    return Results.Content(Cop.Driver.Pages.PackageDirectory.Render(summaries, isLocal), "text/html; charset=utf-8");
});

app.MapGet("/packages", () => Results.Redirect("/"));

app.MapGet("/agents", (HttpContext ctx, TaskManager tm) => {
    if (!IsLocalRequest(ctx))
        return Results.NotFound();
    var tasks = tm.GetAllTasks();
    return Results.Content(Cop.Driver.Pages.Dashboard.Render(tasks), "text/html; charset=utf-8");
});

app.MapGet("/packages/{feed}/{name}", async (string feed, string name, HttpContext ctx) => {
    feed = Uri.UnescapeDataString(feed);
    var isLocal = IsLocalRequest(ctx);
    var localSource = new Cop.Core.LocalPackageSource();
    
    try
    {
        Cop.Core.PackageMetadata metadata;
        var files = new List<Cop.Driver.Pages.PackageFile>();

        if (Cop.Core.FeedManager.IsLocalFeed(feed))
        {
            metadata = await localSource.GetPackageMetadataAsync(feed, name);
            var packageDir = Cop.Core.LocalPackageSource.FindPackagePath(feed, name)
                ?? Path.Combine(feed, name);
            foreach (var filePath in Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(packageDir, filePath).Replace('\\', '/');
                if (relPath.StartsWith('.')) continue;
                var isBinary = IsBinaryFile(filePath);
                files.Add(new Cop.Driver.Pages.PackageFile
                {
                    RelativePath = relPath,
                    Content = isBinary ? null : await File.ReadAllTextAsync(filePath),
                    IsBinary = isBinary
                });
            }
        }
        else
        {
            using var httpClient = new HttpClient();
            var githubSource = new Cop.Core.GitHubPackageSource(httpClient, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
            var parts = feed.Split('/');
            if (parts.Length < 3) return Results.BadRequest("Invalid feed format");
            var pkgRef = new Cop.Core.PackageReference { Host = "github.com", FullPath = $"github.com/{parts[1]}/{parts[2]}/{name}", Owner = parts[1], Repo = parts[2], PackageName = name };
            metadata = await githubSource.GetPackageMetadataAsync(pkgRef);
            var downloaded = await githubSource.DownloadPackageFilesAsync(pkgRef);
            foreach (var kvp in downloaded)
            {
                var isBinary = kvp.Value.Length > 0 && kvp.Value.Take(8192).Any(b => b == 0);
                files.Add(new Cop.Driver.Pages.PackageFile
                {
                    RelativePath = kvp.Key,
                    Content = isBinary ? null : System.Text.Encoding.UTF8.GetString(kvp.Value),
                    IsBinary = isBinary
                });
            }
        }

        return Results.Content(
            Cop.Driver.Pages.PackageDetail.Render(feed, name, metadata, files, isLocal),
            "text/html; charset=utf-8");
    }
    catch (Cop.Core.PackageNotFoundException)
    {
        return Results.NotFound($"Package '{name}' not found in feed '{feed}'");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error loading package: {ex.Message}");
    }
});

static bool IsBinaryFile(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext is ".png" or ".jpg" or ".gif" or ".ico" or ".dll" or ".exe" or ".zip") return true;
    try
    {
        var bytes = new byte[8192];
        using var fs = File.OpenRead(path);
        int read = fs.Read(bytes, 0, bytes.Length);
        return bytes.Take(read).Any(b => b == 0);
    }
    catch { return false; }
}

static bool IsLocalRequest(HttpContext ctx)
{
    var remoteIp = ctx.Connection.RemoteIpAddress;
    if (remoteIp == null) return true;
    return System.Net.IPAddress.IsLoopback(remoteIp);
}

static bool HasDisplayableContent(string dirPath)
{
    if (!Directory.Exists(dirPath)) return false;
    return Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories)
        .Any(f => {
            var name = Path.GetFileName(f);
            return name != ".gitkeep" && !name.Equals("nuget-analyzers.yaml", StringComparison.OrdinalIgnoreCase);
        });
}

Console.WriteLine("copweb listening on http://localhost:5100");

// Open the dashboard in the default browser
try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "http://localhost:5100",
        UseShellExecute = true
    });
}
catch { /* best-effort; ignore if browser can't be opened */ }

app.Run();
