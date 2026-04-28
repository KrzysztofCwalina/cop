using System;

namespace Samples;

#region Snippet:CreateBlobClient
var client = new BlobClient("connection", "container", "blob");
Console.WriteLine(client.Name);
#endregion

public class SnippetSample
{
    #region Snippet:UploadBlob
    public void Upload()
    {
        var data = new byte[] { 1, 2, 3 };
        // Upload the data
    }
    #endregion

    #region Snippet:DownloadBlob
    public void Download()
    {
        // Download logic
    }
    #endregion

    #region NonSnippetRegion
    public void Other() { }
    #endregion
}
