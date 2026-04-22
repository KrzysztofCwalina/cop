---
name: foundry-development
description: 'Build AI-powered applications with Foundry Applications Service in C#, Python, or Node.js. Use this skill when creating Foundry apps, wiring the Foundry Applications Service, using Storage/Tables/Agents APIs, deploying agents with schemas, creating custom tools, managing stacks, running/testing Foundry applications, or using the Foundry CLI. If you have access to MCP tools from foundry-apps server, always call GetStackOverview first to understand the current stack state.'
---

# Foundry Application Development

Build AI-powered server applications using Foundry Applications Service and the Foundry Applications Service pattern in C#, Python, or Node.js.

## MCP Tools (foundry-apps server)

If you have MCP tools from the `foundry-apps` server available, **always call `GetStackOverview` first** when the user asks about their stack, app, or data. This single call returns the complete stack state: details, storage files, tables, agents, settings, app status, and recent logs. It avoids making multiple separate calls.

Key MCP tools:
- **GetStackOverview** — Complete stack state in one call (use this first!)
- **ListStacks** / **CreateStack** / **DeleteStack** — Manage stacks
- **ListStorage** / **UploadFile** / **DownloadFile** / **DeleteFile** — Blob storage
- **ListTables** / **QueryTable** / **GetEntity** / **UpsertEntity** — Table data
- **ListAgents** / **GetAgent** / **DeployAgent** / **DeleteAgent** — AI agents
- **ListSettings** / **GetSetting** / **SetSetting** — Key-value config
- **GetAppStatus** / **StartApp** / **StopApp** — Container app lifecycle
- **GetStackLogs** — Server and app logs
- **GetSample** / **ListSamples** — Code samples and documentation
- **GetPackageFeed** / **ListPackages** — NuGet package info
- **GetServerVersion** — Server version and build info

## Foundry CLI Commands

The `fas` CLI is the primary tool for managing Foundry apps. Prefer `fas run` for Foundry-managed local setup, but standard projects using the new stack pattern can also run with their normal host commands.

### Project Management

```bash
fas new list                      # List available templates
fas new <template> [<template>...]  # Create from composable templates
fas new <template> -a             # Create with agentic setup (MCP + skill configured)
fas init                          # Initialize an existing project (auto-detect)
fas init [aspnet|express|fastapi] # Initialize an existing project explicitly
fas run                           # Run locally with Foundry-managed setup
fas deploy                        # Deploy to Azure Container Apps
```

### Stack Management

```bash
fas stacks list           # List all stacks
fas stacks create <name>  # Create a new stack
fas stacks delete <id>    # Delete a stack and all its data
```

### Storage

```bash
fas storage list [path]       # List files/folders
fas storage print <path>      # Print file contents
fas storage upload <file>     # Upload a file
fas storage delete <path>     # Delete file or folder
```

### Tables

```bash
fas tables list               # List all tables
fas tables query <table>      # Query entities in a table
```

### Agents

```bash
fas agents list               # List deployed agents
fas agents deploy <yaml>      # Deploy agent from YAML file
fas agents delete <name>      # Delete an agent
```

### Settings

```bash
fas settings list             # List all settings
fas settings get <key>        # Get a setting value
fas settings set <key> <val>  # Set a setting
fas settings delete <key>     # Delete a setting
```

### Documentation (Admin)

```bash
fas docs publish              # Publish docs from repo to server
fas docs help                 # Show docs help
```

### Server & Updates

```bash
fas version               # Show CLI version
fas update                # Update CLI and MCP server
fas server list           # List configured servers
fas server add <n> <url>  # Add a server
```

### MCP Server (for AI assistants)

```bash
fas mcp install           # Download MCP server to ~/.fas/
fas mcp configure         # Configure VS Code, Copilot CLI, and Claude Desktop
fas mcp update            # Update MCP server only
```

## Creating Foundry Apps

To create a new Foundry app, use the `fas new` CLI command. To initialize an existing project, use `fas init`. These commands generate or wire up the correct project structure, SDK references, package versions, and configuration files:

```bash
fas new aspnet          # New C# ASP.NET app
fas new express         # New Node.js Express app
fas new fastapi         # New Python FastAPI app
fas new aspnet agent    # New C# app with agent template
fas init                # Initialize an existing project (auto-detect)
fas init aspnet         # Initialize an existing C# ASP.NET app
fas init express        # Initialize an existing Node.js Express app
fas init fastapi        # Initialize an existing Python FastAPI app
fas new list            # List all available templates
```

