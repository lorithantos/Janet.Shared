# Janet.Azure.Storage

Azure Storage service clients built from a credential **the caller chooses**, with retry
budgets tighter than the SDK defaults, and an address check that refuses anything but a
plain https endpoint.

```csharp
var clients = new StorageClients(
    new StorageEndpoints(Blob: blobUri, Table: tableUri),   // or StorageEndpoints.ForAccount("name")
    credential,                                              // your app's choice
    retryOptions);                                           // optional; bindable from configuration

BlobContainerClient container = clients.Blobs.GetBlobContainerClient("campaigns");
TableClient table = clients.Tables.GetTableClient("clients");
```

- **It never picks a credential.** A managed identity in Azure, a developer's own sign-in
  locally, which credential types to allow and which tenant to pin are the app's decisions,
  so the app passes a `TokenCredential`.
- **No keys, no SAS.** There is no constructor for a connection string or account key, and
  `EnsureServiceAddress` refuses a non-https address or one carrying a query string, which is
  how a SAS token travels.
- **Tighter retries.** `StorageRetryOptions` gives blobs 4 exponential retries from 400 ms,
  capped at 8 s, with a 30 s per-attempt timeout; tables and queues 4 retries from 200 ms,
  capped at 4 s, with 15 s. The SDK's default per-attempt timeout is 100 s. Every value is
  settable, so it binds from configuration.
- A service left out of `StorageEndpoints` has no client; asking for it throws rather than
  guessing an address. Each client is built once, on first use.

## Binary reads and writes

```csharp
var container = clients.Blobs.GetBlobContainerClient("neelam-aesthetics");

bool created = await container.TryCreateAsync("images/Principals%20seated", bytes, "image/png", metadata);
BlobContent photo = await container.ReadContentAsync("images/Principals%20seated");   // bytes + type
```

- **`TryCreateAsync` creates, never overwrites.** The upload carries `If-None-Match: *`, so the
  service refuses it when the name is taken, even against a writer racing for the same name; it
  returns false rather than throwing.
- **`ReadContentAsync` returns the bytes with the type they were stored with,** so an image comes
  back as an image and JSON as JSON.
