
## Client secrets

`appsettings.json` ships `Clients[].ClientSecret` **empty on purpose**.

A literal secret in this file is baked into the published demo image and runs on the public demo
server, which makes it a working `client_credentials` credential held by anyone who pulls the image.
Supply it at run time instead:

```bash
docker run -e Clients__0__ClientSecret="$(openssl rand -base64 32)" …
```

The seeder skips a client whose secret is neither supplied nor pre-hashed, so the demo starts without
one and that client simply cannot authenticate until you provide it.