Templates are composable — combine multiple templates in one command (for example, `fas new aspnet agent`). Use `-a` flag to include agentic setup with MCP and skill configured.

After creating, modify the generated files freely to build your app.

## IMPORTANT: Running Foundry Apps

Use `fas run` when you want Foundry-managed local setup. Standard projects using the new stack pattern can also run with their normal host commands when appropriate:

```bash
fas run                   # Run locally with Foundry-managed setup
fas run --explicit-host   # Run with hot reload and explicit Aspire host
dotnet run                    # Standard C# projects can also run directly
fas deploy                # Deploy to Azure Container Apps
```

## Overview

Foundry Applications Service is a platform for building and deploying AI-powered applications. It provides:

- **Stack-based isolation**: Each application runs in its own stack with isolated storage, tables, and agents
- **Foundry Applications Service**: `UseFas()` as the primary ASP.NET API, plus low-level `AddFasServices()` / `UseFoundryMiddleware()`, with DI-injected `FoundryApplicationClient`
- **Storage APIs**: Blob storage for files and documents
- **Tables APIs**: Structured data storage with partition/row key access
- **Agents**: Deploy AI agents with custom instructions and JSON schemas
- **Custom Tools**: Define tools that agents can invoke during reasoning
- **Secrets**: Secure secret storage and retrieval
- **Embeddings**: Vector embeddings for semantic search and similarity
- **Authentication**: Built-in user authentication with Entra ID and GitHub providers
- **Local & Cloud**: Run locally during development, deploy to Azure Container Apps for production

## fas stack Pattern (C#)

The service-based stack pattern is the foundation for Foundry ASP.NET apps. Call `builder.UseFas()` to register Foundry services, build the app, and configure Foundry middleware, then inject `FoundryApplicationClient` where you need Foundry services. Use `AddFasServices()` and `UseFoundryMiddleware()` only for advanced scenarios.

### Creating an Application

```csharp
using Azure.FoundryApplications;
using OpenAI.Responses;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.UseFas();

app.MapPost("/chat", async (FoundryApplicationClient client, HttpContext context) =>
{
    using StreamReader reader = new(context.Request.Body);
    string userMessage = await reader.ReadToEndAsync();
    ResponsesClient agent = client.GetAgentClient("limerickwriter");
    ResponseResult response = await agent.CreateResponseAsync(userMessage);
    return response.GetOutputText();
});

app.Run();
```

### Key Services

| Service | Purpose |
|---------|---------|
| `client.Storage` | Upload, download, list, delete files |
| `client.Tables` | Create tables, CRUD entities |
| `client.Settings` | Key-value configuration storage |
| `client.Secrets` | Retrieve secrets by name |
| `client.Administration` | Deploy and manage agents |
| `client.Responses` | Base ResponsesClient for LLM calls |
| `client.Tools` | Registry for custom agent tools |
| `ILogger<T>` | Standard ASP.NET logging via DI |
| `builder.Configuration` | Access app configuration |
| `app` | Underlying WebApplication |

## Storage APIs

Store and retrieve files in your stack's blob storage.

### Upload Files

```csharp
// Upload text content
client.Storage.Upload("Hello World", "documents", "greeting.txt");

// Upload binary data
byte[] imageBytes = File.ReadAllBytes("photo.jpg");
client.Storage.Upload(BinaryData.FromBytes(imageBytes), "images", "photo.jpg");

// Upload with auto-generated filename
string fileName = client.Storage.Upload("Log entry", "logs");
```

### Download Files

```csharp
// Download as BinaryData
BinaryData data = client.Storage.Download("documents/greeting.txt");
string text = data.ToString();

// Download binary file
BinaryData imageData = client.Storage.Download("images/photo.jpg");
byte[] bytes = imageData.ToArray();
```

### List and Delete

```csharp
// List items in a folder
IEnumerable<StorageItem> items = client.Storage.List("documents");
foreach (StorageItem item in items)
{
    Console.WriteLine($"{item.Name} (IsFolder: {item.IsFolder})");
}

// List root
IEnumerable<StorageItem> rootItems = client.Storage.List();

// Delete file
client.Storage.Delete("documents/greeting.txt");

// Get all files from folder
IEnumerable<BinaryData> allDocs = client.Storage.GetAll("documents");
```

