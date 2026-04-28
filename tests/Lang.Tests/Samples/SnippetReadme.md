# Sample README

This is a sample README for testing snippet validation.

## Create a Client

```C# Snippet:CreateBlobClient
var client = new BlobClient("connection", "container", "blob");
Console.WriteLine(client.Name);
```

## Upload Data

```csharp Snippet:UploadBlob
public void Upload()
{
    var data = new byte[] { 1, 2, 3 };
    // Upload the data
}
```

## Stale Example

```csharp Snippet:DownloadBlob
public void Download()
{
    // OLD download logic that is out of date
}
```

## Orphaned Reference

```csharp Snippet:DeleteBlob
client.Delete();
```

## Non-snippet fence

```python
print("hello")
```
