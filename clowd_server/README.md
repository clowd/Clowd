# Clowd.Server — streaming upload proxy

A mostly-stateless ASP.NET Core service that sits between the Clowd client and an upload
destination (Azure blob storage, etc.) so a download link can be shared *before* the upload
finishes. Destinations like Azure block blobs only become downloadable once every block is
staged and the block list is committed; this proxy bridges that gap.

## How it works

```
client                      clowd_server                        azure
  |-- POST /api/v1/uploads --->|                                  |
  |   (creds + metadata)       |-- open streaming blob write ---->|
  |<-- uploadUrl, downloadUrl -|                                  |
  |                            |                                  |
  |   (share downloadUrl now!) |                                  |
  |                            |                                  |
  |-- PUT /api/v1/uploads/id ->|-- stage blocks as bytes arrive ->|
  |   (raw file body)          |-> tee to local cache file        |
  |                            |     ^                            |
  |              recipient --->|-- GET /d/id tails the cache      |
  |                            |   (bytes flow immediately)       |
  |                            |                                  |
  |   (upload ends)            |-- commit block list ------------>|
  |<-- finalUrl, delete info --|   persist id -> finalUrl         |
  |                            |   forget everything else         |
  |                            |                                  |
  |              recipient --->|-- GET /d/id => 301 to azure      |
```

- **In-progress uploads** live only in memory plus a cache file under the cache mount.
  Credentials sent in StartUpload are held in memory for the duration of the upload and
  never persisted.
- **Completed uploads** leave behind exactly one thing: a tiny json file mapping the id to
  the final destination url, served as a `301 Moved Permanently`.
- A failed or abandoned upload kills any in-flight downloads (connection abort, so
  recipients don't end up with a silently-truncated file) and is swept from disk.

## API

### `POST /api/v1/uploads`

```json
{
  "provider": "azure",
  "fileName": "screenshot.png",
  "contentType": "image/png",
  "contentLength": 1048576,
  "credentials": {
    "containerSasUrl": "https://account.blob.core.windows.net/uploads?sv=...&sp=cw...&sig=...",
    "customDomain": "files.example.com"
  }
}
```

`contentLength` is optional but recommended. When provided:

- proxied downloads carry a `Content-Length` header,
- the upload is rejected if the byte count doesn't match, and
- the **onward** request to the destination carries `Content-Length` instead of
  `Transfer-Encoding: chunked` (the body still streams). Some hosts reject chunked request
  bodies, so declaring the length here makes the proxy work against more destinations.

`backblaze` *requires* it — B2 won't accept chunked uploads at all.

### Providers and their `credentials` keys

| Provider | Required | Optional | Notes |
|----------|----------|----------|-------|
| `azure` | `containerSasUrl` | `customDomain` | SAS needs create+write only — the server never sees account keys. Final url known up front. |
| `backblaze` | `keyId`, `applicationKey`, `bucketName` | | requires `contentLength`; streams with a sha1 trailer (`hex_digits_at_end`) |
| `imgur` | `clientId` | | anonymous; deletehash returned after commit |
| `catbox` | | `userHash`, `expiry` (`never`/`1h`/`12h`/`24h`/`72h`) | expiry ≠ never goes to litterbox |
| `picsur` | `baseUrl` | `apiKey`, `directLink` (`true`/`false`) | self-hosted |
| `vgyme` | `userKey` | | |
| `hastebin` | | `url` (default `https://pastie.io`) | raw text post, not multipart |

Credentials are held in memory only while the upload is in flight, and prefer scoped
secrets where the service supports them (azure SAS instead of a connection string).

Response:

```json
{
  "id": "8fz-K2v1Qx0pLmNa",
  "uploadUrl": "https://share.example.com/api/v1/uploads/8fz-K2v1Qx0pLmNa?token=...",
  "downloadUrl": "https://share.example.com/d/8fz-K2v1Qx0pLmNa",
  "finalUrl": "https://account.blob.core.windows.net/uploads/3a1b...",
  "delete": { "provider": "azure", "uploadKey": "3a1b..." }
}
```

`downloadUrl` is shareable immediately. `finalUrl`/`delete` are included when the provider
knows them up front (Azure does); for anonymous hosts that only hand back a delete url
after the fact, the definitive values come in the upload response below.

### `PUT /api/v1/uploads/{id}?token=...`

Raw file body (the token can also be sent as `Authorization: Bearer ...`). Returns the
definitive `finalUrl` and `delete` info once the destination commits. One body per id.

### `GET /d/{id}`

While the upload is in flight: streams from the local cache, bytes flowing as they arrive.
After completion: `301` to the destination, so the proxy never serves the bytes again.

## Configuration

Bound from the `Clowd` section (env vars use `Clowd__` prefix):

| Key | Default | |
|-----|---------|-|
| `CachePath` | `data/cache` | in-progress upload buffer (mount 1) |
| `RedirectsPath` | `data/redirects` | persisted 301s (mount 2) |
| `PublicBaseUrl` | *(request host)* | origin used in generated urls, set when behind a proxy |
| `MaxUploadBytes` | 10 GiB | hard cap per upload |
| `UploadIdleTimeout` | 10 min | abandon uploads with no incoming bytes |
| `FinishedLinger` | 1 min | how long finished sessions stay around for draining downloads |

## Docker

```sh
docker build -t clowd-server clowd_server
docker run -p 8080:8080 \
  -v clowd-cache:/data/cache \
  -v clowd-redirects:/data/redirects \
  -e Clowd__PublicBaseUrl=https://share.example.com \
  clowd-server
```

## Development

```sh
dotnet test clowd_server/Clowd.Server.Tests   # unit + api tests (no azure account needed)
dotnet run --project clowd_server/Clowd.Server
```

Adding a destination: implement `IDestinationProvider`/`IDestinationUpload`
(`Destinations/IDestinationProvider.cs`) and register it in `Program.cs`. The contract that
matters: `WriteStream` receives bytes as they arrive, `CommitAsync` makes the object
public, and `AbortAsync` must never publish partial data.