## Tables APIs

Store structured data with partition and row key access patterns.

### Create Tables

```csharp
// Create table if it doesn't exist
client.Tables.CreateTableIfNotExists("users");

// List all tables
IEnumerable<string> tables = client.Tables.ListTables();

// Delete table
client.Tables.DeleteTableIfExists("users");
```

### Define Custom Entities

```csharp
using Azure.FoundryApplications;
using System.Text.Json.Serialization;

public class UserEntity : TableEntity
{
    public UserEntity() : base() { }
    
    public UserEntity(string partition, string id) : base(partition, id) { }
    
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;
    
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;
    
    [JsonPropertyName("age")]
    public int Age { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}
```

### CRUD Operations

```csharp
// Create/Update entity
UserEntity user = new UserEntity("customers", "user123")
{
    Email = "john@example.com",
    DisplayName = "John Doe",
    Age = 30
};
string rowKey = client.Tables.SetEntity("users", user);

// Read single entity
UserEntity? retrieved = client.Tables.GetEntity<UserEntity>("users", "customers", "user123");

// Read all entities in partition
IEnumerable<UserEntity> allCustomers = client.Tables.GetEntities<UserEntity>("users", "customers");

// Read specific entities
IEnumerable<UserEntity> specific = client.Tables.GetEntities<UserEntity>("users", "customers", "user123", "user456");

// Delete entity
client.Tables.DeleteEntity("users", "customers", "user123");
```

### Using Dynamic Properties

```csharp
// TableEntity supports dynamic properties via indexer
TableEntity entity = new TableEntity("logs", "entry1");
entity["message"] = "Something happened";
entity["level"] = "info";
entity["timestamp"] = DateTime.UtcNow;

client.Tables.SetEntity("logs", entity);
```

## Agents

Deploy AI agents with custom instructions and optional JSON schemas for structured output.

### YAML Agent Definition

Create a YAML file (e.g., `myagent.yaml`):

```yaml
name: limerick-writer
model: gpt-4
instructions: |
  You are a creative limerick writer.
  When given a topic, write a funny limerick about it.
  Keep it family-friendly and clever.
description: Writes creative limericks on any topic
schema:
  type: object
  properties:
    limerick:
      type: string
      description: The limerick text
    topic:
      type: string
      description: The topic of the limerick
  required:
    - limerick
    - topic
```

### Deploy Agent from YAML

```csharp
// Deploy from file path
client.Administration.DeployAgent("myagent.yaml");

// Deploy from BinaryData
string yamlContent = File.ReadAllText("myagent.yaml");
client.Administration.DeployAgent(BinaryData.FromString(yamlContent), "my-agent");
```

### Deploy Agent Programmatically

```csharp
client.Administration.DeployAgent(
    name: "summarizer",
    model: "gpt-4",
    instructions: "You summarize text concisely. Return a JSON object with 'summary' and 'keyPoints' fields.",
    description: "Summarizes long text into key points",
    schema: BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "summary": { "type": "string" },
            "keyPoints": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["summary", "keyPoints"]
    }
    """)
);
```

### Use Agent for Responses

```csharp
// Get client configured for specific agent
ResponsesClient agentClient = client.GetAgentClient("limerick-writer");

// Create response
CreateResponseOptions options = new CreateResponseOptions();
options.Messages.Add(new UserChatMessage("Write a limerick about programming"));

ClientResult<CreateResponseResult> result = agentClient.CreateResponse(options);

// Get text output
string text = result.Value.GetOutputText();

// Get structured output (if agent has schema)
LimerickOutput output = result.Value.GetOutputAs<LimerickOutput>();
Console.WriteLine(output.Limerick);
```

### List and Manage Agents

```csharp
// List all deployed agents
IEnumerable<string> agents = client.Administration.ListAgents();

// Get agent details
AgentDetails? details = client.Administration.GetAgentDetails("limerick-writer");
Console.WriteLine($"Model: {details.Model}");
Console.WriteLine($"Instructions: {details.Instructions}");

// Delete agent
client.Administration.DeleteAgent("limerick-writer");
```

## Custom Tools

Define tools that agents can invoke during reasoning.

