namespace App;

public class BlobClient { }          // unsealed public client -> flagged

public sealed class QueueClient { }  // sealed -> ok

internal class SecretClient { }      // not public -> ok

public sealed class Helper { }       // not a client -> ok