### Define Tools with [Tool] Attribute

```csharp
using Azure.FoundryApplications;

public class WeatherTools(FoundryApplicationClient client)
{
    [Tool("Gets the current weather for a city")]
    public string GetWeather(string city)
    {
        client.Storage.Upload(city, "weather-lookups");
        return $"Weather in {city}: Sunny, 72°F";
    }
    
    [Tool("Converts temperature between Celsius and Fahrenheit")]
    public double ConvertTemperature(double value, string fromUnit, string toUnit)
    {
        if (fromUnit == "C" && toUnit == "F")
            return value * 9 / 5 + 32;
        if (fromUnit == "F" && toUnit == "C")
            return (value - 32) * 5 / 9;
        return value;
    }
}
```

### Register Tools

```csharp
app.MapFasTools<WeatherTools>();

// Tools are now available to agents through client.Tools
```

### Tool Parameter Types

Supported parameter types:
- `string` - Text values
- `int`, `long` - Integer numbers
- `float`, `double` - Decimal numbers
- `bool` - True/false values
- `DateTime` - Date and time values

### Execute Tools Manually

```csharp
Dictionary<string, object?> arguments = new Dictionary<string, object?>
{
    ["city"] = "Seattle"
};
string result = (string)client.Tools.ExecuteTool("GetWeather", arguments)!;
```

## Settings Client

Store and retrieve key-value configuration.

```csharp
// Set a value
client.Settings.Set("api_key", "sk-abc123");

// Get a value
string? apiKey = client.Settings.Get("api_key");

// Delete a value
client.Settings.Delete("api_key");

// List all keys
IEnumerable<string> keys = client.Settings.ListKeys();

// List keys with prefix
IEnumerable<string> configKeys = client.Settings.ListKeys("config_");

// Get all settings as a dictionary
Dictionary<string, string> allSettings = client.Settings.GetAll();

// Check for changes using etag (useful for polling/caching)
SettingsVersionResult version = client.Settings.GetVersion();
// Later, check if anything changed:
SettingsVersionResult updated = client.Settings.GetVersion(version.ETag);
if (updated.Changed)
{
    // Settings were modified, reload
}
```

## Secrets Client

Retrieve secrets stored securely in your stack. Secrets are managed via the fas server UI or MCP tools.

```csharp
// Get a secret by name
string connectionString = client.Secrets.Get("database-connection");

// List all secret names
string[] secretNames = client.Secrets.ListNames();
```

## Embeddings

Generate vector embeddings for semantic search, clustering, and similarity comparisons.

```csharp
// Get embedding client (uses default model)
EmbeddingClient embeddingClient = client.GetEmbeddingClient();

// Generate embedding for text
OpenAIEmbedding embedding = await embeddingClient.GenerateEmbeddingAsync("Hello world");
ReadOnlyMemory<float> vector = embedding.Value.ToFloats();

// Use a specific model
EmbeddingClient customClient = client.GetEmbeddingClient("text-embedding-3-small");
```

## Authentication

Foundry apps support built-in user authentication with Entra ID and GitHub providers.

### Enabling Authentication (C#)

```csharp
using Azure.FoundryApplications;
using OpenAI.Responses;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.UseFas(AuthProvider.GitHub);

app.MapPost("/chat", async (FoundryApplicationClient client, HttpContext context) =>
{
    FoundryUser user = context.GetFoundryUser();
    string userName = user.DisplayName;
    string userEmail = user.Email;
    bool isAuth = user.IsAuthenticated;

    ResponsesClient agent = client.GetAgentClient("assistant");
    ResponseResult response = await agent.CreateResponseAsync($"User {userName}: hello");
    return response.GetOutputText();
});

app.Run();
```

### FoundryUser Properties

| Property | Type | Description |
|----------|------|-------------|
| `Id` | string | Unique user identifier |
| `DisplayName` | string | User's display name |
| `Email` | string | User's email address |
| `Provider` | string | Auth provider (e.g., "entra-id", "github") |
| `ProviderId` | string | Provider-specific user ID |
| `IsAuthenticated` | bool | Whether the user is authenticated |
| `AvatarUrl` | string? | User's avatar URL (if available) |

## fas-client (Standalone)

For scripts, tests, and console apps that don't need a web server, use `fas-client` directly:

```csharp
using Azure.FoundryApplications;

fas-client client = new("https://myserver.azurewebsites.net", "my-stack");

// Access all data services
IEnumerable<StorageItem> storageItems = client.Storage.List();
List<string> fileNames = new List<string>();
foreach (StorageItem item in storageItems)
{
    fileNames.Add(item.Name);
}

IEnumerable<string> tables = client.Tables.ListTables();
string secret = client.Secrets.Get("api-key");
```

## Testing & Running Apps

### Stack Management

Stacks provide isolated environments for your app's data.

```bash
fas stacks create my-test-stack   # Create a new stack
fas stacks list                   # List all stacks
fas stacks delete my-test-stack   # Delete (removes all data)
```

### Running Apps Locally

Prefer `fas run` for Foundry-managed local setup. Standard projects using the new stack pattern can also use their normal framework commands when needed.

```bash
# Run the app (from the app directory containing app.cs or .csproj)
fas run

# Run with hot reload and explicit Aspire host
fas run --explicit-host

# App connects to configured Foundry endpoint automatically
```

The `fas run` command:
- Resolves Foundry NuGet packages from the local server
- Sets up Aspire hosting automatically
- Configures the connection to Foundry Applications Service
- Handles stack registration

### Running in Containers

```bash
# Build and deploy to Azure Container Apps
fas deploy

# App gets URL like: https://stack-<id>.<env>.azurecontainerapps.io
```

### Testing Workflow

1. **Create test stack:**
   ```bash
   fas stacks create test-myfeature
   ```

2. **Configure app to use stack:**
   ```bash
   # Set environment variable
   set FOUNDRY_STACK=test-myfeature  # Windows
   export FOUNDRY_STACK=test-myfeature  # Linux/Mac
   ```

3. **Run app:**
   ```bash
   fas run
   ```

4. **Test your endpoints** (the app URL is shown in the console output)

5. **Clean up:**
   ```bash
   fas stacks delete test-myfeature
   ```

### Debugging & Inspecting Stack Data

**Via MCP tools** (if available — fastest approach):
- Call `GetStackOverview` to see everything at once
- Use `GetStackLogs` to check for errors
- Use `QueryTable` or `ListStorage` for specific data

**Via CLI:**
```bash
fas storage list documents/          # List files
fas storage print documents/report   # Print file contents
fas tables list                      # List tables
fas agents list                      # List agents
fas settings list                    # List settings
```

**Via Foundry Applications Service Web UI:**
Navigate to `<foundry-endpoint>/stacks-ui` → click your stack → use the blades for Storage, Tables, Agents, Settings, and App status.

## Complete Example

Here's a complete Foundry app with an agent, storage, and tables:

```csharp
using Azure.FoundryApplications;
using OpenAI.Responses;
using System.Text;
using System.Text.Json.Serialization;

// Define entity type
public class NoteEntity : TableEntity
{
    public NoteEntity() : base() { }
    public NoteEntity(string userId, string noteId) : base(userId, noteId) { }
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
    
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Define tools
public class NoteTools(FoundryApplicationClient client)
{
    [Tool("Saves a note for the user")]
    public string SaveNote(string userId, string title, string content)
    {
        NoteEntity note = new(userId, Guid.NewGuid().ToString())
        {
            Title = title,
            Content = content
        };
        client.Tables.SetEntity("notes", note);
        return $"Note '{title}' saved successfully";
    }
    
    [Tool("Lists all notes for a user")]
    public string ListNotes(string userId)
    {
        IEnumerable<NoteEntity> notes = client.Tables.GetEntities<NoteEntity>("notes", userId);
        StringBuilder text = new StringBuilder();
        foreach (NoteEntity note in notes)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append("- ");
            text.Append(note.Title);
        }

        return text.ToString();
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.UseFas();
app.MapFasTools<NoteTools>();

FoundryApplicationClient client = app.Services.GetRequiredService<FoundryApplicationClient>();
client.Tables.CreateTableIfNotExists("notes");

client.Administration.DeployAgent(
    name: "note-assistant",
    model: "gpt-4",
    instructions: "You help users manage their notes. Use the SaveNote and ListNotes tools."
);

app.MapPost("/chat", async (FoundryApplicationClient stack, string userId, string message) =>
{
    ResponsesClient client = stack.GetAgentClient("note-assistant");
    ResponseResult result = await client.CreateResponseAsync($"User {userId}: {message}");
    return result.GetOutputText();
});

app.Run();
```

## Best Practices

1. **Use meaningful partition keys** - Group related entities for efficient queries
2. **Deploy agents at startup** - Use `DeployAgent` in app initialization
3. **Define schemas for structured output** - Ensures consistent agent responses
4. **Use typed entities** - Create classes inheriting `TableEntity` for type safety
5. **Clean up test stacks** - Delete stacks after testing to avoid clutter
6. **Use YAML for agent definitions** - Easier to version control and modify
7. **Register tools before using agents** - Tools must be registered before agent calls

## Python Development

Foundry apps can also be built with Python using FastAPI. Use `fas new fastapi` to create a new Python project or `fas init fastapi` to initialize an existing one.

### Python Project Structure

```
app.py                 # FastAPI application
index.html             # Chat UI
limerickwriter.yaml    # Agent definition
pyproject.toml         # Python dependencies
host.cs                # Aspire orchestration host
foundry/               # Foundry Apps Python SDK
```

### Running and Deploying Python Apps

```bash
# Foundry CLI is the easiest way to run with Foundry-managed setup
fas run

# Standard FastAPI projects can also run directly
uvicorn app:app --reload

# Deploy to Azure Container Apps
fas deploy
```

### create_foundry_stack (Python)

```python
from typing import Annotated

from fastapi import Body, FastAPI
from fastapi.responses import PlainTextResponse
from foundry import create_foundry_stack

foundry = create_foundry_stack()
app = FastAPI(lifespan=foundry.lifespan)

@app.post("/chat", response_class=PlainTextResponse)
async def chat(message: Annotated[str, Body()]):
    agent = client.get_agent("myagent")
    response = await agent.responses.create(input=message)
    return response.output_text
```

The Python `create_foundry_stack()` pattern reads `FOUNDRY`, `FOUNDRY_STACK`, and `FOUNDRY_TOKEN` environment variables automatically. All API calls are scoped to the correct stack.

### Storage (Python)

```python
# Upload content
filename = await client.storage.upload("Hello World", folder="documents")

# Download content
text = await client.storage.download("documents/myfile")

# List files and folders
files = await client.storage.list_files("documents")
folders = await client.storage.list_folders()

# Delete
await client.storage.delete("documents/myfile")
```

### Tables (Python)

```python
# Create a table
await client.tables.create_table("users")

# Upsert entity (auto-creates table if needed, auto-generates rowKey if omitted)
row_key = await client.tables.set_entity("users", "customers", "user123",
    email="john@example.com", name="John Doe", age=30)

# Get single entity
user = await client.tables.get_entity("users", "customers", "user123")

# Query all entities in a partition
customers = await client.tables.get_entities("users", "customers")

# List all entities in a table
all_users = await client.tables.list_entities("users")

# Delete entity
await client.tables.delete_entity("users", "customers", "user123")

# List and delete tables
tables = await client.tables.list_tables()
await client.tables.delete_table("users")
```

### Settings (Python)

```python
# Set a value
await client.settings.set("api_key", "sk-abc123")

# Get a value
value = await client.settings.get("api_key")

# List all settings
all_settings = await client.settings.list()

# Delete
await client.settings.delete("api_key")
```

### Agents (Python)

```python
# Deploy an agent
result = await client.agents.deploy("myagent", "You are a helpful assistant.",
    model="gpt-4o", description="General assistant")

# List agents
agents = await client.agents.list()

# Get agent details
details = await client.agents.get("myagent")

# Delete agent
await client.agents.delete("myagent")
```

### Embeddings (Python)

```python
# Single text
vectors = await client.embeddings.create("Hello world")

# Batch
vectors = await client.embeddings.create(["Hello", "World"])

# With specific model
vectors = await client.embeddings.create("Hello", model="text-embedding-3-small")
```

### Custom Tools (Python)

```python
from foundry import tool

class MyTools:
    @tool("gets the current weather for a city")
    def get_weather(self, city: str) -> str:
        return f"72°F and sunny in {city}"

# Register tools
client.tools.add_tools(MyTools())

# Get definitions for OpenAI API
response = await client.assistant.responses.create(
    input="What's the weather?",
    tools=client.Tools.get_tool_definitions()
)

# Execute tool calls
for item in response.output:
    if item.type == "function_call":
        result = client.tools.execute(item.name, item.arguments)
```

### fas-client - Standalone (Python)

For scripts, tests, and console apps without FastAPI:

```python
from foundry import fas-client

async with fas-client("https://myserver.azurewebsites.net", "my-stack") as client:
    await client.storage.upload("hello", folder="docs")
    agents = await client.agents.list()
    vectors = await client.embeddings.create("hello world")
```

### Secrets (Python)

```python
# Get a secret by name
db_connection = await client.secrets.get("database-connection")

# List all secret names
names = await client.secrets.list_names()
```

### Authentication (Python)

```python
from fastapi import Depends, FastAPI
from foundry import FoundryUser, create_foundry_stack, require_auth

foundry = create_foundry_stack(auth=["github"])
app = FastAPI(lifespan=foundry.lifespan)

@app.post("/chat")
async def chat(user: FoundryUser = Depends(require_auth)):
    # user.display_name, user.email, user.is_authenticated, etc.
    return {"message": f"Hello {user.display_name}"}
```

### Telemetry (Python)

Enable OpenTelemetry tracing, metrics, and logging:

```python
from foundry import configure_opentelemetry

# Call at startup to enable telemetry with OTLP exporters
configure_opentelemetry(fastapi=app)
```

## Node.js Development

Foundry apps can also be built with Node.js using Express.js. Use `fas new express` for a new project or `fas init express` to initialize an existing one.

### Node.js Project Structure

```
app.js                 # Express.js application
package.json           # Node.js dependencies
foundry/               # Foundry Apps Node.js SDK
  index.js             # SDK entry point (createFoundryStack, fas-client, etc.)
```

### Running and Deploying Node.js Apps

```bash
# Install dependencies first
npm install

# Foundry CLI is the easiest way to run with Foundry-managed setup
fas run

# Standard Express projects can also run directly
node app.js

# Deploy to Azure Container Apps
fas deploy
```

### Environment Variables

The Node.js SDK reads these environment variables automatically:

| Variable | Purpose |
|----------|---------|
| `FOUNDRY` | Foundry Applications Service endpoint URL |
| `FOUNDRY_STACK` | Stack name for data isolation |
| `FOUNDRY_TOKEN` | Authentication token |
| `PORT` | HTTP port for the Express.js server |

### createFoundryStack (Node.js)

```javascript
import express from "express";
import { createFoundryStack } from "./foundry/index.js";

const foundry = await createFoundryStack();
const app = express();

// Access built-in services
client.storage    // StorageClient for blob storage
client.tables     // TablesClient for structured data
client.agents     // Agent management
client.settings   // Key-value configuration
client.secrets    // Secure secret retrieval
client.embeddings // Vector embeddings
client.tools      // Custom tool registry

app.get("/", (req, res) => {
    res.send("Hello Foundry!");
});

app.listen(process.env.PORT || 3000);
```

### Storage (Node.js)

```javascript
// Upload content
const filename = await client.storage.upload("Hello World", { folder: "documents" });

// Download content
const data = await client.storage.download("documents/greeting.txt");

// List files in a folder
const files = await client.storage.listFiles("documents");

// List folders
const folders = await client.storage.listFolders();

// Delete
await client.storage.delete("documents/greeting.txt");
```

### Tables (Node.js)

```javascript
// Create a table
await client.tables.createTable("users");

// Upsert entity
const rowKey = await client.tables.setEntity("users", "customers", "user123", {
    email: "john@example.com",
    name: "John Doe",
    age: 30
});

// Get single entity
const user = await client.tables.getEntity("users", "customers", "user123");

// Get all entities in a partition
const customers = await client.tables.getEntities("users", "customers");

// List all entities in a table (across all partitions)
const allUsers = await client.tables.listEntities("users");

// Delete entity
await client.tables.deleteEntity("users", "customers", "user123");

// List and delete tables
const tables = await client.tables.listTables();
await client.tables.deleteTable("users");
```

### Agents (Node.js)

```javascript
// Deploy an agent
await client.agents.deploy("myagent", "You are a helpful assistant.", {
    model: "gpt-4o",
    description: "General assistant"
});

// List agents
const agents = await client.agents.list();

// Get agent details
const details = await client.agents.get("myagent");

// Delete agent
await client.agents.delete("myagent");
```

### API Pattern (Node.js)

Node.js Foundry apps use ESM modules, async/await, and native `fetch()` internally:

```javascript
import express from "express";
import { createFoundryStack } from "./foundry/index.js";

const foundry = await createFoundryStack();
const app = express();
app.use(express.json());

// Deploy agent at startup
await client.agents.deploy("chat-assistant", "You are a helpful assistant.", {
    model: "gpt-4o"
});

// Chat endpoint using async/await
app.post("/chat", async (req, res) => {
    const { message } = req.body;
    const client = client.getAgent("chat-assistant");
    const response = await client.responses.create({ input: message });
    res.json({ reply: response.output_text });
});

app.listen(process.env.PORT || 3000, () => {
    console.log("Foundry app running");
});
```

### fas-client - Standalone (Node.js)

For scripts and tests without Express.js:

```javascript
import { fas-client } from "./foundry/index.js";

const client = new fas-client("https://myserver.azurewebsites.net", "my-stack");

await client.storage.upload("hello", { folder: "docs" });
const agents = await client.agents.list();
const vectors = await client.embeddings.create("hello world");
```

### Secrets (Node.js)

```javascript
// Get a secret by name
const apiKey = await client.secrets.get("external-api-key");

// List all secret names
const names = await client.secrets.listNames();
```

### Embeddings (Node.js)

```javascript
// Generate embeddings for a single text
const vectors = await client.embeddings.create("Hello world");

// Batch embeddings
const batchVectors = await client.embeddings.create(["Hello", "World"]);

// Use a specific model
const customVectors = await client.embeddings.create("Hello", { model: "text-embedding-3-small" });
```

### Settings - Advanced (Node.js)

```javascript
// Get all settings as key-value pairs
const allSettings = await client.settings.getAll();

// Check for changes using etag (useful for caching)
const version = await client.settings.getVersion();
// Later, check if anything changed:
const updated = await client.settings.getVersion(version.etag);
if (updated.changed) {
    // Settings were modified, reload
}
```

### Authentication (Node.js)

```javascript
import express from "express";
import { createFoundryStack, requireAuth } from "./foundry/index.js";

const foundry = await createFoundryStack({ auth: ["github"] });
const app = express();
app.use(client.middleware());

// Access authenticated user in routes
app.post("/chat", requireAuth(), async (req, res) => {
    const user = req.foundryUser;
    // user.displayName, user.email, user.isAuthenticated, etc.
    res.json({ message: `Hello ${user.displayName}` });
});
```

### Custom Tools - Advanced (Node.js)

```javascript
import { tool, Tools } from "./foundry/index.js";

// Register tools via decorator
class MyTools {
    @tool("gets the weather for a city")
    getWeather(city) {
        return `72°F in ${city}`;
    }
}

const tools = new Tools();
tools.addTools(new MyTools());

// Or register tools manually (without decorators)
tools.addTool("getTime", "gets the current time", (args) => {
    return new Date().toISOString();
});

// Async tool execution
const result = await tools.executeAsync("getWeather", { city: "Seattle" });
```

## Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| `fas run` fails with NuGet error | Run `foundry update` to get latest packages |
| Agent not responding | Check if agent is deployed: `foundry agents list` or MCP `ListAgents` |
| Storage returns 404 | The file path may be wrong. Use `foundry storage list` or MCP `ListStorage` to check |
| App not starting after deploy | Check logs: `fas deploy` shows URL; use MCP `GetStackLogs` for errors |
| "Stack not found" | Verify stack exists: `fas stacks list` or MCP `ListStacks` |
| `dotnet run` doesn't work | Use `fas run` for Foundry-managed setup, or use `dotnet run` in a standard ASP.NET project wired with the new stack pattern |
| Tables query returns empty | Verify table exists and has data. Check partition key matches. |
| MCP tools not showing | Run `foundry mcp install` then `foundry mcp configure` in your workspace |

### Deployment Workflow

1. Develop locally with `fas run`
2. Test your endpoints in the browser or with curl
3. When ready, deploy with `fas deploy`
4. Check app status with `foundry` CLI or MCP `GetAppStatus`
5. View logs with MCP `GetStackLogs` if anything goes wrong
6. To stop: MCP `StopApp`. To remove: MCP `UndeployApp`
